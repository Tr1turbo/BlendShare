using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Triturbo.BlendShare.Components;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Persistence;
using nadena.dev.ndmf.preview;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Triturbo.BlendShare.NDMF
{
    internal sealed class BlendShareNdmfPreview : IRenderFilter
    {
        public static readonly TogglablePreviewNode PreviewNode =
            TogglablePreviewNode.Create(() => "BlendShare", "com.triturbo.blendshare/BlendSharePreview", true);

        public bool IsEnabled(ComputeContext context)
        {
            return context.Observe(PreviewNode.IsEnabled);
        }

        public IEnumerable<TogglablePreviewNode> GetPreviewControlNodes()
        {
            yield return PreviewNode;
        }

        public ImmutableList<RenderGroup> GetTargetGroups(ComputeContext context)
        {
            return context.GetComponentsByType<BlendShareMesh>()
                .Where(applier => IsApplierEnabled(applier, context) &&
                                  context.Observe(applier, item => item.TargetRenderer) != null)
                .GroupBy(applier => context.Observe(applier, item => item.TargetRenderer))
                .Select(group => RenderGroup.For(group.Key))
                .ToImmutableList();
        }

        public Task<IRenderFilterNode> Instantiate(RenderGroup group, IEnumerable<(Renderer, Renderer)> pairs, ComputeContext context)
        {
            var node = new Node(pairs, context);
            return Task.FromResult<IRenderFilterNode>(node);
        }

        private static bool IsApplierEnabled(BlendShareMesh applier, ComputeContext context)
        {
            if (applier == null)
            {
                return false;
            }

            var targetRenderer = context.Observe(applier, item => item.TargetRenderer);
            if (!context.ActiveAndEnabled(applier) ||
                context.Observe(applier, item => item.Patch) == null ||
                targetRenderer == null)
            {
                return false;
            }

            var applierAvatar = context.GetAvatarRoot(applier.gameObject);
            return applierAvatar != null;
        }

        private sealed class Node : IRenderFilterNode
        {
            private Mesh mesh;
            private Transform[] bones = System.Array.Empty<Transform>();
            private Transform rootBone;
            private SkinnedMeshRenderer originalRenderer;
            private SkinnedMeshRenderer proxyRenderer;
            private PreviewOriginalBlendShapeWeight[] originalWeightBindings =
                System.Array.Empty<PreviewOriginalBlendShapeWeight>();
            private PreviewBlendShapeWeight[] weightBindings = System.Array.Empty<PreviewBlendShapeWeight>();
            private string generationSignature;
            private bool hasPreviewOutput;

            public RenderAspects WhatChanged { get; private set; } = RenderAspects.Mesh;

            public Node(IEnumerable<(Renderer, Renderer)> pairs, ComputeContext context)
            {
                Process(pairs, context);
            }

            public Task<IRenderFilterNode> Refresh(IEnumerable<(Renderer, Renderer)> pairs, ComputeContext context, RenderAspects aspects)
            {
                bool matchesPair = MatchesProxyPair(pairs);
                string currentSignature = matchesPair
                    ? BuildGenerationSignature(originalRenderer, context)
                    : null;
                if (aspects.HasFlag(RenderAspects.Mesh) ||
                    !matchesPair ||
                    currentSignature != generationSignature)
                {
                    Dispose();
                    var node = new Node(pairs, context);
                    return Task.FromResult<IRenderFilterNode>(node);
                }

                WhatChanged = 0;
                return Task.FromResult<IRenderFilterNode>(this);
            }

            private bool MatchesProxyPair(IEnumerable<(Renderer, Renderer)> pairs)
            {
                if (pairs == null)
                {
                    return false;
                }

                var pairArray = pairs.ToArray();
                return pairArray.Length == 1 &&
                       pairArray[0].Item1 == originalRenderer &&
                       pairArray[0].Item2 == proxyRenderer;
            }

            public void OnFrame(Renderer original, Renderer proxy)
            {
                if (!hasPreviewOutput)
                {
                    return;
                }

                if (proxy is SkinnedMeshRenderer skinnedMeshRenderer)
                {
                    skinnedMeshRenderer.sharedMesh = mesh;
                    skinnedMeshRenderer.rootBone = rootBone;
                    skinnedMeshRenderer.bones = bones ?? System.Array.Empty<Transform>();
                    foreach (var binding in weightBindings)
                    {
                        binding.Apply(skinnedMeshRenderer);
                    }

                    // Existing renderer controls have final authority over same-name patch controls.
                    foreach (var binding in originalWeightBindings)
                    {
                        binding.Apply(skinnedMeshRenderer);
                    }
                }
            }

            public void Dispose()
            {
                if (mesh != null && !UnityEditor.AssetDatabase.Contains(mesh))
                {
                    Object.DestroyImmediate(mesh);
                }

                mesh = null;
                bones = System.Array.Empty<Transform>();
                rootBone = null;
                originalWeightBindings = System.Array.Empty<PreviewOriginalBlendShapeWeight>();
                weightBindings = System.Array.Empty<PreviewBlendShapeWeight>();
                hasPreviewOutput = false;
            }

            private void Process(IEnumerable<(Renderer, Renderer)> pairs, ComputeContext context)
            {
                var (original, proxy) = pairs.Single();
                if (original is not SkinnedMeshRenderer originalRenderer || proxy is not SkinnedMeshRenderer proxyRenderer)
                {
                    return;
                }

                this.originalRenderer = originalRenderer;
                this.proxyRenderer = proxyRenderer;
                generationSignature = BuildGenerationSignature(originalRenderer, context);

                var enabledAppliers = new List<BlendShareMesh>();
                foreach (var applier in FindMeshAppliersForRenderer(originalRenderer, context))
                {
                    ObserveMeshApplier(applier, context);
                    if (!BlendShareComponentSetupService.ValidateMeshApplierMapping(applier, out _) &&
                        !BlendShareComponentSetupService.EnsureMeshApplierMappingCache(applier, out string mappingDiagnostic))
                    {
                        Debug.LogWarning($"[BlendShare Preview] {mappingDiagnostic}", originalRenderer);
                        continue;
                    }

                    enabledAppliers.Add(applier);
                }

                if (enabledAppliers.Count == 0)
                {
                    return;
                }

                var avatarRoot = context.GetAvatarRoot(enabledAppliers[0].gameObject);
                var sourceRoot = avatarRoot != null
                    ? avatarRoot.transform
                    : originalRenderer.transform.root;
                var sharedProxyAppliers = avatarRoot != null
                    ? FindMeshAppliersForAvatar(avatarRoot, context).ToArray()
                    : enabledAppliers.ToArray();
                var availableBoneProxies = avatarRoot != null
                    ? FindBoneProxiesForAvatar(avatarRoot, context).ToArray()
                    : context.GetComponentsByType<BlendShareBoneProxy>()
                        .Where(proxy => proxy != null &&
                                        context.ActiveAndEnabled(proxy) &&
                                        (proxy.transform == sourceRoot || proxy.transform.IsChildOf(sourceRoot)))
                        .ToArray();
                if (!BlendShareBoneMergePass.TryPreparePreview(
                        sharedProxyAppliers,
                        availableBoneProxies,
                        avatarRoot != null ? avatarRoot.transform : sourceRoot,
                        out var boneProxies,
                        out var proxyBoneOverrides,
                        out var temporaryProxyObjects,
                        out string proxyDiagnostic,
                        out var conflictingProxy))
                {
                    Debug.LogWarning($"[BlendShare Preview] {proxyDiagnostic}", conflictingProxy);
                    return;
                }

                BlendShareArtifact artifact;
                string generationDiagnostic;
                try
                {
                    var generationComponents = enabledAppliers.Cast<BlendShareComponent>()
                        .Concat(boneProxies)
                        .Distinct()
                        .ToArray();
                    ObserveProxyInputs(enabledAppliers, availableBoneProxies, context);
                    artifact = BlendShareArtifactService.CreateInMemoryArtifact(
                        sourceRoot != null ? sourceRoot.gameObject : originalRenderer.transform.root.gameObject,
                        generationComponents,
                        out generationDiagnostic);
                }
                finally
                {
                    BlendShareBoneMergePass.ReleaseTemporaryPreviewObjects(temporaryProxyObjects);
                }
                if (artifact == null)
                {
                    Debug.LogWarning(
                        string.IsNullOrWhiteSpace(generationDiagnostic)
                            ? "[BlendShare Preview] Failed to generate BlendShare artifact."
                            : $"[BlendShare Preview] {generationDiagnostic}",
                        original);
                    return;
                }

                var proxyRoot = proxyRenderer.transform.root;
                RetargetArtifactRendererPathForPreview(artifact, proxyRenderer, proxyRoot);

                var result = BlendShareArtifactService.ApplyArtifact(
                    artifact,
                    proxyRoot,
                    new BlendShareArtifactApplyOptions
                    {
                        UseUndo = false,
                        RecordDestructiveMarkers = false,
                        MarkObjectsDirty = false,
                        BonePathRoot = sourceRoot,
                        BoneTransformOverrides = proxyBoneOverrides
                    });

                if (!result.Success)
                {
                    foreach (string diagnostic in result.Diagnostics)
                    {
                        Debug.LogWarning($"[BlendShare Preview] {diagnostic}", original);
                    }
                    return;
                }

                mesh = proxyRenderer.sharedMesh;
                bones = proxyRenderer.bones;
                rootBone = proxyRenderer.rootBone;
                originalWeightBindings = BuildOriginalWeightBindings(
                    originalRenderer,
                    mesh,
                    enabledAppliers);
                weightBindings = BuildWeightBindings(enabledAppliers, mesh);
                hasPreviewOutput = mesh != null;

                context.Observe(originalRenderer, renderer => renderer.sharedMesh);
                context.Observe(originalRenderer, renderer => renderer.bones);
                context.Observe(originalRenderer, renderer => renderer.rootBone);
            }

            private static PreviewBlendShapeWeight[] BuildWeightBindings(
                IEnumerable<BlendShareMesh> appliers,
                Mesh generatedMesh)
            {
                if (generatedMesh == null)
                {
                    return System.Array.Empty<PreviewBlendShapeWeight>();
                }

                var bindings = new List<PreviewBlendShapeWeight>();
                foreach (var applier in appliers ?? System.Array.Empty<BlendShareMesh>())
                {
                    foreach (var weight in applier?.BlendShapeWeights ?? System.Array.Empty<BlendShareProxyBlendShapeWeight>())
                    {
                        if (weight == null || string.IsNullOrWhiteSpace(weight.ShapeName))
                        {
                            continue;
                        }

                        int index = generatedMesh.GetBlendShapeIndex(weight.ShapeName);
                        if (index >= 0)
                        {
                            bindings.Add(new PreviewBlendShapeWeight(index, weight));
                        }
                    }
                }

                return bindings.ToArray();
            }

            private readonly struct PreviewBlendShapeWeight
            {
                private readonly int index;
                private readonly BlendShareProxyBlendShapeWeight weight;

                public PreviewBlendShapeWeight(int index, BlendShareProxyBlendShapeWeight weight)
                {
                    this.index = index;
                    this.weight = weight;
                }

                public void Apply(SkinnedMeshRenderer renderer)
                {
                    if (renderer != null && weight != null)
                    {
                        renderer.SetBlendShapeWeight(index, weight.Weight);
                    }
                }
            }

            private static PreviewOriginalBlendShapeWeight[] BuildOriginalWeightBindings(
                SkinnedMeshRenderer originalRenderer,
                Mesh generatedMesh,
                IEnumerable<BlendShareMesh> appliers)
            {
                var originalMesh = originalRenderer != null ? originalRenderer.sharedMesh : null;
                if (originalMesh == null || generatedMesh == null)
                {
                    return System.Array.Empty<PreviewOriginalBlendShapeWeight>();
                }

                var bindings = new List<PreviewOriginalBlendShapeWeight>();
                var patchControlledNames = new HashSet<string>((appliers ?? System.Array.Empty<BlendShareMesh>())
                    .SelectMany(applier => applier?.BlendShapeWeights ?? System.Array.Empty<BlendShareProxyBlendShapeWeight>())
                    .Where(weight => weight != null && !string.IsNullOrWhiteSpace(weight.ShapeName))
                    .Select(weight => weight.ShapeName));
                for (int originalIndex = 0; originalIndex < originalMesh.blendShapeCount; originalIndex++)
                {
                    string shapeName = originalMesh.GetBlendShapeName(originalIndex);
                    int generatedIndex = generatedMesh.GetBlendShapeIndex(shapeName);
                    if (generatedIndex >= 0 &&
                        (generatedIndex != originalIndex || patchControlledNames.Contains(shapeName)))
                    {
                        bindings.Add(new PreviewOriginalBlendShapeWeight(
                            originalRenderer,
                            originalIndex,
                            generatedIndex));
                    }
                }

                return bindings.ToArray();
            }

            private readonly struct PreviewOriginalBlendShapeWeight
            {
                private readonly SkinnedMeshRenderer originalRenderer;
                private readonly int originalIndex;
                private readonly int generatedIndex;

                public PreviewOriginalBlendShapeWeight(
                    SkinnedMeshRenderer originalRenderer,
                    int originalIndex,
                    int generatedIndex)
                {
                    this.originalRenderer = originalRenderer;
                    this.originalIndex = originalIndex;
                    this.generatedIndex = generatedIndex;
                }

                public void Apply(SkinnedMeshRenderer renderer)
                {
                    if (renderer != null && originalRenderer != null)
                    {
                        renderer.SetBlendShapeWeight(
                            generatedIndex,
                            originalRenderer.GetBlendShapeWeight(originalIndex));
                    }
                }
            }

            private string BuildGenerationSignature(SkinnedMeshRenderer renderer, ComputeContext context)
            {
                if (renderer == null)
                {
                    return string.Empty;
                }

                var builder = new StringBuilder();
                builder.Append("renderer:").Append(renderer.GetInstanceID());
                AppendObject(builder, ":mesh:", context.Observe(renderer, item => item.sharedMesh));
                AppendObject(builder, ":rootBone:", context.Observe(renderer, item => item.rootBone));
                AppendTransformPath(builder, ":rendererPath:", renderer.transform, context);
                foreach (var bone in context.Observe(renderer, item => item.bones, Enumerable.SequenceEqual) ?? System.Array.Empty<Transform>())
                {
                    AppendObject(builder, ":bone:", bone);
                }

                var enabledAppliers = FindMeshAppliersForRenderer(renderer, context)
                    .Select(applier => new PreviewApplierState(applier, ObserveMeshApplier(applier, context)))
                    .ToArray();
                bool hasNameCollision = enabledAppliers
                    .SelectMany(state => state.BlendShapeNames.Distinct())
                    .GroupBy(shapeName => shapeName)
                    .Any(group => group.Count() > 1);
                // Hierarchy order changes mesh output only when one patch can overwrite another by name.
                var signatureAppliers = hasNameCollision
                    ? enabledAppliers
                    : enabledAppliers.OrderBy(state => state.Applier.GetInstanceID()).ToArray();
                foreach (var state in signatureAppliers)
                {
                    var applier = state.Applier;
                    builder.Append("|applier:").Append(applier != null ? applier.GetInstanceID() : 0);
                    AppendObject(builder, ":patch:", context.Observe(applier, item => item.Patch));
                    AppendObject(builder, ":meshData:", context.Observe(applier, item => item.MeshData));
                    foreach (string shapeName in state.BlendShapeNames)
                    {
                        builder.Append(":shape:").Append(shapeName);
                    }

                }

                foreach (var avatarRoot in enabledAppliers
                             .Select(state => context.GetAvatarRoot(state.Applier.gameObject))
                             .Where(root => root != null)
                             .Distinct()
                             .OrderBy(root => root.GetInstanceID()))
                {
                    builder.Append("|sharedProxyAvatar:").Append(avatarRoot.GetInstanceID());
                    foreach (var proxy in FindBoneProxiesForAvatar(avatarRoot, context))
                    {
                        AppendProxyState(builder, proxy, context);
                    }
                }

                return builder.ToString();
            }

            private readonly struct PreviewApplierState
            {
                public PreviewApplierState(BlendShareMesh applier, string[] blendShapeNames)
                {
                    Applier = applier;
                    BlendShapeNames = blendShapeNames ?? System.Array.Empty<string>();
                }

                public BlendShareMesh Applier { get; }
                public string[] BlendShapeNames { get; }
            }

            private static void AppendProxyState(StringBuilder builder, BlendShareBoneProxy proxy, ComputeContext context)
            {
                if (proxy == null)
                {
                    builder.Append(":proxyState:null");
                    return;
                }

                builder.Append(":proxyState:").Append(proxy.GetInstanceID());
                builder.Append(":name:").Append(context.Observe(proxy.gameObject, item => item.name));
                AppendObject(builder, ":sourceArmature:", context.Observe(proxy, item => item.SourceArmature));
                builder.Append(":sourceBonePath:").Append(context.Observe(proxy, item => item.SourceBonePath));
                builder.Append(":bindings:");
                foreach (var binding in context.Observe(
                             proxy,
                             item => item.Bindings.ToArray(),
                             Enumerable.SequenceEqual))
                {
                    if (binding == null)
                    {
                        builder.Append("<null>;");
                        continue;
                    }

                    builder.Append(binding.SourceBonePath).Append('=');
                    AppendObject(builder, string.Empty, binding.Transform);
                    AppendTransformPath(builder, "@", binding.Transform, context);
                    builder.Append(';');
                }
                var targetParent = context.Observe(proxy, item => item.TargetParent);
                builder.Append(":useHierarchyParent:")
                    .Append(context.Observe(proxy, item => item.UseHierarchyParent));
                AppendObject(builder, ":targetParent:", targetParent);
                AppendTransformPath(builder, ":targetParentPath:", targetParent, context);
                builder.Append(":recalc:").Append(context.Observe(proxy, item => item.RecalculateBindpose));
                AppendVector(builder, ":bindP:", context.Observe(proxy, item => item.LocalPosition));
                AppendVector(builder, ":bindR:", context.Observe(proxy, item => item.LocalEulerRotation));
                AppendVector(builder, ":bindS:", context.Observe(proxy, item => item.LocalScale));
            }

            private static void AppendObject(StringBuilder builder, string label, Object value)
            {
                builder.Append(label).Append(value != null ? value.GetInstanceID() : 0);
            }

            private static void AppendVector(StringBuilder builder, string label, Vector3 value)
            {
                builder.Append(label)
                    .Append(value.x.ToString("R"))
                    .Append(',')
                    .Append(value.y.ToString("R"))
                    .Append(',')
                    .Append(value.z.ToString("R"));
            }

            private static void AppendTransformPath(StringBuilder builder, string label, Transform transform, ComputeContext context)
            {
                builder.Append(label);
                if (transform == null)
                {
                    builder.Append("<null>");
                    return;
                }

                bool first = true;
                foreach (var node in context.ObservePath(transform))
                {
                    if (!first)
                    {
                        builder.Append('/');
                    }

                    builder.Append(context.Observe(node.gameObject, item => item.name));
                    first = false;
                }
            }

            private static IEnumerable<BlendShareMesh> FindMeshAppliersForRenderer(
                SkinnedMeshRenderer renderer,
                ComputeContext context)
            {
                if (renderer == null)
                {
                    return System.Array.Empty<BlendShareMesh>();
                }

                var candidates = context.GetComponentsByType<BlendShareMesh>()
                    .Where(applier => applier != null &&
                                      IsApplierEnabled(applier, context) &&
                                      context.Observe(applier, item => item.TargetRenderer) == renderer)
                    .OrderBy(applier => GetHierarchyOrder(applier.transform))
                    .ToArray();
                ObserveDeduplicationInputs(candidates, context);
                return BlendSharePatchIdUtility.DeduplicateMeshComponents(candidates);
            }

            private static IEnumerable<BlendShareMesh> FindMeshAppliersForAvatar(
                GameObject avatarRoot,
                ComputeContext context)
            {
                if (avatarRoot == null)
                {
                    return System.Array.Empty<BlendShareMesh>();
                }

                var candidates = context.GetComponentsByType<BlendShareMesh>()
                    .Where(applier => applier != null &&
                                      IsApplierEnabled(applier, context) &&
                                      context.GetAvatarRoot(applier.gameObject) == avatarRoot)
                    .OrderBy(applier => GetHierarchyOrder(applier.transform))
                    .ToArray();
                ObserveDeduplicationInputs(candidates, context);
                return BlendSharePatchIdUtility.DeduplicateMeshComponents(candidates);
            }

            private static void ObserveDeduplicationInputs(
                IEnumerable<BlendShareMesh> appliers,
                ComputeContext context)
            {
                foreach (var applier in appliers ?? System.Array.Empty<BlendShareMesh>())
                {
                    var patch = context.Observe(applier, item => item.Patch);
                    context.Observe(applier, item => item.MeshData);
                    if (patch != null)
                    {
                        context.Observe(patch, item => item.m_PatchId);
                    }
                }
            }

            private static IEnumerable<BlendShareBoneProxy> FindBoneProxiesForAvatar(
                GameObject avatarRoot,
                ComputeContext context)
            {
                if (avatarRoot == null)
                {
                    return System.Array.Empty<BlendShareBoneProxy>();
                }

                return context.GetComponentsByType<BlendShareBoneProxy>()
                    .Where(proxy => proxy != null &&
                                    context.ActiveAndEnabled(proxy) &&
                                    context.GetAvatarRoot(proxy.gameObject) == avatarRoot)
                    .OrderBy(proxy => GetHierarchyOrder(proxy.transform));
            }

            private static void RetargetArtifactRendererPathForPreview(
                BlendShareArtifact artifact,
                SkinnedMeshRenderer proxyRenderer,
                Transform proxyRoot)
            {
                if (artifact == null || proxyRenderer == null || proxyRoot == null)
                {
                    return;
                }

                string proxyRendererPath = MeshNodePath.Normalize(MeshNodePath.GetRelativePath(proxyRenderer.transform, proxyRoot));
                foreach (var descriptor in artifact.m_Meshes ?? System.Array.Empty<BlendShareMeshDescriptor>())
                {
                    if (descriptor != null)
                    {
                        descriptor.m_NodePath = proxyRendererPath;
                    }
                }
            }

            private static string[] ObserveMeshApplier(BlendShareMesh applier, ComputeContext context)
            {
                if (applier == null)
                {
                    return System.Array.Empty<string>();
                }

                context.Observe(applier, item => item.Patch);
                context.Observe(applier, item => item.TargetRenderer);
                context.Observe(applier, item => item.MeshData);
                context.Observe(applier, item => item.Mapping);
                var blendShapeNames = context.Observe(applier, item => item.BlendShapeWeights
                    .Select(weight => weight != null ? weight.ShapeName : string.Empty)
                    .ToArray(), Enumerable.SequenceEqual);
                return blendShapeNames ?? System.Array.Empty<string>();
            }

            private void ObserveProxyInputs(
                IEnumerable<BlendShareMesh> meshAppliers,
                IEnumerable<BlendShareBoneProxy> boneProxies,
                ComputeContext context)
            {
                foreach (var applier in meshAppliers ?? System.Array.Empty<BlendShareMesh>())
                {
                    if (applier?.TargetRenderer != null)
                    {
                        context.Observe(applier.TargetRenderer, renderer => renderer.bones, Enumerable.SequenceEqual);
                    }

                }

                foreach (var proxy in boneProxies ?? System.Array.Empty<BlendShareBoneProxy>())
                {
                    ObserveProxy(proxy, context);
                }
            }

            private static void ObserveProxy(BlendShareBoneProxy proxy, ComputeContext context)
            {
                if (proxy == null)
                {
                    return;
                }

                context.Observe(proxy, item => item.TargetParent);
                context.Observe(proxy, item => item.UseHierarchyParent);
                context.Observe(proxy, item => item.SourceArmature);
                context.Observe(proxy, item => item.SourceBonePath);
                var bindings = context.Observe(
                    proxy,
                    item => item.Bindings.ToArray(),
                    Enumerable.SequenceEqual);
                context.Observe(proxy, item => item.RecalculateBindpose);
                context.Observe(proxy, item => item.LocalPosition);
                context.Observe(proxy, item => item.LocalEulerRotation);
                context.Observe(proxy, item => item.LocalScale);
                context.Observe(proxy.gameObject, item => item.name);
                ObserveTransformPath(proxy.transform, context);
                foreach (var binding in bindings)
                {
                    if (binding?.Transform != null)
                    {
                        ObserveTransformPath(binding.Transform, context);
                    }
                }
                if (proxy.EffectiveParent != null)
                {
                    ObserveTransformPath(proxy.EffectiveParent, context);
                }
            }

            private static void ObserveTransformPath(Transform transform, ComputeContext context)
            {
                if (transform == null)
                {
                    return;
                }

                foreach (var node in context.ObservePath(transform))
                {
                    context.Observe(node.gameObject, item => item.name);
                }
            }

            private static string GetHierarchyOrder(Transform transform)
            {
                if (transform == null)
                {
                    return string.Empty;
                }

                var indices = new Stack<string>();
                var current = transform;
                while (current != null)
                {
                    indices.Push(current.GetSiblingIndex().ToString("D8"));
                    current = current.parent;
                }

                return string.Join("/", indices);
            }
        }

    }
}
