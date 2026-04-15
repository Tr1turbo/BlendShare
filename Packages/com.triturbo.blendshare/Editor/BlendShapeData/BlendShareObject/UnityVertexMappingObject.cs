using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Serialization;


namespace Triturbo.BlendShapeShare.BlendShapeData
{
    public enum UnityFbxMappingBuildMode
    {
        Unknown,
        Extraction,
        LegacyUpgrade,
        FbxAsset
    }

    [System.Serializable]
    public class MappingUnityBlendShapeCache
    {
        public string m_Name;
        public UnityBlendShapeData m_UnityBlendShapeData;

        public MappingUnityBlendShapeCache() { }

        public MappingUnityBlendShapeCache(string name, UnityBlendShapeData unityBlendShapeData)
        {
            m_Name = name;
            m_UnityBlendShapeData = unityBlendShapeData;
        }
    }

    [System.Serializable]
    public struct FbxIndexGroup
    {
        public int[] m_Indices;
    }

    public class UnityVertexMappingObject : ScriptableObject
    {
        public string m_UnityRendererPath;
        public Mesh m_UnityMesh;
        public int m_UnityVertexCount;

        public string m_UnityVertexHash;
        public float m_FbxToUnityScale = 1f;
        public int[] m_Indices;
        public FbxIndexGroup[] m_IndexGroups;

        public bool m_IsValid;
        public string m_InvalidReason;
        public UnityFbxMappingBuildMode m_BuildMode;
        public string[] m_SourceBlendShapeNames;
        public MappingUnityBlendShapeCache[] m_LegacyCache;
        public float FbxToUnityScale => m_FbxToUnityScale == 0f ? 1f : m_FbxToUnityScale;
        public string UnityVerticesHashShort
        {
            get
            {
                if (!string.IsNullOrEmpty(m_UnityVertexHash))
                {
                    return UnityVertexPositionHash.Shorten(m_UnityVertexHash);
                }

                return m_UnityMesh != null ? m_UnityMesh.name : "NoMesh";
            }
        }

        public bool TryGetFbxIndex(int unityVertexIndex, out int fbxControlPointIndex)
        {
            fbxControlPointIndex = -1;
            if (!m_IsValid || m_Indices == null || unityVertexIndex < 0 || unityVertexIndex >= m_Indices.Length)
            {
                return false;
            }

            fbxControlPointIndex = m_Indices[unityVertexIndex];
            return fbxControlPointIndex >= 0;
        }

        public bool TryGetFbxGroup(int unityVertexIndex, out FbxIndexGroup group)
        {
            group = default;
            if (!m_IsValid) return false;

            if (m_IndexGroups != null)
            {
                if (unityVertexIndex < 0 || unityVertexIndex >= m_IndexGroups.Length) return false;
                group = m_IndexGroups[unityVertexIndex];
                return group.m_Indices != null && group.m_Indices.Length > 0 && group.m_Indices[0] >= 0;
            }

            // Legacy on-the-fly fallback (read-only)
            if (m_Indices != null && unityVertexIndex >= 0 && unityVertexIndex < m_Indices.Length)
            {
                int idx = m_Indices[unityVertexIndex];
                if (idx < 0) return false;
                group = new FbxIndexGroup { m_Indices = new[] { idx } };
                return true;
            }

            return false;
        }

        public bool IsValidFor(Mesh targetMesh)
        {
            return m_IsValid && MatchesUnityMesh(targetMesh);
        }

        public bool MatchesUnityMesh(Mesh targetMesh)
        {
            if (targetMesh == null)
            {
                return false;
            }

            if (m_UnityVertexCount != targetMesh.vertexCount)
            {
                return false;
            }

            if (!string.IsNullOrEmpty(m_UnityVertexHash))
            {
                return m_UnityVertexHash == UnityVertexPositionHash.Calculate(targetMesh);
            }


            return false;

        }

        public UnityBlendShapeData GetCachedBlendShape(string shapeName)
        {
            return m_LegacyCache?
                .FirstOrDefault(cache => cache != null && cache.m_Name == shapeName)
                ?.m_UnityBlendShapeData;
        }

        public void SetLegacyCache(IEnumerable<MappingUnityBlendShapeCache> cache)
        {
            m_LegacyCache = cache?.Where(entry => entry != null && entry.m_UnityBlendShapeData != null).ToArray();
        }
    }
}
