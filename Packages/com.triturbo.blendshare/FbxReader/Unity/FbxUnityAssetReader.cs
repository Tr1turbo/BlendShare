using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Triturbo.Fbx.Unity
{
    public static class FbxUnityAssetReader
    {
        public static FbxReadResult<FbxDocument> Read(GameObject FbxGo, IEnumerable<string> nodePaths = null)
        {
            if (FbxGo == null)
            {
                return FbxReadResult<FbxDocument>.Failed(FbxReadStatus.InvalidArgument, "FBX asset is null.");
            }

            string assetPath = AssetDatabase.GetAssetPath(FbxGo);
            return FbxDocumentReader.Read(assetPath, nodePaths);
        }

        public static FbxReadResult<FbxMeshGeometry> ReadMesh(
            GameObject FbxGo,
            IEnumerable<string> nodePaths)
        {
            if (FbxGo == null)
            {
                return FbxReadResult<FbxMeshGeometry>.Failed(FbxReadStatus.InvalidArgument, "FBX asset is null.");
            }

            return ReadMesh(AssetDatabase.GetAssetPath(FbxGo), nodePaths);
        }

        public static FbxReadResult<FbxMeshGeometry> ReadMesh(
            string assetPath,
            IEnumerable<string> nodePaths)
        {
            var requestedPaths = NormalizeRequestedNodePaths(nodePaths);
            if (requestedPaths.Length == 0)
            {
                return FbxReadResult<FbxMeshGeometry>.Failed(
                    FbxReadStatus.InvalidArgument,
                    "At least one FBX node path is required.");
            }

            var documentResult = FbxDocumentReader.Read(assetPath, requestedPaths);
            if (!documentResult.Success)
            {
                return FbxReadResult<FbxMeshGeometry>.Failed(
                    documentResult.Status,
                    documentResult.Message,
                    documentResult.Diagnostics,
                    documentResult.CandidateMeshes);
            }

            foreach (string requestedPath in requestedPaths)
            {
                var meshResult = FindMesh(documentResult.Value, requestedPath);
                if (meshResult.Success)
                {
                    return meshResult;
                }
            }

            return FbxReadResult<FbxMeshGeometry>.Failed(
                FbxReadStatus.MeshNotFound,
                $"No FBX mesh matched node path: {string.Join(", ", requestedPaths)}.",
                candidateMeshes: documentResult.Value.ListMeshes());
        }

        public static Dictionary<string, FbxMeshGeometry> ReadMeshes(
            GameObject FbxGo,
            IEnumerable<string> nodePaths)
        {
            if (FbxGo == null)
            {
                return new Dictionary<string, FbxMeshGeometry>();
            }

            return ReadMeshes(AssetDatabase.GetAssetPath(FbxGo), nodePaths);
        }

        public static Dictionary<string, FbxMeshGeometry> ReadMeshes(
            string assetPath,
            IEnumerable<string> nodePaths)
        {
            var requestedPaths = NormalizeRequestedNodePaths(nodePaths);
            if (requestedPaths.Length == 0)
            {
                return new Dictionary<string, FbxMeshGeometry>();
            }

            var documentResult = FbxDocumentReader.Read(assetPath, requestedPaths);
            if (!documentResult.Success)
            {
                return new Dictionary<string, FbxMeshGeometry>();
            }

            var meshes = new Dictionary<string, FbxMeshGeometry>();
            foreach (string requestedPath in requestedPaths)
            {
                var meshResult = FindMesh(documentResult.Value, requestedPath);
                if (meshResult.Success)
                {
                    meshes[requestedPath] = meshResult.Value;
                }
            }

            return meshes;
        }

        public static FbxReadResult<FbxMeshGeometry> FindMesh(FbxDocument document, string nodePath)
        {
            if (document == null)
            {
                return FbxReadResult<FbxMeshGeometry>.Failed(FbxReadStatus.InvalidArgument, "FBX document is null.");
            }

            string normalizedPath = FbxNameUtility.NormalizePath(nodePath);
            if (string.IsNullOrEmpty(normalizedPath))
            {
                return FbxReadResult<FbxMeshGeometry>.Failed(
                    FbxReadStatus.InvalidArgument,
                    "FBX node path is empty.");
            }

            return document.TryFindMesh(normalizedPath);
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

        private static string[] NormalizeRequestedNodePaths(IEnumerable<string> nodePaths)
        {
            return nodePaths?
                .Select(FbxNameUtility.NormalizePath)
                .Where(candidate => !string.IsNullOrEmpty(candidate))
                .Distinct(StringComparer.Ordinal)
                .ToArray() ?? Array.Empty<string>();
        }
    }
}
