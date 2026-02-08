using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using nadena.dev.ndmf;
using Triturbo.BlendShapeShare.BlendShapeData;
using Triturbo.BlendShapeShare.Ndmf.Runtime;
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
            var components = context.AvatarRootObject.transform.GetComponentsInChildren<AppendBlendShapes>(true);
            foreach (var c in components)
            {
                ApplyMesh(c);
                TryApplyBlendShapeWeights(c);
            }
            
            CleanCache();
        }

        private void ApplyMesh(AppendBlendShapes component)
        {
            if (component.target == null)
            {
                Debug.LogWarning($"[BlendShare] AppendBlendShapes on {component.gameObject.name} has no target assigned; skipping.");
                Logger.Log(ErrorSeverity.NonFatal, "error.no_target", component.gameObject);
                return;
            }
            
            var blendShapeDataList = component.GetAllBlendShapeData().ToArray();
            var validBlendShapes = GetValidBlendShapes(blendShapeDataList);
            var cacheHash = GetHash(validBlendShapes);
            var assetPath = $"{GetCacheAssetPath(cacheHash)}";

            var cachedMesh = AssetDatabase.LoadAssetAtPath<GeneratedMeshAssetSO>(assetPath);
            if (cachedMesh != null)
            {
                cachedMesh.ApplyMesh(component.target.transform);
                Debug.Log($"[BlendShare] Using cached mesh for {component.target.name}");
                _generatedAssets.Add(cacheHash);
                return;
            }

            var meshRenderer = component.GetComponent<SkinnedMeshRenderer>()?.sharedMesh;
            if (meshRenderer == null)
                return;
                
            var newMesh = BlendShapeAppender.CreateMeshAsset(meshRenderer, validBlendShapes, assetPath);

            if (newMesh == null)
            {
                Debug.LogWarning($"[BlendShare] Failed to create mesh for {component.target.name}");
                Logger.Log(ErrorSeverity.NonFatal, "error.mesh_creation_failed", component.gameObject);
                return;
            }
                
            newMesh.ApplyMesh(component.target.transform);
            _generatedAssets.Add(cacheHash);
        }

        private static void TryApplyBlendShapeWeights(AppendBlendShapes component)
        {
            var smr = component.GetComponent<SkinnedMeshRenderer>();
            if (smr == null)
                return;

            var allWeights = component.GetAllWeights();
            foreach (var shapeWeight in allWeights)
            {
                var mesh = smr.sharedMesh;
                var key = mesh.GetBlendShapeIndex(shapeWeight.Key);
                smr.SetBlendShapeWeight(key, shapeWeight.Value);
            }
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