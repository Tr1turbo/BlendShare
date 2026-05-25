using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Components;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Features.BoneGraph;
using Triturbo.BlendShare.Features.SkinWeights;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Triturbo.BlendShare.NDMF
{
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
            IEnumerable<BlendShareMeshComponent> meshAppliers,
            IEnumerable<BlendShareBoneProxyComponent> boneProxies = null)
        {
            TargetMeshContainer = targetMeshContainer;
            TargetRoot = targetMeshContainer is GameObject gameObject ? gameObject.transform : null;
            var orderedMeshAppliers = (meshAppliers ?? Enumerable.Empty<BlendShareMeshComponent>())
                .Where(applier => applier != null)
                .OrderBy(applier => GetHierarchyOrder(applier.Owner != null ? applier.Owner.transform : null))
                .ThenBy(applier => GetHierarchyOrder(applier.transform))
                .ToArray();

            BlendShares = DedupBlendShares(
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
            IReadOnlyList<BlendShareMeshComponent> orderedMeshAppliers)
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

                foreach (var meshApplier in meshAppliers)
                {
                    var share = FindBlendShareForMeshData(owner, meshApplier.MeshData);
                    if (share == null)
                    {
                        diagnostics.Add($"BlendShare mesh applier '{meshApplier.name}' references mesh data that is not present in its owner BlendShare list.");
                        continue;
                    }

                    var renderer = meshApplier.TargetRenderer;
                    var targetMesh = renderer != null ? renderer.sharedMesh : null;
                    var targetRoot = TargetRoot != null ? TargetRoot : ResolveOwnerRoot(owner);
                    string rendererPath = renderer != null && targetRoot != null
                        ? MeshNodePath.Normalize(MeshNodePath.GetRelativePath(renderer.transform, targetRoot))
                        : meshApplier.RendererNodePath;
                    string requestKey = $"{share.GetInstanceID()}:{meshApplier.MeshData.GetInstanceID()}:{rendererPath}";
                    if (!emittedRequestKeys.Add(requestKey))
                    {
                        continue;
                    }

                    var overrides = BuildBoneOverrides(meshApplier, meshApplier.MeshData);
                    var mappingOverrides = BuildMappingOverrides(share, meshApplier, meshApplier.MeshData);
                    yield return new BlendShareGenerationRequest(
                        order++,
                        share,
                        meshApplier.MeshData,
                        rendererPath,
                        renderer,
                        targetMesh,
                        overrides,
                        mappingOverrides);
                }
            }
        }

        private static BlendShareObject FindBlendShareForMeshData(BlendShareComponent owner, MeshDataObject meshData)
        {
            if (owner == null || meshData == null)
            {
                return null;
            }

            return DedupBlendShares(owner.BlendShares)
                .FirstOrDefault(share => (share.Meshes ?? Array.Empty<MeshDataObject>()).Contains(meshData));
        }

        private static IEnumerable<UnityVertexMappingObject> BuildMappingOverrides(
            BlendShareObject share,
            BlendShareMeshComponent meshApplier,
            MeshDataObject meshData)
        {
            var targetMesh = meshApplier?.TargetRenderer != null ? meshApplier.TargetRenderer.sharedMesh : null;
            if (meshApplier == null || meshData == null || targetMesh == null)
            {
                yield break;
            }

            if (BlendShareVertexMappingCacheService.TryGet(share, meshData, targetMesh, out var mapping))
            {
                yield return mapping;
            }
        }

        private IEnumerable<BlendShareGenerationBoneOverride> BuildBoneOverrides(
            BlendShareMeshComponent meshApplier,
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

        private static bool IsUsableMeshApplier(BlendShareMeshComponent applier)
        {
            return applier != null &&
                   applier.EnabledForBuild &&
                   applier.Owner != null &&
                   applier.TargetRenderer != null &&
                   applier.TargetRenderer.sharedMesh != null &&
                   applier.MeshData != null;
        }

        private static string FormatSkippedMeshApplier(BlendShareMeshComponent applier)
        {
            if (applier == null)
            {
                return "BlendShare mesh applier is missing.";
            }

            if (!applier.EnabledForBuild)
            {
                return $"BlendShare mesh applier '{applier.name}' is disabled.";
            }

            if (applier.Owner == null)
            {
                return $"BlendShare mesh applier '{applier.name}' has no owner.";
            }

            if (applier.TargetRenderer == null)
            {
                return $"BlendShare mesh applier '{applier.name}' has no target renderer.";
            }

            if (applier.TargetRenderer.sharedMesh == null)
            {
                return $"BlendShare mesh applier '{applier.name}' target renderer has no mesh.";
            }

            return $"BlendShare mesh applier '{applier.name}' is invalid.";
        }

        private static bool ShouldReportSkippedMeshApplier(BlendShareMeshComponent applier)
        {
            return applier == null || applier.EnabledForBuild;
        }

        private static bool IsBoneNeeded(SkinWeightFeatureObject skin, string bonePath)
        {
            return (skin.m_BonePaths ?? Array.Empty<string>())
                .Select(MeshNodePath.Normalize)
                .Contains(MeshNodePath.Normalize(bonePath));
        }

        private static IEnumerable<BlendShareObject> DedupBlendShares(IEnumerable<BlendShareObject> blendShares)
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

        private static Transform ResolveOwnerRoot(BlendShareComponent owner)
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
