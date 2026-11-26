using System;
using System.Collections.Generic;
using Triturbo.BlendShare.Fbx;

namespace Triturbo.BlendShare.Pipeline.Extraction
{
    /// <summary>
    /// Lazily decorates an <see cref="IFbxMeshGeometry"/> with a binary64 space transform
    /// (such as <c>Source raw -> Origin raw</c>) and optional control-point weld-group averaging.
    /// Zero allocations for untouched deformers or unused channels.
    /// </summary>
    public static class SourceMeshNormalizer
    {
        public static IFbxMeshGeometry Normalize(
            IFbxMeshGeometry source,
            FbxAffineSpaceTransform spaceTransform,
            IReadOnlyList<int[]> weldGroups = null)
        {
            if (source == null || !spaceTransform.IsValid)
            {
                return null;
            }

            int[][] sanitizedGroups = SanitizeAndMergeWeldGroups(weldGroups);
            bool hasWelding = sanitizedGroups.Length > 0;
            if (spaceTransform.IsIdentity && !hasWelding)
            {
                return source;
            }

            return new NormalizedFbxMesh(source, hasWelding ? sanitizedGroups : null, spaceTransform);
        }

        /// <summary>
        /// Sanitizes weld groups and merges any transitively overlapping groups via union-find
        /// so welding output is deterministic and independent of input group ordering.
        /// </summary>
        private static int[][] SanitizeAndMergeWeldGroups(IReadOnlyList<int[]> weldGroups)
        {
            if (weldGroups == null || weldGroups.Count == 0)
            {
                return Array.Empty<int[]>();
            }

            var parent = new Dictionary<int, int>();

            int Find(int x)
            {
                int root = x;
                while (parent[root] != root)
                {
                    root = parent[root];
                }

                int curr = x;
                while (parent[curr] != root)
                {
                    int next = parent[curr];
                    parent[curr] = root;
                    curr = next;
                }

                return root;
            }

            void Union(int a, int b)
            {
                int ra = Find(a);
                int rb = Find(b);
                if (ra != rb)
                {
                    if (ra < rb) parent[rb] = ra;
                    else parent[ra] = rb;
                }
            }

            for (int g = 0; g < weldGroups.Count; g++)
            {
                var raw = weldGroups[g];
                if (raw == null || raw.Length < 2)
                {
                    continue;
                }

                int firstValid = -1;
                for (int i = 0; i < raw.Length; i++)
                {
                    int cp = raw[i];
                    if (cp < 0) continue;
                    if (!parent.ContainsKey(cp)) parent[cp] = cp;
                    if (firstValid < 0)
                    {
                        firstValid = cp;
                    }
                    else
                    {
                        Union(firstValid, cp);
                    }
                }
            }

            if (parent.Count < 2)
            {
                return Array.Empty<int[]>();
            }

            var buckets = new Dictionary<int, List<int>>();
            foreach (int cp in parent.Keys)
            {
                int root = Find(cp);
                if (!buckets.TryGetValue(root, out var list))
                {
                    list = new List<int>(2);
                    buckets.Add(root, list);
                }

                list.Add(cp);
            }

            var merged = new List<int[]>(buckets.Count);
            foreach (var list in buckets.Values)
            {
                if (list.Count < 2) continue;
                list.Sort();
                merged.Add(list.ToArray());
            }

            merged.Sort((a, b) => a[0].CompareTo(b[0]));
            return merged.Count > 0 ? merged.ToArray() : Array.Empty<int[]>();
        }

        private static bool IsFinite(Vector3d v)
        {
            return !double.IsNaN(v.x) && !double.IsInfinity(v.x) &&
                   !double.IsNaN(v.y) && !double.IsInfinity(v.y) &&
                   !double.IsNaN(v.z) && !double.IsInfinity(v.z);
        }

        private static bool CopyArray<T>(T[] values, Span<T> destination, out int written)
        {
            written = 0;
            if (values == null || !values.AsSpan().TryCopyTo(destination))
            {
                return false;
            }

            written = values.Length;
            return true;
        }

