using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Triturbo.BlendShapeShare.BlendShapeData;
using Triturbo.BlendShare.Components;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Features.BoneGraph;
using Triturbo.BlendShare.Features.SkinWeights;
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
            IEnumerable<BlendShareObject> blendShares,
            string path)
        {
            var shares = blendShares?.Where(share => share != null).Distinct().ToList() ?? new List<BlendShareObject>();
            if (targetMeshContainer == null || shares.Count == 0 || string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var unityPipeline = new UnityMeshGenerationPipeline();
            var appliedBlendShares = GetAppliedBlendShares(targetMeshContainer, shares);
            GameObject targetFbx = GetTargetFbx(targetMeshContainer);
            BlendShareArtifact inMemoryArtifact = null;
            Mesh[] unsavedMeshes = Array.Empty<Mesh>();

            if (targetFbx == null || unityPipeline.CanApplyToUnityMeshes(targetMeshContainer, shares))
            {
                inMemoryArtifact = CreateInMemoryArtifact(
                    targetMeshContainer,
                    shares,
                    null,
                    appliedBlendShares);
                if (inMemoryArtifact != null)
                {
                    unsavedMeshes = (inMemoryArtifact.m_Meshes ?? Array.Empty<BlendShareMeshDescriptor>())
                        .Select(descriptor => descriptor?.m_Mesh)
                        .Where(mesh => mesh != null)
                        .ToArray();
                    try
                    {
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
                return CreateArtifactViaFbxFallback(targetFbx, targetMeshContainer, appliedBlendShares, path);
            }

            return null;
        }

        private static BlendShareArtifact CreateArtifactViaFbxFallback(
            GameObject targetFbx,
            Object targetMeshContainer,
            IEnumerable<BlendShareObject> appliedBlendShares,
            string path)
        {
#if ENABLE_FBX_SDK
            if (targetFbx == null || targetMeshContainer == null || string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var shares = appliedBlendShares?.Where(share => share != null).Distinct().ToArray()
                         ?? Array.Empty<BlendShareObject>();
            if (!new FbxGenerationPipeline().CanApply(shares))
            {
                return null;
            }

            string folder = Path.GetDirectoryName(path) ?? Application.dataPath;
            string tempAssetPath = Path.Combine(folder, $"{targetMeshContainer.name}-{Guid.NewGuid()}.fbx");
            try
            {
                if (!BlendShareGenerationService.CreateFbx(targetFbx, shares, tempAssetPath, true))
                {
                    Debug.LogError("[BlendShare] Failed to create temporary artifact FBX.");
                    return null;
                }

                return SaveArtifactToAsset(
                    targetFbx,
                    shares,
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
            IEnumerable<BlendShareObject> blendShares,
            Func<BlendShareObject, MeshDataObject, bool> shouldGenerateMesh = null)
        {
            var shares = blendShares?.Where(share => share != null).Distinct().ToArray() ?? Array.Empty<BlendShareObject>();
            if (targetMeshContainer == null || shares.Length == 0)
            {
                return null;
            }

            return new UnityMeshGenerationPipeline().CreateArtifact(
                targetMeshContainer,
                shares,
                shouldGenerateMesh,
                GetAppliedBlendShares(targetMeshContainer, shares));
        }

        public static BlendShareArtifact CreateInMemoryArtifact(
            GameObject targetRoot,
            IEnumerable<BlendShareGenerationComponent> components)
        {
            if (targetRoot == null)
            {
                return null;
            }

            var componentArray = (components ?? Enumerable.Empty<BlendShareGenerationComponent>())
                .Where(component => component != null)
                .Distinct()
                .ToArray();
            if (componentArray.Length == 0)
            {
                return null;
            }

            return new UnityMeshGenerationPipeline().CreateArtifactFromComponents(
                targetRoot,
                componentArray);
        }

        private static BlendShareArtifact CreateInMemoryArtifact(
            Object targetMeshContainer,
            IReadOnlyCollection<BlendShareObject> shares,
            Func<BlendShareObject, MeshDataObject, bool> shouldGenerateMesh,
            IReadOnlyCollection<BlendShareObject> appliedBlendShares)
        {
            return new UnityMeshGenerationPipeline().CreateArtifact(
                targetMeshContainer,
                shares,
                shouldGenerateMesh,
                appliedBlendShares);
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

                    BlendShareDestructiveApplyMarker marker = null;
                    if (options.RecordDestructiveMarkers)
                    {
                        marker = renderer.GetComponent<BlendShareDestructiveApplyMarker>();
                        if (marker == null)
                        {
                            marker = options.UseUndo
                                ? Undo.AddComponent<BlendShareDestructiveApplyMarker>(renderer.gameObject)
                                : renderer.gameObject.AddComponent<BlendShareDestructiveApplyMarker>();
                        }

                        RecordObject(marker, "Record BlendShare Apply Marker", options);
                        marker.CaptureBaseline(renderer, nodePath);
                        marker.SetAppliedBlendShares(artifact.m_AppliedBlendShares);
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

        public static bool RevertAppliedRenderer(SkinnedMeshRenderer renderer)
        {
            if (renderer == null)
            {
                return false;
            }

            var marker = renderer.GetComponent<BlendShareDestructiveApplyMarker>();
            if (marker == null || !marker.HasBaseline || marker.OriginalMesh == null)
            {
                Debug.LogError("[BlendShare Artifact] Cannot revert renderer because BlendShare baseline references are missing.", renderer);
                return false;
            }

            Undo.RecordObject(renderer, "Revert BlendShare Artifact");
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

        private static Transform[] CollectExtraBones(Transform[] originalBones, Transform[] appliedBones)
        {
            var original = new HashSet<Transform>((originalBones ?? Array.Empty<Transform>()).Where(bone => bone != null));
            return (appliedBones ?? Array.Empty<Transform>())
                .Where(bone => bone != null && !original.Contains(bone))
                .Distinct()
                .ToArray();
        }

        private static bool IsReferencedByOtherMarker(BlendShareDestructiveApplyMarker owner, Transform bone)
        {
            if (owner == null || bone == null)
            {
                return false;
            }

            return UnityEngine.Object.FindObjectsOfType<BlendShareDestructiveApplyMarker>(true)
                .Where(marker => marker != null && marker != owner)
                .Any(marker => marker.GeneratedBones.Contains(bone));
        }

        private static BlendShareArtifact SaveArtifactToAsset(
            Object targetSource,
            IEnumerable<BlendShareObject> appliedBlendShares,
            string meshContainerAssetPath,
            string path)
        {
            Object[] allAssets = AssetDatabase.LoadAllAssetsAtPath(meshContainerAssetPath);
            if (allAssets == null)
            {
                return null;
            }

            var shares = appliedBlendShares?.Where(share => share != null).Distinct().ToArray()
                         ?? Array.Empty<BlendShareObject>();
            var uniquePaths = shares
                .SelectMany(share => share.Meshes ?? Array.Empty<MeshDataObject>())
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
                shares,
                descriptors,
                BuildArmatureArtifact(shares),
                path);
        }

        private static BlendShareArtifact SaveArtifactToAsset(
            Object targetSource,
            IEnumerable<BlendShareObject> appliedBlendShares,
            IEnumerable<Mesh> meshes,
            Object bindingSource,
            string path)
        {
            var shares = appliedBlendShares?.Where(share => share != null).Distinct().ToArray()
                         ?? Array.Empty<BlendShareObject>();
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
                shares,
                descriptors,
                BuildArmatureArtifact(shares),
                path);
        }

        private static BlendShareArtifact SaveArtifactToAsset(
            Object targetSource,
            IReadOnlyCollection<BlendShareObject> appliedBlendShares,
            IEnumerable<BlendShareMeshDescriptor> meshDescriptors,
            BoneGraphObject armature,
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
                    .OfType<BoneGraphObject>()
                    .FirstOrDefault(candidate => candidate.name == "Armature") ??
                    existingSubAssets.OfType<BoneGraphObject>().FirstOrDefault();

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
                    : ScriptableObject.CreateInstance<BoneGraphObject>();
                armatureToSave.name = "Armature";
                armatureToSave.SetBones(armature?.Bones ?? Array.Empty<BoneNodeData>());
                if (existingArmature == null)
                {
                    AssetDatabase.AddObjectToAsset(armatureToSave, artifact);
                }
                foreach (var staleArmature in existingSubAssets.OfType<BoneGraphObject>())
                {
                    if (staleArmature != armatureToSave)
                    {
                        Object.DestroyImmediate(staleArmature, true);
                    }
                }

                artifact.m_TargetSource = targetSource;
                artifact.m_TargetSourceHash = CalculateHash(targetSource);
                artifact.m_AppliedBlendShares = appliedBlendShares?.Where(share => share != null).Distinct().ToArray()
                                                ?? Array.Empty<BlendShareObject>();
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
            BoneGraphObject armature,
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
            BoneGraphObject armature,
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
            BoneGraphObject armature,
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
                result.AddDiagnostic($"Bone graph contains a parent cycle at '{path}'.");
                return null;
            }

            string parentPath = MeshNodePath.Normalize(bone.m_ParentPath);
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
            if (pathCollision && IsCompatibleGeneratedBone(existingAtPath, parent, bone))
            {
                applyState.GeneratedBonesByArtifactPath[path] = existingAtPath;
                result.AddGeneratedBone(path, existingAtPath, false);
                return existingAtPath;
            }

            string leafName = MeshNodePath.LeafName(path);
            var created = new GameObject(pathCollision ? CreateUniqueChildName(parent, leafName) : leafName);
            if (options.UseUndo)
            {
                Undo.RegisterCreatedObjectUndo(created, "Create BlendShare Artifact Bone");
                Undo.RecordObject(parent, "Create BlendShare Artifact Bone");
            }
            created.transform.SetParent(parent, false);
            created.transform.localPosition = bone.m_FbxLocalTranslation;
            created.transform.localRotation = Quaternion.Euler(bone.m_FbxLocalEulerRotation);
            created.transform.localScale = bone.m_FbxLocalScale == Vector3.zero ? Vector3.one : bone.m_FbxLocalScale;

            string actualPath = MeshNodePath.GetRelativePath(created.transform, targetRoot);
            transformsByPath[MeshNodePath.Normalize(actualPath)] = created.transform;
            if (!pathCollision)
            {
                transformsByPath[path] = created.transform;
            }
            applyState.GeneratedBonesByArtifactPath[path] = created.transform;
            result.AddGeneratedBone(path, created.transform, true);
            MarkDirty(parent, options);
            return created.transform;
        }

        private static Transform ResolveGeneratedBoneParent(
            string parentPath,
            BoneGraphObject armature,
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

        private static bool IsCompatibleGeneratedBone(Transform existing, Transform intendedParent, BoneNodeData bone)
        {
            if (existing == null || bone == null || existing.parent != intendedParent)
            {
                return false;
            }

            var intendedScale = bone.m_FbxLocalScale == Vector3.zero ? Vector3.one : bone.m_FbxLocalScale;
            return Vector3.Distance(existing.localPosition, bone.m_FbxLocalTranslation) <= 0.0001f &&
                   Quaternion.Angle(existing.localRotation, Quaternion.Euler(bone.m_FbxLocalEulerRotation)) <= 0.01f &&
                   Vector3.Distance(existing.localScale, intendedScale) <= 0.0001f;
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

        private static string CreateUniqueChildName(Transform parent, string desiredName)
        {
            desiredName = string.IsNullOrWhiteSpace(desiredName) ? "BlendShareBone" : desiredName;
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

        private static BoneGraphObject BuildArmatureArtifact(
            IEnumerable<BlendShareObject> blendShares,
            Func<BlendShareObject, MeshDataObject, bool> shouldGenerateMesh = null)
        {
            var artifact = ScriptableObject.CreateInstance<BoneGraphObject>();
            artifact.name = "Armature";
            artifact.SetBones((blendShares ?? Enumerable.Empty<BlendShareObject>())
                .Where(share => share != null)
                .SelectMany(share => (share.Meshes ?? Array.Empty<MeshDataObject>())
                    .Where(meshData => meshData != null &&
                                       (shouldGenerateMesh == null || shouldGenerateMesh(share, meshData)))
                    .Select(meshData => new
                    {
                        Feature = meshData.GetFeature<SkinWeightFeatureObject>(),
                        Mapping = GetFbxToUnityMapping(meshData)
                    }))
                .Where(item => item.Feature?.m_BoneGraph != null)
                .SelectMany(item => (item.Feature.m_BoneGraph.Bones ?? Array.Empty<BoneNodeData>())
                    .Where(bone => bone != null)
                    .Select(bone => new { Bone = bone, item.Mapping }))
                .GroupBy(item => MeshNodePath.Normalize(item.Bone.m_Path))
                .Select(group =>
                {
                    var item = group.First();
                    var bone = item.Bone;
                    return new BoneNodeData(
                        bone.m_Path,
                        bone.m_ParentPath,
                        item.Mapping != null ? item.Mapping.ConvertFbxVectorToUnity(bone.m_FbxLocalTranslation) : bone.m_FbxLocalTranslation,
                        bone.m_FbxLocalEulerRotation,
                        bone.m_FbxLocalScale,
                        bone.m_CreateIfMissing);
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

        private static List<BlendShareObject> GetAppliedBlendShares(
            Object targetMeshContainer,
            IEnumerable<BlendShareObject> shares)
        {
            var appliedBlendShares = new List<BlendShareObject>();

            if (targetMeshContainer is GeneratedMeshAssetSO generatedAsset)
            {
                if (generatedAsset.m_AppliedBlendShares != null)
                {
                    appliedBlendShares.AddRange(generatedAsset.m_AppliedBlendShares.Where(share => share != null));
                }

                if (generatedAsset.m_AppliedBlendShapes != null)
                {
                    appliedBlendShares.AddRange(generatedAsset.m_AppliedBlendShapes
                        .Where(legacy => legacy != null)
                        .Select(BlendShareUpgradeService.UpgradeSideBySide)
                        .Where(share => share != null));
                }
            }

            appliedBlendShares.AddRange(shares ?? Enumerable.Empty<BlendShareObject>());
            return appliedBlendShares
                .Where(share => share != null)
                .Distinct()
                .ToList();
        }

        private static GameObject GetTargetFbx(Object targetMeshContainer)
        {
            if (targetMeshContainer is GeneratedMeshAssetSO generatedAsset)
            {
                return generatedAsset.m_OriginalFbxGo;
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

            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            using (var sha256 = SHA256.Create())
            {
                byte[] hashBytes = sha256.ComputeHash(stream);
                return BitConverter.ToString(hashBytes).ToLowerInvariant().Replace("-", string.Empty);
            }
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
