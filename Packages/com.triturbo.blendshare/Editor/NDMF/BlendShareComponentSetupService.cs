using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Components;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Features.SkinWeights;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.NDMF
{
    internal static class BlendShareComponentSetupService
    {

        public static CreateBlendShareSetupResult CreateSetup(Transform targetRoot, BlendShareObject patch)
        {
            return CreateSetup(targetRoot, patch, null, null);
        }

        public static CreateBlendShareSetupResult CreateSetup(
            Transform targetRoot,
            BlendShareObject patch,
            MeshDataObject selectedMesh,
            SkinnedMeshRenderer targetRenderer)
        {
            var result = new CreateBlendShareSetupResult();
            if (targetRoot == null)
            {
                result.AddDiagnostic("Target root is missing.");
                return result;
            }

            if (patch == null)
            {
                result.AddDiagnostic("BlendShare patch is missing.");
                return result;
            }

            if (selectedMesh != null && !(patch.Meshes ?? Array.Empty<MeshDataObject>()).Contains(selectedMesh))
            {
                result.AddDiagnostic("Mesh data is not part of the selected BlendShare patch.");
                return result;
            }

            string groupName = string.IsNullOrWhiteSpace(patch.m_PatchId) ? patch.name : patch.m_PatchId;
            var placementObject = new GameObject(CreateUniqueChildName(targetRoot, groupName));
            Undo.RegisterCreatedObjectUndo(placementObject, "Create BlendShare Setup");
            placementObject.transform.SetParent(targetRoot, false);

            var renderersByPath = targetRoot
                .GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .GroupBy(renderer => MeshNodePath.Normalize(MeshNodePath.GetRelativePath(renderer.transform, targetRoot)))
                .ToDictionary(group => group.Key, group => group.First());
            var desiredMeshes = (selectedMesh != null
                    ? new[] { selectedMesh }
                    : patch.Meshes ?? Array.Empty<MeshDataObject>())
                .Where(mesh => mesh != null)
                .GroupBy(mesh => MeshNodePath.Normalize(mesh.m_Path))
                .Select(group => group.First());

            foreach (var meshData in desiredMeshes)
            {
                string path = MeshNodePath.Normalize(meshData.m_Path);
                var renderer = targetRenderer;
                if (renderer == null && !renderersByPath.TryGetValue(path, out renderer))
                {
                    result.AddDiagnostic($"Renderer path '{path}' was not found under '{targetRoot.name}'.");
                    continue;
                }

                if (renderer == null ||
                    (renderer.transform != targetRoot && !renderer.transform.IsChildOf(targetRoot)))
                {
                    result.AddDiagnostic($"Target renderer '{renderer?.name ?? "<missing>"}' is not under '{targetRoot.name}'.");
                    continue;
                }

                var applier = CreateMeshApplier(placementObject.transform, path, renderer.name);

                Undo.RecordObject(applier, "Create BlendShare Mesh Binding");
                applier.Patch = patch;
                applier.TargetRenderer = renderer;
                applier.MeshData = meshData;
                applier.RendererNodePath = path;
                applier.DiagnosticMessage = string.Empty;
                TryResolveMeshApplierMappingReference(applier, out _);
                applier.SyncActiveBlendShapeWeights();
                EditorUtility.SetDirty(applier);
                result.AddMeshApplier(applier);
            }

            if (result.MeshAppliers.Count == 0)
            {
                Undo.DestroyObjectImmediate(placementObject);
                return result;
            }

            var proxyResult = RebuildBoneProxies(result.MeshAppliers, targetRoot, placementObject.transform);
            foreach (string diagnostic in proxyResult.Diagnostics)
            {
                result.AddDiagnostic(diagnostic);
            }
            foreach (var proxy in proxyResult.BoneProxies)
            {
                result.AddBoneProxy(proxy);
            }

            return result;
        }

        public static Transform ResolveTargetRoot(SkinnedMeshRenderer renderer, MeshDataObject meshData)
        {
            if (renderer == null || meshData == null)
            {
                return null;
            }

            string expectedPath = MeshNodePath.Normalize(meshData.m_Path);
            for (Transform candidate = renderer.transform; candidate != null; candidate = candidate.parent)
            {
                if (MeshNodePath.Normalize(MeshNodePath.GetRelativePath(renderer.transform, candidate)) == expectedPath)
                {
                    return candidate;
                }
            }

            return null;
        }

        public static RebuildBoneProxiesResult RebuildBoneProxies(
            IEnumerable<BlendShareMesh> meshAppliers,
            Transform targetRoot,
            Transform placementParent = null)
        {
            var result = new RebuildBoneProxiesResult();
            if (targetRoot == null)
            {
                result.AddDiagnostic("Target root is missing.");
                return result;
            }

            var appliers = (meshAppliers ?? Array.Empty<BlendShareMesh>())
                .Where(applier => applier != null && applier.isActiveAndEnabled)
                .Distinct()
                .ToArray();
            var transformsByPath = targetRoot
                .GetComponentsInChildren<Transform>(true)
                .GroupBy(transform => MeshNodePath.Normalize(MeshNodePath.GetRelativePath(transform, targetRoot)))
                .ToDictionary(group => group.Key, group => group.First());
            var existingProxies = targetRoot.GetComponentsInChildren<BlendShareBoneProxy>(true)
                .Where(proxy => proxy != null)
                .ToList();
            var proxyLookup = BlendShareBoneProxyLookup.Create(existingProxies);
            var proxiesByKey = existingProxies
                .GroupBy(proxy => BuildProxyKey(proxy.TargetParent, proxy.name, proxy.LocalPosition, proxy.LocalEulerRotation, proxy.LocalScale))
                .ToDictionary(group => group.Key, group => group.ToList());
            var proxiesByName = existingProxies
                .GroupBy(proxy => proxy.name)
                .ToDictionary(group => group.Key, group => group.ToList());
            var usedProxies = new HashSet<BlendShareBoneProxy>();
            var proxiesByBonePath = new Dictionary<BlendShareBoneProxyLookup.SourceKey, BlendShareBoneProxy>();

            foreach (var meshApplier in appliers)
            {
                var renderer = meshApplier.TargetRenderer;
                if (renderer == null)
                {
                    continue;
                }

                var existingRendererBonePaths = new HashSet<string>((renderer.bones ?? Array.Empty<Transform>())
                    .Where(bone => bone != null)
                    .Select(bone => MeshNodePath.Normalize(MeshNodePath.GetRelativePath(bone, targetRoot))));
                foreach (var meshData in GetMeshDataForApplier(meshApplier))
                {
                    var skin = meshData != null ? meshData.GetFeature<SkinWeightFeatureObject>() : null;
                    if (skin?.Armature == null)
                    {
                        continue;
                    }

                    var mapping = GetFbxToUnityMapping(meshApplier, meshData);

                    foreach (string bonePath in skin.GetNeededBonePathsInArmatureOrder())
                    {
                        if (existingRendererBonePaths.Contains(bonePath))
                        {
                            continue;
                        }

                        var bone = skin.Armature.GetBone(bonePath);
                        if (bone == null || !bone.m_CreateIfMissing)
                        {
                            result.AddDiagnostic($"Bone '{bonePath}' is required but cannot be auto-created.");
                            continue;
                        }

                        var parent = ResolveProxyParent(bone, skin.Armature, targetRoot, transformsByPath, proxiesByBonePath);
                        var localScale = bone.m_FbxLocalScale == Vector3.zero ? Vector3.one : bone.m_FbxLocalScale;
                        var localPosition = mapping != null
                            ? mapping.ConvertFbxVectorToUnity(bone.m_FbxLocalTranslation)
                            : bone.m_FbxLocalTranslation;
                        string desiredName = MeshNodePath.LeafName(bonePath);
                        string key = BuildProxyKey(parent, desiredName, localPosition, bone.m_FbxLocalEulerRotation, localScale);
                        var sourceKey = new BlendShareBoneProxyLookup.SourceKey(skin.Armature, bonePath);
                        bool createdProxy = false;
                        if (!proxiesByBonePath.TryGetValue(sourceKey, out var proxy) &&
                            !proxyLookup.TryGet(skin.Armature, bonePath, out proxy) &&
                            !TryTakeProxy(proxiesByKey, key, usedProxies, out proxy) &&
                            !TryTakeProxy(proxiesByName, desiredName, usedProxies, out proxy))
                        {
                            proxy = CreateBoneProxy(placementParent != null ? placementParent : targetRoot, bonePath, parent);
                            createdProxy = true;
                        }

                        usedProxies.Add(proxy);
                        Undo.RecordObject(proxy, "Rebuild BlendShare Bone Proxy");
                        proxy.SourceArmature = skin.Armature;
                        proxy.SourceBonePath = bonePath;
                        proxy.TargetParent = parent;
                        if (createdProxy || !proxy.RecalculateBindpose)
                        {
                            proxy.LocalPosition = localPosition;
                            proxy.LocalEulerRotation = bone.m_FbxLocalEulerRotation;
                            proxy.LocalScale = localScale;
                        }
                        proxy.ResetTransformToBindPose();
                        EditorUtility.SetDirty(proxy);
                        result.AddBoneProxy(proxy);
                        proxiesByBonePath[sourceKey] = proxy;
                    }
                }
            }

            return result;
        }

        private static bool TryTakeProxy(
            IReadOnlyDictionary<string, List<BlendShareBoneProxy>> proxies,
            string key,
            ISet<BlendShareBoneProxy> usedProxies,
            out BlendShareBoneProxy proxy)
        {
            proxy = null;
            if (string.IsNullOrWhiteSpace(key) ||
                proxies == null ||
                !proxies.TryGetValue(key, out var candidates))
            {
                return false;
            }

            proxy = candidates.FirstOrDefault(candidate =>
                candidate != null &&
                candidate.isActiveAndEnabled &&
                candidate.SourceArmature == null &&
                string.IsNullOrWhiteSpace(candidate.SourceBonePath) &&
                !usedProxies.Contains(candidate));
            return proxy != null;
        }

        private static UnityVertexMappingObject GetFbxToUnityMapping(BlendShareMesh meshApplier, MeshDataObject meshData)
        {
            var targetMesh = meshApplier?.TargetRenderer != null ? meshApplier.TargetRenderer.sharedMesh : null;
            if (meshApplier != null && meshData == meshApplier.MeshData &&
                TryResolveMeshApplierMappingReference(meshApplier, out var resolvedMapping))
            {
                return resolvedMapping;
            }

            var mapping = targetMesh != null
                ? (meshData?.m_Mappings ?? Array.Empty<UnityVertexMappingObject>())
                    .FirstOrDefault(candidate => UnityMeshEditorDataUtility.IsMappingCompatible(candidate, meshData, targetMesh))
                : (meshData?.m_Mappings ?? Array.Empty<UnityVertexMappingObject>())
                    .FirstOrDefault(candidate => candidate != null && candidate.m_IsValid);
            if (mapping == null && meshApplier != null && targetMesh != null)
            {
                var sourceFbx = ResolveSourceFbx(meshApplier.Patch, targetMesh);
                BlendShareVertexMappingCacheService.TryGet(
                    sourceFbx,
                    meshData,
                    targetMesh,
                    out mapping);
            }

            return mapping;
        }

        private static IEnumerable<MeshDataObject> GetMeshDataForApplier(BlendShareMesh meshApplier)
        {
            if (meshApplier?.Patch == null || meshApplier.MeshData == null)
            {
                yield break;
            }

            if ((meshApplier.Patch.Meshes ?? Array.Empty<MeshDataObject>()).Contains(meshApplier.MeshData))
            {
                yield return meshApplier.MeshData;
            }
        }

        public static bool ValidateMeshApplierForBuild(
            BlendShareMesh applier,
            out string diagnostic)
        {
            diagnostic = null;
            if (applier == null)
            {
                diagnostic = "BlendShare mesh applier is missing.";
                return false;
            }

            if (applier.Patch == null)
            {
                diagnostic = $"BlendShare mesh applier '{applier.name}' has no patch.";
                return false;
            }

            if (applier.TargetRenderer == null)
            {
                diagnostic = $"BlendShare mesh applier '{applier.name}' has no target renderer.";
                return false;
            }

            if (applier.MeshData == null)
            {
                diagnostic = $"BlendShare mesh applier '{applier.name}' has no mesh data.";
                return false;
            }

            if (!(applier.Patch.Meshes ?? Array.Empty<MeshDataObject>()).Contains(applier.MeshData))
            {
                diagnostic = $"BlendShare mesh applier '{applier.name}' references mesh data that is not present in its patch.";
                return false;
            }

            if (applier.TargetRenderer.sharedMesh == null)
            {
                diagnostic = $"BlendShare mesh applier '{applier.name}' target renderer has no mesh.";
                return false;
            }

            return true;
        }

        public static bool ValidateMeshApplierMapping(
            BlendShareMesh applier,
            out string diagnostic)
        {
            diagnostic = null;
            if (applier == null || !applier.isActiveAndEnabled)
            {
                return true;
            }

            var renderer = applier.TargetRenderer;
            if (renderer == null || applier.Patch == null)
            {
                return true;
            }

            var targetMesh = renderer.sharedMesh;
            if (targetMesh == null)
            {
                diagnostic = $"BlendShare mesh applier '{applier.name}' target renderer has no mesh for mapping compatibility check.";
                return false;
            }

            if (!(applier.Patch.Meshes ?? Array.Empty<MeshDataObject>()).Contains(applier.MeshData))
            {
                diagnostic = $"BlendShare mesh applier '{applier.name}' references mesh data that is not present in its patch.";
                return false;
            }

            if (TryResolveMeshApplierMappingReference(applier, out _))
            {
                return true;
            }

            diagnostic = $"BlendShare mesh applier '{applier.name}' does not have a valid Unity vertex mapping reference.";
            return false;
        }

        public static bool TryGetCachedInvalidMappingDiagnostic(
            BlendShareMesh applier,
            out string diagnostic)
        {
            diagnostic = null;
            if (applier == null || applier.Patch == null || applier.MeshData == null || applier.TargetRenderer == null)
            {
                return false;
            }

            var targetMesh = applier.TargetRenderer.sharedMesh;
            if (targetMesh == null)
            {
                return false;
            }

            var sourceFbx = ResolveSourceFbx(applier.Patch, targetMesh);
            if (sourceFbx == null)
            {
                return false;
            }

            return BlendShareVertexMappingCacheService.TryGetInvalidDiagnostic(
                sourceFbx,
                applier.MeshData,
                targetMesh,
                out diagnostic);
        }

        internal static bool TryResolveMeshApplierMappingReference(
            BlendShareMesh applier,
            out UnityVertexMappingObject mapping)
        {
            mapping = null;
            if (applier == null || applier.Patch == null || applier.MeshData == null || applier.TargetRenderer == null)
            {
                return false;
            }

            var targetMesh = applier.TargetRenderer.sharedMesh;
            if (targetMesh == null)
            {
                AssignMapping(applier, null);
                return false;
            }

            if (IsUsableMappingReference(applier, applier.Mapping, targetMesh))
            {
                mapping = applier.Mapping;
                return true;
            }

            mapping = (applier.MeshData.m_Mappings ?? Array.Empty<UnityVertexMappingObject>())
                .FirstOrDefault(candidate =>
                    UnityMeshEditorDataUtility.IsMappingCompatible(candidate, applier.MeshData, targetMesh));
            if (mapping == null)
            {
                var sourceFbx = ResolveSourceFbx(applier.Patch, targetMesh);
                BlendShareVertexMappingCacheService.TryGet(
                    sourceFbx,
                    applier.MeshData,
                    targetMesh,
                    out mapping);
            }

            AssignMapping(applier, mapping);
            return mapping != null;
        }

        public static bool EnsureMeshApplierMappingCache(
            BlendShareMesh applier,
            out string diagnostic)
        {
            diagnostic = null;
            if (applier == null || applier.TargetRenderer == null)
            {
                return false;
            }

            var targetMesh = applier.TargetRenderer.sharedMesh;
            if (targetMesh == null)
            {
                diagnostic = $"BlendShare mesh applier '{applier.name}' target renderer has no mesh.";
                return false;
            }

            if (TryResolveMeshApplierMappingReference(applier, out _))
            {
                return true;
            }

            var sourceFbx = ResolveSourceFbx(applier.Patch, targetMesh);
            if (sourceFbx == null)
            {
                diagnostic = $"BlendShare mesh applier '{applier.name}' cannot create a mapping because no source FBX is available.";
                return false;
            }

            var failures = new List<string>();
            bool createdOrFoundAll = true;
            var pairs = GetBlendShareMeshPairsForApplier(applier).ToArray();
            if (pairs.Length == 0)
            {
                diagnostic = $"BlendShare mesh applier '{applier.name}' references mesh data that is not present in its patch.";
                return false;
            }

            foreach (var pair in pairs)
            {
                if (BlendShareVertexMappingCacheService.TryGetInvalidDiagnostic(
                        sourceFbx,
                        pair.MeshData,
                        targetMesh,
                        out string cachedInvalidDiagnostic))
                {
                    createdOrFoundAll = false;
                    failures.Add($"{pair.Patch.name}/{pair.MeshData.m_Path}: {cachedInvalidDiagnostic}");
                    continue;
                }

                var mapping = UnityFbxVertexMappingBuilder.BuildFromFbx(pair.MeshData.m_Path, targetMesh, sourceFbx);
                if (mapping == null || !mapping.m_IsValid)
                {
                    createdOrFoundAll = false;
                    string invalidReason = mapping?.m_Report ?? "mapping generation failed";
                    failures.Add($"{pair.Patch.name}/{pair.MeshData.m_Path}: {invalidReason}");
                    if (mapping != null)
                    {
                        BlendShareVertexMappingCacheService.Store(sourceFbx, pair.MeshData, targetMesh, mapping);
                    }
                    else
                    {
                        BlendShareVertexMappingCacheService.StoreInvalid(sourceFbx, pair.MeshData, targetMesh, invalidReason);
                    }

                    DestroyTransientMapping(mapping);
                    continue;
                }

                BlendShareVertexMappingCacheService.Store(sourceFbx, pair.MeshData, targetMesh, mapping);
                DestroyTransientMapping(mapping);

                if (!TryResolveMeshApplierMappingReference(applier, out _))
                {
                    createdOrFoundAll = false;
                    failures.Add($"{pair.Patch.name}/{pair.MeshData.m_Path}: generated mapping cache could not be loaded");
                }
            }

            if (!createdOrFoundAll)
            {
                diagnostic = $"BlendShare mesh applier '{applier.name}' failed to create mapping cache: {string.Join("; ", failures)}";
            }

            return createdOrFoundAll && TryResolveMeshApplierMappingReference(applier, out _);
        }

        private static void DestroyTransientMapping(UnityVertexMappingObject mapping)
        {
            if (mapping != null && !AssetDatabase.Contains(mapping))
            {
                UnityEngine.Object.DestroyImmediate(mapping);
            }
        }

        private static bool IsUsableMappingReference(
            BlendShareMesh applier,
            UnityVertexMappingObject mapping,
            Mesh targetMesh)
        {
            if (!UnityMeshEditorDataUtility.IsMappingCompatible(mapping, applier?.MeshData, targetMesh))
            {
                return false;
            }

            if (AssetDatabase.Contains(mapping))
            {
                return (applier.MeshData.m_Mappings ?? Array.Empty<UnityVertexMappingObject>()).Contains(mapping);
            }

            var sourceFbx = ResolveSourceFbx(applier.Patch, targetMesh);
            return BlendShareVertexMappingCacheService.IsCurrentMapping(
                sourceFbx,
                applier.MeshData,
                targetMesh,
                mapping);
        }

        private static void AssignMapping(BlendShareMesh applier, UnityVertexMappingObject mapping)
        {
            if (applier == null || applier.Mapping == mapping)
            {
                return;
            }

            BlendShareVertexMappingCacheService.ReleaseMapping(applier.Mapping);
            applier.Mapping = mapping;
            EditorUtility.SetDirty(applier);
        }

        private static IEnumerable<(BlendShareObject Patch, MeshDataObject MeshData)> GetBlendShareMeshPairsForApplier(
            BlendShareMesh applier)
        {
            if (applier?.Patch == null || applier.MeshData == null)
            {
                yield break;
            }

            if ((applier.Patch.Meshes ?? Array.Empty<MeshDataObject>()).Contains(applier.MeshData))
            {
                yield return (applier.Patch, applier.MeshData);
            }
        }

        public static GameObject ResolveSourceFbx(BlendShareObject patch, Mesh targetMesh)
        {
            if (targetMesh != null)
            {
                string meshPath = AssetDatabase.GetAssetPath(targetMesh);
                if (!string.IsNullOrEmpty(meshPath))
                {
                    var targetAssetRoot = AssetDatabase.LoadMainAssetAtPath(meshPath) as GameObject;
                    if (targetAssetRoot != null)
                    {
                        return targetAssetRoot;
                    }
                }
            }

            return patch != null ? patch.m_Target : null;
        }

        private static BlendShareMesh CreateMeshApplier(
            Transform placementParent,
            string rendererPath,
            string rendererName)
        {
            string desiredName = rendererPath == MeshNodePath.Root
                ? rendererName
                : MeshNodePath.LeafName(rendererPath);
            var host = new GameObject(CreateUniqueChildName(placementParent, desiredName));
            Undo.RegisterCreatedObjectUndo(host, "Create BlendShare Mesh Binding");
            host.transform.SetParent(placementParent, false);
            return Undo.AddComponent<BlendShareMesh>(host);
        }

        private static BlendShareBoneProxy CreateBoneProxy(
            Transform placementParent,
            string sourceBonePath,
            Transform parent)
        {
            string desiredName = MeshNodePath.LeafName(sourceBonePath);
            var host = new GameObject(string.IsNullOrWhiteSpace(desiredName) ? "BlendShareBone" : desiredName);
            Undo.RegisterCreatedObjectUndo(host, "Create BlendShare Bone Proxy");
            host.transform.SetParent(placementParent, false);
            var proxy = Undo.AddComponent<BlendShareBoneProxy>(host);
            proxy.TargetParent = parent;
            return proxy;
        }

        private static Transform ResolveProxyParent(
            ArmatureBoneData bone,
            ArmatureObject armature,
            Transform targetRoot,
            IReadOnlyDictionary<string, Transform> transformsByPath,
            IReadOnlyDictionary<BlendShareBoneProxyLookup.SourceKey, BlendShareBoneProxy> proxiesByBonePath)
        {
            string parentPath = bone?.ParentPath ?? MeshNodePath.Root;
            while (parentPath != MeshNodePath.Root)
            {
                if (proxiesByBonePath.TryGetValue(
                        new BlendShareBoneProxyLookup.SourceKey(armature, parentPath),
                        out var proxyParent) && proxyParent != null)
                {
                    return proxyParent.transform;
                }

                if (transformsByPath.TryGetValue(parentPath, out var parent) && parent != null)
                {
                    return parent;
                }

                var parentBone = armature != null ? armature.GetBone(parentPath) : null;
                if (parentBone == null)
                {
                    break;
                }

                parentPath = parentBone.ParentPath;
            }

            return targetRoot;
        }

        private static string BuildProxyKey(
            Transform parent,
            string boneName,
            Vector3 localPosition,
            Vector3 localEulerRotation,
            Vector3 localScale)
        {
            string parentId = parent != null ? parent.GetInstanceID().ToString() : "0";
            return string.Join("|",
                parentId,
                boneName ?? string.Empty,
                Quantize(localPosition.x), Quantize(localPosition.y), Quantize(localPosition.z),
                Quantize(localEulerRotation.x), Quantize(localEulerRotation.y), Quantize(localEulerRotation.z),
                Quantize(localScale.x), Quantize(localScale.y), Quantize(localScale.z));
        }

        private static string Quantize(float value)
        {
            return Mathf.RoundToInt(value * 10000f).ToString();
        }

        private static string CreateUniqueChildName(Transform parent, string desiredName)
        {
            desiredName = string.IsNullOrWhiteSpace(desiredName) || desiredName == MeshNodePath.Root
                ? "BlendShareBone"
                : desiredName;
            if (parent == null || parent.Find(desiredName) == null)
            {
                return desiredName;
            }

            string baseName = $"{desiredName} BlendShare";
            if (parent.Find(baseName) == null)
            {
                return baseName;
            }

            for (int i = 1; i < 10000; i++)
            {
                string candidate = $"{baseName} {i}";
                if (parent.Find(candidate) == null)
                {
                    return candidate;
                }
            }

            return $"{baseName} {Guid.NewGuid():N}";
        }
    }

    internal sealed class CreateBlendShareSetupResult
    {
        private readonly List<string> diagnostics = new();
        private readonly List<BlendShareMesh> meshAppliers = new();
        private readonly List<BlendShareBoneProxy> boneProxies = new();

        public IReadOnlyList<string> Diagnostics => diagnostics;
        public IReadOnlyList<BlendShareMesh> MeshAppliers => meshAppliers;
        public IReadOnlyList<BlendShareBoneProxy> BoneProxies => boneProxies;
        public bool Success => diagnostics.Count == 0;

        internal void AddDiagnostic(string diagnostic)
        {
            if (!string.IsNullOrWhiteSpace(diagnostic))
            {
                diagnostics.Add(diagnostic);
            }
        }

        internal void AddMeshApplier(BlendShareMesh applier)
        {
            if (applier != null && !meshAppliers.Contains(applier))
            {
                meshAppliers.Add(applier);
            }
        }

        internal void AddBoneProxy(BlendShareBoneProxy proxy)
        {
            if (proxy != null && !boneProxies.Contains(proxy))
            {
                boneProxies.Add(proxy);
            }
        }
    }

    internal sealed class RebuildBoneProxiesResult
    {
        private readonly List<string> diagnostics = new();
        private readonly List<BlendShareBoneProxy> boneProxies = new();

        public IReadOnlyList<string> Diagnostics => diagnostics;
        public IReadOnlyList<BlendShareBoneProxy> BoneProxies => boneProxies;
        public bool Success => diagnostics.Count == 0;

        internal void AddDiagnostic(string diagnostic)
        {
            if (!string.IsNullOrWhiteSpace(diagnostic))
            {
                diagnostics.Add(diagnostic);
            }
        }

        internal void AddBoneProxy(BlendShareBoneProxy proxy)
        {
            if (proxy != null && !boneProxies.Contains(proxy))
            {
                boneProxies.Add(proxy);
            }
        }
    }
}