        private static void ApplyPositionAverage(Vector3d[] values, int[][] weldGroups)
        {
            if (values == null || weldGroups == null)
            {
                return;
            }

            int length = values.Length;
            for (int g = 0; g < weldGroups.Length; g++)
            {
                var group = weldGroups[g];
                Vector3d sum = Vector3d.zero;
                int count = 0;
                for (int i = 0; i < group.Length; i++)
                {
                    int cp = group[i];
                    if ((uint)cp < (uint)length && IsFinite(values[cp]))
                    {
                        sum += values[cp];
                        count++;
                    }
                }

                if (count < 2)
                {
                    continue;
                }

                Vector3d avg = sum / count;
                for (int i = 0; i < group.Length; i++)
                {
                    int cp = group[i];
                    if ((uint)cp < (uint)length && IsFinite(values[cp]))
                    {
                        values[cp] = avg;
                    }
                }
            }
        }

        private sealed class NormalizedFbxMesh : IFbxMeshGeometry
        {
            private readonly IFbxMeshGeometry m_Source;
            private readonly int[][] m_WeldGroups;
            private readonly FbxAffineSpaceTransform m_SpaceTransform;

            private Vector3d[] m_Points;
            private Vector3d[] m_RawBaseNormals;
            private bool m_RawBaseNormalsLoaded;
            private Vector3d[] m_Normals;
            private bool m_NormalsLoaded;
            private Vector3d[] m_Tangents;
            private bool m_TangentsLoaded;

            public long Id => m_Source.Id;
            public string Name => m_Source.Name;
            public int ControlPointCount => m_Source.ControlPointCount;
            public int FaceCount => m_Source.FaceCount;
            public int FaceCornerCount => m_Source.FaceCornerCount;
            public IReadOnlyList<IFbxDeformer> Deformers { get; }
            public IReadOnlyList<IFbxBlendDeformer> BlendDeformers { get; }
            public IReadOnlyList<IFbxSkinDeformer> SkinDeformers { get; }

            internal int[][] WeldGroups => m_WeldGroups;
            internal FbxAffineSpaceTransform SpaceTransform => m_SpaceTransform;

            internal NormalizedFbxMesh(
                IFbxMeshGeometry source,
                int[][] weldGroups,
                FbxAffineSpaceTransform spaceTransform)
            {
                m_Source = source;
                m_WeldGroups = weldGroups;
                m_SpaceTransform = spaceTransform;

                var sourceDeformers = source.Deformers;
                var all = new List<IFbxDeformer>(sourceDeformers.Count);
                var blends = new List<IFbxBlendDeformer>();
                var skins = new List<IFbxSkinDeformer>();

                for (int i = 0; i < sourceDeformers.Count; i++)
                {
                    var deformer = sourceDeformers[i];
                    if (deformer is IFbxBlendDeformer blend)
                    {
                        var wrapped = new NormalizedBlendDeformer(blend, this);
                        all.Add(wrapped);
                        blends.Add(wrapped);
                    }
                    else if (deformer is IFbxSkinDeformer skin)
                    {
                        var wrapped = new NormalizedSkinDeformer(skin, this);
                        all.Add(wrapped);
                        skins.Add(wrapped);
                    }
                }

                Deformers = all.AsReadOnly();
                BlendDeformers = blends.ToArray();
                SkinDeformers = skins.ToArray();
            }

            internal Vector3d[] GetRawBaseNormals()
            {
                if (!m_RawBaseNormalsLoaded)
                {
                    m_RawBaseNormals = m_Source.GetFirstCornerNormals();
                    m_RawBaseNormalsLoaded = true;
                }

                return m_RawBaseNormals;
            }

            public bool TryCopyFaceSizes(Span<int> destination, out int written) =>
                m_Source.TryCopyFaceSizes(destination, out written);

            public bool TryCopyFaceCornerControlPoints(Span<int> destination, out int written) =>
                m_Source.TryCopyFaceCornerControlPoints(destination, out written);

            public bool TryCopyControlPoints(Span<Vector3d> destination, out int written)
            {
                if (m_Points == null)
                {
                    var loaded = m_Source.GetControlPoints();
                    if (!m_SpaceTransform.IsIdentity)
                    {
                        for (int i = 0; i < loaded.Length; i++)
                        {
                            loaded[i] = m_SpaceTransform.TransformPoint(loaded[i]);
                        }
                    }

                    if (m_WeldGroups != null)
                    {
                        ApplyPositionAverage(loaded, m_WeldGroups);
                    }

                    m_Points = loaded;
                }

                return CopyArray(m_Points, destination, out written);
            }

