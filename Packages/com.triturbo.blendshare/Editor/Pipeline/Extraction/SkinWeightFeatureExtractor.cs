using System;
using System.Collections.Generic;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Model.Documents;
using Triturbo.BlendShare.Model.Features;
using Triturbo.BlendShare.Model.Serialization;
using Triturbo.BlendShare.Primitives;

namespace Triturbo.BlendShare.Pipeline.Extraction
{
    /// <summary>
    /// Four-track extraction options for <see cref="SkinWeightFeatureExtractor"/> and <see cref="SkeletonExtractor"/>:
    /// independent per-mesh weight bone paths, independent per-mesh bindpose bone paths,
    /// shared skeleton transform bone paths, and new bone creation paths (plus optional armature-only host mesh).
    /// </summary>
    public sealed class SkinWeightExtractionOptions : IFeatureExtractionOptions
    {
        private readonly HashSet<string> m_DisabledMeshes = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> m_SelectedWeightBonesByMesh =
            new Dictionary<string, List<string>>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> m_SelectedBindposeBonesByMesh =
            new Dictionary<string, List<string>>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> m_RootBonePathByMesh =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public string FeatureTypeId => SkinWeightFeature.FeatureTypeId;
        public bool Enabled { get; set; } = false;
        public bool ExtractAllClustersWhenSelectionEmpty { get; set; } = true;
        public bool IncludeClusterBindMatrices { get; set; } = false;
        public double ZeroWeightThreshold { get; set; } = 1e-6d;
        public List<string> SelectedBonePaths { get; } = new List<string>();
        public List<string> SelectedTransformBonePaths { get; } = new List<string>();
        public List<string> SelectedNewBonePaths { get; } = new List<string>();
        public string ArmatureOnlyHostMeshPath { get; set; } = string.Empty;

        public bool IsMeshEnabled(string nodePath) => !m_DisabledMeshes.Contains(NormalizeKey(nodePath));

        public void SetMeshEnabled(string nodePath, bool enabled)
        {
            string key = NormalizeKey(nodePath);
            if (enabled) m_DisabledMeshes.Remove(key);
            else m_DisabledMeshes.Add(key);
        }

        public bool HasSelectedWeightBonePathsForMesh(string nodePath) =>
            m_SelectedWeightBonesByMesh.ContainsKey(NormalizeKey(nodePath));

        public bool HasSelectedBindposeBonePathsForMesh(string nodePath) =>
            m_SelectedBindposeBonesByMesh.ContainsKey(NormalizeKey(nodePath));

        public bool HasSelectedBonePathsForMesh(string nodePath) =>
            HasSelectedWeightBonePathsForMesh(nodePath) || HasSelectedBindposeBonePathsForMesh(nodePath);

        public IReadOnlyList<string> GetSelectedWeightBonePaths(string nodePath)
        {
            return m_SelectedWeightBonesByMesh.TryGetValue(NormalizeKey(nodePath), out var list)
                ? list
                : SelectedBonePaths;
        }

        public IReadOnlyList<string> GetSelectedBindposeBonePaths(string nodePath)
        {
            if (m_SelectedBindposeBonesByMesh.TryGetValue(NormalizeKey(nodePath), out var list))
            {
                return list;
            }

            if (HasSelectedWeightBonePathsForMesh(nodePath))
            {
                return Array.Empty<string>();
            }

            return IncludeClusterBindMatrices ? SelectedBonePaths : Array.Empty<string>();
        }

        public IReadOnlyList<string> GetSelectedBonePaths(string nodePath)
        {
            var weights = GetSelectedWeightBonePaths(nodePath);
            var bindposes = GetSelectedBindposeBonePaths(nodePath);
            if (bindposes.Count == 0)
            {
                return weights;
            }

            if (weights.Count == 0)
            {
                return bindposes;
            }

            var combined = new List<string>(weights.Count + bindposes.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < weights.Count; i++)
            {
                if (seen.Add(weights[i])) combined.Add(weights[i]);
            }

            for (int i = 0; i < bindposes.Count; i++)
            {
                if (seen.Add(bindposes[i])) combined.Add(bindposes[i]);
            }

            return combined;
        }

