using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Fbx;

namespace Triturbo.BlendShare.Core
{
    /// <summary>
    /// Lazily decorates a source <see cref="IFbxMeshGeometry"/> with Origin-space alignment and
    /// Unity <c>WeldVertices</c> control-point consistency so extractors stay unaware of preprocessing.
    /// </summary>
    internal static class UfbxSourceMeshNormalizer
    {
        public static IFbxMeshGeometry Normalize(
            IFbxMeshGeometry source,
            FbxControlPointWelding welding,
            FbxMatrix4x4 sourceOffset)
        {
            if (source == null || !FbxSourceSpaceTransform.TryCreate(sourceOffset, out var spaceTransform))
            {
                return null;
            }

            bool hasWelding = welding != null && welding.HasGroups;
            if (spaceTransform.IsIdentity && !hasWelding)
            {
                return source;
            }

            return new NormalizedFbxMesh(source, hasWelding ? welding : null, spaceTransform);
        }

        private static bool Copy<T>(T[] values, Span<T> destination, out int written)
        {
            written = 0;
            if (values == null || !values.AsSpan().TryCopyTo(destination)) return false;
            written = values.Length;
            return true;
        }

        internal sealed class NormalizedFbxMesh : IFbxMeshGeometry
        {
            private readonly IFbxMeshGeometry source;
            private readonly FbxControlPointWelding welding;
            private readonly FbxSourceSpaceTransform spaceTransform;

            private Vector3d[] points;
            private Vector3d[] rawBaseNormals;
            private bool rawBaseNormalsLoaded;
            private Vector3d[] normals;
            private bool normalsLoaded;
            private Vector3d[] tangents;
            private bool tangentsLoaded;

            public long Id => source.Id;
            public string Name => source.Name;
            public int ControlPointCount => source.ControlPointCount;
            public int FaceCount => source.FaceCount;
            public int FaceCornerCount => source.FaceCornerCount;
            public IReadOnlyList<IFbxDeformer> Deformers { get; }
            public IReadOnlyList<IFbxBlendDeformer> BlendDeformers { get; }
            public IReadOnlyList<IFbxSkinDeformer> SkinDeformers { get; }

            internal NormalizedFbxMesh(
                IFbxMeshGeometry source,
                FbxControlPointWelding welding,
                FbxSourceSpaceTransform spaceTransform)
            {
                this.source = source;
                this.welding = welding;
                this.spaceTransform = spaceTransform;

                var deformers = new List<IFbxDeformer>(source.Deformers.Count);
                foreach (var deformer in source.Deformers)
                {
                    if (deformer is IFbxBlendDeformer blend)
                    {
                        deformers.Add(new NormalizedBlendDeformer(blend, this));
                    }
                    else if (deformer is IFbxSkinDeformer skin)
                    {
                        deformers.Add(new NormalizedSkinDeformer(skin, this));
                    }
                }

                Deformers = deformers.AsReadOnly();
                BlendDeformers = deformers.OfType<IFbxBlendDeformer>().ToArray();
                SkinDeformers = deformers.OfType<IFbxSkinDeformer>().ToArray();
            }

            internal FbxControlPointWelding Welding => welding;
            internal FbxSourceSpaceTransform SpaceTransform => spaceTransform;

            internal Vector3d[] GetRawBaseNormals()
            {
                if (!rawBaseNormalsLoaded)
                {
                    rawBaseNormals = source.GetFirstCornerNormals();
                    rawBaseNormalsLoaded = true;
                }

                return rawBaseNormals;
            }

            public bool TryCopyFaceSizes(Span<int> destination, out int written) =>
                source.TryCopyFaceSizes(destination, out written);

            public bool TryCopyFaceCornerControlPoints(Span<int> destination, out int written) =>
                source.TryCopyFaceCornerControlPoints(destination, out written);

            public bool TryCopyControlPoints(Span<Vector3d> destination, out int written)
            {
                if (points == null)
                {
                    var loaded = source.GetControlPoints();
                    if (!spaceTransform.IsIdentity)
                    {
                        for (int i = 0; i < loaded.Length; i++)
                        {
                            loaded[i] = spaceTransform.TransformPoint(loaded[i]);
                        }
                    }

                    welding?.ApplyAverage(loaded);
                    points = loaded;
                }

                return Copy(points, destination, out written);
            }

            public bool TryCopyFirstCornerNormals(Span<Vector3d> destination, out int written)
            {
                // Unity WeldVertices never inspects normals; only apply sourceOffset space alignment.
                if (spaceTransform.IsIdentity)
                {
                    return source.TryCopyFirstCornerNormals(destination, out written);
                }

                if (!normalsLoaded)
                {
                    var raw = GetRawBaseNormals();
                    if (raw.Length == ControlPointCount)
                    {
                        var transformed = new Vector3d[raw.Length];
                        for (int i = 0; i < raw.Length; i++)
                        {
                            transformed[i] = spaceTransform.TransformNormal(raw[i]);
                        }

                        normals = transformed;
                    }

                    normalsLoaded = true;
                }

                return Copy(normals, destination, out written);
            }

