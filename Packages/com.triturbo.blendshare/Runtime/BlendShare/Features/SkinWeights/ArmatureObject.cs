using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Core;
using UnityEngine;

namespace Triturbo.BlendShare.Features.SkinWeights
{
    [System.Serializable]
    public sealed class ArmatureBoneData
    {
        public string m_Path;
        public Vector3 m_FbxLocalTranslation;
        public Vector3 m_FbxLocalEulerRotation;
        public Vector3 m_FbxLocalScale = Vector3.one;
        public bool m_CreateIfMissing = true;

        public string Path => MeshNodePath.Normalize(m_Path);
        public string ParentPath => MeshNodePath.ParentPath(Path);
        public string Name => MeshNodePath.LeafName(Path);

        public ArmatureBoneData() { }

        public ArmatureBoneData(
            string path,
            Vector3 fbxLocalTranslation,
            Vector3 fbxLocalEulerRotation,
            Vector3 fbxLocalScale,
            bool createIfMissing)
        {
            m_Path = MeshNodePath.Normalize(path);
            m_FbxLocalTranslation = fbxLocalTranslation;
            m_FbxLocalEulerRotation = fbxLocalEulerRotation;
            m_FbxLocalScale = fbxLocalScale;
            m_CreateIfMissing = createIfMissing;
        }
    }

    [PreferBinarySerialization]
    public sealed class ArmatureObject : ScriptableObject
    {
        [SerializeField, NonReorderable]
        private List<ArmatureBoneData> m_Bones = new();

        public IReadOnlyList<ArmatureBoneData> Bones => m_Bones;
        public int BoneCount => m_Bones?.Count ?? 0;

        public void Sanitize()
        {
            SetBones(m_Bones);
        }

        public int GetBoneIndex(string path)
        {
            string normalized = MeshNodePath.Normalize(path);
            for (int i = 0; i < (m_Bones?.Count ?? 0); i++)
            {
                if (m_Bones[i] != null && MeshNodePath.Normalize(m_Bones[i].m_Path) == normalized)
                {
                    return i;
                }
            }

            return -1;
        }

        public ArmatureBoneData GetBone(string path)
        {
            int index = GetBoneIndex(path);
            return index >= 0 ? m_Bones[index] : null;
        }

        public bool HasBone(string path)
        {
            return GetBoneIndex(path) >= 0;
        }

        public IEnumerable<string> GetBonePathsInHierarchyOrder()
        {
            return (m_Bones ?? new List<ArmatureBoneData>())
                .Where(bone => bone != null)
                .Select(bone => bone.Path)
                .Where(path => path != MeshNodePath.Root)
                .Distinct()
                .OrderBy(path => path.Count(character => character == '/'))
                .ThenBy(path => path, System.StringComparer.Ordinal);
        }

        public int GetOrAddBone(ArmatureBoneData bone)
        {
            if (bone == null || string.IsNullOrWhiteSpace(bone.m_Path))
            {
                return -1;
            }

            bone = Normalize(bone);
            int existingIndex = GetBoneIndex(bone.m_Path);
            if (existingIndex >= 0)
            {
                return existingIndex;
            }

            m_Bones ??= new List<ArmatureBoneData>();
            m_Bones.Add(bone);
            return m_Bones.Count - 1;
        }

        public void SetBones(IEnumerable<ArmatureBoneData> bones)
        {
            m_Bones = bones?
                .Where(bone => bone != null && !string.IsNullOrWhiteSpace(bone.m_Path))
                .Select(Normalize)
                .GroupBy(bone => bone.m_Path)
                .Select(group => group.First())
                .ToList() ?? new List<ArmatureBoneData>();
        }

        private static ArmatureBoneData Normalize(ArmatureBoneData bone)
        {
            bone.m_Path = MeshNodePath.Normalize(bone.m_Path);
            if (bone.m_FbxLocalScale == Vector3.zero)
            {
                bone.m_FbxLocalScale = Vector3.one;
            }

            return bone;
        }

    }
}
