using System;
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
        
        internal void Process(BuildContext context)
        {
            var stopWatch = new System.Diagnostics.Stopwatch();
            stopWatch.Start();
            var components = context.AvatarRootObject.transform.GetComponentsInChildren<Runtime.AppendBlendShapes>(true);
            foreach (var c in components)
            {
                var validBlendShapes = GetValidBlendShapes(c.blendShapeData);

                var cachedMesh = AssetDatabase.LoadAssetAtPath<GeneratedMeshAssetSO>($"{BasePath}/{GetHash(validBlendShapes)}.asset");
                if (cachedMesh != null)
                {
                    var fbxRoot = FindFbxRoot(context.AvatarRootObject.transform, cachedMesh?.m_OriginalFbxAsset);
                    cachedMesh.ApplyMesh(fbxRoot?.transform ?? c.transform);
                    Debug.Log($"Using cached mesh for {c.gameObject.name}");
                    continue;
                }
                
                //CleanCache();

                var meshRenderer = c.GetComponent<SkinnedMeshRenderer>()?.sharedMesh;
                if (meshRenderer == null)
                    continue;
                
                var newFileName = $"{GetCacheFilePath(GetHash(validBlendShapes))}";
                var newMesh = BlendShapeAppender.CreateMeshAsset(meshRenderer, validBlendShapes, newFileName);

                if (newMesh != null)
                {
                    newMesh.ApplyMesh(c.transform);
                }
            }
            stopWatch.Stop();
            Debug.Log($"AppendBlendShapesHook processed in {stopWatch.ElapsedMilliseconds} ms");
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

        private static string GetCacheFilePath(string hash)
        {
            return Path.Combine(GetCachePath(), $"{hash}.asset").Replace("\\", "/");
        }
        
        public static void CleanCache()
        {
            foreach (var file in Directory.GetFiles(GetCachePath()))
            {
                if (File.Exists(file))
                    File.Delete(file);

                var metaFile = file + ".meta";
                if (File.Exists(metaFile))
                    File.Delete(metaFile);
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

        [CanBeNull]
        private static GameObject FindFbxRoot(Transform parent, GameObject originalFbx)
        {
            if (parent == null || originalFbx == null) return null;

            var source = PrefabUtility.GetCorrespondingObjectFromSource(parent.gameObject);
            if (source == originalFbx)
                return parent.gameObject;

            return (from Transform child in parent
                select FindFbxRoot(child, originalFbx))
                .FirstOrDefault(result => result != null);
        }
    }
}