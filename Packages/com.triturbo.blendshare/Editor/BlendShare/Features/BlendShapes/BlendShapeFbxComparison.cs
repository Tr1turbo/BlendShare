using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Fbx.Ufbx;

namespace Triturbo.BlendShare.Features.BlendShapes
{
    internal enum BlendShapeComparisonStatus
    {
        Same,
        New,
        Changed,
        Unavailable
    }

    internal sealed class BlendShapeComparisonEntry
    {
        public string Name;
        public BlendShapeComparisonStatus Status;
    }

    internal sealed class BlendShapeMeshComparison
    {
        public string Path;
        public string Message;
        public IReadOnlyList<BlendShapeComparisonEntry> BlendShapes = Array.Empty<BlendShapeComparisonEntry>();

        public bool TryGetStatus(string blendShapeName, out BlendShapeComparisonStatus status)
        {
            foreach (var blendShape in BlendShapes)
            {
                if (blendShape != null && blendShape.Name == blendShapeName)
                {
                    status = blendShape.Status;
                    return true;
                }
            }

            status = BlendShapeComparisonStatus.Unavailable;
            return false;
        }
    }

    internal sealed class BlendShapeFbxComparison
    {
        private const double WeightEpsilon = 0.000001d;

        private readonly Dictionary<string, BlendShapeMeshComparison> meshesByPath;

        private BlendShapeFbxComparison(IEnumerable<BlendShapeMeshComparison> meshes)
        {
            meshesByPath = (meshes ?? Enumerable.Empty<BlendShapeMeshComparison>())
                .Where(mesh => mesh != null)
                .GroupBy(mesh => MeshFeatureExtractionSession.BuildMeshKey(mesh.Path))
                .ToDictionary(group => group.Key, group => group.First());
        }

        public static BlendShapeFbxComparison Empty { get; } =
            new BlendShapeFbxComparison(Array.Empty<BlendShapeMeshComparison>());

        public bool TryGetMesh(string path, out BlendShapeMeshComparison comparison)
        {
            return meshesByPath.TryGetValue(
                MeshFeatureExtractionSession.BuildMeshKey(path),
                out comparison);
        }

        public static BlendShapeFbxComparison BuildInspectionData(
            FbxInspectionSession session,
            MeshFeatureExtractionOptionsSet options,
            IEnumerable<MeshFeatureExtractionMeshRequest> requests)
        {
            var meshRequests = requests?
                .Where(request => request != null)
                .ToArray() ?? Array.Empty<MeshFeatureExtractionMeshRequest>();
            if (session == null || meshRequests.Length == 0)
            {
                return Empty;
            }

            double deltaTolerance = options != null &&
                                    options.TryGet<BlendShapeExtractionOptions>(out var blendShapeOptions)
                ? blendShapeOptions.DeltaComparisonTolerance
                : BlendShapeExtractionOptions.DefaultDeltaComparisonTolerance;

            var comparisons = meshRequests
                .Select(request => CompareMesh(
                    session,
                    request.Path,
                    options?.GetSourceOffset(request.Path)?.ToFbxMatrix() ?? FbxMatrix4x4.Identity,
                    deltaTolerance))
                .ToArray();
            return new BlendShapeFbxComparison(comparisons);
        }

        private static BlendShapeMeshComparison CompareMesh(
            FbxInspectionSession session,
            string path,
            FbxMatrix4x4 sourceOffset,
            double deltaTolerance)
        {
            var result = new BlendShapeMeshComparison
            {
                Path = MeshNodePath.Normalize(path)
            };

            session.TryGetMeshPair(path, out var pair);
            if (pair?.SourceMesh == null)
            {
                result.Message = "Source FBX mesh was not found.";
                return result;
            }

            var sourceChannels = GetBlendChannels(pair.SourceMesh);
            var originChannels = GetBlendChannelsByName(pair.OriginMesh);
            int controlPointCount = pair.OriginMesh?.ControlPointCount ?? 0;
            result.BlendShapes = sourceChannels
                .Select(channel => new BlendShapeComparisonEntry
                {
                    Name = channel.Name,
                    Status = GetStatus(channel, originChannels, sourceOffset, controlPointCount, deltaTolerance)
                })
                .ToArray();
            result.Message = pair.OriginMesh == null
                ? "Original FBX mesh was not found."
                : null;
            return result;
        }

        private static Dictionary<string, UfbxBlendChannel> GetBlendChannelsByName(UfbxMesh mesh)
        {
            return GetBlendChannels(mesh)
                .ToDictionary(channel => channel.Name, channel => channel, StringComparer.Ordinal);
        }

        private static IReadOnlyList<UfbxBlendChannel> GetBlendChannels(UfbxMesh mesh)
        {
            var result = new List<UfbxBlendChannel>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (mesh == null)
            {
                return result;
            }

            foreach (var channel in mesh.BlendDeformers.SelectMany(deformer => deformer.Channels))
            {
                if (channel == null || string.IsNullOrWhiteSpace(channel.Name) || !seen.Add(channel.Name))
                {
                    continue;
                }

                result.Add(channel);
            }

            return result;
        }

        private static BlendShapeComparisonStatus GetStatus(
            UfbxBlendChannel source,
            IReadOnlyDictionary<string, UfbxBlendChannel> originChannels,
            FbxMatrix4x4 sourceOffset,
            int controlPointCount,
            double deltaTolerance)
        {
            if (source == null)
            {
                return BlendShapeComparisonStatus.Unavailable;
            }

            if (originChannels == null ||
                !originChannels.TryGetValue(source.Name, out var origin) ||
                origin == null)
            {
                return BlendShapeComparisonStatus.New;
            }

            return BlendShapesMatch(source, origin, sourceOffset, controlPointCount, deltaTolerance)
                ? BlendShapeComparisonStatus.Same
                : BlendShapeComparisonStatus.Changed;
        }