        public void SetSelectedWeightBonePaths(string nodePath, IEnumerable<string> bonePaths)
        {
            m_SelectedWeightBonesByMesh[NormalizeKey(nodePath)] = CleanBonePaths(bonePaths);
        }

        public void SetSelectedBindposeBonePaths(string nodePath, IEnumerable<string> bonePaths)
        {
            m_SelectedBindposeBonesByMesh[NormalizeKey(nodePath)] = CleanBonePaths(bonePaths);
        }

        /// <summary>
        /// Convenience setter configuring both weight and (when <see cref="IncludeClusterBindMatrices"/> is true)
        /// bindpose bone selection for <paramref name="nodePath"/>.
        /// </summary>
        public void SetSelectedBonePaths(string nodePath, IEnumerable<string> bonePaths)
        {
            var cleaned = CleanBonePaths(bonePaths);
            string key = NormalizeKey(nodePath);
            m_SelectedWeightBonesByMesh[key] = cleaned;
            m_SelectedBindposeBonesByMesh[key] = IncludeClusterBindMatrices
                ? new List<string>(cleaned)
                : new List<string>();
        }

        public void SetSelectedTransformBonePaths(IEnumerable<string> bonePaths)
        {
            SelectedTransformBonePaths.Clear();
            SelectedTransformBonePaths.AddRange(CleanBonePaths(bonePaths));
        }

        public void SetSelectedNewBonePaths(IEnumerable<string> bonePaths)
        {
            SelectedNewBonePaths.Clear();
            SelectedNewBonePaths.AddRange(CleanBonePaths(bonePaths));
        }

        public void SetRootBonePath(string nodePath, string rootBonePath)
        {
            string key = NormalizeKey(nodePath);
            string normalizedRoot = NormalizeKey(rootBonePath);
            if (string.IsNullOrEmpty(normalizedRoot))
            {
                m_RootBonePathByMesh.Remove(key);
            }
            else
            {
                m_RootBonePathByMesh[key] = normalizedRoot;
            }
        }

        public bool TryGetRootBonePath(string nodePath, out string rootBonePath)
        {
            return m_RootBonePathByMesh.TryGetValue(NormalizeKey(nodePath), out rootBonePath);
        }

        public bool IsArmatureOnlyHostMesh(string normalizedNodePath)
        {
            string host = NormalizeKey(ArmatureOnlyHostMeshPath);
            return !string.IsNullOrEmpty(host) &&
                   string.Equals(host, NormalizeKey(normalizedNodePath), StringComparison.Ordinal) &&
                   (SelectedTransformBonePaths.Count > 0 || SelectedNewBonePaths.Count > 0);
        }

        public bool ShouldExtractMesh(string normalizedNodePath)
        {
            if (!Enabled)
            {
                return false;
            }

            if (IsArmatureOnlyHostMesh(normalizedNodePath))
            {
                return true;
            }

            if (!IsMeshEnabled(normalizedNodePath))
            {
                return false;
            }

            string key = NormalizeKey(normalizedNodePath);
            bool hasPerMeshWeights = m_SelectedWeightBonesByMesh.TryGetValue(key, out var weightList);
            bool hasPerMeshBindposes = m_SelectedBindposeBonesByMesh.TryGetValue(key, out var bindposeList);
            if (hasPerMeshWeights || hasPerMeshBindposes)
            {
                return (hasPerMeshWeights && weightList.Count > 0) ||
                       (hasPerMeshBindposes && bindposeList.Count > 0);
            }

            return ExtractAllClustersWhenSelectionEmpty || SelectedBonePaths.Count > 0;
        }

        private static List<string> CleanBonePaths(IEnumerable<string> bonePaths)
        {
            var cleaned = new List<string>();
            if (bonePaths == null)
            {
                return cleaned;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string bp in bonePaths)
            {
                string norm = NormalizeKey(bp);
                if (!string.IsNullOrEmpty(norm) && seen.Add(norm))
                {
                    cleaned.Add(norm);
                }
            }

            return cleaned;
        }

        private static string NormalizeKey(string nodePath) => FbxNameUtility.NormalizePath(nodePath ?? string.Empty);
    }

