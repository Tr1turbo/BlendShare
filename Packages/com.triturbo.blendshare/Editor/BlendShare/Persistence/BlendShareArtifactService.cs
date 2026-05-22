using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Triturbo.BlendShapeShare.BlendShapeData;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Features.BoneGraph;
using Triturbo.BlendShare.Features.SkinWeights;
using Triturbo.BlendShare.Migration;
using Triturbo.Fbx.Unity;
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

            var pipeline = new MeshFeatureGenerationPipeline();
            var appliedBlendShares = GetAppliedBlendShares(targetMeshContainer, shares);
            GameObject targetFbx = GetTargetFbx(targetMeshContainer);
            float importScale = GetImportScale(targetFbx);

            if (targetFbx != null && !pipeline.CanApplyToUnityMeshes(targetMeshContainer, shares))
            {
#if ENABLE_FBX_SDK
                if (pipeline.CanApplyToFbx(appliedBlendShares))
                {
                    string folder = Path.GetDirectoryName(path) ?? Application.dataPath;
                    string tempAssetPath = Path.Combine(folder, $"{targetMeshContainer.name}-{Guid.NewGuid()}.fbx");
                    try
                    {
                        if (!BlendShareGenerationService.CreateFbx(targetFbx, appliedBlendShares, tempAssetPath, true))
                        {
                            Debug.LogError("[BlendShare] Failed to create temporary artifact FBX.");
                            return null;
                        }

                        return SaveArtifactToAsset(
                            targetFbx,
                            appliedBlendShares,
                            tempAssetPath,
                            importScale,
                            path);
                    }
                    finally
                    {
                        AssetDatabase.MoveAssetToTrash(tempAssetPath);
                    }
                }
#endif
            }

            bool generationSucceeded = pipeline.TryGenerateUnityMeshes(
                targetMeshContainer,
                shares,
                out var generatedMeshes,
                out var generatedObjects);
            if (!generationSucceeded)
            {
                RemoveGeneratedObjects(generatedObjects);
                return null;
            }

            try
            {
                return SaveArtifactToAsset(
                    targetFbx != null ? targetFbx : targetMeshContainer,
                    appliedBlendShares,
                    generatedMeshes,
                    targetMeshContainer,
                    importScale,
                    path);
            }
            finally
            {
                RemoveGeneratedObjects(generatedMeshes);
                RemoveGeneratedObjects(generatedObjects);
            }
        }

        public static void ApplyArtifact(BlendShareArtifact artifact, Transform targetRoot)
        {
            if (artifact == null || targetRoot == null)
            {
                return;
            }

            var renderersByPath = targetRoot
                .GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .GroupBy(renderer => MeshNodePath.GetRelativePath(renderer.transform, targetRoot))
                .ToDictionary(group => MeshNodePath.Normalize(group.Key), group => group.First());

            var transformsByPath = targetRoot
                .GetComponentsInChildren<Transform>(true)
                .GroupBy(transform => MeshNodePath.GetRelativePath(transform, targetRoot))
                .ToDictionary(group => MeshNodePath.Normalize(group.Key), group => group.First());

            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName($"[BlendShare] Apply Artifact to {targetRoot.name}");

            try
            {
                ResolveArmature(artifact.m_Armature, targetRoot, transformsByPath);

                foreach (var descriptor in artifact.m_Meshes ?? Array.Empty<BlendShareMeshDescriptor>())
                {
                    if (descriptor == null || descriptor.m_Mesh == null)
                    {
                        continue;
                    }

                    string nodePath = MeshNodePath.Normalize(descriptor.m_NodePath);
                    if (!renderersByPath.TryGetValue(nodePath, out var renderer) || renderer == null)
                    {
                        Debug.LogError($"[BlendShare Artifact] Renderer path '{nodePath}' not found in target '{targetRoot.name}'.", targetRoot);
                        continue;
                    }

                    Undo.RecordObject(renderer, "Apply BlendShare Artifact");
                    renderer.sharedMesh = descriptor.m_Mesh;
                    ApplySkinBinding(renderer, descriptor.m_SkinBinding, targetRoot, transformsByPath);
                    EditorUtility.SetDirty(renderer);
                }
            }
            finally
            {
                Undo.CollapseUndoOperations(undoGroup);
            }
        }

        private static BlendShareArtifact SaveArtifactToAsset(
            Object targetSource,
            IEnumerable<BlendShareObject> appliedBlendShares,
            string meshContainerAssetPath,
            float importScale,
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
                BuildArmatureArtifact(shares, importScale),
                path);
        }

        private static BlendShareArtifact SaveArtifactToAsset(
            Object targetSource,
            IEnumerable<BlendShareObject> appliedBlendShares,
            IEnumerable<Mesh> meshes,
            Object bindingSource,
            float importScale,
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
                BuildArmatureArtifact(shares, importScale),
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

        private static void ResolveArmature(
            BoneGraphObject armature,
            Transform targetRoot,
            Dictionary<string, Transform> transformsByPath)
        {
            foreach (var bone in armature?.Bones ?? Array.Empty<BoneNodeData>())
            {
                if (bone == null)
                {
                    continue;
                }

                ResolveOrCreateBone(
                    armature,
                    targetRoot,
                    transformsByPath,
                    MeshNodePath.Normalize(bone.m_Path),
                    new HashSet<string>());
            }
        }

        private static Transform ResolveOrCreateBone(
            BoneGraphObject armature,
            Transform targetRoot,
            Dictionary<string, Transform> transformsByPath,
            string path,
            HashSet<string> resolving)
        {
            path = MeshNodePath.Normalize(path);
            if (path == MeshNodePath.Root)
            {
                return targetRoot;
            }

            if (transformsByPath.TryGetValue(path, out var existing) && existing != null)
            {
                return existing;
            }

            var bone = armature != null ? armature.GetBone(path) : null;
            if (bone == null || !bone.m_CreateIfMissing)
            {
                Debug.LogError($"[BlendShare Artifact] Cannot resolve bone '{path}'.", targetRoot);
                return null;
            }

            if (!resolving.Add(path))
            {
                Debug.LogError($"[BlendShare Artifact] Bone graph contains a parent cycle at '{path}'.", targetRoot);
                return null;
            }

            string parentPath = MeshNodePath.Normalize(bone.m_ParentPath);
            var parent = ResolveOrCreateBone(armature, targetRoot, transformsByPath, parentPath, resolving);
            resolving.Remove(path);
            if (parent == null)
            {
                return null;
            }

            var created = new GameObject(MeshNodePath.LeafName(path));
            Undo.RegisterCreatedObjectUndo(created, "Create BlendShare Artifact Bone");
            Undo.RecordObject(parent, "Create BlendShare Artifact Bone");
            created.transform.SetParent(parent, false);
            created.transform.localPosition = bone.m_FbxLocalTranslation;
            created.transform.localRotation = Quaternion.Euler(bone.m_FbxLocalEulerRotation);
            created.transform.localScale = bone.m_FbxLocalScale == Vector3.zero ? Vector3.one : bone.m_FbxLocalScale;
            transformsByPath[path] = created.transform;
            EditorUtility.SetDirty(parent);
            return created.transform;
        }

        private static void ApplySkinBinding(
            SkinnedMeshRenderer renderer,
            BlendShareSkinBindingDescriptor skinBinding,
            Transform targetRoot,
            IReadOnlyDictionary<string, Transform> transformsByPath)
        {
            if (skinBinding == null)
            {
                return;
            }

            renderer.bones = (skinBinding.m_BonePaths ?? Array.Empty<string>())
                .Select(path => ResolveTransform(transformsByPath, targetRoot, path))
                .ToArray();
            renderer.rootBone = ResolveTransform(transformsByPath, targetRoot, skinBinding.m_RootBonePath) ?? targetRoot;
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

        private static BoneGraphObject BuildArmatureArtifact(IEnumerable<BlendShareObject> blendShares, float importScale)
        {
            importScale = importScale == 0f ? 1f : importScale;
            var artifact = ScriptableObject.CreateInstance<BoneGraphObject>();
            artifact.name = "Armature";
            artifact.SetBones((blendShares ?? Enumerable.Empty<BlendShareObject>())
                .Where(share => share != null)
                .SelectMany(share => share.Meshes ?? Array.Empty<MeshDataObject>())
                .Where(meshData => meshData != null)
                .Select(meshData => meshData.GetFeature<SkinWeightFeatureObject>())
                .Where(feature => feature?.m_BoneGraph != null)
                .SelectMany(feature => feature.m_BoneGraph.Bones ?? Array.Empty<BoneNodeData>())
                .Where(bone => bone != null)
                .GroupBy(bone => MeshNodePath.Normalize(bone.m_Path))
                .Select(group =>
                {
                    var bone = group.First();
                    return new BoneNodeData(
                        bone.m_Path,
                        bone.m_ParentPath,
                        bone.m_FbxLocalTranslation * importScale,
                        bone.m_FbxLocalEulerRotation,
                        bone.m_FbxLocalScale,
                        bone.m_CreateIfMissing);
                }));
            return artifact;
        }

        private static float GetImportScale(GameObject targetFbx)
        {
            float importScale = targetFbx != null ? FbxUnityAssetReader.GetImportScale(targetFbx) : 1f;
            return importScale == 0f ? 1f : importScale;
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
