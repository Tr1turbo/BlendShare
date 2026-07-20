using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Fbx.Unity;
using Triturbo.BlendShare.Fbx.Ufbx;
using UnityEngine;

namespace Triturbo.BlendShare.Features.SkinWeights
{
    internal enum SkinWeightComparisonStatus
    {
        New, // Source has data that origin does not have.
        Changed, // Source and origin both have comparable entries, but the extracted value differs.
        Same,    // No effective difference. For weights, missing and zero are treated as equivalent.
        MissingSource, // Origin has an entry, but source does not. Kept as diagnostic and not selected by default.
        Unavailable
    }

    internal sealed class SkinWeightMeshClusterComparison
    {
        public string MeshPath;
        public string BonePath;
        public SkinWeightComparisonStatus Status;
        public int AffectedControlPointCount;
        public float MaxDelta;
    }

    internal sealed class SkinWeightBindposeComparison
    {
        public string MeshPath;
        public string BonePath;
        public SkinWeightComparisonStatus Status;
    }

    internal sealed class SkinWeightBoneTransformComparison
    {
        public string BonePath;
        public SkinWeightComparisonStatus Status;
    }

    internal sealed class SkinWeightBoneComparison
    {
        public string BonePath;
        public bool SourceBoneExists;
        public bool OriginBoneExists;
        public bool RequiresCreateBone => SourceBoneExists && !OriginBoneExists;
        public SkinWeightBoneTransformComparison Transform;
        public IReadOnlyList<SkinWeightMeshClusterComparison> WeightClusters = Array.Empty<SkinWeightMeshClusterComparison>();
        public IReadOnlyList<SkinWeightBindposeComparison> Bindposes = Array.Empty<SkinWeightBindposeComparison>();
    }

    internal sealed class SkinWeightFbxComparison
    {
        private const float WeightEpsilon = 0.00001f;
        private const double MatrixEpsilon = 0.0001d;

        private readonly Dictionary<string, SkinWeightBoneComparison> bonesByPath;

        public IReadOnlyList<SkinWeightBoneComparison> Bones { get; }
        public IReadOnlyList<string> Diagnostics { get; }

        private SkinWeightFbxComparison(
            IEnumerable<SkinWeightBoneComparison> bones,
            IEnumerable<string> diagnostics)
        {
            Bones = (bones ?? Enumerable.Empty<SkinWeightBoneComparison>())
                .Where(bone => bone != null && !string.IsNullOrWhiteSpace(bone.BonePath))
                .OrderBy(bone => bone.BonePath, StringComparer.Ordinal)
                .ToArray();
            bonesByPath = Bones.ToDictionary(
                bone => MeshNodePath.Normalize(bone.BonePath),
                bone => bone,
                StringComparer.Ordinal);
            Diagnostics = diagnostics?.ToArray() ?? Array.Empty<string>();
        }

        public static SkinWeightFbxComparison Empty { get; } =
            new SkinWeightFbxComparison(Array.Empty<SkinWeightBoneComparison>(), Array.Empty<string>());

        public bool TryGetBone(string bonePath, out SkinWeightBoneComparison comparison)
        {
            return bonesByPath.TryGetValue(MeshNodePath.Normalize(bonePath), out comparison);
        }

        public static SkinWeightFbxComparison BuildInspectionData(
            FbxInspectionSession session,
            MeshFeatureExtractionOptionsSet options,
            IReadOnlyList<MeshFeatureExtractionMeshRequest> meshes)
        {
            var requests = meshes?
                .Where(mesh => mesh != null)
                .ToArray() ?? Array.Empty<MeshFeatureExtractionMeshRequest>();
            if (session == null || requests.Length == 0)
            {
                return Empty;
            }

            var sourceBonePaths = new HashSet<string>(
                session.GetSourceBoneNodes().Select(node => MeshNodePath.Normalize(node.Path)),
                StringComparer.Ordinal);
            var originBonePaths = new HashSet<string>(
                session.GetOriginBoneNodes().Select(node => MeshNodePath.Normalize(node.Path)),
                StringComparer.Ordinal);
            var builders = new Dictionary<string, SkinWeightBoneComparisonBuilder>(StringComparer.Ordinal);
            foreach (string bonePath in sourceBonePaths.Concat(originBonePaths))
            {
                var builder = GetBuilder(builders, bonePath, sourceBonePaths, originBonePaths);
                builder.Transform = CompareBoneTransform(session, bonePath);
            }

            foreach (var request in requests)
            {
                CompareMesh(session, options, request.Path, sourceBonePaths, originBonePaths, builders);
            }

            var bones = builders.Values
                .Select(builder => builder.Build())
                .Where(bone => bone.WeightClusters.Count > 0 ||
                               bone.Bindposes.Count > 0 ||
                               bone.Transform != null ||
                               bone.RequiresCreateBone)
                .ToArray();
            return new SkinWeightFbxComparison(bones, session.Diagnostics);
        }

