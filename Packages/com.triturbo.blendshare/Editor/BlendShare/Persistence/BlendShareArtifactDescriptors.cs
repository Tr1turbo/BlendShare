using System;
using UnityEngine;

namespace Triturbo.BlendShare.Persistence
{
    [Serializable]
    public sealed class BlendShareMeshDescriptor
    {
        public string m_NodePath;
        public Mesh m_Mesh;
        public BlendShareSkinBindingDescriptor m_SkinBinding;
    }

    [Serializable]
    public sealed class BlendShareSkinBindingDescriptor
    {
        public string m_RootBonePath;
        public string[] m_BonePaths = Array.Empty<string>();
    }
}
