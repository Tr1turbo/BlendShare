using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using nadena.dev.ndmf.preview;
using Triturbo.BlendShare.Assets;
using Triturbo.BlendShare.Components;
using Triturbo.BlendShare.EditorAssets;
using Triturbo.BlendShare.EditorAssets.Mapping;
using Triturbo.BlendShare.EditorAssets.Settings;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Model.Documents;
using Triturbo.BlendShare.Model.Features;
using Triturbo.BlendShare.Pipeline.Apply;
using Triturbo.BlendShare.Pipeline.Skeleton;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

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

        // Coupled avatar skeleton/skin evaluation requires every member of the avatar's RenderGroup
        // to remain present even when a sibling renderer is disabled in the scene.
        public bool StrictRenderGroup => true;

        public bool IsEnabled(ComputeContext context) => context.Observe(s_PreviewNode.IsEnabled);
        public IEnumerable<TogglablePreviewNode> GetPreviewControlNodes() { yield return s_PreviewNode; }

        public ImmutableList<RenderGroup> GetTargetGroups(ComputeContext context)
        {
            var groupsByRoot = context.GetComponentsByType<BlendShareMesh>()
                .Where(component => context.ActiveAndEnabled(component))
                .GroupBy(component => context.GetAvatarRoot(component.gameObject))
                .Where(group => group.Key);

            var result = ImmutableList.CreateBuilder<RenderGroup>();
            foreach (var group in groupsByRoot)
            {
                var root = group.Key;
                var selected = SelectMeshComponents(group, context);
                var renderers = new List<SkinnedMeshRenderer>();
                var rendererSet = new HashSet<SkinnedMeshRenderer>();

                for (int i = 0; i < selected.Length; i++)
                {
                    var target = selected[i].TargetRenderer;
                    if (target && context.GetAvatarRoot(target.gameObject) == root && rendererSet.Add(target))
                    {
                        renderers.Add(target);
                    }
                }

                if (renderers.Count == 0)
                {
                    continue;
                }

                var authoredSkeletonPaths = CollectAuthoredSkeletonPaths(selected, context);
                if (authoredSkeletonPaths.Count > 0)
                {
                    // Include unpatched sibling SkinnedMeshRenderers on the same avatar whose bone chain intersects
                    // any authored skeleton bone so preview moves shared bones identically to NDMF Build.
                    foreach (var sibling in context.GetComponentsInChildren<SkinnedMeshRenderer>(root, true))
                    {
                        if (!sibling || rendererSet.Contains(sibling) || context.GetAvatarRoot(sibling.gameObject) != root)
                        {
                            continue;
                        }

                        var siblingMesh = context.Observe(sibling, r => r.sharedMesh);
                        if (!siblingMesh)
                        {
                            continue;
                        }

                        var bones = context.Observe(sibling, r => r.bones, ReferenceArrayEquals);
                        var rootBone = context.Observe(sibling, r => r.rootBone);
                        bool affected = false;

                        if (bones != null)
                        {
                            for (int b = 0; b < bones.Length; b++)
                            {
                                var bone = bones[b];
                                if (!bone) continue;
                                ObserveTransform(context, bone, observePose: false);
                                if (IsTransformAffectedByAuthoredSkeleton(bone, root.transform, authoredSkeletonPaths))
                                {
                                    affected = true;
                                }
                            }
                        }

                        if (rootBone)
                        {
                            ObserveTransform(context, rootBone, observePose: false);
                            if (IsTransformAffectedByAuthoredSkeleton(rootBone, root.transform, authoredSkeletonPaths))
                            {
                                affected = true;
                            }
                        }

                        if (affected && rendererSet.Add(sibling))
                        {
                            renderers.Add(sibling);
                        }
                    }
                }

                result.Add(RenderGroup.For(renderers));
            }

            return result.ToImmutable();
        }

        private static bool ReferenceArrayEquals(Transform[] left, Transform[] right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null || left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++)
            {
                if (!ReferenceEquals(left[i], right[i])) return false;
            }

            return true;
        }

        private static HashSet<string> CollectAuthoredSkeletonPaths(BlendShareMesh[] selectedComponents, ComputeContext context)
        {
            var paths = new HashSet<string>(StringComparer.Ordinal);
            var visitedPatches = new HashSet<BlendShareAsset>();

            for (int i = 0; i < selectedComponents.Length; i++)
            {
                var patch = selectedComponents[i].ResolvedPatch;
                if (!patch || !visitedPatches.Add(patch))
                {
                    continue;
                }

                ObservePatchSettingsRevision(context, patch);
                if (!patch.HasSkeleton && !PatchContainsEntryType(patch, SkeletonDocument.SectionTypeId))
                {
                    continue;
                }

                var skelRes = BlendShareSourceRepository.Shared.ReadSkeleton(patch);
                if (!skelRes.Success || skelRes.Value?.Nodes == null)
                {
                    continue;
                }

                for (int b = 0; b < skelRes.Value.Nodes.Count; b++)
                {
                    string norm = FbxNameUtility.NormalizePath(skelRes.Value.Nodes[b]?.Path ?? string.Empty);
                    if (!string.IsNullOrEmpty(norm))
                    {
                        paths.Add(norm);
                    }
                }
            }

            return paths;
        }

        private static bool IsTransformAffectedByAuthoredSkeleton(
            Transform bone,
            Transform avatarRoot,
            HashSet<string> authoredSkeletonPaths)
        {
            if (!bone || !avatarRoot || bone == avatarRoot || !bone.IsChildOf(avatarRoot))
            {
                return false;
            }

            string relPath = FbxNameUtility.NormalizePath(AnimationUtility.CalculateTransformPath(bone, avatarRoot));
            if (string.IsNullOrEmpty(relPath))
            {
                return false;
            }

            string current = relPath;
            while (!string.IsNullOrEmpty(current))
            {
                if (authoredSkeletonPaths.Contains(current))
                {
                    return true;
                }

                int slash = current.LastIndexOf('/');
                current = slash > 0 ? current.Substring(0, slash) : string.Empty;
            }

            return false;
        }

        private static bool PatchContainsEntryType(BlendShareAsset patch, string typeId)
        {
            if (!patch) return false;
            var entries = patch.Entries;
            if (entries == null) return false;
            for (int i = 0; i < entries.Count; i++)
            {
                if (string.Equals(entries[i].TypeId, typeId, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
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
            var pairArray = pairs
                .Where(p => p.Item1 is SkinnedMeshRenderer && p.Item2 is SkinnedMeshRenderer)
                .Select(p => (Original: (SkinnedMeshRenderer)p.Item1, Proxy: (SkinnedMeshRenderer)p.Item2))
                .ToArray();
            if (pairArray.Length == 0) return Task.FromResult<IRenderFilterNode>(new Node());

            var root = context.GetAvatarRoot(pairArray[0].Original.gameObject);
            if (!root) return Task.FromResult<IRenderFilterNode>(new Node());

            // Observe the avatar root and its full scene ancestor chain so inherited world shear/TRS changes trigger updates.
            ObserveTransform(context, root.transform);

            var activeMeshes = context.GetComponentsInChildren<BlendShareMesh>(root, true)
                .Where(c => context.ActiveAndEnabled(c) && context.GetAvatarRoot(c.gameObject) == root);
            var allSelectedAvatarComponents = SelectMeshComponents(activeMeshes, context);

            var componentsByOriginal = new Dictionary<SkinnedMeshRenderer, List<BlendShareMesh>>();
            bool needsShadowRig = false;
            foreach (var component in allSelectedAvatarComponents)
            {
                var patch = context.Observe(component, c => c.ResolvedPatch);
                ObservePatchSettingsRevision(context, patch);
                if (patch && (PatchContainsEntryType(patch, SkinWeightFeature.FeatureTypeId) ||
                              patch.HasSkeleton ||
                              PatchContainsEntryType(patch, SkeletonDocument.SectionTypeId)))
                {
                    needsShadowRig = true;
                }

                if (!component.TargetRenderer) continue;
                var reference = context.Observe(component, c => c.ReferenceMesh);
                if (reference) context.Observe(reference);
                context.Observe(component, c => c.TargetFbxOverride);
                context.Observe(component, c => c.CreateMissingBones);
                context.Observe(component, c => c.BindPoseOverrides.Where(b => b != null)
                    .Select(b => (b.SourceBonePath, b.BindPose)).ToArray(), (a, b) => a.SequenceEqual(b));
                context.Observe(component, c => c.BlendShapeWeights.Where(w => w != null)
                    .Select(w => (w, w.ShapeName)).ToArray(), (a, b) => a.SequenceEqual(b));

                if (!componentsByOriginal.TryGetValue(component.TargetRenderer, out var list))
                {
                    list = new List<BlendShareMesh>();
                    componentsByOriginal.Add(component.TargetRenderer, list);
                }
                list.Add(component);
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
                var bindings = context.Observe(proxyComponent, p => p.Bindings.Select(b =>
                    (b?.SourceBonePath, b?.Transform)).ToArray(), (a, b) => a.SequenceEqual(b));
                foreach (var (_, target) in bindings)
                    ObserveTransform(context, target);
                proxies.Add(proxyComponent);
            }

            context.Observe(s_LimitSkinWeightsToFour);

            var proxyByOriginal = new Dictionary<SkinnedMeshRenderer, SkinnedMeshRenderer>();
            var validPairs = new List<(SkinnedMeshRenderer Original, SkinnedMeshRenderer Proxy)>(pairArray.Length);
            for (int i = 0; i < pairArray.Length; i++)
            {
                var orig = pairArray[i].Original;
                var prx = pairArray[i].Proxy;
                if (!orig)
                {
                    continue;
                }

                bool isPatchedTarget = componentsByOriginal.TryGetValue(orig, out var targetComps) &&
                                       targetComps != null &&
                                       targetComps.Count > 0;
                if (!prx || !prx.sharedMesh)
                {
                    if (isPatchedTarget)
                    {
                        return Task.FromResult<IRenderFilterNode>(new Node());
                    }

                    // Skip meshless unpatched bone-remap-only siblings rather than suppressing visible patched renderers.
                    continue;
                }

                ObserveTransform(context, orig.transform);
                ObserveTransform(context, prx.transform);
                proxyByOriginal[orig] = prx;
                validPairs.Add((orig, prx));
                if (!isPatchedTarget)
                {
                    needsShadowRig = true;
                }
            }

            // Honor supplied proxy state across the coupled avatar group: if any active targeted renderer in the group
            // lacks a valid proxy or upstream proxy mesh, defer the coupled group rather than falling back to stale original meshes.
            foreach (var kv in componentsByOriginal)
            {
                var orig = kv.Key;
                var comps = kv.Value;
                if (!orig || comps == null || comps.Count == 0) continue;
                if (!proxyByOriginal.TryGetValue(orig, out var prx) || !prx || !prx.sharedMesh)
                {
                    return Task.FromResult<IRenderFilterNode>(new Node());
                }
            }

            if (validPairs.Count == 0)
            {
                return Task.FromResult<IRenderFilterNode>(new Node());
            }

            for (int i = 0; i < validPairs.Count; i++)
            {
                var orig = validPairs[i].Original;
                var prx = validPairs[i].Proxy;

                var origBones = orig.bones;
                if (origBones != null)
                {
                    for (int b = 0; b < origBones.Length; b++)
                    {
                        if (origBones[b]) ObserveTransform(context, origBones[b]);
                    }
                }
                if (orig.rootBone) ObserveTransform(context, orig.rootBone);

                var prxBones = prx.bones;
                if (prxBones != null)
                {
                    for (int b = 0; b < prxBones.Length; b++)
                    {
                        if (prxBones[b]) ObserveTransform(context, prxBones[b]);
                    }
                }
                if (prx.rootBone) ObserveTransform(context, prx.rootBone);
            }

            PreviewSkeletonRig previewRig = null;
            var mappedPairBones = new (Transform[] MappedBones, string[] MappedSlotPaths, Transform MappedRootBone)[validPairs.Count];
            if (needsShadowRig)
            {
                previewRig = PreviewSkeletonRig.CreateInitial(root.transform, validPairs[0].Proxy.gameObject.scene);
                for (int i = 0; i < validPairs.Count; i++)
                {
                    var orig = validPairs[i].Original;
                    var prx = validPairs[i].Proxy;
                    previewRig.RegisterAndMapRendererBones(
                        orig.bones,
                        prx.bones,
                        orig.rootBone,
                        prx.rootBone,
                        out var mappedBones,
                        out var mappedSlotPaths,
                        out var mappedRoot);
                    mappedPairBones[i] = (mappedBones, mappedSlotPaths, mappedRoot);
                }

                // Synchronize pre-BlendShare live and upstream bone poses before skeleton/bindpose solving runs.
                previewRig.Sync();
            }

            var orderedOriginals = new List<SkinnedMeshRenderer>(validPairs.Count);
            var applyRequests = new List<AvatarRendererApplyRequest>(validPairs.Count);

            for (int i = 0; i < validPairs.Count; i++)
            {
                var orig = validPairs[i].Original;
                var prx = validPairs[i].Proxy;
                if (!componentsByOriginal.TryGetValue(orig, out var comps) || comps.Count == 0)
                {
                    continue;
                }

                var reqBones = previewRig != null ? mappedPairBones[i].MappedBones : prx.bones;
                var reqSlotPaths = previewRig != null ? mappedPairBones[i].MappedSlotPaths : null;
                var reqRootBone = previewRig != null ? mappedPairBones[i].MappedRootBone : prx.rootBone;

                orderedOriginals.Add(orig);
                applyRequests.Add(new AvatarRendererApplyRequest
                {
                    IncomingMesh = prx.sharedMesh,
                    IncomingBones = reqBones,
                    IncomingBoneSlotPaths = reqSlotPaths,
                    IncomingRootBone = reqRootBone,
                    RendererTransform = prx.transform,
                    TargetRootTransform = previewRig != null ? previewRig.ShadowRoot : root.transform,
                    AuthoringRootTransform = root.transform,
                    RemapAuthoringBoneTransform = previewRig != null ? previewRig.RemapAuthoringBone : null,
                    Components = comps,
                    BoneProxies = proxies,
                    // When previewRig owns TargetRootTransform, newly authored bones eligible for creation are instantiated
                    // strictly inside previewRig.ShadowRoot (never mutating live scene hierarchy) so TotalOne validation succeeds.
                    CreateMissingBones = previewRig != null,
                    FlushDirtySettings = false,
                    VertexMappingCacheMode = VertexMappingCacheMode.PersistAssetsOnly
                });
            }

            if (orderedOriginals.Count == 0)
            {
                previewRig?.Dispose();
                return Task.FromResult<IRenderFilterNode>(new Node());
            }

            var effectiveTargetRoot = previewRig != null ? previewRig.ShadowRoot : root.transform;
            var results = BlendShareAvatarApplyService.ApplyToAvatarRenderers(effectiveTargetRoot, applyRequests);
            if (results.Any(r => !r.Succeeded))
            {
                for (int i = 0; i < results.Count; i++)
                {
                    var owned = results[i]?.MeshResult?.OutputMesh;
                    if (owned && !AssetDatabase.Contains(owned)) Object.DestroyImmediate(owned);
                }
                previewRig?.Dispose();
                return Task.FromResult<IRenderFilterNode>(new Node());
            }

            var skeletonBonesByPath = new Dictionary<string, GeneratedSkeletonBoneRecord>(StringComparer.Ordinal);
            for (int i = 0; i < results.Count; i++)
            {
                var meshRes = results[i]?.MeshResult;
                if (meshRes?.AppliedSkeletonBones == null) continue;
                for (int b = 0; b < meshRes.AppliedSkeletonBones.Count; b++)
                {
                    var skelBone = meshRes.AppliedSkeletonBones[b];
                    if (skelBone == null || string.IsNullOrEmpty(skelBone.Path)) continue;
                    string normPath = FbxNameUtility.NormalizePath(skelBone.Path);
                    if (skeletonBonesByPath.TryGetValue(normPath, out var existingBone))
                    {
                        if (!existingBone.ApproximatelyMatches(skelBone))
                        {
                            for (int k = 0; k < results.Count; k++)
                            {
                                var owned = results[k]?.MeshResult?.OutputMesh;
                                if (owned && !AssetDatabase.Contains(owned)) Object.DestroyImmediate(owned);
                            }
                            previewRig?.Dispose();
                            return Task.FromResult<IRenderFilterNode>(new Node());
                        }
                    }
                    else
                    {
                        skeletonBonesByPath[normPath] = skelBone;
                    }
                }
            }

            previewRig?.FinalizeAuthoredSkeleton(skeletonBonesByPath);

            var states = new Dictionary<Renderer, RendererNodeState>();
            for (int i = 0; i < orderedOriginals.Count; i++)
            {
                var orig = orderedOriginals[i];
                var res = results[i];
                var prx = proxyByOriginal[orig];
                states[orig] = new RendererNodeState(res, prx, prx.bones, componentsByOriginal[orig]);
            }

            if (previewRig != null && skeletonBonesByPath.Count > 0)
            {
                for (int i = 0; i < validPairs.Count; i++)
                {
                    var orig = validPairs[i].Original;
                    if (states.ContainsKey(orig)) continue;
                    var prx = validPairs[i].Proxy;
                    var siblingState = new RendererNodeState(
                        prx,
                        prx.bones,
                        mappedPairBones[i].MappedBones,
                        mappedPairBones[i].MappedRootBone);
                    if (siblingState.HasChanges)
                    {
                        states[orig] = siblingState;
                    }
                }
            }

            if (states.Count == 0)
            {
                previewRig?.Dispose();
                return Task.FromResult<IRenderFilterNode>(new Node());
            }

            return Task.FromResult<IRenderFilterNode>(new Node(states, previewRig));
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

        /// <summary>
        /// Preview-owned hidden shadow bone hierarchy that mirrors the avatar's full scene ancestor chain
        /// (preserving inherited non-orthogonal shear), isolates distinct upstream-replaced proxy bone slots
        /// per <c>(origRelPath, upstreamBone, upstreamParentBone)</c> while preserving external upstream scale/shear,
        /// hosts preview-created bones without touching the live scene, and applies solved skeleton records on frame.
        /// </summary>
        private sealed class PreviewSkeletonRig : IDisposable
        {
            private readonly struct UpstreamSlotKey : IEquatable<UpstreamSlotKey>
            {
                public readonly string OrigRelPath;
                public readonly Transform UpstreamBone;
                public readonly UpstreamSlotBinding ParentBinding;
                public readonly Transform LiveOriginalBone;

                public UpstreamSlotKey(
                    string origRelPath,
                    Transform upstreamBone,
                    UpstreamSlotBinding parentBinding,
                    Transform liveOriginalBone)
                {
                    OrigRelPath = origRelPath ?? string.Empty;
                    UpstreamBone = upstreamBone;
                    ParentBinding = parentBinding;
                    LiveOriginalBone = liveOriginalBone;
                }

                public bool Equals(UpstreamSlotKey other) =>
                    string.Equals(OrigRelPath, other.OrigRelPath, StringComparison.Ordinal) &&
                    UpstreamBone == other.UpstreamBone &&
                    ReferenceEquals(ParentBinding, other.ParentBinding) &&
                    LiveOriginalBone == other.LiveOriginalBone;

                public override bool Equals(object obj) => obj is UpstreamSlotKey other && Equals(other);

                public override int GetHashCode()
                {
                    unchecked
                    {
                        int h = OrigRelPath != null ? StringComparer.Ordinal.GetHashCode(OrigRelPath) : 0;
                        h = (h * 397) ^ (UpstreamBone != null ? UpstreamBone.GetHashCode() : 0);
                        h = (h * 397) ^ (ParentBinding != null ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(ParentBinding) : 0);
                        h = (h * 397) ^ (LiveOriginalBone != null ? LiveOriginalBone.GetHashCode() : 0);
                        return h;
                    }
                }
            }

            private enum UpstreamAttachmentMode
            {
                MirroredChain,
                DirectAuthoredUnderShadowParent,
                RelativeTrsUnderShadowParent
            }

            private sealed class UpstreamSlotBinding
            {
                public readonly UpstreamSlotKey Key;
                public readonly string OrigRelPath;
                public readonly string ParentPath;
                public readonly Transform UpstreamBone;
                public readonly Transform UpstreamParentBone;
                public readonly Transform LiveOriginalBone;
                public readonly UpstreamSlotBinding RendererUpstreamParentBinding;
                public readonly Transform DeltaOuter;
                public readonly Transform DeltaInner;
                public readonly Transform MirrorParentOfLeaf;
                public readonly Transform ShadowNode;
                public readonly List<(Transform Live, Transform Mirror)> MirroredChain;
                public UpstreamAttachmentMode Mode = UpstreamAttachmentMode.MirroredChain;
                public bool HasAuthoredAncestor;

                public UpstreamSlotBinding(
                    UpstreamSlotKey key,
                    string parentPath,
                    Transform upstreamParentBone,
                    DeltaChainNodes nodes)
                {
                    Key = key;
                    OrigRelPath = key.OrigRelPath;
                    ParentPath = parentPath;
                    UpstreamBone = key.UpstreamBone;
                    UpstreamParentBone = upstreamParentBone;
                    LiveOriginalBone = key.LiveOriginalBone;
                    RendererUpstreamParentBinding = key.ParentBinding;
                    DeltaOuter = nodes.DeltaOuter;
                    DeltaInner = nodes.DeltaInner;
                    MirrorParentOfLeaf = nodes.MirrorParentOfLeaf;
                    ShadowNode = nodes.ShadowNode;
                    MirroredChain = nodes.MirroredChain;
                }
            }

            private readonly struct DeltaChainNodes
            {
                public readonly Transform DeltaOuter;
                public readonly Transform DeltaInner;
                public readonly Transform MirrorParentOfLeaf;
                public readonly Transform ShadowNode;
                public readonly List<(Transform Live, Transform Mirror)> MirroredChain;

                public DeltaChainNodes(
                    Transform deltaOuter,
                    Transform deltaInner,
                    Transform mirrorParentOfLeaf,
                    Transform shadowNode,
                    List<(Transform Live, Transform Mirror)> mirroredChain)
                {
                    DeltaOuter = deltaOuter;
                    DeltaInner = deltaInner;
                    MirrorParentOfLeaf = mirrorParentOfLeaf;
                    ShadowNode = shadowNode;
                    MirroredChain = mirroredChain;
                }
            }

            private readonly Transform m_LiveRoot;
            private readonly GameObject m_TopContainerGo;
            private readonly Transform m_ShadowRoot;
            private readonly List<(Transform Live, Transform Shadow)> m_AncestorChain = new();
            private readonly Dictionary<Transform, Transform> m_ShadowByLive = new();
            private readonly Dictionary<Transform, Transform> m_LiveByShadow = new();
            private readonly Dictionary<Transform, Transform> m_FirstShadowByUpstream = new();
            private readonly Dictionary<UpstreamSlotKey, UpstreamSlotBinding> m_UpstreamByKey = new();
            private readonly List<UpstreamSlotBinding> m_OrderedUpstreamBindings = new();
            private readonly Dictionary<string, Transform> m_ShadowByPath = new(StringComparer.Ordinal);
            private readonly Dictionary<string, Transform> m_LiveByPath = new(StringComparer.Ordinal);
            private readonly Dictionary<string, GeneratedSkeletonBoneRecord> m_AuthoredByPath = new(StringComparer.Ordinal);
            private readonly Dictionary<Transform, GeneratedSkeletonBoneRecord> m_AuthoredByShadow = new();
            private readonly List<(Transform Shadow, Transform Live)> m_OrderedShadowNodes = new();

            public Transform ShadowRoot => m_ShadowRoot;

            private PreviewSkeletonRig(
                Transform liveRoot,
                GameObject topContainerGo,
                Transform shadowRoot,
                List<(Transform Live, Transform Shadow)> ancestorChain)
            {
                m_LiveRoot = liveRoot;
                m_TopContainerGo = topContainerGo;
                m_ShadowRoot = shadowRoot;
                m_AncestorChain = ancestorChain;
                m_ShadowByLive[liveRoot] = shadowRoot;
                m_LiveByShadow[shadowRoot] = liveRoot;
                m_ShadowByPath[string.Empty] = shadowRoot;
                m_LiveByPath[string.Empty] = liveRoot;
            }

            public static PreviewSkeletonRig CreateInitial(Transform liveRoot, Scene targetScene)
            {
                var topGo = new GameObject($"__BlendSharePreviewRig_{liveRoot.name}");
                if (targetScene.IsValid())
                {
                    SceneManager.MoveGameObjectToScene(topGo, targetScene);
                }

                topGo.hideFlags = HideFlags.HideAndDontSave;

                var liveAncestors = new List<Transform>();
                for (var curr = liveRoot; curr != null; curr = curr.parent)
                {
                    liveAncestors.Add(curr);
                }
                liveAncestors.Reverse();

                Transform parentShadow = topGo.transform;
                Transform shadowRoot = null;
                var ancestorChain = new List<(Transform Live, Transform Shadow)>(liveAncestors.Count);

                for (int i = 0; i < liveAncestors.Count; i++)
                {
                    var liveAnc = liveAncestors[i];
                    var go = new GameObject(liveAnc.name);
                    go.transform.SetParent(parentShadow, false);
                    go.hideFlags = HideFlags.HideAndDontSave;
                    go.transform.localPosition = liveAnc.localPosition;
                    go.transform.localRotation = liveAnc.localRotation;
                    go.transform.localScale = liveAnc.localScale;

                    ancestorChain.Add((liveAnc, go.transform));
                    parentShadow = go.transform;
                    if (liveAnc == liveRoot)
                    {
                        shadowRoot = go.transform;
                    }
                }

                var rig = new PreviewSkeletonRig(liveRoot, topGo, shadowRoot, ancestorChain);

                // Mirror the live hierarchy 1-to-1 by Transform identity so distinct sibling children sharing a name
                // (e.g. two live children both named "P/Q") receive distinct shadow transforms and sync independently,
                // while path-based lookups follow Unity's Transform.Find first-child convention.
                void MirrorLiveChildren(Transform liveParent, Transform currentShadowParent, string parentRelPath)
                {
                    int childCount = liveParent.childCount;
                    for (int c = 0; c < childCount; c++)
                    {
                        var liveChild = liveParent.GetChild(c);
                        if (!liveChild) continue;
                        string childName = FbxNameUtility.NormalizePath(liveChild.name);
                        string childRelPath = string.IsNullOrEmpty(parentRelPath)
                            ? childName
                            : parentRelPath + "/" + childName;

                        var childGo = new GameObject(liveChild.name)
                        {
                            hideFlags = HideFlags.HideAndDontSave
                        };
                        var shadowChild = childGo.transform;
                        shadowChild.SetParent(currentShadowParent, false);
                        shadowChild.localPosition = liveChild.localPosition;
                        shadowChild.localRotation = liveChild.localRotation;
                        shadowChild.localScale = liveChild.localScale;

                        rig.m_ShadowByLive[liveChild] = shadowChild;
                        rig.m_LiveByShadow[shadowChild] = liveChild;
                        if (!string.IsNullOrEmpty(childRelPath) && !rig.m_LiveByPath.ContainsKey(childRelPath))
                        {
                            rig.m_LiveByPath[childRelPath] = liveChild;
                            rig.m_ShadowByPath[childRelPath] = shadowChild;
                        }

                        MirrorLiveChildren(liveChild, shadowChild, childRelPath);
                    }
                }

                MirrorLiveChildren(liveRoot, shadowRoot, string.Empty);
                rig.RebuildOrderedSyncPaths();
                return rig;
            }

            public Transform RemapAuthoringBone(Transform authoringBone)
            {
                if (!authoringBone) return null;
                if (m_ShadowByLive.TryGetValue(authoringBone, out var shadow)) return shadow;
                if (m_FirstShadowByUpstream.TryGetValue(authoringBone, out var upstreamShadow)) return upstreamShadow;
                if (m_LiveRoot && (authoringBone == m_LiveRoot || authoringBone.IsChildOf(m_LiveRoot)))
                {
                    string relPath = FbxNameUtility.NormalizePath(AnimationUtility.CalculateTransformPath(authoringBone, m_LiveRoot));
                    return EnsurePath(relPath);
                }

                return authoringBone;
            }

            public void RegisterAndMapRendererBones(
                Transform[] originalBones,
                Transform[] proxyBones,
                Transform originalRootBone,
                Transform proxyRootBone,
                out Transform[] mappedBones,
                out string[] mappedSlotPaths,
                out Transform mappedRootBone)
            {
                var rendererExternalsByPath = new Dictionary<string, List<(Transform ProxyBone, Transform OriginalBone)>>(StringComparer.Ordinal);
                if (proxyBones != null && originalBones != null)
                {
                    int n = Math.Min(originalBones.Length, proxyBones.Length);
                    for (int i = 0; i < n; i++)
                    {
                        RecordRendererUpstreamCorrespondence(originalBones[i], proxyBones[i], rendererExternalsByPath);
                    }
                }

                RecordRendererUpstreamCorrespondence(originalRootBone, proxyRootBone, rendererExternalsByPath);

                // Build isolated per-renderer upstream bindings in strict parent-before-child depth order for EXTERNAL
                // replacement bones (e.g. [Q, Q] -> [E1, E2] and G -> G/P -> G/P/C). Live proxy slots inside `m_LiveRoot`
                // always map directly to their canonical live shadow node in `MapSingleSlot` so unweighted external
                // descendants or sibling slots can never alter a live parent slot's deformation.
                bool addedAny = false;
                var rendererBindingsByPath = new Dictionary<string, List<UpstreamSlotBinding>>(StringComparer.Ordinal);
                if (rendererExternalsByPath.Count > 0)
                {
                    var requiredChainPaths = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var extPath in rendererExternalsByPath.Keys)
                    {
                        var prefixes = new List<string>();
                        string curr = extPath;
                        while (!string.IsNullOrEmpty(curr))
                        {
                            prefixes.Add(curr);
                            int slash = curr.LastIndexOf('/');
                            curr = slash > 0 ? curr.Substring(0, slash) : string.Empty;
                        }
                        prefixes.Reverse();

                        bool underExternal = false;
                        for (int p = 0; p < prefixes.Count; p++)
                        {
                            if (rendererExternalsByPath.ContainsKey(prefixes[p]))
                            {
                                underExternal = true;
                            }
                            if (underExternal)
                            {
                                requiredChainPaths.Add(prefixes[p]);
                            }
                        }
                    }

                    var orderedChainPaths = requiredChainPaths
                        .OrderBy(p => p.Count(c => c == '/'))
                        .ThenBy(p => p, StringComparer.Ordinal)
                        .ToArray();

                    for (int p = 0; p < orderedChainPaths.Length; p++)
                    {
                        string path = orderedChainPaths[p];
                        EnsurePath(path);
                        int slash = path.LastIndexOf('/');
                        string parentPath = slash > 0 ? path.Substring(0, slash) : string.Empty;

                        rendererBindingsByPath.TryGetValue(parentPath, out var parentBindings);
                        var sourceEntries = new List<(Transform ProxyBone, Transform OriginalBone)>();
                        if (rendererExternalsByPath.TryGetValue(path, out var extList) && extList.Count > 0)
                        {
                            sourceEntries.AddRange(extList);
                        }
                        else if (parentBindings != null && parentBindings.Count > 0)
                        {
                            string leafName = slash >= 0 ? path.Substring(slash + 1) : path;
                            for (int pb = 0; pb < parentBindings.Count; pb++)
                            {
                                var liveOrigPar = parentBindings[pb].LiveOriginalBone;
                                if (!liveOrigPar) continue;
                                int childCount = liveOrigPar.childCount;
                                for (int c = 0; c < childCount; c++)
                                {
                                    var ch = liveOrigPar.GetChild(c);
                                    if (ch && string.Equals(FbxNameUtility.NormalizePath(ch.name), leafName, StringComparison.Ordinal))
                                    {
                                        var entry = (ch, ch);
                                        if (!sourceEntries.Contains(entry))
                                        {
                                            sourceEntries.Add(entry);
                                        }
                                    }
                                }
                            }

                            if (sourceEntries.Count == 0 &&
                                m_LiveByPath.TryGetValue(path, out var liveBone) &&
                                liveBone != null)
                            {
                                sourceEntries.Add((liveBone, liveBone));
                            }
                        }
                        if (sourceEntries.Count == 0) continue;

                        var createdAtPath = new List<UpstreamSlotBinding>(sourceEntries.Count);

                        for (int s = 0; s < sourceEntries.Count; s++)
                        {
                            var (sourceBone, origBoneAtSlot) = sourceEntries[s];
                            if (sourceBone == null) continue;

                            Transform expectedLiveOrigParent = origBoneAtSlot != null ? origBoneAtSlot.parent : null;
                            UpstreamSlotBinding chosenParentBinding = null;
                            if (parentBindings != null && parentBindings.Count > 0)
                            {
                                // Strictly require `cand.LiveOriginalBone == expectedLiveOrigParent` whenever known so an external
                                // child under live `P2` (e.g. `[P1, P2, P2/C] -> [E1, P2, EC]`) can NEVER fall back onto sibling `P1 -> E1`.
                                for (var anc = sourceBone.parent; anc != null && chosenParentBinding == null; anc = anc.parent)
                                {
                                    for (int pb = 0; pb < parentBindings.Count; pb++)
                                    {
                                        var cand = parentBindings[pb];
                                        if ((expectedLiveOrigParent == null || cand.LiveOriginalBone == expectedLiveOrigParent) &&
                                            cand.UpstreamBone == anc)
                                        {
                                            chosenParentBinding = cand;
                                            break;
                                        }
                                    }
                                }

                                if (chosenParentBinding == null)
                                {
                                    for (int pb = 0; pb < parentBindings.Count; pb++)
                                    {
                                        var cand = parentBindings[pb];
                                        if (expectedLiveOrigParent == null || cand.LiveOriginalBone == expectedLiveOrigParent)
                                        {
                                            chosenParentBinding = cand;
                                            break;
                                        }
                                    }
                                }
                            }

                            Transform upstreamParentBone = chosenParentBinding?.UpstreamBone;
                            if (upstreamParentBone == null && !string.IsNullOrEmpty(parentPath) &&
                                rendererExternalsByPath.TryGetValue(parentPath, out var parentExtList) &&
                                parentExtList.Count > 0)
                            {
                                for (var anc = sourceBone.parent; anc != null && upstreamParentBone == null; anc = anc.parent)
                                {
                                    for (int pe = 0; pe < parentExtList.Count; pe++)
                                    {
                                        if ((expectedLiveOrigParent == null || parentExtList[pe].OriginalBone == expectedLiveOrigParent) &&
                                            parentExtList[pe].ProxyBone == anc)
                                        {
                                            upstreamParentBone = anc;
                                            break;
                                        }
                                    }
                                }

                                if (upstreamParentBone == null)
                                {
                                    for (int pe = 0; pe < parentExtList.Count; pe++)
                                    {
                                        if (expectedLiveOrigParent == null || parentExtList[pe].OriginalBone == expectedLiveOrigParent)
                                        {
                                            upstreamParentBone = parentExtList[pe].ProxyBone;
                                            break;
                                        }
                                    }
                                }
                            }

                            var key = new UpstreamSlotKey(path, sourceBone, chosenParentBinding, origBoneAtSlot);
                            if (!m_UpstreamByKey.TryGetValue(key, out var binding))
                            {
                                binding = CreateUpstreamSlotBinding(key, parentPath, upstreamParentBone);
                                m_UpstreamByKey[key] = binding;
                                addedAny = true;
                            }

                            createdAtPath.Add(binding);
                            if (rendererExternalsByPath.ContainsKey(path) && !m_FirstShadowByUpstream.ContainsKey(sourceBone))
                            {
                                m_FirstShadowByUpstream[sourceBone] = binding.ShadowNode;
                            }
                        }

                        if (createdAtPath.Count > 0)
                        {
                            rendererBindingsByPath[path] = createdAtPath;
                        }
                    }
                }

                if (proxyBones == null)
                {
                    mappedBones = null;
                    mappedSlotPaths = null;
                }
                else
                {
                    mappedBones = new Transform[proxyBones.Length];
                    mappedSlotPaths = new string[proxyBones.Length];
                    for (int i = 0; i < proxyBones.Length; i++)
                    {
                        var origBone = originalBones != null && i < originalBones.Length ? originalBones[i] : null;
                        mappedBones[i] = MapSingleSlot(origBone, proxyBones[i], rendererBindingsByPath, out string slotPath, ref addedAny);
                        mappedSlotPaths[i] = slotPath;
                    }
                }

                mappedRootBone = MapSingleSlot(originalRootBone, proxyRootBone, rendererBindingsByPath, out _, ref addedAny);
                if (addedAny)
                {
                    RebuildOrderedSyncPaths();
                }
            }

            private void RecordRendererUpstreamCorrespondence(
                Transform originalBone,
                Transform proxyBone,
                Dictionary<string, List<(Transform ProxyBone, Transform OriginalBone)>> rendererExternalsByPath)
            {
                if (!originalBone || !proxyBone || !m_LiveRoot) return;
                if (proxyBone == m_LiveRoot || proxyBone.IsChildOf(m_LiveRoot)) return;
                if (originalBone == m_LiveRoot || originalBone.IsChildOf(m_LiveRoot))
                {
                    string origRelPath = FbxNameUtility.NormalizePath(AnimationUtility.CalculateTransformPath(originalBone, m_LiveRoot));
                    if (!string.IsNullOrEmpty(origRelPath))
                    {
                        if (!rendererExternalsByPath.TryGetValue(origRelPath, out var list))
                        {
                            list = new List<(Transform ProxyBone, Transform OriginalBone)>(1);
                            rendererExternalsByPath.Add(origRelPath, list);
                        }

                        var pair = (proxyBone, originalBone);
                        if (!list.Contains(pair))
                        {
                            list.Add(pair);
                        }
                    }
                }
            }

            private Transform MapSingleSlot(
                Transform originalBone,
                Transform proxyBone,
                Dictionary<string, List<UpstreamSlotBinding>> rendererBindingsByPath,
                out string resolvedSlotPath,
                ref bool addedAny)
            {
                resolvedSlotPath = string.Empty;
                if (!proxyBone)
                {
                    if (originalBone && m_LiveRoot && (originalBone == m_LiveRoot || originalBone.IsChildOf(m_LiveRoot)))
                    {
                        resolvedSlotPath = FbxNameUtility.NormalizePath(AnimationUtility.CalculateTransformPath(originalBone, m_LiveRoot));
                    }

                    return null;
                }

                if (m_LiveRoot && (proxyBone == m_LiveRoot || proxyBone.IsChildOf(m_LiveRoot)))
                {
                    string relPath = FbxNameUtility.NormalizePath(AnimationUtility.CalculateTransformPath(proxyBone, m_LiveRoot));
                    resolvedSlotPath = relPath;
                    if (m_ShadowByLive.TryGetValue(proxyBone, out var liveShadow))
                    {
                        return liveShadow;
                    }

                    addedAny = true;
                    return EnsurePath(relPath);
                }

                if (originalBone && m_LiveRoot && (originalBone == m_LiveRoot || originalBone.IsChildOf(m_LiveRoot)))
                {
                    string origRelPath = FbxNameUtility.NormalizePath(AnimationUtility.CalculateTransformPath(originalBone, m_LiveRoot));
                    resolvedSlotPath = origRelPath;
                    if (!string.IsNullOrEmpty(origRelPath) &&
                        rendererBindingsByPath.TryGetValue(origRelPath, out var bindingsAtPath))
                    {
                        for (int i = 0; i < bindingsAtPath.Count; i++)
                        {
                            if (bindingsAtPath[i].UpstreamBone == proxyBone &&
                                bindingsAtPath[i].LiveOriginalBone == originalBone)
                            {
                                return bindingsAtPath[i].ShadowNode;
                            }
                        }

                        for (int i = 0; i < bindingsAtPath.Count; i++)
                        {
                            if (bindingsAtPath[i].UpstreamBone == proxyBone)
                            {
                                return bindingsAtPath[i].ShadowNode;
                            }
                        }
                    }

                    var key = new UpstreamSlotKey(origRelPath, proxyBone, null, originalBone);
                    if (!m_UpstreamByKey.TryGetValue(key, out var rootBinding))
                    {
                        rootBinding = CreateUpstreamSlotBinding(key, string.Empty, null);
                        m_UpstreamByKey[key] = rootBinding;
                        addedAny = true;
                    }
                    if (!m_FirstShadowByUpstream.ContainsKey(proxyBone))
                    {
                        m_FirstShadowByUpstream[proxyBone] = rootBinding.ShadowNode;
                    }
                    return rootBinding.ShadowNode;
                }

                if (m_FirstShadowByUpstream.TryGetValue(proxyBone, out var existingUpstreamShadow))
                {
                    return existingUpstreamShadow;
                }

                return proxyBone;
            }

            private UpstreamSlotBinding CreateUpstreamSlotBinding(
                UpstreamSlotKey key,
                string parentPath,
                Transform upstreamParentBone)
            {
                var deltaOuterGo = new GameObject($"__UpstreamDeltaOuter_{key.UpstreamBone.name}")
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                deltaOuterGo.transform.SetParent(m_TopContainerGo.transform, false);

                var deltaInnerGo = new GameObject($"__UpstreamDeltaInner_{key.UpstreamBone.name}")
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                deltaInnerGo.transform.SetParent(deltaOuterGo.transform, false);

                var upAncestors = new List<Transform>();
                for (var curr = key.UpstreamBone; curr != null; curr = curr.parent)
                {
                    upAncestors.Add(curr);
                }
                upAncestors.Reverse();

                var mirroredChain = new List<(Transform Live, Transform Mirror)>(upAncestors.Count);
                Transform parentMirror = deltaInnerGo.transform;
                Transform mirrorParentOfLeaf = deltaInnerGo.transform;
                Transform shadowLeaf = null;

                for (int i = 0; i < upAncestors.Count; i++)
                {
                    var upAnc = upAncestors[i];
                    bool isLeaf = i == upAncestors.Count - 1;
                    if (isLeaf)
                    {
                        mirrorParentOfLeaf = parentMirror;
                    }

                    var mirrorGo = new GameObject(upAnc.name)
                    {
                        hideFlags = HideFlags.HideAndDontSave
                    };
                    mirrorGo.transform.SetParent(parentMirror, false);
                    mirrorGo.transform.localPosition = upAnc.localPosition;
                    mirrorGo.transform.localRotation = upAnc.localRotation;
                    mirrorGo.transform.localScale = upAnc.localScale;

                    mirroredChain.Add((upAnc, mirrorGo.transform));
                    parentMirror = mirrorGo.transform;
                    if (isLeaf)
                    {
                        shadowLeaf = mirrorGo.transform;
                    }
                }

                return new UpstreamSlotBinding(
                    key,
                    parentPath,
                    upstreamParentBone,
                    new DeltaChainNodes(
                        deltaOuterGo.transform,
                        deltaInnerGo.transform,
                        mirrorParentOfLeaf,
                        shadowLeaf,
                        mirroredChain));
            }

            public void FinalizeAuthoredSkeleton(Dictionary<string, GeneratedSkeletonBoneRecord> skeletonBonesByPath)
            {
                m_AuthoredByPath.Clear();
                m_AuthoredByShadow.Clear();

                // Register any new bones materialized inside ShadowRoot (when component CreateMissingBones = true)
                // and mark them HideAndDontSave BEFORE binding authored records, never fabricating unmaterialized bones.
                if (m_ShadowRoot != null)
                {
                    foreach (var child in m_ShadowRoot.GetComponentsInChildren<Transform>(true))
                    {
                        if (!child) continue;
                        child.gameObject.hideFlags = HideFlags.HideAndDontSave;
                        if (child == m_ShadowRoot) continue;
                        string relPath = FbxNameUtility.NormalizePath(AnimationUtility.CalculateTransformPath(child, m_ShadowRoot));
                        if (!string.IsNullOrEmpty(relPath) && !m_ShadowByPath.ContainsKey(relPath))
                        {
                            m_ShadowByPath[relPath] = child;
                        }
                    }
                }

                if (skeletonBonesByPath != null)
                {
                    foreach (var kv in skeletonBonesByPath)
                    {
                        if (string.IsNullOrEmpty(kv.Key) || kv.Value == null) continue;
                        Transform targetShadow = null;
                        if (!m_ShadowByPath.TryGetValue(kv.Key, out targetShadow) && m_ShadowRoot != null)
                        {
                            targetShadow = m_ShadowRoot.Find(kv.Key);
                        }

                        if (targetShadow == null)
                        {
                            continue;
                        }

                        m_AuthoredByPath[kv.Key] = kv.Value;
                        m_AuthoredByShadow[targetShadow] = kv.Value;
                    }
                }

                RebuildOrderedSyncPaths();

                // Configure attachment mode for each upstream binding based on whether OrigRelPath itself or a strict ancestor
                // was authored by BlendShare, preserving external scale/shear via the mirrored upstream chain whenever needed.
                for (int i = 0; i < m_OrderedUpstreamBindings.Count; i++)
                {
                    var binding = m_OrderedUpstreamBindings[i];
                    if (IsBindingDirectlyAuthored(binding))
                    {
                        binding.Mode = UpstreamAttachmentMode.DirectAuthoredUnderShadowParent;
                        binding.HasAuthoredAncestor = true;
                        var shadowParent = GetShadowParent(binding);
                        if (binding.ShadowNode.parent != shadowParent)
                        {
                            binding.ShadowNode.SetParent(shadowParent, false);
                        }
                        continue;
                    }

                    bool hasAuthoredAncestor = HasAuthoredAncestorForBinding(binding);
                    binding.HasAuthoredAncestor = hasAuthoredAncestor;
                    if (!hasAuthoredAncestor)
                    {
                        binding.Mode = UpstreamAttachmentMode.MirroredChain;
                        if (binding.ShadowNode.parent != binding.MirrorParentOfLeaf)
                        {
                            binding.ShadowNode.SetParent(binding.MirrorParentOfLeaf, false);
                        }
                        continue;
                    }

                    Matrix4x4 preParentW2L = GetPreParentWorldToLocal(binding);
                    Matrix4x4 relMat = preParentW2L * binding.UpstreamBone.localToWorldMatrix;
                    if (SkeletonTransformMath.TryDecomposeUnityLocalTransform(relMat, out _, out _, out _, out _))
                    {
                        binding.Mode = UpstreamAttachmentMode.RelativeTrsUnderShadowParent;
                        var shadowParent = GetShadowParent(binding);
                        if (binding.ShadowNode.parent != shadowParent)
                        {
                            binding.ShadowNode.SetParent(shadowParent, false);
                        }
                    }
                    else
                    {
                        binding.Mode = UpstreamAttachmentMode.MirroredChain;
                        if (binding.ShadowNode.parent != binding.MirrorParentOfLeaf)
                        {
                            binding.ShadowNode.SetParent(binding.MirrorParentOfLeaf, false);
                        }
                    }
                }

                Sync();
            }

            private bool IsBindingDirectlyAuthored(UpstreamSlotBinding binding)
            {
                if (string.IsNullOrEmpty(binding.OrigRelPath) || !m_AuthoredByPath.ContainsKey(binding.OrigRelPath))
                {
                    return false;
                }

                if (binding.LiveOriginalBone != null &&
                    m_ShadowByLive.TryGetValue(binding.LiveOriginalBone, out var origShadow) &&
                    m_ShadowByPath.TryGetValue(binding.OrigRelPath, out var canonicalShadow))
                {
                    return origShadow == canonicalShadow;
                }

                return true;
            }

            private bool HasAuthoredAncestorForBinding(UpstreamSlotBinding binding)
            {
                if (binding.RendererUpstreamParentBinding != null)
                {
                    return binding.RendererUpstreamParentBinding.HasAuthoredAncestor;
                }

                for (var s = GetShadowParent(binding); s != null && s != m_ShadowRoot; s = s.parent)
                {
                    if (m_AuthoredByShadow.ContainsKey(s))
                    {
                        return true;
                    }
                }

                return false;
            }

            private Transform GetShadowParent(UpstreamSlotBinding binding)
            {
                if (binding.RendererUpstreamParentBinding?.ShadowNode != null)
                {
                    return binding.RendererUpstreamParentBinding.ShadowNode;
                }

                if (binding.LiveOriginalBone != null &&
                    binding.LiveOriginalBone.parent != null &&
                    m_ShadowByLive.TryGetValue(binding.LiveOriginalBone.parent, out var exactLiveParentShadow) &&
                    exactLiveParentShadow != null)
                {
                    return exactLiveParentShadow;
                }

                return EnsurePath(binding.ParentPath);
            }

            private Matrix4x4 GetPreParentWorldToLocal(UpstreamSlotBinding binding)
            {
                if (binding.UpstreamParentBone != null)
                {
                    return binding.UpstreamParentBone.worldToLocalMatrix;
                }

                if (binding.LiveOriginalBone != null && binding.LiveOriginalBone.parent != null)
                {
                    return binding.LiveOriginalBone.parent.worldToLocalMatrix;
                }

                if (m_LiveByPath.TryGetValue(binding.ParentPath, out var liveParent) && liveParent != null)
                {
                    return liveParent.worldToLocalMatrix;
                }

                return m_LiveRoot != null ? m_LiveRoot.worldToLocalMatrix : Matrix4x4.identity;
            }

            private Transform EnsurePath(string normalizedPath)
            {
                if (string.IsNullOrEmpty(normalizedPath)) return m_ShadowRoot;
                if (m_ShadowByPath.TryGetValue(normalizedPath, out var existing) && existing != null) return existing;

                int slash = normalizedPath.LastIndexOf('/');
                string parentPath = slash > 0 ? normalizedPath.Substring(0, slash) : string.Empty;
                string leafName = slash >= 0 ? normalizedPath.Substring(slash + 1) : normalizedPath;
                var parentShadow = EnsurePath(parentPath);

                var existingChild = parentShadow.Find(leafName);
                Transform childShadow;
                if (existingChild != null)
                {
                    childShadow = existingChild;
                    childShadow.gameObject.hideFlags = HideFlags.HideAndDontSave;
                }
                else
                {
                    var childGo = new GameObject(leafName)
                    {
                        hideFlags = HideFlags.HideAndDontSave
                    };
                    childShadow = childGo.transform;
                    childShadow.SetParent(parentShadow, false);
                }

                m_ShadowByPath[normalizedPath] = childShadow;

                if (!m_LiveByPath.TryGetValue(normalizedPath, out var liveNode) && m_LiveRoot != null)
                {
                    liveNode = m_LiveRoot.Find(normalizedPath);
                    if (liveNode != null)
                    {
                        m_LiveByPath[normalizedPath] = liveNode;
                    }
                }

                if (liveNode != null)
                {
                    m_ShadowByLive[liveNode] = childShadow;
                    m_LiveByShadow[childShadow] = liveNode;
                    childShadow.localPosition = liveNode.localPosition;
                    childShadow.localRotation = liveNode.localRotation;
                    childShadow.localScale = liveNode.localScale;
                }

                return childShadow;
            }

            private void RebuildOrderedSyncPaths()
            {
                m_OrderedShadowNodes.Clear();
                if (m_ShadowRoot != null)
                {
                    void CollectSubtree(Transform parent)
                    {
                        int count = parent.childCount;
                        for (int c = 0; c < count; c++)
                        {
                            var child = parent.GetChild(c);
                            if (!child) continue;
                            m_LiveByShadow.TryGetValue(child, out var liveNode);
                            m_OrderedShadowNodes.Add((child, liveNode));
                            CollectSubtree(child);
                        }
                    }

                    CollectSubtree(m_ShadowRoot);
                }

                m_OrderedUpstreamBindings.Clear();
                m_OrderedUpstreamBindings.AddRange(
                    m_UpstreamByKey.Values
                        .OrderBy(b => b.OrigRelPath.Count(c => c == '/'))
                        .ThenBy(b => b.OrigRelPath, StringComparer.Ordinal));
            }

            public void Sync()
            {
                if (!m_ShadowRoot || !m_LiveRoot) return;

                // 1. Sync full ancestor chain from top scene ancestor down to m_ShadowRoot to preserve exact world shear.
                for (int i = 0; i < m_AncestorChain.Count; i++)
                {
                    var (liveAnc, shadowAnc) = m_AncestorChain[i];
                    if (!liveAnc || !shadowAnc) continue;
                    shadowAnc.localPosition = liveAnc.localPosition;
                    shadowAnc.localRotation = liveAnc.localRotation;
                    shadowAnc.localScale = liveAnc.localScale;
                }

                // 2. Sync shadow descendants in exact topological parent-before-child tree order by Transform identity.
                for (int i = 0; i < m_OrderedShadowNodes.Count; i++)
                {
                    var (shadowNode, liveNode) = m_OrderedShadowNodes[i];
                    if (!shadowNode) continue;

                    if (m_AuthoredByShadow.TryGetValue(shadowNode, out var authoredRecord) && authoredRecord != null)
                    {
                        authoredRecord.ApplyLocalTransform(shadowNode);
                        continue;
                    }

                    if (liveNode != null)
                    {
                        shadowNode.localPosition = liveNode.localPosition;
                        shadowNode.localRotation = liveNode.localRotation;
                        shadowNode.localScale = liveNode.localScale;
                    }
                }

                // 3. Sync isolated upstream replacement bindings in parent-before-child depth order.
                for (int i = 0; i < m_OrderedUpstreamBindings.Count; i++)
                {
                    var binding = m_OrderedUpstreamBindings[i];
                    if (!binding.ShadowNode || !binding.UpstreamBone) continue;

                    if (binding.Mode == UpstreamAttachmentMode.DirectAuthoredUnderShadowParent &&
                        m_AuthoredByPath.TryGetValue(binding.OrigRelPath, out var authoredRecord) &&
                        authoredRecord != null)
                    {
                        var shadowParent = GetShadowParent(binding);
                        if (binding.ShadowNode.parent != shadowParent)
                        {
                            binding.ShadowNode.SetParent(shadowParent, false);
                        }
                        authoredRecord.ApplyLocalTransform(binding.ShadowNode);
                        continue;
                    }

                    if (binding.Mode == UpstreamAttachmentMode.RelativeTrsUnderShadowParent)
                    {
                        Matrix4x4 preParentW2L = GetPreParentWorldToLocal(binding);
                        Matrix4x4 relMat = preParentW2L * binding.UpstreamBone.localToWorldMatrix;
                        if (SkeletonTransformMath.TryDecomposeUnityLocalTransform(
                                relMat,
                                out var uPos,
                                out var uRot,
                                out var uScale,
                                out _))
                        {
                            var shadowParent = GetShadowParent(binding);
                            if (binding.ShadowNode.parent != shadowParent)
                            {
                                binding.ShadowNode.SetParent(shadowParent, false);
                            }
                            binding.ShadowNode.localPosition = uPos;
                            binding.ShadowNode.localRotation = uRot;
                            binding.ShadowNode.localScale = uScale;
                            continue;
                        }

                        // External hierarchy dynamically introduced shear: switch to mirrored upstream chain.
                        binding.Mode = UpstreamAttachmentMode.MirroredChain;
                    }

                    if (binding.ShadowNode.parent != binding.MirrorParentOfLeaf)
                    {
                        binding.ShadowNode.SetParent(binding.MirrorParentOfLeaf, false);
                    }

                    for (int m = 0; m < binding.MirroredChain.Count; m++)
                    {
                        var (liveAnc, mirrorAnc) = binding.MirroredChain[m];
                        if (!liveAnc || !mirrorAnc) continue;
                        mirrorAnc.localPosition = liveAnc.localPosition;
                        mirrorAnc.localRotation = liveAnc.localRotation;
                        mirrorAnc.localScale = liveAnc.localScale;
                    }

                    if (!binding.HasAuthoredAncestor)
                    {
                        binding.DeltaOuter.localPosition = Vector3.zero;
                        binding.DeltaOuter.localRotation = Quaternion.identity;
                        binding.DeltaOuter.localScale = Vector3.one;
                        binding.DeltaInner.localPosition = Vector3.zero;
                        binding.DeltaInner.localRotation = Quaternion.identity;
                        binding.DeltaInner.localScale = Vector3.one;
                    }
                    else
                    {
                        var shadowParent = GetShadowParent(binding);
                        Matrix4x4 deltaWp = shadowParent.localToWorldMatrix * GetPreParentWorldToLocal(binding);
                        ApplyAffineMatrixToTwoNodePair(deltaWp, binding.DeltaOuter, binding.DeltaInner);
                    }
                }
            }

            private static void ApplyAffineMatrixToTwoNodePair(Matrix4x4 m, Transform outer, Transform inner)
            {
                if (SkeletonTransformMath.TryDecomposeUnityLocalTransform(
                        m,
                        out var pos,
                        out var rot,
                        out var scale,
                        out _))
                {
                    outer.localPosition = pos;
                    outer.localRotation = rot;
                    outer.localScale = scale;
                    inner.localPosition = Vector3.zero;
                    inner.localRotation = Quaternion.identity;
                    inner.localScale = Vector3.one;
                    return;
                }

                // Exact two-transform SVD decomposition A = U * diag(sigma) * V^T for arbitrary invertible sheared 3x3 blocks.
                Decompose3x3Svd(m, out var qU, out var sigma, out var qVt);
                outer.localPosition = m.GetColumn(3);
                outer.localRotation = qU;
                outer.localScale = sigma;
                inner.localPosition = Vector3.zero;
                inner.localRotation = qVt;
                inner.localScale = Vector3.one;
            }

            private static void Decompose3x3Svd(
                Matrix4x4 m,
                out Quaternion qU,
                out Vector3 sigma,
                out Quaternion qVt)
            {
                double[,] a =
                {
                    { m.m00, m.m01, m.m02 },
                    { m.m10, m.m11, m.m12 },
                    { m.m20, m.m21, m.m22 }
                };
                double[,] ata = new double[3, 3];
                for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                    ata[r, c] = a[0, r] * a[0, c] + a[1, r] * a[1, c] + a[2, r] * a[2, c];

                double[,] v = { { 1d, 0d, 0d }, { 0d, 1d, 0d }, { 0d, 0d, 1d } };
                int[,] pairs = { { 0, 1 }, { 0, 2 }, { 1, 2 } };
                for (int sweep = 0; sweep < 16; sweep++)
                {
                    for (int k = 0; k < 3; k++)
                    {
                        int p = pairs[k, 0], q = pairs[k, 1];
                        double apq = ata[p, q];
                        if (Math.Abs(apq) <= 1e-15d) continue;
                        double tau = (ata[q, q] - ata[p, p]) / (2d * apq);
                        double t = tau >= 0d
                            ? 1d / (tau + Math.Sqrt(1d + tau * tau))
                            : -1d / (-tau + Math.Sqrt(1d + tau * tau));
                        double cs = 1d / Math.Sqrt(1d + t * t);
                        double sn = t * cs;

                        double app = ata[p, p], aqq = ata[q, q];
                        ata[p, p] = cs * cs * app - 2d * sn * cs * apq + sn * sn * aqq;
                        ata[q, q] = sn * sn * app + 2d * sn * cs * apq + cs * cs * aqq;
                        ata[p, q] = 0d;
                        ata[q, p] = 0d;

                        int r = 3 - p - q;
                        double apr = ata[p, r], aqr = ata[q, r];
                        ata[p, r] = ata[r, p] = cs * apr - sn * aqr;
                        ata[q, r] = ata[r, q] = sn * apr + cs * aqr;

                        for (int i = 0; i < 3; i++)
                        {
                            double vip = v[i, p], viq = v[i, q];
                            v[i, p] = cs * vip - sn * viq;
                            v[i, q] = sn * vip + cs * viq;
                        }
                    }
                }

                double s0 = Math.Sqrt(Math.Max(ata[0, 0], 1e-16d));
                double s1 = Math.Sqrt(Math.Max(ata[1, 1], 1e-16d));
                double s2 = Math.Sqrt(Math.Max(ata[2, 2], 1e-16d));
                double[] sArr = { s0, s1, s2 };
                double[,] u = new double[3, 3];
                for (int c = 0; c < 3; c++)
                {
                    double invS = 1d / sArr[c];
                    for (int r = 0; r < 3; r++)
                    {
                        u[r, c] = (a[r, 0] * v[0, c] + a[r, 1] * v[1, c] + a[r, 2] * v[2, c]) * invS;
                    }
                }

                double detU =
                    u[0, 0] * (u[1, 1] * u[2, 2] - u[1, 2] * u[2, 1]) -
                    u[0, 1] * (u[1, 0] * u[2, 2] - u[1, 2] * u[2, 0]) +
                    u[0, 2] * (u[1, 0] * u[2, 1] - u[1, 1] * u[2, 0]);
                if (detU < 0d)
                {
                    u[0, 0] = -u[0, 0];
                    u[1, 0] = -u[1, 0];
                    u[2, 0] = -u[2, 0];
                    s0 = -s0;
                }

                double detV =
                    v[0, 0] * (v[1, 1] * v[2, 2] - v[1, 2] * v[2, 1]) -
                    v[0, 1] * (v[1, 0] * v[2, 2] - v[1, 2] * v[2, 0]) +
                    v[0, 2] * (v[1, 0] * v[2, 1] - v[1, 1] * v[2, 0]);
                if (detV < 0d)
                {
                    v[0, 0] = -v[0, 0];
                    v[1, 0] = -v[1, 0];
                    v[2, 0] = -v[2, 0];
                    s0 = -s0;
                }

                var uCol1 = new Vector3((float)u[0, 1], (float)u[1, 1], (float)u[2, 1]);
                var uCol2 = new Vector3((float)u[0, 2], (float)u[1, 2], (float)u[2, 2]);
                qU = uCol2.sqrMagnitude > 1e-12f && uCol1.sqrMagnitude > 1e-12f
                    ? Quaternion.LookRotation(uCol2.normalized, uCol1.normalized)
                    : Quaternion.identity;

                // Columns of V^T are the rows of V!
                var vtCol1 = new Vector3((float)v[1, 0], (float)v[1, 1], (float)v[1, 2]);
                var vtCol2 = new Vector3((float)v[2, 0], (float)v[2, 1], (float)v[2, 2]);
                qVt = vtCol2.sqrMagnitude > 1e-12f && vtCol1.sqrMagnitude > 1e-12f
                    ? Quaternion.LookRotation(vtCol2.normalized, vtCol1.normalized)
                    : Quaternion.identity;

                sigma = new Vector3((float)s0, (float)s1, (float)s2);
            }

            public void Dispose()
            {
                if (m_TopContainerGo != null)
                {
                    Object.DestroyImmediate(m_TopContainerGo);
                }
            }
        }

        private sealed class RendererNodeState
        {
            private readonly Mesh m_OwnedMesh;
            private readonly Transform[] m_OutputBones;
            private readonly Transform m_OutputRootBone;
            private readonly (int Index, BlendShareProxyBlendShapeWeight Control)[] m_Weights;
            private readonly (int Input, int Output)[] m_Inherited;
            private readonly float[] m_FrameWeights;

            public bool HasChanges => m_OwnedMesh != null || m_OutputBones != null || m_OutputRootBone != null;

            public RendererNodeState(
                AvatarRendererApplyResult result,
                SkinnedMeshRenderer proxy,
                Transform[] originalProxyBones,
                IEnumerable<BlendShareMesh> components)
            {
                m_OwnedMesh = result.MeshResult.OutputMesh;
                var bones = result.MeshResult.OutputBones;
                if (bones != null && (originalProxyBones == null || !bones.SequenceEqual(originalProxyBones)))
                {
                    m_OutputBones = bones;
                }

                var candidateRootBone = result.MeshResult.OutputRootBone;
                if (candidateRootBone != null && candidateRootBone != proxy.rootBone)
                {
                    m_OutputRootBone = candidateRootBone;
                }

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

            public RendererNodeState(
                SkinnedMeshRenderer proxy,
                Transform[] originalProxyBones,
                Transform[] mappedShadowBones,
                Transform mappedShadowRootBone)
            {
                m_OwnedMesh = null;
                if (mappedShadowBones != null && (originalProxyBones == null || !mappedShadowBones.SequenceEqual(originalProxyBones)))
                {
                    m_OutputBones = mappedShadowBones;
                }

                if (mappedShadowRootBone != null && mappedShadowRootBone != proxy.rootBone)
                {
                    m_OutputRootBone = mappedShadowRootBone;
                }

                m_Weights = Array.Empty<(int, BlendShareProxyBlendShapeWeight)>();
                m_Inherited = Array.Empty<(int, int)>();
                m_FrameWeights = Array.Empty<float>();
            }

            public void ApplyOnFrame(SkinnedMeshRenderer smr)
            {
                if (!smr) return;
                if (!m_OwnedMesh)
                {
                    if (m_OutputBones != null) smr.bones = m_OutputBones;
                    if (m_OutputRootBone != null) smr.rootBone = m_OutputRootBone;
                    return;
                }

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

        private sealed class Node : IRenderFilterNode
        {
            private readonly Dictionary<Renderer, RendererNodeState> m_States;
            private readonly PreviewSkeletonRig m_PreviewRig;
            public RenderAspects WhatChanged => m_States != null && m_States.Values.Any(s => s.HasChanges)
                ? (RenderAspects.Mesh | RenderAspects.Shapes)
                : 0;
            public Node() { }
            public Node(Dictionary<Renderer, RendererNodeState> states, PreviewSkeletonRig previewRig = null)
            {
                m_States = states;
                m_PreviewRig = previewRig;
            }

            public Task<IRenderFilterNode> Refresh(IEnumerable<(Renderer, Renderer)> pairs, ComputeContext context, RenderAspects updatedAspects)
                => Task.FromResult<IRenderFilterNode>(null);

            public void OnFrame(Renderer original, Renderer proxy)
            {
                if (m_States != null && proxy is SkinnedMeshRenderer smr && m_States.TryGetValue(original, out var state))
                {
                    m_PreviewRig?.Sync();
                    state.ApplyOnFrame(smr);
                }
            }

            public void Dispose()
            {
                if (m_States != null)
                {
                    foreach (var state in m_States.Values) state.Dispose();
                }

                m_PreviewRig?.Dispose();
            }
        }
    }
}