        private static void CompareMesh(
            FbxInspectionSession session,
            MeshFeatureExtractionOptionsSet options,
            string meshPath,
            HashSet<string> sourceBonePaths,
            HashSet<string> originBonePaths,
            Dictionary<string, SkinWeightBoneComparisonBuilder> builders)
        {
            string normalizedMeshPath = MeshNodePath.Normalize(meshPath);
            var sourceSkin = session.GetSourceSkin(normalizedMeshPath);
            var originSkin = session.GetOriginSkin(normalizedMeshPath);
            var sourceBonesByIndex = BuildBonePathsByIndex(sourceSkin);
            var originBonesByIndex = BuildBonePathsByIndex(originSkin);
            var sourceWeights = BuildWeightsByPath(sourceSkin, sourceBonesByIndex);
            var originWeights = BuildWeightsByPath(originSkin, originBonesByIndex);
            var sourceBindPoses = BuildBindPosesByPath(sourceSkin, sourceBonesByIndex);
            var originBindPoses = BuildBindPosesByPath(originSkin, originBonesByIndex);
            var sourceOffset = options?.GetSourceOffset(normalizedMeshPath)?.ToFbxMatrix() ?? FbxMatrix4x4.Identity;
            if (sourceOffset.TryInverse(out var inverseSourceOffset))
            {
                foreach (var bindPose in sourceBindPoses.Values)
                {
                    bindPose.m_FbxTransformMatrix = inverseSourceOffset * bindPose.m_FbxTransformMatrix;
                }
            }
            int controlPointCount = session.Origin?.GetMesh(normalizedMeshPath)?.ControlPointCount ?? 0;
            var sourceClusterPaths = BuildClusterBonePaths(sourceSkin, sourceBonesByIndex);
            var originClusterPaths = BuildClusterBonePaths(originSkin, originBonesByIndex);

            var clusterBonePaths = sourceClusterPaths.Concat(originClusterPaths)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            foreach (string bonePath in clusterBonePaths)
            {
                var builder = GetBuilder(builders, bonePath, sourceBonePaths, originBonePaths);
                var weightComparison = CompareWeights(
                    normalizedMeshPath,
                    bonePath,
                    sourceClusterPaths.Contains(bonePath),
                    originClusterPaths.Contains(bonePath),
                    sourceWeights,
                    originWeights,
                    controlPointCount);
                builder.WeightClusters.Add(weightComparison);

                if (sourceBindPoses.ContainsKey(bonePath) || originBindPoses.ContainsKey(bonePath))
                {
                    builder.Bindposes.Add(CompareBindpose(
                        normalizedMeshPath,
                        bonePath,
                        weightComparison.Status,
                        sourceBindPoses,
                        originBindPoses));
                }
            }
        }

        private static SkinWeightMeshClusterComparison CompareWeights(
            string meshPath,
            string bonePath,
            bool hasSourceCluster,
            bool hasOriginCluster,
            IReadOnlyDictionary<string, Dictionary<int, float>> sourceWeights,
            IReadOnlyDictionary<string, Dictionary<int, float>> originWeights,
            int controlPointCount)
        {
            sourceWeights.TryGetValue(bonePath, out var sourceByControlPoint);
            originWeights.TryGetValue(bonePath, out var originByControlPoint);
            int affected = 0;
            float maxDelta = 0f;
                foreach (int controlPoint in (sourceByControlPoint?.Keys ?? Enumerable.Empty<int>())
                         .Concat(originByControlPoint?.Keys ?? Enumerable.Empty<int>())
                         .Where(index => index >= 0 && index < controlPointCount)
                         .Distinct())
            {
                float source = 0f;
                float origin = 0f;
                sourceByControlPoint?.TryGetValue(controlPoint, out source);
                originByControlPoint?.TryGetValue(controlPoint, out origin);
                float delta = source - origin;
                if (Math.Abs(delta) <= WeightEpsilon)
                {
                    continue;
                }

                affected++;
                maxDelta = Math.Max(maxDelta, Math.Abs(delta));
            }

            return new SkinWeightMeshClusterComparison
            {
                MeshPath = meshPath,
                BonePath = bonePath,
                Status = GetWeightStatus(hasSourceCluster, hasOriginCluster, affected > 0),
                AffectedControlPointCount = affected,
                MaxDelta = maxDelta
            };
        }

