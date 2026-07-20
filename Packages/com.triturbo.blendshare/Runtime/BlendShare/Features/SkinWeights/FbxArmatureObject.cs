using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Fbx;
using UnityEngine;

namespace Triturbo.BlendShare.Features.SkinWeights
{
    [System.Serializable]
    public sealed class FbxArmatureBoneData
    {
        public string m_Path;
        public bool m_HasTransformData;
        public Vector3d m_LclTranslation = Vector3d.zero;
        public Vector3d m_LclRotation = Vector3d.zero;
        public Vector3d m_LclScaling = Vector3d.one;
        public FbxRotationOrder m_RotationOrder = FbxRotationOrder.XYZ;
        public FbxTransformInheritMode m_InheritMode = FbxTransformInheritMode.RrSs;
        public bool m_RotationActive = true;
        public Vector3d m_PreRotation = Vector3d.zero;
        public Vector3d m_PostRotation = Vector3d.zero;
        public Vector3d m_RotationPivot = Vector3d.zero;
        public Vector3d m_ScalingPivot = Vector3d.zero;
        public Vector3d m_RotationOffset = Vector3d.zero;
        public Vector3d m_ScalingOffset = Vector3d.zero;
        public FbxMatrix4x4 m_EvaluatedNodeToParentMatrix = FbxMatrix4x4.Identity;
        public bool m_CreateIfMissing = true;

        public string Path => MeshNodePath.Normalize(m_Path);
        public string ParentPath => MeshNodePath.ParentPath(Path);
        public string Name => MeshNodePath.LeafName(Path);
        public bool HasTransformData => m_HasTransformData;
        public Vector3d LclTranslation => m_LclTranslation;
        public Vector3d LclRotation => m_LclRotation;
        public Vector3d LclScaling => m_LclScaling;
        public FbxMatrix4x4 EvaluatedNodeToParentMatrix => m_EvaluatedNodeToParentMatrix;

        public FbxArmatureBoneData() { }

        /// <summary>Creates an FBX armature bone record without fabricating transform data.</summary>
        public FbxArmatureBoneData(string path, bool createIfMissing)
        {
            m_Path = MeshNodePath.Normalize(path);
            m_CreateIfMissing = createIfMissing;
        }

        /// <summary>Compares the complete static FBX node transform state using a numeric tolerance.</summary>
        public bool ApproximatelyTransform(FbxArmatureBoneData other, double epsilon = 1e-8)
        {
            return other != null &&
                   m_HasTransformData &&
                   other.m_HasTransformData &&
                   m_RotationOrder == other.m_RotationOrder &&
                   m_InheritMode == other.m_InheritMode &&
                   m_RotationActive == other.m_RotationActive &&
                   m_LclTranslation.Approximately(other.m_LclTranslation, epsilon) &&
                   m_LclRotation.Approximately(other.m_LclRotation, epsilon) &&
                   m_LclScaling.Approximately(other.m_LclScaling, epsilon) &&
                   m_PreRotation.Approximately(other.m_PreRotation, epsilon) &&
                   m_PostRotation.Approximately(other.m_PostRotation, epsilon) &&
                   m_RotationPivot.Approximately(other.m_RotationPivot, epsilon) &&
                   m_ScalingPivot.Approximately(other.m_ScalingPivot, epsilon) &&
                   m_RotationOffset.Approximately(other.m_RotationOffset, epsilon) &&
                   m_ScalingOffset.Approximately(other.m_ScalingOffset, epsilon) &&
                   m_EvaluatedNodeToParentMatrix.Approximately(other.m_EvaluatedNodeToParentMatrix, epsilon);
        }
    }

    [PreferBinarySerialization]
    public sealed class FbxArmatureObject : ScriptableObject
    {
        [SerializeField, NonReorderable]
        private List<FbxArmatureBoneData> m_Bones = new();

        public IReadOnlyList<FbxArmatureBoneData> Bones => m_Bones;
        public int BoneCount => m_Bones?.Count ?? 0;

        /// <summary>Normalizes stored bone paths without fabricating missing FBX transform data.</summary>
        public void Sanitize()
        {
            SetBones(m_Bones);
        }

        /// <summary>Gets the index of a bone by normalized hierarchy path.</summary>
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

        /// <summary>Gets a bone by normalized hierarchy path.</summary>
        public FbxArmatureBoneData GetBone(string path)
        {
            int index = GetBoneIndex(path);
            return index >= 0 ? m_Bones[index] : null;
        }

        /// <summary>Checks whether a normalized hierarchy path exists.</summary>
        public bool HasBone(string path)
        {
            return GetBoneIndex(path) >= 0;
        }

        /// <summary>Enumerates stored bone paths in parent-before-child order.</summary>
        public IEnumerable<string> GetBonePathsInHierarchyOrder()
        {
            return (m_Bones ?? new List<FbxArmatureBoneData>())
                .Where(bone => bone != null)
                .Select(bone => bone.Path)
                .Where(path => path != MeshNodePath.Root)
                .Distinct()
                .OrderBy(path => path.Count(character => character == '/'))
                .ThenBy(path => path, System.StringComparer.Ordinal);
        }

        /// <summary>Adds a bone when its normalized path is not already present.</summary>
        public int GetOrAddBone(FbxArmatureBoneData bone)
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

            m_Bones ??= new List<FbxArmatureBoneData>();
            m_Bones.Add(bone);
            return m_Bones.Count - 1;
        }

        /// <summary>Replaces the armature with normalized, unique bone definitions.</summary>
        public void SetBones(IEnumerable<FbxArmatureBoneData> bones)
        {
            m_Bones = bones?
                .Where(bone => bone != null && !string.IsNullOrWhiteSpace(bone.m_Path))
                .Select(Normalize)
                .GroupBy(bone => bone.m_Path)
                .Select(group => group.First())
                .ToList() ?? new List<FbxArmatureBoneData>();
        }

        private static FbxArmatureBoneData Normalize(FbxArmatureBoneData bone)
        {
            bone.m_Path = MeshNodePath.Normalize(bone.m_Path);
            return bone;
        }

    }
}
