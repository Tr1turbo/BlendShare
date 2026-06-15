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
                .Where(applier => context.Observe(applier, item => item.EnabledForBuild) &&
                                  IsOwnerEnabled(applier, context) &&
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

        private static bool IsOwnerEnabled(BlendShareMesh applier, ComputeContext context)
        {
            if (applier == null)
            {
                return false;
            }

            var owner = context.Observe(applier, item => item.Owner);
            return owner != null && context.Observe(owner, item => item.enabled);
        }

        private sealed class Node : IRenderFilterNode
        {
            private Mesh mesh;
            private Transform[] bones = System.Array.Empty<Transform>();
            private Transform rootBone;
            private SkinnedMeshRenderer originalRenderer;
            private SkinnedMeshRenderer proxyRenderer;
            private BlendShareMesh[] weightAppliers = System.Array.Empty<BlendShareMesh>();
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
                    foreach (var applier in weightAppliers)
                    {
                        BlendShareBlendShapeWeightService.ApplyWeightsToRenderer(applier, skinnedMeshRenderer);
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
                weightAppliers = System.Array.Empty<BlendShareMesh>();
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

                    BlendShareComponentSetupService.PrepareMeshApplierGenerationMappings(applier);
                    enabledAppliers.Add(applier);
                }

                if (enabledAppliers.Count == 0)
                {
                    return;
                }

                var owners = enabledAppliers
                    .Select(applier => context.Observe(applier, item => item.Owner))
                    .Where(owner => owner != null)
                    .Distinct()
                    .ToArray();
                var sourceRoot = owners
                    .Select(BlendShareComponentSetupService.ResolveTargetRoot)
                    .FirstOrDefault(root => root != null) ?? originalRenderer.transform.root;
                var boneProxies = sourceRoot != null
                    ? sourceRoot.GetComponentsInChildren<BlendShareBoneProxy>(true)
                    : System.Array.Empty<BlendShareBoneProxy>();
                var generationComponents = owners.Cast<BlendShareComponent>()
                    .Concat(enabledAppliers)
                    .Concat(boneProxies.Where(proxy => proxy != null && owners.Contains(proxy.Owner)))
                    .Distinct()
                    .ToArray();
                ObserveProxyInputs(enabledAppliers, boneProxies, context);
                var artifact = BlendShareArtifactService.CreateInMemoryArtifact(
                    sourceRoot != null ? sourceRoot.gameObject : originalRenderer.transform.root.gameObject,
                    generationComponents);
                if (artifact == null)
                {
                    Debug.LogWarning("[BlendShare Preview] Failed to generate BlendShare artifact.", original);
                    return;
                }

                var proxyRoot = proxyRenderer.transform.root;
                RetargetArtifactRendererPathForPreview(artifact, proxyRenderer, proxyRoot);
                var proxyBoneOverrides = BuildProxyBoneOverrides(enabledAppliers, sourceRoot);

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
                weightAppliers = enabledAppliers.ToArray();
                hasPreviewOutput = mesh != null;

                context.Observe(originalRenderer, renderer => renderer.sharedMesh);
                context.Observe(originalRenderer, renderer => renderer.bones);
                context.Observe(originalRenderer, renderer => renderer.rootBone);
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

                var enabledAppliers = FindMeshAppliersForRenderer(renderer, context).ToArray();
                foreach (var applier in enabledAppliers)
                {
                    ObserveMeshApplier(applier, context);
                    builder.Append("|applier:").Append(applier != null ? applier.GetInstanceID() : 0);
                    AppendObject(builder, ":owner:", context.Observe(applier, item => item.Owner));
                    AppendObject(builder, ":meshData:", context.Observe(applier, item => item.MeshData));
                    foreach (var binding in applier?.BoneProxyBindings ?? System.Array.Empty<BlendShareBoneProxyBinding>())
                    {
                        AppendObject(builder, ":bindingArmature:", binding?.Armature);
                        builder.Append(":bindingPath:").Append(binding?.SourceBonePath);
                        AppendObject(builder, ":bindingProxy:", binding?.Proxy);
                        AppendProxyState(builder, binding?.Proxy, context);
                    }
                }

                return builder.ToString();
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
                AppendObject(builder, ":targetParent:", context.Observe(proxy, item => item.TargetParent));
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

                return context.GetComponentsByType<BlendShareMesh>()
                    .Where(applier => applier != null &&
                                      context.Observe(applier, item => item.EnabledForBuild) &&
                                      IsOwnerEnabled(applier, context) &&
                                      context.Observe(applier, item => item.TargetRenderer) == renderer)
                    .OrderBy(applier => GetHierarchyOrder(applier.Owner != null ? applier.Owner.transform : null))
                    .ThenBy(applier => GetHierarchyOrder(applier.transform));
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

            private static IReadOnlyDictionary<string, Transform> BuildProxyBoneOverrides(
                IEnumerable<BlendShareMesh> meshAppliers,
                Transform sourceRoot)
            {
                var overrides = new Dictionary<string, Transform>();
                if (sourceRoot == null)
                {
                    return overrides;
                }

                foreach (var applier in meshAppliers ?? System.Array.Empty<BlendShareMesh>())
                {
                    foreach (var binding in applier?.BoneProxyBindings ?? System.Array.Empty<BlendShareBoneProxyBinding>())
                    {
                        var proxy = binding?.Proxy;
                        if (proxy == null || proxy.TargetParent == null)
                        {
                            continue;
                        }

                        string parentPath = MeshNodePath.Normalize(MeshNodePath.GetRelativePath(proxy.TargetParent, sourceRoot));
                        string finalPath = parentPath == MeshNodePath.Root
                            ? MeshNodePath.Normalize(proxy.name)
                            : MeshNodePath.Normalize($"{parentPath}/{proxy.name}");
                        if (!overrides.ContainsKey(finalPath))
                        {
                            overrides.Add(finalPath, proxy.transform);
                        }
                    }
                }

                return overrides;
            }

            private static void ObserveMeshApplier(BlendShareMesh applier, ComputeContext context)
            {
                if (applier == null)
                {
                    return;
                }

                context.Observe(applier);
                context.Observe(applier, item => item.Owner);
                context.Observe(applier, item => item.TargetRenderer);
                context.Observe(applier, item => item.MeshData);
                context.Observe(applier, item => item.BlendShapeWeights.Count);
                context.Observe(applier, item => item.BlendShapeWeights
                    .Select(weight => weight != null ? $"{weight.ShapeName}:{weight.Weight:R}" : string.Empty)
                    .ToArray(), Enumerable.SequenceEqual);
                context.Observe(applier, item => item.BoneProxyBindings.Count);
                foreach (var binding in applier.BoneProxyBindings)
                {
                    if (binding == null)
                    {
                        continue;
                    }

                    context.Observe(binding.Armature);
                    context.Observe(binding.Proxy);
                }
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

                    foreach (var binding in applier?.BoneProxyBindings ?? System.Array.Empty<BlendShareBoneProxyBinding>())
                    {
                        ObserveProxy(binding?.Proxy, context);
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

                context.Observe(proxy, item => item.Owner);
                context.Observe(proxy, item => item.TargetParent);
                context.Observe(proxy, item => item.RecalculateBindpose);
                context.Observe(proxy, item => item.LocalPosition);
                context.Observe(proxy, item => item.LocalEulerRotation);
                context.Observe(proxy, item => item.LocalScale);
                context.Observe(proxy.gameObject, item => item.name);
                ObserveTransformPath(proxy.transform, context);
                if (proxy.TargetParent != null)
                {
                    ObserveTransformPath(proxy.TargetParent, context);
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
