using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Fbx.Ufbx;

namespace Triturbo.BlendShare.Core
{
    /// <summary>
    /// Creates normalized source ufbx mesh snapshots for extraction.
    /// Welding decisions live here.
    /// </summary>
    internal static class UfbxSourceMeshNormalizer
    {
        public static UfbxMesh Normalize(UfbxMesh source, FbxControlPointWelding welding, FbxMatrix4x4 sourceOffset)
        {
            if (source == null)
            {
                return null;
            }

            var vertices = source.GetVertices();
            var normals = source.GetNormals();
            var tangents = source.GetTangents();

            if (!sourceOffset.TryInverse(out var inverseSourceOffset))
            {
                return null;
            }

            TransformPoints(vertices, sourceOffset);
            TransformNormals(normals, inverseSourceOffset);
            TransformVectors(tangents, sourceOffset, preserveMagnitude: true);

            if (welding != null && welding.HasGroups)
            {
                welding.ApplyAverage(vertices);
                welding.ApplyAverage(normals);
                welding.ApplyAverage(tangents);
            }

            var snapshot = new UfbxMesh(source, vertices, normals, tangents);
            snapshot.SetSnapshotDeformers(CopyDeformers(source, snapshot, welding, sourceOffset, inverseSourceOffset));
            return snapshot;
        }

        private static IEnumerable<UfbxDeformer> CopyDeformers(
            UfbxMesh source,
            UfbxMesh ownerMesh,
            FbxControlPointWelding welding,
            FbxMatrix4x4 sourceOffset,
            FbxMatrix4x4 inverseSourceOffset)
        {
            foreach (var deformer in source.Deformers)
            {
                switch (deformer)
                {
                    case UfbxBlendDeformer blend:
                        yield return CopyBlendDeformer(blend, ownerMesh, welding, sourceOffset, inverseSourceOffset);
                        break;
                    case UfbxSkinDeformer skin:
                        yield return CopySkinDeformer(skin, ownerMesh, welding, inverseSourceOffset);
                        break;
                }
            }
        }

        private static UfbxBlendDeformer CopyBlendDeformer(
            UfbxBlendDeformer source,
            UfbxMesh ownerMesh,
            FbxControlPointWelding welding,
            FbxMatrix4x4 sourceOffset,
            FbxMatrix4x4 inverseSourceOffset)
        {
            var snapshot = new UfbxBlendDeformer(source, ownerMesh);
            snapshot.SetSnapshotChannels(source.Channels.Select(channel => CopyBlendChannel(channel, ownerMesh, snapshot, welding, sourceOffset, inverseSourceOffset)));
            return snapshot;
        }

        private static UfbxBlendChannel CopyBlendChannel(
            UfbxBlendChannel source,
            UfbxMesh ownerMesh,
            UfbxBlendDeformer ownerDeformer,
            FbxControlPointWelding welding,
            FbxMatrix4x4 sourceOffset,
            FbxMatrix4x4 inverseSourceOffset)
        {
            var snapshot = new UfbxBlendChannel(source, ownerMesh, ownerDeformer);
            snapshot.SetSnapshotBlendShapes(source.BlendShapes.Select(shape => CopyBlendShape(shape, ownerMesh, ownerDeformer, snapshot, welding, sourceOffset, inverseSourceOffset)));
            return snapshot;
        }

        private static UfbxBlendShape CopyBlendShape(
            UfbxBlendShape source,
            UfbxMesh ownerMesh,
            UfbxBlendDeformer ownerDeformer,
            UfbxBlendChannel ownerChannel,
            FbxControlPointWelding welding,
            FbxMatrix4x4 sourceOffset,
            FbxMatrix4x4 inverseSourceOffset)
        {
            int controlPointCount = ownerMesh.ControlPointCount;
            var positionDeltas = new Vector3d[controlPointCount];
            var normalDeltas = new Vector3d[controlPointCount];

            if (source.OffsetCount > 0)
            {
                var indices = new int[source.OffsetCount];
                var positions = new double[source.OffsetCount * 3];
                var normals = new double[source.OffsetCount * 3];
                if (source.CopyOffsets(indices, positions, normals) != 0)
                {
                    CopyDense(indices, FbxArrayUtility.ToVector3dArray(positions), positionDeltas);
                    CopyDense(indices, FbxArrayUtility.ToVector3dArray(normals), normalDeltas);
                }
            }

            TransformVectors(positionDeltas, sourceOffset, preserveMagnitude: false);
            TransformNormalDeltas(
                normalDeltas,
                source.OwnerMesh?.GetNormals(),
                sourceOffset,
                inverseSourceOffset);

            if (welding != null && welding.HasGroups)
            {
                welding.ApplyAverage(positionDeltas);
                welding.ApplyAverage(normalDeltas);
            }

            BuildSparse(positionDeltas, normalDeltas, out var sparseIndices, out var sparsePositions, out var sparseNormals);
            return new UfbxBlendShape(source, ownerMesh, ownerDeformer, ownerChannel, sparseIndices, sparsePositions, sparseNormals);
        }

