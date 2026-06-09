using System.Linq;
using Triturbo.BlendShare.Hashing;
using UnityEngine;

namespace Triturbo.BlendShare.Core
{
    /// <summary>
    /// Stores the FBX control-point indices that correspond to one Unity vertex.
    /// </summary>
    [System.Serializable]
    public struct FbxIndexGroup
    {
        public int[] m_Indices;
    }

    /// <summary>
    /// ScriptableObject mapping from Unity mesh vertices back to FBX control-point indices.
    /// </summary>
    public class UnityVertexMappingObject : ScriptableObject
    {
        public string m_UnityVertexHash;

        public Mesh m_UnityMesh;
        public int m_UnityVertexCount;

        //Import Settings
        public float m_FbxToUnityScale = 1f;
        public bool m_BakeAxisConversion;

        public int[] m_Indices;
        public FbxIndexGroup[] m_IndexGroups;

        public bool m_IsValid;
        public string m_Report;
        public float FbxToUnityScale => m_FbxToUnityScale == 0f ? 1f : m_FbxToUnityScale;
        public Matrix4x4 FbxToUnityMatrix => Matrix4x4.Scale(m_BakeAxisConversion
            ? new Vector3(FbxToUnityScale, FbxToUnityScale, -FbxToUnityScale)
            : new Vector3(-FbxToUnityScale, FbxToUnityScale, FbxToUnityScale));
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

            return false;
        }

        public Vector3 ConvertFbxPointToUnity(Vector3 fbxPoint)
        {
            return FbxToUnityMatrix.MultiplyPoint3x4(fbxPoint);
        }

        public Vector3 ConvertFbxVectorToUnity(Vector3 fbxVector)
        {
            return FbxToUnityMatrix.MultiplyVector(fbxVector);
        }

        public Vector3 ConvertFbxDirectionToUnity(Vector3 fbxDirection)
        {
            return FbxToUnityMatrix.MultiplyVector(fbxDirection).normalized;
        }

        public Matrix4x4 ConvertFbxMatrixToUnity(Matrix4x4 fbxMatrix)
        {
            var fbxToUnity = FbxToUnityMatrix;
            return fbxToUnity * fbxMatrix * fbxToUnity.inverse;
        }

        public bool IsValidFor(Mesh targetMesh)
        {
            return m_IsValid && MatchesUnityMesh(targetMesh);
        }

        public bool IsCompatibleWith(MeshDataObject meshData, Mesh targetMesh)
        {
            return IsValidFor(targetMesh) && MatchesFbxControlPointCount(meshData?.FbxControlPointCount ?? -1);
        }

        public bool MatchesFbxControlPointCount(int fbxControlPointCount)
        {
            if (fbxControlPointCount <= 0)
            {
                return true;
            }

            if (m_IndexGroups != null)
            {
                return m_IndexGroups.All(group =>
                    group.m_Indices == null ||
                    group.m_Indices.All(index => index < 0 || index < fbxControlPointCount));
            }

            return m_Indices == null || m_Indices.All(index => index < 0 || index < fbxControlPointCount);
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

            if (m_UnityMesh == targetMesh)
            {
                return true;
            }

            if (!string.IsNullOrEmpty(m_UnityVertexHash))
            {
                return m_UnityVertexHash == UnityVertexPositionHash.Calculate(targetMesh);
            }


            return false;

        }
    }
}
