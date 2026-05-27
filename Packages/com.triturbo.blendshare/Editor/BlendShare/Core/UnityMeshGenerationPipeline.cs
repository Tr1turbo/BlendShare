using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Triturbo.BlendShare.Components;
using Triturbo.BlendShare.Persistence;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Triturbo.BlendShare.Core
{
    /// <summary>
    /// Feature generator execution for Unity mesh artifacts.
    /// </summary>
    public sealed class UnityMeshGenerationPipeline
    {
        private static readonly IReadOnlyList<IMeshFeatureGenerator> FeatureGenerators =
            BlendShareFeatureModules.All
                .Select(module => module?.Generator)
                .Where(generator => generator != null)
                .OrderBy(generator => generator.Order)
                .ThenBy(generator => generator.GetType().FullName, StringComparer.Ordinal)
                .ToArray();

        public BlendShareArtifact CreateArtifact(
            Object targetMeshContainer,
            IEnumerable<BlendShareObject> blendShares,
            Func<BlendShareObject, MeshDataObject, bool> shouldGenerateMesh = null,
            IEnumerable<BlendShareObject> appliedBlendShares = null)
        {
            var shares = DedupBlendShares(blendShares).ToArray();
            if (targetMeshContainer == null || shares.Length == 0)
            {
                return null;
            }

            var targetLookup = UnityMeshTargetLookup.Create(targetMeshContainer);
            if (targetLookup == null)
            {
                return null;
            }

            var targetRoot = targetMeshContainer is GameObject gameObject ? gameObject.transform : null;
            var session = new UnityMeshGenerationSession(
                targetMeshContainer,
                shares,
                targetLookup,
                Array.Empty<BlendShareGenerationComponent>());
            var generatedByMeshKey = new Dictionary<string, Mesh>();

            foreach (var share in shares)
            {
                foreach (var meshData in share?.Meshes ?? Array.Empty<MeshDataObject>())
                {
                    if (meshData == null || (shouldGenerateMesh != null && !shouldGenerateMesh(share, meshData)))
                    {
                        continue;
                    }

                    GenerateMesh(
                        session,
                        targetLookup,
                        generatedByMeshKey,
                        targetRoot,
                        share,
                        meshData,
                        MeshNodePath.Normalize(meshData.m_Path));
                }
            }

            if (generatedByMeshKey.Count == 0)
            {
                return null;
            }

            return BuildArtifact(
                targetMeshContainer,
                targetRoot,
                appliedBlendShares ?? shares,
                generatedByMeshKey.Values,
                session);
        }

        public BlendShareArtifact CreateArtifactFromComponents(
            GameObject targetRoot,
            IEnumerable<BlendShareGenerationComponent> components,
            IEnumerable<BlendShareObject> appliedBlendShares = null)
        {
            if (targetRoot == null)
            {
                return null;
            }

            var componentList = (components ?? Array.Empty<BlendShareGenerationComponent>())
                .Where(component => component != null)
                .Distinct()
                .ToArray();
            var shares = DedupBlendShares(componentList
                .OfType<BlendShareMeshComponent>()
                .Where(IsUsableMeshComponent)
                .Select(component => FindBlendShareForMeshData(component.Owner, component.MeshData)))
                .ToArray();
            if (shares.Length == 0)
            {
                return null;
            }

            var targetLookup = UnityMeshTargetLookup.Create(targetRoot);
            if (targetLookup == null)
            {
                return null;
            }

            var session = new UnityMeshGenerationSession(targetRoot, shares, targetLookup, componentList);
            var generatedByMeshKey = new Dictionary<string, Mesh>();
            var emitted = new HashSet<string>();
            foreach (var meshComponent in componentList
                         .OfType<BlendShareMeshComponent>()
                         .Where(IsUsableMeshComponent)
                         .OrderBy(component => GetHierarchyOrder(component.Owner != null ? component.Owner.transform : null))
                         .ThenBy(component => GetHierarchyOrder(component.transform)))
            {
                var share = FindBlendShareForMeshData(meshComponent.Owner, meshComponent.MeshData);
                if (share == null)
                {
                    Debug.LogError($"[BlendShare] BlendShare mesh component '{meshComponent.name}' references mesh data that is not present in its owner BlendShare list.");
                    continue;
                }

                var renderer = meshComponent.TargetRenderer;
                var targetMesh = renderer != null ? renderer.sharedMesh : null;
                string rendererPath = renderer != null
                    ? MeshNodePath.Normalize(MeshNodePath.GetRelativePath(renderer.transform, targetRoot.transform))
                    : meshComponent.RendererNodePath;
                string key = $"{share.GetInstanceID()}:{meshComponent.MeshData.GetInstanceID()}:{rendererPath}";
                if (!emitted.Add(key))
                {
                    continue;
                }

                GenerateMesh(
                    session,
                    targetLookup,
                    generatedByMeshKey,
                    targetRoot.transform,
                    share,
                    meshComponent.MeshData,
                    rendererPath,
                    renderer,
                    targetMesh,
                    GetComponentsForMeshPass(componentList, meshComponent.Owner, meshComponent),
                    BuildMappingOverrides(meshComponent, meshComponent.MeshData));
            }

            if (generatedByMeshKey.Count == 0)
            {
                return null;
            }

            return BuildArtifact(
                targetRoot,
                targetRoot.transform,
                appliedBlendShares ?? shares,
                generatedByMeshKey.Values,
                session);
        }

        public bool CanApplyToUnityMeshes(
            Object targetMeshContainer,
            IEnumerable<BlendShareObject> blendShares)
        {
            return CanApplyToUnityMeshes(
                UnityMeshTargetLookup.Create(targetMeshContainer),
                targetMeshContainer,
                blendShares);
        }

        public bool CanApplyToUnityMeshes(
            IEnumerable<BlendShareObject> blendShares,
            IEnumerable<Mesh> meshes)
        {
            return CanApplyToUnityMeshes(
                UnityMeshTargetLookup.Create(meshes),
                null,
                blendShares);
        }

        private bool CanApplyToUnityMeshes(
            UnityMeshTargetLookup targetLookup,
            Object targetMeshContainer,
            IEnumerable<BlendShareObject> blendShares)
        {
            var shares = DedupBlendShares(blendShares).ToArray();
            if (targetLookup == null)
            {
                return false;
            }

            var session = new UnityMeshGenerationSession(targetMeshContainer, shares, targetLookup);
            foreach (var share in shares)
            {
                foreach (var meshData in share?.Meshes ?? Array.Empty<MeshDataObject>())
                {
                    if (meshData == null || !targetLookup.TryGetMesh(meshData, out var targetMesh))
                    {
                        return false;
                    }

                    targetLookup.TryGetRenderer(meshData, out var targetRenderer);
                    var context = new UnityMeshGenerationContext(
                        session,
                        share,
                        meshData,
                        targetMesh,
                        targetMesh,
                        targetRenderer,
                        targetLookup.RootTransform,
                        UnityMeshGenerationSession.BuildMeshKey(meshData));

                    bool canGenerateFeature = false;
                    foreach (var generator in FeatureGenerators)
                    {
                        var result = generator.CanApplyToUnityMesh(context);
                        if (result.Failed)
                        {
                            return false;
                        }

                        if (result.Status != MeshFeatureGenerationStatus.Skipped)
                        {
                            canGenerateFeature = true;
                        }
                    }

                    if (!canGenerateFeature || context.HasUnhandledFeatures)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static void GenerateMesh(
            UnityMeshGenerationSession session,
            UnityMeshTargetLookup targetLookup,
            Dictionary<string, Mesh> generatedByMeshKey,
            Transform targetRoot,
            BlendShareObject share,
            MeshDataObject meshData,
            string rendererPath,
            SkinnedMeshRenderer targetRenderer = null,
            Mesh targetMesh = null,
            IEnumerable<BlendShareGenerationComponent> components = null,
            IEnumerable<UnityVertexMappingObject> mappingOverrides = null)
        {
            string meshKey = !string.IsNullOrWhiteSpace(rendererPath)
                ? MeshNodePath.Normalize(rendererPath)
                : UnityMeshGenerationSession.BuildMeshKey(meshData);
            bool createdForThisPass = false;
            if (!generatedByMeshKey.TryGetValue(meshKey, out var workingMesh))
            {
                if (targetMesh == null)
                {
                    targetLookup.TryGetMesh(rendererPath, out targetMesh);
                }

                if (targetMesh == null && !targetLookup.TryGetMesh(meshData, out targetMesh))
                {
                    Debug.LogError($"[BlendShare] Target mesh '{FormatMesh(meshData)}' was not found in '{session.FormatTargetName()}': {targetLookup.GetResolutionError(meshData)}");
                    return;
                }

                workingMesh = Object.Instantiate(targetMesh);
                workingMesh.name = meshKey;
                generatedByMeshKey.Add(meshKey, workingMesh);
                createdForThisPass = true;
            }

            Mesh baseline = null;
            if (!createdForThisPass && workingMesh != null)
            {
                baseline = Object.Instantiate(workingMesh);
                baseline.name = workingMesh.name;
            }

            if (targetRenderer == null)
            {
                targetLookup.TryGetRenderer(rendererPath, out targetRenderer);
            }

            if (targetRenderer == null)
            {
                targetLookup.TryGetRenderer(meshData, out targetRenderer);
            }

            bool failed = false;
            bool generatedFeature = false;
            var context = new UnityMeshGenerationContext(
                session,
                share,
                meshData,
                workingMesh,
                workingMesh,
                targetRenderer,
                targetRoot != null ? targetRoot : targetLookup.RootTransform,
                meshKey,
                components,
                mappingOverrides);

            foreach (var generator in FeatureGenerators)
            {
                var canApply = generator.CanApplyToUnityMesh(context);
                if (canApply.Failed)
                {
                    LogFeatureFailure("validate", generator, meshData, canApply);
                    failed = true;
                    break;
                }

                if (canApply.Status == MeshFeatureGenerationStatus.Skipped)
                {
                    continue;
                }

                var result = generator.ApplyToUnityMesh(context);
                if (result.Failed)
                {
                    LogFeatureFailure("apply", generator, meshData, result);
                    failed = true;
                    break;
                }

                if (result.Status == MeshFeatureGenerationStatus.Skipped)
                {
                    continue;
                }

                generatedFeature = true;
                workingMesh = context.WorkingMesh;
                generatedByMeshKey[meshKey] = workingMesh;
            }

            if (!failed && context.HasUnhandledFeatures)
            {
                LogUnhandledFeatures("apply", meshData, context.GetUnhandledFeatures());
                failed = true;
            }

            if ((failed || !generatedFeature) && createdForThisPass)
            {
                generatedByMeshKey.Remove(meshKey);
                DestroyGeneratedObject(baseline);
                DestroyGeneratedObject(workingMesh);
            }
            else if (failed)
            {
                if (baseline != null)
                {
                    generatedByMeshKey[meshKey] = baseline;
                    DestroyGeneratedObject(workingMesh);
                }

                Debug.LogWarning($"[BlendShare] Skipped BlendShare asset '{share.name}' for mesh '{FormatMesh(meshData)}'. Earlier accumulated output for this mesh will be kept.");
            }
            else
            {
                DestroyGeneratedObject(baseline);
            }
        }

        private static BlendShareArtifact BuildArtifact(
            Object targetMeshContainer,
            Transform targetRoot,
            IEnumerable<BlendShareObject> appliedBlendShares,
            IEnumerable<Mesh> meshes,
            UnityMeshGenerationSession session)
        {
            var meshDescriptors = (meshes ?? Enumerable.Empty<Mesh>())
                .Where(mesh => mesh != null)
                .Select(mesh =>
                {
                    string meshPath = MeshNodePath.Normalize(mesh.name);
                    UnityMeshSkinBindingOutput generatedBinding = null;
                    session?.SkinBindingsByMeshKey?.TryGetValue(meshPath, out generatedBinding);
                    return new BlendShareMeshDescriptor
                    {
                        m_NodePath = meshPath,
                        m_Mesh = mesh,
                        m_SkinBinding = generatedBinding != null
                            ? ToArtifactSkinBinding(generatedBinding)
                            : BuildSkinBinding(targetRoot != null ? targetRoot.gameObject : targetMeshContainer as GameObject, meshPath)
                    };
                })
                .GroupBy(descriptor => descriptor.m_NodePath)
                .Select(group => group.First())
                .ToArray();
            if (meshDescriptors.Length == 0)
            {
                return null;
            }

            var targetSource = ResolveTargetSource(targetMeshContainer);
            var artifact = ScriptableObject.CreateInstance<BlendShareArtifact>();
            artifact.name = $"{targetMeshContainer.name}_BlendShareArtifact";
            artifact.m_TargetSource = targetSource;
            artifact.m_TargetSourceHash = CalculateHash(targetSource);
            artifact.m_AppliedBlendShares = DedupBlendShares(appliedBlendShares).ToArray();
            artifact.m_Meshes = meshDescriptors;
            artifact.m_Armature = session?.Armature;
            return artifact;
        }

        private static BlendShareSkinBindingDescriptor BuildSkinBinding(GameObject root, string rendererPath)
        {
            if (root == null)
            {
                return null;
            }

            var rendererTransform = MeshNodePath.FindRelativeTransform(root.transform, rendererPath);
            var renderer = rendererTransform != null
                ? rendererTransform.GetComponent<SkinnedMeshRenderer>()
                : null;
            if (renderer == null)
            {
                return null;
            }

            var bonePaths = (renderer.bones ?? Array.Empty<Transform>())
                .Select(bone => bone != null ? MeshNodePath.GetRelativePath(bone, root.transform) : null)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(MeshNodePath.Normalize)
                .ToArray();
            string rootBonePath = renderer.rootBone != null
                ? MeshNodePath.GetRelativePath(renderer.rootBone, root.transform)
                : MeshNodePath.Root;

            if (bonePaths.Length == 0 && renderer.rootBone == null)
            {
                return null;
            }

            return new BlendShareSkinBindingDescriptor
            {
                m_RootBonePath = MeshNodePath.Normalize(rootBonePath),
                m_BonePaths = bonePaths
            };
        }

        private static BlendShareSkinBindingDescriptor ToArtifactSkinBinding(UnityMeshSkinBindingOutput binding)
        {
            if (binding == null)
            {
                return null;
            }

            return new BlendShareSkinBindingDescriptor
            {
                m_RootBonePath = MeshNodePath.Normalize(binding.RootBonePath),
                m_BonePaths = (binding.BonePaths ?? Array.Empty<string>())
                    .Select(MeshNodePath.Normalize)
                    .ToArray()
            };
        }

        private static IEnumerable<UnityVertexMappingObject> BuildMappingOverrides(
            BlendShareMeshComponent meshComponent,
            MeshDataObject meshData)
        {
            var targetMesh = meshComponent?.TargetRenderer != null ? meshComponent.TargetRenderer.sharedMesh : null;
            if (meshComponent == null || meshData == null || targetMesh == null)
            {
                yield break;
            }

            foreach (var mapping in meshComponent.GenerationMappingOverrides ?? Array.Empty<UnityVertexMappingObject>())
            {
                yield return mapping;
            }
        }

        private static IReadOnlyList<BlendShareGenerationComponent> GetComponentsForMeshPass(
            IEnumerable<BlendShareGenerationComponent> components,
            BlendShareComponent owner,
            BlendShareMeshComponent meshComponent)
        {
            var result = new List<BlendShareGenerationComponent>();
            if (owner != null)
            {
                result.Add(owner);
            }

            if (meshComponent != null)
            {
                result.Add(meshComponent);
            }

            result.AddRange((components ?? Array.Empty<BlendShareGenerationComponent>())
                .OfType<BlendShareBoneProxyComponent>()
                .Where(proxy => proxy != null && proxy.Owner == owner));

            return result
                .Where(component => component != null)
                .Distinct()
                .ToArray();
        }

        private static bool IsUsableMeshComponent(BlendShareMeshComponent component)
        {
            return component != null &&
                   component.EnabledForBuild &&
                   component.Owner != null &&
                   component.Owner.enabled &&
                   component.TargetRenderer != null &&
                   component.TargetRenderer.sharedMesh != null &&
                   component.MeshData != null;
        }

        private static BlendShareObject FindBlendShareForMeshData(BlendShareComponent owner, MeshDataObject meshData)
        {
            return (owner?.BlendShares ?? Array.Empty<BlendShareObject>())
                .Where(share => share != null)
                .FirstOrDefault(share => (share.Meshes ?? Array.Empty<MeshDataObject>()).Contains(meshData));
        }

        private static IEnumerable<BlendShareObject> DedupBlendShares(IEnumerable<BlendShareObject> blendShares)
        {
            var seen = new HashSet<BlendShareObject>();
            foreach (var share in blendShares ?? Array.Empty<BlendShareObject>())
            {
                if (share != null && seen.Add(share))
                {
                    yield return share;
                }
            }
        }

        private static Object ResolveTargetSource(Object targetMeshContainer)
        {
            if (targetMeshContainer is GeneratedMeshAssetSO generatedAsset)
            {
                return generatedAsset.m_OriginalFbxGo != null
                    ? generatedAsset.m_OriginalFbxGo
                    : targetMeshContainer;
            }

            return targetMeshContainer;
        }

        private static string CalculateHash(Object obj)
        {
            if (obj == null)
            {
                return string.Empty;
            }

            string filePath = AssetDatabase.GetAssetPath(obj);
            if (string.IsNullOrEmpty(filePath))
            {
                return string.Empty;
            }

            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            using (var sha256 = SHA256.Create())
            {
                byte[] hashBytes = sha256.ComputeHash(stream);
                return BitConverter.ToString(hashBytes).ToLowerInvariant().Replace("-", string.Empty);
            }
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

        private static void LogFeatureFailure(
            string action,
            IMeshFeatureGenerator generator,
            MeshDataObject meshData,
            MeshFeatureGenerationResult result)
        {
            Debug.LogError(
                $"[BlendShare] Failed to {action} generator '{generator.GetType().Name}' for mesh '{FormatMesh(meshData)}': {result.Message}");
        }

        private static void LogUnhandledFeatures(
            string action,
            MeshDataObject meshData,
            IEnumerable<MeshFeatureObject> features)
        {
            string featureNames = string.Join(", ", (features ?? Enumerable.Empty<MeshFeatureObject>())
                .Where(feature => feature != null)
                .Select(feature => feature.GetType().Name)
                .Distinct());

            if (string.IsNullOrEmpty(featureNames))
            {
                return;
            }

            Debug.LogError(
                $"[BlendShare] Failed to {action} mesh '{FormatMesh(meshData)}': no generation pass handled feature object(s): {featureNames}.");
        }

        private static string FormatMesh(MeshDataObject meshData)
        {
            return meshData == null ? "<null>" : MeshNodePath.Normalize(meshData.m_Path);
        }

        private static void DestroyGeneratedObject(Object obj)
        {
            if (obj == null || AssetDatabase.Contains(obj))
            {
                return;
            }

            Object.DestroyImmediate(obj);
        }
    }
}
