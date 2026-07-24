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

            return CreateBatchedArtifact(
                targetMeshContainer,
                deduplicatedPatches,
                shouldGenerateMesh,
                appliedPatches,
                progress,
                targetLookup);
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

            return CreateBatchedArtifactFromComponents(
                targetRoot,
                componentList,
                patches,
                appliedPatches,
                progress,
                targetLookup,
                proxiesPrepared);
        }

        private BlendShareArtifact CreateBatchedArtifact(
            Object targetMeshContainer,
            IReadOnlyList<BlendShareObject> patches,
            Func<BlendShareObject, MeshDataObject, bool> shouldGenerateMesh,
            IEnumerable<BlendShareObject> appliedPatches,
            IBlendShareProgress progress,
            UnityMeshTargetLookup targetLookup)
        {
            var targetRoot = targetMeshContainer is GameObject gameObject ? gameObject.transform : null;
            var session = new UnityMeshGenerationSession(
                targetMeshContainer,
                patches,
                targetLookup,
                Array.Empty<BlendShareComponent>(),
                progress);
            var definitions = BuildPatchBatchDefinitions(patches, shouldGenerateMesh, targetLookup);
            return GenerateBatchedArtifact(
                targetMeshContainer,
                targetRoot,
                appliedPatches ?? patches,
                session,
                definitions,
                progress);
        }

        private BlendShareArtifact CreateBatchedArtifactFromComponents(
            GameObject targetRoot,
            IReadOnlyList<BlendShareComponent> componentList,
            IReadOnlyList<BlendShareObject> patches,
            IEnumerable<BlendShareObject> appliedPatches,
            IBlendShareProgress progress,
            UnityMeshTargetLookup targetLookup,
            bool proxiesPrepared)
        {
            var session = new UnityMeshGenerationSession(targetRoot, patches, targetLookup, componentList, progress);
            var meshComponents = BlendSharePatchIdUtility.DeduplicateMeshComponents(componentList
                    .OfType<BlendShareMesh>()
                    .Where(IsUsableMeshComponent)
                    .OrderBy(component => GetHierarchyOrder(component.transform)))
                .ToArray();
            IEnumerable<BlendShareBoneProxy> preparedProxies = proxiesPrepared
                ? componentList.OfType<BlendShareBoneProxy>()
                    .Where(proxy => proxy != null && proxy.isActiveAndEnabled)
                    .Distinct()
                    .ToArray()
                : targetRoot.GetComponentsInChildren<BlendShareBoneProxy>(true);
            BlendShareMesh.RefreshBoneProxyCaches(meshComponents, targetRoot.transform, preparedProxies);
            var definitions = BuildComponentBatchDefinitions(meshComponents, targetRoot.transform);
            return GenerateBatchedArtifact(
                targetRoot,
                targetRoot.transform,
                appliedPatches ?? patches,
                session,
                definitions,
                progress);
        }

        private BlendShareArtifact GenerateBatchedArtifact(
            Object targetMeshContainer,
            Transform targetRoot,
            IEnumerable<BlendShareObject> appliedPatches,
            UnityMeshGenerationSession session,
            IReadOnlyList<UnityBatchDefinition> definitions,
            IBlendShareProgress progress)
        {
            var generatedByMeshKey = new Dictionary<string, Mesh>();
            try
            {
                for (int index = 0; index < definitions.Count; index++)
                {
                    var definition = definitions[index];
                    BlendShareProgressUtility.Report(
                        progress,
                        null,
                        $"Generating mesh {definition.MeshKey}...",
                        GetGenerationProgress(index + 1, definitions.Count),
                        true);
                    if (!GenerateMeshBatch(session, definition, targetRoot, generatedByMeshKey))
                    {
                        continue;
                    }

                    if (!string.IsNullOrEmpty(session.FatalDiagnostic))
                    {
                        LastDiagnostic = session.FatalDiagnostic;
                        DestroyGeneratedObjects(generatedByMeshKey.Values);
                        session.DestroyGeneratedObjects();
                        return null;
                    }
                }

                foreach (var generator in FeatureGenerators)
                {
                    var result = generator.FinalizeUnityGeneration(session);
                    if (result.Failed)
                    {
                        LastDiagnostic = result.Message;
                        Debug.LogError($"[BlendShare] Failed to finalize '{generator.GetType().Name}': {result.Message}");
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
            catch (Exception ex)
            {
                LastDiagnostic = ex.Message;
                DestroyGeneratedObjects(generatedByMeshKey.Values);
                session.DestroyGeneratedObjects();
                Debug.LogException(ex);
                return null;
            }

            if (generatedByMeshKey.Count == 0)
            {
                session.DestroyGeneratedObjects();
                return null;
            }

            BlendShareProgressUtility.Report(progress, null, "Preparing artifact data...", 0.82f, true);
            return BuildArtifact(
                targetMeshContainer,
                targetRoot,
                appliedPatches,
                generatedByMeshKey.Values,
                session);
        }

        private static bool GenerateMeshBatch(
            UnityMeshGenerationSession session,
            UnityBatchDefinition definition,
            Transform targetRoot,
            IDictionary<string, Mesh> generatedByMeshKey)
        {
            if (definition?.OriginalMesh == null || definition.Handles.Count == 0)
            {
                return false;
            }

            var workingMesh = Object.Instantiate(definition.OriginalMesh);
            workingMesh.name = definition.MeshKey;
            TryEnableGeneratedMeshReadability(workingMesh);
            var context = new UnityMeshFeatureContext(
                session,
                definition.MeshKey,
                definition.OriginalMesh,
                workingMesh,
                definition.Renderer,
                targetRoot,
                definition.Handles);
            bool generatedFeature = false;
            foreach (var generator in FeatureGenerators)
            {
                BlendShareProgressUtility.Report(
                    session.Progress,
                    null,
                    $"Applying {generator.GetType().Name} to {definition.MeshKey}...",
                    0.5f,
                    true);
                var canApply = generator.CanApplyToUnityMesh(context);
                if (canApply.Failed)
                {
                    LogBatchFeatureFailure("validate", generator, definition.MeshKey, canApply);
                    continue;
                }

                if (canApply.Status == MeshFeatureGenerationStatus.Skipped)
                {
                    continue;
                }

                var result = generator.ApplyToUnityMesh(context);
                if (result.Failed)
                {
                    LogBatchFeatureFailure("apply", generator, definition.MeshKey, result);
                    continue;
                }

                if (result.Status != MeshFeatureGenerationStatus.Skipped)
                {
                    generatedFeature = true;
                }
            }

            var unhandled = context.GetUnhandledHandles();
            if (unhandled.Count > 0)
            {
                LogUnhandledFeatures(
                    "apply",
                    unhandled[0].MeshData,
                    unhandled.Select(handle => handle.Feature));
                DestroyGeneratedObject(context.WorkingMesh);
                return false;
            }

            if (!generatedFeature)
            {
                DestroyGeneratedObject(context.WorkingMesh);
                return false;
            }

            TryEnableGeneratedMeshReadability(context.WorkingMesh);
            generatedByMeshKey[definition.MeshKey] = context.WorkingMesh;
            return true;
        }

        private static IReadOnlyList<UnityBatchDefinition> BuildPatchBatchDefinitions(
            IEnumerable<BlendShareObject> patches,
            Func<BlendShareObject, MeshDataObject, bool> shouldGenerateMesh,
            UnityMeshTargetLookup targetLookup)
        {
            var ordered = new List<UnityBatchDefinition>();
            var byKey = new Dictionary<string, UnityBatchDefinition>(StringComparer.Ordinal);
            foreach (var patch in patches ?? Array.Empty<BlendShareObject>())
            {
                foreach (var meshData in patch?.Meshes ?? Array.Empty<MeshDataObject>())
                {
                    if (meshData == null || (shouldGenerateMesh != null && !shouldGenerateMesh(patch, meshData)))
                    {
                        continue;
                    }

                    string meshKey = MeshNodePath.Normalize(meshData.m_Path);
                    if (!targetLookup.TryGetMesh(meshKey, out var targetMesh))
                    {
                        Debug.LogError($"[BlendShare] Target mesh '{FormatMesh(meshData)}' was not found: {targetLookup.GetResolutionError(meshData)}");
                        continue;
                    }

                    targetLookup.TryGetRenderer(meshKey, out var renderer);
                    var definition = GetOrCreateDefinition(ordered, byKey, meshKey, targetMesh, renderer);
                    definition.AddFeatures(patch, meshData);
                }
            }

            return ordered;
        }

        private static IReadOnlyList<UnityBatchDefinition> BuildComponentBatchDefinitions(
            IEnumerable<BlendShareMesh> meshComponents,
            Transform targetRoot)
        {
            var ordered = new List<UnityBatchDefinition>();
            var byKey = new Dictionary<string, UnityBatchDefinition>(StringComparer.Ordinal);
            foreach (var component in meshComponents ?? Array.Empty<BlendShareMesh>())
            {
                var renderer = component?.TargetRenderer;
                var targetMesh = renderer != null ? renderer.sharedMesh : null;
                if (component == null || targetMesh == null || component.MeshData == null)
                {
                    continue;
                }

                string meshKey = renderer != null
                    ? MeshNodePath.Normalize(MeshNodePath.GetRelativePath(renderer.transform, targetRoot))
                    : component.RendererNodePath;
                var definition = GetOrCreateDefinition(ordered, byKey, meshKey, targetMesh, renderer);
                definition.AddFeatures(
                    component.Patch,
                    component.MeshData,
                    GetComponentsForMeshPass(component),
                    GetComponentMapping(component, component.MeshData));
            }

            return ordered;
        }

        private static UnityBatchDefinition GetOrCreateDefinition(
            ICollection<UnityBatchDefinition> ordered,
            IDictionary<string, UnityBatchDefinition> byKey,
            string meshKey,
            Mesh targetMesh,
            SkinnedMeshRenderer renderer)
        {
            if (byKey.TryGetValue(meshKey, out var existing))
            {
                return existing;
            }

            var created = new UnityBatchDefinition(meshKey, targetMesh, renderer);
            byKey.Add(meshKey, created);
            ordered.Add(created);
            return created;
        }

        private static void LogBatchFeatureFailure(
            string action,
            IMeshFeatureGenerator generator,
            string meshKey,
            MeshFeatureGenerationResult result)
        {
            Debug.LogError(
                $"[BlendShare] Failed to {action} generator '{generator.GetType().Name}' for mesh '{meshKey}': {result.Message}");
        }

        private sealed class UnityBatchDefinition
        {
            internal UnityBatchDefinition(string meshKey, Mesh originalMesh, SkinnedMeshRenderer renderer)
            {
                MeshKey = MeshNodePath.Normalize(meshKey);
                OriginalMesh = originalMesh;
                Renderer = renderer;
            }

            internal string MeshKey { get; }
            internal Mesh OriginalMesh { get; }
            internal SkinnedMeshRenderer Renderer { get; }
            internal List<MeshFeatureHandle> Handles { get; } = new();

            internal void AddFeatures(
                BlendShareObject patch,
                MeshDataObject meshData,
                IEnumerable<BlendShareComponent> components = null,
                IEnumerable<UnityVertexMappingObject> mappingOverrides = null)
            {
                foreach (var feature in meshData?.Features ?? Array.Empty<MeshFeatureObject>())
                {
                    if (feature == null)
                    {
                        continue;
                    }

                    Handles.Add(new MeshFeatureHandle(
                        feature,
                        patch,
                        meshData,
                        MeshKey,
                        Handles.Count,
                        components,
                        mappingOverrides));
                }
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

            return CanApplyBatched(targetLookup, targetMeshContainer, deduplicatedPatches);
        }

        private static bool CanApplyBatched(
            UnityMeshTargetLookup targetLookup,
            Object targetMeshContainer,
            IReadOnlyList<BlendShareObject> patches)
        {
            var session = new UnityMeshGenerationSession(targetMeshContainer, patches, targetLookup);
            var definitions = new List<UnityBatchDefinition>();
            var definitionsByKey = new Dictionary<string, UnityBatchDefinition>(StringComparer.Ordinal);
            foreach (var patch in patches)
            {
                foreach (var meshData in patch?.Meshes ?? Array.Empty<MeshDataObject>())
                {
                    if (meshData == null || !targetLookup.TryGetMesh(meshData, out var targetMesh))
                    {
                        return false;
                    }

                    string meshKey = MeshNodePath.Normalize(meshData.m_Path);
                    targetLookup.TryGetRenderer(meshData, out var renderer);
                    var definition = GetOrCreateDefinition(
                        definitions,
                        definitionsByKey,
                        meshKey,
                        targetMesh,
                        renderer);
                    definition.AddFeatures(patch, meshData);
                }
            }

            foreach (var definition in definitions)
            {
                var context = new UnityMeshFeatureContext(
                    session,
                    definition.MeshKey,
                    definition.OriginalMesh,
                    definition.OriginalMesh,
                    definition.Renderer,
                    targetLookup.RootTransform,
                    definition.Handles);
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

                if (!canGenerateFeature || context.GetUnhandledHandles().Count > 0)
                {
                    return false;
                }
            }

            return true;
        }

        private static void TryEnableGeneratedMeshReadability(Mesh mesh)
        {
            if (mesh != null && !UnityMeshEditorDataUtility.TryEnableReadability(mesh))
            {
                Debug.LogWarning($"[BlendShare] Generated mesh '{mesh.name}' could not be marked readable. Generation will continue.");
            }
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
                    if (meshComponent.TryGetCachedBone(sourceBonePath, out var resolved))
                    {
                        result.Add(resolved.Proxy);
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
