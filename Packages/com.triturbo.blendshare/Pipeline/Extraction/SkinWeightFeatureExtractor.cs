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
    /// Extraction options for <see cref="SkinWeightFeatureExtractor"/>.
    /// </summary>
    public sealed class SkinWeightExtractionOptions : IFeatureExtractionOptions
    {
        private readonly HashSet<string> m_DisabledMeshes = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> m_SelectedBonesByMesh =
            new Dictionary<string, List<string>>(StringComparer.Ordinal);

        public string FeatureTypeId => SkinWeightFeature.FeatureTypeId;
        public bool Enabled { get; set; } = false;
        public string ArmatureId { get; set; } = "armature-0";
        public bool ExtractAllClustersWhenSelectionEmpty { get; set; } = true;
        public bool IncludeClusterBindMatrices { get; set; } = true;
        public double ZeroWeightThreshold { get; set; } = 1e-6d;
        public List<string> SelectedBonePaths { get; } = new List<string>();

        public bool IsMeshEnabled(string nodePath) => !m_DisabledMeshes.Contains(NormalizeKey(nodePath));

        public void SetMeshEnabled(string nodePath, bool enabled)
        {
            string key = NormalizeKey(nodePath);
            if (enabled) m_DisabledMeshes.Remove(key);
            else m_DisabledMeshes.Add(key);
        }

        public bool HasSelectedBonePathsForMesh(string nodePath) =>
            m_SelectedBonesByMesh.ContainsKey(NormalizeKey(nodePath));

        public IReadOnlyList<string> GetSelectedBonePaths(string nodePath)
        {
            return m_SelectedBonesByMesh.TryGetValue(NormalizeKey(nodePath), out var list)
                ? list
                : SelectedBonePaths;
        }

        public void SetSelectedBonePaths(string nodePath, IEnumerable<string> bonePaths)
        {
            string key = NormalizeKey(nodePath);
            var cleaned = new List<string>();
            if (bonePaths != null)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (string bp in bonePaths)
                {
                    string norm = FbxNameUtility.NormalizePath(bp ?? string.Empty);
                    if (!string.IsNullOrEmpty(norm) && seen.Add(norm))
                    {
                        cleaned.Add(norm);
                    }
                }
            }

            m_SelectedBonesByMesh[key] = cleaned;
        }

        public bool ShouldExtractMesh(string normalizedNodePath)
        {
            if (!Enabled || !IsMeshEnabled(normalizedNodePath))
            {
                return false;
            }

            if (HasSelectedBonePathsForMesh(normalizedNodePath))
            {
                return m_SelectedBonesByMesh[NormalizeKey(normalizedNodePath)].Count > 0;
            }

            return ExtractAllClustersWhenSelectionEmpty || SelectedBonePaths.Count > 0;
        }

        private static string NormalizeKey(string nodePath) => FbxNameUtility.NormalizePath(nodePath ?? string.Empty);
    }

    /// <summary>
    /// Built-in <see cref="IFeatureExtractor"/> for <see cref="SkinWeightFeature"/>.
    /// Extracts normalized per-cluster sparse control-point weights (<c>DataSemantic.NormalizedWeight</c>)
    /// and binary64 bind matrices in the mesh's stored Origin FBX local space,
    /// keeping mesh-local alignment <c>L</c> strictly separated from scene/world basis transform <c>W</c>:
    /// <c>TransformMatrix = L^-1 * M_meshBind * W</c>, <c>TransformLinkMatrix = W^-1 * M_boneBind * W</c>.
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

            IFbxSkinDeformer primarySkin = FindPrimarySkinDeformer(sourceMesh);
            if (primarySkin == null)
            {
                return FeatureExtractionResult.Skipped(
                    "SW_EXT_NO_SKIN",
                    "Source FBX mesh contains no skin deformers with bone weights.",
                    context.MeshId,
                    FeatureTypeId);
            }

            bool filterBySelection = swOptions.HasSelectedBonePathsForMesh(context.NodePath) ||
                                     !swOptions.ExtractAllClustersWhenSelectionEmpty ||
                                     swOptions.SelectedBonePaths.Count > 0;
            HashSet<string> selectedBones = null;
            if (filterBySelection)
            {
                selectedBones = new HashSet<string>(swOptions.GetSelectedBonePaths(context.NodePath), StringComparer.Ordinal);
                if (selectedBones.Count == 0)
                {
                    return FeatureExtractionResult.Skipped("SW_EXT_NO_SELECTION", "No skin weight bones selected.", context.MeshId, FeatureTypeId);
                }
            }

            // Use scene/world basis transform W (Source world -> Stored world), strictly separated from per-mesh
            // alignment L (already left-multiplied into cluster.MeshBindWorld by SourceMeshNormalizer as L^-1 * M_meshBind).
            FbxAffineSpaceTransform worldToStored = context.SourceWorldToStoredSpace;
            if (!worldToStored.IsValid)
            {
                FbxMatrix4x4 fallbackMatrix = context.SpaceProvenance != null
                    ? CanonicalMeshSpace.BuildSpaceToSpace(
                        context.SourceScene.Metadata.Axes,
                        context.SourceScene.Metadata.UnitMeters,
                        context.SpaceProvenance.OriginAxes,
                        context.SpaceProvenance.OriginRawMetersPerUnit,
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
            var clusterEntries = new List<SkinWeightClusterEntry>();
            var rawBuffers = new List<IndexedBuffer<double>>();
            var seenBonePaths = new HashSet<string>(StringComparer.Ordinal);
            var referencedBoneNodes = new List<IFbxNode>();
            var clusterBindWorldByPath = new Dictionary<string, FbxMatrix4x4>(StringComparer.Ordinal);

            int[] idxScratch = Array.Empty<int>();
            double[] wScratch = Array.Empty<double>();
            double threshold = swOptions.ZeroWeightThreshold >= 0d ? swOptions.ZeroWeightThreshold : 0d;

            // Pass 1: accumulate per-control-point total weight denominator across ALL clusters in the source skin
            // BEFORE applying bone selection filter, so selecting a subset of bones preserves each bone's true weight
            // instead of inflating a 0.3 weight to 1.0!
            var fullSkinSums = new double[domainCount];
            var allClusterMaps = new SortedDictionary<int, double>[primarySkin.Clusters.Count];
            int totalNonEmptyClusters = 0;

            for (int c = 0; c < primarySkin.Clusters.Count; c++)
            {
                var cluster = primarySkin.Clusters[c];
                if (cluster == null) continue;

                int weightCount = cluster.WeightCount;
                if (weightCount < 0)
                {
                    return FeatureExtractionResult.Failed(
                        "SW_EXT_INVALID_CLUSTER",
                        $"Skin cluster {c} reported negative weight count ({weightCount}).",
                        context.MeshId,
                        FeatureTypeId,
                        diagnostics);
                }

                var weightByCp = new SortedDictionary<int, double>();
                if (weightCount > 0)
                {
                    totalNonEmptyClusters++;
                    if (idxScratch.Length < weightCount)
                    {
                        idxScratch = new int[weightCount];
                        wScratch = new double[weightCount];
                    }

                    if (!cluster.TryCopyControlPointIndices(idxScratch.AsSpan(0, weightCount), out int idxWritten) ||
                        !cluster.TryCopyWeights(wScratch.AsSpan(0, weightCount), out int wWritten) ||
                        idxWritten != weightCount || wWritten != weightCount)
                    {
                        return FeatureExtractionResult.Failed(
                            "SW_EXT_CLUSTER_COPY_FAILED",
                            $"Failed to copy skin weights for cluster {c} ('{cluster.BonePath}').",
                            context.MeshId,
                            FeatureTypeId,
                            diagnostics);
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
                            return FeatureExtractionResult.Failed(
                                "SW_EXT_INVALID_WEIGHT",
                                $"Skin cluster '{cluster.BonePath}' control point {cp} has invalid weight {w}.",
                                context.MeshId,
                                FeatureTypeId,
                                diagnostics);
                        }

                        if (w <= 0d)
                        {
                            continue;
                        }

                        weightByCp.TryGetValue(cp, out double existing);
                        weightByCp[cp] = existing + w;
                        fullSkinSums[cp] += w;
                    }
                }

                allClusterMaps[c] = weightByCp;
            }

            // Pass 2: emit selected clusters normalized against fullSkinSums.
            int skippedBySelection = 0;
            for (int c = 0; c < primarySkin.Clusters.Count; c++)
            {
                var cluster = primarySkin.Clusters[c];
                if (cluster == null) continue;

                string bonePath = FbxNameUtility.NormalizePath(cluster.BoneNode != null ? cluster.BoneNode.Path : cluster.BonePath);
                if (string.IsNullOrEmpty(bonePath))
                {
                    diagnostics.Add(new ExtractionDiagnostic(
                        ExtractionDiagnosticSeverity.Warning,
                        "SW_EXT_UNNAMED_CLUSTER",
                        $"Skipped skin cluster {c} with empty bone path.",
                        context.MeshId,
                        FeatureTypeId));
                    continue;
                }

                if (selectedBones != null && !selectedBones.Contains(bonePath))
                {
                    skippedBySelection++;
                    continue;
                }

                if (!seenBonePaths.Add(bonePath))
                {
                    diagnostics.Add(new ExtractionDiagnostic(
                        ExtractionDiagnosticSeverity.Warning,
                        "SW_EXT_DUP_BONE_CLUSTER",
                        $"Skipped duplicate skin cluster for bone '{bonePath}'.",
                        context.MeshId,
                        FeatureTypeId));
                    continue;
                }

                var weightByCp = allClusterMaps[c];
                var cpIndices = new List<int>(weightByCp.Count);
                var cpWeights = new List<double>(weightByCp.Count);
                foreach (var kv in weightByCp)
                {
                    double denom = fullSkinSums[kv.Key];
                    double normalized = denom > 0d ? kv.Value / denom : 0d;
                    if (normalized > 1d) normalized = 1d;
                    if (normalized <= threshold) continue;
                    cpIndices.Add(kv.Key);
                    cpWeights.Add(normalized);
                }

                // cluster.MeshBindWorld already holds L^-1 * M_meshBind_src from SourceMeshNormalizer.
                // Right-multiplying by W yields L^-1 * M_meshBind_src * W; bone bind conjugates strictly by W: W^-1 * M_boneBind_src * W.
                FbxMatrix4x4 storedMeshBindWorld = worldToStored.IsIdentity
                    ? cluster.MeshBindWorld
                    : cluster.MeshBindWorld * worldToStored.Offset;
                FbxMatrix4x4 storedBoneBindWorld = worldToStored.IsIdentity
                    ? cluster.BindToWorld
                    : worldToStored.InverseOffset * cluster.BindToWorld * worldToStored.Offset;

                if (cluster.BoneNode != null)
                {
                    referencedBoneNodes.Add(cluster.BoneNode);
                }

                clusterBindWorldByPath[bonePath] = storedBoneBindWorld;

                rawBuffers.Add(new IndexedBuffer<double>(
                    IndexDomain.ControlPoint,
                    domainCount,
                    cpIndices.ToArray(),
                    cpWeights.ToArray(),
                    takeOwnership: true));

                clusterEntries.Add(new SkinWeightClusterEntry
                {
                    BonePath = bonePath,
                    HasClusterMatrices = swOptions.IncludeClusterBindMatrices,
                    TransformMatrix = swOptions.IncludeClusterBindMatrices ? storedMeshBindWorld : FbxMatrix4x4.Identity,
                    TransformLinkMatrix = swOptions.IncludeClusterBindMatrices ? storedBoneBindWorld : FbxMatrix4x4.Identity
                });
            }

            if (clusterEntries.Count == 0)
            {
                return FeatureExtractionResult.Skipped(
                    "SW_EXT_NO_CLUSTERS",
                    "No matching skin weight clusters were extracted.",
                    context.MeshId,
                    FeatureTypeId);
            }

            // Only renormalize across emitted clusters when ALL clusters of the source skin are emitted;
            // partial bone selection preserves true fractional weights normalized against fullSkinSums.
            IndexedBuffer<double>[] finalBuffers = skippedBySelection == 0
                ? SkinWeightNormalization.RenormalizePerVertex(rawBuffers, domainCount, ZeroWeightSumPolicy.KeepUnweighted, threshold)
                : rawBuffers.ToArray();

            for (int c = 0; c < clusterEntries.Count; c++)
            {
                clusterEntries[c].Payload = LazyPayload<SkinWeightClusterPayload>.FromValue(new SkinWeightClusterPayload
                {
                    DeltaWeights = finalBuffers[c]
                });
            }

            string armatureId = string.IsNullOrWhiteSpace(swOptions.ArmatureId) ? "armature-0" : swOptions.ArmatureId.Trim();
            string rootBonePath = ResolveCommonRootBonePath(referencedBoneNodes, clusterEntries);
            context.RegisterArmatureBones(armatureId, rootBonePath, referencedBoneNodes, clusterBindWorldByPath, worldToStored);

            var feature = new SkinWeightFeature
            {
                FeatureId = DefaultFeatureId,
                Required = true,
                ArmatureId = armatureId,
                RootBonePath = rootBonePath,
                Clusters = clusterEntries
            };

            return FeatureExtractionResult.Success(feature, diagnostics);
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

            return null;
        }

        private static string ResolveCommonRootBonePath(
            IReadOnlyList<IFbxNode> boneNodes,
            IReadOnlyList<SkinWeightClusterEntry> clusters)
        {
            var paths = new List<string>();
            if (clusters != null)
            {
                for (int i = 0; i < clusters.Count; i++)
                {
                    if (!string.IsNullOrEmpty(clusters[i].BonePath))
                    {
                        paths.Add(FbxNameUtility.NormalizePath(clusters[i].BonePath));
                    }
                }
            }

            if (paths.Count == 0 && boneNodes != null)
            {
                for (int i = 0; i < boneNodes.Count; i++)
                {
                    string p = FbxNameUtility.NormalizePath(boneNodes[i]?.Path);
                    if (!string.IsNullOrEmpty(p)) paths.Add(p);
                }
            }

            return ComputeLowestCommonAncestorPath(paths);
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
