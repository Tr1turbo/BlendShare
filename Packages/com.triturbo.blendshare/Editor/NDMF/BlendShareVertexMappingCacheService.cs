using System;
using System.IO;
using System.Linq;
using Triturbo.BlendShare.Core;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.NDMF
{
    internal static class BlendShareVertexMappingCacheService
    {
        private const string CachePath = "Library/BlendShare/VertexMappingCache.json";

        public static bool TryGet(
            BlendShareObject blendShare,
            MeshDataObject meshData,
            Mesh targetMesh,
            out UnityVertexMappingObject mapping)
        {
            mapping = null;
            if (blendShare == null || meshData == null || targetMesh == null)
            {
                return false;
            }

            var cache = LoadCache();
            string blendShareId = GetGlobalId(blendShare);
            string meshDataId = GetGlobalId(meshData);
            var entry = (cache.entries ?? Array.Empty<Entry>()).FirstOrDefault(candidate =>
                candidate != null &&
                candidate.blendShareGlobalId == blendShareId &&
                candidate.meshDataGlobalId == meshDataId &&
                candidate.unityVertexCount == targetMesh.vertexCount &&
                candidate.unityVertexHash == UnityVertexPositionHash.Calculate(targetMesh));
            if (entry == null)
            {
                return false;
            }

            mapping = CreateMapping(entry);
            if (mapping == null || !mapping.IsCompatibleWith(meshData, targetMesh))
            {
                if (mapping != null)
                {
                    UnityEngine.Object.DestroyImmediate(mapping);
                }

                mapping = null;
                return false;
            }

            return true;
        }

        public static bool ContainsCompatible(
            BlendShareObject blendShare,
            MeshDataObject meshData,
            Mesh targetMesh)
        {
            if (blendShare == null || meshData == null || targetMesh == null)
            {
                return false;
            }

            var cache = LoadCache();
            string blendShareId = GetGlobalId(blendShare);
            string meshDataId = GetGlobalId(meshData);
            string targetHash = UnityVertexPositionHash.Calculate(targetMesh);
            return (cache.entries ?? Array.Empty<Entry>()).Any(candidate =>
                candidate != null &&
                candidate.blendShareGlobalId == blendShareId &&
                candidate.meshDataGlobalId == meshDataId &&
                candidate.isValid &&
                candidate.unityVertexCount == targetMesh.vertexCount &&
                candidate.unityVertexHash == targetHash &&
                MatchesFbxControlPointCount(candidate, meshData.m_FbxControlPointCount));
        }

        public static bool TryGetInvalidDiagnostic(
            BlendShareObject blendShare,
            MeshDataObject meshData,
            Mesh targetMesh,
            out string diagnostic)
        {
            diagnostic = null;
            if (blendShare == null || meshData == null || targetMesh == null)
            {
                return false;
            }

            var cache = LoadCache();
            string blendShareId = GetGlobalId(blendShare);
            string meshDataId = GetGlobalId(meshData);
            string targetHash = UnityVertexPositionHash.Calculate(targetMesh);
            var entry = (cache.entries ?? Array.Empty<Entry>()).FirstOrDefault(candidate =>
                candidate != null &&
                candidate.blendShareGlobalId == blendShareId &&
                candidate.meshDataGlobalId == meshDataId &&
                !candidate.isValid &&
                candidate.unityVertexCount == targetMesh.vertexCount &&
                candidate.unityVertexHash == targetHash);
            if (entry == null)
            {
                return false;
            }

            diagnostic = string.IsNullOrWhiteSpace(entry.invalidReason)
                ? "cached mapping generation failed"
                : entry.invalidReason;
            return true;
        }

        public static void Store(
            BlendShareObject blendShare,
            MeshDataObject meshData,
            UnityVertexMappingObject mapping)
        {
            if (blendShare == null || meshData == null || mapping == null)
            {
                return;
            }

            var cache = LoadCache();
            string blendShareId = GetGlobalId(blendShare);
            string meshDataId = GetGlobalId(meshData);
            string vertexHash = mapping.m_UnityVertexHash ?? string.Empty;
            int vertexCount = mapping.m_UnityVertexCount;
            cache.entries = (cache.entries ?? Array.Empty<Entry>())
                .Where(entry => entry != null &&
                                !(entry.blendShareGlobalId == blendShareId &&
                                  entry.meshDataGlobalId == meshDataId &&
                                  entry.unityVertexHash == vertexHash &&
                                  entry.unityVertexCount == vertexCount))
                .Concat(new[] { CreateEntry(blendShareId, meshDataId, mapping) })
                .ToArray();
            SaveCache(cache);
        }

        public static void StoreInvalid(
            BlendShareObject blendShare,
            MeshDataObject meshData,
            Mesh targetMesh,
            string invalidReason)
        {
            if (blendShare == null || meshData == null || targetMesh == null)
            {
                return;
            }

            var mapping = ScriptableObject.CreateInstance<UnityVertexMappingObject>();
            mapping.m_UnityMesh = targetMesh;
            mapping.m_UnityVertexCount = targetMesh.vertexCount;
            mapping.m_UnityVertexHash = UnityVertexPositionHash.Calculate(targetMesh);
            mapping.m_FbxToUnityScale = 1f;
            mapping.m_IndexGroups = Array.Empty<FbxIndexGroup>();
            mapping.m_Indices = Array.Empty<int>();
            mapping.m_IsValid = false;
            mapping.m_InvalidReason = string.IsNullOrWhiteSpace(invalidReason)
                ? "mapping generation failed"
                : invalidReason;
            mapping.hideFlags = HideFlags.HideAndDontSave;
            Store(blendShare, meshData, mapping);
            UnityEngine.Object.DestroyImmediate(mapping);
        }

        private static Entry CreateEntry(
            string blendShareId,
            string meshDataId,
            UnityVertexMappingObject mapping)
        {
            return new Entry
            {
                blendShareGlobalId = blendShareId,
                meshDataGlobalId = meshDataId,
                unityVertexHash = mapping.m_UnityVertexHash ?? string.Empty,
                unityVertexCount = mapping.m_UnityVertexCount,
                fbxToUnityScale = mapping.FbxToUnityScale,
                indices = mapping.m_Indices ?? Array.Empty<int>(),
                indexGroups = mapping.m_IndexGroups ?? Array.Empty<FbxIndexGroup>(),
                isValid = mapping.m_IsValid,
                invalidReason = mapping.m_InvalidReason ?? string.Empty
            };
        }

        private static UnityVertexMappingObject CreateMapping(Entry entry)
        {
            if (entry == null)
            {
                return null;
            }

            var mapping = ScriptableObject.CreateInstance<UnityVertexMappingObject>();
            mapping.m_UnityVertexHash = entry.unityVertexHash;
            mapping.m_UnityVertexCount = entry.unityVertexCount;
            mapping.m_FbxToUnityScale = entry.fbxToUnityScale == 0f ? 1f : entry.fbxToUnityScale;
            mapping.m_Indices = entry.indices ?? Array.Empty<int>();
            mapping.m_IndexGroups = entry.indexGroups ?? Array.Empty<FbxIndexGroup>();
            mapping.m_IsValid = entry.isValid;
            mapping.m_InvalidReason = entry.invalidReason ?? string.Empty;
            mapping.hideFlags = HideFlags.HideAndDontSave;
            return mapping;
        }

        private static bool MatchesFbxControlPointCount(Entry entry, int fbxControlPointCount)
        {
            if (fbxControlPointCount <= 0)
            {
                return true;
            }

            if (entry.indexGroups != null)
            {
                return entry.indexGroups.All(group =>
                    group.m_Indices == null ||
                    group.m_Indices.All(index => index < 0 || index < fbxControlPointCount));
            }

            return entry.indices == null || entry.indices.All(index => index < 0 || index < fbxControlPointCount);
        }

        private static CacheFile LoadCache()
        {
            if (!File.Exists(CachePath))
            {
                return new CacheFile();
            }

            try
            {
                return JsonUtility.FromJson<CacheFile>(File.ReadAllText(CachePath)) ?? new CacheFile();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[BlendShare] Failed to read vertex mapping cache '{CachePath}': {ex.Message}");
                return new CacheFile();
            }
        }

        private static void SaveCache(CacheFile cache)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath) ?? "Library/BlendShare");
            string temporaryPath = CachePath + ".tmp";
            File.WriteAllText(temporaryPath, JsonUtility.ToJson(cache ?? new CacheFile(), true));
            if (File.Exists(CachePath))
            {
                File.Delete(CachePath);
            }

            File.Move(temporaryPath, CachePath);
        }

        private static string GetGlobalId(UnityEngine.Object obj)
        {
            return obj != null ? GlobalObjectId.GetGlobalObjectIdSlow(obj).ToString() : string.Empty;
        }

        [Serializable]
        private sealed class CacheFile
        {
            public Entry[] entries = Array.Empty<Entry>();
        }

        [Serializable]
        private sealed class Entry
        {
            public string blendShareGlobalId;
            public string meshDataGlobalId;
            public string unityVertexHash;
            public int unityVertexCount;
            public float fbxToUnityScale = 1f;
            public int[] indices = Array.Empty<int>();
            public FbxIndexGroup[] indexGroups = Array.Empty<FbxIndexGroup>();
            public bool isValid;
            public string invalidReason;
        }
    }
}