            public bool TryCopyFirstCornerTangents(Span<Vector3d> destination, out int written)
            {
                // Unity WeldVertices never inspects tangents; only apply sourceOffset space alignment.
                if (spaceTransform.IsIdentity)
                {
                    return source.TryCopyFirstCornerTangents(destination, out written);
                }

                if (!tangentsLoaded)
                {
                    var raw = source.GetFirstCornerTangents();
                    if (raw.Length == ControlPointCount)
                    {
                        for (int i = 0; i < raw.Length; i++)
                        {
                            raw[i] = spaceTransform.Offset.TransformDirectionPreservingMagnitude(raw[i]);
                        }

                        tangents = raw;
                    }

                    tangentsLoaded = true;
                }

                return Copy(tangents, destination, out written);
            }
        }

        internal abstract class NormalizedElement : IFbxElement
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

        internal sealed class NormalizedBlendDeformer : NormalizedElement, IFbxBlendDeformer
        {
            public int ChannelCount => Channels.Count;
            public IReadOnlyList<IFbxBlendChannel> Channels { get; }

            internal NormalizedBlendDeformer(IFbxBlendDeformer source, NormalizedFbxMesh mesh)
                : base(source, mesh)
            {
                Channels = source.Channels
                    .Select(channel => new NormalizedBlendChannel(channel, this, mesh))
                    .ToArray();
            }
        }

        internal sealed class NormalizedBlendChannel : NormalizedElement, IFbxBlendChannel
        {
            public IFbxBlendDeformer OwnerDeformer { get; }
            public int KeyframeCount => Keyframes.Count;
            public IReadOnlyList<IFbxBlendKeyframe> Keyframes { get; }

            internal NormalizedBlendChannel(IFbxBlendChannel source, IFbxBlendDeformer owner, NormalizedFbxMesh mesh)
                : base(source, mesh)
            {
                OwnerDeformer = owner;
                Keyframes = source.Keyframes
                    .Select(key => new NormalizedBlendKeyframe(key, this, mesh))
                    .ToArray();
            }
        }

        internal sealed class NormalizedBlendKeyframe : IFbxBlendKeyframe
        {
            public double TargetWeight { get; }
            public double EffectiveWeight { get; }
            public IFbxBlendShape Shape { get; }

            internal NormalizedBlendKeyframe(IFbxBlendKeyframe source, IFbxBlendChannel owner, NormalizedFbxMesh mesh)
            {
                TargetWeight = source.TargetWeight;
                EffectiveWeight = source.EffectiveWeight;
                var shape = source.Shape;
                Shape = shape == null ? null : new NormalizedBlendShape(shape, owner, mesh);
            }
        }

        internal sealed class ShapeOffsets
        {
            internal readonly int[] Indices;
            internal readonly Vector3d[] Positions;
            internal readonly Vector3d[] Normals;
            internal readonly double[] Weights;

            internal ShapeOffsets(int count, bool normals, bool weights)
            {
                Indices = new int[count];
                Positions = new Vector3d[count];
                Normals = normals ? new Vector3d[count] : null;
                Weights = weights ? new double[count] : null;
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

            if (!source.TryCopyOffsets(sparse.Indices, sparse.Positions, sparse.Normals, sparse.Weights, out int written)
                || written != sourceCount)
            {
                throw new InvalidOperationException("Unable to copy FBX blend shape offsets.");
            }

            var spaceTransform = ownerMesh.SpaceTransform;
            if (!spaceTransform.IsIdentity)
            {
                Vector3d[] baseNormals = hasNormals ? ownerMesh.GetRawBaseNormals() : null;
                int baseNormalLength = baseNormals?.Length ?? 0;
                for (int i = 0; i < written; i++)
                {
                    sparse.Positions[i] = spaceTransform.TransformVector(sparse.Positions[i]);
                    if (!hasNormals)
                    {
                        continue;
                    }

                    int cp = sparse.Indices[i];
                    var baseNormal = (uint)cp < (uint)baseNormalLength ? baseNormals[cp] : Vector3d.zero;
                    sparse.Normals[i] = spaceTransform.TransformNormalDelta(baseNormal, sparse.Normals[i]);
                }
            }

            var welding = ownerMesh.Welding;
            if (welding == null || !welding.HasGroups || written == 0)
            {
                return sparse;
            }

            int pointCount = ownerMesh.ControlPointCount;
            var positionByPoint = new Dictionary<int, Vector3d>(written);
            var normalByPoint = hasNormals ? new Dictionary<int, Vector3d>(written) : null;
            // Keep explicit weights attached to their original control points. Newly affected points use the FBX default.
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

            // Weld only position deltas across Origin's Unity WeldVertices groups; normal deltas stay untouched.
            var groups = welding.Groups;
            for (int g = 0; g < groups.Count; g++)
            {
                var group = groups[g];
                if (group == null || group.Length < 2)
                {
                    continue;
                }

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

                var average = sum / validCount;
                bool isZero = average.IsZero();
                for (int i = 0; i < group.Length; i++)
                {
                    int cp = group[i];
                    if ((uint)cp >= (uint)pointCount)
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

            var sorted = affected.ToArray();
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
                    result.Weights[i] = weightByPoint.TryGetValue(cp, out var weight) ? weight : 1d;
                }
            }

            return result;
        }

