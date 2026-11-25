using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Fbx;

namespace Triturbo.BlendShare.Pipeline.Inspection
{
    public enum SkinWeightComparisonStatus
    {
        New = 0,
        Changed = 1,
        Same = 2,
        MissingSource = 3,
        Unavailable = 4
    }

    public sealed class SkinWeightMeshClusterComparison
    {
        public SkinWeightComparisonStatus Status { get; set; }
        public bool SourceExists { get; set; }
        public bool OriginExists { get; set; }
        public int AffectedControlPointCount { get; set; }
        public double MaxDelta { get; set; }
    }

    public sealed class SkinWeightBindposeComparison
    {
        public SkinWeightComparisonStatus Status { get; set; }
        public bool SourceExists { get; set; }
        public bool OriginExists { get; set; }
    }

    public sealed class SkinWeightBoneTransformComparison
    {
        public string BonePath { get; set; } = string.Empty;
        public SkinWeightComparisonStatus Status { get; set; }
    }

    public sealed class SkinWeightBoneComparison
    {
        public string BonePath { get; set; } = string.Empty;
        public bool SourceBoneExists { get; set; }
        public bool OriginBoneExists { get; set; }
        public bool RequiresCreateBone => SourceBoneExists && !OriginBoneExists;
        public SkinWeightBoneTransformComparison Transform { get; set; }
        public IReadOnlyList<SkinWeightMeshBoneComparison> Meshes { get; set; } = Array.Empty<SkinWeightMeshBoneComparison>();
        public bool HasMeshData => Meshes.Count > 0;
    }

    public sealed class SkinWeightMeshBoneComparison
    {
        public string MeshPath { get; set; } = string.Empty;
        public string BonePath { get; set; } = string.Empty;
        public SkinWeightMeshClusterComparison Weight { get; set; }
        public SkinWeightBindposeComparison Bindpose { get; set; }
    }

    public sealed class SkinWeightMeshComparison
    {
        public string MeshPath { get; set; } = string.Empty;
        public IReadOnlyList<SkinWeightMeshBoneComparison> Bones { get; set; } = Array.Empty<SkinWeightMeshBoneComparison>();
    }

    /// <summary>
    /// Compares Source vs Original FBX skin weights, cluster bind matrices, and bone transforms
    /// in Origin FBX native local space using BlendShare 2.0's separated <c>L</c> (mesh-local alignment)
    /// and <c>W</c> (scene/world basis conversion) algebra with unit-scaled physical-meter tolerances.
    /// </summary>
    public sealed class SkinWeightFbxComparison
    {
        private const double k_WeightComparisonTolerance = 0.0001d;
        private const double k_LinearMatrixTolerance = 0.0001d;
        private const double k_TranslationToleranceMeters = 0.0001d;

        private readonly Dictionary<string, SkinWeightBoneComparison> m_BonesByPath;

        public IReadOnlyList<SkinWeightBoneComparison> Bones { get; }
        public IReadOnlyList<SkinWeightBoneComparison> RelevantBones { get; }
        public IReadOnlyList<SkinWeightMeshComparison> Meshes { get; }
        public IReadOnlyList<string> Diagnostics { get; }