            public bool TryCopyFirstCornerNormals(Span<Vector3d> destination, out int written)
            {
                if (m_SpaceTransform.IsIdentity)
                {
                    return m_Source.TryCopyFirstCornerNormals(destination, out written);
                }

                if (!m_NormalsLoaded)
                {
                    var raw = GetRawBaseNormals();
                    if (raw.Length == ControlPointCount)
                    {
                        var transformed = new Vector3d[raw.Length];
                        for (int i = 0; i < raw.Length; i++)
                        {
                            transformed[i] = m_SpaceTransform.TransformNormal(raw[i]);
                        }

                        m_Normals = transformed;
                    }

                    m_NormalsLoaded = true;
                }

                return CopyArray(m_Normals, destination, out written);
            }

            public bool TryCopyFirstCornerTangents(Span<Vector3d> destination, out int written)
            {
                if (m_SpaceTransform.IsIdentity)
                {
                    return m_Source.TryCopyFirstCornerTangents(destination, out written);
                }

                if (!m_TangentsLoaded)
                {
                    var raw = m_Source.GetFirstCornerTangents();
                    if (raw.Length == ControlPointCount)
                    {
                        for (int i = 0; i < raw.Length; i++)
                        {
                            raw[i] = m_SpaceTransform.Offset.TransformDirectionPreservingMagnitude(raw[i]);
                        }

                        m_Tangents = raw;
                    }

                    m_TangentsLoaded = true;
                }

                return CopyArray(m_Tangents, destination, out written);
            }
        }

        private abstract class NormalizedElement : IFbxElement
        {
            public long Id { get; }
            public string Name { get; }
            public FbxElementType ElementType { get; }
            public IFbxMeshGeometry OwnerMesh { get; }

            protected NormalizedElement(IFbxElement source, IFbxMeshGeometry mesh)
            {
                Id = source.Id;
                Name = source.Name;
                ElementType = source.ElementType;
                OwnerMesh = mesh;
            }
        }

        private sealed class NormalizedBlendDeformer : NormalizedElement, IFbxBlendDeformer
        {
            public int ChannelCount => Channels.Count;
            public IReadOnlyList<IFbxBlendChannel> Channels { get; }

            internal NormalizedBlendDeformer(IFbxBlendDeformer source, NormalizedFbxMesh mesh)
                : base(source, mesh)
            {
                var srcChannels = source.Channels;
                var wrapped = new IFbxBlendChannel[srcChannels.Count];
                for (int i = 0; i < srcChannels.Count; i++)
                {
                    wrapped[i] = new NormalizedBlendChannel(srcChannels[i], this, mesh);
                }

                Channels = wrapped;
            }
        }

        private sealed class NormalizedBlendChannel : NormalizedElement, IFbxBlendChannel
        {
            public IFbxBlendDeformer OwnerDeformer { get; }
            public int KeyframeCount => Keyframes.Count;
            public IReadOnlyList<IFbxBlendKeyframe> Keyframes { get; }

            internal NormalizedBlendChannel(IFbxBlendChannel source, IFbxBlendDeformer owner, NormalizedFbxMesh mesh)
                : base(source, mesh)
            {
                OwnerDeformer = owner;
                var srcKeyframes = source.Keyframes;
                var wrapped = new IFbxBlendKeyframe[srcKeyframes.Count];
                for (int i = 0; i < srcKeyframes.Count; i++)
                {
                    wrapped[i] = new NormalizedBlendKeyframe(srcKeyframes[i], this, mesh);
                }

                Keyframes = wrapped;
            }
        }

        private sealed class NormalizedBlendKeyframe : IFbxBlendKeyframe
        {
            public double TargetWeight { get; }
            public double EffectiveWeight { get; }
            public IFbxBlendShape Shape { get; }

            internal NormalizedBlendKeyframe(IFbxBlendKeyframe source, IFbxBlendChannel owner, NormalizedFbxMesh mesh)
            {
                TargetWeight = source.TargetWeight;
                EffectiveWeight = source.EffectiveWeight;
                Shape = source.Shape != null ? new NormalizedBlendShape(source.Shape, owner, mesh) : null;
            }
        }

        private sealed class ShapeOffsets
        {
            internal readonly int[] Indices;
            internal readonly Vector3d[] Positions;
            internal readonly Vector3d[] Normals;
            internal readonly double[] Weights;

            internal ShapeOffsets(int count, bool hasNormals, bool hasWeights)
            {
                Indices = new int[count];
                Positions = new Vector3d[count];
                Normals = hasNormals ? new Vector3d[count] : null;
                Weights = hasWeights ? new double[count] : null;
            }
        }