    /// <summary>
    /// Built-in <see cref="IFeatureExtractor"/> for <see cref="SkinWeightFeature"/>.
    /// Extracts signed per-cluster sparse control-point weight deltas (<c>sourceWeight - originWeight</c>,
    /// <c>DataSemantic.SignedWeightDelta</c>) and optional explicit bind matrices in stored Origin FBX space.
    /// Patch-wide <see cref="SkeletonDocument"/> extraction is handled by <see cref="SkeletonExtractor"/>.
    /// </summary>
    public sealed class SkinWeightFeatureExtractor : IFeatureExtractor
    {
        public const string DefaultFeatureId = "skin-weights";

        public string FeatureTypeId => SkinWeightFeature.FeatureTypeId;
        public int ExecutionOrder => 50;
        public Type OptionsType => typeof(SkinWeightExtractionOptions);

        public IFeatureExtractionOptions CreateDefaultOptions() => new SkinWeightExtractionOptions();

        public FeatureExtractionResult Extract(MeshExtractionContext context, IFeatureExtractionOptions options)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            var swOptions = options as SkinWeightExtractionOptions ?? new SkinWeightExtractionOptions();
            if (!swOptions.ShouldExtractMesh(context.NodePath))
            {
                return FeatureExtractionResult.Skipped(
                    "SW_EXT_DISABLED",
                    "SkinWeight extraction is disabled or has no selected bones for this mesh.",
                    context.MeshId,
                    FeatureTypeId);
            }

            var sourceMesh = context.NormalizedSourceMesh;
            var originMesh = context.NormalizedOriginMesh;
            if (sourceMesh == null || originMesh == null)
            {
                return FeatureExtractionResult.Failed(
                    "SW_EXT_MISSING_MESH",
                    "Both normalized source and origin meshes are required for skin weight extraction.",
                    context.MeshId,
                    FeatureTypeId);
            }

            int domainCount = originMesh.ControlPointCount;
            if (domainCount <= 0)
            {
                return FeatureExtractionResult.Skipped("SW_EXT_EMPTY_MESH", "Origin mesh has zero control points.", context.MeshId, FeatureTypeId);
            }

            bool isArmatureOnlyHost = swOptions.IsArmatureOnlyHostMesh(context.NodePath);
            IFbxSkinDeformer sourceSkin = FindPrimarySkinDeformer(sourceMesh);
            IFbxSkinDeformer originSkin = FindPrimarySkinDeformer(originMesh);
            if (sourceSkin == null && originSkin == null && !isArmatureOnlyHost)
            {
                return FeatureExtractionResult.Skipped(
                    "SW_EXT_NO_SKIN",
                    "Source and origin FBX meshes contain no skin deformers with bone weights.",
                    context.MeshId,
                    FeatureTypeId);
            }

            FbxAffineSpaceTransform worldToStored = context.SourceWorldToStoredSpace;
            if (!worldToStored.IsValid)
            {
                FbxMatrix4x4 fallbackMatrix = context.SpaceProvenance != null
                    ? CanonicalMeshSpace.BuildSpaceToSpace(
                        context.SourceScene.Metadata.Axes,
                        context.SourceScene.Metadata.UnitMeters,
                        context.SpaceProvenance.Axes,
                        context.SpaceProvenance.MetersPerUnit,
                        out _,
                        out _)
                    : CanonicalMeshSpace.BuildRawToCanonical(context.SourceScene.Metadata, out _);

                if (!FbxAffineSpaceTransform.TryCreate(fallbackMatrix, out worldToStored))
                {
                    return FeatureExtractionResult.Failed(
                        "SW_EXT_SINGULAR_SPACE",
                        "Source scene world-to-stored transform is singular.",
                        context.MeshId,
                        FeatureTypeId);
                }
            }

            var diagnostics = new List<ExtractionDiagnostic>();
            var sourceClusterByPath = new Dictionary<string, IFbxSkinCluster>(StringComparer.Ordinal);

