using System;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Features.BoneGraph;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Triturbo.BlendShare.Persistence
{
    /// <summary>
    /// Generated BlendShare output containing mesh subassets and renderer binding descriptors.
    /// </summary>
    [PreferBinarySerialization]
    public sealed class BlendShareArtifact : ScriptableObject
    {
        public Object m_TargetSource;
        public string m_TargetSourceHash;
        public BlendShareObject[] m_AppliedBlendShares = Array.Empty<BlendShareObject>();

        public BlendShareMeshDescriptor[] m_Meshes = Array.Empty<BlendShareMeshDescriptor>();
        public BoneGraphObject m_Armature;
    }
}
