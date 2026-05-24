using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Components;
using Triturbo.BlendShare.Features.BoneGraph;
using Triturbo.BlendShare.Features.SkinWeights;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Triturbo.BlendShare.Core
{
    public interface IBlendShareGenerationSource
    {
        Object TargetMeshContainer { get; }
        Transform TargetRoot { get; }
        IReadOnlyList<BlendShareObject> BlendShares { get; }
        IReadOnlyList<BlendShareGenerationRequest> Requests { get; }
        IReadOnlyList<string> Diagnostics { get; }
    }

    public sealed class BlendShareGenerationRequest
    {
        private readonly Dictionary<string, BlendShareGenerationBoneOverride> boneOverridesByPath;

        public int Order { get; }
        public BlendShareObject Share { get; }
        public MeshDataObject MeshData { get; }
        public string RendererNodePath { get; }
        public SkinnedMeshRenderer TargetRenderer { get; }
        public IReadOnlyList<BlendShareGenerationBoneOverride> BoneOverrides { get; }

        public BlendShareGenerationRequest(
            int order,
            BlendShareObject share,
            MeshDataObject meshData,
            string rendererNodePath = null,
            SkinnedMeshRenderer targetRenderer = null,
            IEnumerable<BlendShareGenerationBoneOverride> boneOverrides = null)
        {
            Order = order;
            Share = share;
            MeshData = meshData;
            RendererNodePath = MeshNodePath.Normalize(rendererNodePath ?? meshData?.m_Path);
            TargetRenderer = targetRenderer;
            BoneOverrides = (boneOverrides ?? Enumerable.Empty<BlendShareGenerationBoneOverride>())
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.SourceBonePath))
                .GroupBy(item => item.SourceBonePath)
                .Select(group => group.First())
                .ToArray();
            boneOverridesByPath = BoneOverrides.ToDictionary(item => item.SourceBonePath, item => item);
        }

        public bool TryGetBoneOverride(string sourceBonePath, out BlendShareGenerationBoneOverride boneOverride)
        {
            return boneOverridesByPath.TryGetValue(MeshNodePath.Normalize(sourceBonePath), out boneOverride);
        }
    }

    public sealed class BlendShareGenerationBoneOverride
    {
        public string SourceBonePath { get; }
        public string FinalBonePath { get; }
        public string ParentPath { get; }
        public string BoneName { get; }
        public Transform TargetParent { get; }
        public Transform ProxyTransform { get; }
        public Vector3 LocalPosition { get; }
        public Vector3 LocalEulerRotation { get; }
        public Vector3 LocalScale { get; }
        public Vector3 GeneratedLocalPosition { get; }
        public Vector3 GeneratedLocalEulerRotation { get; }
        public Vector3 GeneratedLocalScale { get; }
        public bool RecalculateBindpose { get; }

        public BlendShareGenerationBoneOverride(
            string sourceBonePath,
            string finalBonePath,
            string parentPath,
            string boneName,
            Transform targetParent,
            Transform proxyTransform,
            Vector3 localPosition,
            Vector3 localEulerRotation,
            Vector3 localScale,
            Vector3 generatedLocalPosition,
            Vector3 generatedLocalEulerRotation,
            Vector3 generatedLocalScale,
            bool recalculateBindpose)
        {
            SourceBonePath = MeshNodePath.Normalize(sourceBonePath);
            FinalBonePath = MeshNodePath.Normalize(finalBonePath);
            ParentPath = MeshNodePath.Normalize(parentPath);
            BoneName = string.IsNullOrWhiteSpace(boneName) ? MeshNodePath.LeafName(FinalBonePath) : boneName;
            TargetParent = targetParent;
            ProxyTransform = proxyTransform;
            LocalPosition = localPosition;
            LocalEulerRotation = localEulerRotation;
            LocalScale = localScale == Vector3.zero ? Vector3.one : localScale;
            GeneratedLocalPosition = generatedLocalPosition;
            GeneratedLocalEulerRotation = generatedLocalEulerRotation;
            GeneratedLocalScale = generatedLocalScale == Vector3.zero ? Vector3.one : generatedLocalScale;
            RecalculateBindpose = recalculateBindpose;
        }

        public bool TryGetLocalToWorld(Transform fallbackRoot, out Matrix4x4 localToWorld)
        {
            var parent = TargetParent != null ? TargetParent : fallbackRoot;
            if (parent == null)
            {
                localToWorld = Matrix4x4.identity;
                return false;
            }

            localToWorld = parent.localToWorldMatrix * Matrix4x4.TRS(
                LocalPosition,
                Quaternion.Euler(LocalEulerRotation),
                LocalScale);
            return true;
        }
    }

    public sealed class BlendShareObjectGenerationSource : IBlendShareGenerationSource
    {
        private readonly List<string> diagnostics = new();

        public Object TargetMeshContainer { get; }
        public Transform TargetRoot { get; }
        public IReadOnlyList<BlendShareObject> BlendShares { get; }
        public IReadOnlyList<BlendShareGenerationRequest> Requests { get; }
        public IReadOnlyList<string> Diagnostics => diagnostics;

        public BlendShareObjectGenerationSource(
            Object targetMeshContainer,
            IEnumerable<BlendShareObject> blendShares,
            Func<BlendShareObject, MeshDataObject, bool> shouldGenerateMesh = null)
        {
            TargetMeshContainer = targetMeshContainer;
            TargetRoot = targetMeshContainer is GameObject gameObject ? gameObject.transform : null;
            BlendShares = DedupBlendShares(blendShares).ToArray();
            Requests = BuildRequests(BlendShares, shouldGenerateMesh).ToArray();
        }

        private static IEnumerable<BlendShareGenerationRequest> BuildRequests(
            IEnumerable<BlendShareObject> blendShares,
            Func<BlendShareObject, MeshDataObject, bool> shouldGenerateMesh)
        {
            int order = 0;
            foreach (var share in blendShares ?? Enumerable.Empty<BlendShareObject>())
            {
                foreach (var meshData in share.Meshes ?? Array.Empty<MeshDataObject>())
                {
                    if (meshData == null || (shouldGenerateMesh != null && !shouldGenerateMesh(share, meshData)))
                    {
                        continue;
                    }

                    yield return new BlendShareGenerationRequest(order++, share, meshData);
                }
            }
        }

        internal static IEnumerable<BlendShareObject> DedupBlendShares(IEnumerable<BlendShareObject> blendShares)
        {
            var seen = new HashSet<BlendShareObject>();
            foreach (var share in blendShares ?? Enumerable.Empty<BlendShareObject>())
            {
                if (share != null && seen.Add(share))
                {
                    yield return share;
                }
            }
        }
    }

    public sealed class BlendShareComponentGenerationSource : IBlendShareGenerationSource
    {
        private readonly List<string> diagnostics = new();

        public Object TargetMeshContainer { get; }
        public Transform TargetRoot { get; }
        public IReadOnlyList<BlendShareObject> BlendShares { get; }
        public IReadOnlyList<BlendShareGenerationRequest> Requests { get; }
        public IReadOnlyList<string> Diagnostics => diagnostics;

        public BlendShareComponentGenerationSource(
            Object targetMeshContainer,
            IEnumerable<BlendShareMeshApplierComponent> meshAppliers,
            IEnumerable<BlendShareBoneProxyComponent> boneProxies = null)
        {
            TargetMeshContainer = targetMeshContainer;
            TargetRoot = targetMeshContainer is GameObject gameObject ? gameObject.transform : null;
            var orderedMeshAppliers = (meshAppliers ?? Enumerable.Empty<BlendShareMeshApplierComponent>())
                .Where(applier => applier != null)
                .OrderBy(applier => GetHierarchyOrder(applier.Owner != null ? applier.Owner.transform : null))
                .ThenBy(applier => GetHierarchyOrder(applier.transform))
                .ToArray();

            BlendShares = BlendShareObjectGenerationSource.DedupBlendShares(
                orderedMeshAppliers
                    .Select(applier => applier.Owner)
                    .Where(owner => owner != null)
                    .Distinct()
                    .OrderBy(owner => GetHierarchyOrder(owner.transform))
                    .SelectMany(owner => owner.BlendShares))
                .ToArray();
            Requests = BuildRequests(orderedMeshAppliers).ToArray();
        }

        private IEnumerable<BlendShareGenerationRequest> BuildRequests(
            IReadOnlyList<BlendShareMeshApplierComponent> orderedMeshAppliers)
        {
            int order = 0;
            var emittedRequestKeys = new HashSet<string>();
            foreach (var ownerGroup in orderedMeshAppliers.GroupBy(applier => applier.Owner)
                         .OrderBy(group => GetHierarchyOrder(group.Key != null ? group.Key.transform : null)))
            {
                var owner = ownerGroup.Key;
                if (owner == null)
                {
                    diagnostics.Add("BlendShare mesh applier has no owner.");
                    continue;
                }

                var meshAppliers = ownerGroup
                    .Where(IsUsableMeshApplier)
                    .OrderBy(applier => GetHierarchyOrder(applier.transform))
                    .ToArray();
                foreach (var skipped in ownerGroup.Where(applier => !IsUsableMeshApplier(applier) && ShouldReportSkippedMeshApplier(applier)))
                {
                    diagnostics.Add(FormatSkippedMeshApplier(skipped));
                }

                foreach (var share in BlendShareObjectGenerationSource.DedupBlendShares(owner.BlendShares))
                {
                    foreach (var meshApplier in meshAppliers)
                    {
                        string rendererPath = MeshNodePath.Normalize(meshApplier.RendererNodePath);
                        foreach (var meshData in share.Meshes ?? Array.Empty<MeshDataObject>())
                        {
                            if (meshData == null || MeshNodePath.Normalize(meshData.m_Path) != rendererPath)
                            {
                                continue;
                            }

                            string requestKey = $"{share.GetInstanceID()}:{rendererPath}";
                            if (!emittedRequestKeys.Add(requestKey))
                            {
                                continue;
                            }

                            var overrides = BuildBoneOverrides(meshApplier, meshData);
                            yield return new BlendShareGenerationRequest(
                                order++,
                                share,
                                meshData,
                                rendererPath,
                                meshApplier.TargetRenderer,
                                overrides);
                        }
                    }
                }
            }
        }

        private IEnumerable<BlendShareGenerationBoneOverride> BuildBoneOverrides(
            BlendShareMeshApplierComponent meshApplier,
            MeshDataObject meshData)
        {
            var renderer = meshApplier.TargetRenderer;
            var targetRoot = TargetRoot != null ? TargetRoot : ResolveOwnerRoot(meshApplier.Owner);
            var skin = meshData != null ? meshData.GetFeature<SkinWeightFeatureObject>() : null;
            if (renderer == null || targetRoot == null || skin?.m_BoneGraph == null)
            {
                yield break;
            }

            var existingRendererBonePaths = new HashSet<string>((renderer.bones ?? Array.Empty<Transform>())
                .Where(bone => bone != null)
                .Select(bone => MeshNodePath.Normalize(MeshNodePath.GetRelativePath(bone, targetRoot))));

            foreach (var bone in skin.m_BoneGraph.Bones ?? Array.Empty<BoneNodeData>())
            {
                if (bone == null)
                {
                    continue;
                }

                string sourceBonePath = MeshNodePath.Normalize(bone.m_Path);
                if (!IsBoneNeeded(skin, sourceBonePath) || existingRendererBonePaths.Contains(sourceBonePath))
                {
                    continue;
                }

                if (!meshApplier.TryGetBoneProxyBinding(skin.m_BoneGraph, sourceBonePath, out var binding))
                {
                    diagnostics.Add($"Bone proxy binding for '{sourceBonePath}' was not found for renderer '{meshApplier.RendererNodePath}'. Rebuild bone proxies.");
                    continue;
                }

                var proxy = binding.Proxy;
                if (proxy == null)
                {
                    diagnostics.Add($"Bone proxy binding for '{sourceBonePath}' on renderer '{meshApplier.RendererNodePath}' has no proxy.");
                    continue;
                }

                if (proxy.Owner != meshApplier.Owner)
                {
                    diagnostics.Add($"Bone proxy binding for '{sourceBonePath}' on renderer '{meshApplier.RendererNodePath}' references a proxy owned by another BlendShare applier.");
                    continue;
                }

                if (proxy.TargetParent == null)
                {
                    diagnostics.Add($"Bone proxy '{proxy.name}' for '{sourceBonePath}' has no target parent.");
                    continue;
                }

                string parentPath = proxy.TargetParent != null
                    ? MeshNodePath.Normalize(MeshNodePath.GetRelativePath(proxy.TargetParent, targetRoot))
                    : MeshNodePath.Root;
                string finalPath = BuildChildPath(parentPath, proxy.name);
                GetProxyLocalTransform(proxy, out var localPosition, out var localEulerRotation, out var localScale);
                yield return new BlendShareGenerationBoneOverride(
                    sourceBonePath,
                    finalPath,
                    parentPath,
                    proxy.name,
                    proxy.TargetParent,
                    proxy.transform,
                    proxy.LocalPosition,
                    proxy.LocalEulerRotation,
                    proxy.LocalScale,
                    localPosition,
                    localEulerRotation,
                    localScale,
                    proxy.RecalculateBindpose);
            }
        }

        private static bool IsUsableMeshApplier(BlendShareMeshApplierComponent applier)
        {
            return applier != null &&
                   applier.EnabledForBuild &&
                   !applier.IsStale &&
                   applier.Owner != null &&
                   applier.TargetRenderer != null &&
                   applier.MeshData != null &&
                   !string.IsNullOrWhiteSpace(applier.RendererNodePath);
        }

        private static string FormatSkippedMeshApplier(BlendShareMeshApplierComponent applier)
        {
            if (applier == null)
            {
                return "BlendShare mesh applier is missing.";
            }

            if (!applier.EnabledForBuild)
            {
                return $"BlendShare mesh applier '{applier.name}' is disabled.";
            }

            if (applier.IsStale)
            {
                return string.IsNullOrWhiteSpace(applier.DiagnosticMessage)
                    ? $"BlendShare mesh applier '{applier.name}' is stale."
                    : applier.DiagnosticMessage;
            }

            if (applier.Owner == null)
            {
                return $"BlendShare mesh applier '{applier.name}' has no owner.";
            }

            if (applier.TargetRenderer == null)
            {
                return $"BlendShare mesh applier '{applier.name}' has no target renderer.";
            }

            return $"BlendShare mesh applier '{applier.name}' is invalid.";
        }

        private static bool ShouldReportSkippedMeshApplier(BlendShareMeshApplierComponent applier)
        {
            return applier == null || applier.EnabledForBuild;
        }

        private static bool IsBoneNeeded(SkinWeightFeatureObject skin, string bonePath)
        {
            return (skin.m_BonePaths ?? Array.Empty<string>())
                .Select(MeshNodePath.Normalize)
                .Contains(MeshNodePath.Normalize(bonePath));
        }

        private static void GetProxyLocalTransform(
            BlendShareBoneProxyComponent proxy,
            out Vector3 localPosition,
            out Vector3 localEulerRotation,
            out Vector3 localScale)
        {
            if (proxy != null &&
                proxy.TryGetCurrentLocalTransform(out localPosition, out localEulerRotation, out localScale))
            {
                localScale = localScale == Vector3.zero ? Vector3.one : localScale;
                return;
            }

            localPosition = proxy != null ? proxy.LocalPosition : Vector3.zero;
            localEulerRotation = proxy != null ? proxy.LocalEulerRotation : Vector3.zero;
            localScale = proxy != null ? proxy.LocalScale : Vector3.one;
        }

        private static string BuildChildPath(string parentPath, string childName)
        {
            childName = string.IsNullOrWhiteSpace(childName) ? "BlendShareBone" : childName;
            parentPath = MeshNodePath.Normalize(parentPath);
            return parentPath == MeshNodePath.Root
                ? MeshNodePath.Normalize(childName)
                : MeshNodePath.Normalize($"{parentPath}/{childName}");
        }

        private static Transform ResolveOwnerRoot(BlendShareApplierComponent owner)
        {
            return owner != null && owner.TargetRoot != null ? owner.TargetRoot : owner != null ? owner.transform : null;
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