            if (!TryReadEffectiveSkinWeights(
                    sourceSkin,
                    "Source",
                    domainCount,
                    context.MeshId,
                    FeatureTypeId,
                    diagnostics,
                    sourceClusterByPath,
                    null,
                    out var sourceWeightsByPath,
                    out var sourceLinkMode,
                    out var readError))
            {
                return readError;
            }

            if (!TryReadEffectiveSkinWeights(
                    originSkin,
                    "Origin",
                    domainCount,
                    context.MeshId,
                    FeatureTypeId,
                    diagnostics,
                    null,
                    null,
                    out var originWeightsByPath,
                    out var originLinkMode,
                    out readError))
            {
                return readError;
            }

            if (sourceLinkMode.HasValue &&
                originLinkMode.HasValue &&
                sourceLinkMode.Value != originLinkMode.Value)
            {
                return FeatureExtractionResult.Failed(
                    "SW_EXT_LINK_MODE_MISMATCH",
                    $"Origin skin link mode '{originLinkMode.Value}' does not match Source skin link mode '{sourceLinkMode.Value}' on mesh '{context.NodePath}'.",
                    context.MeshId,
                    FeatureTypeId,
                    diagnostics);
            }

            var selectedWeightBones = ResolveSelectedWeightBonePaths(
                swOptions,
                context.NodePath,
                sourceWeightsByPath,
                originWeightsByPath);
            var selectedBindposeBones = ResolveSelectedBindposeBonePaths(
                swOptions,
                context.NodePath,
                sourceClusterByPath);

            double threshold = swOptions.ZeroWeightThreshold >= 0d ? swOptions.ZeroWeightThreshold : 0d;
            var clustersByPath = new SortedDictionary<string, SkinWeightClusterEntry>(StringComparer.Ordinal);

            // 1. Compute signed weight deltas (sourceWeight - originWeight) for selected weight bones.
            foreach (string bonePath in selectedWeightBones)
            {
                sourceWeightsByPath.TryGetValue(bonePath, out var sourceByCp);
                originWeightsByPath.TryGetValue(bonePath, out var originByCp);

                var controlPoints = new SortedSet<int>();
                if (sourceByCp != null)
                {
                    foreach (int cp in sourceByCp.Keys) controlPoints.Add(cp);
                }

                if (originByCp != null)
                {
                    foreach (int cp in originByCp.Keys) controlPoints.Add(cp);
                }

                var cpIndices = new List<int>(controlPoints.Count);
                var cpDeltas = new List<double>(controlPoints.Count);
                foreach (int cp in controlPoints)
                {
                    double src = 0d;
                    double org = 0d;
                    sourceByCp?.TryGetValue(cp, out src);
                    originByCp?.TryGetValue(cp, out org);
                    double delta = src - org;
                    if (delta > 1d) delta = 1d;
                    if (delta < -1d) delta = -1d;
                    if (Math.Abs(delta) <= threshold)
                    {
                        continue;
                    }

                    cpIndices.Add(cp);
                    cpDeltas.Add(delta);
                }

                if (cpIndices.Count == 0)
                {
                    continue;
                }

                clustersByPath[bonePath] = new SkinWeightClusterEntry
                {
                    BonePath = bonePath,
                    HasClusterMatrices = false,
                    TransformMatrix = FbxMatrix4x4.Identity,
                    TransformLinkMatrix = FbxMatrix4x4.Identity,
                    Payload = LazyPayload<SkinWeightClusterPayload>.FromValue(new SkinWeightClusterPayload
                    {
                        DeltaWeights = new IndexedBuffer<double>(
                            IndexDomain.ControlPoint,
                            domainCount,
                            cpIndices.ToArray(),
                            cpDeltas.ToArray(),
                            takeOwnership: true)
                    })
                };
            }

