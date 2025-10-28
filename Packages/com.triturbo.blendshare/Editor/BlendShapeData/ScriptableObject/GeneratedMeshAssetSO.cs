
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
        
        public void ApplyMesh(Transform target)
        {
            
            string assetPath = AssetDatabase.GetAssetPath(this);
            if (string.IsNullOrEmpty(assetPath))
            {
                Debug.LogError($"ApplyMesh: Invalid asset path for {name}.");
                return;
            }

            Object[] subAssets = AssetDatabase.LoadAllAssetRepresentationsAtPath(assetPath);

            // Build dictionary: use sharedMesh.name if available, otherwise GameObject.name
            var meshRenderers = target.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .ToDictionary(
                    renderer => renderer.sharedMesh != null ? renderer.sharedMesh.name : renderer.gameObject.name,
                    renderer => renderer
                );

            foreach (var asset in subAssets)
            {
                if (asset is Mesh mesh)
                {
                    if (meshRenderers.TryGetValue(mesh.name, out var targetMeshRenderer))
                    {
                        targetMeshRenderer.sharedMesh = mesh;
                    }
                    else
                    {
                        Debug.LogWarning($"Mesh '{mesh.name}' not found in target GameObject: {target.name}", target);
                    }
                }
            }
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


