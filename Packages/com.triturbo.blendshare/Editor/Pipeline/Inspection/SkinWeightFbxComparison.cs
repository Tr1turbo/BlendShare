using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Pipeline.Extraction;

namespace Triturbo.BlendShare.Pipeline.Inspection
{
    public enum SkinWeightComparisonStatus
    {
        New = 0,
        Changed = 1,
        Same = 2,
        MissingSource = 3,
        Unavailable = 4,
        BasisOnly = 5
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
        public bool IsEndBone { get; set; }
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
    /// Compares Source vs Original FBX skin weights, cluster bind matrices, and bindpose-reconstructed bone rest transforms
    /// in Origin FBX native local space using separated <c>L</c> (mesh-local alignment) and <c>W</c> (scene/world basis conversion)
    /// algebra with unit-scaled physical-meter tolerances.
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
        public SkeletonComparison Skeleton { get; }

        private SkinWeightFbxComparison(
            SkeletonComparison skeletonComparison,
            IEnumerable<SkinWeightBoneComparison> bones,
            IEnumerable<SkinWeightMeshBoneComparison> pairs,
            IEnumerable<string> diagnostics)
        {
            Skeleton = skeletonComparison ?? SkeletonComparison.Empty;
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
                        (!bone.IsEndBone && bone.Transform?.Status == SkinWeightComparisonStatus.Changed))
                    .Select(bone => bone.BonePath),
                StringComparer.Ordinal);

