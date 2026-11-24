using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Fbx;

namespace Triturbo.BlendShare.Core
{
    internal static class UfbxSourceMeshNormalizer
    {
        public static IFbxMeshGeometry Normalize(IFbxMeshGeometry source, FbxControlPointWelding welding, FbxMatrix4x4 sourceOffset)
        {
            if (source == null || !sourceOffset.TryInverse(out var inverse))
            {
                return null;
            }

            return new NormalizedFbxMesh(source, welding, sourceOffset, inverse);
        }

        internal static FbxMatrix4x4 CorrectMeshBindWorld(FbxMatrix4x4 inverseSourceOffset, FbxMatrix4x4 meshBindWorld)
        {
            return inverseSourceOffset * meshBindWorld;
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
            private readonly Vector3d[] points;
            private readonly Vector3d[] normals;
            private readonly Vector3d[] tangents;
            private readonly int[] faceSizes;
            private readonly int[] corners;

            public long Id { get; }
            public string Name { get; }
            public int ControlPointCount => points.Length;
            public int FaceCount => faceSizes.Length;
            public int FaceCornerCount => corners.Length;
            public IReadOnlyList<IFbxDeformer> Deformers { get; }
            public IReadOnlyList<IFbxBlendDeformer> BlendDeformers { get; }
            public IReadOnlyList<IFbxSkinDeformer> SkinDeformers { get; }

            internal NormalizedFbxMesh(IFbxMeshGeometry source, FbxControlPointWelding welding, FbxMatrix4x4 offset, FbxMatrix4x4 inverse)
            {
                Id = source.Id;
                Name = source.Name;
                points = source.GetControlPoints();
                var rawNormals = source.GetFirstCornerNormals();
                // Retain the untransformed base normals for blendshape normal-delta transformation
                // without issuing a second native read across the bridge.
                bool mutatesNormals = !offset.IsIdentity || (welding != null && welding.HasGroups);
                normals = mutatesNormals ? (Vector3d[])rawNormals.Clone() : rawNormals;
                tangents = source.GetFirstCornerTangents();
                faceSizes = source.GetFaceSizes();
                corners = source.GetFaceCornerControlPoints();

                if (!offset.IsIdentity)
                {
                    for (int i = 0; i < points.Length; i++) points[i] = offset.TransformPoint(points[i]);
                    for (int i = 0; i < normals.Length; i++) normals[i] = inverse.TransformNormalWithInverse(normals[i]);
                    for (int i = 0; i < tangents.Length; i++) tangents[i] = offset.TransformDirectionPreservingMagnitude(tangents[i]);
                }
                welding?.ApplyAverage(points);
                welding?.ApplyAverage(normals);
                welding?.ApplyAverage(tangents);

                var deformers = new List<IFbxDeformer>();
                foreach (var deformer in source.Deformers)
                {
                    if (deformer is IFbxBlendDeformer blend)
                        deformers.Add(new NormalizedBlendDeformer(blend, this, shape =>
                            CopyBlendShape(shape, ControlPointCount, welding, offset, inverse, rawNormals)));
                    else if (deformer is IFbxSkinDeformer skin)
                        deformers.Add(new NormalizedSkinDeformer(skin, this, welding, inverse));
                }

                Deformers = deformers.AsReadOnly();
                BlendDeformers = deformers.OfType<IFbxBlendDeformer>().ToArray();
                SkinDeformers = deformers.OfType<IFbxSkinDeformer>().ToArray();
            }

            public bool TryCopyFaceSizes(Span<int> destination, out int written) => Copy(faceSizes, destination, out written);
            public bool TryCopyFaceCornerControlPoints(Span<int> destination, out int written) => Copy(corners, destination, out written);
            public bool TryCopyControlPoints(Span<Vector3d> destination, out int written) => Copy(points, destination, out written);
            public bool TryCopyFirstCornerNormals(Span<Vector3d> destination, out int written) =>
                Copy(normals.Length == ControlPointCount ? normals : null, destination, out written);
            public bool TryCopyFirstCornerTangents(Span<Vector3d> destination, out int written) =>
                Copy(tangents.Length == ControlPointCount ? tangents : null, destination, out written);
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

            internal NormalizedBlendDeformer(IFbxBlendDeformer source, IFbxMeshGeometry mesh, Func<IFbxBlendShape, ShapeOffsets> copy)
                : base(source, mesh)
            {
                Channels = source.Channels.Select(channel => new NormalizedBlendChannel(channel, this, copy)).ToArray();
            }
        }