        private SkinWeightFbxComparison(
            IEnumerable<SkinWeightBoneComparison> bones,
            IEnumerable<SkinWeightMeshBoneComparison> pairs,
            IEnumerable<string> diagnostics)
        {
            Bones = (bones ?? Enumerable.Empty<SkinWeightBoneComparison>())
                .Where(bone => bone != null && !string.IsNullOrWhiteSpace(bone.BonePath))
                .OrderBy(bone => bone.BonePath, StringComparer.Ordinal)
                .ToArray();

            m_BonesByPath = new Dictionary<string, SkinWeightBoneComparison>(StringComparer.Ordinal);
            for (int i = 0; i < Bones.Count; i++)
            {
                string key = FbxNameUtility.NormalizePath(Bones[i].BonePath);
                if (!m_BonesByPath.ContainsKey(key))
                {
                    m_BonesByPath.Add(key, Bones[i]);
                }
            }

            var relevantBonePaths = new HashSet<string>(
                Bones.Where(bone =>
                        bone.HasMeshData ||
                        bone.Transform?.Status == SkinWeightComparisonStatus.Changed)
                    .Select(bone => bone.BonePath),
                StringComparer.Ordinal);

            foreach (string path in relevantBonePaths.ToArray())
            {
                string parent = GetParentPath(path);
                while (!string.IsNullOrEmpty(parent))
                {
                    if (m_BonesByPath.TryGetValue(parent, out var parentBone) && parentBone.RequiresCreateBone)
                    {
                        relevantBonePaths.Add(parent);
                    }

                    parent = GetParentPath(parent);
                }
            }

            RelevantBones = Bones.Where(bone => relevantBonePaths.Contains(bone.BonePath)).ToArray();

            Meshes = (pairs ?? Enumerable.Empty<SkinWeightMeshBoneComparison>())
                .Where(pair => pair != null)
                .GroupBy(pair => FbxNameUtility.NormalizePath(pair.MeshPath), StringComparer.Ordinal)
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
            return m_BonesByPath.TryGetValue(FbxNameUtility.NormalizePath(bonePath ?? string.Empty), out comparison);
        }

        public static SkinWeightFbxComparison BuildInspectionData(
            FbxInspectionSession session,
            IReadOnlyList<string> meshPaths,
            Func<string, MeshFeatureSourceOffset> sourceOffsetResolver = null)
        {
            if (session == null || meshPaths == null || meshPaths.Count == 0)
            {
                return Empty;
            }

            double originUnitMeters = CanonicalMeshSpace.IsValidUnitMeters(session.OriginUnitMeters)
                ? session.OriginUnitMeters
                : 1d;
            double translationTol = k_TranslationToleranceMeters / originUnitMeters;
            var worldToOrigin = session.SourceWorldToOriginRaw;

            var sourceBonePaths = new HashSet<string>(
                session.GetSourceBoneNodes()
                    .Select(node => FbxNameUtility.NormalizePath(node.Path))
                    .Where(p => !string.IsNullOrEmpty(p)),
                StringComparer.Ordinal);
            var originBonePaths = new HashSet<string>(
                session.GetOriginBoneNodes()
                    .Select(node => FbxNameUtility.NormalizePath(node.Path))
                    .Where(p => !string.IsNullOrEmpty(p)),
                StringComparer.Ordinal);

            var builders = new Dictionary<string, SkinWeightBoneComparisonBuilder>(StringComparer.Ordinal);
            var pairs = new List<SkinWeightMeshBoneComparison>();

            foreach (string bonePath in sourceBonePaths.Concat(originBonePaths).Distinct(StringComparer.Ordinal))
            {
                var builder = GetBuilder(builders, bonePath, sourceBonePaths, originBonePaths);
                builder.Transform = CompareBoneTransform(session, bonePath, worldToOrigin, translationTol);
            }

            for (int i = 0; i < meshPaths.Count; i++)
            {
                string meshPath = meshPaths[i];
                if (string.IsNullOrWhiteSpace(meshPath)) continue;
                var rawOffset = sourceOffsetResolver?.Invoke(meshPath)?.ToFbxMatrix() ?? FbxMatrix4x4.Identity;
                CompareMesh(
                    session,
                    meshPath,
                    rawOffset,
                    worldToOrigin,
                    translationTol,
                    sourceBonePaths,
                    originBonePaths,
                    builders,
                    pairs);
            }

            var bones = builders.Values
                .Select(builder => builder.Build())
                .Where(bone => bone.HasMeshData || bone.Transform != null || bone.RequiresCreateBone)
                .ToArray();

            return new SkinWeightFbxComparison(bones, pairs, session.Diagnostics);
        }