            // 2. Attach cluster bind matrices independently ONLY for explicitly selected bindpose bones.
            if (selectedBindposeBones.Count > 0)
            {
                var rawSourceNode = context.SourceScene?.FindNodeByPath(context.NodePath) ?? context.RawSourceMesh?.PrimaryNode;
                FbxMatrix4x4 sourceGeomToNode = rawSourceNode != null ? rawSourceNode.GeometryToNodeMatrix : FbxMatrix4x4.Identity;
                bool hasSourceGeomPivot = !FbxAffineSpaceTransform.IsExactIdentity(sourceGeomToNode);
                Dictionary<string, IFbxSkinCluster> rawSourceClustersByPath = null;
                if (hasSourceGeomPivot)
                {
                    var rawSkin = FindPrimarySkinDeformer(context.RawSourceMesh);
                    if (rawSkin?.Clusters != null)
                    {
                        rawSourceClustersByPath = new Dictionary<string, IFbxSkinCluster>(StringComparer.Ordinal);
                        for (int c = 0; c < rawSkin.Clusters.Count; c++)
                        {
                            var rc = rawSkin.Clusters[c];
                            if (rc == null) continue;
                            string bp = FbxNameUtility.NormalizePath(rc.BoneNode != null ? rc.BoneNode.Path : rc.BonePath);
                            if (!string.IsNullOrEmpty(bp) && !rawSourceClustersByPath.ContainsKey(bp))
                            {
                                rawSourceClustersByPath[bp] = rc;
                            }
                        }
                    }
                }

                foreach (string bonePath in selectedBindposeBones)
                {
                    if (!sourceClusterByPath.TryGetValue(bonePath, out var cluster) || cluster == null)
                    {
                        continue;
                    }

                    FbxMatrix4x4 normalizedGeomBindWorld = cluster.MeshBindWorld;
                    if (hasSourceGeomPivot &&
                        rawSourceClustersByPath != null &&
                        rawSourceClustersByPath.TryGetValue(bonePath, out var rawCluster) &&
                        rawCluster != null)
                    {
                        FbxMatrix4x4 rawGeomBindWorld = sourceGeomToNode * rawCluster.MeshBindWorld;
                        normalizedGeomBindWorld = context.SourceMeshToStoredSpace.IsValid
                            ? context.SourceMeshToStoredSpace.TransformMeshBindWorld(rawGeomBindWorld)
                            : rawGeomBindWorld;
                    }

                    FbxMatrix4x4 storedMeshBindWorld = worldToStored.IsIdentity
                        ? normalizedGeomBindWorld
                        : normalizedGeomBindWorld * worldToStored.Offset;
                    FbxMatrix4x4 storedBoneBindWorld = worldToStored.IsIdentity
                        ? cluster.BindToWorld
                        : worldToStored.InverseOffset * cluster.BindToWorld * worldToStored.Offset;

                    if (!clustersByPath.TryGetValue(bonePath, out var entry))
                    {
                        entry = new SkinWeightClusterEntry
                        {
                            BonePath = bonePath,
                            Payload = LazyPayload<SkinWeightClusterPayload>.Empty
                        };
                        clustersByPath[bonePath] = entry;
                    }

                    entry.HasClusterMatrices = true;
                    entry.TransformMatrix = storedMeshBindWorld;
                    entry.TransformLinkMatrix = storedBoneBindWorld;
                }
            }

            var clusterEntries = new List<SkinWeightClusterEntry>(clustersByPath.Values);
            if (clusterEntries.Count == 0 && !isArmatureOnlyHost)
            {
                return FeatureExtractionResult.Skipped(
                    "SW_EXT_NO_CLUSTERS",
                    "No matching skin weight deltas or bindpose clusters were extracted.",
                    context.MeshId,
                    FeatureTypeId);
            }

            string rootBonePath = swOptions.TryGetRootBonePath(context.NodePath, out var explicitRoot)
                ? explicitRoot
                : string.Empty;
            var resolvedLinkMode = (sourceLinkMode ?? originLinkMode) == FbxClusterLinkMode.TotalOne
                ? SkinWeightLinkMode.TotalOne
                : SkinWeightLinkMode.Normalize;

            var feature = new SkinWeightFeature
            {
                FeatureId = DefaultFeatureId,
                Required = true,
                RootBonePath = rootBonePath,
                LinkMode = resolvedLinkMode,
                Clusters = clusterEntries
            };

            return FeatureExtractionResult.Success(feature, diagnostics);
        }