        internal sealed class NormalizedBlendChannel : NormalizedElement, IFbxBlendChannel
        {
            public IFbxBlendDeformer OwnerDeformer { get; }
            public int KeyframeCount => Keyframes.Count;
            public IReadOnlyList<IFbxBlendKeyframe> Keyframes { get; }

            internal NormalizedBlendChannel(IFbxBlendChannel source, IFbxBlendDeformer owner, Func<IFbxBlendShape, ShapeOffsets> copy)
                : base(source, owner.OwnerMesh)
            {
                OwnerDeformer = owner;
                Keyframes = source.Keyframes.Select(key => new NormalizedBlendKeyframe(key, this, copy)).ToArray();
            }
        }

        internal sealed class NormalizedBlendKeyframe : IFbxBlendKeyframe
        {
            public double TargetWeight { get; }
            public double EffectiveWeight { get; }
            public IFbxBlendShape Shape { get; }

            internal NormalizedBlendKeyframe(IFbxBlendKeyframe source, IFbxBlendChannel owner, Func<IFbxBlendShape, ShapeOffsets> copy)
            {
                TargetWeight = source.TargetWeight;
                EffectiveWeight = source.EffectiveWeight;
                var shape = source.Shape;
                Shape = shape == null ? null : new NormalizedBlendShape(shape, owner, copy(shape));
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

        private static ShapeOffsets CopyBlendShape(IFbxBlendShape source, int pointCount, FbxControlPointWelding welding,
            FbxMatrix4x4 offset, FbxMatrix4x4 inverse, Vector3d[] baseNormals)
        {
            var sparse = new ShapeOffsets(source.OffsetCount, source.HasNormalOffsets, source.HasOffsetWeights);
            if (!source.TryCopyOffsets(sparse.Indices, sparse.Positions, sparse.Normals, sparse.Weights, out int written)
                || written != sparse.Indices.Length)
                throw new InvalidOperationException("Unable to copy FBX blend shape offsets.");

            for (int i = 0; !offset.IsIdentity && i < written; i++)
            {
                sparse.Positions[i] = offset.TransformVector(sparse.Positions[i]);
                if (sparse.Normals == null) continue;
                int index = sparse.Indices[i];
                var normal = (uint)index < (uint)baseNormals.Length ? baseNormals[index] : Vector3d.zero;
                sparse.Normals[i] = normal.sqrMagnitude <= Vector3d.Epsilon
                    ? inverse.TransformNormalWithInverse(sparse.Normals[i])
                    : inverse.TransformNormalWithInverse(normal + sparse.Normals[i]) - inverse.TransformNormalWithInverse(normal);
            }

            if (welding == null || !welding.HasGroups || written == 0) return sparse;

            var positions = new Vector3d[pointCount];
            var normalOffsets = source.HasNormalOffsets ? new Vector3d[pointCount] : null;
            // Keep explicit weights attached to their original control points. Newly affected points use the FBX default.
            var weightByPoint = source.HasOffsetWeights ? new Dictionary<int, double>() : null;
            for (int i = 0; i < written; i++)
            {
                int index = sparse.Indices[i];
                if ((uint)index >= (uint)pointCount) continue;
                positions[index] = sparse.Positions[i];
                if (normalOffsets != null) normalOffsets[index] = sparse.Normals[i];
                if (weightByPoint != null) weightByPoint[index] = sparse.Weights[i];
            }

            welding.ApplyAverage(positions);
            welding.ApplyAverage(normalOffsets);
            var affected = new List<int>();
            for (int i = 0; i < pointCount; i++)
            {
                if (!positions[i].IsZero() || (normalOffsets != null && !normalOffsets[i].IsZero()) ||
                    (weightByPoint != null && weightByPoint.ContainsKey(i))) affected.Add(i);
            }

            var result = new ShapeOffsets(affected.Count, source.HasNormalOffsets, source.HasOffsetWeights);
            for (int i = 0; i < affected.Count; i++)
            {
                int index = affected[i];
                result.Indices[i] = index;
                result.Positions[i] = positions[index];
                if (normalOffsets != null) result.Normals[i] = normalOffsets[index];
                if (weightByPoint != null) result.Weights[i] = weightByPoint.TryGetValue(index, out var weight) ? weight : 1d;
            }
            return result;
        }

        internal sealed class NormalizedBlendShape : NormalizedElement, IFbxBlendShape
        {
            private readonly ShapeOffsets offsets;
            public IFbxBlendChannel OwnerChannel { get; }
            public IFbxBlendDeformer OwnerDeformer => OwnerChannel.OwnerDeformer;
            public int OffsetCount => offsets.Indices.Length;
            public bool HasNormalOffsets => offsets.Normals != null;
            public bool HasOffsetWeights => offsets.Weights != null;

            internal NormalizedBlendShape(IFbxBlendShape source, IFbxBlendChannel owner, ShapeOffsets offsets)
                : base(source, owner.OwnerMesh)
            {
                OwnerChannel = owner;
                this.offsets = offsets;
            }

            public bool TryCopyOffsets(Span<int> indices, Span<Vector3d> positions, Span<Vector3d> normals, Span<double> weights, out int written)
            {
                written = 0;
                if (indices.IsEmpty && positions.IsEmpty && normals.IsEmpty && weights.IsEmpty)
                {
                    return OffsetCount == 0;
                }

                if ((!indices.IsEmpty && indices.Length < OffsetCount) || (!positions.IsEmpty && positions.Length < OffsetCount) ||
                    (!normals.IsEmpty && normals.Length < OffsetCount) || (!weights.IsEmpty && weights.Length < OffsetCount)) return false;

                if (!indices.IsEmpty) offsets.Indices.AsSpan().CopyTo(indices);
                if (!positions.IsEmpty) offsets.Positions.AsSpan().CopyTo(positions);
                if (!normals.IsEmpty)
                {
                    if (HasNormalOffsets) offsets.Normals.AsSpan().CopyTo(normals);
                    else normals.Slice(0, OffsetCount).Clear();
                }
                if (!weights.IsEmpty)
                {
                    if (HasOffsetWeights) offsets.Weights.AsSpan().CopyTo(weights);
                    else weights.Slice(0, OffsetCount).Fill(1d);
                }
                written = OffsetCount;
                return true;
            }
        }

        internal sealed class NormalizedSkinDeformer : NormalizedElement, IFbxSkinDeformer
        {
            public int ClusterCount => Clusters.Count;
            public IReadOnlyList<IFbxSkinCluster> Clusters { get; }

            internal NormalizedSkinDeformer(IFbxSkinDeformer source, IFbxMeshGeometry mesh, FbxControlPointWelding welding, FbxMatrix4x4 inverse)
                : base(source, mesh)
            {
                Clusters = source.Clusters.Select(cluster => new NormalizedSkinCluster(cluster, this, welding, inverse)).ToArray();
            }
        }

        internal sealed class NormalizedSkinCluster : NormalizedElement, IFbxSkinCluster
        {
            private readonly int[] indices;
            private readonly double[] weights;
            public IFbxSkinDeformer OwnerSkin { get; }
            public IFbxNode BoneNode => null;
            public string BonePath { get; }
            public int WeightCount => indices.Length;
            public FbxMatrix4x4 MeshBindWorld { get; }
            public FbxMatrix4x4 BindToWorld { get; }
            public FbxMatrix4x4 MeshNodeToBone { get; }
            public FbxMatrix4x4 GeometryToBone { get; }

            internal NormalizedSkinCluster(IFbxSkinCluster source, IFbxSkinDeformer owner, FbxControlPointWelding welding, FbxMatrix4x4 inverse)
                : base(source, owner.OwnerMesh)
            {
                OwnerSkin = owner;
                BonePath = source.BonePath;
                MeshBindWorld = CorrectMeshBindWorld(inverse, source.MeshBindWorld);
                BindToWorld = source.BindToWorld;
                MeshNodeToBone = source.MeshNodeToBone;
                GeometryToBone = source.GeometryToBone;

                var sourceIndices = source.GetControlPointIndices();
                var sourceWeights = source.GetWeights();
                var dense = new double[OwnerMesh.ControlPointCount];
                int pairCount = Math.Min(sourceIndices.Length, sourceWeights.Length);
                for (int i = 0; i < pairCount; i++)
                {
                    int index = sourceIndices[i];
                    if ((uint)index < (uint)dense.Length) dense[index] += sourceWeights[i];
                }
                welding?.ApplyAverage(dense, () => new DoubleAccumulator(),
                    (acc, value) => acc.Sum += value, (acc, count) => acc.Sum / count);
                indices = Enumerable.Range(0, dense.Length).Where(index => dense[index] != 0d).ToArray();
                weights = indices.Select(index => dense[index]).ToArray();
            }

            public bool TryCopyControlPointIndices(Span<int> destination, out int written) => Copy(indices, destination, out written);
            public bool TryCopyWeights(Span<double> destination, out int written) => Copy(weights, destination, out written);
        }

        private sealed class DoubleAccumulator
        {
            internal double Sum;
        }
    }
}
