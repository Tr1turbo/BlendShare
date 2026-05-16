using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Triturbo.Fbx;
using Debug = UnityEngine.Debug;

#if ENABLE_FBX_SDK
using Autodesk.Fbx;
#endif

namespace Triturbo.BlendShare.Core
{
    /// <summary>
    /// Shared FBX control-point welding policy for feature extraction.
    /// Feature extractors pass per-control-point values in and receive normalized values back.
    /// </summary>
    public sealed class FbxControlPointWelding
    {
        public static readonly FbxControlPointWelding Empty = new(System.Array.Empty<int[]>());

        private readonly int[][] groups;

        public int GroupCount => groups.Length;
        public bool HasGroups => groups.Length > 0;

        private FbxControlPointWelding(IEnumerable<int[]> groups)
        {
            this.groups = groups?
                .Where(group => group != null && group.Length > 1)
                .Select(group => group.Distinct().ToArray())
                .Where(group => group.Length > 1)
                .ToArray() ?? System.Array.Empty<int[]>();
        }

        public static FbxControlPointWelding FromReaderMesh(FbxMeshGeometry mesh)
        {
            if (mesh == null || mesh.ControlPoints.Count == 0)
            {
                return Empty;
            }

            var stopwatch = Stopwatch.StartNew();
            var controlPointPosition = new Dictionary<Vector3d, List<int>>();

            for (int i = 0; i < mesh.ControlPoints.Count; i++)
            {
                Vector3d position = mesh.ControlPoints[i];
                if (!controlPointPosition.TryGetValue(position, out var indices))
                {
                    indices = new List<int>();
                    controlPointPosition[position] = indices;
                }

                indices.Add(i);
            }

            var groups = controlPointPosition
                .Where(kv => kv.Value.Count > 1)
                .Select(kv => kv.Value)
                .ToList();

            foreach (var deformer in mesh.BlendShapeDeformers)
            {
                foreach (var channel in deformer.Channels)
                {
                    foreach (var frame in channel.Frames)
                    {
                        groups = GroupWithShape(groups, mesh.ControlPoints, frame);
                    }
                }
            }

            stopwatch.Stop();
            Debug.Log($"[BlendShare] Build FBX reader welding groups: {groups.Count} groups in {stopwatch.ElapsedMilliseconds} ms");
            return groups.Count > 0 ? new FbxControlPointWelding(groups.Select(group => group.ToArray())) : Empty;
        }

#if ENABLE_FBX_SDK
        public static FbxControlPointWelding FromMesh(FbxMesh mesh)
        {
            if (mesh == null)
            {
                return Empty;
            }

            var stopwatch = Stopwatch.StartNew();
            int count = mesh.GetControlPointsCount();
            var controlPointPosition = new Dictionary<FbxVector4, List<int>>();

            for (int i = 0; i < count; i++)
            {
                FbxVector4 position = mesh.GetControlPointAt(i);
                if (!controlPointPosition.TryGetValue(position, out var indices))
                {
                    indices = new List<int>();
                    controlPointPosition[position] = indices;
                }

                indices.Add(i);
            }

            var groups = controlPointPosition
                .Where(kv => kv.Value.Count > 1)
                .Select(kv => kv.Value)
                .ToList();

            for (int deformerIndex = 0; deformerIndex < mesh.GetDeformerCount(Autodesk.Fbx.FbxDeformer.EDeformerType.eBlendShape); deformerIndex++)
            {
                var deformer = mesh.GetBlendShapeDeformer(deformerIndex);
                for (int channelIndex = 0; channelIndex < deformer.GetBlendShapeChannelCount(); channelIndex++)
                {
                    var channel = deformer.GetBlendShapeChannel(channelIndex);
                    for (int shapeIndex = 0; shapeIndex < channel.GetTargetShapeCount(); shapeIndex++)
                    {
                        groups = GroupWithShape(groups, channel.GetTargetShape(shapeIndex));
                    }
                }
            }

            stopwatch.Stop();
            Debug.Log($"[BlendShare] Build FBX welding groups: {groups.Count} groups in {stopwatch.ElapsedMilliseconds} ms");
            return groups.Count > 0 ? new FbxControlPointWelding(groups.Select(group => group.ToArray())) : Empty;
        }
#endif

