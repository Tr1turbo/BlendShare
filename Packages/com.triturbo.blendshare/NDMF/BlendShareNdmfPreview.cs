using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using nadena.dev.ndmf.preview;
using Triturbo.BlendShare.EditorAssets.Settings;
using Triturbo.BlendShare.Pipeline.Apply;
using Triturbo.BlendShare.Runtime.Assets;
using Triturbo.BlendShare.Runtime.Components;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.NDMF
{
    internal sealed class BlendShareNdmfPreview : IRenderFilter
    {
        private static readonly TogglablePreviewNode s_PreviewNode =
            TogglablePreviewNode.Create(() => "BlendShare", "com.triturbo.blendshare/BlendSharePreview", true);
        private static readonly Dictionary<string, PublishedValue<long>> s_SettingsRevisionsByGuid = new();

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
            var targets = new HashSet<SkinnedMeshRenderer>();
            foreach (var component in context.GetComponentsByType<BlendSharePatchMesh>())
            {
                var target = context.Observe(component, c => c.TargetRenderer);
                var patch = context.Observe(component, c => c.ResolvedPatch);
                var meshId = context.Observe(component, c => c.ResolvedMeshId);
                var root = context.GetAvatarRoot(component.gameObject);
                if (context.ActiveAndEnabled(component) && root && target && patch && !string.IsNullOrWhiteSpace(meshId))
                    targets.Add(target);
            }
            return targets.Select(t => RenderGroup.For(t)).ToImmutableList();
        }

        public Task<IRenderFilterNode> Instantiate(RenderGroup group, IEnumerable<(Renderer, Renderer)> pairs, ComputeContext context)
        {
            var (original, proxy) = pairs.Single();
            var originalSmr = (SkinnedMeshRenderer)original;
            var proxySmr = (SkinnedMeshRenderer)proxy;
            var root = context.GetAvatarRoot(originalSmr.gameObject);
            if (!root) return Task.FromResult<IRenderFilterNode>(new Node());
            var components = new List<BlendSharePatchMesh>();
            foreach (var component in context.GetComponentsInChildren<BlendSharePatchMesh>(root, true))
            {
                if (context.Observe(component, c => c.TargetRenderer) != originalSmr) continue;
                context.Observe(component, c => c.ResolvedMeshId);
                var meshAsset = context.Observe(component, c => c.MeshAsset);
                if (meshAsset) context.Observe(meshAsset);
                var reference = context.Observe(component, c => c.ReferenceMesh);
                if (reference) context.Observe(reference);
                context.Observe(component, c => c.TargetFbxOverride);
                context.Observe(component, c => c.CreateMissingBones);
                context.Observe(component, c => c.BlendShapeWeights.Where(w => w != null)
                    .Select(w => (w.ShapeName, w.Weight)).ToArray(), (a, b) => a.SequenceEqual(b));
                context.ObservePath(component.transform).ToArray();
                var patch = context.Observe(component, c => c.ResolvedPatch);
                ObservePatchSettingsRevision(context, patch);
                if (context.ActiveAndEnabled(component)) components.Add(component);
            }
            var proxies = new List<BlendSharePatchBoneProxy>();
            foreach (var proxyComponent in context.GetComponentsInChildren<BlendSharePatchBoneProxy>(root, true))
            {
                context.Observe(proxyComponent);
                context.ObservePath(proxyComponent.transform).ToArray();
                ObservePatchSettingsRevision(context, context.Observe(proxyComponent, p => p.Patch));
                context.Observe(proxyComponent, p => p.ArmatureId);
                var bindings = context.Observe(proxyComponent, p => p.Bindings.Select(b =>
                    (b?.SourceBonePath, b?.TargetTransform)).ToArray(), (a, b) => a.SequenceEqual(b));
                foreach (var (_, target) in bindings)
                    if (target) context.ObservePath(target).ToArray();
                if (context.ActiveAndEnabled(proxyComponent)) proxies.Add(proxyComponent);
            }
            if (components.Count == 0 || !proxySmr || !proxySmr.sharedMesh)
                return Task.FromResult<IRenderFilterNode>(new Node());
            var incomingBones = proxySmr.bones;
            var result = BlendShareAvatarApplyService.ApplyToRenderer(new AvatarRendererApplyRequest
            {
                IncomingMesh = proxySmr.sharedMesh, IncomingBones = incomingBones, RendererTransform = proxySmr.transform,
                TargetRootTransform = root.transform, Components = components, BoneProxies = proxies,
                CreateMissingBones = false, FlushDirtySettings = false
            });
            return Task.FromResult<IRenderFilterNode>(result.Succeeded ? new Node(result, proxySmr, incomingBones) : new Node());
        }

        private sealed class Node : IRenderFilterNode
        {
            private readonly Mesh m_OwnedMesh;
            private readonly Transform[] m_OutputBones;
            private readonly (int Index, float Weight)[] m_Weights;
            private readonly (int Input, int Output)[] m_Inherited;
            private readonly float[] m_FrameWeights;
            public RenderAspects WhatChanged => m_OwnedMesh ? (RenderAspects.Mesh | RenderAspects.Shapes) : 0;
            public Node() { }
            public Node(AvatarRendererApplyResult result, SkinnedMeshRenderer proxy, Transform[] incomingBones)
            {
                m_OwnedMesh = result.MeshResult.OutputMesh;
                var bones = result.MeshResult.OutputBones;
                if (bones != null && !bones.SequenceEqual(incomingBones)) m_OutputBones = bones;
                m_Weights = result.ResolvedBlendShapeWeights.Select(w => (Index: m_OwnedMesh.GetBlendShapeIndex(w.Key), w.Value))
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
                foreach (var (index, weight) in m_Weights) smr.SetBlendShapeWeight(index, weight);
                for (int i = 0; i < m_Inherited.Length; i++) smr.SetBlendShapeWeight(m_Inherited[i].Output, m_FrameWeights[i]);
            }

            public void Dispose()
            {
                if (m_OwnedMesh && !AssetDatabase.Contains(m_OwnedMesh)) Object.DestroyImmediate(m_OwnedMesh);
            }
        }
    }
}
