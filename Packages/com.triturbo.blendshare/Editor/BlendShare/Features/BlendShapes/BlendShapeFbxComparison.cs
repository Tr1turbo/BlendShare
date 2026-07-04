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
        private const double Epsilon = 0.000001d;

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
            IEnumerable<MeshFeatureExtractionMeshRequest> requests)
        {
            var meshRequests = requests?
                .Where(request => request != null)
                .ToArray() ?? Array.Empty<MeshFeatureExtractionMeshRequest>();
            if (session == null || meshRequests.Length == 0)
            {
                return Empty;
            }

            var comparisons = meshRequests
                .Select(request => CompareMesh(session, request.Path))
                .ToArray();
            return new BlendShapeFbxComparison(comparisons);
        }

        private static BlendShapeMeshComparison CompareMesh(
            FbxInspectionSession session,
            string path)
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
            result.BlendShapes = sourceChannels
                .Select(channel => new BlendShapeComparisonEntry
                {
                    Name = channel.Name,
                    Status = GetStatus(channel, originChannels)
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
            IReadOnlyDictionary<string, UfbxBlendChannel> originChannels)
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

            return BlendShapesMatch(source, origin)
                ? BlendShapeComparisonStatus.Same
                : BlendShapeComparisonStatus.Changed;
        }

        private static bool BlendShapesMatch(UfbxBlendChannel source, UfbxBlendChannel origin)
        {
            if (source.BlendShapes.Count != origin.BlendShapes.Count)
            {
                return false;
            }

            for (int i = 0; i < source.BlendShapes.Count; i++)
            {
                if (!BlendShapeFramesMatch(source.BlendShapes[i], origin.BlendShapes[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool BlendShapeFramesMatch(UfbxBlendShape source, UfbxBlendShape origin)
        {
            if (Math.Abs(source.Weight - origin.Weight) > Epsilon ||
                source.OffsetCount != origin.OffsetCount)
            {
                return false;
            }

            var sourceDeltas = CopyDeltas(source);
            var originDeltas = CopyDeltas(origin);
            if (sourceDeltas.Count != originDeltas.Count)
            {
                return false;
            }

            foreach (var pair in sourceDeltas)
            {
                if (!originDeltas.TryGetValue(pair.Key, out var originDelta) ||
                    !BlendShapeDeltaEquals(pair.Value, originDelta))
                {
                    return false;
                }
            }

            return true;
        }

        private static Dictionary<int, BlendShapeDelta> CopyDeltas(UfbxBlendShape frame)
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
            int count = Math.Min(indices.Length, Math.Min(deltas.Length, normalDeltas.Length));
            for (int i = 0; i < count; i++)
            {
                result[indices[i]] = new BlendShapeDelta(deltas[i], normalDeltas[i]);
            }

            return result;
        }

        private static bool BlendShapeDeltaEquals(BlendShapeDelta left, BlendShapeDelta right)
        {
            return VectorEquals(left.Position, right.Position) &&
                   VectorEquals(left.Normal, right.Normal);
        }

        private static bool VectorEquals(Vector3d left, Vector3d right)
        {
            return Math.Abs(left.x - right.x) <= Epsilon &&
                   Math.Abs(left.y - right.y) <= Epsilon &&
                   Math.Abs(left.z - right.z) <= Epsilon;
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