        private static ShapeOffsets BuildNormalizedBlendShapeOffsets(
            IFbxBlendShape source,
            NormalizedFbxMesh ownerMesh)
        {
            int sourceCount = source.OffsetCount;
            bool hasNormals = source.HasNormalOffsets;
            bool hasWeights = source.HasOffsetWeights;
            var sparse = new ShapeOffsets(sourceCount, hasNormals, hasWeights);
            if (sourceCount == 0)
            {
                return sparse;
            }

            if (!source.TryCopyOffsets(
                    sparse.Indices,
                    sparse.Positions,
                    sparse.Normals ?? Span<Vector3d>.Empty,
                    sparse.Weights ?? Span<double>.Empty,
                    out int written) ||
                written != sourceCount)
            {
                throw new InvalidOperationException("Unable to copy FBX blend shape offsets.");
            }

            var spaceTransform = ownerMesh.SpaceTransform;
            if (!spaceTransform.IsIdentity)
            {
                Vector3d[] baseNormals = hasNormals ? ownerMesh.GetRawBaseNormals() : null;
                int baseNormalLength = baseNormals != null ? baseNormals.Length : 0;
                for (int i = 0; i < written; i++)
                {
                    sparse.Positions[i] = spaceTransform.TransformVector(sparse.Positions[i]);
                    if (!hasNormals)
                    {
                        continue;
                    }

                    int cp = sparse.Indices[i];
                    Vector3d baseNormal = (uint)cp < (uint)baseNormalLength ? baseNormals[cp] : Vector3d.zero;
                    sparse.Normals[i] = spaceTransform.TransformNormalDelta(baseNormal, sparse.Normals[i]);
                }
            }

            var weldGroups = ownerMesh.WeldGroups;
            if (weldGroups == null || weldGroups.Length == 0 || written == 0)
            {
                return sparse;
            }

            int pointCount = ownerMesh.ControlPointCount;
            var positionByPoint = new Dictionary<int, Vector3d>(written);
            var normalByPoint = hasNormals ? new Dictionary<int, Vector3d>(written) : null;
            var weightByPoint = hasWeights ? new Dictionary<int, double>(written) : null;

            for (int i = 0; i < written; i++)
            {
                int cp = sparse.Indices[i];
                if ((uint)cp >= (uint)pointCount)
                {
                    continue;
                }

                positionByPoint[cp] = sparse.Positions[i];
                if (normalByPoint != null) normalByPoint[cp] = sparse.Normals[i];
                if (weightByPoint != null) weightByPoint[cp] = sparse.Weights[i];
            }

            for (int g = 0; g < weldGroups.Length; g++)
            {
                var group = weldGroups[g];
                Vector3d first = Vector3d.zero;
                Vector3d sum = Vector3d.zero;
                int validCount = 0;
                bool differs = false;

                for (int i = 0; i < group.Length; i++)
                {
                    int cp = group[i];
                    if ((uint)cp >= (uint)pointCount)
                    {
                        continue;
                    }

                    positionByPoint.TryGetValue(cp, out var delta);
                    if (!IsFinite(delta))
                    {
                        // Never allow a non-finite delta to poison finite weld peers; leave it intact so extractor diagnoses it.
                        continue;
                    }

                    if (validCount == 0)
                    {
                        first = delta;
                    }
                    else if (!delta.Equals(first))
                    {
                        differs = true;
                    }

                    sum += delta;
                    validCount++;
                }

                if (!differs || validCount < 2)
                {
                    continue;
                }

                Vector3d average = sum / validCount;
                bool isZero = average.IsZero();
                for (int i = 0; i < group.Length; i++)
                {
                    int cp = group[i];
                    if ((uint)cp >= (uint)pointCount)
                    {
                        continue;
                    }

                    if (positionByPoint.TryGetValue(cp, out var existing) && !IsFinite(existing))
                    {
                        continue;
                    }

                    if (isZero)
                    {
                        positionByPoint.Remove(cp);
                    }
                    else
                    {
                        positionByPoint[cp] = average;
                    }
                }
            }

            var affected = new HashSet<int>();
            foreach (var pair in positionByPoint)
            {
                if (!pair.Value.IsZero())
                {
                    affected.Add(pair.Key);
                }
            }

            if (normalByPoint != null)
            {
                foreach (var pair in normalByPoint)
                {
                    if (!pair.Value.IsZero())
                    {
                        affected.Add(pair.Key);
                    }
                }
            }

            if (weightByPoint != null)
            {
                foreach (int cp in weightByPoint.Keys)
                {
                    affected.Add(cp);
                }
            }

            var sorted = new int[affected.Count];
            affected.CopyTo(sorted);
            Array.Sort(sorted);

            var result = new ShapeOffsets(sorted.Length, hasNormals, hasWeights);
            for (int i = 0; i < sorted.Length; i++)
            {
                int cp = sorted[i];
                result.Indices[i] = cp;
                positionByPoint.TryGetValue(cp, out result.Positions[i]);
                if (normalByPoint != null)
                {
                    normalByPoint.TryGetValue(cp, out result.Normals[i]);
                }

                if (weightByPoint != null)
                {
                    result.Weights[i] = weightByPoint.TryGetValue(cp, out double w) ? w : 1d;
                }
            }

            return result;
        }

