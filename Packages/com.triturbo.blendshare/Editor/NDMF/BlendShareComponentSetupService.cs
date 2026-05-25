using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Components;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Features.BoneGraph;
using Triturbo.BlendShare.Features.SkinWeights;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.NDMF
{
    public static class BlendShareComponentSetupService
    {
        public static RebuildMeshBindingsResult RebuildMeshBindings(BlendShareComponent owner)
        {
            var result = new RebuildMeshBindingsResult();
            if (owner == null)
            {
                result.AddDiagnostic("BlendShare Core is missing.");
                return result;
            }

            Transform targetRoot = ResolveTargetRoot(owner);
            if (targetRoot == null)
            {
                result.AddDiagnostic("Target root is missing.");
                return result;
            }

            var renderersByPath = targetRoot
                .GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .GroupBy(renderer => MeshNodePath.Normalize(MeshNodePath.GetRelativePath(renderer.transform, targetRoot)))
                .ToDictionary(group => group.Key, group => group.First());
            var existingByPath = FindOwnedMeshAppliers(owner)
                .GroupBy(applier => MeshNodePath.Normalize(applier.RendererNodePath))
                .ToDictionary(group => group.Key, group => group.ToList());
            var desiredMeshes = owner.BlendShares
                .Where(share => share != null)
                .SelectMany(share => share.Meshes.Where(mesh => mesh != null))
                .GroupBy(mesh => MeshNodePath.Normalize(mesh.m_Path))
                .Select(group => group.First());

            foreach (var meshData in desiredMeshes)
            {
                string path = MeshNodePath.Normalize(meshData.m_Path);
                if (!renderersByPath.TryGetValue(path, out var renderer) || renderer == null)
                {
                    result.AddDiagnostic($"Renderer path '{path}' was not found under '{targetRoot.name}'.");
                    continue;
                }

                var applier = existingByPath.TryGetValue(path, out var current) && current.Count > 0
                    ? TakeFirst(current)
                    : CreateMeshApplier(owner, renderer, path);

                Undo.RecordObject(applier, "Rebuild BlendShare Mesh Binding");
                applier.Owner = owner;
                applier.TargetRenderer = renderer;
                applier.MeshData = meshData;
                applier.RendererNodePath = path;
                applier.EnabledForBuild = true;
                applier.DiagnosticMessage = string.Empty;
                EditorUtility.SetDirty(applier);
                result.AddMeshApplier(applier);
            }

            return result;
        }

        public static RebuildBoneProxiesResult RebuildBoneProxies(BlendShareComponent owner)
        {
            var result = new RebuildBoneProxiesResult();
            if (owner == null)
            {
                result.AddDiagnostic("BlendShare applier is missing.");
                return result;
            }

            Transform targetRoot = ResolveTargetRoot(owner);
            if (targetRoot == null)
            {
                result.AddDiagnostic("Target root is missing.");
                return result;
            }

            var transformsByPath = targetRoot
                .GetComponentsInChildren<Transform>(true)
                .GroupBy(transform => MeshNodePath.Normalize(MeshNodePath.GetRelativePath(transform, targetRoot)))
                .ToDictionary(group => group.Key, group => group.First());
            var existingProxies = FindOwnedBoneProxies(owner).ToList();
            var proxiesByKey = existingProxies
                .GroupBy(proxy => BuildProxyKey(proxy.TargetParent, proxy.LocalPosition, proxy.LocalEulerRotation, proxy.LocalScale))
                .ToDictionary(group => group.Key, group => group.ToList());
            var proxiesByName = existingProxies
                .GroupBy(proxy => proxy.name)
                .ToDictionary(group => group.Key, group => group.ToList());
            var updatedProxiesByKey = new Dictionary<string, BlendShareBoneProxyComponent>();
            var usedProxies = new HashSet<BlendShareBoneProxyComponent>();
            var proxiesByBonePath = new Dictionary<string, BlendShareBoneProxyComponent>();

            foreach (var meshApplier in FindOwnedMeshAppliers(owner)
                         .Where(applier => applier != null && applier.EnabledForBuild))
            {
                var renderer = meshApplier.TargetRenderer;
                if (renderer == null)
                {
                    continue;
                }

                var existingRendererBonePaths = new HashSet<string>((renderer.bones ?? Array.Empty<Transform>())
                    .Where(bone => bone != null)
                    .Select(bone => MeshNodePath.Normalize(MeshNodePath.GetRelativePath(bone, targetRoot))));
                var bindings = new List<BlendShareBoneProxyBinding>();

                foreach (var meshData in GetMeshDataForApplier(meshApplier))
                {
                    var skin = meshData != null ? meshData.GetFeature<SkinWeightFeatureObject>() : null;
                    if (skin?.m_BoneGraph == null)
                    {
                        continue;
                    }

                    float fbxToUnityScale = GetFbxToUnityScale(meshApplier, meshData);

                    foreach (string bonePath in GetNeededBonePathsInGraphOrder(skin))
                    {
                        if (existingRendererBonePaths.Contains(bonePath))
                        {
                            continue;
                        }

                        var bone = skin.m_BoneGraph.GetBone(bonePath);
                        if (bone == null || !bone.m_CreateIfMissing)
                        {
                            result.AddDiagnostic($"Bone '{bonePath}' is required but cannot be auto-created.");
                            continue;
                        }

                        var parent = ResolveProxyParent(bone, skin.m_BoneGraph, targetRoot, transformsByPath, proxiesByBonePath);
                        var localScale = bone.m_FbxLocalScale == Vector3.zero ? Vector3.one : bone.m_FbxLocalScale;
                        var localPosition = bone.m_FbxLocalTranslation * fbxToUnityScale;
                        string key = BuildProxyKey(parent, localPosition, bone.m_FbxLocalEulerRotation, localScale);
                        string desiredName = MeshNodePath.LeafName(bonePath);
                        bool createdProxy = false;
                        if (!updatedProxiesByKey.TryGetValue(key, out var proxy) &&
                            !TryTakeProxy(proxiesByKey, key, usedProxies, out proxy) &&
                            !TryTakeProxy(proxiesByName, desiredName, usedProxies, out proxy))
                        {
                            proxy = CreateBoneProxy(owner, bonePath, parent);
                            createdProxy = true;
                        }

                        updatedProxiesByKey[key] = proxy;
                        usedProxies.Add(proxy);
                        Undo.RecordObject(proxy, "Rebuild BlendShare Bone Proxy");
                        proxy.Owner = owner;
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
                        proxiesByBonePath[BuildBoneProxyBindingKey(skin.m_BoneGraph, bonePath)] = proxy;
                        bindings.Add(new BlendShareBoneProxyBinding
                        {
                            BoneGraph = skin.m_BoneGraph,
                            SourceBonePath = bonePath,
                            Proxy = proxy
                        });
                    }
                }

                Undo.RecordObject(meshApplier, "Rebuild BlendShare Bone Proxy Bindings");
                meshApplier.SetBoneProxyBindings(bindings);
                EditorUtility.SetDirty(meshApplier);
            }

            return result;
        }

        private static bool TryTakeProxy(
            IReadOnlyDictionary<string, List<BlendShareBoneProxyComponent>> proxies,
            string key,
            ISet<BlendShareBoneProxyComponent> usedProxies,
            out BlendShareBoneProxyComponent proxy)
        {
            proxy = null;
            if (string.IsNullOrWhiteSpace(key) ||
                proxies == null ||
                !proxies.TryGetValue(key, out var candidates))
            {
                return false;
            }

            proxy = candidates.FirstOrDefault(candidate => candidate != null && !usedProxies.Contains(candidate));
            return proxy != null;
        }

        public static Transform ResolveTargetRoot(BlendShareComponent owner)
        {
            if (owner == null)
            {
                return null;
            }

            return owner.TargetRoot != null ? owner.TargetRoot : owner.transform;
        }

        private static float GetFbxToUnityScale(BlendShareMeshComponent meshApplier, MeshDataObject meshData)
        {
            var targetMesh = meshApplier?.TargetRenderer != null ? meshApplier.TargetRenderer.sharedMesh : null;
            var mapping = targetMesh != null
                ? (meshData?.m_Mappings ?? Array.Empty<UnityVertexMappingObject>())
                    .FirstOrDefault(candidate => candidate != null && candidate.IsCompatibleWith(meshData, targetMesh))
                : (meshData?.m_Mappings ?? Array.Empty<UnityVertexMappingObject>())
                    .FirstOrDefault(candidate => candidate != null && candidate.m_IsValid);
            if (mapping == null && meshApplier != null && targetMesh != null)
            {
                BlendShareVertexMappingCacheService.TryGet(
                    FindBlendShareForMeshData(meshApplier.Owner, meshData),
                    meshData,
                    targetMesh,
                    out mapping);
            }

            return mapping != null ? mapping.FbxToUnityScale : 1f;
        }

        private static IEnumerable<MeshDataObject> GetMeshDataForApplier(BlendShareMeshComponent meshApplier)
        {
            if (meshApplier?.Owner == null || meshApplier.MeshData == null)
            {
                yield break;
            }

            if (FindBlendShareForMeshData(meshApplier.Owner, meshApplier.MeshData) != null)
            {
                yield return meshApplier.MeshData;
            }
        }

        public static bool ValidateMeshApplierForBuild(
            BlendShareMeshComponent applier,
            out string diagnostic)
        {
            diagnostic = null;
            if (applier == null)
            {
                diagnostic = "BlendShare mesh applier is missing.";
                return false;
            }

            if (applier.Owner == null)
            {
                diagnostic = $"BlendShare mesh applier '{applier.name}' has no owner.";
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

            if (FindBlendShareForMeshData(applier.Owner, applier.MeshData) == null)
            {
                diagnostic = $"BlendShare mesh applier '{applier.name}' references mesh data that is not present in its owner BlendShare list.";
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
            BlendShareMeshComponent applier,
            out string diagnostic)
        {
            diagnostic = null;
            if (applier == null || !applier.EnabledForBuild)
            {
                return true;
            }

            var renderer = applier.TargetRenderer;
            if (renderer == null || applier.Owner == null)
            {
                return true;
            }

            var targetMesh = renderer.sharedMesh;
            if (targetMesh == null)
            {
                diagnostic = $"BlendShare mesh applier '{applier.name}' target renderer has no mesh for mapping compatibility check.";
                return false;
            }

            var missing = GetBlendShareMeshPairsForApplier(applier)
                .Where(pair => !HasValidMapping(pair.Share, pair.MeshData, targetMesh))
                .ToArray();
            if (missing.Length == 0 && FindBlendShareForMeshData(applier.Owner, applier.MeshData) == null)
            {
                diagnostic = $"BlendShare mesh applier '{applier.name}' references mesh data that is not present in its owner BlendShare list.";
                return false;
            }

            if (missing.Length == 0)
            {
                return true;
            }

            diagnostic = $"BlendShare mesh applier '{applier.name}' does not have valid Unity vertex mappings for {missing.Length} BlendShare mesh request(s).";
            return false;
        }

        public static bool TryGetCachedInvalidMappingDiagnostic(
            BlendShareMeshComponent applier,
            out string diagnostic)
        {
            diagnostic = null;
            if (applier == null || applier.Owner == null || applier.MeshData == null || applier.TargetRenderer == null)
            {
                return false;
            }

            var targetMesh = applier.TargetRenderer.sharedMesh;
            if (targetMesh == null)
            {
                return false;
            }

            var share = FindBlendShareForMeshData(applier.Owner, applier.MeshData);
            if (share == null)
            {
                return false;
            }

            return BlendShareVertexMappingCacheService.TryGetInvalidDiagnostic(
                share,
                applier.MeshData,
                targetMesh,
                out diagnostic);
        }

        public static bool EnsureMeshApplierMappingCache(
            BlendShareMeshComponent applier,
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

            var sourceFbx = ResolveSourceFbx(applier.Owner, targetMesh);
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
                diagnostic = $"BlendShare mesh applier '{applier.name}' references mesh data that is not present in its owner BlendShare list.";
                return false;
            }

            foreach (var pair in pairs)
            {
                if (HasValidMapping(pair.Share, pair.MeshData, targetMesh))
                {
                    continue;
                }

                if (BlendShareVertexMappingCacheService.TryGetInvalidDiagnostic(
                        pair.Share,
                        pair.MeshData,
                        targetMesh,
                        out string cachedInvalidDiagnostic))
                {
                    createdOrFoundAll = false;
                    failures.Add($"{pair.Share.name}/{pair.MeshData.m_Path}: {cachedInvalidDiagnostic}");
                    continue;
                }

                var mapping = UnityFbxVertexMappingBuilder.BuildFromFbx(pair.MeshData.m_Path, targetMesh, sourceFbx);
                if (mapping == null || !mapping.m_IsValid)
                {
                    createdOrFoundAll = false;
                    string invalidReason = mapping?.m_InvalidReason ?? "mapping generation failed";
                    failures.Add($"{pair.Share.name}/{pair.MeshData.m_Path}: {invalidReason}");
                    if (mapping != null)
                    {
                        BlendShareVertexMappingCacheService.Store(pair.Share, pair.MeshData, mapping);
                    }
                    else
                    {
                        BlendShareVertexMappingCacheService.StoreInvalid(pair.Share, pair.MeshData, targetMesh, invalidReason);
                    }

                    DestroyTransientMapping(mapping);
                    continue;
                }

                BlendShareVertexMappingCacheService.Store(pair.Share, pair.MeshData, mapping);
                DestroyTransientMapping(mapping);
            }

            if (!createdOrFoundAll)
            {
                diagnostic = $"BlendShare mesh applier '{applier.name}' failed to create mapping cache: {string.Join("; ", failures)}";
            }

            return createdOrFoundAll;
        }

        private static void DestroyTransientMapping(UnityVertexMappingObject mapping)
        {
            if (mapping != null && !AssetDatabase.Contains(mapping))
            {
                UnityEngine.Object.DestroyImmediate(mapping);
            }
        }

        private static bool HasValidMapping(BlendShareObject blendShare, MeshDataObject meshData, Mesh targetMesh)
        {
            return (meshData?.m_Mappings ?? Array.Empty<UnityVertexMappingObject>())
                   .Any(mapping => mapping != null && mapping.IsCompatibleWith(meshData, targetMesh)) ||
                   BlendShareVertexMappingCacheService.ContainsCompatible(blendShare, meshData, targetMesh);
        }

        private static IEnumerable<(BlendShareObject Share, MeshDataObject MeshData)> GetBlendShareMeshPairsForApplier(
            BlendShareMeshComponent applier)
        {
            if (applier?.Owner == null || applier.MeshData == null)
            {
                yield break;
            }

            var share = FindBlendShareForMeshData(applier.Owner, applier.MeshData);
            if (share != null)
            {
                yield return (share, applier.MeshData);
            }
        }

        private static BlendShareObject FindBlendShareForMeshData(BlendShareComponent owner, MeshDataObject meshData)
        {
            return (owner?.BlendShares ?? Array.Empty<BlendShareObject>())
                .Where(share => share != null)
                .FirstOrDefault(share => (share.Meshes ?? Array.Empty<MeshDataObject>()).Contains(meshData));
        }

        private static GameObject ResolveSourceFbx(BlendShareComponent owner, Mesh targetMesh)
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

            return ResolveOriginalFbx(owner);
        }

        private static GameObject ResolveOriginalFbx(BlendShareComponent owner)
        {
            return (owner?.BlendShares ?? Array.Empty<BlendShareObject>())
                .Where(share => share != null)
                .Select(share => share.m_Original)
                .FirstOrDefault(original => original != null);
        }

        public static BlendShareMeshComponent[] FindOwnedMeshAppliers(BlendShareComponent owner)
        {
            if (owner == null)
            {
                return Array.Empty<BlendShareMeshComponent>();
            }

            return UnityEngine.Object.FindObjectsOfType<BlendShareMeshComponent>(true)
                .Where(applier => applier != null &&
                                  applier.Owner == owner &&
                                  applier.gameObject.scene == owner.gameObject.scene)
                .ToArray();
        }

        public static BlendShareBoneProxyComponent[] FindOwnedBoneProxies(BlendShareComponent owner)
        {
            if (owner == null)
            {
                return Array.Empty<BlendShareBoneProxyComponent>();
            }

            return UnityEngine.Object.FindObjectsOfType<BlendShareBoneProxyComponent>(true)
                .Where(proxy => proxy != null &&
                                proxy.Owner == owner &&
                                proxy.gameObject.scene == owner.gameObject.scene)
                .ToArray();
        }

        private static BlendShareMeshComponent CreateMeshApplier(
            BlendShareComponent owner,
            SkinnedMeshRenderer renderer,
            string rendererPath)
        {
            GameObject host;
            if (renderer.transform == owner.transform || renderer.transform.IsChildOf(owner.transform))
            {
                host = renderer.gameObject;
            }
            else
            {
                host = new GameObject($"{MeshNodePath.LeafName(rendererPath)}");
                Undo.RegisterCreatedObjectUndo(host, "Create BlendShare Mesh Binding");
                host.transform.SetParent(owner.transform, false);
            }

            var applier = host.GetComponents<BlendShareMeshComponent>()
                .FirstOrDefault(component => component.Owner == owner || component.Owner == null);
            if (applier == null)
            {
                applier = Undo.AddComponent<BlendShareMeshComponent>(host);
            }

            return applier;
        }

        private static BlendShareBoneProxyComponent CreateBoneProxy(
            BlendShareComponent owner,
            string sourceBonePath,
            Transform parent)
        {
            string desiredName = MeshNodePath.LeafName(sourceBonePath);
            var host = new GameObject(CreateUniqueChildName(owner.transform, desiredName));
            Undo.RegisterCreatedObjectUndo(host, "Create BlendShare Bone Proxy");
            host.transform.SetParent(owner.transform, false);
            var proxy = Undo.AddComponent<BlendShareBoneProxyComponent>(host);
            proxy.TargetParent = parent;
            return proxy;
        }

        private static IEnumerable<string> GetNeededBonePathsInGraphOrder(SkinWeightFeatureObject skin)
        {
            var needed = new HashSet<string>((skin.m_BonePaths ?? Array.Empty<string>()).Select(MeshNodePath.Normalize));
            foreach (var bone in skin.m_BoneGraph?.Bones ?? Array.Empty<BoneNodeData>())
            {
                if (bone == null)
                {
                    continue;
                }

                string path = MeshNodePath.Normalize(bone.m_Path);
                if (needed.Contains(path))
                {
                    yield return path;
                }
            }
        }

        private static Transform ResolveProxyParent(
            BoneNodeData bone,
            BoneGraphObject graph,
            Transform targetRoot,
            IReadOnlyDictionary<string, Transform> transformsByPath,
            IReadOnlyDictionary<string, BlendShareBoneProxyComponent> proxiesByBonePath)
        {
            string parentPath = MeshNodePath.Normalize(bone?.m_ParentPath);
            while (parentPath != MeshNodePath.Root)
            {
                if (proxiesByBonePath.TryGetValue(parentPath, out var proxyParent) && proxyParent != null)
                {
                    return proxyParent.transform;
                }

                if (proxiesByBonePath.TryGetValue(BuildBoneProxyBindingKey(graph, parentPath), out proxyParent) && proxyParent != null)
                {
                    return proxyParent.transform;
                }

                if (transformsByPath.TryGetValue(parentPath, out var parent) && parent != null)
                {
                    return parent;
                }

                var parentBone = graph != null ? graph.GetBone(parentPath) : null;
                if (parentBone == null)
                {
                    break;
                }

                parentPath = MeshNodePath.Normalize(parentBone.m_ParentPath);
            }

            return targetRoot;
        }

        private static string BuildBoneProxyBindingKey(BoneGraphObject graph, string sourceBonePath)
        {
            return $"{(graph != null ? graph.GetInstanceID() : 0)}:{MeshNodePath.Normalize(sourceBonePath)}";
        }

        private static string BuildProxyKey(Transform parent, Vector3 localPosition, Vector3 localEulerRotation, Vector3 localScale)
        {
            string parentId = parent != null ? parent.GetInstanceID().ToString() : "0";
            return string.Join("|",
                parentId,
                Quantize(localPosition.x), Quantize(localPosition.y), Quantize(localPosition.z),
                Quantize(localEulerRotation.x), Quantize(localEulerRotation.y), Quantize(localEulerRotation.z),
                Quantize(localScale.x), Quantize(localScale.y), Quantize(localScale.z));
        }

        private static BlendShareMeshComponent TakeFirst(List<BlendShareMeshComponent> appliers)
        {
            var first = appliers.FirstOrDefault();
            if (first != null)
            {
                appliers.Remove(first);
            }

            return first;
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

    public sealed class RebuildMeshBindingsResult
    {
        private readonly List<string> diagnostics = new();
        private readonly List<BlendShareMeshComponent> meshAppliers = new();

        public IReadOnlyList<string> Diagnostics => diagnostics;
        public IReadOnlyList<BlendShareMeshComponent> MeshAppliers => meshAppliers;
        public bool Success => diagnostics.Count == 0;

        internal void AddDiagnostic(string diagnostic)
        {
            if (!string.IsNullOrWhiteSpace(diagnostic))
            {
                diagnostics.Add(diagnostic);
            }
        }

        internal void AddMeshApplier(BlendShareMeshComponent applier)
        {
            if (applier != null && !meshAppliers.Contains(applier))
            {
                meshAppliers.Add(applier);
            }
        }
    }

    public sealed class RebuildBoneProxiesResult
    {
        private readonly List<string> diagnostics = new();
        private readonly List<BlendShareBoneProxyComponent> boneProxies = new();

        public IReadOnlyList<string> Diagnostics => diagnostics;
        public IReadOnlyList<BlendShareBoneProxyComponent> BoneProxies => boneProxies;
        public bool Success => diagnostics.Count == 0;

        internal void AddDiagnostic(string diagnostic)
        {
            if (!string.IsNullOrWhiteSpace(diagnostic))
            {
                diagnostics.Add(diagnostic);
            }
        }

        internal void AddBoneProxy(BlendShareBoneProxyComponent proxy)
        {
            if (proxy != null && !boneProxies.Contains(proxy))
            {
                boneProxies.Add(proxy);
            }
        }
    }
}
