
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEditor;
using System.Linq;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Hashing;
using Object = UnityEngine.Object;

namespace Triturbo.BlendShapeShare.BlendShapeData
{

    // a container for mesh assets
    [PreferBinarySerialization]
    [Obsolete("GeneratedMeshAssetSO is a legacy generated mesh container. Use BlendShareArtifact for new generated assets.")]
    public class GeneratedMeshAssetSO : ScriptableObject
    {
        public GameObject m_OriginalFbxAsset;
        public string m_OriginalFbxHash;
        public BlendShapeDataSO[]  m_AppliedBlendShapes;
        public BlendShareObject[] m_AppliedBlendShares;



        /// <summary>
        /// Applies all meshes stored in this asset to matching <see cref="SkinnedMeshRenderer"/>s
        /// under the specified <paramref name="target"/> transform.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This method requires the asset to be a persistent asset (saved in the Project, not a scene object),
        /// since it loads sub-assets using <see cref="UnityEditor.AssetDatabase"/>.
        /// </para>
        /// <para>
        /// Meshes are matched by their <see cref="Mesh.name"/> (or the GameObject name if the renderer
        /// has missing <see cref="SkinnedMeshRenderer.sharedMesh"/>). All renderers with matching names will be updated.
        /// </para>
        /// <para>
        /// If multiple renderers share the same mesh name, the mesh is applied to all of them and an
        /// error is logged for visibility.
        /// </para>
        /// <para>
        /// This operation is Undo/Redo compatible — Unity’s <see cref="Undo"/> system records
        /// each mesh assignment so that changes can be reverted or redone safely.
        /// </para>
        /// </remarks>
        /// <param name="target">The root transform whose child renderers will receive mesh assignments.</param>
        /// <exception cref="System.ArgumentNullException">
        /// Thrown if <paramref name="target"/> is null.
        /// </exception>
        public void ApplyMesh(Transform target)
        {
            string assetPath = AssetDatabase.GetAssetPath(this);
            if (string.IsNullOrEmpty(assetPath))
            {
                Debug.LogError($"[BlendShare Apply Mesh]: Invalid asset path for {name}.");
                return;
            }

            Object[] subAssets = AssetDatabase.LoadAllAssetRepresentationsAtPath(assetPath);

            // Build name -> renderer list map
            var meshRenderers = new Dictionary<string, List<SkinnedMeshRenderer>>();
            foreach (var renderer in target.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                string key = renderer.sharedMesh != null ? renderer.sharedMesh.name : renderer.gameObject.name;
                if (!meshRenderers.TryGetValue(key, out var list))
                {
                    list = new List<SkinnedMeshRenderer>();
                    meshRenderers[key] = list;
                }
                list.Add(renderer);
            }

            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName($"[BlendShare] Apply Meshes to {target.name}");
            // Apply meshes
            foreach (var asset in subAssets)
            {
                if (asset is Mesh mesh)
                {
                    if (meshRenderers.TryGetValue(mesh.name, out var renderers))
                    {
                        if (renderers.Count > 1)
                        {
                            Debug.LogWarning(
                                $"[BlendShare Apply Mesh] Multiple renderers share the same mesh name '{mesh.name}' under '{target.name}'.",
                                target
                            );
                        }

                        foreach (var targetMeshRenderer in renderers)
                        {
                            // Record before modification for undo/redo
                            Undo.RecordObject(targetMeshRenderer, "Apply Mesh");
                            targetMeshRenderer.sharedMesh = mesh;
                            EditorUtility.SetDirty(targetMeshRenderer);
                        }
                    }
                    else
                    {
                        Debug.LogError($"[BlendShare Apply Mesh] Mesh '{mesh.name}' not found in target '{target.name}'.", target);
                    }
                }
            }

            // Finalize undo group
            Undo.CollapseUndoOperations(undoGroup);
        }

