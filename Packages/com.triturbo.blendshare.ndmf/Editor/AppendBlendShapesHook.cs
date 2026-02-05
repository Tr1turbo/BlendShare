using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using JetBrains.Annotations;
using nadena.dev.ndmf;
using Triturbo.BlendShapeShare.BlendShapeData;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShapeShare.Ndmf.Editor
{
    internal class AppendBlendShapesHook
    {
        private const string BasePath = "Packages/com.triturbo.blendshare.ndmf/Cache";
        private readonly List<string> _generatedAssets = new();
        
        internal void Process(BuildContext context)
        {
            var components = context.AvatarRootObject.transform.GetComponentsInChildren<Runtime.AppendBlendShapes>(true);
            foreach (var c in components)
            {
                if (c.target == null)
                {
                    Debug.LogWarning($"[BlendShare] AppendBlendShapes on {c.gameObject.name} has no target assigned; skipping.");
                    continue;
                }
                
                var validBlendShapes = GetValidBlendShapes(c.blendShapeData);
                var cacheHash = GetHash(validBlendShapes);
                var assetPath = $"{GetCacheAssetPath(cacheHash)}";

                var cachedMesh = AssetDatabase.LoadAssetAtPath<GeneratedMeshAssetSO>(assetPath);
                if (cachedMesh != null)
                {
                    cachedMesh.ApplyMesh(c.target.transform);
                    Debug.Log($"[BlendShare] Using cached mesh for {c.target.name}");
                    _generatedAssets.Add(cacheHash);
                    continue;
                }

                var meshRenderer = c.GetComponent<SkinnedMeshRenderer>()?.sharedMesh;
                if (meshRenderer == null)
                    continue;
                
                var newMesh = BlendShapeAppender.CreateMeshAsset(meshRenderer, validBlendShapes, assetPath);

                if (newMesh == null)
                {
                    Debug.LogWarning($"[BlendShare] Failed to create mesh for {c.target.name}");
                    continue;
                }
                
                newMesh.ApplyMesh(c.target.transform);
                _generatedAssets.Add(cacheHash);
            }
            
            CleanCache();
        }
        
        private BlendShapeDataSO[] GetValidBlendShapes(BlendShapeDataSO[] blendShapeList)
        {
            return blendShapeList
                .Where(b => b != null)
                .Distinct()
                .ToArray();
        }

        private static string GetCachePath()
        {
            var cachePath = Path.Combine(Path.GetFullPath("."), BasePath).Replace("\\", "/");

            if (Directory.Exists(cachePath))
                return cachePath;
            
            Directory.CreateDirectory(cachePath);
            AssetDatabase.Refresh();
            return cachePath;
        }

        private static string GetCacheAssetPath(string hash)
        {
            return Path.Combine(BasePath, $"{hash}.asset").Replace("\\", "/");
        }

        private void CleanCache()
        {
            foreach (var file in Directory.GetFiles(GetCachePath()))
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                if (_generatedAssets.Contains(fileName))
                    continue;
                
                if (file.EndsWith(".meta"))
                    continue;
                
                if (File.Exists(file))
                    AssetDatabase.MoveAssetToTrash(GetCacheAssetPath(fileName));

                var metaFile = GetCacheAssetPath(fileName) + ".meta";
                if (File.Exists(metaFile))
                    AssetDatabase.MoveAssetToTrash(metaFile);
            }

            AssetDatabase.Refresh();
        }

        private string GetHash(BlendShapeDataSO[] blendShapeData)
        {
            if (blendShapeData == null || blendShapeData.Length == 0)
                return string.Empty;

            var sb = new StringBuilder();

            foreach (var obj in blendShapeData)
            {
                if (obj == null)
                    continue;

                var path = AssetDatabase.GetAssetPath(obj);
                var guid = AssetDatabase.AssetPathToGUID(path);

                sb.Append(guid).Append(';');
            }

            using var md5 = MD5.Create();
            var hashBytes = md5.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));

            var shortHash = Convert.ToBase64String(hashBytes)
                .Replace("+", "")
                .Replace("/", "")
                .Replace("=", "");

            return shortHash[..10];
        }
    }
}