        private static bool TryReadEffectiveSkinWeights(
            IFbxSkinDeformer skin,
            string roleLabel,
            int domainCount,
            string meshId,
            string featureTypeId,
            List<ExtractionDiagnostic> diagnostics,
            Dictionary<string, IFbxSkinCluster> clustersByPath,
            Dictionary<string, IFbxNode> boneNodesByPath,
            out Dictionary<string, Dictionary<int, double>> effectiveWeightsByPath,
            out FbxClusterLinkMode? skinLinkMode,
            out FeatureExtractionResult failure)
        {
            failure = null;
            List<int> unnamedIndices = clustersByPath != null ? new List<int>() : null;
            bool ok = TryEvaluateEffectiveSkinWeights(
                skin,
                roleLabel,
                domainCount,
                clustersByPath,
                boneNodesByPath,
                unnamedIndices,
                out effectiveWeightsByPath,
                out skinLinkMode,
                out string errCode,
                out string errMessage);

            if (unnamedIndices != null && diagnostics != null)
            {
                for (int i = 0; i < unnamedIndices.Count; i++)
                {
                    diagnostics.Add(new ExtractionDiagnostic(
                        ExtractionDiagnosticSeverity.Warning,
                        "SW_EXT_UNNAMED_CLUSTER",
                        $"Skipped skin cluster {unnamedIndices[i]} with empty bone path.",
                        meshId,
                        featureTypeId));
                }
            }

            if (!ok)
            {
                failure = FeatureExtractionResult.Failed(errCode, errMessage, meshId, featureTypeId, diagnostics);
                return false;
            }

            return true;
        }

        internal static bool TryDetectSkinLinkMode(
            IFbxSkinDeformer skin,
            string roleLabel,
            out FbxClusterLinkMode? skinLinkMode,
            out string errorCode,
            out string errorMessage)
        {
            skinLinkMode = null;
            errorCode = null;
            errorMessage = null;
            if (skin?.Clusters == null || skin.Clusters.Count == 0)
            {
                return true;
            }

            for (int c = 0; c < skin.Clusters.Count; c++)
            {
                var cluster = skin.Clusters[c];
                if (cluster == null) continue;

                var mode = cluster.LinkMode;
                if (mode != FbxClusterLinkMode.Normalize && mode != FbxClusterLinkMode.TotalOne)
                {
                    errorCode = "SW_EXT_UNSUPPORTED_LINK_MODE";
                    errorMessage = $"{roleLabel} skin cluster '{cluster.Name}' uses unsupported FBX link mode '{mode}' (only Normalize and TotalOne are supported).";
                    return false;
                }

                if (!skinLinkMode.HasValue)
                {
                    skinLinkMode = mode;
                }
                else if (skinLinkMode.Value != mode)
                {
                    errorCode = "SW_EXT_MIXED_LINK_MODES_UNSUPPORTED";
                    errorMessage = $"{roleLabel} skin mixes cluster link modes ('{skinLinkMode.Value}' and '{mode}'), which cannot be safely extracted.";
                    return false;
                }

                if (cluster.WeightCount < 0)
                {
                    errorCode = "SW_EXT_INVALID_CLUSTER";
                    errorMessage = $"Skin cluster {c} reported negative weight count ({cluster.WeightCount}).";
                    return false;
                }
            }

            return true;
        }

