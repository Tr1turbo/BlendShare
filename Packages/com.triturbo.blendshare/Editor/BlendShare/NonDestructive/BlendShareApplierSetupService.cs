using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Components;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Features.BoneGraph;
using Triturbo.BlendShare.Features.SkinWeights;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.NonDestructive
{
    public static class BlendShareApplierSetupService
    {
        public static RebuildMeshBindingsResult RebuildMeshBindings(BlendShareApplierComponent owner)
        {
            var result = new RebuildMeshBindingsResult();
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

            var renderersByPath = targetRoot
                .GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .GroupBy(renderer => MeshNodePath.Normalize(MeshNodePath.GetRelativePath(renderer.transform, targetRoot)))
                .ToDictionary(group => group.Key, group => group.First());
            var existingByPath = FindOwnedMeshAppliers(owner)
                .GroupBy(applier => MeshNodePath.Normalize(applier.RendererNodePath))
                .ToDictionary(group => group.Key, group => group.ToList());
            var matchedPaths = new HashSet<string>();
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

                matchedPaths.Add(path);
                var applier = existingByPath.TryGetValue(path, out var current) && current.Count > 0
                    ? TakeFirst(current)
                    : CreateMeshApplier(owner, renderer, path);

                Undo.RecordObject(applier, "Rebuild BlendShare Mesh Binding");
                applier.Owner = owner;
                applier.TargetRenderer = renderer;
                applier.MeshData = meshData;
                applier.RendererNodePath = path;
                applier.EnabledForBuild = true;
                applier.SetDiagnostic(false, string.Empty);
                EditorUtility.SetDirty(applier);
                result.AddMeshApplier(applier);
            }

            foreach (var stale in existingByPath.SelectMany(pair => pair.Value)
                         .Where(applier => applier != null && !matchedPaths.Contains(applier.RendererNodePath)))
            {
                Undo.RecordObject(stale, "Mark BlendShare Mesh Binding Stale");
                stale.SetDiagnostic(true, "This binding no longer matches the current target root and BlendShare object list.");
                EditorUtility.SetDirty(stale);
                result.AddStaleMeshApplier(stale);
            }

            return result;
        }

        public static RebuildBoneProxiesResult RebuildBoneProxies(BlendShareApplierComponent owner)
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
                         .Where(applier => applier != null && applier.EnabledForBuild && !applier.IsStale))
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

                    float fbxToUnityScale = GetFbxToUnityScale(meshData);

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

        public static Transform ResolveTargetRoot(BlendShareApplierComponent owner)
        {
            if (owner == null)
            {
                return null;
            }

            return owner.TargetRoot != null ? owner.TargetRoot : owner.transform;
        }

        private static float GetFbxToUnityScale(MeshDataObject meshData)
        {
            var mapping = (meshData?.m_Mappings ?? Array.Empty<UnityVertexMappingObject>())
                .FirstOrDefault(candidate => candidate != null && candidate.m_IsValid);
            return mapping != null ? mapping.FbxToUnityScale : 1f;
        }

        private static IEnumerable<MeshDataObject> GetMeshDataForApplier(BlendShareMeshApplierComponent meshApplier)
        {
            if (meshApplier?.Owner == null)
            {
                yield break;
            }

            string rendererPath = MeshNodePath.Normalize(meshApplier.RendererNodePath);
            var seen = new HashSet<MeshDataObject>();
            foreach (var share in meshApplier.Owner.BlendShares ?? Array.Empty<BlendShareObject>())
            {
                foreach (var meshData in share?.Meshes ?? Array.Empty<MeshDataObject>())
                {
                    if (meshData != null &&
                        MeshNodePath.Normalize(meshData.m_Path) == rendererPath &&
                        seen.Add(meshData))
                    {
                        yield return meshData;
                    }
                }
            }
        }

        public static bool ValidateMeshApplierForBuild(
            BlendShareMeshApplierComponent applier,
            out string diagnostic)
        {
            diagnostic = null;
            if (applier == null)
            {
                diagnostic = "BlendShare mesh applier is missing.";
                return false;
            }

            if (applier.IsStale)
            {
                diagnostic = string.IsNullOrWhiteSpace(applier.DiagnosticMessage)
                    ? $"BlendShare mesh applier '{applier.name}' is stale."
                    : applier.DiagnosticMessage;
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

            Transform targetRoot = ResolveTargetRoot(applier.Owner);
            string actualPath = MeshNodePath.Normalize(MeshNodePath.GetRelativePath(applier.TargetRenderer.transform, targetRoot));
            if (actualPath != applier.RendererNodePath)
            {
                diagnostic = $"BlendShare mesh applier '{applier.name}' targets renderer path '{actualPath}', expected '{applier.RendererNodePath}'.";
                return false;
            }

            return true;
        }

        public static bool ValidateMeshApplierMapping(
            BlendShareMeshApplierComponent applier,
            out string diagnostic)
        {
            diagnostic = null;
            if (applier == null || !applier.EnabledForBuild || applier.IsStale)
            {
                return true;
            }

            var renderer = applier.TargetRenderer;
            if (renderer == null || applier.MeshData == null)
            {
                return true;
            }

            var targetMesh = renderer.sharedMesh;
            if (targetMesh == null)
            {
                diagnostic = $"BlendShare mesh applier '{applier.name}' target renderer has no mesh for mapping compatibility check.";
                return false;
            }

            bool hasValidMapping = (applier.MeshData.m_Mappings ?? Array.Empty<UnityVertexMappingObject>())
                .Any(mapping => mapping != null && mapping.IsValidFor(targetMesh));
            if (hasValidMapping)
            {
                return true;
            }

            diagnostic = $"BlendShare mesh applier '{applier.name}' does not have a valid Unity vertex mapping for renderer path '{applier.RendererNodePath}'. ND build/preview may fail or require a slow FBX fallback.";
            return false;
        }

        public static BlendShareMeshApplierComponent[] FindOwnedMeshAppliers(BlendShareApplierComponent owner)
        {
            if (owner == null)
            {
                return Array.Empty<BlendShareMeshApplierComponent>();
            }

            return UnityEngine.Object.FindObjectsOfType<BlendShareMeshApplierComponent>(true)
                .Where(applier => applier != null &&
                                  applier.Owner == owner &&
                                  applier.gameObject.scene == owner.gameObject.scene)
                .ToArray();
        }

        public static BlendShareBoneProxyComponent[] FindOwnedBoneProxies(BlendShareApplierComponent owner)
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

        private static BlendShareMeshApplierComponent CreateMeshApplier(
            BlendShareApplierComponent owner,
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
                host = new GameObject($"BlendShareMeshApplier: {MeshNodePath.LeafName(rendererPath)}");
                Undo.RegisterCreatedObjectUndo(host, "Create BlendShare Mesh Binding");
                host.transform.SetParent(owner.transform, false);
            }

            var applier = host.GetComponents<BlendShareMeshApplierComponent>()
                .FirstOrDefault(component => component.Owner == owner || component.Owner == null);
            if (applier == null)
            {
                applier = Undo.AddComponent<BlendShareMeshApplierComponent>(host);
            }

            return applier;
        }

        private static BlendShareBoneProxyComponent CreateBoneProxy(
            BlendShareApplierComponent owner,
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

        private static BlendShareMeshApplierComponent TakeFirst(List<BlendShareMeshApplierComponent> appliers)
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
        private readonly List<BlendShareMeshApplierComponent> meshAppliers = new();
        private readonly List<BlendShareMeshApplierComponent> staleMeshAppliers = new();

        public IReadOnlyList<string> Diagnostics => diagnostics;
        public IReadOnlyList<BlendShareMeshApplierComponent> MeshAppliers => meshAppliers;
        public IReadOnlyList<BlendShareMeshApplierComponent> StaleMeshAppliers => staleMeshAppliers;
        public bool Success => diagnostics.Count == 0;

        internal void AddDiagnostic(string diagnostic)
        {
            if (!string.IsNullOrWhiteSpace(diagnostic))
            {
                diagnostics.Add(diagnostic);
            }
        }

        internal void AddMeshApplier(BlendShareMeshApplierComponent applier)
        {
            if (applier != null && !meshAppliers.Contains(applier))
            {
                meshAppliers.Add(applier);
            }
        }

        internal void AddStaleMeshApplier(BlendShareMeshApplierComponent applier)
        {
            if (applier != null && !staleMeshAppliers.Contains(applier))
            {
                staleMeshAppliers.Add(applier);
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