        private static void CompareMesh(
            FbxInspectionSession session,
            string meshPath,
            FbxMatrix4x4 sourceToOriginRawOffset,
            FbxAffineSpaceTransform worldToOrigin,
            double translationTolerance,
            HashSet<string> sourceBonePaths,
            HashSet<string> originBonePaths,
            Dictionary<string, SkinWeightBoneComparisonBuilder> builders,
            List<SkinWeightMeshBoneComparison> pairs)
        {
            string normalizedMeshPath = FbxNameUtility.NormalizePath(meshPath);
            var sourceSkin = session.GetSourceSkin(normalizedMeshPath);
            var originSkin = session.GetOriginSkin(normalizedMeshPath);
            var sourceBonesByIndex = BuildBonePathsByIndex(sourceSkin);
            var originBonesByIndex = BuildBonePathsByIndex(originSkin);
            var sourceWeights = BuildWeightsByPath(sourceSkin, sourceBonesByIndex);
            var originWeights = BuildWeightsByPath(originSkin, originBonesByIndex);

            bool hasValidL = session.TryBuildSourceMeshToOriginTransform(sourceToOriginRawOffset, out var meshLocalToOrigin);
            var sourceBindPoses = BuildBindPosesByPath(sourceSkin, sourceBonesByIndex, hasValidL ? meshLocalToOrigin : FbxAffineSpaceTransform.Identity, worldToOrigin);
            var originBindPoses = BuildBindPosesByPath(originSkin, originBonesByIndex, FbxAffineSpaceTransform.Identity, FbxAffineSpaceTransform.Identity);

            int controlPointCount = session.Origin?.GetMesh(normalizedMeshPath)?.ControlPointCount ?? 0;
            var sourceClusterPaths = BuildClusterBonePaths(sourceSkin, sourceBonesByIndex);
            var originClusterPaths = BuildClusterBonePaths(originSkin, originBonesByIndex);

            var clusterBonePaths = sourceClusterPaths.Concat(originClusterPaths)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            for (int i = 0; i < clusterBonePaths.Length; i++)
            {
                string bonePath = clusterBonePaths[i];
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
                        bonePath,
                        translationTolerance);
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

            var controlPoints = new HashSet<int>();
            if (sourceByControlPoint != null)
            {
                foreach (int cp in sourceByControlPoint.Keys)
                {
                    if (cp >= 0 && cp < controlPointCount) controlPoints.Add(cp);
                }
            }

            if (originByControlPoint != null)
            {
                foreach (int cp in originByControlPoint.Keys)
                {
                    if (cp >= 0 && cp < controlPointCount) controlPoints.Add(cp);
                }
            }

            foreach (int cp in controlPoints)
            {
                double src = 0d;
                double org = 0d;
                sourceByControlPoint?.TryGetValue(cp, out src);
                originByControlPoint?.TryGetValue(cp, out org);
                double delta = src - org;
                if (Math.Abs(delta) <= k_WeightComparisonTolerance)
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

        private static SkinWeightBindposeComparison CompareBindpose(
            SkinWeightComparisonStatus weightStatus,
            IReadOnlyDictionary<string, ClusterBindPair> sourceBindPoses,
            IReadOnlyDictionary<string, ClusterBindPair> originBindPoses,
            string bonePath,
            double translationTolerance)
        {
            bool hasSource = sourceBindPoses.TryGetValue(bonePath, out var source);
            bool hasOrigin = originBindPoses.TryGetValue(bonePath, out var origin);
            bool changed = hasSource &&
                           hasOrigin &&
                           (!MatricesApproximatelyEqual(source.TransformMatrix, origin.TransformMatrix, k_LinearMatrixTolerance, translationTolerance) ||
                            !MatricesApproximatelyEqual(source.TransformLinkMatrix, origin.TransformLinkMatrix, k_LinearMatrixTolerance, translationTolerance));
            return new SkinWeightBindposeComparison
            {
                Status = GetBindposeStatus(hasSource, hasOrigin, changed, weightStatus),
                SourceExists = hasSource,
                OriginExists = hasOrigin
            };
        }

        private static SkinWeightComparisonStatus GetWeightStatus(bool hasSourceCluster, bool hasOriginCluster, bool changed)
        {
            if (!changed)
            {
                return hasSourceCluster || hasOriginCluster
                    ? SkinWeightComparisonStatus.Same
                    : SkinWeightComparisonStatus.Unavailable;
            }

            if (!hasSourceCluster && hasOriginCluster) return SkinWeightComparisonStatus.MissingSource;
            if (hasSourceCluster && !hasOriginCluster) return SkinWeightComparisonStatus.New;
            return SkinWeightComparisonStatus.Changed;
        }

        private static SkinWeightComparisonStatus GetBindposeStatus(
            bool hasSource,
            bool hasOrigin,
            bool changed,
            SkinWeightComparisonStatus weightStatus)
        {
            if (!hasSource && hasOrigin) return SkinWeightComparisonStatus.MissingSource;
            if (weightStatus == SkinWeightComparisonStatus.Same && hasSource != hasOrigin) return SkinWeightComparisonStatus.Same;
            return GetStatus(hasSource, hasOrigin, changed);
        }

        private static SkinWeightComparisonStatus GetStatus(bool hasSource, bool hasOrigin, bool changed)
        {
            if (hasSource && !hasOrigin) return SkinWeightComparisonStatus.New;
            if (!hasSource && hasOrigin) return SkinWeightComparisonStatus.MissingSource;
            if (!hasSource && !hasOrigin) return SkinWeightComparisonStatus.Unavailable;
            return changed ? SkinWeightComparisonStatus.Changed : SkinWeightComparisonStatus.Same;
        }

        private static SkinWeightBoneTransformComparison CompareBoneTransform(
            FbxInspectionSession session,
            string bonePath,
            FbxAffineSpaceTransform worldToOrigin,
            double translationTolerance)
        {
            var sourceNode = session.Source?.GetNode(bonePath);
            var originNode = session.Origin?.GetNode(bonePath);
            bool hasSource = sourceNode != null;
            bool hasOrigin = originNode != null;
            bool changed = false;

            if (hasSource && hasOrigin)
            {
                if (worldToOrigin.IsIdentity)
                {
                    changed = !ApproximatelyNodeTransform(sourceNode, originNode, k_LinearMatrixTolerance, translationTolerance);
                }
                else
                {
                    FbxMatrix4x4 sourceLocalInOrigin =
                        worldToOrigin.InverseOffset * sourceNode.NodeToParentMatrix * worldToOrigin.Offset;
                    changed = !MatricesApproximatelyEqual(
                        sourceLocalInOrigin,
                        originNode.NodeToParentMatrix,
                        k_LinearMatrixTolerance,
                        translationTolerance);
                }
            }

            return new SkinWeightBoneTransformComparison
            {
                BonePath = FbxNameUtility.NormalizePath(bonePath),
                Status = GetStatus(hasSource, hasOrigin, changed)
            };
        }

        private static bool ApproximatelyNodeTransform(
            IFbxNode first,
            IFbxNode second,
            double linearTolerance,
            double translationTolerance)
        {
            if (first == null || second == null)
            {
                return false;
            }

            if (!MatricesApproximatelyEqual(
                    first.NodeToParentMatrix,
                    second.NodeToParentMatrix,
                    linearTolerance,
                    translationTolerance))
            {
                return false;
            }

            var firstRaw = first.RawTransform;
            var secondRaw = second.RawTransform;
            if (!firstRaw.LclScaling.IsZero() && !secondRaw.LclScaling.IsZero())
            {
                return firstRaw.RotationOrder == secondRaw.RotationOrder &&
                       firstRaw.OriginalInheritMode == secondRaw.OriginalInheritMode &&
                       firstRaw.RotationActive == secondRaw.RotationActive &&
                       VectorsApproximatelyEqual(firstRaw.LclTranslation, secondRaw.LclTranslation, translationTolerance) &&
                       VectorsApproximatelyEqual(firstRaw.LclRotation, secondRaw.LclRotation, linearTolerance) &&
                       VectorsApproximatelyEqual(firstRaw.LclScaling, secondRaw.LclScaling, linearTolerance) &&
                       VectorsApproximatelyEqual(firstRaw.PreRotation, secondRaw.PreRotation, linearTolerance) &&
                       VectorsApproximatelyEqual(firstRaw.PostRotation, secondRaw.PostRotation, linearTolerance) &&
                       VectorsApproximatelyEqual(firstRaw.RotationPivot, secondRaw.RotationPivot, translationTolerance) &&
                       VectorsApproximatelyEqual(firstRaw.ScalingPivot, secondRaw.ScalingPivot, translationTolerance) &&
                       VectorsApproximatelyEqual(firstRaw.RotationOffset, secondRaw.RotationOffset, translationTolerance) &&
                       VectorsApproximatelyEqual(firstRaw.ScalingOffset, secondRaw.ScalingOffset, translationTolerance);
            }

            return true;
        }

        private static bool VectorsApproximatelyEqual(Vector3d a, Vector3d b, double tolerance)
        {
            return Math.Abs(a.x - b.x) <= tolerance &&
                   Math.Abs(a.y - b.y) <= tolerance &&
                   Math.Abs(a.z - b.z) <= tolerance;
        }

        private static Dictionary<int, string> BuildBonePathsByIndex(IFbxSkinDeformer skin)
        {
            var map = new Dictionary<int, string>();
            if (skin?.Clusters == null) return map;
            for (int i = 0; i < skin.Clusters.Count; i++)
            {
                var cluster = skin.Clusters[i];
                string raw = cluster?.BoneNode?.Path ?? cluster?.BonePath;
                string norm = FbxNameUtility.NormalizePath(raw ?? string.Empty);
                if (!string.IsNullOrEmpty(norm))
                {
                    map[i] = norm;
                }
            }

            return map;
        }

        private static Dictionary<string, Dictionary<int, double>> BuildWeightsByPath(
            IFbxSkinDeformer skin,
            IReadOnlyDictionary<int, string> bonePathByIndex)
        {
            var result = new Dictionary<string, Dictionary<int, double>>(StringComparer.Ordinal);
            if (skin?.Clusters == null || bonePathByIndex == null) return result;

            var cpSums = new Dictionary<int, double>();
            for (int boneIndex = 0; boneIndex < skin.Clusters.Count; boneIndex++)
            {
                var cluster = skin.Clusters[boneIndex];
                if (cluster == null || !bonePathByIndex.TryGetValue(boneIndex, out string bonePath)) continue;

                var indices = cluster.GetControlPointIndices();
                var weights = cluster.GetWeights();
                int count = Math.Min(indices.Length, weights.Length);
                for (int i = 0; i < count; i++)
                {
                    int cp = indices[i];
                    double w = weights[i];
                    if (cp < 0 || w <= 0d || double.IsNaN(w) || double.IsInfinity(w)) continue;

                    if (!result.TryGetValue(bonePath, out var map))
                    {
                        map = new Dictionary<int, double>();
                        result[bonePath] = map;
                    }

                    map.TryGetValue(cp, out double existing);
                    map[cp] = existing + w;
                    cpSums.TryGetValue(cp, out double total);
                    cpSums[cp] = total + w;
                }
            }

            if (cpSums.Count > 0)
            {
                foreach (var map in result.Values)
                {
                    var keys = map.Keys.ToArray();
                    for (int i = 0; i < keys.Length; i++)
                    {
                        int cp = keys[i];
                        if (cpSums.TryGetValue(cp, out double total) && total > 0d)
                        {
                            double normalized = map[cp] / total;
                            map[cp] = normalized > 1d ? 1d : normalized;
                        }
                    }
                }
            }

            return result;
        }

        private static HashSet<string> BuildClusterBonePaths(
            IFbxSkinDeformer skin,
            IReadOnlyDictionary<int, string> bonePathByIndex)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (skin?.Clusters == null || bonePathByIndex == null) return set;
            for (int i = 0; i < skin.Clusters.Count; i++)
            {
                if (skin.Clusters[i] != null && bonePathByIndex.TryGetValue(i, out string path))
                {
                    set.Add(path);
                }
            }

            return set;
        }

        private static Dictionary<string, ClusterBindPair> BuildBindPosesByPath(
            IFbxSkinDeformer skin,
            IReadOnlyDictionary<int, string> bonePathByIndex,
            FbxAffineSpaceTransform meshLocalToOrigin,
            FbxAffineSpaceTransform worldToOrigin)
        {
            var result = new Dictionary<string, ClusterBindPair>(StringComparer.Ordinal);
            if (skin?.Clusters == null || bonePathByIndex == null) return result;

            for (int i = 0; i < skin.Clusters.Count; i++)
            {
                if (!bonePathByIndex.TryGetValue(i, out string path)) continue;
                var cluster = skin.Clusters[i];
                if (cluster == null) continue;

                // Separated L and W algebra matching SkinWeightFeatureExtractor:
                // TransformMatrix     = L^-1 * M_meshBind * W
                // TransformLinkMatrix = W^-1 * M_boneBind * W
                FbxMatrix4x4 meshBind = meshLocalToOrigin.IsIdentity
                    ? cluster.MeshBindWorld
                    : meshLocalToOrigin.InverseOffset * cluster.MeshBindWorld;
                if (!worldToOrigin.IsIdentity)
                {
                    meshBind = meshBind * worldToOrigin.Offset;
                }

                FbxMatrix4x4 boneBind = worldToOrigin.IsIdentity
                    ? cluster.BindToWorld
                    : worldToOrigin.InverseOffset * cluster.BindToWorld * worldToOrigin.Offset;

                result[path] = new ClusterBindPair(meshBind, boneBind);
            }

            return result;
        }

        private static bool MatricesApproximatelyEqual(
            FbxMatrix4x4 a,
            FbxMatrix4x4 b,
            double linearTolerance,
            double translationTolerance)
        {
            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++)
                {
                    double tol = (r == 3 && c < 3) ? translationTolerance : linearTolerance;
                    if (Math.Abs(a[r, c] - b[r, c]) > tol)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static string GetParentPath(string normalizedPath)
        {
            if (string.IsNullOrEmpty(normalizedPath)) return string.Empty;
            int slash = normalizedPath.LastIndexOf('/');
            return slash > 0 ? normalizedPath.Substring(0, slash) : string.Empty;
        }

        private static SkinWeightBoneComparisonBuilder GetBuilder(
            Dictionary<string, SkinWeightBoneComparisonBuilder> builders,
            string bonePath,
            HashSet<string> sourceBonePaths,
            HashSet<string> originBonePaths)
        {
            string normalized = FbxNameUtility.NormalizePath(bonePath);
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

        private readonly struct ClusterBindPair
        {
            public readonly FbxMatrix4x4 TransformMatrix;
            public readonly FbxMatrix4x4 TransformLinkMatrix;

            public ClusterBindPair(FbxMatrix4x4 transformMatrix, FbxMatrix4x4 transformLinkMatrix)
            {
                TransformMatrix = transformMatrix;
                TransformLinkMatrix = transformLinkMatrix;
            }
        }

        private sealed class SkinWeightBoneComparisonBuilder
        {
            public string BonePath;
            public bool SourceBoneExists;
            public bool OriginBoneExists;
            public SkinWeightBoneTransformComparison Transform;
            public readonly List<SkinWeightMeshBoneComparison> Meshes = new List<SkinWeightMeshBoneComparison>();

            public SkinWeightBoneComparison Build()
            {
                return new SkinWeightBoneComparison
                {
                    BonePath = BonePath,
                    SourceBoneExists = SourceBoneExists,
                    OriginBoneExists = OriginBoneExists,
                    Transform = Transform,
                    Meshes = Meshes.OrderBy(pair => pair.MeshPath, StringComparer.Ordinal).ToArray()
                };
            }
        }
    }
}