        internal static bool TryEvaluateEffectiveSkinWeights(
            IFbxSkinDeformer skin,
            string roleLabel,
            int domainCount,
            Dictionary<string, IFbxSkinCluster> clustersByPath,
            Dictionary<string, IFbxNode> boneNodesByPath,
            List<int> unnamedClusterIndices,
            out Dictionary<string, Dictionary<int, double>> effectiveWeightsByPath,
            out FbxClusterLinkMode? skinLinkMode,
            out string errorCode,
            out string errorMessage)
        {
            effectiveWeightsByPath = new Dictionary<string, Dictionary<int, double>>(StringComparer.Ordinal);
            skinLinkMode = null;
            errorCode = null;
            errorMessage = null;
            if (skin?.Clusters == null || skin.Clusters.Count == 0 || domainCount <= 0)
            {
                return true;
            }

            int[] idxScratch = Array.Empty<int>();
            double[] wScratch = Array.Empty<double>();
            var fullSkinSums = new double[domainCount];

            for (int c = 0; c < skin.Clusters.Count; c++)
            {
                var cluster = skin.Clusters[c];
                if (cluster == null) continue;

                var mode = cluster.LinkMode;
                if (mode != FbxClusterLinkMode.Normalize && mode != FbxClusterLinkMode.TotalOne)
                {
                    errorCode = "SW_EXT_UNSUPPORTED_LINK_MODE";
                    errorMessage = $"{roleLabel} skin cluster '{cluster.Name}' uses unsupported FBX link mode '{mode}' (only Normalize and TotalOne are supported).";
                    return false;
                }

                if (!skinLinkMode.HasValue)
                {
                    skinLinkMode = mode;
                }
                else if (skinLinkMode.Value != mode)
                {
                    errorCode = "SW_EXT_MIXED_LINK_MODES_UNSUPPORTED";
                    errorMessage = $"{roleLabel} skin mixes cluster link modes ('{skinLinkMode.Value}' and '{mode}'), which cannot be safely extracted.";
                    return false;
                }

                int weightCount = cluster.WeightCount;
                if (weightCount < 0)
                {
                    errorCode = "SW_EXT_INVALID_CLUSTER";
                    errorMessage = $"Skin cluster {c} reported negative weight count ({weightCount}).";
                    return false;
                }

                string bonePath = FbxNameUtility.NormalizePath(
                    cluster.BoneNode != null ? cluster.BoneNode.Path : cluster.BonePath);
                if (string.IsNullOrEmpty(bonePath))
                {
                    unnamedClusterIndices?.Add(c);
                    continue;
                }

                if (clustersByPath != null && !clustersByPath.ContainsKey(bonePath))
                {
                    clustersByPath[bonePath] = cluster;
                }

                if (boneNodesByPath != null && cluster.BoneNode != null && !boneNodesByPath.ContainsKey(bonePath))
                {
                    boneNodesByPath[bonePath] = cluster.BoneNode;
                }

                if (weightCount == 0)
                {
                    continue;
                }

                if (idxScratch.Length < weightCount)
                {
                    idxScratch = new int[weightCount];
                    wScratch = new double[weightCount];
                }

                if (!cluster.TryCopyControlPointIndices(idxScratch.AsSpan(0, weightCount), out int idxWritten) ||
                    !cluster.TryCopyWeights(wScratch.AsSpan(0, weightCount), out int wWritten) ||
                    idxWritten != weightCount || wWritten != weightCount)
                {
                    errorCode = "SW_EXT_CLUSTER_COPY_FAILED";
                    errorMessage = $"Failed to copy skin weights for cluster {c} ('{bonePath}').";
                    return false;
                }

                if (!effectiveWeightsByPath.TryGetValue(bonePath, out var map))
                {
                    map = new Dictionary<int, double>();
                    effectiveWeightsByPath[bonePath] = map;
                }

                for (int i = 0; i < weightCount; i++)
                {
                    int cp = idxScratch[i];
                    if ((uint)cp >= (uint)domainCount)
                    {
                        continue;
                    }

                    double w = wScratch[i];
                    if (double.IsNaN(w) || double.IsInfinity(w) || w < 0d)
                    {
                        errorCode = "SW_EXT_INVALID_WEIGHT";
                        errorMessage = $"Skin cluster '{bonePath}' control point {cp} has invalid weight {w}.";
                        return false;
                    }

                    if (w <= 0d)
                    {
                        continue;
                    }

                    map.TryGetValue(cp, out double existing);
                    map[cp] = existing + w;
                    fullSkinSums[cp] += w;
                }
            }

            bool preservePartialSums = skinLinkMode == FbxClusterLinkMode.TotalOne;
            foreach (var map in effectiveWeightsByPath.Values)
            {
                var cps = new List<int>(map.Keys);
                for (int i = 0; i < cps.Count; i++)
                {
                    int cp = cps[i];
                    double sum = fullSkinSums[cp];
                    if (sum > 0d)
                    {
                        if (!preservePartialSums || sum > 1d)
                        {
                            double normalized = map[cp] / sum;
                            map[cp] = normalized > 1d ? 1d : normalized;
                        }
                    }
                    else
                    {
                        map.Remove(cp);
                    }
                }
            }

            return true;
        }