        /// <summary>
        /// Creates and saves a <see cref="GeneratedMeshAssetSO"/> asset by copying meshes from a specified container asset,
        /// based on the mesh information included in the provided <see cref="BlendShapeDataSO"/> instances.
        /// </summary>
        /// <param name="originalFbx">
        /// The original FBX <see cref="GameObject"/> used as a reference for generating the meshes.
        /// </param>
        /// <param name="appliedBlendShapeData">
        /// The collection of applied <see cref="BlendShapeDataSO"/> instances used to generate the meshes and define which meshes should be included.
        /// </param>
        /// <param name="meshContainerAsset">
        /// The asset (e.g., an FBX file or <see cref="GeneratedMeshAssetSO"/>) that contains the original meshes to be copied.
        /// </param>
        /// <param name="path">
        /// The file path where the new <see cref="GeneratedMeshAssetSO"/> will be created.
        /// </param>
        /// <returns>
        /// The created <see cref="GeneratedMeshAssetSO"/> instance, or <see langword="null"/> if the container asset path is invalid.
        /// </returns>
        public static GeneratedMeshAssetSO SaveMeshesToAsset(
            GameObject originalFbx,
            IEnumerable<BlendShapeDataSO> appliedBlendShapeData,
            Object meshContainerAsset,
            string path)
        {
            string targetPath = AssetDatabase.GetAssetPath(meshContainerAsset);
            if(string.IsNullOrEmpty(targetPath)) return null;
            return SaveMeshesToAsset(originalFbx, appliedBlendShapeData, targetPath, path);
        }
        /// <summary>
        /// Creates and saves a <see cref="GeneratedMeshAssetSO"/> asset by copying meshes from a specified container asset,
        /// based on the mesh information included in the provided <see cref="BlendShapeDataSO"/> instances.
        /// </summary>
        /// <param name="originalFbx">
        /// The original FBX <see cref="GameObject"/> used as a reference for generating the meshes.
        /// </param>
        /// <param name="appliedBlendShapeData">
        /// The collection of applied <see cref="BlendShapeDataSO"/> instances used to generate the meshes and define which meshes should be included.
        /// </param>
        /// <param name="meshContainerAssetPath">
        /// The path of asset (e.g., an FBX file or <see cref="GeneratedMeshAssetSO"/>) that contains the original meshes to be copied.
        /// </param>
        /// <param name="path">
        /// The file path where the new <see cref="GeneratedMeshAssetSO"/> will be created.
        /// </param>
        /// <returns>
        /// The created <see cref="GeneratedMeshAssetSO"/> instance, or <see langword="null"/> if the container asset path is invalid.
        /// </returns>
        public static GeneratedMeshAssetSO SaveMeshesToAsset(
            GameObject originalFbx,
            IEnumerable<BlendShapeDataSO> appliedBlendShapeData,
            string meshContainerAssetPath,
            string path)
        {
            Object[] allAssets = AssetDatabase.LoadAllAssetsAtPath(meshContainerAssetPath);
            if(allAssets == null) return null;

            var uniqueMeshNames = appliedBlendShapeData
                .SelectMany(data => data.m_MeshDataList)
                .Select(meshData => meshData.m_MeshName)
                .Distinct()
                .ToArray();

            List<Mesh> meshesList = new List<Mesh>(uniqueMeshNames.Length);

            foreach (var meshName in uniqueMeshNames)
            {
                Mesh targetMesh = allAssets
                    .OfType<Mesh>()
                    .FirstOrDefault(mesh => mesh.name == meshName);
                if (targetMesh == null) continue;
                meshesList.Add(targetMesh);
            }

            return SaveMeshesToAsset(originalFbx, appliedBlendShapeData, meshesList, path);
        }

        /// <summary>
        /// Creates and saves a <see cref="GeneratedMeshAssetSO"/> asset that contains the specified meshes and related metadata.
        /// </summary>
        /// <param name="originalFbx">
        /// The original FBX <see cref="GameObject"/> used as a reference for generating the meshes.
        /// Its information is stored in the resulting asset.
        /// </param>
        /// <param name="appliedBlendShapeData">
        /// The collection of applied <see cref="BlendShapeDataSO"/> instances used to generate the meshes.
        /// </param>
        /// <param name="meshes">
        /// The set of meshes to be saved as sub-assets under the generated asset.
        /// </param>
        /// <param name="path">
        /// The file path where the new asset will be created.
        /// </param>
        /// <returns>
        /// The created <see cref="GeneratedMeshAssetSO"/> instance.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown if the specified <paramref name="path"/> is null, empty, or whitespace.
        /// </exception>
        public static GeneratedMeshAssetSO SaveMeshesToAsset(
            GameObject originalFbx,
            IEnumerable<BlendShapeDataSO> appliedBlendShapeData,
            IEnumerable<Mesh> meshes,
            string path)
        {
            return SaveMeshesToAsset(
                originalFbx,
                appliedBlendShapeData,
                meshes,
                path,
                Enumerable.Empty<Object>());
        }

