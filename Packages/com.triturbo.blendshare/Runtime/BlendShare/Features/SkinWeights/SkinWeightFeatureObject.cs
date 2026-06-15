using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Fbx;
using UnityEngine;
using UnityEngine.Scripting.APIUpdating;

namespace Triturbo.BlendShare.Features.SkinWeights
{
    [System.Serializable]
    public sealed class SkinWeightClusterData
    {
        public string m_BonePath;
        public int[] m_Indices = System.Array.Empty<int>();
        public float[] m_Deltaweights = System.Array.Empty<float>();

        public FbxMatrix4x4 m_FbxTransformMatrix = FbxMatrix4x4.Identity;
        public FbxMatrix4x4 m_FbxTransformLinkMatrix = FbxMatrix4x4.Identity;
        public bool m_HasFbxClusterMatrices;

        public string BonePath => MeshNodePath.Normalize(m_BonePath);
        public int WeightCount => System.Math.Min(m_Indices?.Length ?? 0, m_Deltaweights?.Length ?? 0);

        public SkinWeightClusterData() { }

        public SkinWeightClusterData(string bonePath, IEnumerable<int> indices, IEnumerable<float> deltaWeights)
        {
            m_BonePath = MeshNodePath.Normalize(bonePath);
            m_Indices = indices?.ToArray() ?? System.Array.Empty<int>();
            m_Deltaweights = deltaWeights?.ToArray() ?? System.Array.Empty<float>();
        }

        public void SetFbxClusterMatrices(FbxMatrix4x4 transformMatrix, FbxMatrix4x4 transformLinkMatrix)
        {
            m_FbxTransformMatrix = transformMatrix;
            m_FbxTransformLinkMatrix = transformLinkMatrix;
            m_HasFbxClusterMatrices = true;
        }
    }

    public sealed class SkinWeightFeatureObject : MeshFeatureObject
    {
        private const float WeightEpsilon = 0.00001f;

        public const string Id = "skin-weights";

        public ArmatureObject m_Armature;
        public string m_RootBonePath = MeshNodePath.Root;

        [SerializeField, NonReorderable]
        public List<SkinWeightClusterData> m_Clusters = new();

        public override string FeatureId => Id;
        public IReadOnlyList<SkinWeightClusterData> Clusters =>
            m_Clusters != null
                ? (IReadOnlyList<SkinWeightClusterData>)m_Clusters
                : System.Array.Empty<SkinWeightClusterData>();
        public ArmatureObject Armature => m_Armature;
        public string RootBonePath => MeshNodePath.Normalize(m_RootBonePath);
        public int ClusterCount => m_Clusters?.Count ?? 0;
        public int WeightedControlPointCount => (m_Clusters ?? new List<SkinWeightClusterData>())
            .Where(cluster => cluster != null)
            .SelectMany(cluster => cluster.m_Indices ?? System.Array.Empty<int>())
            .Where(index => index >= 0)
            .Distinct()
            .Count();

        public override void Sanitize(MeshDataObject owner)
        {
            m_Armature?.Sanitize();
            m_RootBonePath = MeshNodePath.Normalize(m_RootBonePath);
            m_Clusters = (m_Clusters ?? new List<SkinWeightClusterData>())
                .Where(cluster => cluster != null)
                .Select(SanitizeCluster)
                .Where(cluster => cluster != null)
                .GroupBy(cluster => cluster.BonePath)
                .Select(MergeClusters)
                .OrderBy(cluster => cluster.BonePath, System.StringComparer.Ordinal)
                .ToList();
        }

        public void SetSkinning(
            ArmatureObject armature,
            string rootBonePath,
            IEnumerable<SkinWeightClusterData> clusters)
        {
            m_Armature = armature;
            m_RootBonePath = MeshNodePath.Normalize(rootBonePath);
            m_Clusters = clusters?.Where(cluster => cluster != null).ToList() ?? new List<SkinWeightClusterData>();
            Sanitize(null);
        }

        public IEnumerable<string> GetWeightedBonePaths()
        {
            return (m_Clusters ?? new List<SkinWeightClusterData>())
                .Where(cluster => cluster != null && cluster.WeightCount > 0)
                .Select(cluster => cluster.BonePath)
                .Where(path => path != MeshNodePath.Root)
                .Distinct(System.StringComparer.Ordinal);
        }

        public IEnumerable<string> GetNeededBonePathsInArmatureOrder()
        {
            var needed = new HashSet<string>(GetWeightedBonePaths(), System.StringComparer.Ordinal);
            foreach (string path in m_Armature?.GetBonePathsInHierarchyOrder() ?? System.Array.Empty<string>())
            {
                if (needed.Remove(path))
                {
                    yield return path;
                }
            }

            foreach (string path in (m_Clusters ?? new List<SkinWeightClusterData>())
                         .Where(cluster => cluster != null)
                         .Select(cluster => cluster.BonePath)
                         .Where(path => needed.Contains(path)))
            {
                if (needed.Remove(path))
                {
                    yield return path;
                }
            }
        }