        private static UfbxSkinDeformer CopySkinDeformer(
            UfbxSkinDeformer source,
            UfbxMesh ownerMesh,
            FbxControlPointWelding welding,
            FbxMatrix4x4 inverseSourceOffset)
        {
            var snapshot = new UfbxSkinDeformer(source, ownerMesh);
            snapshot.SetSnapshotClusters(source.Clusters.Select(cluster => CopySkinCluster(cluster, ownerMesh, snapshot, welding, inverseSourceOffset)));
            return snapshot;
        }

        private static UfbxSkinCluster CopySkinCluster(
            UfbxSkinCluster source,
            UfbxMesh ownerMesh,
            UfbxSkinDeformer ownerSkin,
            FbxControlPointWelding welding,
            FbxMatrix4x4 inverseSourceOffset)
        {
            int controlPointCount = ownerMesh.ControlPointCount;
            var denseWeights = new double[controlPointCount];
            var indices = source.GetIndices();
            var weights = source.GetWeights();
            int count = System.Math.Min(indices.Length, weights.Length);
            for (int i = 0; i < count; i++)
            {
                int index = indices[i];
                if (index >= 0 && index < denseWeights.Length)
                {
                    denseWeights[index] = weights[i];
                }
            }

            if (welding != null && welding.HasGroups)
            {
                welding.ApplyAverage(
                    denseWeights,
                    () => new DoubleAccumulator(),
                    (accumulator, value) => accumulator.Add(value),
                    (accumulator, mergedCount) => accumulator.Average(mergedCount));
            }

            var sparseIndices = new List<int>();
            var sparseWeights = new List<double>();
            for (int i = 0; i < denseWeights.Length; i++)
            {
                if (denseWeights[i] == 0d)
                {
                    continue;
                }

                sparseIndices.Add(i);
                sparseWeights.Add(denseWeights[i]);
            }

            return new UfbxSkinCluster(
                source,
                ownerMesh,
                ownerSkin,
                sparseIndices.ToArray(),
                sparseWeights.ToArray(),
                CorrectMeshBindWorld(inverseSourceOffset, source.MeshBindWorld),
                source.BindToWorld);
        }

        internal static FbxMatrix4x4 CorrectMeshBindWorld(
            FbxMatrix4x4 inverseSourceOffset,
            FbxMatrix4x4 meshBindWorld)
        {
            return inverseSourceOffset * meshBindWorld;
        }

        private static void CopyDense(
            IReadOnlyList<int> indices,
            IReadOnlyList<Vector3d> values,
            Vector3d[] destination)
        {
            int count = System.Math.Min(indices?.Count ?? 0, values?.Count ?? 0);
            for (int i = 0; i < count; i++)
            {
                int index = indices[i];
                if (index >= 0 && index < destination.Length)
                {
                    destination[index] = values[i];
                }
            }
        }

        private static void TransformPoints(Vector3d[] points, FbxMatrix4x4 matrix)
        {
            if (points == null || matrix.IsIdentity)
            {
                return;
            }

            for (int i = 0; i < points.Length; i++)
            {
                points[i] = TransformPoint(matrix, points[i]);
            }
        }

        private static void TransformVectors(Vector3d[] vectors, FbxMatrix4x4 matrix, bool preserveMagnitude)
        {
            if (vectors == null || matrix.IsIdentity)
            {
                return;
            }

            for (int i = 0; i < vectors.Length; i++)
            {
                vectors[i] = preserveMagnitude
                    ? TransformDirectionPreservingMagnitude(matrix, vectors[i])
                    : TransformVector(matrix, vectors[i]);
            }
        }

        private static void TransformNormals(Vector3d[] normals, FbxMatrix4x4 inverseMatrix)
        {
            if (normals == null || inverseMatrix.IsIdentity)
            {
                return;
            }

            for (int i = 0; i < normals.Length; i++)
            {
                normals[i] = TransformNormal(inverseMatrix, normals[i]);
            }
        }

