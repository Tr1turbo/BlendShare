using System.Collections.Generic;
using System.Linq;
using Triturbo.Fbx;

namespace Triturbo.BlendShare.Core
{
    /// <summary>
    /// Creates normalized source ufbx mesh snapshots for extraction.
    /// Welding decisions live here.
    /// </summary>
    internal static class UfbxSourceMeshNormalizer
    {
        public static UfbxMesh Normalize(UfbxMesh source, FbxControlPointWelding welding)
        {
            if (source == null)
            {
                return null;
            }

            var vertices = source.GetVertices();
            var normals = source.GetNormals();
            var tangents = source.GetTangents();

            if (welding != null && welding.HasGroups)
            {
                welding.ApplyAverage(vertices);
                welding.ApplyAverage(normals);
                welding.ApplyAverage(tangents);
            }

            var snapshot = new UfbxMesh(source, vertices, normals, tangents);
            snapshot.SetSnapshotDeformers(CopyDeformers(source, snapshot, welding));
            return snapshot;
        }

        private static IEnumerable<UfbxDeformer> CopyDeformers(
            UfbxMesh source,
            UfbxMesh ownerMesh,
            FbxControlPointWelding welding)
        {
            foreach (var deformer in source.Deformers)
            {
                switch (deformer)
                {
                    case UfbxBlendDeformer blend:
                        yield return CopyBlendDeformer(blend, ownerMesh, welding);
                        break;
                    case UfbxSkinDeformer skin:
                        yield return CopySkinDeformer(skin, ownerMesh, welding);
                        break;
                }
            }
        }

        private static UfbxBlendDeformer CopyBlendDeformer(
            UfbxBlendDeformer source,
            UfbxMesh ownerMesh,
            FbxControlPointWelding welding)
        {
            var snapshot = new UfbxBlendDeformer(source, ownerMesh);
            snapshot.SetSnapshotChannels(source.Channels.Select(channel => CopyBlendChannel(channel, ownerMesh, snapshot, welding)));
            return snapshot;
        }

        private static UfbxBlendChannel CopyBlendChannel(
            UfbxBlendChannel source,
            UfbxMesh ownerMesh,
            UfbxBlendDeformer ownerDeformer,
            FbxControlPointWelding welding)
        {
            var snapshot = new UfbxBlendChannel(source, ownerMesh, ownerDeformer);
            snapshot.SetSnapshotBlendShapes(source.BlendShapes.Select(shape => CopyBlendShape(shape, ownerMesh, ownerDeformer, snapshot, welding)));
            return snapshot;
        }

        private static UfbxBlendShape CopyBlendShape(
            UfbxBlendShape source,
            UfbxMesh ownerMesh,
            UfbxBlendDeformer ownerDeformer,
            UfbxBlendChannel ownerChannel,
            FbxControlPointWelding welding)
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
                    CopyDense(indices, UfbxScene.ToVector3dArray(positions), positionDeltas);
                    CopyDense(indices, UfbxScene.ToVector3dArray(normals), normalDeltas);
                }
            }

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
            FbxControlPointWelding welding)
        {
            var snapshot = new UfbxSkinDeformer(source, ownerMesh);
            snapshot.SetSnapshotClusters(source.Clusters.Select(cluster => CopySkinCluster(cluster, ownerMesh, snapshot, welding)));
            return snapshot;
        }

        private static UfbxSkinCluster CopySkinCluster(
            UfbxSkinCluster source,
            UfbxMesh ownerMesh,
            UfbxSkinDeformer ownerSkin,
            FbxControlPointWelding welding)
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

            return new UfbxSkinCluster(source, ownerMesh, ownerSkin, sparseIndices.ToArray(), sparseWeights.ToArray());
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