        private static SkinWeightBindposeComparison CompareBindpose(
            string meshPath,
            string bonePath,
            SkinWeightComparisonStatus weightStatus,
            IReadOnlyDictionary<string, SkinWeightClusterData> sourceBindPoses,
            IReadOnlyDictionary<string, SkinWeightClusterData> originBindPoses)
        {
            bool hasSource = sourceBindPoses.TryGetValue(bonePath, out var source);
            bool hasOrigin = originBindPoses.TryGetValue(bonePath, out var origin);
            bool changed = hasSource &&
                           hasOrigin &&
                           (!Approximately(source.m_FbxTransformMatrix, origin.m_FbxTransformMatrix) ||
                            !Approximately(source.m_FbxTransformLinkMatrix, origin.m_FbxTransformLinkMatrix));
            return new SkinWeightBindposeComparison
            {
                MeshPath = meshPath,
                BonePath = bonePath,
                Status = GetBindposeStatus(hasSource, hasOrigin, changed, weightStatus)
            };
        }

        private static SkinWeightComparisonStatus GetWeightStatus(
            bool hasSourceCluster,
            bool hasOriginCluster,
            bool changed)
        {
            if (!changed)
            {
                return hasSourceCluster || hasOriginCluster
                    ? SkinWeightComparisonStatus.Same
                    : SkinWeightComparisonStatus.Unavailable;
            }

            if (!hasSourceCluster && hasOriginCluster)
            {
                return SkinWeightComparisonStatus.MissingSource;
            }

            if (hasSourceCluster && !hasOriginCluster)
            {
                return SkinWeightComparisonStatus.New;
            }

            return SkinWeightComparisonStatus.Changed;
        }

        private static SkinWeightComparisonStatus GetBindposeStatus(
            bool hasSource,
            bool hasOrigin,
            bool changed,
            SkinWeightComparisonStatus weightStatus)
        {
            if (!hasSource && hasOrigin)
            {
                return SkinWeightComparisonStatus.MissingSource;
            }

            if (weightStatus == SkinWeightComparisonStatus.Same &&
                hasSource != hasOrigin)
            {
                return SkinWeightComparisonStatus.Same;
            }

            return GetStatus(hasSource, hasOrigin, changed);
        }

        private static SkinWeightComparisonStatus GetStatus(
            bool hasSource,
            bool hasOrigin,
            bool changed)
        {
            if (hasSource && !hasOrigin)
            {
                return SkinWeightComparisonStatus.New;
            }

            if (!hasSource && hasOrigin)
            {
                return SkinWeightComparisonStatus.MissingSource;
            }

            if (!hasSource && !hasOrigin)
            {
                return SkinWeightComparisonStatus.Unavailable;
            }

            return changed ? SkinWeightComparisonStatus.Changed : SkinWeightComparisonStatus.Same;
        }

        private static SkinWeightBoneTransformComparison CompareBoneTransform(
            FbxInspectionSession session,
            string bonePath)
        {
            var sourceNode = session.Source?.GetNode(bonePath);
            var originNode = session.Origin?.GetNode(bonePath);
            bool hasSource = sourceNode != null;
            bool hasOrigin = originNode != null;
            bool changed = hasSource &&
                           hasOrigin &&
                           !ApproximatelyNodeTransform(sourceNode, originNode, MatrixEpsilon);
            return new SkinWeightBoneTransformComparison
            {
                BonePath = MeshNodePath.Normalize(bonePath),
                Status = GetStatus(hasSource, hasOrigin, changed)
            };
        }

        private static bool ApproximatelyNodeTransform(UfbxNode first, UfbxNode second, double epsilon)
        {
            return first != null &&
                   second != null &&
                   first.RotationOrder == second.RotationOrder &&
                   first.InheritMode == second.InheritMode &&
                   first.RotationActive == second.RotationActive &&
                   first.LclTranslation.Approximately(second.LclTranslation, epsilon) &&
                   first.LclRotation.Approximately(second.LclRotation, epsilon) &&
                   first.LclScaling.Approximately(second.LclScaling, epsilon) &&
                   first.PreRotation.Approximately(second.PreRotation, epsilon) &&
                   first.PostRotation.Approximately(second.PostRotation, epsilon) &&
                   first.RotationPivot.Approximately(second.RotationPivot, epsilon) &&
                   first.ScalingPivot.Approximately(second.ScalingPivot, epsilon) &&
                   first.RotationOffset.Approximately(second.RotationOffset, epsilon) &&
                   first.ScalingOffset.Approximately(second.ScalingOffset, epsilon) &&
                   first.NodeToParentMatrix.Approximately(second.NodeToParentMatrix, epsilon);
        }

