using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Triturbo.BlendShare.EditorAssets.Mapping;
using Triturbo.BlendShare.Pipeline.Apply;
using Triturbo.BlendShare.Assets;
using Triturbo.BlendShare.Components;
using UnityEngine;
using UnityEngine.UIElements;

namespace Triturbo.BlendShare.NDMF
{
    [DependsOnContext(typeof(AnimatorServicesContext))]
    internal sealed class BlendShareNdmfPass : Pass<BlendShareNdmfPass>
    {
        public override string QualifiedName => "com.triturbo.blendshare.apply";
        public override string DisplayName => "Apply BlendShare";

        protected override void Execute(BuildContext context)
        {
            var root = context.AvatarRootObject;
            var allMeshes = root.GetComponentsInChildren<BlendShareMesh>(true);
            var allProxies = root.GetComponentsInChildren<BlendShareBoneProxy>(true);
            if (allMeshes.Length == 0 && allProxies.Length == 0) return;
            var components = SelectMeshComponents(allMeshes.Where(c => c.isActiveAndEnabled));
            var proxies = allProxies.Where(c => c.isActiveAndEnabled).ToArray();
            var animator = context.Extension<AnimatorServicesContext>();
            var mergeResolver = new BlendShareBoneBindingMergeResolver(context, animator);
            var uses = new List<AvatarBoneBindingUse>();
            var skeletonBonesByPath = new Dictionary<string, GeneratedSkeletonBoneRecord>(System.StringComparer.Ordinal);
            var capturedWeights = BlendShareBlendShapeWeightService.CaptureExistingRendererWeights(components);
            bool succeeded = true;
            var targetedGroups = new List<(SkinnedMeshRenderer Renderer, BlendShareMesh[] Group)>();
            var avatarRequests = new List<AvatarRendererApplyRequest>();
            foreach (var group in components.GroupBy(c => c.TargetRenderer))
            {
                var renderer = group.Key;
                if (!renderer || !renderer.transform.IsChildOf(root.transform))
                {
                    foreach (var component in group)
                        ErrorReport.ReportError(new ApplyError($"Missing avatar target renderer on {component.name}."));
                    succeeded = false;
                    continue;
                }

                var groupArray = group.ToArray();
                targetedGroups.Add((renderer, groupArray));
                avatarRequests.Add(new AvatarRendererApplyRequest
                {
                    IncomingMesh = renderer.sharedMesh, IncomingBones = renderer.bones, IncomingRootBone = renderer.rootBone,
                    Components = groupArray, BoneProxies = proxies,
                    RendererTransform = renderer.transform, TargetRootTransform = root.transform,
                    // Never invoke SaveAndReimport() inside an NDMF build pass; EffectiveSettingsResolver
                    // already consumes live in-memory settings buffers cleanly across Edit/Play/read-only assets.
                    CreateMissingBones = true, FlushDirtySettings = false,
                    VertexMappingCacheMode = VertexMappingCacheMode.ReadOnly
                });
            }

            if (!succeeded) return;

            var avatarResults = BlendShareAvatarApplyService.ApplyToAvatarRenderers(root.transform, avatarRequests);
            for (int r = 0; r < targetedGroups.Count; r++)
            {
                var result = avatarResults[r];
                if (!result.Succeeded)
                {
                    ErrorReport.ReportError(new ApplyError(string.Join("\n", result.Diagnostics.Select(d => $"{d.Code}: {d.Message}"))));
                    succeeded = false;
                    continue;
                }

                uses.AddRange(result.BoneBindingUses);
                if (result.MeshResult.AppliedSkeletonBones != null)
                {
                    foreach (var skelBone in result.MeshResult.AppliedSkeletonBones)
                    {
                        if (skelBone == null || string.IsNullOrEmpty(skelBone.Path))
                        {
                            continue;
                        }

                        if (skeletonBonesByPath.TryGetValue(skelBone.Path, out var existingSkelBone))
                        {
                            if (!existingSkelBone.ApproximatelyMatches(skelBone))
                            {
                                ErrorReport.ReportError(new ApplyError(
                                    $"Conflicting skeleton bone transforms for '{skelBone.Path}' across avatar renderers."));
                                succeeded = false;
                                break;
                            }
                        }
                        else
                        {
                            skeletonBonesByPath[skelBone.Path] = skelBone;
                        }
                    }
                }
            }

            if (!succeeded)
            {
                for (int r = 0; r < avatarResults.Count; r++)
                {
                    var outMesh = avatarResults[r]?.MeshResult?.OutputMesh;
                    if (outMesh != null && !UnityEditor.AssetDatabase.Contains(outMesh))
                    {
                        Object.DestroyImmediate(outMesh);
                    }
                }

                return;
            }

            for (int r = 0; r < targetedGroups.Count; r++)
            {
                var (renderer, group) = targetedGroups[r];
                var incoming = renderer.sharedMesh;
                var result = avatarResults[r];
                var output = result.MeshResult.OutputMesh;
                ObjectRegistry.RegisterReplacedObject(incoming, output);
                context.AssetSaver.SaveAsset(output);
                renderer.sharedMesh = output;
                var bones = result.MeshResult.OutputBones;
                if (bones != null && bones.Length > 0)
                    renderer.bones = bones;
                if (result.MeshResult.OutputRootBone != null)
                    renderer.rootBone = result.MeshResult.OutputRootBone;
                foreach (var applier in group)
                    BlendShareBlendShapeWeightService.ApplyWeightsToRenderer(applier, renderer);
                BlendShareBlendShapeWeightService.ApplyCapturedRendererWeights(capturedWeights, renderer);
            }

            // Apply authored skeleton bone transforms strictly onto existing or already-materialized transforms
            // (so missing bones from components with CreateMissingBones = false are never fabricated here).
            var ensuredSkeletonTransforms = new HashSet<Transform>();
            foreach (var skelBone in skeletonBonesByPath.Values
                         .OrderBy(b => b.Path.Count(c => c == '/'))
                         .ThenBy(b => b.Path, System.StringComparer.Ordinal))
            {
                var targetBone = root.transform.Find(skelBone.Path);
                if (targetBone != null)
                {
                    skelBone.ApplyLocalTransform(targetBone);
                    ensuredSkeletonTransforms.Add(targetBone);
                }
            }
            BlendShareBlendShapeWeightService.RetargetRendererBlendShapeCurves(components, animator);
            if (!mergeResolver.Prepare(uses, out var diagnostic) || !mergeResolver.Cleanup(out diagnostic))
            {
                ErrorReport.ReportError(new ApplyError(diagnostic));
                return;
            }

            context.GetState<RendererWeightState>().Weights =
                BlendShareBlendShapeWeightService.CaptureExistingRendererWeights(components);
            var mapped = new HashSet<Transform>(root.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .SelectMany(renderer => renderer.bones.Append(renderer.rootBone)));
            mapped.UnionWith(ensuredSkeletonTransforms);
            mapped.UnionWith(proxies.Where(proxy => proxy).SelectMany(proxy => proxy.Bindings)
                .Where(binding => binding != null).Select(binding => binding.Transform));
            var allAuthoring = allMeshes.Cast<Component>().Concat(allProxies);
            foreach (var component in allAuthoring)
            {
                if (!component) continue;
                var host = component.gameObject;
                bool dedicated = host != root && host.transform.childCount == 0 && !mapped.Contains(host.transform)
                    && host.GetComponents<Component>().Length == 2 && mergeResolver.CanRemoveHost(host.transform);
                Object.DestroyImmediate(dedicated ? (Object)host : component);
            }
        }

