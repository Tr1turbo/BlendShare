using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Triturbo.BlendShare.Pipeline.Apply;
using Triturbo.BlendShare.Runtime.Assets;
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

                var incoming = renderer.sharedMesh;
                var result = BlendShareAvatarApplyService.ApplyToRenderer(new AvatarRendererApplyRequest
                {
                    IncomingMesh = incoming, IncomingBones = renderer.bones, IncomingRootBone = renderer.rootBone,
                    Components = group.ToArray(), BoneProxies = proxies,
                    RendererTransform = renderer.transform, TargetRootTransform = root.transform,
                    CreateMissingBones = true, FlushDirtySettings = true
                });
                if (!result.Succeeded)
                {
                    ErrorReport.ReportError(new ApplyError(string.Join("\n", result.Diagnostics.Select(d => $"{d.Code}: {d.Message}"))));
                    succeeded = false;
                    continue;
                }

                var output = result.MeshResult.OutputMesh;
                uses.AddRange(result.BoneBindingUses);
                if (result.MeshResult.AppliedSkeletonBones != null)
                {
                    foreach (var skelBone in result.MeshResult.AppliedSkeletonBones)
                    {
                        if (skelBone != null && !string.IsNullOrEmpty(skelBone.Path))
                        {
                            skeletonBonesByPath[skelBone.Path] = skelBone;
                        }
                    }
                }

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
            if (!succeeded) return;

            // Apply authored existing-bone transform changes (non-proxy) in parent-before-child order.
            foreach (var skelBone in skeletonBonesByPath.Values
                         .OrderBy(b => b.Path.Count(c => c == '/'))
                         .ThenBy(b => b.Path, System.StringComparer.Ordinal))
            {
                var existing = root.transform.Find(skelBone.Path);
                if (existing != null)
                {
                    skelBone.ApplyLocalTransform(existing);
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
