using System;
using System.Collections.Generic;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Model.Documents;
using Triturbo.BlendShare.Model.Serialization;
using Triturbo.BlendShare.Primitives;

namespace Triturbo.BlendShare.Model.Features
{
    [BlendShareFeature(SkinWeightFeature.FeatureTypeId, SchemaVersion = 1, DefaultRequired = true)]
    public sealed class SkinWeightFeature : BlendShareFeatureData
    {
        public const string FeatureTypeId = "blendshare.skin-weights";

        [BlendShareField(16)] public string RootBonePath = string.Empty;
        [BlendShareField(17)] public List<SkinWeightClusterEntry> Clusters = new List<SkinWeightClusterEntry>();

        public SkinWeightFeature()
        {
            TypeId = FeatureTypeId;
            SchemaVersion = 1;
            Required = true;
        }

        public List<string> GetWeightedBonePaths()
        {
            var result = new List<string>();
            if (Clusters == null)
            {
                return result;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < Clusters.Count; i++)
            {
                var cluster = Clusters[i];
                if (cluster == null || cluster.Payload == null || !cluster.Payload.HasPayload)
                {
                    continue;
                }

                string normalized = SkeletonDocument.NormalizeBonePath(cluster.BonePath);
                if (!string.IsNullOrEmpty(normalized) && seen.Add(normalized))
                {
                    result.Add(normalized);
                }
            }

            return result;
        }

        /// <summary>
        /// Returns all bone paths referenced by weighted or bindpose clusters in skeleton parent-before-child order,
        /// followed by any cluster bone paths not listed in the skeleton document.
        /// </summary>
        public List<string> GetNeededBonePathsInSkeletonOrder(SkeletonDocument skeleton)
        {
            var needed = new HashSet<string>(StringComparer.Ordinal);
            if (Clusters != null)
            {
                for (int i = 0; i < Clusters.Count; i++)
                {
                    var cluster = Clusters[i];
                    if (cluster == null)
                    {
                        continue;
                    }

                    bool hasWeights = cluster.Payload != null && cluster.Payload.HasPayload;
                    if (!hasWeights && !cluster.HasClusterMatrices)
                    {
                        continue;
                    }

                    string normalized = SkeletonDocument.NormalizeBonePath(cluster.BonePath);
                    if (!string.IsNullOrEmpty(normalized))
                    {
                        needed.Add(normalized);
                    }
                }
            }

            var ordered = new List<string>(needed.Count);
            if (skeleton != null)
            {
                var skeletonPaths = skeleton.GetBonePathsInHierarchyOrder();
                for (int i = 0; i < skeletonPaths.Count; i++)
                {
                    if (needed.Remove(skeletonPaths[i]))
                    {
                        ordered.Add(skeletonPaths[i]);
                    }
                }
            }

            if (Clusters != null && needed.Count > 0)
            {
                for (int i = 0; i < Clusters.Count; i++)
                {
                    var cluster = Clusters[i];
                    if (cluster == null)
                    {
                        continue;
                    }

                    string normalized = SkeletonDocument.NormalizeBonePath(cluster.BonePath);
                    if (!string.IsNullOrEmpty(normalized) && needed.Remove(normalized))
                    {
                        ordered.Add(normalized);
                    }
                }
            }

            return ordered;
        }

        public bool TryGetCluster(string bonePath, out SkinWeightClusterEntry cluster)
        {
            cluster = null;
            string normalized = SkeletonDocument.NormalizeBonePath(bonePath);
            if (string.IsNullOrEmpty(normalized) || Clusters == null)
            {
                return false;
            }

            for (int i = 0; i < Clusters.Count; i++)
            {
                var candidate = Clusters[i];
                if (candidate != null &&
                    string.Equals(SkeletonDocument.NormalizeBonePath(candidate.BonePath), normalized, StringComparison.Ordinal))
                {
                    cluster = candidate;
                    return true;
                }
            }

            return false;
        }

        public bool TryGetBindPoseFbxClusterMatrices(
            string bonePath,
            out FbxMatrix4x4 transformMatrix,
            out FbxMatrix4x4 transformLinkMatrix)
        {
            if (TryGetCluster(bonePath, out var cluster) && cluster.HasClusterMatrices)
            {
                transformMatrix = cluster.TransformMatrix;
                transformLinkMatrix = cluster.TransformLinkMatrix;
                return true;
            }

            transformMatrix = FbxMatrix4x4.Identity;
            transformLinkMatrix = FbxMatrix4x4.Identity;
            return false;
        }
    }