        private static SortedSet<string> ResolveSelectedWeightBonePaths(
            SkinWeightExtractionOptions swOptions,
            string nodePath,
            Dictionary<string, Dictionary<int, double>> sourceWeightsByPath,
            Dictionary<string, Dictionary<int, double>> originWeightsByPath)
        {
            var result = new SortedSet<string>(StringComparer.Ordinal);
            if (!swOptions.IsMeshEnabled(nodePath))
            {
                return result;
            }

            if (swOptions.HasSelectedWeightBonePathsForMesh(nodePath))
            {
                var selected = swOptions.GetSelectedWeightBonePaths(nodePath);
                for (int i = 0; i < selected.Count; i++) result.Add(selected[i]);
                return result;
            }

            if (swOptions.HasSelectedBindposeBonePathsForMesh(nodePath))
            {
                return result;
            }

            if (swOptions.SelectedBonePaths.Count > 0)
            {
                for (int i = 0; i < swOptions.SelectedBonePaths.Count; i++)
                {
                    string norm = FbxNameUtility.NormalizePath(swOptions.SelectedBonePaths[i]);
                    if (!string.IsNullOrEmpty(norm)) result.Add(norm);
                }

                return result;
            }

            if (swOptions.ExtractAllClustersWhenSelectionEmpty)
            {
                foreach (string path in sourceWeightsByPath.Keys) result.Add(path);
                foreach (string path in originWeightsByPath.Keys) result.Add(path);
            }

            return result;
        }

        private static SortedSet<string> ResolveSelectedBindposeBonePaths(
            SkinWeightExtractionOptions swOptions,
            string nodePath,
            Dictionary<string, IFbxSkinCluster> sourceClusterByPath)
        {
            var result = new SortedSet<string>(StringComparer.Ordinal);
            if (!swOptions.IsMeshEnabled(nodePath))
            {
                return result;
            }

            if (swOptions.HasSelectedBindposeBonePathsForMesh(nodePath))
            {
                var selected = swOptions.GetSelectedBindposeBonePaths(nodePath);
                for (int i = 0; i < selected.Count; i++) result.Add(selected[i]);
                return result;
            }

            if (!swOptions.IncludeClusterBindMatrices || swOptions.HasSelectedWeightBonePathsForMesh(nodePath))
            {
                return result;
            }

            if (swOptions.SelectedBonePaths.Count > 0)
            {
                for (int i = 0; i < swOptions.SelectedBonePaths.Count; i++)
                {
                    string norm = FbxNameUtility.NormalizePath(swOptions.SelectedBonePaths[i]);
                    if (!string.IsNullOrEmpty(norm)) result.Add(norm);
                }

                return result;
            }

            if (swOptions.ExtractAllClustersWhenSelectionEmpty)
            {
                foreach (string path in sourceClusterByPath.Keys) result.Add(path);
            }

            return result;
        }

        internal static IFbxSkinDeformer FindPrimarySkinDeformer(IFbxMeshGeometry mesh) =>
            FbxDeformerUtility.FindPrimarySkinDeformer(mesh);

        internal static string ComputeLowestCommonAncestorPath(IReadOnlyList<string> normalizedPaths)
        {
            if (normalizedPaths == null || normalizedPaths.Count == 0)
            {
                return string.Empty;
            }

            string[] prefix = normalizedPaths[0].Split('/');
            int prefixLen = prefix.Length;

            for (int i = 1; i < normalizedPaths.Count && prefixLen > 0; i++)
            {
                string[] parts = normalizedPaths[i].Split('/');
                int max = Math.Min(prefixLen, parts.Length);
                int match = 0;
                while (match < max && string.Equals(prefix[match], parts[match], StringComparison.Ordinal))
                {
                    match++;
                }

                prefixLen = match;
            }

            return prefixLen > 0 ? string.Join("/", prefix, 0, prefixLen) : string.Empty;
        }
    }
}