            foreach (string path in relevantBonePaths.ToArray())
            {
                string parent = FbxNameUtility.GetParentPath(path);
                while (!string.IsNullOrEmpty(parent))
                {
                    if (m_BonesByPath.TryGetValue(parent, out var parentBone) && parentBone.RequiresCreateBone)
                    {
                        relevantBonePaths.Add(parent);
                    }

                    parent = FbxNameUtility.GetParentPath(parent);
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
                SkeletonComparison.Empty,
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

            var skeletonComparison = session.GetSkeletonComparison(meshPaths);
            var builders = new Dictionary<string, SkinWeightBoneComparisonBuilder>(StringComparer.Ordinal);
            var pairs = new List<SkinWeightMeshBoneComparison>();
            var diagnostics = new List<string>(session.Diagnostics ?? Array.Empty<string>());

            for (int i = 0; i < skeletonComparison.Entries.Count; i++)
            {
                var skelEntry = skeletonComparison.Entries[i];
                if (!skelEntry.IsBone &&
                    !skelEntry.RequiresCreate &&
                    skelEntry.Status != SkeletonNodeComparisonStatus.Changed)
                {
                    continue;
                }

                var builder = GetBuilder(
                    builders,
                    skelEntry.Path,
                    skelEntry.SourceExists,
                    skelEntry.OriginExists,
                    skelEntry.IsEndNode);
                builder.Transform = new SkinWeightBoneTransformComparison
                {
                    BonePath = skelEntry.Path,
                    Status = ToSkinWeightStatus(skelEntry.Status)
                };
            }

            for (int i = 0; i < meshPaths.Count; i++)
            {
                string meshPath = meshPaths[i];
                if (string.IsNullOrWhiteSpace(meshPath)) continue;
                var rawOffset = sourceOffsetResolver?.Invoke(meshPath)?.ToFbxMatrix() ?? FbxMatrix4x4.Identity;
                CompareMesh(
                    session,
                    skeletonComparison,
                    meshPath,
                    rawOffset,
                    worldToOrigin,
                    translationTol,
                    builders,
                    pairs,
                    diagnostics);
            }

            var bones = builders.Values
                .Select(builder => builder.Build())
                .Where(bone => bone.HasMeshData || bone.Transform != null || bone.RequiresCreateBone)
                .ToArray();

            return new SkinWeightFbxComparison(skeletonComparison, bones, pairs, diagnostics);
        }

        private static SkinWeightComparisonStatus ToSkinWeightStatus(SkeletonNodeComparisonStatus status)
        {
            return status switch
            {
                SkeletonNodeComparisonStatus.Same => SkinWeightComparisonStatus.Same,
                SkeletonNodeComparisonStatus.Changed => SkinWeightComparisonStatus.Changed,
                SkeletonNodeComparisonStatus.BasisOnly => SkinWeightComparisonStatus.BasisOnly,
                SkeletonNodeComparisonStatus.New => SkinWeightComparisonStatus.New,
                SkeletonNodeComparisonStatus.MissingSource => SkinWeightComparisonStatus.MissingSource,
                _ => SkinWeightComparisonStatus.Unavailable
            };
        }

        private static void CompareMesh(
            FbxInspectionSession session,
            SkeletonComparison skeletonComparison,
            string meshPath,
            FbxMatrix4x4 sourceToOriginRawOffset,
            FbxAffineSpaceTransform worldToOrigin,
            double translationTolerance,
            Dictionary<string, SkinWeightBoneComparisonBuilder> builders,
            List<SkinWeightMeshBoneComparison> pairs,
            List<string> diagnostics)
        {
            string normalizedMeshPath = FbxNameUtility.NormalizePath(meshPath);
            var sourceSkin = session.GetSourceSkin(normalizedMeshPath);
            var originSkin = session.GetOriginSkin(normalizedMeshPath);
            var sourceBonesByIndex = BuildBonePathsByIndex(sourceSkin);
            var originBonesByIndex = BuildBonePathsByIndex(originSkin);

            int sourceCpCount = session.Source?.GetMesh(normalizedMeshPath)?.ControlPointCount ?? 0;
            int originCpCount = session.Origin?.GetMesh(normalizedMeshPath)?.ControlPointCount ?? 0;
            int controlPointCount = originCpCount > 0 ? originCpCount : sourceCpCount;
            int effectiveDomainCount = Math.Max(sourceCpCount, originCpCount);

            bool sourceWeightsOk = SkinWeightFeatureExtractor.TryEvaluateEffectiveSkinWeights(
                sourceSkin,
                "Source",
                effectiveDomainCount,
                null,
                null,
                null,
                out var sourceWeights,
                out var sourceLinkMode,
                out _,
                out string sourceWeightErr);
            bool originWeightsOk = SkinWeightFeatureExtractor.TryEvaluateEffectiveSkinWeights(
                originSkin,
                "Origin",
                effectiveDomainCount,
                null,
                null,
                null,
                out var originWeights,
                out var originLinkMode,
                out _,
                out string originWeightErr);

            bool linkModeMismatch = sourceWeightsOk &&
                                    originWeightsOk &&
                                    sourceLinkMode.HasValue &&
                                    originLinkMode.HasValue &&
                                    sourceLinkMode.Value != originLinkMode.Value;
            if (!sourceWeightsOk && !string.IsNullOrEmpty(sourceWeightErr))
            {
                diagnostics?.Add($"[{normalizedMeshPath}] {sourceWeightErr}");
            }

            if (!originWeightsOk && !string.IsNullOrEmpty(originWeightErr))
            {
                diagnostics?.Add($"[{normalizedMeshPath}] {originWeightErr}");
            }

            if (linkModeMismatch)
            {
                diagnostics?.Add(
                    $"[{normalizedMeshPath}] Origin skin link mode '{originLinkMode.Value}' does not match Source skin link mode '{sourceLinkMode.Value}'.");
            }

            bool weightsAvailable = sourceWeightsOk && originWeightsOk && !linkModeMismatch;

            bool hasValidL = session.TryBuildSourceMeshToOriginTransform(sourceToOriginRawOffset, out var meshLocalToOrigin);
            var sourceBindPoses = BuildBindPosesByPath(sourceSkin, sourceBonesByIndex, hasValidL ? meshLocalToOrigin : FbxAffineSpaceTransform.Identity, worldToOrigin);
            var originBindPoses = BuildBindPosesByPath(originSkin, originBonesByIndex, FbxAffineSpaceTransform.Identity, FbxAffineSpaceTransform.Identity);

            var sourceClusterPaths = BuildClusterBonePaths(sourceSkin, sourceBonesByIndex);
            var originClusterPaths = BuildClusterBonePaths(originSkin, originBonesByIndex);

            var clusterBonePaths = sourceClusterPaths.Concat(originClusterPaths)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            for (int i = 0; i < clusterBonePaths.Length; i++)
            {
                string bonePath = clusterBonePaths[i];
                skeletonComparison.TryGetEntry(bonePath, out var skelEntry);
                bool sourceBoneExists = skelEntry?.SourceExists ?? session.Source?.GetNode(bonePath) != null;
                bool originBoneExists = skelEntry?.OriginExists ?? session.Origin?.GetNode(bonePath) != null;
                bool isEndNode = skelEntry?.IsEndNode ?? false;

                var builder = GetBuilder(builders, bonePath, sourceBoneExists, originBoneExists, isEndNode);
                bool hasSourceCluster = sourceClusterPaths.Contains(bonePath);
                bool hasOriginCluster = originClusterPaths.Contains(bonePath);
                var weightComparison = weightsAvailable
                    ? CompareWeights(
                        bonePath,
                        hasSourceCluster,
                        hasOriginCluster,
                        sourceWeights,
                        originWeights,
                        controlPointCount)
                    : new SkinWeightMeshClusterComparison
                    {
                        Status = SkinWeightComparisonStatus.Unavailable,
                        SourceExists = hasSourceCluster,
                        OriginExists = hasOriginCluster,
                        AffectedControlPointCount = 0,
                        MaxDelta = 0d
                    };

                SkinWeightBindposeComparison bindposeComparison = null;
                if (sourceBindPoses.ContainsKey(bonePath) || originBindPoses.ContainsKey(bonePath))
                {
                    bindposeComparison = CompareBindpose(
                        weightComparison.Status,
                        sourceBindPoses,
                        originBindPoses,
                        skelEntry,
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
            SkeletonNodeComparisonEntry skelEntry,
            string bonePath,
            double translationTolerance)
        {
            bool hasSource = sourceBindPoses.TryGetValue(bonePath, out var source);
            bool hasOrigin = originBindPoses.TryGetValue(bonePath, out var origin);
            bool changed = false;
            if (hasSource && hasOrigin)
            {
                FbxMatrix4x4 calibratedSourceLink = source.TransformLinkMatrix;
                if (skelEntry != null &&
                    skelEntry.Status == SkeletonNodeComparisonStatus.BasisOnly &&
                    FbxAffineSpaceTransform.TryCreate(skelEntry.BasisRotation, out var basisXform))
                {
                    // Calibrate out pure DCC bone roll Q so untouched rolled bones compare Same when their skin bind matches rest.
                    calibratedSourceLink = basisXform.InverseOffset * source.TransformLinkMatrix;
                }

                changed = !MatricesApproximatelyEqual(source.TransformMatrix, origin.TransformMatrix, k_LinearMatrixTolerance, translationTolerance) ||
                          !MatricesApproximatelyEqual(calibratedSourceLink, origin.TransformLinkMatrix, k_LinearMatrixTolerance, translationTolerance);
            }

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

        private static SkinWeightBoneComparisonBuilder GetBuilder(
            Dictionary<string, SkinWeightBoneComparisonBuilder> builders,
            string bonePath,
            bool sourceBoneExists,
            bool originBoneExists,
            bool isEndNode)
        {
            string normalized = FbxNameUtility.NormalizePath(bonePath);
            if (!builders.TryGetValue(normalized, out var builder))
            {
                builder = new SkinWeightBoneComparisonBuilder
                {
                    BonePath = normalized,
                    SourceBoneExists = sourceBoneExists,
                    OriginBoneExists = originBoneExists,
                    IsEndBone = isEndNode
                };
                builders[normalized] = builder;
            }
            else
            {
                builder.SourceBoneExists |= sourceBoneExists;
                builder.OriginBoneExists |= originBoneExists;
                builder.IsEndBone |= isEndNode;
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
            public bool IsEndBone;
            public SkinWeightBoneTransformComparison Transform;
            public readonly List<SkinWeightMeshBoneComparison> Meshes = new List<SkinWeightMeshBoneComparison>();

            public SkinWeightBoneComparison Build()
            {
                return new SkinWeightBoneComparison
                {
                    BonePath = BonePath,
                    SourceBoneExists = SourceBoneExists,
                    OriginBoneExists = OriginBoneExists,
                    IsEndBone = IsEndBone,
                    Transform = Transform,
                    Meshes = Meshes.OrderBy(pair => pair.MeshPath, StringComparer.Ordinal).ToArray()
                };
            }
        }
    }
}
