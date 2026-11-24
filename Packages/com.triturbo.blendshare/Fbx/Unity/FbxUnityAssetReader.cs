#if UNITY_EDITOR
using Triturbo.BlendShare.Fbx.Ufbx;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.Fbx.Unity
{
    /// <summary>Unity Editor helpers for reading FBX assets and their ModelImporter space settings.</summary>
    public static class FbxUnityAssetReader
    {
        /// <summary>Loads an FBX scene from a Unity imported FBX <see cref="GameObject"/> asset.</summary>
        public static FbxReadResult<UfbxScene> ReadScene(GameObject fbxGo, UfbxLoadOptions? options = null)
        {
            if (fbxGo == null)
            {
                return FbxReadResult<UfbxScene>.Failed(FbxReadStatus.InvalidArgument, "FBX asset is null.");
            }

            string assetPath = AssetDatabase.GetAssetPath(fbxGo);
            return UfbxScene.TryLoad(assetPath, options);
        }

        /// <summary>Finds a mesh attached to the node at the given hierarchy path.</summary>
        public static FbxReadResult<IFbxMesh> FindMesh(IFbxScene scene, string nodePath)
        {
            if (scene == null)
            {
                return FbxReadResult<IFbxMesh>.Failed(FbxReadStatus.InvalidArgument, "FBX scene is null.");
            }

            string normalizedPath = FbxNameUtility.NormalizePath(nodePath);
            if (string.IsNullOrEmpty(normalizedPath))
            {
                return FbxReadResult<IFbxMesh>.Failed(
                    FbxReadStatus.InvalidArgument,
                    "FBX node path is empty.");
            }

            var mesh = scene.FindMeshByNodePath(normalizedPath);
            return mesh != null
                ? FbxReadResult<IFbxMesh>.Succeeded(mesh)
                : FbxReadResult<IFbxMesh>.Failed(FbxReadStatus.NodeNotFound, $"FBX mesh node '{normalizedPath}' was not found.");
        }

        /// <summary>Reads <see cref="ModelImporter.fileScale"/> for the asset at <paramref name="assetPath"/>.</summary>
        public static float GetImportScale(string assetPath)
        {
            var importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            return importer != null ? importer.fileScale : 1f;
        }

        /// <summary>Reads <see cref="ModelImporter.fileScale"/> for the asset backing <paramref name="fbxGo"/>.</summary>
        public static float GetImportScale(GameObject fbxGo)
        {
            return fbxGo != null ? GetImportScale(AssetDatabase.GetAssetPath(fbxGo)) : 1f;
        }

        /// <summary>Reads <see cref="ModelImporter.bakeAxisConversion"/> for the asset at <paramref name="assetPath"/>.</summary>
        public static bool GetBakeAxisConversion(string assetPath)
        {
            var importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            return importer != null && importer.bakeAxisConversion;
        }

        /// <summary>Reads <see cref="ModelImporter.bakeAxisConversion"/> for the asset backing <paramref name="fbxGo"/>.</summary>
        public static bool GetBakeAxisConversion(GameObject fbxGo)
        {
            return fbxGo != null && GetBakeAxisConversion(AssetDatabase.GetAssetPath(fbxGo));
        }

        /// <summary>Returns the FBX-to-Unity importer scale/axis matrix for the asset at <paramref name="assetPath"/>.</summary>
        public static Matrix4x4 GetImporterSpaceTransform(string assetPath)
        {
            float scale = GetImportScale(assetPath);
            bool bakeAxisConversion = GetBakeAxisConversion(assetPath);
            return bakeAxisConversion
                ? Matrix4x4.Scale(new Vector3(scale, scale, -scale))
                : Matrix4x4.Scale(new Vector3(-scale, scale, scale));
        }

        /// <summary>Returns the FBX-to-Unity importer scale/axis matrix for the asset backing <paramref name="fbxGo"/>.</summary>
        public static Matrix4x4 GetImporterSpaceTransform(GameObject fbxGo)
        {
            return fbxGo != null ? GetImporterSpaceTransform(AssetDatabase.GetAssetPath(fbxGo)) : Matrix4x4.identity;
        }
    }
}
#endif