        private static void TransformNormalDeltas(
            Vector3d[] deltas,
            IReadOnlyList<Vector3d> baseNormals,
            FbxMatrix4x4 sourceOffset,
            FbxMatrix4x4 inverseSourceOffset)
        {
            if (deltas == null || sourceOffset.IsIdentity)
            {
                return;
            }

            for (int i = 0; i < deltas.Length; i++)
            {
                var baseNormal = i < (baseNormals?.Count ?? 0) ? baseNormals[i] : Vector3d.zero;
                if (baseNormal.sqrMagnitude <= Vector3d.Epsilon)
                {
                    deltas[i] = TransformNormal(inverseSourceOffset, deltas[i]);
                    continue;
                }

                var correctedBase = TransformNormal(inverseSourceOffset, baseNormal);
                var correctedTarget = TransformNormal(inverseSourceOffset, baseNormal + deltas[i]);
                deltas[i] = correctedTarget - correctedBase;
            }
        }

        private static Vector3d TransformNormal(FbxMatrix4x4 inverseMatrix, Vector3d normal)
        {
            var transformed = new Vector3d(
                normal.x * inverseMatrix[0, 0] + normal.y * inverseMatrix[0, 1] + normal.z * inverseMatrix[0, 2],
                normal.x * inverseMatrix[1, 0] + normal.y * inverseMatrix[1, 1] + normal.z * inverseMatrix[1, 2],
                normal.x * inverseMatrix[2, 0] + normal.y * inverseMatrix[2, 1] + normal.z * inverseMatrix[2, 2]);
            return transformed.normalized;
        }

        private static Vector3d TransformPoint(FbxMatrix4x4 matrix, Vector3d point)
        {
            return new Vector3d(
                point.x * matrix[0, 0] + point.y * matrix[1, 0] + point.z * matrix[2, 0] + matrix[3, 0],
                point.x * matrix[0, 1] + point.y * matrix[1, 1] + point.z * matrix[2, 1] + matrix[3, 1],
                point.x * matrix[0, 2] + point.y * matrix[1, 2] + point.z * matrix[2, 2] + matrix[3, 2]);
        }

        private static Vector3d TransformVector(FbxMatrix4x4 matrix, Vector3d vector)
        {
            return new Vector3d(
                vector.x * matrix[0, 0] + vector.y * matrix[1, 0] + vector.z * matrix[2, 0],
                vector.x * matrix[0, 1] + vector.y * matrix[1, 1] + vector.z * matrix[2, 1],
                vector.x * matrix[0, 2] + vector.y * matrix[1, 2] + vector.z * matrix[2, 2]);
        }

        private static Vector3d TransformDirectionPreservingMagnitude(FbxMatrix4x4 matrix, Vector3d vector)
        {
            var transformed = TransformVector(matrix, vector);
            double sourceMagnitude = vector.magnitude;
            double transformedMagnitude = transformed.magnitude;
            return sourceMagnitude > Vector3d.Epsilon && transformedMagnitude > Vector3d.Epsilon
                ? transformed * (sourceMagnitude / transformedMagnitude)
                : transformed;
        }

        private static void BuildSparse(
            IReadOnlyList<Vector3d> positions,
            IReadOnlyList<Vector3d> normals,
            out int[] indices,
            out Vector3d[] sparsePositions,
            out Vector3d[] sparseNormals)
        {
            var indexList = new List<int>();
            var positionList = new List<Vector3d>();
            var normalList = new List<Vector3d>();
            int count = System.Math.Max(positions?.Count ?? 0, normals?.Count ?? 0);
            for (int i = 0; i < count; i++)
            {
                var position = i < (positions?.Count ?? 0) ? positions[i] : Vector3d.zero;
                var normal = i < (normals?.Count ?? 0) ? normals[i] : Vector3d.zero;
                if (position.IsZero() && normal.IsZero())
                {
                    continue;
                }

                indexList.Add(i);
                positionList.Add(position);
                normalList.Add(normal);
            }

            indices = indexList.ToArray();
            sparsePositions = positionList.ToArray();
            sparseNormals = normalList.ToArray();
        }

        private sealed class DoubleAccumulator
        {
            private double sum;

            public void Add(double value)
            {
                sum += value;
            }

            public double Average(int count)
            {
                return count > 0 ? sum / count : 0d;
            }
        }
    }
}