        public bool TryGetCluster(string bonePath, out SkinWeightClusterData cluster)
        {
            string normalized = MeshNodePath.Normalize(bonePath);
            cluster = (m_Clusters ?? new List<SkinWeightClusterData>())
                .FirstOrDefault(candidate => candidate != null && candidate.BonePath == normalized);
            return cluster != null;
        }

        public bool TryGetBindPoseFbxClusterMatrices(
            string bonePath,
            out FbxMatrix4x4 transformMatrix,
            out FbxMatrix4x4 transformLinkMatrix)
        {
            if (TryGetCluster(bonePath, out var cluster) && cluster.m_HasFbxClusterMatrices)
            {
                transformMatrix = cluster.m_FbxTransformMatrix;
                transformLinkMatrix = cluster.m_FbxTransformLinkMatrix;
                return true;
            }

            transformMatrix = FbxMatrix4x4.Identity;
            transformLinkMatrix = FbxMatrix4x4.Identity;
            return false;
        }

        public bool TryGetBindPoseFbxTransformLinkMatrix(
            string bonePath,
            out FbxMatrix4x4 transformLinkMatrix)
        {
            return TryGetBindPoseFbxClusterMatrices(
                bonePath,
                out _,
                out transformLinkMatrix);
        }

        public void AccumulateDeltasForControlPoints(
            IEnumerable<int> controlPointIndices,
            IDictionary<string, float> deltasByBonePath)
        {
            if (deltasByBonePath == null)
            {
                return;
            }

            var indices = new HashSet<int>((controlPointIndices ?? System.Array.Empty<int>()).Where(index => index >= 0));
            if (indices.Count == 0)
            {
                return;
            }

            foreach (var cluster in m_Clusters ?? new List<SkinWeightClusterData>())
            {
                if (cluster == null || cluster.WeightCount == 0)
                {
                    continue;
                }

                string path = cluster.BonePath;
                for (int i = 0; i < cluster.WeightCount; i++)
                {
                    if (!indices.Contains(cluster.m_Indices[i]))
                    {
                        continue;
                    }

                    float delta = cluster.m_Deltaweights[i];
                    if (Mathf.Abs(delta) <= WeightEpsilon)
                    {
                        continue;
                    }

                    deltasByBonePath.TryGetValue(path, out float existing);
                    deltasByBonePath[path] = existing + delta;
                }
            }
        }

        private static SkinWeightClusterData SanitizeCluster(SkinWeightClusterData cluster)
        {
            string path = MeshNodePath.Normalize(cluster.m_BonePath);
            if (path == MeshNodePath.Root)
            {
                return null;
            }

            int count = cluster.WeightCount;
            var weightsByIndex = new Dictionary<int, float>();
            for (int i = 0; i < count; i++)
            {
                int index = cluster.m_Indices[i];
                float delta = cluster.m_Deltaweights[i];
                if (index < 0 || Mathf.Abs(delta) <= WeightEpsilon)
                {
                    continue;
                }

                weightsByIndex.TryGetValue(index, out float existing);
                weightsByIndex[index] = existing + delta;
            }

            var entries = weightsByIndex
                .Where(pair => Mathf.Abs(pair.Value) > WeightEpsilon)
                .OrderBy(pair => pair.Key)
                .ToArray();

            if (entries.Length == 0 && !cluster.m_HasFbxClusterMatrices)
            {
                return null;
            }

            cluster.m_BonePath = path;
            cluster.m_Indices = entries.Select(pair => pair.Key).ToArray();
            cluster.m_Deltaweights = entries.Select(pair => pair.Value).ToArray();
            return cluster;
        }

        private static SkinWeightClusterData MergeClusters(IGrouping<string, SkinWeightClusterData> group)
        {
            var result = new SkinWeightClusterData { m_BonePath = group.Key };
            var weightsByIndex = new Dictionary<int, float>();
            foreach (var cluster in group)
            {
                if (cluster.m_HasFbxClusterMatrices)
                {
                    result.m_FbxTransformMatrix = cluster.m_FbxTransformMatrix;
                    result.m_FbxTransformLinkMatrix = cluster.m_FbxTransformLinkMatrix;
                    result.m_HasFbxClusterMatrices = true;
                }

                for (int i = 0; i < cluster.WeightCount; i++)
                {
                    weightsByIndex.TryGetValue(cluster.m_Indices[i], out float existing);
                    weightsByIndex[cluster.m_Indices[i]] = existing + cluster.m_Deltaweights[i];
                }
            }

            var entries = weightsByIndex
                .Where(pair => Mathf.Abs(pair.Value) > WeightEpsilon)
                .OrderBy(pair => pair.Key)
                .ToArray();
            result.m_Indices = entries.Select(pair => pair.Key).ToArray();
            result.m_Deltaweights = entries.Select(pair => pair.Value).ToArray();
            return result;
        }
    }
}
