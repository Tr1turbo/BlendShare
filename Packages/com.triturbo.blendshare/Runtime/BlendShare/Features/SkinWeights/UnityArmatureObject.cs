using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Core;
using UnityEngine;

namespace Triturbo.BlendShare.Features.SkinWeights
{
    /// <summary>Stores one absolute Unity-space bone definition in a generated artifact.</summary>
    [System.Serializable]
    public sealed class UnityArmatureBoneData
    {
        public string m_Path;
        public UnityLocalTransform m_LocalTransform = UnityLocalTransform.Identity;
        public bool m_CreateIfMissing = true;

        public UnityArmatureBoneData() { }

        public UnityArmatureBoneData(string path, UnityLocalTransform localTransform, bool createIfMissing)
        {
            m_Path = MeshNodePath.Normalize(path);
            m_LocalTransform = localTransform;
            m_CreateIfMissing = createIfMissing;
        }

        public string Path => MeshNodePath.Normalize(m_Path);
        public string ParentPath => MeshNodePath.ParentPath(Path);
        public string Name => MeshNodePath.LeafName(Path);
        public UnityLocalTransform LocalTransform => m_LocalTransform;
    }

    /// <summary>Stores the Unity-space armature generated from one or more FBX patch armatures.</summary>
    [PreferBinarySerialization]
    public sealed class UnityArmatureObject : ScriptableObject
    {
        [SerializeField, NonReorderable]
        private List<UnityArmatureBoneData> m_Bones = new();

        public IReadOnlyList<UnityArmatureBoneData> Bones => m_Bones;
        public int BoneCount => m_Bones?.Count ?? 0;

        /// <summary>Replaces the artifact armature with normalized, unique bone definitions.</summary>
        public void SetBones(IEnumerable<UnityArmatureBoneData> bones)
        {
            m_Bones = bones?
                .Where(bone => bone != null && !string.IsNullOrWhiteSpace(bone.m_Path))
                .Select(bone =>
                {
                    bone.m_Path = MeshNodePath.Normalize(bone.m_Path);
                    return bone;
                })
                .GroupBy(bone => bone.m_Path)
                .Select(group => group.First())
                .ToList() ?? new List<UnityArmatureBoneData>();
        }

        /// <summary>Gets an artifact bone by normalized hierarchy path.</summary>
        public UnityArmatureBoneData GetBone(string path)
        {
            string normalized = MeshNodePath.Normalize(path);
            return (m_Bones ?? new List<UnityArmatureBoneData>())
                .FirstOrDefault(bone => bone != null && bone.Path == normalized);
        }

        /// <summary>Checks whether a normalized hierarchy path exists.</summary>
        public bool HasBone(string path)
        {
            return GetBone(path) != null;
        }

        /// <summary>Enumerates artifact bone paths in parent-before-child order.</summary>
        public IEnumerable<string> GetBonePathsInHierarchyOrder()
        {
            return (m_Bones ?? new List<UnityArmatureBoneData>())
                .Where(bone => bone != null)
                .Select(bone => bone.Path)
                .Where(path => path != MeshNodePath.Root)
                .Distinct()
                .OrderBy(path => path.Count(character => character == '/'))
                .ThenBy(path => path, System.StringComparer.Ordinal);
        }
    }
}
