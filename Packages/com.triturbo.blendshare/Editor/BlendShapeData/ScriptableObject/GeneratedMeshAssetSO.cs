
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEditor;
using System.Linq;
using System.Security.Cryptography;
using UnityEditorInternal;
using Object = UnityEngine.Object;

namespace Triturbo.BlendShapeShare.BlendShapeData
{
    
    // a container for mesh assets
    [PreferBinarySerialization]
    public class GeneratedMeshAssetSO : ScriptableObject
    {
        public GameObject m_OriginalFbxAsset;
        public string m_OriginalFbxHash;
        public BlendShapeDataSO[]  m_AppliedBlendShapes;
        
        
        
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

            // Build name → renderer list map
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
            if (meshes == null) return null;
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Path must be non-empty", nameof(path));
        
            // Create instance
            var asset = CreateInstance<GeneratedMeshAssetSO>();
            asset.m_OriginalFbxAsset = originalFbx;
            asset.m_OriginalFbxHash = CalculateHash(originalFbx);
            asset.m_AppliedBlendShapes =  appliedBlendShapeData.ToArray();
            
            try
            {
                AssetDatabase.StartAssetEditing();
                AssetDatabase.DisallowAutoRefresh();
                AssetDatabase.CreateAsset(asset, path);
                // Add meshes as sub-assets
                foreach (var mesh in meshes)
                {
                    var meshToAdd = mesh;
                    if (EditorUtility.IsPersistent(mesh))
                    {
                        meshToAdd = Instantiate(mesh);
                        meshToAdd.name = mesh.name;
                    }
                    AssetDatabase.AddObjectToAsset(meshToAdd, asset);
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
        
        
        public static string CalculateHash(Object obj)
        {
            if(obj == null)
            {
                return "";
            }
            string filePath = AssetDatabase.GetAssetPath(obj);
            if(string.IsNullOrEmpty(filePath)) return "";

            using (FileStream stream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            {
                using (SHA256 sha256 = SHA256.Create())
                {
                    byte[] hashBytes = sha256.ComputeHash(stream);
                    return BitConverter.ToString(hashBytes).ToLower().Replace("-", "");
                }
            }
        }
    }
}


