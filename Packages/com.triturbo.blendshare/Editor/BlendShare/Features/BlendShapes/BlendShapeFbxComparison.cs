using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Fbx;

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
        private const double FrameWeightComparisonTolerance = 0.000001d;

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
            Vector3d[] cachedSourceBaseNormals = null;
            bool sourceBaseNormalsLoaded = false;
            result.BlendShapes = sourceChannels
                .Select(channel => new BlendShapeComparisonEntry
                {
                    Name = channel.Name,
                    Status = GetStatus(
                        channel,
                        originChannels,
                        sourceOffset,
                        controlPointCount,
                        deltaTolerance,
                        pair.SourceMesh,
                        ref cachedSourceBaseNormals,
                        ref sourceBaseNormalsLoaded)
                })
                .ToArray();
            result.Message = pair.OriginMesh == null
                ? "Original FBX mesh was not found."
                : null;
            return result;
        }

        private static Dictionary<string, IFbxBlendChannel> GetBlendChannelsByName(IFbxMeshGeometry mesh)
        {
            return GetBlendChannels(mesh)
                .ToDictionary(channel => channel.Name, channel => channel, StringComparer.Ordinal);
        }

        private static IReadOnlyList<IFbxBlendChannel> GetBlendChannels(IFbxMeshGeometry mesh)
        {
            var result = new List<IFbxBlendChannel>();
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
            IFbxBlendChannel source,
            IReadOnlyDictionary<string, IFbxBlendChannel> originChannels,
            FbxMatrix4x4 sourceOffset,
            int controlPointCount,
            double deltaTolerance,
            IFbxMeshGeometry sourceMesh,
            ref Vector3d[] cachedSourceBaseNormals,
            ref bool sourceBaseNormalsLoaded)
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

            return BlendShapesMatch(
                source,
                origin,
                sourceOffset,
                controlPointCount,
                deltaTolerance,
                sourceMesh,
                ref cachedSourceBaseNormals,
                ref sourceBaseNormalsLoaded)
                ? BlendShapeComparisonStatus.Same
                : BlendShapeComparisonStatus.Changed;
        }

        private static bool BlendShapesMatch(
            IFbxBlendChannel source,
            IFbxBlendChannel origin,
            FbxMatrix4x4 sourceOffset,
            int controlPointCount,
            double deltaTolerance,
            IFbxMeshGeometry sourceMesh,
            ref Vector3d[] cachedSourceBaseNormals,
            ref bool sourceBaseNormalsLoaded)
        {
            if (source.Keyframes.Count != origin.Keyframes.Count)
            {
                return false;
            }

            for (int i = 0; i < source.Keyframes.Count; i++)
            {
                if (!BlendShapeFramesMatch(
                        source.Keyframes[i],
                        origin.Keyframes[i],
                        sourceOffset,
                        controlPointCount,
                        deltaTolerance,
                        sourceMesh,
                        ref cachedSourceBaseNormals,
                        ref sourceBaseNormalsLoaded))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool BlendShapeFramesMatch(
            IFbxBlendKeyframe source,
            IFbxBlendKeyframe origin,
            FbxMatrix4x4 sourceOffset,
            int controlPointCount,
            double deltaTolerance,
            IFbxMeshGeometry sourceMesh,
            ref Vector3d[] cachedSourceBaseNormals,
            ref bool sourceBaseNormalsLoaded)
        {
            if (source == null || origin == null)
            {
                return source == null && origin == null;
            }

            if (!FbxSourceSpaceTransform.TryCreate(sourceOffset, out var sourceSpaceTransform))
            {
                return false;
            }

            if (Math.Abs(source.TargetWeight - origin.TargetWeight) > FrameWeightComparisonTolerance)
            {
                return false;
            }

            Vector3d[] sourceBaseNormals = null;
            if (!sourceSpaceTransform.IsIdentity && source.Shape?.HasNormalOffsets == true)
            {
                if (!sourceBaseNormalsLoaded)
                {
                    // Cache representative per-control-point normals once per mesh instead of
                    // re-reading them for every blendshape keyframe during inspection.
                    cachedSourceBaseNormals = sourceMesh?.GetFirstCornerNormals() ?? Array.Empty<Vector3d>();
                    sourceBaseNormalsLoaded = true;
                }

                sourceBaseNormals = cachedSourceBaseNormals;
            }

            var sourceDeltas = CopyDeltas(source.Shape, sourceSpaceTransform, controlPointCount, sourceBaseNormals);
            var originDeltas = CopyDeltas(origin.Shape, FbxSourceSpaceTransform.Identity, controlPointCount, null);
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
            IFbxBlendShape frame,
            FbxSourceSpaceTransform spaceTransform,
            int controlPointCount,
            Vector3d[] baseNormals)
        {
            var result = new Dictionary<int, BlendShapeDelta>();
            if (frame == null || frame.OffsetCount <= 0)
            {
                return result;
            }

            var indices = new int[frame.OffsetCount];
            var deltas = new Vector3d[frame.OffsetCount];
            var normalDeltas = frame.HasNormalOffsets
                ? new Vector3d[frame.OffsetCount]
                : Array.Empty<Vector3d>();
            if (!frame.TryCopyOffsets(indices, deltas, normalDeltas, Span<double>.Empty, out int count) || count == 0)
            {
                return result;
            }

            bool isIdentity = spaceTransform.IsIdentity;

            int safeCount = Math.Min(count, Math.Min(indices.Length, deltas.Length));
            int baseNormalLength = baseNormals?.Length ?? 0;
            for (int i = 0; i < safeCount; i++)
            {
                int cp = indices[i];
                if (cp < 0 || cp >= controlPointCount)
                {
                    continue;
                }

                Vector3d positionDelta = isIdentity ? deltas[i] : spaceTransform.TransformVector(deltas[i]);
                Vector3d rawNormalDelta = i < normalDeltas.Length ? normalDeltas[i] : Vector3d.zero;
                Vector3d normalDelta = isIdentity
                    ? rawNormalDelta
                    : spaceTransform.TransformNormalDelta(
                        cp < baseNormalLength ? baseNormals[cp] : Vector3d.zero,
                        rawNormalDelta);
                result[cp] = new BlendShapeDelta(positionDelta, normalDelta);
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