        public bool ApplyAverage(Vector3d[] values)
        {
            if (groups.Length == 0 || values == null)
            {
                return false;
            }

            foreach (var group in groups)
            {
                var average = Vector3d.zero;
                int mergedCount = 0;

                foreach (int index in group)
                {
                    if (index < 0 || index >= values.Length)
                    {
                        continue;
                    }

                    average += values[index];
                    mergedCount++;
                }

                if (mergedCount == 0)
                {
                    continue;
                }

                average /= mergedCount;
                foreach (int index in group)
                {
                    if (index >= 0 && index < values.Length)
                    {
                        values[index] = average;
                    }
                }
            }

            return true;
        }

        private static List<List<int>> GroupWithShape(
            List<List<int>> groups,
            IReadOnlyList<Vector3d> basePositions,
            FbxShapeFrame shapeFrame)
        {
            var newGroups = new List<List<int>>();
            foreach (var group in groups)
            {
                var newGroup = new Dictionary<Vector3d, List<int>>();
                foreach (int index in group)
                {
                    var vertex = GetTargetPosition(basePositions, shapeFrame, index);
                    if (!newGroup.TryGetValue(vertex, out var indices))
                    {
                        indices = new List<int>();
                        newGroup[vertex] = indices;
                    }

                    indices.Add(index);
                }

                newGroups.AddRange(newGroup
                    .Where(kv => kv.Value.Count > 1)
                    .Select(kv => kv.Value)
                    .ToList());
            }

            return newGroups;
        }

        private static Vector3d GetTargetPosition(
            IReadOnlyList<Vector3d> basePositions,
            FbxShapeFrame shapeFrame,
            int controlPointIndex)
        {
            var basePosition = controlPointIndex >= 0 && controlPointIndex < (basePositions?.Count ?? 0)
                ? basePositions[controlPointIndex]
                : Vector3d.zero;

            var indices = shapeFrame?.ControlPointIndices ?? System.Array.Empty<int>();
            var deltas = shapeFrame?.ControlPointDeltas ?? System.Array.Empty<Vector3d>();
            int count = System.Math.Min(indices.Count, deltas.Count);
            for (int i = 0; i < count; i++)
            {
                if (indices[i] == controlPointIndex)
                {
                    return basePosition + deltas[i];
                }
            }

            return basePosition;
        }

#if ENABLE_FBX_SDK
        public bool ApplyAverage(FbxVector4[] values)
        {
            if (groups.Length == 0 || values == null)
            {
                return false;
            }

            foreach (var group in groups)
            {
                var average = new FbxVector4(0, 0, 0, 0);
                int mergedCount = 0;

                foreach (int index in group)
                {
                    if (index < 0 || index >= values.Length)
                    {
                        continue;
                    }

                    average += values[index];
                    mergedCount++;
                }

                if (mergedCount == 0)
                {
                    continue;
                }

                average /= mergedCount;
                foreach (int index in group)
                {
                    if (index >= 0 && index < values.Length)
                    {
                        values[index] = average;
                    }
                }
            }

            return true;
        }

        private static List<List<int>> GroupWithShape(List<List<int>> groups, FbxShape targetShape)
        {
            var newGroups = new List<List<int>>();
            foreach (var group in groups)
            {
                var newGroup = new Dictionary<FbxVector4, List<int>>();
                foreach (int index in group)
                {
                    var vertex = targetShape.GetControlPointAt(index);
                    if (!newGroup.TryGetValue(vertex, out var indices))
                    {
                        indices = new List<int>();
                        newGroup[vertex] = indices;
                    }

                    indices.Add(index);
                }

                newGroups.AddRange(newGroup
                    .Where(kv => kv.Value.Count > 1)
                    .Select(kv => kv.Value)
                    .ToList());
            }

            return newGroups;
        }
#endif
    }
}