        private static GeneratedMeshAssetSO SaveMeshesToAsset(
            GameObject originalFbx,
            IEnumerable<BlendShapeDataSO> appliedBlendShapeData,
            IEnumerable<Mesh> meshes,
            string path,
            IEnumerable<Object> additionalSubAssets)
        {
            if (meshes == null) return null;
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Path must be non-empty", nameof(path));
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? "Assets");

            BlendShapeDataSO[] appliedBlendShapes = appliedBlendShapeData?.ToArray() ?? Array.Empty<BlendShapeDataSO>();
            Mesh[] meshArray = meshes.Where(mesh => mesh != null).ToArray();
            Object[] additionalObjects = (additionalSubAssets ?? Enumerable.Empty<Object>())
                .Where(obj => obj != null)
                .GroupBy(GetGeneratedObjectKey)
                .Select(group => group.First())
                .ToArray();
            var existingAsset = AssetDatabase.LoadAssetAtPath<GeneratedMeshAssetSO>(path);
            GeneratedMeshAssetSO asset = existingAsset != null ? existingAsset : CreateInstance<GeneratedMeshAssetSO>();

            try
            {
                AssetDatabase.StartAssetEditing();
                AssetDatabase.DisallowAutoRefresh();
                if (existingAsset == null)
                {
                    AssetDatabase.CreateAsset(asset, path);
                }

                asset.m_OriginalFbxAsset = originalFbx;
                asset.m_OriginalFbxHash = CalculateHash(originalFbx);
                asset.m_AppliedBlendShapes = appliedBlendShapes;
                asset.m_AppliedBlendShares = Array.Empty<BlendShareObject>();
                EditorUtility.SetDirty(asset);

                var existingMeshesByName = AssetDatabase.LoadAllAssetRepresentationsAtPath(path)
                    .OfType<Mesh>()
                    .GroupBy(mesh => mesh.name)
                    .ToDictionary(group => group.Key, group => group.First());
                var existingGeneratedObjectsByKey = AssetDatabase.LoadAllAssetRepresentationsAtPath(path)
                    .Where(obj => obj != null && !(obj is Mesh))
                    .GroupBy(GetGeneratedObjectKey)
                    .ToDictionary(group => group.Key, group => group.First());

                var desiredMeshNames = new HashSet<string>(meshArray.Select(mesh => mesh.name));
                var desiredGeneratedObjectKeys = new HashSet<string>(additionalObjects.Select(GetGeneratedObjectKey));

                // Reuse same-name mesh sub-assets to keep their local file IDs stable.
                foreach (var mesh in meshArray)
                {
                    if (existingMeshesByName.TryGetValue(mesh.name, out var existingMesh))
                    {
                        EditorUtility.CopySerialized(mesh, existingMesh);
                        existingMesh.name = mesh.name;
                        EditorUtility.SetDirty(existingMesh);
                        continue;
                    }

                    var meshToAdd = EditorUtility.IsPersistent(mesh) ? Instantiate(mesh) : mesh;
                    meshToAdd.name = mesh.name;
                    AssetDatabase.AddObjectToAsset(meshToAdd, asset);
                    EditorUtility.SetDirty(meshToAdd);
                }

                foreach (var generatedObject in additionalObjects)
                {
                    string key = GetGeneratedObjectKey(generatedObject);
                    if (existingGeneratedObjectsByKey.TryGetValue(key, out var existingObject))
                    {
                        if (existingObject != generatedObject)
                        {
                            EditorUtility.CopySerialized(generatedObject, existingObject);
                        }

                        existingObject.name = generatedObject.name;
                        EditorUtility.SetDirty(existingObject);
                        continue;
                    }

                    var objectToAdd = EditorUtility.IsPersistent(generatedObject)
                        ? Instantiate(generatedObject)
                        : generatedObject;
                    objectToAdd.name = generatedObject.name;
                    AssetDatabase.AddObjectToAsset(objectToAdd, asset);
                    EditorUtility.SetDirty(objectToAdd);
                }

                foreach (var mesh in existingMeshesByName.Values)
                {
                    if (desiredMeshNames.Contains(mesh.name))
                    {
                        continue;
                    }

                    Object.DestroyImmediate(mesh, true);
                }

                foreach (var entry in existingGeneratedObjectsByKey)
                {
                    if (desiredGeneratedObjectKeys.Contains(entry.Key))
                    {
                        continue;
                    }

                    Object.DestroyImmediate(entry.Value, true);
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.AllowAutoRefresh();
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }
            return asset;
        }