        private static bool BlendShapesMatch(
            UfbxBlendChannel source,
            UfbxBlendChannel origin,
            FbxMatrix4x4 sourceOffset,
            int controlPointCount,
            double deltaTolerance)
        {
            if (source.BlendShapes.Count != origin.BlendShapes.Count)
            {
                return false;
            }

            for (int i = 0; i < source.BlendShapes.Count; i++)
            {
                if (!BlendShapeFramesMatch(
                        source.BlendShapes[i],
                        origin.BlendShapes[i],
                        sourceOffset,
                        controlPointCount,
                        deltaTolerance))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool BlendShapeFramesMatch(
            UfbxBlendShape source,
            UfbxBlendShape origin,
            FbxMatrix4x4 sourceOffset,
            int controlPointCount,
            double deltaTolerance)
        {
            if (Math.Abs(source.Weight - origin.Weight) > WeightEpsilon)
            {
                return false;
            }

            var sourceDeltas = CopyDeltas(source, sourceOffset, controlPointCount);
            var originDeltas = CopyDeltas(origin, FbxMatrix4x4.Identity, controlPointCount);
            foreach (var pair in sourceDeltas)
            {
                originDeltas.TryGetValue(pair.Key, out var originDelta);
                if (!BlendShapeDeltaEquals(pair.Value, originDelta, deltaTolerance))
                {
                    return false;
                }
            }

            foreach (var pair in originDeltas)
            {
                if (!sourceDeltas.ContainsKey(pair.Key) &&
                    !BlendShapeDeltaEquals(default, pair.Value, deltaTolerance))
                {
                    return false;
                }
            }

            return true;
        }

        private static Dictionary<int, BlendShapeDelta> CopyDeltas(
            UfbxBlendShape frame,
            FbxMatrix4x4 transform,
            int controlPointCount)
        {
            var result = new Dictionary<int, BlendShapeDelta>();
            if (frame == null || frame.OffsetCount <= 0)
            {
                return result;
            }

            var indices = new int[frame.OffsetCount];
            var values = new double[frame.OffsetCount * 3];
            var normals = new double[frame.OffsetCount * 3];
            if (frame.CopyOffsets(indices, values, normals) == 0)
            {
                return result;
            }

            var deltas = FbxArrayUtility.ToVector3dArray(values);
            var normalDeltas = FbxArrayUtility.ToVector3dArray(normals);
            var baseNormals = frame.OwnerMesh?.GetNormals();
            transform.TryInverse(out var inverseTransform);
            int count = Math.Min(indices.Length, Math.Min(deltas.Length, normalDeltas.Length));
            for (int i = 0; i < count; i++)
            {
                if (indices[i] < 0 || indices[i] >= controlPointCount)
                {
                    continue;
                }

                result[indices[i]] = new BlendShapeDelta(
                    TransformVector(transform, deltas[i]),
                    TransformNormalDelta(
                        transform,
                        inverseTransform,
                        indices[i] < (baseNormals?.Length ?? 0) ? baseNormals[indices[i]] : Vector3d.zero,
                        normalDeltas[i]));
            }

            return result;
        }

        private static bool BlendShapeDeltaEquals(
            BlendShapeDelta left,
            BlendShapeDelta right,
            double tolerance)
        {
            return VectorEquals(left.Position, right.Position, tolerance) &&
                   VectorEquals(left.Normal, right.Normal, tolerance);
        }

        private static bool VectorEquals(Vector3d left, Vector3d right, double tolerance)
        {
            return Math.Abs(left.x - right.x) <= tolerance &&
                   Math.Abs(left.y - right.y) <= tolerance &&
                   Math.Abs(left.z - right.z) <= tolerance;
        }

        private static Vector3d TransformVector(FbxMatrix4x4 matrix, Vector3d vector)
        {
            if (matrix.IsIdentity)
            {
                return vector;
            }

            return new Vector3d(
                vector.x * matrix[0, 0] + vector.y * matrix[1, 0] + vector.z * matrix[2, 0],
                vector.x * matrix[0, 1] + vector.y * matrix[1, 1] + vector.z * matrix[2, 1],
                vector.x * matrix[0, 2] + vector.y * matrix[1, 2] + vector.z * matrix[2, 2]);
        }

        private static Vector3d TransformNormalDelta(
            FbxMatrix4x4 matrix,
            FbxMatrix4x4 inverseMatrix,
            Vector3d baseNormal,
            Vector3d normalDelta)
        {
            if (matrix.IsIdentity)
            {
                return normalDelta;
            }

            if (baseNormal.sqrMagnitude <= Vector3d.Epsilon)
            {
                return TransformNormal(inverseMatrix, normalDelta);
            }

            return TransformNormal(inverseMatrix, baseNormal + normalDelta) -
                   TransformNormal(inverseMatrix, baseNormal);
        }

        private static Vector3d TransformNormal(FbxMatrix4x4 inverseMatrix, Vector3d normal)
        {
            return new Vector3d(
                normal.x * inverseMatrix[0, 0] + normal.y * inverseMatrix[0, 1] + normal.z * inverseMatrix[0, 2],
                normal.x * inverseMatrix[1, 0] + normal.y * inverseMatrix[1, 1] + normal.z * inverseMatrix[1, 2],
                normal.x * inverseMatrix[2, 0] + normal.y * inverseMatrix[2, 1] + normal.z * inverseMatrix[2, 2]).normalized;
        }

        private readonly struct BlendShapeDelta
        {
            public readonly Vector3d Position;
            public readonly Vector3d Normal;

            public BlendShapeDelta(Vector3d position, Vector3d normal)
            {
                Position = position;
                Normal = normal;
            }
        }
    }
}
