using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.Fbx;

namespace Triturbo.BlendShare.Core
{
    /// <summary>
    /// Normalizes source FBX reader data for extraction by welding equivalent control-point values in place.
    /// </summary>
    internal static class FbxSourceFbxNormalizer
    {
        public static FbxDocument Normalize(
            FbxDocument document,
            Func<string, FbxControlPointWelding> getWelding)
        {
            if (document == null)
            {
                return null;
            }

            foreach (var mesh in document.Meshes)
            {
                Normalize(mesh, getWelding?.Invoke(mesh.OwnerNode?.Path));
            }

            return document;
        }

        public static FbxMeshGeometry Normalize(FbxMeshGeometry mesh, FbxControlPointWelding welding)
        {
            if (mesh == null || welding == null || !welding.HasGroups)
            {
                return mesh;
            }

            welding.ApplyAverage(mesh.MutableControlPoints);
            welding.ApplyAverage(mesh.MutableControlPointNormals);
            welding.ApplyAverage(mesh.MutableControlPointTangents);

            foreach (var deformer in mesh.Deformers)
            {
                NormalizeDeformer(deformer, mesh.ControlPointCount, welding);
            }

            return mesh;
        }

        private static void NormalizeDeformer(
            FbxDeformer deformer,
            int controlPointCount,
            FbxControlPointWelding welding)
        {
            switch (deformer)
            {
                case FbxBlendShapeDeformer blendShape:
                    NormalizeBlendShapeDeformer(blendShape, controlPointCount, welding);
                    break;
                case FbxSkinDeformer skin:
                    NormalizeSkinDeformer(skin, controlPointCount, welding);
                    break;
            }
        }

        private static void NormalizeBlendShapeDeformer(
            FbxBlendShapeDeformer deformer,
            int controlPointCount,
            FbxControlPointWelding welding)
        {
            foreach (var channel in deformer.Channels)
            {
                foreach (var frame in channel.Frames)
                {
                    NormalizeFrame(frame, controlPointCount, welding);
                }
            }
        }

        private static void NormalizeFrame(
            FbxShapeFrame frame,
            int controlPointCount,
            FbxControlPointWelding welding)
        {
            if (frame == null || controlPointCount <= 0)
            {
                return;
            }

            var deltas = BuildDense(frame.ControlPointIndices, frame.ControlPointDeltas, controlPointCount);
            var normalDeltas = BuildDense(frame.ControlPointIndices, frame.ControlPointNormalDeltas, controlPointCount);
            var tangentDeltas = BuildDense(frame.ControlPointIndices, frame.ControlPointTangentDeltas, controlPointCount);

            welding.ApplyAverage(deltas);
            welding.ApplyAverage(normalDeltas);
            welding.ApplyAverage(tangentDeltas);

            frame.MutableControlPointIndices.Clear();
            frame.MutableControlPointDeltas.Clear();
            frame.MutableControlPointNormalDeltas.Clear();
            frame.MutableControlPointTangentDeltas.Clear();

            for (int i = 0; i < controlPointCount; i++)
            {
                if (deltas[i].IsZero() && normalDeltas[i].IsZero() && tangentDeltas[i].IsZero())
                {
                    continue;
                }

                frame.MutableControlPointIndices.Add(i);
                frame.MutableControlPointDeltas.Add(deltas[i]);
                frame.MutableControlPointNormalDeltas.Add(normalDeltas[i]);
                frame.MutableControlPointTangentDeltas.Add(tangentDeltas[i]);
            }
        }

        private static void NormalizeSkinDeformer(
            FbxSkinDeformer skin,
            int controlPointCount,
            FbxControlPointWelding welding)
        {
            if (skin == null || controlPointCount <= 0)
            {
                return;
            }

            var denseWeights = new List<FbxControlPointBoneWeight>[controlPointCount];
            int count = Math.Min(skin.MutableControlPointWeights.Count, controlPointCount);
            for (int i = 0; i < count; i++)
            {
                denseWeights[i] = new List<FbxControlPointBoneWeight>(skin.MutableControlPointWeights[i]);
            }

            welding.ApplyAverage(
                denseWeights,
                () => new BoneWeightAccumulator(),
                (accumulator, weights) => accumulator.Add(weights),
                (accumulator, mergedCount) => accumulator.Average(mergedCount));

            EnsureControlPointWeightCount(skin.MutableControlPointWeights, controlPointCount);
            for (int i = 0; i < controlPointCount; i++)
            {
                skin.MutableControlPointWeights[i].Clear();
                skin.MutableControlPointWeights[i].AddRange(denseWeights[i] ?? Enumerable.Empty<FbxControlPointBoneWeight>());
            }

            foreach (var cluster in skin.Clusters)
            {
                NormalizeClusterWeights(cluster, controlPointCount, welding);
            }
        }

        private static void NormalizeClusterWeights(
            FbxCluster cluster,
            int controlPointCount,
            FbxControlPointWelding welding)
        {
            if (cluster == null)
            {
                return;
            }

            var denseWeights = new double[controlPointCount];
            int count = Math.Min(cluster.ControlPointIndices.Count, cluster.Weights.Count);
            for (int i = 0; i < count; i++)
            {
                int index = cluster.ControlPointIndices[i];
                if (index >= 0 && index < controlPointCount)
                {
                    denseWeights[index] = cluster.Weights[i];
                }
            }

            welding.ApplyAverage(
                denseWeights,
                () => new DoubleAccumulator(),
                (accumulator, weight) => accumulator.Add(weight),
                (accumulator, mergedCount) => accumulator.Average(mergedCount));

            cluster.MutableControlPointIndices.Clear();
            cluster.MutableWeights.Clear();
            for (int i = 0; i < denseWeights.Length; i++)
            {
                if (denseWeights[i] == 0d)
                {
                    continue;
                }

                cluster.MutableControlPointIndices.Add(i);
                cluster.MutableWeights.Add(denseWeights[i]);
            }
        }

        private static Vector3d[] BuildDense(
            IReadOnlyList<int> indices,
            IReadOnlyList<Vector3d> values,
            int controlPointCount)
        {
            var dense = new Vector3d[controlPointCount];
            int count = Math.Min(indices?.Count ?? 0, values?.Count ?? 0);
            for (int i = 0; i < count; i++)
            {
                int index = indices[i];
                if (index >= 0 && index < controlPointCount)
                {
                    dense[index] = values[i];
                }
            }

            return dense;
        }

        private static void EnsureControlPointWeightCount(
            List<List<FbxControlPointBoneWeight>> weights,
            int count)
        {
            while (weights.Count < count)
            {
                weights.Add(new List<FbxControlPointBoneWeight>());
            }
        }

        private sealed class BoneWeightAccumulator
        {
            private readonly Dictionary<int, float> totals = new();

            public void Add(IEnumerable<FbxControlPointBoneWeight> weights)
            {
                if (weights == null)
                {
                    return;
                }

                foreach (var weight in weights)
                {
                    totals.TryGetValue(weight.BoneIndex, out float total);
                    totals[weight.BoneIndex] = total + weight.Weight;
                }
            }

            public List<FbxControlPointBoneWeight> Average(int count)
            {
                if (count <= 0)
                {
                    return new List<FbxControlPointBoneWeight>();
                }

                return totals
                    .Select(pair => new FbxControlPointBoneWeight(pair.Key, pair.Value / count))
                    .Where(weight => weight.Weight != 0f)
                    .OrderByDescending(weight => weight.Weight)
                    .ToList();
            }
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
