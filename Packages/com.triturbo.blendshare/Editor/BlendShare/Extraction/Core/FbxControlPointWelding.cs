using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Triturbo.BlendShare.Fbx;
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
        public IReadOnlyList<int[]> Groups => groups;

        private FbxControlPointWelding(IEnumerable<int[]> groups)
        {
            this.groups = groups?
                .Where(group => group != null && group.Length > 1)
                .Select(group => group.Distinct().ToArray())
                .Where(group => group.Length > 1)
                .ToArray() ?? System.Array.Empty<int[]>();
        }
        /// <summary>
        /// Predicts Unity's pre-split control-point weld groups using default ModelImporter weld settings.
        /// </summary>
        public static FbxControlPointWelding FromFbxMesh(IFbxMeshGeometry mesh)
        {
            return FromFbxMesh(mesh, FbxUnityWeldOptions.Default);
        }

        /// <summary>
        /// Predicts Unity's pre-split control-point weld groups using the given ModelImporter weld settings.
        /// </summary>
        public static FbxControlPointWelding FromFbxMesh(IFbxMeshGeometry mesh, FbxUnityWeldOptions options)
        {
            if (mesh == null || mesh.ControlPointCount == 0 || !options.WeldVertices)
            {
                return Empty;
            }

            var stopwatch = Stopwatch.StartNew();
            var prediction = FbxUnityWeldPredictor.Predict(mesh, options);
            stopwatch.Stop();
            Debug.Log($"[BlendShare] Build ufbx welding groups: {prediction.MultiMemberGroups.Count} groups in {stopwatch.ElapsedMilliseconds} ms");
            return prediction.HasMultiMemberGroups
                ? new FbxControlPointWelding(prediction.MultiMemberGroups)
                : Empty;
        }
#if ENABLE_FBX_SDK
        /// <summary>
        /// Builds FBX control-point weld groups from an Autodesk FBX SDK mesh using default weld settings.
        /// </summary>
        public static FbxControlPointWelding FromMesh(FbxMesh mesh)
        {
            return FromMesh(mesh, FbxUnityWeldOptions.Default);
        }

        /// <summary>
        /// Builds FBX control-point weld groups from an Autodesk FBX SDK mesh respecting the given weld settings.
        /// </summary>
        public static FbxControlPointWelding FromMesh(FbxMesh mesh, FbxUnityWeldOptions options)
        {
            if (mesh == null || !options.WeldVertices)
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

            if (options.ImportBlendShapes)
            {
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
            }

            stopwatch.Stop();
            Debug.Log($"[BlendShare] Build FBX welding groups: {groups.Count} groups in {stopwatch.ElapsedMilliseconds} ms");
            return groups.Count > 0 ? new FbxControlPointWelding(groups.Select(group => group.ToArray())) : Empty;
        }
#endif

        public bool ApplyAverage<TValue, TAccumulator>(
            IList<TValue> values,
            System.Func<TAccumulator> createAccumulator,
            System.Action<TAccumulator, TValue> addValue,
            System.Func<TAccumulator, int, TValue> getAverage)
        {
            if (groups.Length == 0 || values == null || createAccumulator == null || addValue == null || getAverage == null)
            {
                return false;
            }

            foreach (var group in groups)
            {
                var accumulator = createAccumulator();
                int mergedCount = 0;

                foreach (int index in group)
                {
                    if (index < 0 || index >= values.Count)
                    {
                        continue;
                    }

                    addValue(accumulator, values[index]);
                    mergedCount++;
                }

                if (mergedCount == 0)
                {
                    continue;
                }

                var average = getAverage(accumulator, mergedCount);
                foreach (int index in group)
                {
                    if (index >= 0 && index < values.Count)
                    {
                        values[index] = average;
                    }
                }
            }

            return true;
        }

        public FbxIndexGroup[] CreateIndexGroups(int controlPointCount)
        {
            var result = new FbxIndexGroup[System.Math.Max(0, controlPointCount)];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = new FbxIndexGroup { m_Indices = new[] { i } };
            }

            foreach (var group in groups)
            {
                if (group == null || group.Length == 0)
                {
                    continue;
                }

                var normalized = group
                    .Where(index => index >= 0 && index < result.Length)
                    .Distinct()
                    .OrderBy(index => index)
                    .ToArray();
                foreach (int index in normalized)
                {
                    result[index] = new FbxIndexGroup { m_Indices = normalized };
                }
            }

            return result;
        }

        public int[] CreateControlPointGroupIds(int controlPointCount)
        {
            var ids = new int[System.Math.Max(0, controlPointCount)];
            for (int i = 0; i < ids.Length; i++)
            {
                ids[i] = i;
            }

            foreach (var group in groups)
            {
                if (group == null || group.Length == 0)
                {
                    continue;
                }

                int groupId = group.Where(index => index >= 0 && index < ids.Length).DefaultIfEmpty(-1).Min();
                if (groupId < 0)
                {
                    continue;
                }

                foreach (int index in group)
                {
                    if (index >= 0 && index < ids.Length)
                    {
                        ids[index] = groupId;
                    }
                }
            }

            return ids;
        }

        public bool ApplyAverage(Vector3d[] values)
        {
            return ApplyAverage(
                values,
                () => new Vector3dAccumulator(),
                (accumulator, value) => accumulator.Add(value),
                (accumulator, count) => accumulator.Average(count));
        }

#if ENABLE_FBX_SDK
        public bool ApplyAverage(FbxVector4[] values)
        {
            return ApplyAverage(
                values,
                () => new FbxVector4Accumulator(),
                (accumulator, value) => accumulator.Add(value),
                (accumulator, count) => accumulator.Average(count));
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

        private sealed class Vector3dAccumulator
        {
            private Vector3d sum = Vector3d.zero;

            public void Add(Vector3d value)
            {
                sum += value;
            }

            public Vector3d Average(int count)
            {
                return count > 0 ? sum / count : Vector3d.zero;
            }
        }

#if ENABLE_FBX_SDK
        private sealed class FbxVector4Accumulator
        {
            private FbxVector4 sum = new(0, 0, 0, 0);

            public void Add(FbxVector4 value)
            {
                sum += value;
            }

            public FbxVector4 Average(int count)
            {
                return count > 0 ? sum / count : new FbxVector4(0, 0, 0, 0);
            }
        }
#endif
    }
}