        internal sealed class NormalizedBlendShape : NormalizedElement, IFbxBlendShape
        {
            private readonly IFbxBlendShape source;
            private readonly NormalizedFbxMesh ownerMesh;
            private ShapeOffsets offsets;

            public IFbxBlendChannel OwnerChannel { get; }
            public IFbxBlendDeformer OwnerDeformer => OwnerChannel.OwnerDeformer;
            public int OffsetCount => GetOrBuildOffsets().Indices.Length;
            public bool HasNormalOffsets => source.HasNormalOffsets;
            public bool HasOffsetWeights => source.HasOffsetWeights;

            internal NormalizedBlendShape(IFbxBlendShape source, IFbxBlendChannel owner, NormalizedFbxMesh ownerMesh)
                : base(source, ownerMesh)
            {
                this.source = source;
                this.ownerMesh = ownerMesh;
                OwnerChannel = owner;
            }

            private ShapeOffsets GetOrBuildOffsets()
            {
                return offsets ??= BuildNormalizedBlendShapeOffsets(source, ownerMesh);
            }

            public bool TryCopyOffsets(Span<int> indices, Span<Vector3d> positions, Span<Vector3d> normals, Span<double> weights, out int written)
            {
                var built = GetOrBuildOffsets();
                int count = built.Indices.Length;
                written = 0;
                if (indices.IsEmpty && positions.IsEmpty && normals.IsEmpty && weights.IsEmpty)
                {
                    return count == 0;
                }

                if ((!indices.IsEmpty && indices.Length < count) || (!positions.IsEmpty && positions.Length < count) ||
                    (!normals.IsEmpty && normals.Length < count) || (!weights.IsEmpty && weights.Length < count))
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

        internal sealed class NormalizedSkinDeformer : NormalizedElement, IFbxSkinDeformer
        {
            public int ClusterCount => Clusters.Count;
            public IReadOnlyList<IFbxSkinCluster> Clusters { get; }

            internal NormalizedSkinDeformer(IFbxSkinDeformer source, NormalizedFbxMesh mesh)
                : base(source, mesh)
            {
                Clusters = source.Clusters
                    .Select(cluster => new NormalizedSkinCluster(cluster, this, mesh))
                    .ToArray();
            }
        }

        internal sealed class NormalizedSkinCluster : NormalizedElement, IFbxSkinCluster
        {
            private readonly IFbxSkinCluster source;
            private readonly NormalizedFbxMesh ownerMesh;
            private int[] indices;
            private double[] weights;
            private bool weightsBuilt;

            public IFbxSkinDeformer OwnerSkin { get; }
            public IFbxNode BoneNode => source.BoneNode;
            public string BonePath => source.BonePath;
            public int WeightCount => ownerMesh.Welding == null ? source.WeightCount : EnsureWeightsBuilt().Length;
            public FbxMatrix4x4 MeshBindWorld { get; }
            public FbxMatrix4x4 BindToWorld => source.BindToWorld;
            public FbxMatrix4x4 MeshNodeToBone => source.MeshNodeToBone;
            public FbxMatrix4x4 GeometryToBone => source.GeometryToBone;

            internal NormalizedSkinCluster(IFbxSkinCluster source, IFbxSkinDeformer owner, NormalizedFbxMesh ownerMesh)
                : base(source, ownerMesh)
            {
                this.source = source;
                this.ownerMesh = ownerMesh;
                OwnerSkin = owner;
                MeshBindWorld = ownerMesh.SpaceTransform.TransformMeshBindWorld(source.MeshBindWorld);
            }

            public bool TryCopyControlPointIndices(Span<int> destination, out int written)
            {
                if (ownerMesh.Welding == null)
                {
                    return source.TryCopyControlPointIndices(destination, out written);
                }

                EnsureWeightsBuilt();
                return Copy(indices, destination, out written);
            }

            public bool TryCopyWeights(Span<double> destination, out int written)
            {
                if (ownerMesh.Welding == null)
                {
                    return source.TryCopyWeights(destination, out written);
                }

                EnsureWeightsBuilt();
                return Copy(weights, destination, out written);
            }

            private int[] EnsureWeightsBuilt()
            {
                if (weightsBuilt)
                {
                    return indices;
                }

                var sourceIndices = source.GetControlPointIndices();
                var sourceWeights = source.GetWeights();
                int pointCount = ownerMesh.ControlPointCount;
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

                var groups = ownerMesh.Welding.Groups;
                for (int g = 0; g < groups.Count; g++)
                {
                    var group = groups[g];
                    if (group == null || group.Length < 2)
                    {
                        continue;
                    }

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
                indices = nonZero.ToArray();
                weights = new double[indices.Length];
                for (int i = 0; i < indices.Length; i++)
                {
                    weights[i] = weightByPoint[indices[i]];
                }

                weightsBuilt = true;
                return indices;
            }
        }
    }
}