    [BlendShareRecord(SchemaVersion = 1)]
    public sealed class SkinWeightClusterEntry
    {
        [BlendShareField(1)] public string BonePath = string.Empty;
        [BlendShareField(2)] public bool HasClusterMatrices;
        [BlendShareField(3)] public FbxMatrix4x4 TransformMatrix = FbxMatrix4x4.Identity;
        [BlendShareField(4)] public FbxMatrix4x4 TransformLinkMatrix = FbxMatrix4x4.Identity;
        [BlendShareField(5)] public LazyPayload<SkinWeightClusterPayload> Payload = LazyPayload<SkinWeightClusterPayload>.Empty;
    }

    [BlendShareRecord(SchemaVersion = 1)]
    public sealed class SkinWeightClusterPayload
    {
        [BlendShareField(1, Semantic = DataSemantic.SignedWeightDelta)]
        public IndexedBuffer<double> DeltaWeights;
    }

    public enum ZeroWeightSumPolicy : byte
    {
        /// <summary>Leave vertices whose weights all quantized to zero unweighted (caller preserves base skin).</summary>
        KeepUnweighted = 0,

        /// <summary>Assign weight 1.0 to the first cluster referencing the vertex.</summary>
        FallbackToFirstCluster = 1,

        /// <summary>Throw InvalidDataException if any referenced vertex sums to zero.</summary>
        Reject = 2
    }

    /// <summary>
    /// Deterministic per-vertex renormalization across multiple decoded skin weight clusters.
    /// </summary>
    public static class SkinWeightNormalization
    {
        public static IndexedBuffer<double>[] RenormalizePerVertex(
            IReadOnlyList<IndexedBuffer<double>> clusterWeights,
            int controlPointCount,
            ZeroWeightSumPolicy zeroPolicy = ZeroWeightSumPolicy.KeepUnweighted,
            double minWeightThreshold = 0d)
        {
            if (clusterWeights == null)
            {
                throw new System.ArgumentNullException(nameof(clusterWeights));
            }

            if (controlPointCount < 0)
            {
                throw new System.ArgumentOutOfRangeException(nameof(controlPointCount));
            }

            int clusterCount = clusterWeights.Count;
            if (clusterCount == 0)
            {
                return System.Array.Empty<IndexedBuffer<double>>();
            }

            var sums = new double[controlPointCount];
            var firstClusterForVertex = new int[controlPointCount];
            for (int v = 0; v < controlPointCount; v++)
            {
                firstClusterForVertex[v] = -1;
            }

            for (int c = 0; c < clusterCount; c++)
            {
                var buf = clusterWeights[c];
                if (buf == null) continue;
                var indices = buf.Indices;
                var values = buf.Values;
                for (int i = 0; i < indices.Length; i++)
                {
                    int cp = indices[i];
                    double w = values[i];
                    if (double.IsNaN(w) || double.IsInfinity(w) || w < 0d || w > 1d)
                    {
                        throw new System.IO.InvalidDataException($"Normalized skin weight out of [0,1] at cluster {c}, cp {cp}: {w}.");
                    }

                    if (firstClusterForVertex[cp] < 0)
                    {
                        firstClusterForVertex[cp] = c;
                    }

                    if (w >= minWeightThreshold && w > 0d)
                    {
                        sums[cp] += w;
                    }
                }
            }

            var normalized = new IndexedBuffer<double>[clusterCount];
            for (int c = 0; c < clusterCount; c++)
            {
                var buf = clusterWeights[c];
                if (buf == null)
                {
                    normalized[c] = IndexedBuffer<double>.Empty(IndexDomain.ControlPoint, controlPointCount);
                    continue;
                }

                var outIndices = new List<int>(buf.Count);
                var outValues = new List<double>(buf.Count);
                var indices = buf.Indices;
                var values = buf.Values;

                for (int i = 0; i < indices.Length; i++)
                {
                    int cp = indices[i];
                    double sum = sums[cp];
                    if (sum > 0d)
                    {
                        double w = values[i];
                        if (w >= minWeightThreshold && w > 0d)
                        {
                            outIndices.Add(cp);
                            outValues.Add(w / sum);
                        }
                    }
                    else
                    {
                        switch (zeroPolicy)
                        {
                            case ZeroWeightSumPolicy.KeepUnweighted:
                                break;
                            case ZeroWeightSumPolicy.FallbackToFirstCluster:
                                if (firstClusterForVertex[cp] == c)
                                {
                                    outIndices.Add(cp);
                                    outValues.Add(1d);
                                }
                                break;
                            case ZeroWeightSumPolicy.Reject:
                                throw new System.IO.InvalidDataException($"All skin influences for control point {cp} summed to zero after quantization/thresholding.");
                        }
                    }
                }

                normalized[c] = new IndexedBuffer<double>(
                    IndexDomain.ControlPoint,
                    controlPointCount,
                    outIndices.ToArray(),
                    outValues.ToArray(),
                    takeOwnership: true);
            }

            return normalized;
        }
    }
}
