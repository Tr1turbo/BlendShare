using System;

namespace Triturbo.BlendShare.Hashing
{
    /// <summary>
    /// Serializable FBX topology identity data for Unity assets.
    /// </summary>
    [Serializable]
    public sealed class FbxTopologySignature
    {
        public string m_Hash = string.Empty;
        public int m_ControlPointCount = -1;
        public int m_FaceCount = -1;
        public bool m_IsValid;

        public string Hash => m_Hash ?? string.Empty;
        public int ControlPointCount => m_ControlPointCount;
        public int FaceCount => m_FaceCount;
        public bool IsValid => m_IsValid;

        public FbxTopologySignature()
        {
        }

        public FbxTopologySignature(
            string hash,
            int controlPointCount,
            int faceCount,
            bool isValid)
        {
            m_Hash = hash ?? string.Empty;
            m_ControlPointCount = controlPointCount;
            m_FaceCount = faceCount;
            m_IsValid = isValid;
        }
    }
}
