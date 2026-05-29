using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Core;
using UnityEngine;

namespace Triturbo.BlendShare.Features.SkinWeights
{
    [System.Serializable]
    public sealed class BoneNodeData
    {
        public string m_Path;
        public string m_ParentPath;
        public Vector3 m_FbxLocalTranslation;
        public Vector3 m_FbxLocalEulerRotation;
        public Vector3 m_FbxLocalScale = Vector3.one;
        public bool m_CreateIfMissing = true;

        public BoneNodeData() { }

        public BoneNodeData(
            string path,
            string parentPath,
            Vector3 fbxLocalTranslation,
            Vector3 fbxLocalEulerRotation,
            Vector3 fbxLocalScale,
            bool createIfMissing)
        {
            m_Path = MeshNodePath.Normalize(path);
            m_ParentPath = MeshNodePath.Normalize(parentPath);
            m_FbxLocalTranslation = fbxLocalTranslation;
            m_FbxLocalEulerRotation = fbxLocalEulerRotation;
            m_FbxLocalScale = fbxLocalScale;
            m_CreateIfMissing = createIfMissing;
        }
    }

    [PreferBinarySerialization]
    public sealed class BoneGraphObject : ScriptableObject
    {
        public const string Id = "bone-graph";

        [SerializeField, NonReorderable]
        private List<BoneNodeData> m_Bones = new();

        public IReadOnlyList<BoneNodeData> Bones => m_Bones;
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

        public BoneNodeData GetBone(string path)
        {
            int index = GetBoneIndex(path);
            return index >= 0 ? m_Bones[index] : null;
        }

        public bool HasBone(string path)
        {
            return GetBoneIndex(path) >= 0;
        }

        public int GetOrAddBone(BoneNodeData bone)
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

            m_Bones ??= new List<BoneNodeData>();
            m_Bones.Add(bone);
            return m_Bones.Count - 1;
        }

        public void SetBones(IEnumerable<BoneNodeData> bones)
        {
            m_Bones = bones?
                .Where(bone => bone != null && !string.IsNullOrWhiteSpace(bone.m_Path))
                .Select(Normalize)
                .GroupBy(bone => bone.m_Path)
                .Select(group => group.First())
                .ToList() ?? new List<BoneNodeData>();
        }

        private static BoneNodeData Normalize(BoneNodeData bone)
        {
            bone.m_Path = MeshNodePath.Normalize(bone.m_Path);
            bone.m_ParentPath = MeshNodePath.Normalize(bone.m_ParentPath);
            if (bone.m_FbxLocalScale == Vector3.zero)
            {
                bone.m_FbxLocalScale = Vector3.one;
            }

            return bone;
        }

    }
}
