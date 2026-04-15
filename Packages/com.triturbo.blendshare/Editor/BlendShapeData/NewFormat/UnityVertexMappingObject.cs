using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;


namespace Triturbo.BlendShapeShare.BlendShapeData
{
    public enum UnityFbxMappingBuildMode
    {
        Unknown,
        Extraction,
        LegacyUpgrade
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

    public class UnityVertexMappingObject : UpgradableScriptableObject
    {
        protected override int CurrentVersion => 2;

        public string m_UnityRendererPath;
        public Mesh m_UnityMesh;
        public int m_UnityVertexCount;
        public int m_UnityVerticesHash;
        public float m_FbxToUnityScale = 1f;
        public int[] m_Indices;
        public bool m_IsValid;
        public string m_InvalidReason;
        public UnityFbxMappingBuildMode m_BuildMode;
        public string[] m_SourceBlendShapeNames;
        public MappingUnityBlendShapeCache[] m_LegacyCache;
        public float FbxToUnityScale => m_FbxToUnityScale == 0f ? 1f : m_FbxToUnityScale;

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

            return m_UnityVertexCount == targetMesh.vertexCount &&
                   m_UnityVerticesHash == MeshData.GetVerticesHash(targetMesh);
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

        protected override void UpgradeStep(int fromVersion)
        {
            if (fromVersion == 0)
            {
                m_Indices ??= System.Array.Empty<int>();
                m_SourceBlendShapeNames ??= System.Array.Empty<string>();
                SetVersion(1);
                return;
            }

            if (fromVersion == 1)
            {
                if (m_FbxToUnityScale == 0f)
                {
                    m_FbxToUnityScale = 1f;
                }

                SetVersion(2);
                return;
            }

            SetVersion(CurrentVersion);
        }
    }
}
