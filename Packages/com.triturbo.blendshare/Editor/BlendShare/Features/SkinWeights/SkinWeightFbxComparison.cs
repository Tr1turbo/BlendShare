using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Fbx.Unity;
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
        public SkinWeightComparisonStatus Status;
        public bool SourceExists;
        public bool OriginExists;
        public int AffectedControlPointCount;
        public double MaxDelta;
    }

    internal sealed class SkinWeightBindposeComparison
    {
        public SkinWeightComparisonStatus Status;
        public bool SourceExists;
        public bool OriginExists;
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
        public IReadOnlyList<SkinWeightMeshBoneComparison> Meshes = Array.Empty<SkinWeightMeshBoneComparison>();
        public bool HasMeshData => Meshes.Count > 0;
    }

    internal sealed class SkinWeightMeshBoneComparison
    {
        public string MeshPath;
        public string BonePath;
        public SkinWeightMeshClusterComparison Weight;
        public SkinWeightBindposeComparison Bindpose;
    }

    internal sealed class SkinWeightMeshComparison
    {
        public string MeshPath;
        public IReadOnlyList<SkinWeightMeshBoneComparison> Bones = Array.Empty<SkinWeightMeshBoneComparison>();
    }

    internal sealed class SkinWeightFbxComparison
    {
        private const double WeightComparisonTolerance = 0.00001d;
        private const double MatrixComparisonTolerance = 0.0001d;

        private readonly Dictionary<string, SkinWeightBoneComparison> bonesByPath;

        public IReadOnlyList<SkinWeightBoneComparison> Bones { get; }
        public IReadOnlyList<SkinWeightBoneComparison> RelevantBones { get; }
        public IReadOnlyList<SkinWeightMeshComparison> Meshes { get; }
        public IReadOnlyList<string> Diagnostics { get; }

        internal SkinWeightFbxComparison(
            IEnumerable<SkinWeightBoneComparison> bones,
            IEnumerable<SkinWeightMeshBoneComparison> pairs,
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
            var relevantBonePaths = new HashSet<string>(
                Bones.Where(bone =>
                        bone.HasMeshData ||
                        bone.Transform?.Status == SkinWeightComparisonStatus.Changed)
                    .Select(bone => bone.BonePath),
                StringComparer.Ordinal);
            foreach (string path in relevantBonePaths.ToArray())
            {
                string parent = MeshNodePath.ParentPath(path);
                while (parent != MeshNodePath.Root)
                {
                    if (bonesByPath.TryGetValue(parent, out var parentBone) && parentBone.RequiresCreateBone)
                    {
                        relevantBonePaths.Add(parent);
                    }

                    parent = MeshNodePath.ParentPath(parent);
                }
            }

            RelevantBones = Bones.Where(bone => relevantBonePaths.Contains(bone.BonePath)).ToArray();
            Meshes = (pairs ?? Enumerable.Empty<SkinWeightMeshBoneComparison>())
                .Where(pair => pair != null)
                .GroupBy(pair => MeshFeatureExtractionSession.BuildMeshKey(pair.MeshPath), StringComparer.Ordinal)
                .Select(group => new SkinWeightMeshComparison
                {
                    MeshPath = group.First().MeshPath,
                    Bones = group.OrderBy(pair => pair.BonePath, StringComparer.Ordinal).ToArray()
                })
                .OrderBy(mesh => mesh.MeshPath, StringComparer.Ordinal)
                .ToArray();
            Diagnostics = diagnostics?.ToArray() ?? Array.Empty<string>();
        }

        public static SkinWeightFbxComparison Empty { get; } =
            new SkinWeightFbxComparison(
                Array.Empty<SkinWeightBoneComparison>(),
                Array.Empty<SkinWeightMeshBoneComparison>(),
                Array.Empty<string>());

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
            var pairs = new List<SkinWeightMeshBoneComparison>();
            foreach (string bonePath in sourceBonePaths.Concat(originBonePaths))
            {
                var builder = GetBuilder(builders, bonePath, sourceBonePaths, originBonePaths);
                builder.Transform = CompareBoneTransform(session, bonePath);
            }

            foreach (var request in requests)
            {
                CompareMesh(
                    session,
                    options,
                    request.Path,
                    sourceBonePaths,
                    originBonePaths,
                    builders,
                    pairs);
            }

            var bones = builders.Values
                .Select(builder => builder.Build())
                .Where(bone => bone.HasMeshData ||
                               bone.Transform != null ||
                               bone.RequiresCreateBone)
                .ToArray();
            return new SkinWeightFbxComparison(bones, pairs, session.Diagnostics);
        }

        private static void CompareMesh(
            FbxInspectionSession session,
            MeshFeatureExtractionOptionsSet options,
            string meshPath,
            HashSet<string> sourceBonePaths,
            HashSet<string> originBonePaths,
            Dictionary<string, SkinWeightBoneComparisonBuilder> builders,
            List<SkinWeightMeshBoneComparison> pairs)
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
                    bonePath,
                    sourceClusterPaths.Contains(bonePath),
                    originClusterPaths.Contains(bonePath),
                    sourceWeights,
                    originWeights,
                    controlPointCount);
                SkinWeightBindposeComparison bindposeComparison = null;
                if (sourceBindPoses.ContainsKey(bonePath) || originBindPoses.ContainsKey(bonePath))
                {
                    bindposeComparison = CompareBindpose(
                        weightComparison.Status,
                        sourceBindPoses,
                        originBindPoses,
                        bonePath);
                }

                var pair = new SkinWeightMeshBoneComparison
                {
                    MeshPath = normalizedMeshPath,
                    BonePath = bonePath,
                    Weight = weightComparison,
                    Bindpose = bindposeComparison
                };
                builder.Meshes.Add(pair);
                pairs.Add(pair);
            }
        }

        private static SkinWeightMeshClusterComparison CompareWeights(
            string bonePath,
            bool hasSourceCluster,
            bool hasOriginCluster,
            IReadOnlyDictionary<string, Dictionary<int, double>> sourceWeights,
            IReadOnlyDictionary<string, Dictionary<int, double>> originWeights,
            int controlPointCount)
        {
            sourceWeights.TryGetValue(bonePath, out var sourceByControlPoint);
            originWeights.TryGetValue(bonePath, out var originByControlPoint);
            int affected = 0;
            double maxDelta = 0d;
                foreach (int controlPoint in (sourceByControlPoint?.Keys ?? Enumerable.Empty<int>())
                         .Concat(originByControlPoint?.Keys ?? Enumerable.Empty<int>())
                         .Where(index => index >= 0 && index < controlPointCount)
                         .Distinct())
            {
                double source = 0d;
                double origin = 0d;
                sourceByControlPoint?.TryGetValue(controlPoint, out source);
                originByControlPoint?.TryGetValue(controlPoint, out origin);
                double delta = source - origin;
                if (!IsWeightDeltaChanged(delta))
                {
                    continue;
                }

                affected++;
                maxDelta = Math.Max(maxDelta, Math.Abs(delta));
            }

            return new SkinWeightMeshClusterComparison
            {
                Status = GetWeightStatus(hasSourceCluster, hasOriginCluster, affected > 0),
                SourceExists = hasSourceCluster,
                OriginExists = hasOriginCluster,
                AffectedControlPointCount = affected,
                MaxDelta = maxDelta
            };
        }

        internal static bool IsWeightDeltaChanged(double delta)
        {
            return Math.Abs(delta) > WeightComparisonTolerance;
        }

        private static SkinWeightBindposeComparison CompareBindpose(
            SkinWeightComparisonStatus weightStatus,
            IReadOnlyDictionary<string, SkinWeightClusterData> sourceBindPoses,
            IReadOnlyDictionary<string, SkinWeightClusterData> originBindPoses,
            string bonePath)
        {
            bool hasSource = sourceBindPoses.TryGetValue(bonePath, out var source);
            bool hasOrigin = originBindPoses.TryGetValue(bonePath, out var origin);
            bool changed = hasSource &&
                           hasOrigin &&
                           (!Approximately(source.m_FbxTransformMatrix, origin.m_FbxTransformMatrix) ||
                            !Approximately(source.m_FbxTransformLinkMatrix, origin.m_FbxTransformLinkMatrix));
            return new SkinWeightBindposeComparison
            {
                Status = GetBindposeStatus(hasSource, hasOrigin, changed, weightStatus),
                SourceExists = hasSource,
                OriginExists = hasOrigin
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
                           !ApproximatelyNodeTransform(sourceNode, originNode, MatrixComparisonTolerance);
            return new SkinWeightBoneTransformComparison
            {
                BonePath = MeshNodePath.Normalize(bonePath),
                Status = GetStatus(hasSource, hasOrigin, changed)
            };
        }

        private static bool ApproximatelyNodeTransform(IFbxNode first, IFbxNode second, double epsilon)
        {
            if (first == null || second == null)
            {
                return false;
            }

            var firstRaw = first.RawTransform;
            var secondRaw = second.RawTransform;
            return firstRaw.RotationOrder == secondRaw.RotationOrder &&
                   firstRaw.OriginalInheritMode == secondRaw.OriginalInheritMode &&
                   firstRaw.RotationActive == secondRaw.RotationActive &&
                   firstRaw.LclTranslation.Approximately(secondRaw.LclTranslation, epsilon) &&
                   firstRaw.LclRotation.Approximately(secondRaw.LclRotation, epsilon) &&
                   firstRaw.LclScaling.Approximately(secondRaw.LclScaling, epsilon) &&
                   firstRaw.PreRotation.Approximately(secondRaw.PreRotation, epsilon) &&
                   firstRaw.PostRotation.Approximately(secondRaw.PostRotation, epsilon) &&
                   firstRaw.RotationPivot.Approximately(secondRaw.RotationPivot, epsilon) &&
                   firstRaw.ScalingPivot.Approximately(secondRaw.ScalingPivot, epsilon) &&
                   firstRaw.RotationOffset.Approximately(secondRaw.RotationOffset, epsilon) &&
                   firstRaw.ScalingOffset.Approximately(secondRaw.ScalingOffset, epsilon) &&
                   first.NodeToParentMatrix.Approximately(second.NodeToParentMatrix, epsilon);
        }

        internal static Dictionary<int, string> BuildBonePathsByIndex(IFbxSkinDeformer skin)
        {
            return (skin?.Clusters ?? (IEnumerable<IFbxSkinCluster>)Array.Empty<IFbxSkinCluster>())
                .Select((cluster, index) => new
                {
                    index,
                    path = cluster?.BoneNode?.Path ?? cluster?.BonePath
                })
                .Where(item => !string.IsNullOrWhiteSpace(item.path))
                .ToDictionary(
                    item => item.index,
                    item => MeshNodePath.Normalize(item.path));
        }

        internal static Dictionary<string, Dictionary<int, double>> BuildWeightsByPath(
            IFbxSkinDeformer skin,
            IReadOnlyDictionary<int, string> bonePathByIndex)
        {
            var result = new Dictionary<string, Dictionary<int, double>>(StringComparer.Ordinal);
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

                var indices = cluster.GetControlPointIndices();
                var weights = cluster.GetWeights();
                int count = Math.Min(indices.Length, weights.Length);
                for (int i = 0; i < count; i++)
                {
                    int controlPointIndex = indices[i];
                    double weight = weights[i];
                    if (controlPointIndex < 0 || weight <= 0d)
                    {
                        continue;
                    }

                    if (!result.TryGetValue(bonePath, out var weightsByControlPoint))
                    {
                        weightsByControlPoint = new Dictionary<int, double>();
                        result[bonePath] = weightsByControlPoint;
                    }

                    weightsByControlPoint.TryGetValue(controlPointIndex, out double existing);
                    weightsByControlPoint[controlPointIndex] = existing + weight;
                }
            }

            return result;
        }

        private static HashSet<string> BuildClusterBonePaths(
            IFbxSkinDeformer skin,
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
            IFbxSkinDeformer skin,
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
                    if (Math.Abs(a[row, column] - b[row, column]) > MatrixComparisonTolerance)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool Approximately(Vector3 a, Vector3 b)
        {
            return Math.Abs(a.x - b.x) <= MatrixComparisonTolerance &&
                   Math.Abs(a.y - b.y) <= MatrixComparisonTolerance &&
                   Math.Abs(a.z - b.z) <= MatrixComparisonTolerance;
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
            public readonly List<SkinWeightMeshBoneComparison> Meshes = new();

            public SkinWeightBoneComparison Build()
            {
                return new SkinWeightBoneComparison
                {
                    BonePath = BonePath,
                    SourceBoneExists = SourceBoneExists,
                    OriginBoneExists = OriginBoneExists,
                    Transform = Transform,
                    Meshes = Meshes
                        .OrderBy(pair => pair.MeshPath, StringComparer.Ordinal)
                        .ToArray()
                };
            }
        }

    }
}