        private sealed class NormalizedBlendShape : NormalizedElement, IFbxBlendShape
        {
            private readonly IFbxBlendShape m_Source;
            private readonly NormalizedFbxMesh m_OwnerMesh;
            private ShapeOffsets m_Offsets;

            public IFbxBlendChannel OwnerChannel { get; }
            public IFbxBlendDeformer OwnerDeformer => OwnerChannel.OwnerDeformer;
            public int OffsetCount => GetOrBuildOffsets().Indices.Length;
            public bool HasNormalOffsets => m_Source.HasNormalOffsets;
            public bool HasOffsetWeights => m_Source.HasOffsetWeights;

            internal NormalizedBlendShape(IFbxBlendShape source, IFbxBlendChannel owner, NormalizedFbxMesh ownerMesh)
                : base(source, ownerMesh)
            {
                m_Source = source;
                m_OwnerMesh = ownerMesh;
                OwnerChannel = owner;
            }

            private ShapeOffsets GetOrBuildOffsets()
            {
                if (m_Offsets == null)
                {
                    m_Offsets = BuildNormalizedBlendShapeOffsets(m_Source, m_OwnerMesh);
                }

                return m_Offsets;
            }

            public bool TryCopyOffsets(
                Span<int> indices,
                Span<Vector3d> positions,
                Span<Vector3d> normals,
                Span<double> weights,
                out int written)
            {
                var built = GetOrBuildOffsets();
                int count = built.Indices.Length;
                written = 0;
                if (indices.IsEmpty && positions.IsEmpty && normals.IsEmpty && weights.IsEmpty)
                {
                    return count == 0;
                }

                if ((!indices.IsEmpty && indices.Length < count) ||
                    (!positions.IsEmpty && positions.Length < count) ||
                    (!normals.IsEmpty && normals.Length < count) ||
                    (!weights.IsEmpty && weights.Length < count))
                {
                    return false;
                }

                if (!indices.IsEmpty) built.Indices.AsSpan().CopyTo(indices);
                if (!positions.IsEmpty) built.Positions.AsSpan().CopyTo(positions);
                if (!normals.IsEmpty)
                {
                    if (built.Normals != null) built.Normals.AsSpan().CopyTo(normals);
                    else normals.Slice(0, count).Clear();
                }

                if (!weights.IsEmpty)
                {
                    if (built.Weights != null) built.Weights.AsSpan().CopyTo(weights);
                    else weights.Slice(0, count).Fill(1d);
                }

                written = count;
                return true;
            }
        }

        private sealed class NormalizedSkinDeformer : NormalizedElement, IFbxSkinDeformer
        {
            public int ClusterCount => Clusters.Count;
            public IReadOnlyList<IFbxSkinCluster> Clusters { get; }

            internal NormalizedSkinDeformer(IFbxSkinDeformer source, NormalizedFbxMesh mesh)
                : base(source, mesh)
            {
                var srcClusters = source.Clusters;
                var wrapped = new IFbxSkinCluster[srcClusters.Count];
                for (int i = 0; i < srcClusters.Count; i++)
                {
                    wrapped[i] = new NormalizedSkinCluster(srcClusters[i], this, mesh);
                }

                Clusters = wrapped;
            }
        }

        private sealed class NormalizedSkinCluster : NormalizedElement, IFbxSkinCluster
        {
            private readonly IFbxSkinCluster m_Source;
            private readonly NormalizedFbxMesh m_OwnerMesh;
            private int[] m_Indices;
            private double[] m_Weights;
            private bool m_WeightsBuilt;