        internal static Dictionary<int, string> BuildBonePathsByIndex(UfbxSkinDeformer skin)
        {
            return (skin?.Clusters ?? (IEnumerable<UfbxSkinCluster>)Array.Empty<UfbxSkinCluster>())
                .Select((cluster, index) => new { cluster, index })
                .Where(item => item.cluster?.BoneNode != null)
                .ToDictionary(
                    item => item.index,
                    item => MeshNodePath.Normalize(item.cluster.BoneNode.Path));
        }

        internal static Dictionary<string, Dictionary<int, float>> BuildWeightsByPath(
            UfbxSkinDeformer skin,
            IReadOnlyDictionary<int, string> bonePathByIndex)
        {
            var result = new Dictionary<string, Dictionary<int, float>>(StringComparer.Ordinal);
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
                int count = Math.Min(indices.Length, weights.Length);
                for (int i = 0; i < count; i++)
                {
                    int controlPointIndex = indices[i];
                    float weight = (float)weights[i];
                    if (controlPointIndex < 0 || weight <= 0f)
                    {
                        continue;
                    }

                    if (!result.TryGetValue(bonePath, out var weightsByControlPoint))
                    {
                        weightsByControlPoint = new Dictionary<int, float>();
                        result[bonePath] = weightsByControlPoint;
                    }

                    weightsByControlPoint.TryGetValue(controlPointIndex, out float existing);
                    weightsByControlPoint[controlPointIndex] = existing + weight;
                }
            }

            return result;
        }

        private static HashSet<string> BuildClusterBonePaths(
            UfbxSkinDeformer skin,
            IReadOnlyDictionary<int, string> bonePathByIndex)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (skin == null || bonePathByIndex == null)
            {
                return result;
            }

            for (int boneIndex = 0; boneIndex < skin.Clusters.Count; boneIndex++)
            {
                if (skin.Clusters[boneIndex] != null &&
                    bonePathByIndex.TryGetValue(boneIndex, out string bonePath))
                {
                    result.Add(bonePath);
                }
            }

            return result;
        }

        internal static Dictionary<string, SkinWeightClusterData> BuildBindPosesByPath(
            UfbxSkinDeformer skin,
            IReadOnlyDictionary<int, string> bonePathByIndex)
        {
            var result = new Dictionary<string, SkinWeightClusterData>(StringComparer.Ordinal);
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
                    var data = new SkinWeightClusterData { m_BonePath = path };
                    data.SetFbxClusterMatrices(cluster.MeshBindWorld, cluster.BindToWorld);
                    result[path] = data;
                }
            }

            return result;
        }

        private static bool Approximately(
            Triturbo.BlendShare.Fbx.FbxMatrix4x4 a,
            Triturbo.BlendShare.Fbx.FbxMatrix4x4 b)
        {
            for (int row = 0; row < 4; row++)
            {
                for (int column = 0; column < 4; column++)
                {
                    if (Math.Abs(a[row, column] - b[row, column]) > MatrixEpsilon)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool Approximately(Vector3 a, Vector3 b)
        {
            return Math.Abs(a.x - b.x) <= MatrixEpsilon &&
                   Math.Abs(a.y - b.y) <= MatrixEpsilon &&
                   Math.Abs(a.z - b.z) <= MatrixEpsilon;
        }

        private static SkinWeightBoneComparisonBuilder GetBuilder(
            Dictionary<string, SkinWeightBoneComparisonBuilder> builders,
            string bonePath,
            HashSet<string> sourceBonePaths,
            HashSet<string> originBonePaths)
        {
            string normalized = MeshNodePath.Normalize(bonePath);
            if (!builders.TryGetValue(normalized, out var builder))
            {
                builder = new SkinWeightBoneComparisonBuilder
                {
                    BonePath = normalized,
                    SourceBoneExists = sourceBonePaths.Contains(normalized),
                    OriginBoneExists = originBonePaths.Contains(normalized)
                };
                builders[normalized] = builder;
            }

            return builder;
        }

        private sealed class SkinWeightBoneComparisonBuilder
        {
            public string BonePath;
            public bool SourceBoneExists;
            public bool OriginBoneExists;
            public SkinWeightBoneTransformComparison Transform;
            public readonly List<SkinWeightMeshClusterComparison> WeightClusters = new();
            public readonly List<SkinWeightBindposeComparison> Bindposes = new();

            public SkinWeightBoneComparison Build()
            {
                return new SkinWeightBoneComparison
                {
                    BonePath = BonePath,
                    SourceBoneExists = SourceBoneExists,
                    OriginBoneExists = OriginBoneExists,
                    Transform = Transform,
                    WeightClusters = WeightClusters
                        .OrderBy(cluster => cluster.MeshPath, StringComparer.Ordinal)
                        .ToArray(),
                    Bindposes = Bindposes
                        .OrderBy(bindPose => bindPose.MeshPath, StringComparer.Ordinal)
                        .ToArray()
                };
            }
        }
    }
}
