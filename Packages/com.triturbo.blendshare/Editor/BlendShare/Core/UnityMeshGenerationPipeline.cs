using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShapeShare.BlendShapeData;
using Triturbo.BlendShare.Components;
using Triturbo.BlendShare.Features.SkinWeights;
using Triturbo.BlendShare.Hashing;
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
        internal string LastDiagnostic { get; private set; }

        private static readonly IReadOnlyList<IMeshFeatureGenerator> FeatureGenerators =
            BlendShareFeatureModules.All
                .Select(module => module?.Generator)
                .Where(generator => generator != null)
                .OrderBy(generator => generator.Order)
                .ThenBy(generator => generator.GetType().FullName, StringComparer.Ordinal)
                .ToArray();

        public BlendShareArtifact CreateArtifact(
            Object targetMeshContainer,
            IEnumerable<BlendShareObject> patches,
            Func<BlendShareObject, MeshDataObject, bool> shouldGenerateMesh = null,
            IEnumerable<BlendShareObject> appliedPatches = null,
            IBlendShareProgress progress = null)
        {
            LastDiagnostic = null;
            progress = BlendShareProgressUtility.Resolve(progress);
            var deduplicatedPatches = BlendSharePatchIdUtility.DeduplicateByPatchId(patches).ToArray();
            if (targetMeshContainer == null || deduplicatedPatches.Length == 0)
            {
                return null;
            }

            BlendShareProgressUtility.Report(progress, null, "Preparing target meshes...", 0.05f, true);
            var targetLookup = UnityMeshTargetLookup.Create(targetMeshContainer);
            if (targetLookup == null)
            {
                return null;
            }

            var targetRoot = targetMeshContainer is GameObject gameObject ? gameObject.transform : null;
            var session = new UnityMeshGenerationSession(
                targetMeshContainer,
                deduplicatedPatches,
                targetLookup,
                Array.Empty<BlendShareComponent>(),
                progress);
            var generatedByMeshKey = new Dictionary<string, Mesh>();

            try
            {
                int meshStep = 0;
                int meshStepCount = CountMeshes(deduplicatedPatches, shouldGenerateMesh);
                foreach (var patch in deduplicatedPatches)
                {
                    foreach (var meshData in patch?.Meshes ?? Array.Empty<MeshDataObject>())
                    {
                        if (meshData == null || (shouldGenerateMesh != null && !shouldGenerateMesh(patch, meshData)))
                        {
                            continue;
                        }

                        meshStep++;
                        BlendShareProgressUtility.Report(
                            progress,
                            null,
                            $"Generating mesh {FormatMesh(meshData)}...",
                            GetGenerationProgress(meshStep, meshStepCount),
                            true);

                        GenerateMesh(
                            session,
                            targetLookup,
                            generatedByMeshKey,
                            targetRoot,
                            patch,
                            meshData,
                            MeshNodePath.Normalize(meshData.m_Path));
                        if (!string.IsNullOrEmpty(session.FatalDiagnostic))
                        {
                            LastDiagnostic = session.FatalDiagnostic;
                            DestroyGeneratedObjects(generatedByMeshKey.Values);
                            session.DestroyGeneratedObjects();
                            return null;
                        }
                    }
                }
            }
            catch (BlendShareOperationCanceledException)
            {
                DestroyGeneratedObjects(generatedByMeshKey.Values);
                session.DestroyGeneratedObjects();
                throw;
            }

            if (generatedByMeshKey.Count == 0)
            {
                session.DestroyGeneratedObjects();
                return null;
            }

            if (!SkinWeightFeatureGenerator.FinalizeUnitySkinWeights(session, out string skinError))
            {
                LastDiagnostic = skinError;
                Debug.LogError($"[BlendShare] Failed to finalize skin weights: {skinError}");
                DestroyGeneratedObjects(generatedByMeshKey.Values);
                session.DestroyGeneratedObjects();
                return null;
            }

            try
            {
                BlendShareProgressUtility.Report(progress, null, "Preparing artifact data...", 0.82f, true);
                return BuildArtifact(
                    targetMeshContainer,
                    targetRoot,
                    appliedPatches ?? deduplicatedPatches,
                    generatedByMeshKey.Values,
                    session);
            }
            catch (BlendShareOperationCanceledException)
            {
                DestroyGeneratedObjects(generatedByMeshKey.Values);
                session.DestroyGeneratedObjects();
                throw;
            }
        }

        public BlendShareArtifact CreateArtifactFromComponents(
            GameObject targetRoot,
            IEnumerable<BlendShareComponent> components,
            IEnumerable<BlendShareObject> appliedPatches = null,
            IBlendShareProgress progress = null)
        {
            return CreateArtifactFromComponents(
                targetRoot,
                components,
                appliedPatches,
                progress,
                false);
        }

        internal BlendShareArtifact CreateArtifactFromPreparedComponents(
            GameObject targetRoot,
            IEnumerable<BlendShareComponent> components,
            IEnumerable<BlendShareObject> appliedPatches = null,
            IBlendShareProgress progress = null)
        {
            return CreateArtifactFromComponents(
                targetRoot,
                components,
                appliedPatches,
                progress,
                true);
        }

        private BlendShareArtifact CreateArtifactFromComponents(
            GameObject targetRoot,
            IEnumerable<BlendShareComponent> components,
            IEnumerable<BlendShareObject> appliedPatches,
            IBlendShareProgress progress,
            bool proxiesPrepared)
        {
            LastDiagnostic = null;
            progress = BlendShareProgressUtility.Resolve(progress);
            if (targetRoot == null)
            {
                return null;
            }

            var componentList = (components ?? Array.Empty<BlendShareComponent>())
                .Where(component => component != null)
                .Distinct()
                .ToArray();
            var patches = BlendSharePatchIdUtility.DeduplicateByPatchId(componentList
                .OfType<BlendShareMesh>()
                .Where(IsUsableMeshComponent)
                .Select(component => component.Patch))
                .ToArray();
            if (patches.Length == 0)
            {
                return null;
            }

            var targetLookup = UnityMeshTargetLookup.Create(targetRoot);
            if (targetLookup == null)
            {
                return null;
            }

            var session = new UnityMeshGenerationSession(targetRoot, patches, targetLookup, componentList, progress);
            var generatedByMeshKey = new Dictionary<string, Mesh>();
            var meshComponents = BlendSharePatchIdUtility.DeduplicateMeshComponents(componentList
                    .OfType<BlendShareMesh>()
                    .Where(IsUsableMeshComponent)
                    .OrderBy(component => GetHierarchyOrder(component.transform)))
                .ToArray();
            IEnumerable<BlendShareBoneProxy> preparedProxies;
            if (proxiesPrepared)
            {
                preparedProxies = componentList
                    .OfType<BlendShareBoneProxy>()
                    .Where(proxy => proxy != null && proxy.isActiveAndEnabled)
                    .Distinct()
                    .ToArray();
            }
            else
            {
                preparedProxies = targetRoot.GetComponentsInChildren<BlendShareBoneProxy>(true);
            }
            BlendShareMesh.RefreshBoneProxyCaches(meshComponents, targetRoot.transform, preparedProxies);
            try
            {
                for (int meshStep = 0; meshStep < meshComponents.Length; meshStep++)
                {
                    var meshComponent = meshComponents[meshStep];
                    var patch = meshComponent.Patch;

                    var renderer = meshComponent.TargetRenderer;
                    var targetMesh = renderer != null ? renderer.sharedMesh : null;
                    string rendererPath = renderer != null
                        ? MeshNodePath.Normalize(MeshNodePath.GetRelativePath(renderer.transform, targetRoot.transform))
                        : meshComponent.RendererNodePath;

                    BlendShareProgressUtility.Report(
                        progress,
                        null,
                        $"Generating mesh {FormatMesh(meshComponent.MeshData)}...",
                        GetGenerationProgress(meshStep + 1, meshComponents.Length),
                        true);

                    var meshPassComponents = GetComponentsForMeshPass(meshComponent);
                    GenerateMesh(
                        session,
                        targetLookup,
                        generatedByMeshKey,
                        targetRoot.transform,
                        patch,
                        meshComponent.MeshData,
                        rendererPath,
                        renderer,
                        targetMesh,
                        meshPassComponents,
                        GetComponentMapping(meshComponent, meshComponent.MeshData));
                    if (!string.IsNullOrEmpty(session.FatalDiagnostic))
                    {
                        LastDiagnostic = session.FatalDiagnostic;
                        DestroyGeneratedObjects(generatedByMeshKey.Values);
                        session.DestroyGeneratedObjects();
                        return null;
                    }
                }
            }
            catch (BlendShareOperationCanceledException)
            {
                DestroyGeneratedObjects(generatedByMeshKey.Values);
                session.DestroyGeneratedObjects();
                throw;
            }

            if (generatedByMeshKey.Count == 0)
            {
                session.DestroyGeneratedObjects();
                return null;
            }

            if (!SkinWeightFeatureGenerator.FinalizeUnitySkinWeights(session, out string skinError))
            {
                LastDiagnostic = skinError;
                Debug.LogError($"[BlendShare] Failed to finalize skin weights: {skinError}");
                DestroyGeneratedObjects(generatedByMeshKey.Values);
                session.DestroyGeneratedObjects();
                return null;
            }

            try
            {
                BlendShareProgressUtility.Report(progress, null, "Preparing artifact data...", 0.82f, true);
                return BuildArtifact(
                    targetRoot,
                    targetRoot.transform,
                    appliedPatches ?? patches,
                    generatedByMeshKey.Values,
                    session);
            }
            catch (BlendShareOperationCanceledException)
            {
                DestroyGeneratedObjects(generatedByMeshKey.Values);
                session.DestroyGeneratedObjects();
                throw;
            }
        }

        public bool CanApplyToUnityMeshes(
            Object targetMeshContainer,
            IEnumerable<BlendShareObject> patches)
        {
            return CanApplyToUnityMeshes(
                UnityMeshTargetLookup.Create(targetMeshContainer),
                targetMeshContainer,
                patches);
        }

        public bool CanApplyToUnityMeshes(
            IEnumerable<BlendShareObject> patches,
            IEnumerable<Mesh> meshes)
        {
            return CanApplyToUnityMeshes(
                UnityMeshTargetLookup.Create(meshes),
                null,
                patches);
        }

        private bool CanApplyToUnityMeshes(
            UnityMeshTargetLookup targetLookup,
            Object targetMeshContainer,
            IEnumerable<BlendShareObject> patches)
        {
            var deduplicatedPatches = BlendSharePatchIdUtility.DeduplicateByPatchId(patches).ToArray();
            if (targetLookup == null)
            {
                return false;
            }

            var session = new UnityMeshGenerationSession(targetMeshContainer, deduplicatedPatches, targetLookup);
            foreach (var patch in deduplicatedPatches)
            {
                foreach (var meshData in patch?.Meshes ?? Array.Empty<MeshDataObject>())
                {
                    if (meshData == null || !targetLookup.TryGetMesh(meshData, out var targetMesh))
                    {
                        return false;
                    }

                    targetLookup.TryGetRenderer(meshData, out var targetRenderer);
                    var context = new UnityMeshGenerationContext(
                        session,
                        patch,
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
            BlendShareObject patch,
            MeshDataObject meshData,
            string rendererPath,
            SkinnedMeshRenderer targetRenderer = null,
            Mesh targetMesh = null,
            IEnumerable<BlendShareComponent> components = null,
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
                TryEnableGeneratedMeshReadability(workingMesh);
                generatedByMeshKey.Add(meshKey, workingMesh);
                createdForThisPass = true;
            }

            Mesh baseline = null;
            if (!createdForThisPass && workingMesh != null)
            {
                baseline = Object.Instantiate(workingMesh);
                baseline.name = workingMesh.name;
                TryEnableGeneratedMeshReadability(baseline);
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
                patch,
                meshData,
                targetMesh,
                workingMesh,
                targetRenderer,
                targetRoot != null ? targetRoot : targetLookup.RootTransform,
                meshKey,
                components,
                mappingOverrides);

            foreach (var generator in FeatureGenerators)
            {
                BlendShareProgressUtility.Report(
                    session.Progress,
                    null,
                    $"Applying {generator.GetType().Name} to {FormatMesh(meshData)}...",
                    0.5f,
                    true);
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
                SkinWeightFeatureGenerator.DiscardUnitySkinWeights(session, meshKey);
                generatedByMeshKey.Remove(meshKey);
                DestroyGeneratedObject(baseline);
                DestroyGeneratedObject(workingMesh);
            }
            else if (failed)
            {
                SkinWeightFeatureGenerator.DiscardUnitySkinWeights(session, meshKey);
                if (baseline != null)
                {
                    generatedByMeshKey[meshKey] = baseline;
                    DestroyGeneratedObject(workingMesh);
                    SkinWeightFeatureGenerator.RestoreUnitySkinMesh(session, meshKey, baseline);
                }

                Debug.LogWarning($"[BlendShare] Skipped BlendShare patch '{patch.name}' for mesh '{FormatMesh(meshData)}'. Earlier accumulated output for this mesh will be kept.");
            }
            else
            {
                SkinWeightFeatureGenerator.CommitUnitySkinWeights(session, meshKey);
                TryEnableGeneratedMeshReadability(workingMesh);
                DestroyGeneratedObject(baseline);
            }
        }

        private static void TryEnableGeneratedMeshReadability(Mesh mesh)
        {
            if (mesh != null && !UnityMeshEditorDataUtility.TryEnableReadability(mesh))
            {
                Debug.LogWarning($"[BlendShare] Generated mesh '{mesh.name}' could not be marked readable. Generation will continue.");
            }
        }

        private static int CountMeshes(
            IEnumerable<BlendShareObject> patches,
            Func<BlendShareObject, MeshDataObject, bool> shouldGenerateMesh)
        {
            int count = 0;
            foreach (var patch in patches ?? Array.Empty<BlendShareObject>())
            {
                foreach (var meshData in patch?.Meshes ?? Array.Empty<MeshDataObject>())
                {
                    if (meshData != null && (shouldGenerateMesh == null || shouldGenerateMesh(patch, meshData)))
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        private static float GetGenerationProgress(int currentMesh, int meshCount)
        {
            if (meshCount <= 0)
            {
                return 0.2f;
            }

            return Mathf.Lerp(0.15f, 0.78f, Mathf.Clamp01((float)currentMesh / meshCount));
        }

        private static BlendShareArtifact BuildArtifact(
            Object targetMeshContainer,
            Transform targetRoot,
            IEnumerable<BlendShareObject> appliedPatches,
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
            artifact.m_AppliedBlendShares = BlendSharePatchIdUtility.DeduplicateByPatchId(appliedPatches).ToArray();
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

        private static IEnumerable<UnityVertexMappingObject> GetComponentMapping(
            BlendShareMesh meshComponent,
            MeshDataObject meshData)
        {
            var targetMesh = meshComponent?.TargetRenderer != null ? meshComponent.TargetRenderer.sharedMesh : null;
            if (meshComponent == null || meshData == null || targetMesh == null)
            {
                yield break;
            }

            if (UnityMeshEditorDataUtility.IsMappingCompatible(meshComponent.Mapping, meshData, targetMesh))
            {
                yield return meshComponent.Mapping;
            }
        }

        private static IReadOnlyList<BlendShareComponent> GetComponentsForMeshPass(
            BlendShareMesh meshComponent)
        {
            var result = new List<BlendShareComponent>();
            if (meshComponent != null)
            {
                result.Add(meshComponent);
            }

            var skin = meshComponent?.MeshData?.GetFeature<SkinWeightFeatureObject>();
            if (skin?.Armature != null)
            {
                foreach (string sourceBonePath in skin.GetNeededBonePathsInArmatureOrder())
                {
                    if (meshComponent.TryGetCachedBoneProxy(sourceBonePath, out var proxy))
                    {
                        result.Add(proxy);
                    }
                }
            }

            return result
                .Where(component => component != null)
                .Distinct()
                .ToArray();
        }

        private static bool IsUsableMeshComponent(BlendShareMesh component)
        {
            return component != null &&
                   component.isActiveAndEnabled &&
                   component.Patch != null &&
                   component.TargetRenderer != null &&
                   component.TargetRenderer.sharedMesh != null &&
                   component.MeshData != null &&
                   (component.Patch.Meshes ?? Array.Empty<MeshDataObject>()).Contains(component.MeshData);
        }

        private static Object ResolveTargetSource(Object targetMeshContainer)
        {
            if (targetMeshContainer is GeneratedMeshAssetSO generatedAsset)
            {
                return generatedAsset.m_OriginalFbxAsset != null
                    ? generatedAsset.m_OriginalFbxAsset
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

            return BlendShareHashUtility.Sha256File(filePath);
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

        private static void DestroyGeneratedObjects(IEnumerable<Object> objects)
        {
            foreach (var obj in objects ?? Enumerable.Empty<Object>())
            {
                DestroyGeneratedObject(obj);
            }
        }
    }
}
