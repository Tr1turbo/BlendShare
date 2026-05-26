using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Triturbo.BlendShare.Core;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.NDMF
{
    internal static class BlendShareVertexMappingCacheService
    {
        private const string CacheDirectory = "Library/BlendShare/VertexMappingCache/v1";
        private const string PayloadMagic = "BSMP";
        private const int CacheVersion = 2;

        public static bool TryGet(
            GameObject sourceFbx,
            MeshDataObject meshData,
            Mesh targetMesh,
            out UnityVertexMappingObject mapping)
        {
            mapping = null;
            if (!TryCreateLookup(sourceFbx, meshData, targetMesh, out var lookup) ||
                !TryFindEntry(lookup, out var entry) ||
                !entry.isValid ||
                string.IsNullOrEmpty(entry.payloadId))
            {
                return false;
            }

            if (!TryReadPayloadFile(GetPayloadPath(entry.payloadId), lookup.SourceFbxId, lookup.MeshPath, entry, out var payload) ||
                !PayloadIsCompatible(payload, meshData, targetMesh))
            {
                return false;
            }

            mapping = CreateMapping(payload, targetMesh);
            if (mapping == null)
            {
                DestroyTransientMapping(mapping);
                mapping = null;
                return false;
            }

            return true;
        }

        public static bool ContainsCompatible(
            GameObject sourceFbx,
            MeshDataObject meshData,
            Mesh targetMesh)
        {
            if (!TryCreateLookup(sourceFbx, meshData, targetMesh, out var lookup) ||
                !TryFindEntry(lookup, out var entry) ||
                !entry.isValid ||
                string.IsNullOrEmpty(entry.payloadId))
            {
                return false;
            }

            return TryReadPayloadFile(GetPayloadPath(entry.payloadId), lookup.SourceFbxId, lookup.MeshPath, entry, out var payload) &&
                   PayloadIsCompatible(payload, meshData, targetMesh);
        }

        public static bool TryGetInvalidDiagnostic(
            GameObject sourceFbx,
            MeshDataObject meshData,
            Mesh targetMesh,
            out string diagnostic)
        {
            diagnostic = null;
            if (!TryCreateLookup(sourceFbx, meshData, targetMesh, out var lookup) ||
                !TryFindEntry(lookup, out var entry) ||
                entry.isValid)
            {
                return false;
            }

            diagnostic = string.IsNullOrWhiteSpace(entry.invalidReason)
                ? "cached mapping generation failed"
                : entry.invalidReason;
            return true;
        }

        public static void Store(
            GameObject sourceFbx,
            MeshDataObject meshData,
            Mesh targetMesh,
            UnityVertexMappingObject mapping)
        {
            if (mapping == null || !TryCreateLookup(sourceFbx, meshData, targetMesh, out var lookup))
            {
                return;
            }

            if (!mapping.m_IsValid)
            {
                StoreInvalid(sourceFbx, meshData, targetMesh, mapping.m_Report);
                return;
            }

            string unityVertexHash = !string.IsNullOrEmpty(mapping.m_UnityVertexHash)
                ? mapping.m_UnityVertexHash
                : UnityVertexPositionHash.Calculate(targetMesh);
            int unityVertexCount = mapping.m_UnityVertexCount > 0
                ? mapping.m_UnityVertexCount
                : targetMesh.vertexCount;
            string payloadId = GetPayloadId(
                lookup.SourceFbxId,
                lookup.MeshPath,
                lookup.UnityMeshId,
                unityVertexHash,
                unityVertexCount);

            var payload = new CachePayload
            {
                sourceFbxId = lookup.SourceFbxId,
                meshPath = lookup.MeshPath,
                unityMeshId = lookup.UnityMeshId,
                unityVertexHash = unityVertexHash,
                unityVertexCount = unityVertexCount,
                fbxToUnityScale = mapping.FbxToUnityScale,
                bakeAxisConversion = mapping.m_BakeAxisConversion,
                indices = mapping.m_Indices ?? Array.Empty<int>(),
                indexGroups = mapping.m_IndexGroups ?? Array.Empty<FbxIndexGroup>()
            };

            WritePayloadFile(GetPayloadPath(payloadId), payload);
            StoreIndexEntry(lookup.SourceFbxId, lookup.MeshPath, new CacheIndexEntry
            {
                payloadId = payloadId,
                unityMeshId = lookup.UnityMeshId,
                unityVertexHash = unityVertexHash,
                unityVertexCount = unityVertexCount,
                isValid = mapping.m_IsValid,
                invalidReason = mapping.m_Report ?? string.Empty
            });
        }

        public static void StoreInvalid(
            GameObject sourceFbx,
            MeshDataObject meshData,
            Mesh targetMesh,
            string invalidReason)
        {
            if (!TryCreateLookup(sourceFbx, meshData, targetMesh, out var lookup))
            {
                return;
            }

            StoreIndexEntry(lookup.SourceFbxId, lookup.MeshPath, new CacheIndexEntry
            {
                payloadId = string.Empty,
                unityMeshId = lookup.UnityMeshId,
                unityVertexHash = UnityVertexPositionHash.Calculate(targetMesh),
                unityVertexCount = lookup.UnityVertexCount,
                isValid = false,
                invalidReason = string.IsNullOrWhiteSpace(invalidReason)
                    ? "mapping generation failed"
                    : invalidReason
            });
        }

        private static bool TryFindEntry(CacheLookup lookup, out CacheIndexEntry entry)
        {
            entry = null;
            if (!TryReadIndex(lookup.IndexPath, lookup.SourceFbxId, out var index) ||
                !TryGetMeshIndex(index, lookup.MeshPath, out var meshIndex))
            {
                return false;
            }

            entry = FindExactEntry(meshIndex, lookup.UnityMeshId, lookup.UnityVertexCount);
            if (entry != null)
            {
                return true;
            }

            string vertexHash = UnityVertexPositionHash.Calculate(lookup.TargetMesh);
            entry = FindHashEntry(meshIndex, vertexHash, lookup.UnityVertexCount);
            return entry != null;
        }

        private static bool TryCreateLookup(
            GameObject sourceFbx,
            MeshDataObject meshData,
            Mesh targetMesh,
            out CacheLookup lookup)
        {
            lookup = default;
            if (sourceFbx == null || meshData == null || targetMesh == null)
            {
                return false;
            }

            string sourceFbxId = GetAssetGuid(sourceFbx);
            if (string.IsNullOrEmpty(sourceFbxId))
            {
                return false;
            }

            lookup = new CacheLookup
            {
                SourceFbxId = sourceFbxId,
                MeshPath = MeshNodePath.Normalize(meshData.m_Path),
                UnityMeshId = GetGlobalId(targetMesh),
                UnityVertexCount = targetMesh.vertexCount,
                TargetMesh = targetMesh,
                IndexPath = GetIndexPath(sourceFbxId)
            };
            return true;
        }

        private static bool PayloadIsCompatible(CachePayload payload, MeshDataObject meshData, Mesh targetMesh)
        {
            if (payload == null || targetMesh == null || payload.unityVertexCount != targetMesh.vertexCount)
            {
                return false;
            }

            if (!MatchesFbxControlPointCount(payload, meshData?.m_FbxControlPointCount ?? -1))
            {
                return false;
            }

            string targetMeshId = GetGlobalId(targetMesh);
            if (!string.IsNullOrEmpty(payload.unityMeshId) && payload.unityMeshId == targetMeshId)
            {
                return true;
            }

            return !string.IsNullOrEmpty(payload.unityVertexHash) &&
                   payload.unityVertexHash == UnityVertexPositionHash.Calculate(targetMesh);
        }

        private static bool MatchesFbxControlPointCount(CachePayload payload, int fbxControlPointCount)
        {
            if (fbxControlPointCount <= 0)
            {
                return true;
            }

            if (payload.indexGroups != null)
            {
                return payload.indexGroups.All(group =>
                    group.m_Indices == null ||
                    group.m_Indices.All(index => index < 0 || index < fbxControlPointCount));
            }

            return payload.indices == null || payload.indices.All(index => index < 0 || index < fbxControlPointCount);
        }

        private static UnityVertexMappingObject CreateMapping(CachePayload payload, Mesh targetMesh)
        {
            if (payload == null)
            {
                return null;
            }

            var mapping = ScriptableObject.CreateInstance<UnityVertexMappingObject>();
            mapping.m_UnityMesh = targetMesh;
            mapping.m_UnityVertexHash = payload.unityVertexHash ?? string.Empty;
            mapping.m_UnityVertexCount = payload.unityVertexCount;
            mapping.m_FbxToUnityScale = payload.fbxToUnityScale == 0f ? 1f : payload.fbxToUnityScale;
            mapping.m_BakeAxisConversion = payload.bakeAxisConversion;
            mapping.m_Indices = payload.indices ?? Array.Empty<int>();
            mapping.m_IndexGroups = payload.indexGroups ?? Array.Empty<FbxIndexGroup>();
            mapping.m_IsValid = true;
            mapping.m_Report = "Cached mapping valid.";
            mapping.hideFlags = HideFlags.HideAndDontSave;
            return mapping;
        }

        private static void StoreIndexEntry(string sourceFbxId, string meshPath, CacheIndexEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(sourceFbxId))
            {
                return;
            }

            meshPath = MeshNodePath.Normalize(meshPath);
            var indexPath = GetIndexPath(sourceFbxId);
            if (!TryReadIndex(indexPath, sourceFbxId, out var index))
            {
                index = new CacheIndex
                {
                    sourceFbxId = sourceFbxId,
                    version = CacheVersion,
                    meshes = Array.Empty<CacheMeshIndex>()
                };
            }

            var meshIndex = GetOrCreateMeshIndex(index, meshPath);
            meshIndex.entries = (meshIndex.entries ?? Array.Empty<CacheIndexEntry>())
                .Where(candidate => !MatchesEntry(candidate, entry))
                .Concat(new[] { entry })
                .ToArray();
            WriteIndexFile(indexPath, index);
        }

        private static bool MatchesEntry(CacheIndexEntry candidate, CacheIndexEntry replacement)
        {
            if (candidate == null)
            {
                return true;
            }

            if (replacement == null)
            {
                return false;
            }

            if (!string.IsNullOrEmpty(candidate.payloadId) && candidate.payloadId == replacement.payloadId)
            {
                return true;
            }

            if (!string.IsNullOrEmpty(candidate.unityMeshId) && candidate.unityMeshId == replacement.unityMeshId)
            {
                return true;
            }

            return !string.IsNullOrEmpty(candidate.unityVertexHash) &&
                   candidate.unityVertexHash == replacement.unityVertexHash &&
                   candidate.unityVertexCount == replacement.unityVertexCount;
        }

        private static CacheIndexEntry FindExactEntry(CacheMeshIndex meshIndex, string unityMeshId, int vertexCount)
        {
            if (string.IsNullOrEmpty(unityMeshId))
            {
                return null;
            }

            return (meshIndex.entries ?? Array.Empty<CacheIndexEntry>())
                .FirstOrDefault(entry => entry != null &&
                                         entry.unityMeshId == unityMeshId &&
                                         entry.unityVertexCount == vertexCount);
        }

        private static CacheIndexEntry FindHashEntry(CacheMeshIndex meshIndex, string vertexHash, int vertexCount)
        {
            if (string.IsNullOrEmpty(vertexHash))
            {
                return null;
            }

            return (meshIndex.entries ?? Array.Empty<CacheIndexEntry>())
                .FirstOrDefault(entry => entry != null &&
                                         entry.unityVertexHash == vertexHash &&
                                         entry.unityVertexCount == vertexCount);
        }

        private static bool TryReadIndex(
            string path,
            string expectedSourceFbxId,
            out CacheIndex index)
        {
            index = null;
            if (!File.Exists(path))
            {
                return false;
            }

            try
            {
                index = JsonUtility.FromJson<CacheIndex>(File.ReadAllText(path));
                return index != null &&
                       index.version == CacheVersion &&
                       index.sourceFbxId == expectedSourceFbxId;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[BlendShare] Failed to read vertex mapping cache index '{path}': {ex.Message}");
                index = null;
                return false;
            }
        }

        private static void WriteIndexFile(string path, CacheIndex index)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? CacheDirectory);
            string temporaryPath = path + ".tmp";
            index.version = CacheVersion;
            File.WriteAllText(temporaryPath, JsonUtility.ToJson(index, true));
            ReplaceFile(temporaryPath, path);
        }

        private static bool TryReadPayloadFile(
            string path,
            string sourceFbxId,
            string meshPath,
            CacheIndexEntry entry,
            out CachePayload payload)
        {
            payload = null;
            if (!File.Exists(path))
            {
                return false;
            }

            try
            {
                using var reader = new BinaryReader(File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read));
                if (reader.ReadString() != PayloadMagic || reader.ReadInt32() != CacheVersion)
                {
                    return false;
                }

                payload = new CachePayload
                {
                    sourceFbxId = reader.ReadString(),
                    meshPath = reader.ReadString(),
                    unityMeshId = reader.ReadString(),
                    unityVertexHash = reader.ReadString(),
                    unityVertexCount = reader.ReadInt32(),
                    fbxToUnityScale = reader.ReadSingle(),
                    bakeAxisConversion = reader.ReadBoolean(),
                    indices = ReadIntArray(reader),
                    indexGroups = ReadIndexGroups(reader)
                };

                return payload.sourceFbxId == sourceFbxId &&
                       payload.meshPath == meshPath &&
                       payload.unityMeshId == entry.unityMeshId &&
                       payload.unityVertexHash == entry.unityVertexHash &&
                       payload.unityVertexCount == entry.unityVertexCount;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[BlendShare] Failed to read vertex mapping cache payload '{path}': {ex.Message}");
                payload = null;
                return false;
            }
        }

        private static void WritePayloadFile(string path, CachePayload payload)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? CacheDirectory);
            string temporaryPath = path + ".tmp";
            using (var writer = new BinaryWriter(File.Open(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None)))
            {
                writer.Write(PayloadMagic);
                writer.Write(CacheVersion);
                writer.Write(payload.sourceFbxId ?? string.Empty);
                writer.Write(payload.meshPath ?? string.Empty);
                writer.Write(payload.unityMeshId ?? string.Empty);
                writer.Write(payload.unityVertexHash ?? string.Empty);
                writer.Write(payload.unityVertexCount);
                writer.Write(payload.fbxToUnityScale == 0f ? 1f : payload.fbxToUnityScale);
                writer.Write(payload.bakeAxisConversion);
                WriteIntArray(writer, payload.indices ?? Array.Empty<int>());
                WriteIndexGroups(writer, payload.indexGroups ?? Array.Empty<FbxIndexGroup>());
            }

            ReplaceFile(temporaryPath, path);
        }

        private static int[] ReadIntArray(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            var values = new int[Mathf.Max(0, length)];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = reader.ReadInt32();
            }

            return values;
        }

        private static void WriteIntArray(BinaryWriter writer, IReadOnlyList<int> values)
        {
            writer.Write(values?.Count ?? 0);
            if (values == null)
            {
                return;
            }

            for (int i = 0; i < values.Count; i++)
            {
                writer.Write(values[i]);
            }
        }

        private static FbxIndexGroup[] ReadIndexGroups(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            var groups = new FbxIndexGroup[Mathf.Max(0, length)];
            for (int i = 0; i < groups.Length; i++)
            {
                groups[i] = new FbxIndexGroup
                {
                    m_Indices = ReadIntArray(reader)
                };
            }

            return groups;
        }

        private static void WriteIndexGroups(BinaryWriter writer, IReadOnlyList<FbxIndexGroup> groups)
        {
            writer.Write(groups?.Count ?? 0);
            if (groups == null)
            {
                return;
            }

            for (int i = 0; i < groups.Count; i++)
            {
                WriteIntArray(writer, groups[i].m_Indices);
            }
        }

        private static void ReplaceFile(string temporaryPath, string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            File.Move(temporaryPath, path);
        }

        private static string GetIndexPath(string sourceFbxId)
        {
            return Path.Combine(CacheDirectory, "index", sourceFbxId + ".json");
        }

        private static string GetPayloadPath(string payloadId)
        {
            return Path.Combine(CacheDirectory, "payloads", payloadId.Substring(0, 2), payloadId + ".bin");
        }

        private static string GetPayloadId(
            string sourceFbxId,
            string meshPath,
            string unityMeshId,
            string unityVertexHash,
            int unityVertexCount)
        {
            return Sha256($"{sourceFbxId}|{meshPath}|{unityMeshId}|{unityVertexHash}|{unityVertexCount}");
        }

        private static string GetGlobalId(UnityEngine.Object obj)
        {
            return obj != null ? GlobalObjectId.GetGlobalObjectIdSlow(obj).ToString() : string.Empty;
        }

        private static string GetAssetGuid(UnityEngine.Object obj)
        {
            if (obj == null)
            {
                return string.Empty;
            }

            string path = AssetDatabase.GetAssetPath(obj);
            return string.IsNullOrEmpty(path) ? string.Empty : AssetDatabase.AssetPathToGUID(path);
        }

        private static bool TryGetMeshIndex(CacheIndex index, string meshPath, out CacheMeshIndex meshIndex)
        {
            meshPath = MeshNodePath.Normalize(meshPath);
            meshIndex = (index?.meshes ?? Array.Empty<CacheMeshIndex>())
                .FirstOrDefault(mesh => mesh != null && MeshNodePath.Normalize(mesh.meshPath) == meshPath);
            return meshIndex != null;
        }

        private static CacheMeshIndex GetOrCreateMeshIndex(CacheIndex index, string meshPath)
        {
            meshPath = MeshNodePath.Normalize(meshPath);
            if (TryGetMeshIndex(index, meshPath, out var meshIndex))
            {
                return meshIndex;
            }

            meshIndex = new CacheMeshIndex
            {
                meshPath = meshPath,
                entries = Array.Empty<CacheIndexEntry>()
            };
            index.meshes = (index.meshes ?? Array.Empty<CacheMeshIndex>())
                .Where(mesh => mesh != null && MeshNodePath.Normalize(mesh.meshPath) != meshPath)
                .Concat(new[] { meshIndex })
                .ToArray();
            return meshIndex;
        }

        private static string Sha256(string value)
        {
            using var sha = SHA256.Create();
            byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
            var builder = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++)
            {
                builder.Append(bytes[i].ToString("x2"));
            }

            return builder.ToString();
        }

        private static void DestroyTransientMapping(UnityVertexMappingObject mapping)
        {
            if (mapping != null && !AssetDatabase.Contains(mapping))
            {
                UnityEngine.Object.DestroyImmediate(mapping);
            }
        }

        private struct CacheLookup
        {
            public string SourceFbxId;
            public string MeshPath;
            public string UnityMeshId;
            public int UnityVertexCount;
            public Mesh TargetMesh;
            public string IndexPath;
        }

        [Serializable]
        private sealed class CacheIndex
        {
            public int version = CacheVersion;
            public string sourceFbxId;
            public CacheMeshIndex[] meshes = Array.Empty<CacheMeshIndex>();
        }

        [Serializable]
        private sealed class CacheMeshIndex
        {
            public string meshPath;
            public CacheIndexEntry[] entries = Array.Empty<CacheIndexEntry>();
        }

        [Serializable]
        private sealed class CacheIndexEntry
        {
            public string payloadId;
            public string unityMeshId;
            public string unityVertexHash;
            public int unityVertexCount;
            public bool isValid;
            public string invalidReason;
        }

        private sealed class CachePayload
        {
            public string sourceFbxId;
            public string meshPath;
            public string unityMeshId;
            public string unityVertexHash;
            public int unityVertexCount;
            public float fbxToUnityScale = 1f;
            public bool bakeAxisConversion;
            public int[] indices = Array.Empty<int>();
            public FbxIndexGroup[] indexGroups = Array.Empty<FbxIndexGroup>();
        }
    }
}
