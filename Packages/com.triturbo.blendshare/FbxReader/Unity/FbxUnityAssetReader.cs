using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Triturbo.FBX.Unity
{
    public static class FbxUnityAssetReader
    {
        public static FbxReadResult<FbxDocument> Read(GameObject fbxAsset, FbxReadSettings settings = null)
        {
            if (fbxAsset == null)
            {
                return FbxReadResult<FbxDocument>.Failed(FbxReadStatus.InvalidArgument, "FBX asset is null.");
            }

            string assetPath = AssetDatabase.GetAssetPath(fbxAsset);
            return FbxDocumentReader.Read(assetPath, settings);
        }

        public static FbxReadResult<FbxMeshGeometry> ReadMesh(
            GameObject fbxAsset,
            IEnumerable<string> meshPathsOrNames,
            FbxReadSettings settings = null)
        {
            if (fbxAsset == null)
            {
                return FbxReadResult<FbxMeshGeometry>.Failed(FbxReadStatus.InvalidArgument, "FBX asset is null.");
            }

            return ReadMesh(AssetDatabase.GetAssetPath(fbxAsset), meshPathsOrNames, settings);
        }

        public static FbxReadResult<FbxMeshGeometry> ReadMesh(
            string assetPath,
            IEnumerable<string> meshPathsOrNames,
            FbxReadSettings settings = null)
        {
            var requestedKeys = NormalizeRequestedKeys(meshPathsOrNames);
            if (requestedKeys.Length == 0)
            {
                return FbxReadResult<FbxMeshGeometry>.Failed(
                    FbxReadStatus.InvalidArgument,
                    "At least one FBX mesh path or name is required.");
            }

            var documentResult = FbxDocumentReader.Read(assetPath, settings);
            if (!documentResult.Success)
            {
                return FbxReadResult<FbxMeshGeometry>.Failed(
                    documentResult.Status,
                    documentResult.Message,
                    documentResult.Diagnostics,
                    documentResult.CandidateMeshes);
            }

            FbxReadResult<FbxMeshGeometry> ambiguousResult = null;
            foreach (string requestedKey in requestedKeys)
            {
                var meshResult = FindMesh(documentResult.Value, requestedKey);
                if (meshResult.Success)
                {
                    return meshResult;
                }

                if (meshResult.Status == FbxReadStatus.AmbiguousMesh && ambiguousResult == null)
                {
                    ambiguousResult = meshResult;
                }
            }

            return ambiguousResult ?? FbxReadResult<FbxMeshGeometry>.Failed(
                FbxReadStatus.MeshNotFound,
                $"No FBX mesh matched: {string.Join(", ", requestedKeys)}.",
                candidateMeshes: documentResult.Value.MeshDescriptors);
        }

        public static Dictionary<string, FbxMeshGeometry> ReadMeshes(
            GameObject fbxAsset,
            IEnumerable<string> meshPathsOrNames,
            FbxReadSettings settings = null)
        {
            if (fbxAsset == null)
            {
                return new Dictionary<string, FbxMeshGeometry>();
            }

            return ReadMeshes(AssetDatabase.GetAssetPath(fbxAsset), meshPathsOrNames, settings);
        }

        public static Dictionary<string, FbxMeshGeometry> ReadMeshes(
            string assetPath,
            IEnumerable<string> meshPathsOrNames,
            FbxReadSettings settings = null)
        {
            var requestedKeys = NormalizeRequestedKeys(meshPathsOrNames);
            if (requestedKeys.Length == 0)
            {
                return new Dictionary<string, FbxMeshGeometry>();
            }

            var documentResult = FbxDocumentReader.Read(assetPath, settings);
            if (!documentResult.Success)
            {
                return new Dictionary<string, FbxMeshGeometry>();
            }

            var meshes = new Dictionary<string, FbxMeshGeometry>();
            foreach (string requestedKey in requestedKeys)
            {
                var meshResult = FindMesh(documentResult.Value, requestedKey);
                if (meshResult.Success)
                {
                    meshes[requestedKey] = meshResult.Value;
                }
            }

            return meshes;
        }

        public static FbxReadResult<FbxMeshGeometry> FindMesh(FbxDocument document, string meshPathOrName)
        {
            if (document == null)
            {
                return FbxReadResult<FbxMeshGeometry>.Failed(FbxReadStatus.InvalidArgument, "FBX document is null.");
            }

            string normalizedKey = FbxNameUtility.NormalizePath(meshPathOrName);
            if (string.IsNullOrEmpty(normalizedKey))
            {
                return FbxReadResult<FbxMeshGeometry>.Failed(
                    FbxReadStatus.InvalidArgument,
                    "FBX mesh path or name is empty.");
            }

            var matches = FindMatches(document, normalizedKey);
            if (matches.Count == 0)
            {
                return FbxReadResult<FbxMeshGeometry>.Failed(
                    FbxReadStatus.MeshNotFound,
                    $"No FBX mesh matched '{meshPathOrName}'.",
                    candidateMeshes: document.MeshDescriptors);
            }

            if (matches.Count > 1)
            {
                return FbxReadResult<FbxMeshGeometry>.Failed(
                    FbxReadStatus.AmbiguousMesh,
                    $"More than one FBX mesh matched '{meshPathOrName}'.",
                    candidateMeshes: matches.Select(mesh => mesh.Descriptor));
            }

            return FbxReadResult<FbxMeshGeometry>.Succeeded(matches[0]);
        }

        public static float GetImportScale(string assetPath)
        {
            var importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            return importer != null ? importer.fileScale : 1f;
        }

        public static float GetImportScale(GameObject fbxAsset)
        {
            return fbxAsset != null ? GetImportScale(AssetDatabase.GetAssetPath(fbxAsset)) : 1f;
        }

        private static string[] NormalizeRequestedKeys(IEnumerable<string> meshPathsOrNames)
        {
            return meshPathsOrNames?
                .Where(candidate => !string.IsNullOrEmpty(candidate))
                .Distinct()
                .ToArray() ?? Array.Empty<string>();
        }

        private static List<FbxMeshGeometry> FindMatches(FbxDocument document, string normalizedKey)
        {
            var byExactPath = MatchingMeshes(document, mesh =>
                FbxNameUtility.NormalizePath(mesh.OwnerNode?.Path) == normalizedKey);
            if (byExactPath.Count > 0)
            {
                return byExactPath;
            }

            var byPathSuffix = MatchingMeshes(document, mesh => HasPathSuffix(mesh.OwnerNode?.Path, normalizedKey));
            if (byPathSuffix.Count > 0)
            {
                return byPathSuffix;
            }

            var byNodeName = MatchingMeshes(document, mesh =>
                FbxNameUtility.NormalizePath(mesh.OwnerNode?.Name) == normalizedKey);
            if (byNodeName.Count > 0)
            {
                return byNodeName;
            }

            return MatchingMeshes(document, mesh => FbxNameUtility.NormalizePath(mesh.Name) == normalizedKey);
        }

        private static List<FbxMeshGeometry> MatchingMeshes(
            FbxDocument document,
            Func<FbxMeshGeometry, bool> predicate)
        {
            return document.Meshes.Where(predicate).Distinct().ToList();
        }

        private static bool HasPathSuffix(string path, string normalizedKey)
        {
            path = FbxNameUtility.NormalizePath(path);
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(normalizedKey))
            {
                return false;
            }

            string[] parts = path.Split('/');
            for (int i = 1; i < parts.Length - 1; i++)
            {
                if (string.Join("/", parts, i, parts.Length - i) == normalizedKey)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
