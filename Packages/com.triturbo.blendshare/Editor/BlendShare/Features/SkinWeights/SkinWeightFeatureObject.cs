using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Features.BoneGraph;
using UnityEngine;
using UnityEngine.Scripting.APIUpdating;

namespace Triturbo.BlendShare.Features.SkinWeights
{
    [System.Serializable]
    public sealed class SkinWeightInfluenceData
    {
        public int m_BoneIndex;
        public float m_Weight;
    }

    [System.Serializable]
    public sealed class SkinWeightControlPointData
    {
        public int m_ControlPointIndex;
        public SkinWeightInfluenceData[] m_Influences;
    }

    [System.Serializable]
    public sealed class SkinWeightExtraBoneBindPoseData
    {
        public string m_Path;

        public Triturbo.Fbx.FbxMatrix4x4 m_FbxTransformMatrix = Triturbo.Fbx.FbxMatrix4x4.Identity;
        public Triturbo.Fbx.FbxMatrix4x4 m_FbxTransformLinkMatrix = Triturbo.Fbx.FbxMatrix4x4.Identity;
        public bool m_HasFbxClusterMatrices;

        public SkinWeightExtraBoneBindPoseData() { }

        public SkinWeightExtraBoneBindPoseData(
            string path,
            Triturbo.Fbx.FbxMatrix4x4 fbxTransformMatrix,
            Triturbo.Fbx.FbxMatrix4x4 fbxTransformLinkMatrix)
        {
            m_Path = MeshNodePath.Normalize(path);
            m_FbxTransformMatrix = fbxTransformMatrix;
            m_FbxTransformLinkMatrix = fbxTransformLinkMatrix;
            m_HasFbxClusterMatrices = true;
        }
    }

    [MovedFrom(true, "Triturbo.BlendShapeShare.BlendShapeData", "Triturbo.BlendShapeShare.Data.Editor")]
    public sealed class SkinWeightFeatureObject : MeshFeatureObject
    {
        public const string Id = "skin-weights";

        public BoneGraphObject m_BoneGraph;
        public string m_RootBonePath = MeshNodePath.Root;
        public int m_FbxControlPointCount = -1;
        public string[] m_BonePaths = System.Array.Empty<string>();
        public SkinWeightExtraBoneBindPoseData[] m_ExtraBoneBindPoses = System.Array.Empty<SkinWeightExtraBoneBindPoseData>();

        [SerializeField, NonReorderable]
        private List<SkinWeightControlPointData> m_ControlPointWeights = new();

        public override string FeatureId => Id;
        public IReadOnlyList<SkinWeightControlPointData> ControlPointWeights => m_ControlPointWeights;
        public int BoneSlotCount => m_BonePaths?.Length ?? 0;
        public int WeightedControlPointCount => m_ControlPointWeights?.Count ?? 0;
        public IReadOnlyList<SkinWeightExtraBoneBindPoseData> ExtraBoneBindPoses =>
            m_ExtraBoneBindPoses ?? System.Array.Empty<SkinWeightExtraBoneBindPoseData>();

        public override void Sanitize(MeshDataObject owner)
        {
            m_BoneGraph?.Sanitize();
            m_RootBonePath = MeshNodePath.Normalize(m_RootBonePath);
            m_BonePaths = (m_BonePaths ?? System.Array.Empty<string>())
                .Select(MeshNodePath.Normalize)
                .Where(path => path != MeshNodePath.Root)
                .Distinct()
                .ToArray();

            m_ExtraBoneBindPoses = (m_ExtraBoneBindPoses ?? System.Array.Empty<SkinWeightExtraBoneBindPoseData>())
                .Where(bindPose => bindPose != null && !string.IsNullOrWhiteSpace(bindPose.m_Path))
                .Select(bindPose =>
                {
                    bindPose.m_Path = MeshNodePath.Normalize(bindPose.m_Path);
                    return bindPose;
                })
                .Where(bindPose => m_BonePaths.Contains(bindPose.m_Path))
                .GroupBy(bindPose => bindPose.m_Path)
                .Select(group => group.First())
                .ToArray();

            int slotCount = BoneSlotCount;
            m_ControlPointWeights = (m_ControlPointWeights ?? new List<SkinWeightControlPointData>())
                .Where(weights => weights != null && weights.m_ControlPointIndex >= 0)
                .Select(weights =>
                {
                    weights.m_Influences = (weights.m_Influences ?? System.Array.Empty<SkinWeightInfluenceData>())
                        .Where(influence => influence != null &&
                                            influence.m_BoneIndex >= 0 &&
                                            influence.m_BoneIndex < slotCount &&
                                            !Mathf.Approximately(influence.m_Weight, 0f))
                        .GroupBy(influence => influence.m_BoneIndex)
                        .Select(group => new SkinWeightInfluenceData
                        {
                            m_BoneIndex = group.Key,
                            m_Weight = group.Sum(influence => influence.m_Weight)
                        })
                        .Where(influence => Mathf.Abs(influence.m_Weight) > 0.00001f)
                        .OrderByDescending(influence => Mathf.Abs(influence.m_Weight))
                        .ToArray();
                    return weights;
                })
                .Where(weights => weights.m_Influences.Length > 0)
                .GroupBy(weights => weights.m_ControlPointIndex)
                .Select(group => group.First())
                .OrderBy(weights => weights.m_ControlPointIndex)
                .ToList();
        }

        public void SetSkinning(
            BoneGraphObject boneGraph,
            string rootBonePath,
            int fbxControlPointCount,
            IEnumerable<string> bonePaths,
            IEnumerable<SkinWeightControlPointData> controlPointWeights,
            IEnumerable<SkinWeightExtraBoneBindPoseData> extraBoneBindPoses = null)
        {
            m_BoneGraph = boneGraph;
            m_RootBonePath = MeshNodePath.Normalize(rootBonePath);
            m_FbxControlPointCount = fbxControlPointCount;
            m_BonePaths = bonePaths?.ToArray() ?? System.Array.Empty<string>();
            m_ControlPointWeights = controlPointWeights?.Where(weights => weights != null).ToList() ??
                                    new List<SkinWeightControlPointData>();
            m_ExtraBoneBindPoses = extraBoneBindPoses?.Where(bindPose => bindPose != null).ToArray() ??
                                   System.Array.Empty<SkinWeightExtraBoneBindPoseData>();
            Sanitize(null);
        }

        public bool TryGetExtraBoneFbxClusterMatrices(
            string path,
            out Triturbo.Fbx.FbxMatrix4x4 transformMatrix,
            out Triturbo.Fbx.FbxMatrix4x4 transformLinkMatrix)
        {
            string normalized = MeshNodePath.Normalize(path);
            var entry = (m_ExtraBoneBindPoses ?? System.Array.Empty<SkinWeightExtraBoneBindPoseData>())
                .FirstOrDefault(candidate => candidate != null && MeshNodePath.Normalize(candidate.m_Path) == normalized);
            transformMatrix = entry != null ? entry.m_FbxTransformMatrix : Triturbo.Fbx.FbxMatrix4x4.Identity;
            transformLinkMatrix = entry != null ? entry.m_FbxTransformLinkMatrix : Triturbo.Fbx.FbxMatrix4x4.Identity;
            return entry != null && entry.m_HasFbxClusterMatrices;
        }

        public bool TryGetExtraBoneFbxTransformLinkMatrix(
            string path,
            out Triturbo.Fbx.FbxMatrix4x4 transformLinkMatrix)
        {
            return TryGetExtraBoneFbxClusterMatrices(
                path,
                out _,
                out transformLinkMatrix);
        }
    }
}
