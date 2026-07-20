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
        private const string BoneProxyContainerName = "Armature";

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
            var existingBindingsBySource = BuildBindingsBySource(existingProxies);
            var proxiesByKey = existingProxies
                .GroupBy(proxy => BuildProxyKey(proxy.EffectiveParent, proxy.name, proxy.LocalPosition, proxy.LocalEulerRotation, proxy.LocalScale))
                .ToDictionary(group => group.Key, group => group.ToList());
            var proxiesByName = existingProxies
                .GroupBy(proxy => proxy.name)
                .ToDictionary(group => group.Key, group => group.ToList());
            var usedProxies = new HashSet<BlendShareBoneProxy>();
            var proxiesByBonePath = new Dictionary<SourceKey, ResolvedBinding>();
            var boneProxyPlacementParent = placementParent != null ? placementParent : targetRoot;
            Transform boneProxyContainer = FindBoneProxyContainer(boneProxyPlacementParent);
            MoveBoneProxyContainerFirst(boneProxyContainer);

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
                        bool useHierarchyParent = parent != null && proxiesByBonePath.Values.Any(candidate =>
                            candidate.FinalTransform == parent);
                        if (mapping == null)
                        {
                            result.AddDiagnostic($"Cannot create proxy for bone '{bonePath}': missing FBX-to-Unity conversion.");
                            continue;
                        }

                        if (!bone.HasTransformData)
                        {
                            result.AddDiagnostic(
                                $"Cannot create proxy for bone '{bonePath}': missing FBX transform data. Re-extract the BlendShare patch.");
                            continue;
                        }

                        if (!mapping.SpaceConversion.TryConvertLocalTransform(
                                bone.EvaluatedNodeToParentMatrix,
                                out var localTransform,
                                out string transformDiagnostic))
                        {
                            result.AddDiagnostic($"Cannot create proxy for bone '{bonePath}': {transformDiagnostic}");
                            continue;
                        }

                        var localPosition = localTransform.Position;
                        var localEulerRotation = localTransform.Rotation.eulerAngles;
                        var localScale = localTransform.Scale;
                        string desiredName = MeshNodePath.LeafName(bonePath);
                        string key = BuildProxyKey(parent, desiredName, localPosition, localEulerRotation, localScale);
                        var sourceKey = new SourceKey(skin.Armature, bonePath);
                        bool createdProxy = false;
                        BlendShareBoneProxy proxy;
                        BlendShareBoneProxyBinding binding;
                        Transform finalTransform;
                        if (proxiesByBonePath.TryGetValue(sourceKey, out var resolved) ||
                            existingBindingsBySource.TryGetValue(sourceKey, out resolved))
                        {
                            proxy = resolved.Component;
                            binding = resolved.Binding;
                            finalTransform = resolved.FinalTransform;
                        }
                        else if (TryClaimUninitializedProxy(
                                     proxiesByKey,
                                     key,
                                     skin.Armature,
                                     bonePath,
                                     usedProxies,
                                     out proxy,
                                     out binding) ||
                                 TryClaimUninitializedProxy(
                                     proxiesByName,
                                     desiredName,
                                     skin.Armature,
                                     bonePath,
                                     usedProxies,
                                     out proxy,
                                     out binding))
                        {
                            finalTransform = proxy.transform;
                        }
                        else
                        {
                            var authoringParent = useHierarchyParent
                                ? parent
                                : (boneProxyContainer ??= GetOrCreateBoneProxyContainer(
                                    boneProxyPlacementParent));
                            proxy = CreateBoneProxy(
                                authoringParent,
                                skin.Armature,
                                bonePath,
                                useHierarchyParent ? null : parent);
                            binding = proxy.Bindings[0];
                            finalTransform = proxy.transform;
                            createdProxy = true;
                        }

                        if (proxy == null || binding == null || finalTransform == null)
                        {
                            result.AddDiagnostic($"Bone proxy binding '{bonePath}' has no final transform.");
                            continue;
                        }

                        usedProxies.Add(proxy);
                        Undo.RecordObject(proxy, "Rebuild BlendShare Bone Proxy");
                        Undo.RecordObject(finalTransform, "Rebuild BlendShare Bone Proxy");
                        if (useHierarchyParent && finalTransform.parent != parent)
                        {
                            Undo.SetTransformParent(finalTransform, parent, "Rebuild BlendShare Bone Proxy Hierarchy");
                        }
                        proxy.SourceArmature = skin.Armature;
                        proxy.UpdateBinding(binding, bonePath, finalTransform);
                        if (proxy.IsRootBinding(binding))
                        {
                            proxy.UseHierarchyParent = useHierarchyParent;
                            proxy.TargetParent = useHierarchyParent ? null : parent;
                        }

                        if (createdProxy || !proxy.RecalculateBindpose)
                        {
                            if (!proxy.HasExplicitBindings && finalTransform == proxy.transform)
                            {
                                proxy.LocalPosition = localPosition;
                                proxy.LocalEulerRotation = localEulerRotation;
                                proxy.LocalScale = localScale;
                                proxy.ResetTransformToBindPose();
                            }
                            else if (useHierarchyParent)
                            {
                                finalTransform.localPosition = localPosition;
                                finalTransform.localRotation = Quaternion.Euler(localEulerRotation);
                                finalTransform.localScale = localScale;
                            }
                        }
                        EditorUtility.SetDirty(proxy);
                        EditorUtility.SetDirty(finalTransform);
                        result.AddBoneProxy(proxy);
                        proxiesByBonePath[sourceKey] = new ResolvedBinding(proxy, binding);
                    }
                }
            }

            ConsolidateGeneratedProxySubtrees(proxiesByBonePath.Values, usedProxies, result);
            BlendShareMesh.RefreshBoneProxyCaches(
                appliers,
                targetRoot,
                targetRoot.GetComponentsInChildren<BlendShareBoneProxy>(true));
            return result;
        }

        private static void ConsolidateGeneratedProxySubtrees(
            IEnumerable<ResolvedBinding> resolvedBindings,
            ISet<BlendShareBoneProxy> usedProxies,
            RebuildBoneProxiesResult result)
        {
            var bindings = (resolvedBindings ?? Array.Empty<ResolvedBinding>())
                .Where(resolved => resolved.Component != null && resolved.Binding != null && resolved.FinalTransform != null)
                .ToArray();
            foreach (var armatureGroup in bindings.GroupBy(resolved => resolved.Component.SourceArmature))
            {
                var finalTransforms = armatureGroup.Select(resolved => resolved.FinalTransform).ToHashSet();
                foreach (var root in armatureGroup.Where(resolved => !finalTransforms.Contains(resolved.FinalTransform.parent)))
                {
                    var subtree = armatureGroup
                        .Where(resolved => resolved.FinalTransform == root.FinalTransform ||
                                           resolved.FinalTransform.IsChildOf(root.FinalTransform))
                        .OrderBy(resolved => GetHierarchyOrder(resolved.FinalTransform), StringComparer.Ordinal)
                        .ToArray();
                    if (subtree.Length <= 1 || subtree.Any(resolved =>
                            !usedProxies.Contains(resolved.Component) || !IsConvertibleFlatProxy(resolved)))
                    {
                        continue;
                    }

                    var holder = root.Component;
                    Undo.RecordObject(holder, "Consolidate BlendShare Bone Proxy Subtree");
                    holder.SetBindings(subtree.Select(resolved =>
                        new BlendShareBoneProxyBinding(resolved.SourceBonePath, resolved.FinalTransform)));
                    EditorUtility.SetDirty(holder);
                    foreach (var descendant in subtree.Select(resolved => resolved.Component).Distinct())
                    {
                        if (descendant == holder)
                        {
                            continue;
                        }

                        usedProxies.Remove(descendant);
                        result.RemoveBoneProxy(descendant);
                        Undo.DestroyObjectImmediate(descendant);
                    }

                    result.AddBoneProxy(holder);
                }
            }
        }

        private static bool IsConvertibleFlatProxy(ResolvedBinding resolved)
        {
            var component = resolved.Component;
            return component != null &&
                   !component.HasExplicitBindings &&
                   resolved.FinalTransform == component.transform &&
                   component.gameObject.GetComponents<Component>().All(candidate =>
                       candidate is Transform || candidate == component);
        }

        private static bool TryClaimUninitializedProxy(
            IReadOnlyDictionary<string, List<BlendShareBoneProxy>> proxies,
            string key,
            FbxArmatureObject sourceArmature,
            string sourceBonePath,
            ISet<BlendShareBoneProxy> usedProxies,
            out BlendShareBoneProxy proxy,
            out BlendShareBoneProxyBinding binding)
        {
            proxy = null;
            binding = null;
            if (string.IsNullOrWhiteSpace(key) ||
                proxies == null ||
                !proxies.TryGetValue(key, out var candidates))
            {
                return false;
            }

            proxy = candidates.FirstOrDefault(candidate =>
                candidate != null &&
                candidate.isActiveAndEnabled &&
                !candidate.HasExplicitBindings &&
                (candidate.SourceArmature == null || candidate.SourceArmature == sourceArmature) &&
                string.IsNullOrEmpty(candidate.SourceBonePath) &&
                !usedProxies.Contains(candidate));
            if (proxy == null)
            {
                return false;
            }

            Undo.RecordObject(proxy, "Repair BlendShare Bone Proxy Binding");
            InitializeSingleBindingProxy(proxy, sourceArmature, sourceBonePath);
            binding = proxy.Bindings[0];
            return true;
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
            FbxArmatureObject sourceArmature,
            string sourceBonePath,
            Transform parent)
        {
            string desiredName = MeshNodePath.LeafName(sourceBonePath);
            var host = new GameObject(string.IsNullOrWhiteSpace(desiredName) ? "BlendShareBone" : desiredName);
            Undo.RegisterCreatedObjectUndo(host, "Create BlendShare Bone Proxy");
            host.transform.SetParent(placementParent, false);
            var proxy = Undo.AddComponent<BlendShareBoneProxy>(host);
            InitializeSingleBindingProxy(proxy, sourceArmature, sourceBonePath);
            proxy.TargetParent = parent;
            return proxy;
        }

        private static void InitializeSingleBindingProxy(
            BlendShareBoneProxy proxy,
            FbxArmatureObject sourceArmature,
            string sourceBonePath)
        {
            proxy.SetLegacySourceBinding(sourceArmature, sourceBonePath);
        }

        private static Transform GetOrCreateBoneProxyContainer(Transform placementParent)
        {
            var existing = FindBoneProxyContainer(placementParent);
            if (existing != null)
            {
                MoveBoneProxyContainerFirst(existing);
                return existing;
            }

            var container = new GameObject(BoneProxyContainerName);
            Undo.RegisterCreatedObjectUndo(container, "Create BlendShare Bone Proxy Container");
            container.transform.SetParent(placementParent, false);
            container.transform.SetAsFirstSibling();
            return container.transform;
        }

        private static Transform FindBoneProxyContainer(Transform placementParent)
        {
            return Enumerable.Range(0, placementParent.childCount)
                .Select(placementParent.GetChild)
                .FirstOrDefault(child =>
                    child.name == BoneProxyContainerName &&
                    child.gameObject.GetComponents<Component>().All(component => component is Transform) &&
                    (child.childCount == 0 || child.GetComponentInChildren<BlendShareBoneProxy>(true) != null));
        }

        private static void MoveBoneProxyContainerFirst(Transform container)
        {
            if (container != null && container.GetSiblingIndex() != 0)
            {
                Undo.RecordObject(container, "Move BlendShare Bone Proxy Container");
                container.SetAsFirstSibling();
                EditorUtility.SetDirty(container);
            }
        }

        private static Transform ResolveProxyParent(
            FbxArmatureBoneData bone,
            FbxArmatureObject armature,
            Transform targetRoot,
            IReadOnlyDictionary<string, Transform> transformsByPath,
            IReadOnlyDictionary<SourceKey, ResolvedBinding> proxiesByBonePath)
        {
            string parentPath = bone?.ParentPath ?? MeshNodePath.Root;
            while (parentPath != MeshNodePath.Root)
            {
                if (proxiesByBonePath.TryGetValue(
                        new SourceKey(armature, parentPath),
                        out var proxyParent) && proxyParent.FinalTransform != null)
                {
                    return proxyParent.FinalTransform;
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

        private static Dictionary<SourceKey, ResolvedBinding> BuildBindingsBySource(
            IEnumerable<BlendShareBoneProxy> proxies)
        {
            var result = new Dictionary<SourceKey, ResolvedBinding>();
            foreach (var proxy in (proxies ?? Enumerable.Empty<BlendShareBoneProxy>())
                         .Where(proxy => proxy != null && proxy.isActiveAndEnabled && proxy.SourceArmature != null)
                         .Distinct()
                         .OrderBy(proxy => GetHierarchyOrder(proxy.transform), StringComparer.Ordinal))
            {
                foreach (var binding in proxy.Bindings.Where(binding => binding?.IsConfigured == true))
                {
                    result[new SourceKey(proxy.SourceArmature, binding.SourceBonePath)] =
                        new ResolvedBinding(proxy, binding);
                }
            }

            return result;
        }

        private readonly struct SourceKey : IEquatable<SourceKey>
        {
            private readonly FbxArmatureObject armature;
            private readonly string sourceBonePath;

            public SourceKey(FbxArmatureObject armature, string sourceBonePath)
            {
                this.armature = armature;
                this.sourceBonePath = MeshNodePath.NormalizeOptional(sourceBonePath);
            }

            public bool Equals(SourceKey other)
            {
                return ReferenceEquals(armature, other.armature) && sourceBonePath == other.sourceBonePath;
            }

            public override bool Equals(object obj)
            {
                return obj is SourceKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return ((armature != null ? armature.GetInstanceID() : 0) * 397) ^
                           (sourceBonePath != null ? sourceBonePath.GetHashCode() : 0);
                }
            }
        }

        private readonly struct ResolvedBinding
        {
            public ResolvedBinding(BlendShareBoneProxy component, BlendShareBoneProxyBinding binding)
            {
                Component = component;
                Binding = binding;
            }

            public BlendShareBoneProxy Component { get; }
            public BlendShareBoneProxyBinding Binding { get; }
            public Transform FinalTransform => Binding?.Transform;
            public string SourceBonePath => Binding?.SourceBonePath;
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

        private static string GetHierarchyOrder(Transform transform)
        {
            var indices = new Stack<string>();
            for (var current = transform; current != null; current = current.parent)
            {
                indices.Push(current.GetSiblingIndex().ToString("D8"));
            }

            return string.Join("/", indices);
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

        internal void RemoveBoneProxy(BlendShareBoneProxy proxy)
        {
            boneProxies.Remove(proxy);
        }
    }
}