        // A newer instance of a logical patch replaces the older asset at its original
        // hierarchy slot. Within that asset the last setup for a renderer and mesh wins.
        internal static BlendShareMesh[] SelectMeshComponents(IEnumerable<BlendShareMesh> candidates)
        {
            var ordered = candidates.ToArray();
            var patches = ordered.Select(c => c.ResolvedPatch).Where(patch => patch)
                .GroupBy(patch => (patch.PatchId, AnonymousPatch: string.IsNullOrEmpty(patch.PatchId) ? patch : null))
                .Select((group, slot) => (Patch: group.Last(), Slot: slot))
                .ToDictionary(pair => pair.Patch, pair => pair.Slot);
            var winners = ordered.Where(c => c.ResolvedPatch && patches.ContainsKey(c.ResolvedPatch))
                .GroupBy(c => (c.ResolvedPatch, c.ResolvedMeshId, c.TargetRenderer))
                .Select(group => group.Last()).ToHashSet();
            return ordered.Where(c => !c.ResolvedPatch || winners.Contains(c))
                .OrderBy(c => c.ResolvedPatch ? patches[c.ResolvedPatch] : int.MaxValue).ToArray();
        }

        private sealed class RendererWeightState
        {
            public IReadOnlyDictionary<SkinnedMeshRenderer, IReadOnlyDictionary<string, float>> Weights { get; set; }
        }

        internal static void RestoreRendererWeights(BuildContext context)
        {
            var weights = context.GetState<RendererWeightState>().Weights;
            if (weights == null) return;
            foreach (var renderer in weights.Keys)
                BlendShareBlendShapeWeightService.ApplyCapturedRendererWeights(weights, renderer);
        }

        private sealed class ApplyError : IError
        {
            private readonly string m_Message;
            public ApplyError(string message) => m_Message = message;
            public ErrorSeverity Severity => ErrorSeverity.Error;
            public string ToMessage() => m_Message;
            public VisualElement CreateVisualElement(ErrorReport report) => new Label(m_Message);
            public void AddReference(ObjectReference obj) { }
        }
    }
}