        [Obsolete("SaveBlendShareMeshesToAsset creates the legacy GeneratedMeshAssetSO format. Use BlendShareArtifactService.CreateArtifact for new editor workflows.")]
        public static GeneratedMeshAssetSO SaveBlendShareMeshesToAsset(
            GameObject originalFbx,
            IEnumerable<BlendShareObject> appliedBlendShares,
            IEnumerable<Mesh> meshes,
            string path)
        {
            return SaveBlendShareMeshesToAsset(
                originalFbx,
                appliedBlendShares,
                meshes,
                Enumerable.Empty<Object>(),
                path);
        }

        /// <summary>
        /// Creates or updates a generated asset with meshes and additional feature-created subassets.
        /// </summary>
        /// <param name="originalFbx">Original FBX asset referenced by the generated asset.</param>
        /// <param name="appliedBlendShares">BlendShare assets applied to the generated output.</param>
        /// <param name="meshes">Generated meshes to save as subassets.</param>
        /// <param name="additionalSubAssets">Additional subassets created by feature generator passes.</param>
        /// <param name="path">Generated asset path.</param>
        /// <returns>The generated mesh asset, or <c>null</c> when saving fails.</returns>
        [Obsolete("SaveBlendShareMeshesToAsset creates the legacy GeneratedMeshAssetSO format. Use BlendShareArtifactService.CreateArtifact for new editor workflows.")]
        public static GeneratedMeshAssetSO SaveBlendShareMeshesToAsset(
            GameObject originalFbx,
            IEnumerable<BlendShareObject> appliedBlendShares,
            IEnumerable<Mesh> meshes,
            IEnumerable<Object> additionalSubAssets,
            string path)
        {
            var asset = SaveMeshesToAsset(
                originalFbx,
                Enumerable.Empty<BlendShapeDataSO>(),
                meshes,
                path,
                additionalSubAssets);
            if (asset == null)
            {
                return null;
            }

            asset.m_AppliedBlendShares = appliedBlendShares?.Where(share => share != null).Distinct().ToArray()
                                        ?? Array.Empty<BlendShareObject>();
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            return asset;
        }

        [Obsolete("SaveBlendShareMeshesToAsset creates the legacy GeneratedMeshAssetSO format. Use BlendShareArtifactService.CreateArtifact for new editor workflows.")]
        public static GeneratedMeshAssetSO SaveBlendShareMeshesToAsset(
            GameObject originalFbx,
            IEnumerable<BlendShareObject> appliedBlendShares,
            string meshContainerAssetPath,
            string path)
        {
            Object[] allAssets = AssetDatabase.LoadAllAssetsAtPath(meshContainerAssetPath);
            if (allAssets == null)
            {
                return null;
            }

            var blendShareArray = appliedBlendShares?.Where(share => share != null).Distinct().ToArray()
                                  ?? Array.Empty<BlendShareObject>();
            var uniquePaths = blendShareArray
                .Where(share => share != null)
                .SelectMany(share => share.Meshes)
                .Where(meshData => meshData != null)
                .Select(meshData => MeshNodePath.Normalize(meshData.m_Path))
                .Distinct()
                .ToArray();

            var meshesByPath = BuildMeshesByPath(meshContainerAssetPath, allAssets);
            var meshesList = new List<Mesh>(uniquePaths.Length);
            foreach (var meshPath in uniquePaths)
            {
                if (meshesByPath.TryGetValue(meshPath, out var targetMesh))
                {
                    meshesList.Add(targetMesh);
                }
            }

            return SaveBlendShareMeshesToAsset(originalFbx, blendShareArray, meshesList, path);
        }


        public static string CalculateHash(Object obj)
        {
            if(obj == null)
            {
                return "";
            }
            string filePath = AssetDatabase.GetAssetPath(obj);
            if(string.IsNullOrEmpty(filePath)) return "";

            return BlendShareHashUtility.Sha256File(filePath);
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

        private static string GetGeneratedObjectKey(Object obj)
        {
            if (obj == null)
            {
                return string.Empty;
            }

            string objectName = string.IsNullOrWhiteSpace(obj.name) ? obj.GetType().Name : obj.name;
            return $"{obj.GetType().FullName ?? obj.GetType().Name}::{objectName}";
        }
    }
}
