using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShapeShare;
using Triturbo.BlendShare.Core;
using Triturbo.Fbx;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.Features.SkinWeights
{
    public sealed class SkinWeightExtractionOptions : MeshFeatureExtractionOptions
    {
        public override string FeatureId => SkinWeightFeatureObject.Id;

        public SkinWeightExtractionOptions()
        {
            Enabled = false;
        }
    }

    public sealed class SkinWeightExtractionOptionsProvider
        : MeshFeatureExtractionOptionsProvider<SkinWeightExtractionOptions>
    {
        public override string FeatureId => SkinWeightFeatureObject.Id;
        public override int DisplayOrder => -50;

        protected override SkinWeightExtractionOptions CreateDefault()
        {
            return new SkinWeightExtractionOptions();
        }

        protected override void DrawOptionsGUI(
            SkinWeightExtractionOptions options,
            MeshFeatureOptionsEditorContext context)
        {
            if (options == null)
            {
                return;
            }

            options.Enabled = EditorGUILayout.ToggleLeft(Localization.S("skin_weights"), options.Enabled);
        }
    }

    public sealed class SkinWeightFeatureExtractor
        : MeshFeatureExtractor<SkinWeightFeatureObject, SkinWeightExtractionOptions>
    {
        private const float WeightEpsilon = 0.00001f;

        public static readonly SkinWeightFeatureExtractor Instance = new();

        public override string FeatureId => SkinWeightFeatureObject.Id;

        protected override MeshFeatureExtractionResult TryExtract(
            MeshFeatureExtractionContext context,
            SkinWeightExtractionOptions options,
            out SkinWeightFeatureObject feature)
        {
            feature = null;

            var sourceMesh = context.GetSourceFbxMesh();
            var sourceSkin = SkinWeightExtractionUtility.GetPrimarySourceSkinDeformer(context);
            if (sourceMesh == null || sourceSkin == null)
            {
                return MeshFeatureExtractionResult.Skipped("No source skin weights were found for this mesh.");
            }

            var originSkin = SkinWeightExtractionUtility.GetPrimaryOriginSkinDeformer(context);
            var sourceBonesByIndex = BuildBonePathsByIndex(sourceSkin);
            var originBonesByIndex = BuildBonePathsByIndex(originSkin);
            var sourceWeights = BuildWeightsByControlPointAndPath(sourceSkin, sourceBonesByIndex);
            var originWeights = BuildWeightsByControlPointAndPath(originSkin, originBonesByIndex);
            var deltaWeights = BuildDeltaWeights(sourceWeights, originWeights);
            if (deltaWeights.Count == 0)
            {
                return MeshFeatureExtractionResult.Skipped("No source skin weight deltas were found for this mesh.");
            }

            var bonePaths = deltaWeights
                .SelectMany(weights => weights.Value.Keys)
                .Distinct()
                .OrderBy(path => path, System.StringComparer.Ordinal)
                .ToArray();
            var localBoneIndexByPath = bonePaths
                .Select((path, index) => new { path, index })
                .ToDictionary(item => item.path, item => item.index);

            var extraBoneBindPoses = ExtractExtraBoneBindPoses(
                sourceSkin,
                bonePaths,
                sourceBonesByIndex,
                context.Session);
            var boneGraph = context.Session.GetBoneGraphIfCreated();

            var controlPointWeights = deltaWeights
                .Select(pair => new SkinWeightControlPointData
                {
                    m_ControlPointIndex = pair.Key,
                    m_Influences = pair.Value
                        .Where(weight => localBoneIndexByPath.ContainsKey(weight.Key))
                        .Select(weight => new SkinWeightInfluenceData
                        {
                            m_BoneIndex = localBoneIndexByPath[weight.Key],
                            m_Weight = weight.Value
                        })
                        .OrderByDescending(influence => Mathf.Abs(influence.m_Weight))
                        .ToArray()
                })
                .Where(weights => weights.m_Influences.Length > 0)
                .ToList();

            if (controlPointWeights.Count == 0)
            {
                return MeshFeatureExtractionResult.Skipped("No source skin weight deltas were found for this mesh.");
            }

            feature = ScriptableObject.CreateInstance<SkinWeightFeatureObject>();
            feature.SetSkinning(
                boneGraph,
                GetRootBonePath(context),
                sourceMesh.ControlPointCount,
                bonePaths,
                controlPointWeights,
                extraBoneBindPoses);
            return MeshFeatureExtractionResult.Success();
        }

        private static Dictionary<int, string> BuildBonePathsByIndex(UfbxSkinDeformer skin)
        {
            return (skin?.Clusters ?? (IEnumerable<UfbxSkinCluster>)System.Array.Empty<UfbxSkinCluster>())
                .Select((cluster, index) => new { cluster, index })
                .Where(item => item.cluster?.BoneNode != null)
                .ToDictionary(
                    item => item.index,
                    item => SkinWeightExtractionUtility.NormalizeBonePath(item.cluster.BoneNode.Path));
        }

        private static Dictionary<int, Dictionary<string, float>> BuildWeightsByControlPointAndPath(
            UfbxSkinDeformer skin,
            IReadOnlyDictionary<int, string> bonePathByIndex)
        {
            var result = new Dictionary<int, Dictionary<string, float>>();
            if (skin == null || bonePathByIndex == null)
            {
                return result;
            }

            for (int boneIndex = 0; boneIndex < skin.Clusters.Count; boneIndex++)
            {
                var cluster = skin.Clusters[boneIndex];
                if (cluster == null || !bonePathByIndex.TryGetValue(boneIndex, out string bonePath))
                {
                    continue;
                }

                var indices = cluster.GetIndices();
                var weights = cluster.GetWeights();
                int count = System.Math.Min(indices.Length, weights.Length);
                for (int i = 0; i < count; i++)
                {
                    int controlPointIndex = indices[i];
                    float weight = (float)weights[i];
                    if (controlPointIndex < 0 || weight <= 0f)
                    {
                        continue;
                    }

                    if (!result.TryGetValue(controlPointIndex, out var weightsByPath))
                    {
                        weightsByPath = new Dictionary<string, float>();
                        result[controlPointIndex] = weightsByPath;
                    }

                    weightsByPath.TryGetValue(bonePath, out float existing);
                    weightsByPath[bonePath] = existing + weight;
                }
            }

            return result;
        }

        private static Dictionary<int, Dictionary<string, float>> BuildDeltaWeights(
            IReadOnlyDictionary<int, Dictionary<string, float>> sourceWeights,
            IReadOnlyDictionary<int, Dictionary<string, float>> originWeights)
        {
            var result = new Dictionary<int, Dictionary<string, float>>();
            var controlPointIndices = sourceWeights.Keys.Concat(originWeights.Keys).Distinct();
            foreach (int controlPointIndex in controlPointIndices)
            {
                sourceWeights.TryGetValue(controlPointIndex, out var sourceByPath);
                originWeights.TryGetValue(controlPointIndex, out var originByPath);
                var bonePaths = (sourceByPath?.Keys ?? Enumerable.Empty<string>())
                    .Concat(originByPath?.Keys ?? Enumerable.Empty<string>())
                    .Distinct();

                foreach (string bonePath in bonePaths)
                {
                    float source = sourceByPath != null && sourceByPath.TryGetValue(bonePath, out float sourceWeight)
                        ? sourceWeight
                        : 0f;
                    float origin = originByPath != null && originByPath.TryGetValue(bonePath, out float originWeight)
                        ? originWeight
                        : 0f;
                    float delta = source - origin;
                    if (Mathf.Abs(delta) <= WeightEpsilon)
                    {
                        continue;
                    }

                    if (!result.TryGetValue(controlPointIndex, out var deltasByPath))
                    {
                        deltasByPath = new Dictionary<string, float>();
                        result[controlPointIndex] = deltasByPath;
                    }

                    deltasByPath[bonePath] = delta;
                }
            }

            return result;
        }

        private static List<SkinWeightExtraBoneBindPoseData> ExtractExtraBoneBindPoses(
            UfbxSkinDeformer sourceSkin,
            IReadOnlyList<string> bonePaths,
            IReadOnlyDictionary<int, string> sourceBonesByIndex,
            MeshFeatureExtractionSession session)
        {
            var result = new List<SkinWeightExtraBoneBindPoseData>();
            var sourceBindPoses = BuildBindPosesByPath(sourceSkin, sourceBonesByIndex);
            foreach (string bonePath in bonePaths ?? System.Array.Empty<string>())
            {
                if (session.OriginHasTransform(bonePath))
                {
                    continue;
                }

                session.AddMissingBonePatch(bonePath, FindBoneNode(sourceSkin, sourceBonesByIndex, bonePath));
                if (sourceBindPoses.TryGetValue(bonePath, out var sourceBindPose))
                {
                    result.Add(sourceBindPose);
                }
            }

            return result;
        }

        private static UfbxNode FindBoneNode(
            UfbxSkinDeformer skin,
            IReadOnlyDictionary<int, string> bonePathByIndex,
            string bonePath)
        {
            if (skin == null || bonePathByIndex == null)
            {
                return null;
            }

            string normalized = MeshNodePath.Normalize(bonePath);
            for (int boneIndex = 0; boneIndex < skin.Clusters.Count; boneIndex++)
            {
                if (!bonePathByIndex.TryGetValue(boneIndex, out string path) ||
                    MeshNodePath.Normalize(path) != normalized)
                {
                    continue;
                }

                return skin.Clusters[boneIndex]?.BoneNode;
            }

            return null;
        }

        private static Dictionary<string, SkinWeightExtraBoneBindPoseData> BuildBindPosesByPath(
            UfbxSkinDeformer skin,
            IReadOnlyDictionary<int, string> bonePathByIndex)
        {
            var result = new Dictionary<string, SkinWeightExtraBoneBindPoseData>();
            if (skin == null || bonePathByIndex == null)
            {
                return result;
            }

            for (int boneIndex = 0; boneIndex < skin.Clusters.Count; boneIndex++)
            {
                if (!bonePathByIndex.TryGetValue(boneIndex, out string path))
                {
                    continue;
                }

                var cluster = skin.Clusters[boneIndex];
                if (cluster != null)
                {
                    result[path] = new SkinWeightExtraBoneBindPoseData(
                        path,
                        cluster.MeshBindWorld,
                        cluster.BindToWorld);
                }
            }

            return result;
        }
        private static string GetRootBonePath(MeshFeatureExtractionContext context)
        {
            var sourceRenderer = SkinWeightExtractionUtility.GetSourceRenderer(context);
            string rootBonePath = SkinWeightExtractionUtility.GetTransformPath(
                sourceRenderer != null ? sourceRenderer.rootBone : null,
                context.Session.SourceFbxGo.transform);
            return MeshNodePath.Normalize(rootBonePath);
        }
    }
}
