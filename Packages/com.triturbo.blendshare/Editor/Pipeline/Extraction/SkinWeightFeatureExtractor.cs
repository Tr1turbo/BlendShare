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
    /// Four-track extraction options for <see cref="SkinWeightFeatureExtractor"/>:
    /// independent per-mesh weight bone paths, independent per-mesh bindpose bone paths,
    /// shared armature transform bone paths, and new bone creation paths (plus optional armature-only host mesh).
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
        public bool IncludeClusterBindMatrices { get; set; } = true;
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
    /// <c>DataSemantic.SignedWeightDelta</c>), independent binary64 bind matrices in stored Origin FBX space
    /// (<c>TransformMatrix = L^-1 * M_meshBind * W</c>, <c>TransformLinkMatrix = W^-1 * M_boneBind * W</c>),
    /// and shared <see cref="SkeletonDocument"/> bone registrations with explicit <c>CreateIfMissing</c> flags.
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
            var sourceBoneNodeByPath = new Dictionary<string, IFbxNode>(StringComparer.Ordinal);

            if (!TryReadNormalizedSkinWeights(
                    sourceSkin,
                    domainCount,
                    context.MeshId,
                    FeatureTypeId,
                    diagnostics,
                    sourceClusterByPath,
                    sourceBoneNodeByPath,
                    out var sourceWeightsByPath,
                    out var readError))
            {
                return readError;
            }

            if (!TryReadNormalizedSkinWeights(
                    originSkin,
                    domainCount,
                    context.MeshId,
                    FeatureTypeId,
                    diagnostics,
                    null,
                    null,
                    out var originWeightsByPath,
                    out readError))
            {
                return readError;
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

            // 2. Attach cluster bind matrices independently for selected bindpose bones.
            foreach (string bonePath in selectedBindposeBones)
            {
                if (!sourceClusterByPath.TryGetValue(bonePath, out var cluster) || cluster == null)
                {
                    continue;
                }

                // cluster.MeshBindWorld already holds L^-1 * M_meshBind_src from SourceMeshNormalizer.
                // Right-multiplying by W yields L^-1 * M_meshBind_src * W; bone bind conjugates strictly by W: W^-1 * M_boneBind_src * W.
                FbxMatrix4x4 storedMeshBindWorld = worldToStored.IsIdentity
                    ? cluster.MeshBindWorld
                    : cluster.MeshBindWorld * worldToStored.Offset;
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

            var clusterEntries = new List<SkinWeightClusterEntry>(clustersByPath.Values);
            if (clusterEntries.Count == 0 && !isArmatureOnlyHost)
            {
                return FeatureExtractionResult.Skipped(
                    "SW_EXT_NO_CLUSTERS",
                    "No matching skin weight deltas or bindpose clusters were extracted.",
                    context.MeshId,
                    FeatureTypeId);
            }

            // 3. Build shared skeleton bone registrations (unique per bone path across all meshes).
            var skeletonRegistrations = BuildSkeletonRegistrations(
                context,
                swOptions,
                clusterEntries,
                sourceBoneNodeByPath,
                out string skeletonError);
            if (!string.IsNullOrEmpty(skeletonError))
            {
                return FeatureExtractionResult.Failed(
                    "SW_EXT_MISSING_SOURCE_BONE",
                    skeletonError,
                    context.MeshId,
                    FeatureTypeId,
                    diagnostics);
            }

            if (skeletonRegistrations.Count > 0)
            {
                context.RegisterSkeletonBones(skeletonRegistrations, worldToStored);
            }

            string rootBonePath = swOptions.TryGetRootBonePath(context.NodePath, out var explicitRoot)
                ? explicitRoot
                : string.Empty;

            var feature = new SkinWeightFeature
            {
                FeatureId = DefaultFeatureId,
                Required = true,
                RootBonePath = rootBonePath,
                Clusters = clusterEntries
            };

            return FeatureExtractionResult.Success(feature, diagnostics);
        }

        private static bool TryReadNormalizedSkinWeights(
            IFbxSkinDeformer skin,
            int domainCount,
            string meshId,
            string featureTypeId,
            List<ExtractionDiagnostic> diagnostics,
            Dictionary<string, IFbxSkinCluster> clustersByPath,
            Dictionary<string, IFbxNode> boneNodesByPath,
            out Dictionary<string, Dictionary<int, double>> normalizedWeightsByPath,
            out FeatureExtractionResult failure)
        {
            normalizedWeightsByPath = new Dictionary<string, Dictionary<int, double>>(StringComparer.Ordinal);
            failure = null;
            if (skin?.Clusters == null || skin.Clusters.Count == 0)
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

                string bonePath = FbxNameUtility.NormalizePath(
                    cluster.BoneNode != null ? cluster.BoneNode.Path : cluster.BonePath);
                if (string.IsNullOrEmpty(bonePath))
                {
                    if (clustersByPath != null)
                    {
                        diagnostics.Add(new ExtractionDiagnostic(
                            ExtractionDiagnosticSeverity.Warning,
                            "SW_EXT_UNNAMED_CLUSTER",
                            $"Skipped skin cluster {c} with empty bone path.",
                            meshId,
                            featureTypeId));
                    }

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

                int weightCount = cluster.WeightCount;
                if (weightCount < 0)
                {
                    failure = FeatureExtractionResult.Failed(
                        "SW_EXT_INVALID_CLUSTER",
                        $"Skin cluster {c} reported negative weight count ({weightCount}).",
                        meshId,
                        featureTypeId,
                        diagnostics);
                    return false;
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
                    failure = FeatureExtractionResult.Failed(
                        "SW_EXT_CLUSTER_COPY_FAILED",
                        $"Failed to copy skin weights for cluster {c} ('{bonePath}').",
                        meshId,
                        featureTypeId,
                        diagnostics);
                    return false;
                }

                if (!normalizedWeightsByPath.TryGetValue(bonePath, out var map))
                {
                    map = new Dictionary<int, double>();
                    normalizedWeightsByPath[bonePath] = map;
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
                        failure = FeatureExtractionResult.Failed(
                            "SW_EXT_INVALID_WEIGHT",
                            $"Skin cluster '{bonePath}' control point {cp} has invalid weight {w}.",
                            meshId,
                            featureTypeId,
                            diagnostics);
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

            foreach (var map in normalizedWeightsByPath.Values)
            {
                var cps = new List<int>(map.Keys);
                for (int i = 0; i < cps.Count; i++)
                {
                    int cp = cps[i];
                    double sum = fullSkinSums[cp];
                    if (sum > 0d)
                    {
                        double normalized = map[cp] / sum;
                        map[cp] = normalized > 1d ? 1d : normalized;
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

        private static List<SkeletonBoneRegistration> BuildSkeletonRegistrations(
            MeshExtractionContext context,
            SkinWeightExtractionOptions swOptions,
            IReadOnlyList<SkinWeightClusterEntry> extractedClusters,
            IReadOnlyDictionary<string, IFbxNode> sourceBoneNodeByPath,
            out string error)
        {
            error = null;
            var createPaths = new HashSet<string>(StringComparer.Ordinal);
            var transformPaths = new HashSet<string>(StringComparer.Ordinal);

            bool hasExplicitArmatureSelection =
                swOptions.SelectedTransformBonePaths.Count > 0 ||
                swOptions.SelectedNewBonePaths.Count > 0;

            if (hasExplicitArmatureSelection)
            {
                for (int i = 0; i < swOptions.SelectedNewBonePaths.Count; i++)
                {
                    string norm = FbxNameUtility.NormalizePath(swOptions.SelectedNewBonePaths[i]);
                    if (!string.IsNullOrEmpty(norm))
                    {
                        createPaths.Add(norm);
                        transformPaths.Add(norm);
                    }
                }

                for (int i = 0; i < swOptions.SelectedTransformBonePaths.Count; i++)
                {
                    string norm = FbxNameUtility.NormalizePath(swOptions.SelectedTransformBonePaths[i]);
                    if (!string.IsNullOrEmpty(norm))
                    {
                        transformPaths.Add(norm);
                    }
                }
            }
            else
            {
                // Programmatic caller fallback: register all referenced cluster bones, marking bones absent
                // in OriginScene as CreateIfMissing = true (matching 007f7f0 automatic dependencies).
                for (int i = 0; i < extractedClusters.Count; i++)
                {
                    string norm = FbxNameUtility.NormalizePath(extractedClusters[i].BonePath);
                    if (string.IsNullOrEmpty(norm)) continue;

                    var sourceNode = ResolveSourceBoneNode(context.SourceScene, sourceBoneNodeByPath, norm);
                    if (sourceNode == null) continue;

                    transformPaths.Add(norm);
                    if (context.OriginScene?.FindNodeByPath(norm) == null)
                    {
                        createPaths.Add(norm);
                    }
                }
            }

            if (transformPaths.Count == 0 && createPaths.Count == 0)
            {
                return new List<SkeletonBoneRegistration>();
            }

            var registrationsByPath = new Dictionary<string, SkeletonBoneRegistration>(StringComparer.Ordinal);
            var orderedPaths = new List<string>(transformPaths);
            orderedPaths.Sort(StringComparer.Ordinal);

            for (int i = 0; i < orderedPaths.Count; i++)
            {
                string path = orderedPaths[i];
                if (!AddSkeletonBoneRecursive(
                        context,
                        sourceBoneNodeByPath,
                        registrationsByPath,
                        path,
                        createPaths.Contains(path),
                        out error))
                {
                    return null;
                }
            }

            return new List<SkeletonBoneRegistration>(registrationsByPath.Values);
        }

        private static bool AddSkeletonBoneRecursive(
            MeshExtractionContext context,
            IReadOnlyDictionary<string, IFbxNode> sourceBoneNodeByPath,
            Dictionary<string, SkeletonBoneRegistration> registrationsByPath,
            string bonePath,
            bool createIfMissing,
            out string error)
        {
            error = null;
            string normalized = FbxNameUtility.NormalizePath(bonePath);
            if (string.IsNullOrEmpty(normalized))
            {
                return true;
            }

            if (registrationsByPath.TryGetValue(normalized, out var existing))
            {
                if (createIfMissing && !existing.CreateIfMissing)
                {
                    registrationsByPath[normalized] = new SkeletonBoneRegistration(normalized, existing.SourceNode, true);
                }

                return true;
            }

            var sourceNode = ResolveSourceBoneNode(context.SourceScene, sourceBoneNodeByPath, normalized);
            if (sourceNode == null)
            {
                error = $"Bone '{normalized}' was not found in the source FBX.";
                return false;
            }

            if (createIfMissing)
            {
                string parentPath = sourceNode.Parent != null && !sourceNode.Parent.IsRoot
                    ? FbxNameUtility.NormalizePath(sourceNode.Parent.Path)
                    : GetParentPath(normalized);
                if (!string.IsNullOrEmpty(parentPath) && context.OriginScene?.FindNodeByPath(parentPath) == null)
                {
                    var parentSourceNode = sourceNode.Parent != null && !sourceNode.Parent.IsRoot
                        ? sourceNode.Parent
                        : ResolveSourceBoneNode(context.SourceScene, sourceBoneNodeByPath, parentPath);
                    if (parentSourceNode != null &&
                        !AddSkeletonBoneRecursive(
                            context,
                            sourceBoneNodeByPath,
                            registrationsByPath,
                            parentPath,
                            createIfMissing: true,
                            out error))
                    {
                        return false;
                    }
                }
            }

            registrationsByPath[normalized] = new SkeletonBoneRegistration(normalized, sourceNode, createIfMissing);
            return true;
        }

        private static IFbxNode ResolveSourceBoneNode(
            IFbxScene sourceScene,
            IReadOnlyDictionary<string, IFbxNode> sourceBoneNodeByPath,
            string normalizedPath)
        {
            var found = sourceScene?.FindNodeByPath(normalizedPath);
            if (found != null)
            {
                return found;
            }

            if (sourceBoneNodeByPath != null)
            {
                if (sourceBoneNodeByPath.TryGetValue(normalizedPath, out var direct) && direct != null)
                {
                    return direct;
                }

                // Walk parent chains of known cluster bone nodes (handles mock IFbxScene implementations without FindNodeByPath).
                foreach (var node in sourceBoneNodeByPath.Values)
                {
                    var curr = node?.Parent;
                    while (curr != null && !curr.IsRoot)
                    {
                        if (string.Equals(FbxNameUtility.NormalizePath(curr.Path), normalizedPath, StringComparison.Ordinal))
                        {
                            return curr;
                        }

                        curr = curr.Parent;
                    }
                }
            }

            return null;
        }

        private static string GetParentPath(string normalizedPath)
        {
            if (string.IsNullOrEmpty(normalizedPath)) return string.Empty;
            int slash = normalizedPath.LastIndexOf('/');
            return slash > 0 ? normalizedPath.Substring(0, slash) : string.Empty;
        }

        private static IFbxSkinDeformer FindPrimarySkinDeformer(IFbxMeshGeometry mesh)
        {
            if (mesh?.SkinDeformers == null) return null;
            for (int i = 0; i < mesh.SkinDeformers.Count; i++)
            {
                var skin = mesh.SkinDeformers[i];
                if (skin == null || skin.Clusters == null) continue;
                for (int c = 0; c < skin.Clusters.Count; c++)
                {
                    if (skin.Clusters[c] != null && skin.Clusters[c].WeightCount > 0)
                    {
                        return skin;
                    }
                }
            }

            return mesh.SkinDeformers.Count > 0 ? mesh.SkinDeformers[0] : null;
        }

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
