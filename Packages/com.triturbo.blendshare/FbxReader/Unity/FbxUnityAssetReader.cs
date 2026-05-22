using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.Fbx.Ufbx;
using UnityEditor;
using UnityEngine;

namespace Triturbo.Fbx.Unity
{
    public static class FbxUnityAssetReader
    {
        public static FbxReadResult<UfbxScene> ReadScene(GameObject FbxGo)
        {
            if (FbxGo == null)
            {
                return FbxReadResult<UfbxScene>.Failed(FbxReadStatus.InvalidArgument, "FBX asset is null.");
            }

            string assetPath = AssetDatabase.GetAssetPath(FbxGo);
            return UfbxScene.TryLoad(assetPath);
        }

        public static FbxReadResult<UfbxMesh> FindMesh(UfbxScene scene, string nodePath)
        {
            if (scene == null)
            {
                return FbxReadResult<UfbxMesh>.Failed(FbxReadStatus.InvalidArgument, "FBX scene is null.");
            }

            string normalizedPath = FbxNameUtility.NormalizePath(nodePath);
            if (string.IsNullOrEmpty(normalizedPath))
            {
                return FbxReadResult<UfbxMesh>.Failed(
                    FbxReadStatus.InvalidArgument,
                    "FBX node path is empty.");
            }

            var mesh = scene.FindMeshByNodePath(normalizedPath);
            return mesh != null
                ? FbxReadResult<UfbxMesh>.Succeeded(mesh)
                : FbxReadResult<UfbxMesh>.Failed(FbxReadStatus.NodeNotFound, $"FBX mesh node '{normalizedPath}' was not found.");
        }

        public static float GetImportScale(string assetPath)
        {
            var importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            return importer != null ? importer.fileScale : 1f;
        }

        public static float GetImportScale(GameObject FbxGo)
        {
            return FbxGo != null ? GetImportScale(AssetDatabase.GetAssetPath(FbxGo)) : 1f;
        }
    }
}
