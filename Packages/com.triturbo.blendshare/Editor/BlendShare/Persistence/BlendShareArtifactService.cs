using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Triturbo.BlendShapeShare.BlendShapeData;
using Triturbo.BlendShare.Components;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Features.SkinWeights;
using Triturbo.BlendShare.Hashing;
using Triturbo.BlendShare.Migration;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Triturbo.BlendShare.Persistence
{
    /// <summary>
    /// Creates and persists generated BlendShare artifact assets.
    /// </summary>
    public static class BlendShareArtifactService
    {
        public static BlendShareArtifact CreateArtifact(
            Object targetMeshContainer,
            IEnumerable<BlendShareObject> patches,
            string path,
            IBlendShareProgress progress = null)
        {
            progress = BlendShareProgressUtility.Resolve(progress);
            var deduplicatedPatches = BlendSharePatchIdUtility.DeduplicateByPatchId(patches).ToList();
            if (targetMeshContainer == null || deduplicatedPatches.Count == 0 || string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            BlendShareProgressUtility.Report(progress, null, "Preparing artifact generation...", 0.03f, true);
            var unityPipeline = new UnityMeshGenerationPipeline();
            var appliedPatches = GetAppliedPatches(targetMeshContainer, deduplicatedPatches);
            GameObject targetFbx = GetTargetFbx(targetMeshContainer);
            BlendShareArtifact inMemoryArtifact = null;
            Mesh[] unsavedMeshes = Array.Empty<Mesh>();

            if (targetFbx == null || unityPipeline.CanApplyToUnityMeshes(targetMeshContainer, deduplicatedPatches))
            {
                inMemoryArtifact = CreateInMemoryArtifact(
                    targetMeshContainer,
                    deduplicatedPatches,
                    null,
                    appliedPatches,
                    progress);
                if (inMemoryArtifact != null)
                {
                    unsavedMeshes = (inMemoryArtifact.m_Meshes ?? Array.Empty<BlendShareMeshDescriptor>())
                        .Select(descriptor => descriptor?.m_Mesh)
                        .Where(mesh => mesh != null)
                        .ToArray();
                    try
                    {
                        BlendShareProgressUtility.Report(progress, null, "Saving artifact asset...", 0.92f, false);
                        return SaveArtifactToAsset(inMemoryArtifact, path);
                    }
                    finally
                    {
                        RemoveGeneratedObjects(unsavedMeshes);
                        RemoveGeneratedObjects(new Object[] { inMemoryArtifact.m_Armature, inMemoryArtifact });
                    }
                }
            }

            if (targetFbx != null)
            {
                return CreateArtifactViaFbxFallback(targetFbx, targetMeshContainer, appliedPatches, path, progress);
            }

            return null;
        }

        private static BlendShareArtifact CreateArtifactViaFbxFallback(
            GameObject targetFbx,
            Object targetMeshContainer,
            IEnumerable<BlendShareObject> appliedPatches,
            string path,
            IBlendShareProgress progress)
        {
#if ENABLE_FBX_SDK
            if (targetFbx == null || targetMeshContainer == null || string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var deduplicatedPatches = BlendSharePatchIdUtility.DeduplicateByPatchId(appliedPatches).ToArray();
            if (!new FbxGenerationPipeline().CanApply(deduplicatedPatches))
            {
                return null;
            }

            string folder = Path.GetDirectoryName(path) ?? Application.dataPath;
            string tempAssetPath = Path.Combine(folder, $"{targetMeshContainer.name}-{Guid.NewGuid()}.fbx");
            try
            {
                if (!BlendShareGenerationService.CreateFbx(targetFbx, deduplicatedPatches, tempAssetPath, true, false, progress: progress))
                {
                    Debug.LogError("[BlendShare] Failed to create temporary artifact FBX.");
                    return null;
                }

                BlendShareProgressUtility.Report(progress, null, "Saving artifact asset...", 0.92f, false);
                return SaveArtifactToAsset(
                    targetFbx,
                    deduplicatedPatches,
                    tempAssetPath,
                    path);
            }
            finally
            {
                AssetDatabase.MoveAssetToTrash(tempAssetPath);
            }
#else
            return null;
#endif
        }

        public static BlendShareArtifact CreateInMemoryArtifact(
            Object targetMeshContainer,
            IEnumerable<BlendShareObject> patches,
            Func<BlendShareObject, MeshDataObject, bool> shouldGenerateMesh = null,
            IBlendShareProgress progress = null)
        {
            var deduplicatedPatches = BlendSharePatchIdUtility.DeduplicateByPatchId(patches).ToArray();
            if (targetMeshContainer == null || deduplicatedPatches.Length == 0)
            {
                return null;
            }

            return new UnityMeshGenerationPipeline().CreateArtifact(
                targetMeshContainer,
                deduplicatedPatches,
                shouldGenerateMesh,
                GetAppliedPatches(targetMeshContainer, deduplicatedPatches),
                progress);
        }

        public static BlendShareArtifact CreateInMemoryArtifact(
            GameObject targetRoot,
            IEnumerable<BlendShareComponent> components)
        {
            return CreateInMemoryArtifact(targetRoot, components, out _);
        }

        internal static BlendShareArtifact CreateInMemoryArtifact(
            GameObject targetRoot,
            IEnumerable<BlendShareComponent> components,
            out string diagnostic)
        {
            diagnostic = null;
            if (targetRoot == null)
            {
                return null;
            }

            var componentArray = (components ?? Enumerable.Empty<BlendShareComponent>())
                .Where(component => component != null)
                .Distinct()
                .ToArray();
            if (componentArray.Length == 0)
            {
                return null;
            }

            var pipeline = new UnityMeshGenerationPipeline();
            var artifact = pipeline.CreateArtifactFromComponents(
                targetRoot,
                componentArray);
            diagnostic = pipeline.LastDiagnostic;
            return artifact;
        }

        private static BlendShareArtifact CreateInMemoryArtifact(
            Object targetMeshContainer,
            IReadOnlyCollection<BlendShareObject> patches,
            Func<BlendShareObject, MeshDataObject, bool> shouldGenerateMesh,
            IReadOnlyCollection<BlendShareObject> appliedPatches,
            IBlendShareProgress progress)
        {
            return new UnityMeshGenerationPipeline().CreateArtifact(
                targetMeshContainer,
                patches,
                shouldGenerateMesh,
                appliedPatches,
                progress);
        }

        public static void ApplyArtifact(BlendShareArtifact artifact, Transform targetRoot)
        {
            var result = ApplyArtifact(artifact, targetRoot, new BlendShareArtifactApplyOptions());
            foreach (string diagnostic in result.Diagnostics)
            {
                Debug.LogError($"[BlendShare Artifact] {diagnostic}", targetRoot);
            }
        }

        public static BlendShareArtifactApplyResult ApplyArtifact(
            BlendShareArtifact artifact,
            Transform targetRoot,
            BlendShareArtifactApplyOptions options)
        {
            var result = new BlendShareArtifactApplyResult();
            if (artifact == null || targetRoot == null)
            {
                result.AddDiagnostic("Apply requires an artifact and target root.");
                return result;
            }

            options ??= new BlendShareArtifactApplyOptions();
            bool requiresArmature = (artifact.m_AppliedBlendShares ?? Array.Empty<BlendShareObject>())
                .Where(patch => patch != null)
                .SelectMany(patch => patch.Meshes ?? Array.Empty<MeshDataObject>())
                .Any(mesh => mesh?.GetFeature<SkinWeightFeatureObject>()?.Armature != null);
            if (requiresArmature && artifact.m_Armature == null)
            {
                result.AddDiagnostic(
                    "This artifact uses an unsupported pre-2.0 armature schema. Regenerate it from re-extracted BlendShare patches.");
                return result;
            }

            Transform bonePathRoot = options.BonePathRoot != null ? options.BonePathRoot : targetRoot;
            var boneTransformOverrides = BuildBoneTransformOverrideLookup(options.BoneTransformOverrides);
            var renderersByPath = targetRoot
                .GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .GroupBy(renderer => MeshNodePath.GetRelativePath(renderer.transform, targetRoot))
                .ToDictionary(group => MeshNodePath.Normalize(group.Key), group => group.First());

            var transformsByPath = targetRoot
                .GetComponentsInChildren<Transform>(true)
                .GroupBy(transform => MeshNodePath.GetRelativePath(transform, targetRoot))
                .ToDictionary(group => MeshNodePath.Normalize(group.Key), group => group.First());

            int undoGroup = -1;
            if (options.UseUndo)
            {
                undoGroup = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName(string.IsNullOrWhiteSpace(options.UndoName)
                    ? $"[BlendShare] Apply Artifact to {targetRoot.name}"
                    : options.UndoName);
            }

            try
            {
                var applyState = new ApplyState();
                ApplyArmatureTransforms(
                    artifact.m_Armature,
                    targetRoot,
                    transformsByPath,
                    boneTransformOverrides,
                    applyState,
                    result,
                    options);

                foreach (var descriptor in artifact.m_Meshes ?? Array.Empty<BlendShareMeshDescriptor>())
                {
                    if (descriptor == null || descriptor.m_Mesh == null)
                    {
                        continue;
                    }

                    string nodePath = MeshNodePath.Normalize(descriptor.m_NodePath);
                    if (!renderersByPath.TryGetValue(nodePath, out var renderer) || renderer == null)
                    {
                        result.AddDiagnostic($"Renderer path '{nodePath}' not found in target '{targetRoot.name}'.");
                        continue;
                    }

                    BlendShareAppliedRenderer marker = null;
                    if (options.RecordDestructiveMarkers)
                    {
                        marker = renderer.GetComponent<BlendShareAppliedRenderer>();
                        if (marker == null)
                        {
                            marker = options.UseUndo
                                ? Undo.AddComponent<BlendShareAppliedRenderer>(renderer.gameObject)
                                : renderer.gameObject.AddComponent<BlendShareAppliedRenderer>();
                        }

                        RecordObject(marker, "Record BlendShare Apply Marker", options);
                        marker.CaptureBaseline(renderer);
                    }

                    RecordObject(renderer, "Apply BlendShare Artifact", options);
                    options.SaveGeneratedMesh?.Invoke(descriptor.m_Mesh);
                    renderer.sharedMesh = descriptor.m_Mesh;
                    if (!ApplySkinBinding(
                            renderer,
                            descriptor.m_SkinBinding,
                            artifact.m_Armature,
                            targetRoot,
                            bonePathRoot,
                            transformsByPath,
                            boneTransformOverrides,
                            applyState,
                            result,
                            options))
                    {
                        continue;
                    }

                    if (marker != null)
                    {
                        marker.SetGeneratedBones(CollectExtraBones(marker.OriginalBones, renderer.bones));
                        MarkDirty(marker, options);
                    }

                    MarkDirty(renderer, options);
                    result.AddAppliedRenderer(renderer);
                }
            }
            finally
            {
                if (options.UseUndo && undoGroup >= 0)
                {
                    Undo.CollapseUndoOperations(undoGroup);
                }
            }

            return result;
        }

        public static bool ApplyArtifactMeshAssignment(
            BlendShareArtifact artifact,
            BlendShareMeshDescriptor descriptor,
            SkinnedMeshRenderer renderer,
            Mesh originalMesh,
            Transform originalRootBone,
            Transform[] originalBones,
            int changeUndoGroup,
            out IReadOnlyList<string> diagnostics)
        {
            var result = new BlendShareArtifactApplyResult();
            diagnostics = result.Diagnostics;
            if (artifact == null || descriptor == null || renderer == null || descriptor.m_Mesh == null)
            {
                result.AddDiagnostic("Apply requires an artifact mesh descriptor and target renderer.");
                return false;
            }

            var targetRoot = ResolveArtifactAssignmentRoot(renderer.transform, descriptor.m_NodePath);
            if (targetRoot == null)
            {
                result.AddDiagnostic($"Cannot resolve target root for renderer '{renderer.name}'.");
                return false;
            }

            var options = new BlendShareArtifactApplyOptions
            {
                UndoName = "Apply BlendShare Artifact Mesh",
                BonePathRoot = targetRoot
            };

            var transformsByPath = targetRoot
                .GetComponentsInChildren<Transform>(true)
                .GroupBy(transform => MeshNodePath.GetRelativePath(transform, targetRoot))
                .ToDictionary(group => MeshNodePath.Normalize(group.Key), group => group.First());

            int undoGroup = changeUndoGroup >= 0 ? changeUndoGroup : Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(options.UndoName);
            try
            {
                var marker = renderer.GetComponent<BlendShareAppliedRenderer>();
                bool addedMarker = marker == null;
                if (marker == null)
                {
                    marker = Undo.AddComponent<BlendShareAppliedRenderer>(renderer.gameObject);
                }

                Undo.RecordObject(marker, "Record BlendShare Apply Marker");
                marker.CaptureBaseline(originalMesh, originalRootBone, originalBones);

                Undo.RecordObject(renderer, options.UndoName);
                renderer.sharedMesh = descriptor.m_Mesh;
                bool applied = ApplySkinBinding(
                    renderer,
                    descriptor.m_SkinBinding,
                    artifact.m_Armature,
                    targetRoot,
                    targetRoot,
                    transformsByPath,
                    null,
                    new ApplyState(),
                    result,
                    options);

                if (!applied || !result.Success)
                {
                    RestoreRendererBaseline(renderer, originalMesh, originalRootBone, originalBones);
                    DestroyCreatedGeneratedBones(result.GeneratedBones);
                    if (addedMarker)
                    {
                        Undo.DestroyObjectImmediate(marker);
                    }

                    EditorUtility.SetDirty(renderer);
                    return false;
                }

                marker.AddGeneratedBones(CollectExtraBones(marker.OriginalBones, renderer.bones));
                EditorUtility.SetDirty(marker);
                EditorUtility.SetDirty(renderer);
                return true;
            }
            finally
            {
                Undo.CollapseUndoOperations(undoGroup);
            }
        }

        public static bool RevertAppliedRenderer(SkinnedMeshRenderer renderer, int changeUndoGroup = -1)
        {
            if (renderer == null)
            {
                return false;
            }

            var marker = renderer.GetComponent<BlendShareAppliedRenderer>();
            if (marker == null || !marker.HasBaseline)
            {
                Debug.LogError("[BlendShare Artifact] Cannot revert renderer because BlendShare baseline references are missing.", renderer);
                return false;
            }

            int undoGroup = changeUndoGroup >= 0 ? changeUndoGroup : Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Revert BlendShare Artifact");
            Undo.RecordObject(renderer, "Revert BlendShare Artifact");
            try
            {
                renderer.sharedMesh = marker.OriginalMesh;
                renderer.rootBone = marker.OriginalRootBone;
                renderer.bones = marker.OriginalBones;
                foreach (var bone in marker.GeneratedBones)
                {
                    if (bone == null || IsReferencedByOtherMarker(marker, bone))
                    {
                        continue;
                    }

                    Undo.DestroyObjectImmediate(bone.gameObject);
                }

                Undo.DestroyObjectImmediate(marker);
                EditorUtility.SetDirty(renderer);
                return true;
            }
            finally
            {
                Undo.CollapseUndoOperations(undoGroup);
            }
        }

        private static void RestoreRendererBaseline(
            SkinnedMeshRenderer renderer,
            Mesh originalMesh,
            Transform originalRootBone,
            Transform[] originalBones)
        {
            if (renderer == null)
            {
                return;
            }

            renderer.sharedMesh = originalMesh;
            renderer.rootBone = originalRootBone;
            renderer.bones = originalBones ?? Array.Empty<Transform>();
        }

        private static void DestroyCreatedGeneratedBones(IEnumerable<BlendShareGeneratedBoneRecord> generatedBones)
        {
            foreach (var bone in (generatedBones ?? Array.Empty<BlendShareGeneratedBoneRecord>())
                         .Where(record => record?.Created == true)
                         .Select(record => record.Transform)
                         .Where(transform => transform != null)
                         .Distinct()
                         .OrderByDescending(GetHierarchyDepth))
            {
                Undo.DestroyObjectImmediate(bone.gameObject);
            }
        }

        private static int GetHierarchyDepth(Transform transform)
        {
            int depth = 0;
            for (var current = transform; current != null; current = current.parent)
            {
                depth++;
            }

            return depth;
        }

        private static Transform[] CollectExtraBones(Transform[] originalBones, Transform[] appliedBones)
        {
            var original = new HashSet<Transform>((originalBones ?? Array.Empty<Transform>()).Where(bone => bone != null));
            return (appliedBones ?? Array.Empty<Transform>())
                .Where(bone => bone != null && !original.Contains(bone))
                .Distinct()
                .ToArray();
        }

        private static Transform ResolveArtifactAssignmentRoot(Transform rendererTransform, string rendererNodePath)
        {
            rendererNodePath = MeshNodePath.Normalize(rendererNodePath);
            if (rendererTransform == null || rendererNodePath == MeshNodePath.Root)
            {
                return rendererTransform;
            }

            var current = rendererTransform;
            var normalized = rendererNodePath;
            while (current.parent != null && normalized != MeshNodePath.Root)
            {
                if (!string.Equals(MeshNodePath.LeafName(normalized), current.name, StringComparison.Ordinal))
                {
                    break;
                }

                current = current.parent;
                normalized = MeshNodePath.ParentPath(normalized);
            }

            return normalized == MeshNodePath.Root ? current : rendererTransform.root;
        }

        private static bool IsReferencedByOtherMarker(BlendShareAppliedRenderer owner, Transform bone)
        {
            if (owner == null || bone == null)
            {
                return false;
            }

            return UnityEngine.Object.FindObjectsOfType<BlendShareAppliedRenderer>(true)
                .Where(marker => marker != null && marker != owner)
                .Any(marker => marker.GeneratedBones.Contains(bone));
        }

        private static BlendShareArtifact SaveArtifactToAsset(
            Object targetSource,
            IEnumerable<BlendShareObject> appliedPatches,
            string meshContainerAssetPath,
            string path)
        {
            Object[] allAssets = AssetDatabase.LoadAllAssetsAtPath(meshContainerAssetPath);
            if (allAssets == null)
            {
                return null;
            }

            var deduplicatedPatches = BlendSharePatchIdUtility.DeduplicateByPatchId(appliedPatches).ToArray();
            var uniquePaths = deduplicatedPatches
                .SelectMany(patch => patch.Meshes ?? Array.Empty<MeshDataObject>())
                .Where(meshData => meshData != null)
                .Select(meshData => MeshNodePath.Normalize(meshData.m_Path))
                .Distinct()
                .ToArray();

            var root = AssetDatabase.LoadAssetAtPath<GameObject>(meshContainerAssetPath);
            var meshesByPath = BuildMeshesByPath(meshContainerAssetPath, allAssets);
            var descriptors = new List<BlendShareMeshDescriptor>();
            foreach (string meshPath in uniquePaths)
            {
                if (!meshesByPath.TryGetValue(meshPath, out var mesh))
                {
                    continue;
                }

                descriptors.Add(new BlendShareMeshDescriptor
                {
                    m_NodePath = meshPath,
                    m_Mesh = mesh,
                    m_SkinBinding = BuildSkinBinding(root, meshPath)
                });
            }

            return SaveArtifactToAsset(
                targetSource,
                deduplicatedPatches,
                descriptors,
                BuildArmatureArtifact(deduplicatedPatches),
                path);
        }

        private static BlendShareArtifact SaveArtifactToAsset(
            Object targetSource,
            IEnumerable<BlendShareObject> appliedPatches,
            IEnumerable<Mesh> meshes,
            Object bindingSource,
            string path)
        {
            var deduplicatedPatches = BlendSharePatchIdUtility.DeduplicateByPatchId(appliedPatches).ToArray();
            GameObject root = bindingSource as GameObject;
            var descriptors = (meshes ?? Enumerable.Empty<Mesh>())
                .Where(mesh => mesh != null)
                .Select(mesh =>
                {
                    string meshPath = MeshNodePath.Normalize(mesh.name);
                    return new BlendShareMeshDescriptor
                    {
                        m_NodePath = meshPath,
                        m_Mesh = mesh,
                        m_SkinBinding = BuildSkinBinding(root, meshPath)
                    };
                })
                .GroupBy(descriptor => descriptor.m_NodePath)
                .Select(group => group.First())
                .ToArray();

            return SaveArtifactToAsset(
                targetSource,
                deduplicatedPatches,
                descriptors,
                BuildArmatureArtifact(deduplicatedPatches),
                path);
        }

        private static BlendShareArtifact SaveArtifactToAsset(
            Object targetSource,
            IReadOnlyCollection<BlendShareObject> appliedPatches,
            IEnumerable<BlendShareMeshDescriptor> meshDescriptors,
            UnityArmatureObject armature,
            string path)
        {
            var descriptors = (meshDescriptors ?? Enumerable.Empty<BlendShareMeshDescriptor>())
                .Where(descriptor => descriptor != null && descriptor.m_Mesh != null)
                .GroupBy(descriptor => MeshNodePath.Normalize(descriptor.m_NodePath))
                .Select(group =>
                {
                    var descriptor = group.First();
                    descriptor.m_NodePath = MeshNodePath.Normalize(descriptor.m_NodePath);
                    return descriptor;
                })
                .ToArray();
            if (descriptors.Length == 0)
            {
                return null;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? "Assets");
            var existingAsset = AssetDatabase.LoadAssetAtPath<BlendShareArtifact>(path);
            var artifact = existingAsset != null ? existingAsset : ScriptableObject.CreateInstance<BlendShareArtifact>();

            try
            {
                AssetDatabase.StartAssetEditing();
                AssetDatabase.DisallowAutoRefresh();
                if (existingAsset == null)
                {
                    AssetDatabase.CreateAsset(artifact, path);
                }

                var existingSubAssets = AssetDatabase.LoadAllAssetRepresentationsAtPath(path);
                var existingMeshesByName = existingSubAssets
                    .OfType<Mesh>()
                    .GroupBy(mesh => mesh.name)
                    .ToDictionary(group => group.Key, group => group.First());
                var existingArmature = existingSubAssets
                    .OfType<UnityArmatureObject>()
                    .FirstOrDefault(candidate => candidate.name == "Armature") ??
                    existingSubAssets.OfType<UnityArmatureObject>().FirstOrDefault();

                foreach (var descriptor in descriptors)
                {
                    string meshName = MeshNodePath.Normalize(descriptor.m_NodePath);
                    if (existingMeshesByName.TryGetValue(meshName, out var existingMesh))
                    {
                        EditorUtility.CopySerialized(descriptor.m_Mesh, existingMesh);
                        existingMesh.name = meshName;
                        descriptor.m_Mesh = existingMesh;
                        EditorUtility.SetDirty(existingMesh);
                        continue;
                    }

                    var meshToAdd = Object.Instantiate(descriptor.m_Mesh);
                    meshToAdd.name = meshName;
                    AssetDatabase.AddObjectToAsset(meshToAdd, artifact);
                    descriptor.m_Mesh = meshToAdd;
                    EditorUtility.SetDirty(meshToAdd);
                }

                var desiredMeshNames = new HashSet<string>(descriptors.Select(descriptor => descriptor.m_Mesh.name));
                foreach (var mesh in existingMeshesByName.Values)
                {
                    if (!desiredMeshNames.Contains(mesh.name))
                    {
                        Object.DestroyImmediate(mesh, true);
                    }
                }

                var armatureToSave = existingArmature != null
                    ? existingArmature
                    : ScriptableObject.CreateInstance<UnityArmatureObject>();
                armatureToSave.name = "Armature";
                armatureToSave.SetBones(armature?.Bones ?? Array.Empty<UnityArmatureBoneData>());
                if (existingArmature == null)
                {
                    AssetDatabase.AddObjectToAsset(armatureToSave, artifact);
                }
                foreach (var staleArmature in existingSubAssets.OfType<UnityArmatureObject>())
                {
                    if (staleArmature != armatureToSave)
                    {
                        Object.DestroyImmediate(staleArmature, true);
                    }
                }

                artifact.m_TargetSource = targetSource;
                artifact.m_TargetSourceHash = CalculateHash(targetSource);
                artifact.m_AppliedBlendShares = BlendSharePatchIdUtility.DeduplicateByPatchId(appliedPatches).ToArray();
                artifact.m_Meshes = descriptors;
                artifact.m_Armature = armatureToSave;

                EditorUtility.SetDirty(armatureToSave);
                EditorUtility.SetDirty(artifact);
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.AllowAutoRefresh();
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }

            return artifact;
        }

        private static BlendShareArtifact SaveArtifactToAsset(BlendShareArtifact artifact, string path)
        {
            if (artifact == null)
            {
                return null;
            }

            return SaveArtifactToAsset(
                artifact.m_TargetSource,
                artifact.m_AppliedBlendShares,
                artifact.m_Meshes,
                artifact.m_Armature,
                path);
        }

        private static bool ApplySkinBinding(
            SkinnedMeshRenderer renderer,
            BlendShareSkinBindingDescriptor skinBinding,
            UnityArmatureObject armature,
            Transform targetRoot,
            Transform bonePathRoot,
            Dictionary<string, Transform> transformsByPath,
            IReadOnlyDictionary<string, Transform> boneTransformOverrides,
            ApplyState applyState,
            BlendShareArtifactApplyResult result,
            BlendShareArtifactApplyOptions options)
        {
            if (skinBinding == null)
            {
                return true;
            }

            int diagnosticCountBefore = result.Diagnostics.Count;
            var originalBonesByPath = BuildOriginalBonePathLookup(renderer, bonePathRoot != null ? bonePathRoot : targetRoot);
            var resolvedBones = new List<Transform>();
            foreach (string bonePath in skinBinding.m_BonePaths ?? Array.Empty<string>())
            {
                var bone = ResolveBindingTransform(
                    MeshNodePath.Normalize(bonePath),
                    armature,
                    targetRoot,
                    transformsByPath,
                    originalBonesByPath,
                    boneTransformOverrides,
                    applyState,
                    result,
                    options);
                resolvedBones.Add(bone);
            }

            var rootBone = ResolveBindingTransform(
                MeshNodePath.Normalize(skinBinding.m_RootBonePath),
                armature,
                targetRoot,
                transformsByPath,
                originalBonesByPath,
                boneTransformOverrides,
                applyState,
                result,
                options);
            renderer.bones = resolvedBones.ToArray();
            renderer.rootBone = rootBone != null ? rootBone : targetRoot;
            return result.Diagnostics.Count == diagnosticCountBefore;
        }

        private static void ApplyArmatureTransforms(
            UnityArmatureObject armature,
            Transform targetRoot,
            Dictionary<string, Transform> transformsByPath,
            IReadOnlyDictionary<string, Transform> boneTransformOverrides,
            ApplyState applyState,
            BlendShareArtifactApplyResult result,
            BlendShareArtifactApplyOptions options)
        {
            if (armature == null)
            {
                return;
            }

            foreach (string path in armature.GetBonePathsInHierarchyOrder())
            {
                if (boneTransformOverrides != null &&
                    boneTransformOverrides.TryGetValue(path, out var overrideTransform) &&
                    overrideTransform != null)
                {
                    applyState.GeneratedBonesByArtifactPath[path] = overrideTransform;
                    result.AddGeneratedBone(path, overrideTransform, false);
                    continue;
                }

                if (transformsByPath.TryGetValue(path, out var existing) && existing != null)
                {
                    ApplyAbsoluteBoneTransform(existing, armature.GetBone(path), options);
                    applyState.GeneratedBonesByArtifactPath[path] = existing;
                    continue;
                }

                ResolveOrCreateGeneratedBone(
                    path,
                    armature,
                    targetRoot,
                    transformsByPath,
                    transformsByPath,
                    boneTransformOverrides,
                    applyState,
                    result,
                    options,
                    new HashSet<string>());
            }
        }

        private static Transform ResolveTransform(
            IReadOnlyDictionary<string, Transform> transformsByPath,
            Transform targetRoot,
            string path)
        {
            string normalized = MeshNodePath.Normalize(path);
            if (normalized == MeshNodePath.Root)
            {
                return targetRoot;
            }

            return transformsByPath.TryGetValue(normalized, out var transform) ? transform : null;
        }

        private static Transform ResolveBindingTransform(
            string path,
            UnityArmatureObject armature,
            Transform targetRoot,
            Dictionary<string, Transform> transformsByPath,
            IReadOnlyDictionary<string, Transform> originalBonesByPath,
            IReadOnlyDictionary<string, Transform> boneTransformOverrides,
            ApplyState applyState,
            BlendShareArtifactApplyResult result,
            BlendShareArtifactApplyOptions options)
        {
            path = MeshNodePath.Normalize(path);
            if (path == MeshNodePath.Root)
            {
                return targetRoot;
            }

            if (boneTransformOverrides != null &&
                boneTransformOverrides.TryGetValue(path, out var overrideTransform) &&
                overrideTransform != null)
            {
                applyState.GeneratedBonesByArtifactPath[path] = overrideTransform;
                result.AddGeneratedBone(path, overrideTransform, false);
                return overrideTransform;
            }

            if (originalBonesByPath.TryGetValue(path, out var originalBone) && originalBone != null)
            {
                ApplyAbsoluteBoneTransform(originalBone, armature?.GetBone(path), options);
                return originalBone;
            }

            var armatureBone = armature != null ? armature.GetBone(path) : null;
            if (armatureBone != null)
            {
                return ResolveOrCreateGeneratedBone(
                    path,
                    armature,
                    targetRoot,
                    transformsByPath,
                    originalBonesByPath,
                    boneTransformOverrides,
                    applyState,
                    result,
                    options,
                    new HashSet<string>());
            }

            return ResolveTransform(transformsByPath, targetRoot, path);
        }

        private static Transform ResolveOrCreateGeneratedBone(
            string path,
            UnityArmatureObject armature,
            Transform targetRoot,
            Dictionary<string, Transform> transformsByPath,
            IReadOnlyDictionary<string, Transform> originalBonesByPath,
            IReadOnlyDictionary<string, Transform> boneTransformOverrides,
            ApplyState applyState,
            BlendShareArtifactApplyResult result,
            BlendShareArtifactApplyOptions options,
            HashSet<string> resolving)
        {
            path = MeshNodePath.Normalize(path);
            if (path == MeshNodePath.Root)
            {
                return targetRoot;
            }

            if (boneTransformOverrides != null &&
                boneTransformOverrides.TryGetValue(path, out var overrideTransform) &&
                overrideTransform != null)
            {
                applyState.GeneratedBonesByArtifactPath[path] = overrideTransform;
                result.AddGeneratedBone(path, overrideTransform, false);
                return overrideTransform;
            }

            if (originalBonesByPath.TryGetValue(path, out var originalBone) && originalBone != null)
            {
                ApplyAbsoluteBoneTransform(originalBone, armature?.GetBone(path), options);
                return originalBone;
            }

            if (applyState.GeneratedBonesByArtifactPath.TryGetValue(path, out var generated) && generated != null)
            {
                result.AddGeneratedBone(path, generated, false);
                return generated;
            }

            var bone = armature != null ? armature.GetBone(path) : null;
            if (bone == null || !bone.m_CreateIfMissing)
            {
                result.AddDiagnostic($"Cannot resolve bone '{path}'.");
                return null;
            }

            if (!resolving.Add(path))
            {
                result.AddDiagnostic($"Armature contains a parent cycle at '{path}'.");
                return null;
            }

            string parentPath = bone.ParentPath;
            var parent = ResolveGeneratedBoneParent(
                parentPath,
                armature,
                targetRoot,
                transformsByPath,
                originalBonesByPath,
                boneTransformOverrides,
                applyState,
                result,
                options,
                resolving);
            resolving.Remove(path);
            if (parent == null)
            {
                return null;
            }

            bool pathCollision = transformsByPath.TryGetValue(path, out var existingAtPath) && existingAtPath != null;
            if (pathCollision)
            {
                if (IsCompatibleGeneratedBone(existingAtPath, parent, bone))
                {
                    applyState.GeneratedBonesByArtifactPath[path] = existingAtPath;
                    result.AddGeneratedBone(path, existingAtPath, false);
                    return existingAtPath;
                }

                result.AddDiagnostic($"Bone path '{path}' already exists with an incompatible parent or local transform.");
                return null;
            }

            string leafName = MeshNodePath.LeafName(path);
            var created = new GameObject(leafName);
            if (options.UseUndo)
            {
                Undo.RegisterCreatedObjectUndo(created, "Create BlendShare Artifact Bone");
                Undo.RecordObject(parent, "Create BlendShare Artifact Bone");
            }
            created.transform.SetParent(parent, false);
            bone.LocalTransform.ApplyTo(created.transform);

            string actualPath = MeshNodePath.GetRelativePath(created.transform, targetRoot);
            transformsByPath[MeshNodePath.Normalize(actualPath)] = created.transform;
            transformsByPath[path] = created.transform;
            applyState.GeneratedBonesByArtifactPath[path] = created.transform;
            result.AddGeneratedBone(path, created.transform, true);
            MarkDirty(parent, options);
            return created.transform;
        }

        private static Transform ResolveGeneratedBoneParent(
            string parentPath,
            UnityArmatureObject armature,
            Transform targetRoot,
            Dictionary<string, Transform> transformsByPath,
            IReadOnlyDictionary<string, Transform> originalBonesByPath,
            IReadOnlyDictionary<string, Transform> boneTransformOverrides,
            ApplyState applyState,
            BlendShareArtifactApplyResult result,
            BlendShareArtifactApplyOptions options,
            HashSet<string> resolving)
        {
            parentPath = MeshNodePath.Normalize(parentPath);
            if (parentPath == MeshNodePath.Root)
            {
                return targetRoot;
            }

            if (originalBonesByPath.TryGetValue(parentPath, out var originalParent) && originalParent != null)
            {
                return originalParent;
            }

            if (armature != null && armature.HasBone(parentPath))
            {
                return ResolveOrCreateGeneratedBone(
                    parentPath,
                    armature,
                    targetRoot,
                    transformsByPath,
                    originalBonesByPath,
                    boneTransformOverrides,
                    applyState,
                    result,
                    options,
                    resolving);
            }

            return ResolveTransform(transformsByPath, targetRoot, parentPath) ?? targetRoot;
        }

        private static bool IsCompatibleGeneratedBone(Transform existing, Transform intendedParent, UnityArmatureBoneData bone)
        {
            if (existing == null || bone == null || existing.parent != intendedParent)
            {
                return false;
            }

            return Vector3.Distance(existing.localPosition, bone.LocalTransform.Position) <= 0.0001f &&
                   Quaternion.Angle(existing.localRotation, bone.LocalTransform.Rotation) <= 0.01f &&
                   Vector3.Distance(existing.localScale, bone.LocalTransform.Scale) <= 0.0001f;
        }

        private static void ApplyAbsoluteBoneTransform(
            Transform transform,
            UnityArmatureBoneData bone,
            BlendShareArtifactApplyOptions options)
        {
            if (transform == null || bone == null)
            {
                return;
            }

            RecordObject(transform, "Apply BlendShare Bone Transform", options);
            bone.LocalTransform.ApplyTo(transform);
            MarkDirty(transform, options);
        }

        private static Dictionary<string, Transform> BuildOriginalBonePathLookup(
            SkinnedMeshRenderer renderer,
            Transform targetRoot)
        {
            var result = new Dictionary<string, Transform>();
            foreach (var bone in renderer.bones ?? Array.Empty<Transform>())
            {
                if (bone == null)
                {
                    continue;
                }

                string path = MeshNodePath.Normalize(MeshNodePath.GetRelativePath(bone, targetRoot));
                if (!result.ContainsKey(path))
                {
                    result.Add(path, bone);
                }
            }

            if (renderer.rootBone != null)
            {
                string rootBonePath = MeshNodePath.Normalize(MeshNodePath.GetRelativePath(renderer.rootBone, targetRoot));
                if (!result.ContainsKey(rootBonePath))
                {
                    result.Add(rootBonePath, renderer.rootBone);
                }
            }

            return result;
        }

        private static IReadOnlyDictionary<string, Transform> BuildBoneTransformOverrideLookup(
            IReadOnlyDictionary<string, Transform> overrides)
        {
            if (overrides == null || overrides.Count == 0)
            {
                return null;
            }

            return overrides
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value != null)
                .GroupBy(pair => MeshNodePath.Normalize(pair.Key))
                .ToDictionary(group => group.Key, group => group.First().Value);
        }

        private static void RecordObject(Object obj, string name, BlendShareArtifactApplyOptions options)
        {
            if (obj != null && options.UseUndo)
            {
                Undo.RecordObject(obj, name);
            }
        }

        private static void MarkDirty(Object obj, BlendShareArtifactApplyOptions options)
        {
            if (obj != null && options.MarkObjectsDirty)
            {
                EditorUtility.SetDirty(obj);
            }
        }

        private sealed class ApplyState
        {
            public Dictionary<string, Transform> GeneratedBonesByArtifactPath { get; } = new();
        }

        private static UnityArmatureObject BuildArmatureArtifact(
            IEnumerable<BlendShareObject> patches,
            Func<BlendShareObject, MeshDataObject, bool> shouldGenerateMesh = null)
        {
            var artifact = ScriptableObject.CreateInstance<UnityArmatureObject>();
            artifact.name = "Armature";
            artifact.SetBones((patches ?? Enumerable.Empty<BlendShareObject>())
                .Where(patch => patch != null)
                .SelectMany(patch => (patch.Meshes ?? Array.Empty<MeshDataObject>())
                    .Where(meshData => meshData != null &&
                                       (shouldGenerateMesh == null || shouldGenerateMesh(patch, meshData)))
                    .Select(meshData => new
                    {
                        Feature = meshData.GetFeature<SkinWeightFeatureObject>(),
                        Mapping = GetFbxToUnityMapping(meshData)
                    }))
                .Where(item => item.Feature?.Armature != null)
                .SelectMany(item => (item.Feature.Armature.Bones ?? Array.Empty<FbxArmatureBoneData>())
                    .Where(bone => bone != null)
                    .Select(bone => new { Bone = bone, item.Mapping }))
                .GroupBy(item => MeshNodePath.Normalize(item.Bone.m_Path))
                .Select(group =>
                {
                    var items = group.ToArray();
                    var item = items[0];
                    var bone = item.Bone;
                    if (item.Mapping == null)
                    {
                        throw new InvalidOperationException($"Bone '{bone.Path}' has no valid FBX-to-Unity mapping.");
                    }

                    foreach (var candidate in items.Skip(1))
                    {
                        if (candidate.Mapping == null ||
                            !item.Mapping.SpaceConversion.ApproximatelyEquals(candidate.Mapping.SpaceConversion))
                        {
                            throw new InvalidOperationException(
                                $"Bone '{bone.Path}' is contributed through incompatible FBX importer settings.");
                        }
                    }

                    if (!bone.HasTransformData)
                    {
                        throw new InvalidOperationException(
                            $"Bone '{bone.Path}' has no FBX transform data. Re-extract the BlendShare patch.");
                    }

                    if (!item.Mapping.SpaceConversion.TryConvertLocalTransform(
                            bone.EvaluatedNodeToParentMatrix,
                            out UnityLocalTransform localTransform,
                            out string diagnostic))
                    {
                        throw new InvalidOperationException($"Cannot convert bone '{bone.Path}': {diagnostic}");
                    }

                    return new UnityArmatureBoneData(bone.m_Path, localTransform, bone.m_CreateIfMissing);
            }));
            return artifact;
        }

        private static UnityVertexMappingObject GetFbxToUnityMapping(MeshDataObject meshData)
        {
            var mapping = (meshData?.m_Mappings ?? Array.Empty<UnityVertexMappingObject>())
                .FirstOrDefault(candidate => candidate != null && candidate.m_IsValid);
            return mapping;
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

        private static Dictionary<string, Mesh> BuildMeshesByPath(string meshContainerAssetPath, IEnumerable<Object> allAssets)
        {
            var meshesByPath = new Dictionary<string, Mesh>();
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(meshContainerAssetPath);
            if (root != null)
            {
                foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (renderer.sharedMesh == null)
                    {
                        continue;
                    }

                    string path = MeshNodePath.GetRelativePath(renderer.transform, root.transform);
                    if (!meshesByPath.ContainsKey(path))
                    {
                        meshesByPath.Add(path, renderer.sharedMesh);
                    }
                }

                return meshesByPath;
            }

            foreach (var mesh in (allAssets ?? Enumerable.Empty<Object>()).OfType<Mesh>())
            {
                string path = MeshNodePath.Normalize(mesh.name);
                if (!meshesByPath.ContainsKey(path))
                {
                    meshesByPath.Add(path, mesh);
                }
            }

            return meshesByPath;
        }

        private static List<BlendShareObject> GetAppliedPatches(
            Object targetMeshContainer,
            IEnumerable<BlendShareObject> patches)
        {
            var appliedPatches = new List<BlendShareObject>();

            if (targetMeshContainer is GeneratedMeshAssetSO generatedAsset)
            {
                if (generatedAsset.m_AppliedBlendShares != null)
                {
                    appliedPatches.AddRange(generatedAsset.m_AppliedBlendShares.Where(patch => patch != null));
                }

                if (generatedAsset.m_AppliedBlendShapes != null)
                {
                    appliedPatches.AddRange(generatedAsset.m_AppliedBlendShapes
                        .Where(legacy => legacy != null)
                        .Select(BlendShareUpgradeService.UpgradeSideBySide)
                        .Where(patch => patch != null));
                }
            }

            appliedPatches.AddRange(patches ?? Enumerable.Empty<BlendShareObject>());
            return BlendSharePatchIdUtility.DeduplicateByPatchId(appliedPatches).ToList();
        }

        private static GameObject GetTargetFbx(Object targetMeshContainer)
        {
            if (targetMeshContainer is GeneratedMeshAssetSO generatedAsset)
            {
                return generatedAsset.m_OriginalFbxAsset;
            }

            return targetMeshContainer as GameObject;
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

        private static void RemoveGeneratedObjects(IEnumerable<Object> generatedObjects)
        {
            foreach (var generatedObject in generatedObjects ?? Enumerable.Empty<Object>())
            {
                if (generatedObject == null || AssetDatabase.Contains(generatedObject))
                {
                    continue;
                }

                Object.DestroyImmediate(generatedObject);
            }
        }
    }
}
