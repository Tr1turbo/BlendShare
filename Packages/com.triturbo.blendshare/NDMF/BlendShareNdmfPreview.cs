using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using nadena.dev.ndmf.preview;
using Triturbo.BlendShare.EditorAssets.Settings;
using Triturbo.BlendShare.Pipeline.Apply;
using Triturbo.BlendShare.Runtime.Assets;
using Triturbo.BlendShare.Components;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.NDMF
{
    internal sealed class BlendShareNdmfPreview : IRenderFilter
    {
        private static readonly TogglablePreviewNode s_PreviewNode =
            TogglablePreviewNode.Create(() => "BlendShare", "com.triturbo.blendshare/BlendSharePreview", true);
        private static readonly Dictionary<string, PublishedValue<long>> s_SettingsRevisionsByGuid = new();
        private static readonly PublishedValue<bool> s_LimitSkinWeightsToFour =
            new PublishedValue<bool>(BlendShareSkinWeightSettings.LimitSkinWeightsToFour);

        static BlendShareNdmfPreview()
        {
            // SettingsChanged is raised by Editor settings operations and Undo callbacks.
            BlendShareSettingsSession.SettingsChanged += (guid, revision) =>
            {
                PublishedValue<long> value;
                lock (s_SettingsRevisionsByGuid)
                {
                    if (!s_SettingsRevisionsByGuid.TryGetValue(guid, out value)) return;
                }

                // Invoke PublishedValue setter outside lock to avoid re-entrancy deadlocks during invalidation.
                value.Value = revision;
            };
            BlendShareSkinWeightSettings.Changed += () =>
            {
                s_LimitSkinWeightsToFour.Value = BlendShareSkinWeightSettings.LimitSkinWeightsToFour;
            };
        }

        private static void ObservePatchSettingsRevision(ComputeContext context, BlendShareAsset patch)
        {
            if (patch) context.Observe(patch);
            if (!patch || !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(patch, out string guid, out long _)) return;
            PublishedValue<long> value;
            lock (s_SettingsRevisionsByGuid)
            {
                if (!s_SettingsRevisionsByGuid.TryGetValue(guid, out value))
                    s_SettingsRevisionsByGuid.Add(guid, value = new PublishedValue<long>(BlendShareSettingsSession.GetSettingsRevision(guid)));
            }

            context.Observe(value);
        }

        public bool IsEnabled(ComputeContext context) => context.Observe(s_PreviewNode.IsEnabled);
        public IEnumerable<TogglablePreviewNode> GetPreviewControlNodes() { yield return s_PreviewNode; }

        public ImmutableList<RenderGroup> GetTargetGroups(ComputeContext context)
        {
            var targets = context.GetComponentsByType<BlendShareMesh>()
                .Where(component => context.ActiveAndEnabled(component))
                .GroupBy(component => context.GetAvatarRoot(component.gameObject))
                .Where(group => group.Key)
                .SelectMany(group => SelectMeshComponents(group, context).Where(c => c.TargetRenderer &&
                    context.GetAvatarRoot(c.TargetRenderer.gameObject) == group.Key))
                .Select(component => component.TargetRenderer)
                .Distinct();
            return targets.Select(t => RenderGroup.For(t)).ToImmutableList();
        }

        private static BlendShareMesh[] SelectMeshComponents(IEnumerable<BlendShareMesh> candidates, ComputeContext context)
        {
            var ordered = candidates.ToArray();
            foreach (var component in ordered)
            {
                ObserveTransform(context, component.transform, observePose: false);
                var patch = context.Observe(component, c => c.ResolvedPatch);
                if (patch) context.Observe(patch, p => p.PatchId);
                var meshAsset = context.Observe(component, c => c.MeshAsset);
                if (meshAsset) context.Observe(meshAsset, asset => asset.MeshId);
                context.Observe(component, c => c.ResolvedMeshId);
                context.Observe(component, c => c.TargetRenderer);
            }
            return BlendShareNdmfPass.SelectMeshComponents(ordered)
                .Where(c => c.ResolvedPatch && !string.IsNullOrWhiteSpace(c.ResolvedMeshId)).ToArray();
        }

        public Task<IRenderFilterNode> Instantiate(RenderGroup group, IEnumerable<(Renderer, Renderer)> pairs, ComputeContext context)
        {
            var (original, proxy) = pairs.Single();
            var originalSmr = (SkinnedMeshRenderer)original;
            var proxySmr = (SkinnedMeshRenderer)proxy;
            var root = context.GetAvatarRoot(originalSmr.gameObject);
            if (!root) return Task.FromResult<IRenderFilterNode>(new Node());
            var components = new List<BlendShareMesh>();
            var activeMeshes = context.GetComponentsInChildren<BlendShareMesh>(root, true)
                .Where(c => context.ActiveAndEnabled(c) && context.GetAvatarRoot(c.gameObject) == root);
            foreach (var component in SelectMeshComponents(activeMeshes, context))
            {
                if (component.TargetRenderer != originalSmr) continue;
                var reference = context.Observe(component, c => c.ReferenceMesh);
                if (reference) context.Observe(reference);
                context.Observe(component, c => c.TargetFbxOverride);
                context.Observe(component, c => c.CreateMissingBones);
                context.Observe(component, c => c.BindPoseOverrides.Where(b => b != null)
                    .Select(b => (b.SourceBonePath, b.BindPose)).ToArray(), (a, b) => a.SequenceEqual(b));
                context.Observe(component, c => c.BlendShapeWeights.Where(w => w != null)
                    .Select(w => (w, w.ShapeName)).ToArray(), (a, b) => a.SequenceEqual(b));
                var patch = context.Observe(component, c => c.ResolvedPatch);
                ObservePatchSettingsRevision(context, patch);
                components.Add(component);
            }
            var proxies = new List<BlendShareBoneProxy>();
            foreach (var proxyComponent in context.GetComponentsInChildren<BlendShareBoneProxy>(root, true))
            {
                if (!context.ActiveAndEnabled(proxyComponent) || context.GetAvatarRoot(proxyComponent.gameObject) != root) continue;
                context.Observe(proxyComponent);
                ObserveTransform(context, proxyComponent.transform);
                context.Observe(proxyComponent, p => p.UseHierarchyParent);
                ObserveTransform(context, context.Observe(proxyComponent, p => p.TargetParent));
                ObservePatchSettingsRevision(context, context.Observe(proxyComponent, p => p.Patch));
                context.Observe(proxyComponent, p => p.ArmatureId);
                var bindings = context.Observe(proxyComponent, p => p.Bindings.Select(b =>
                    (b?.SourceBonePath, b?.Transform)).ToArray(), (a, b) => a.SequenceEqual(b));
                foreach (var (_, target) in bindings)
                    ObserveTransform(context, target);
                proxies.Add(proxyComponent);
            }
            if (components.Count == 0 || !proxySmr || !proxySmr.sharedMesh)
                return Task.FromResult<IRenderFilterNode>(new Node());
            ObserveTransform(context, originalSmr.transform);
            context.Observe(s_LimitSkinWeightsToFour);
            var incomingBones = proxySmr.bones;
            var result = BlendShareAvatarApplyService.ApplyToRenderer(new AvatarRendererApplyRequest
            {
                IncomingMesh = proxySmr.sharedMesh, IncomingBones = incomingBones, IncomingRootBone = proxySmr.rootBone,
                RendererTransform = proxySmr.transform,
                TargetRootTransform = root.transform, Components = components, BoneProxies = proxies,
                CreateMissingBones = false, FlushDirtySettings = false
            });
            return Task.FromResult<IRenderFilterNode>(result.Succeeded ? new Node(result, proxySmr, incomingBones, components) : new Node());
        }

        private static void ObserveTransform(ComputeContext context, Transform target, bool observePose = true)
        {
            if (!target) return;
            foreach (var ancestor in context.ObservePath(target))
            {
                context.Observe(ancestor, t => t.GetSiblingIndex());
                if (observePose) context.Observe(ancestor, t => (t.localPosition, t.localRotation, t.localScale));
                context.Observe(ancestor.gameObject, go => go.name);
            }
        }

        private sealed class Node : IRenderFilterNode
        {
            private readonly Mesh m_OwnedMesh;
            private readonly Transform[] m_OutputBones;
            private readonly Transform m_OutputRootBone;
            private readonly (int Index, BlendShareProxyBlendShapeWeight Control)[] m_Weights;
            private readonly (int Input, int Output)[] m_Inherited;
            private readonly float[] m_FrameWeights;
            public RenderAspects WhatChanged => m_OwnedMesh ? (RenderAspects.Mesh | RenderAspects.Shapes) : 0;
            public Node() { }
            public Node(AvatarRendererApplyResult result, SkinnedMeshRenderer proxy, Transform[] incomingBones, IEnumerable<BlendShareMesh> components)
            {
                m_OwnedMesh = result.MeshResult.OutputMesh;
                var bones = result.MeshResult.OutputBones;
                if (bones != null && !bones.SequenceEqual(incomingBones)) m_OutputBones = bones;
                if (result.MeshResult.OutputRootBone != null && result.MeshResult.OutputRootBone != proxy.rootBone)
                    m_OutputRootBone = result.MeshResult.OutputRootBone;
                m_Weights = components.SelectMany(c => c.BlendShapeWeights)
                    .Where(w => w != null && !string.IsNullOrWhiteSpace(w.ShapeName))
                    .Select(w => (Index: m_OwnedMesh.GetBlendShapeIndex(w.ShapeName), Control: w))
                    .Where(w => w.Index >= 0).ToArray();
                var incoming = proxy.sharedMesh;
                m_Inherited = Enumerable.Range(0, incoming.blendShapeCount)
                    .Select(i => (i, Output: m_OwnedMesh.GetBlendShapeIndex(incoming.GetBlendShapeName(i))))
                    .Where(w => w.Output >= 0).ToArray();
                m_FrameWeights = new float[m_Inherited.Length];
            }

            public Task<IRenderFilterNode> Refresh(IEnumerable<(Renderer, Renderer)> pairs, ComputeContext context, RenderAspects updatedAspects)
                => Task.FromResult<IRenderFilterNode>(null);

            public void OnFrame(Renderer original, Renderer proxy)
            {
                if (!m_OwnedMesh || !(proxy is SkinnedMeshRenderer smr)) return;
                var currentMesh = smr.sharedMesh;
                int currentCount = currentMesh ? currentMesh.blendShapeCount : 0;
                bool isAlreadyOwned = currentMesh == m_OwnedMesh;
                for (int i = 0; i < m_Inherited.Length; i++)
                {
                    int srcIndex = isAlreadyOwned ? m_Inherited[i].Output : m_Inherited[i].Input;
                    m_FrameWeights[i] = (uint)srcIndex < (uint)currentCount ? smr.GetBlendShapeWeight(srcIndex) : 0f;
                }
                smr.sharedMesh = m_OwnedMesh;
                if (m_OutputBones != null) smr.bones = m_OutputBones;
                if (m_OutputRootBone != null) smr.rootBone = m_OutputRootBone;
                foreach (var (index, control) in m_Weights) smr.SetBlendShapeWeight(index, control.Weight);
                for (int i = 0; i < m_Inherited.Length; i++) smr.SetBlendShapeWeight(m_Inherited[i].Output, m_FrameWeights[i]);
            }

            public void Dispose()
            {
                if (m_OwnedMesh && !AssetDatabase.Contains(m_OwnedMesh)) Object.DestroyImmediate(m_OwnedMesh);
            }
        }
    }
}