            public IFbxSkinDeformer OwnerSkin { get; }
            public IFbxNode BoneNode => m_Source.BoneNode;
            public string BonePath => m_Source.BonePath;
            public FbxClusterLinkMode LinkMode => m_Source.LinkMode;
            public int WeightCount => m_OwnerMesh.WeldGroups == null ? m_Source.WeightCount : EnsureWeightsBuilt().Length;
            public FbxMatrix4x4 MeshBindWorld { get; }
            public FbxMatrix4x4 BindToWorld => m_Source.BindToWorld;
            public FbxMatrix4x4 MeshNodeToBone => m_Source.MeshNodeToBone;
            public FbxMatrix4x4 GeometryToBone => m_Source.GeometryToBone;

            internal NormalizedSkinCluster(IFbxSkinCluster source, IFbxSkinDeformer owner, NormalizedFbxMesh ownerMesh)
                : base(source, ownerMesh)
            {
                m_Source = source;
                m_OwnerMesh = ownerMesh;
                OwnerSkin = owner;
                MeshBindWorld = ownerMesh.SpaceTransform.TransformMeshBindWorld(source.MeshBindWorld);
            }

            public bool TryCopyControlPointIndices(Span<int> destination, out int written)
            {
                if (m_OwnerMesh.WeldGroups == null)
                {
                    return m_Source.TryCopyControlPointIndices(destination, out written);
                }

                EnsureWeightsBuilt();
                return CopyArray(m_Indices, destination, out written);
            }

            public bool TryCopyWeights(Span<double> destination, out int written)
            {
                if (m_OwnerMesh.WeldGroups == null)
                {
                    return m_Source.TryCopyWeights(destination, out written);
                }

                EnsureWeightsBuilt();
                return CopyArray(m_Weights, destination, out written);
            }

            private int[] EnsureWeightsBuilt()
            {
                if (m_WeightsBuilt)
                {
                    return m_Indices;
                }

                var sourceIndices = m_Source.GetControlPointIndices();
                var sourceWeights = m_Source.GetWeights();
                int pointCount = m_OwnerMesh.ControlPointCount;
                int pairCount = Math.Min(sourceIndices.Length, sourceWeights.Length);
                var weightByPoint = new Dictionary<int, double>(pairCount);

                for (int i = 0; i < pairCount; i++)
                {
                    int cp = sourceIndices[i];
                    if ((uint)cp >= (uint)pointCount)
                    {
                        continue;
                    }

                    weightByPoint.TryGetValue(cp, out double existing);
                    weightByPoint[cp] = existing + sourceWeights[i];
                }

                var groups = m_OwnerMesh.WeldGroups;
                for (int g = 0; g < groups.Length; g++)
                {
                    var group = groups[g];
                    double first = 0d;
                    double sum = 0d;
                    int validCount = 0;
                    bool differs = false;

                    for (int i = 0; i < group.Length; i++)
                    {
                        int cp = group[i];
                        if ((uint)cp >= (uint)pointCount)
                        {
                            continue;
                        }

                        weightByPoint.TryGetValue(cp, out double w);
                        if (validCount == 0)
                        {
                            first = w;
                        }
                        else if (!w.Equals(first))
                        {
                            differs = true;
                        }

                        sum += w;
                        validCount++;
                    }

                    if (!differs || validCount < 2)
                    {
                        continue;
                    }

                    double average = sum / validCount;
                    for (int i = 0; i < group.Length; i++)
                    {
                        int cp = group[i];
                        if ((uint)cp >= (uint)pointCount)
                        {
                            continue;
                        }

                        if (average == 0d)
                        {
                            weightByPoint.Remove(cp);
                        }
                        else
                        {
                            weightByPoint[cp] = average;
                        }
                    }
                }

                var nonZero = new List<int>(weightByPoint.Count);
                foreach (var pair in weightByPoint)
                {
                    if (pair.Value != 0d)
                    {
                        nonZero.Add(pair.Key);
                    }
                }

                nonZero.Sort();
                m_Indices = nonZero.ToArray();
                m_Weights = new double[m_Indices.Length];
                for (int i = 0; i < m_Indices.Length; i++)
                {
                    m_Weights[i] = weightByPoint[m_Indices[i]];
                }

                m_WeightsBuilt = true;
                return m_Indices;
            }
        }
    }
}
