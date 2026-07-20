using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Fbx;
using UnityEngine;

namespace Triturbo.BlendShare.Features.SkinWeights
{
    [System.Serializable]
    public sealed class SkinWeightClusterData
    {
        private const float WeightEpsilon = 0.00001f;

        public string m_BonePath;
        public SparseArray<float> m_DeltaWeights = new();

        public FbxMatrix4x4 m_FbxTransformMatrix = FbxMatrix4x4.Identity;
        public FbxMatrix4x4 m_FbxTransformLinkMatrix = FbxMatrix4x4.Identity;
        public bool m_HasFbxClusterMatrices;

        public string BonePath => MeshNodePath.Normalize(m_BonePath);
        public int WeightCount => m_DeltaWeights?.Count ?? 0;

        public SkinWeightClusterData() { }

        public SkinWeightClusterData(string bonePath, IEnumerable<int> indices, IEnumerable<float> deltaWeights)
        {
            m_BonePath = MeshNodePath.Normalize(bonePath);
            SetWeights(indices, deltaWeights);
        }

        public IEnumerable<KeyValuePair<int, float>> GetWeights()
        {
            return m_DeltaWeights?.Entries() ?? System.Linq.Enumerable.Empty<KeyValuePair<int, float>>();
        }

        public float GetWeight(int index)
        {
            return m_DeltaWeights?[index] ?? 0f;
        }

        public void SetWeight(int index, float deltaWeight)
        {
            m_DeltaWeights ??= new SparseArray<float>();
            if (index < 0 || Mathf.Abs(deltaWeight) <= WeightEpsilon)
            {
                m_DeltaWeights.Remove(index);
                return;
            }

            m_DeltaWeights[index] = deltaWeight;
        }

        public void SetWeights(IEnumerable<int> indices, IEnumerable<float> deltaWeights)
        {
            m_DeltaWeights = new SparseArray<float>();
            var indexArray = indices?.ToArray() ?? System.Array.Empty<int>();
            var weightArray = deltaWeights?.ToArray() ?? System.Array.Empty<float>();
            int count = System.Math.Min(indexArray.Length, weightArray.Length);
            for (int i = 0; i < count; i++)
            {
                SetWeight(indexArray[i], weightArray[i]);
            }
        }

        public SkinWeightClusterData WithWeights(IEnumerable<int> indices, IEnumerable<float> deltaWeights)
        {
            SetWeights(indices, deltaWeights);
            return this;
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

        public FbxArmatureObject m_Armature;
        public string m_RootBonePath = MeshNodePath.Root;

        [SerializeField, NonReorderable]
        public List<SkinWeightClusterData> m_Clusters = new();

        public override string FeatureId => Id;
        public IReadOnlyList<SkinWeightClusterData> Clusters =>
            m_Clusters != null
                ? (IReadOnlyList<SkinWeightClusterData>)m_Clusters
                : System.Array.Empty<SkinWeightClusterData>();
        public FbxArmatureObject Armature => m_Armature;
        public string RootBonePath => MeshNodePath.Normalize(m_RootBonePath);
        public int ClusterCount => m_Clusters?.Count ?? 0;
        public int WeightedControlPointCount => (m_Clusters ?? new List<SkinWeightClusterData>())
            .Where(cluster => cluster != null)
            .SelectMany(cluster => cluster.GetWeights().Select(pair => pair.Key))
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
            FbxArmatureObject armature,
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

        private static SkinWeightClusterData SanitizeCluster(SkinWeightClusterData cluster)
        {
            string path = MeshNodePath.Normalize(cluster.m_BonePath);
            if (path == MeshNodePath.Root)
            {
                return null;
            }

            var weightsByIndex = new Dictionary<int, float>();
            foreach (var pair in cluster.GetWeights())
            {
                int index = pair.Key;
                float delta = pair.Value;
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
            cluster.SetWeights(
                entries.Select(pair => pair.Key),
                entries.Select(pair => pair.Value));
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

                foreach (var pair in cluster.GetWeights())
                {
                    weightsByIndex.TryGetValue(pair.Key, out float existing);
                    weightsByIndex[pair.Key] = existing + pair.Value;
                }
            }

            var entries = weightsByIndex
                .Where(pair => Mathf.Abs(pair.Value) > WeightEpsilon)
                .OrderBy(pair => pair.Key)
                .ToArray();
            result.SetWeights(
                entries.Select(pair => pair.Key),
                entries.Select(pair => pair.Value));
            return result;
        }
    }
}
