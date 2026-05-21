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

        private static Dictionary<int, string> BuildBonePathsByIndex(FbxSkinDeformer skin)
        {
            return (skin?.Bones ?? (IEnumerable<FbxBoneBinding>)System.Array.Empty<FbxBoneBinding>())
                .Where(bone => bone != null)
                .ToDictionary(
                    bone => bone.Index,
                    bone => SkinWeightExtractionUtility.NormalizeBonePath(bone.Path));
        }

        private static Dictionary<int, Dictionary<string, float>> BuildWeightsByControlPointAndPath(
            FbxSkinDeformer skin,
            IReadOnlyDictionary<int, string> bonePathByIndex)
        {
            var result = new Dictionary<int, Dictionary<string, float>>();
            if (skin == null || bonePathByIndex == null)
            {
                return result;
            }

            for (int controlPointIndex = 0; controlPointIndex < skin.ControlPointWeights.Count; controlPointIndex++)
            {
                foreach (var influence in skin.GetWeights(controlPointIndex))
                {
                    if (influence.Weight <= 0f || !bonePathByIndex.TryGetValue(influence.BoneIndex, out string bonePath))
                    {
                        continue;
                    }

                    if (!result.TryGetValue(controlPointIndex, out var weightsByPath))
                    {
                        weightsByPath = new Dictionary<string, float>();
                        result[controlPointIndex] = weightsByPath;
                    }

                    weightsByPath.TryGetValue(bonePath, out float existing);
                    weightsByPath[bonePath] = existing + influence.Weight;
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
            FbxSkinDeformer sourceSkin,
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

                session.AddMissingBonePatch(bonePath);
                if (sourceBindPoses.TryGetValue(bonePath, out var sourceBindPose))
                {
                    result.Add(sourceBindPose);
                }
            }

            return result;
        }

        private static Dictionary<string, SkinWeightExtraBoneBindPoseData> BuildBindPosesByPath(
            FbxSkinDeformer skin,
            IReadOnlyDictionary<int, string> bonePathByIndex)
        {
            var result = new Dictionary<string, SkinWeightExtraBoneBindPoseData>();
            if (skin == null || bonePathByIndex == null)
            {
                return result;
            }

            foreach (var bone in skin.Bones ?? (IEnumerable<FbxBoneBinding>)System.Array.Empty<FbxBoneBinding>())
            {
                if (bone == null || !bonePathByIndex.TryGetValue(bone.Index, out string path))
                {
                    continue;
                }

                var cluster = SkinWeightExtractionUtility.GetCluster(skin, bone.Index);
                if (cluster != null && cluster.HasBindPose)
                {
                    // The reader's cluster Transform/derived bind pose are not stable through SDK rebuilds.
                    // For extra bones, keep only the link-side bind matrix; generation supplies the mesh
                    // Transform from the SDK mesh node currently being rebuilt.
                    result[path] = new SkinWeightExtraBoneBindPoseData(
                        path,
                        cluster.TransformLinkMatrix);
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
