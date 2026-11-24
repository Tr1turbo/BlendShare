using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Fbx;

namespace Triturbo.BlendShare.Pipeline.Inspection
{
    public enum BlendShapeComparisonStatus
    {
        Same = 0,
        New = 1,
        Changed = 2,
        Unavailable = 3
    }

    public sealed class BlendShapeComparisonEntry
    {
        public string Name { get; set; } = string.Empty;
        public BlendShapeComparisonStatus Status { get; set; }
        public int KeyframeCount { get; set; }
    }

    public sealed class BlendShapeMeshComparison
    {
        public string Path { get; set; } = string.Empty;
        public string Message { get; set; }
        public IReadOnlyList<BlendShapeComparisonEntry> BlendShapes { get; set; } = Array.Empty<BlendShapeComparisonEntry>();

        public bool TryGetStatus(string blendShapeName, out BlendShapeComparisonStatus status)
        {
            for (int i = 0; i < BlendShapes.Count; i++)
            {
                var blendShape = BlendShapes[i];
                if (blendShape != null && string.Equals(blendShape.Name, blendShapeName, StringComparison.Ordinal))
                {
                    status = blendShape.Status;
                    return true;
                }
            }

            status = BlendShapeComparisonStatus.Unavailable;
            return false;
        }
    }

    /// <summary>
    /// Compares Source vs Original FBX blendshapes in Origin FBX native local space using the exact
    /// <c>L = W * SourceToOriginRawOffset</c> transform and unit-scaled physical-meter tolerances.
    /// </summary>
    public sealed class BlendShapeFbxComparison
    {
        public const double DefaultDeltaComparisonToleranceMeters = 0.00001d;
        private const double k_FrameWeightComparisonTolerance = 0.000001d;
        private const double k_NormalDeltaComparisonTolerance = 0.0001d;

        private readonly Dictionary<string, BlendShapeMeshComparison> m_MeshesByPath;

        private BlendShapeFbxComparison(IEnumerable<BlendShapeMeshComparison> meshes)
        {
            m_MeshesByPath = new Dictionary<string, BlendShapeMeshComparison>(StringComparer.Ordinal);
            foreach (var mesh in meshes ?? Enumerable.Empty<BlendShapeMeshComparison>())
            {
                if (mesh == null) continue;
                string key = FbxNameUtility.NormalizePath(mesh.Path ?? string.Empty);
                if (!m_MeshesByPath.ContainsKey(key))
                {
                    m_MeshesByPath.Add(key, mesh);
                }
            }
        }

        public static BlendShapeFbxComparison Empty { get; } =
            new BlendShapeFbxComparison(Array.Empty<BlendShapeMeshComparison>());

        public bool TryGetMesh(string path, out BlendShapeMeshComparison comparison)
        {
            return m_MeshesByPath.TryGetValue(FbxNameUtility.NormalizePath(path ?? string.Empty), out comparison);
        }

        public static BlendShapeFbxComparison BuildInspectionData(
            FbxInspectionSession session,
            IReadOnlyList<string> meshPaths,
            Func<string, MeshFeatureSourceOffset> sourceOffsetResolver = null,
            double deltaToleranceMeters = DefaultDeltaComparisonToleranceMeters)
        {
            if (session == null || meshPaths == null || meshPaths.Count == 0)
            {
                return Empty;
            }

            double originUnitMeters = CanonicalMeshSpace.IsValidUnitMeters(session.OriginUnitMeters)
                ? session.OriginUnitMeters
                : 1d;
            double safeMetersTol = deltaToleranceMeters > 0d ? deltaToleranceMeters : DefaultDeltaComparisonToleranceMeters;
            double positionToleranceOriginUnits = safeMetersTol / originUnitMeters;

            var comparisons = new List<BlendShapeMeshComparison>(meshPaths.Count);
            for (int i = 0; i < meshPaths.Count; i++)
            {
                string path = meshPaths[i];
                if (string.IsNullOrWhiteSpace(path)) continue;
                var rawOffset = sourceOffsetResolver?.Invoke(path)?.ToFbxMatrix() ?? FbxMatrix4x4.Identity;
                comparisons.Add(CompareMesh(session, path, rawOffset, positionToleranceOriginUnits, k_NormalDeltaComparisonTolerance));
            }

            return new BlendShapeFbxComparison(comparisons);
        }

        private static BlendShapeMeshComparison CompareMesh(
            FbxInspectionSession session,
            string path,
            FbxMatrix4x4 sourceToOriginRawOffset,
            double positionTolerance,
            double normalTolerance)
        {
            string normalizedPath = FbxNameUtility.NormalizePath(path);
            var result = new BlendShapeMeshComparison
            {
                Path = normalizedPath
            };

            session.TryGetMeshPair(normalizedPath, out var pair);
            if (pair?.SourceMesh == null)
            {
                result.Message = "Source FBX mesh was not found.";
                return result;
            }

            bool hasValidTransform = session.TryBuildSourceMeshToOriginTransform(sourceToOriginRawOffset, out var sourceToOrigin);
            var sourceChannels = GetBlendChannels(pair.SourceMesh);
            var originChannels = GetBlendChannelsByName(pair.OriginMesh);
            int controlPointCount = pair.OriginMesh?.ControlPointCount ?? 0;

            Vector3d[] cachedSourceBaseNormals = null;
            bool sourceBaseNormalsLoaded = false;

            var entries = new BlendShapeComparisonEntry[sourceChannels.Count];
            for (int i = 0; i < sourceChannels.Count; i++)
            {
                var channel = sourceChannels[i];
                var status = pair.OriginMesh == null
                    ? BlendShapeComparisonStatus.New
                    : !hasValidTransform
                        ? BlendShapeComparisonStatus.Unavailable
                        : GetStatus(
                            channel,
                            originChannels,
                            sourceToOrigin,
                            controlPointCount,
                            positionTolerance,
                            normalTolerance,
                            pair.SourceMesh,
                            ref cachedSourceBaseNormals,
                            ref sourceBaseNormalsLoaded);

                entries[i] = new BlendShapeComparisonEntry
                {
                    Name = channel.Name,
                    Status = status,
                    KeyframeCount = channel.Keyframes?.Count ?? 0
                };
            }

            result.BlendShapes = entries;
            result.Message = pair.OriginMesh == null
                ? "Original FBX mesh was not found."
                : !hasValidTransform
                    ? "Source alignment offset is singular."
                    : null;
            return result;
        }

        private static Dictionary<string, IFbxBlendChannel> GetBlendChannelsByName(IFbxMeshGeometry mesh)
        {
            var map = new Dictionary<string, IFbxBlendChannel>(StringComparer.Ordinal);
            var channels = GetBlendChannels(mesh);
            for (int i = 0; i < channels.Count; i++)
            {
                map[channels[i].Name] = channels[i];
            }

            return map;
        }

        private static IReadOnlyList<IFbxBlendChannel> GetBlendChannels(IFbxMeshGeometry mesh)
        {
            var result = new List<IFbxBlendChannel>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (mesh?.BlendDeformers == null)
            {
                return result;
            }

            for (int d = 0; d < mesh.BlendDeformers.Count; d++)
            {
                var deformer = mesh.BlendDeformers[d];
                if (deformer?.Channels == null) continue;
                for (int c = 0; c < deformer.Channels.Count; c++)
                {
                    var channel = deformer.Channels[c];
                    if (channel == null || string.IsNullOrWhiteSpace(channel.Name) || !seen.Add(channel.Name))
                    {
                        continue;
                    }

                    result.Add(channel);
                }
            }

            return result;
        }

        private static BlendShapeComparisonStatus GetStatus(
            IFbxBlendChannel source,
            IReadOnlyDictionary<string, IFbxBlendChannel> originChannels,
            FbxAffineSpaceTransform sourceToOrigin,
            int controlPointCount,
            double positionTolerance,
            double normalTolerance,
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
                sourceToOrigin,
                controlPointCount,
                positionTolerance,
                normalTolerance,
                sourceMesh,
                ref cachedSourceBaseNormals,
                ref sourceBaseNormalsLoaded)
                ? BlendShapeComparisonStatus.Same
                : BlendShapeComparisonStatus.Changed;
        }

        private static bool BlendShapesMatch(
            IFbxBlendChannel source,
            IFbxBlendChannel origin,
            FbxAffineSpaceTransform sourceToOrigin,
            int controlPointCount,
            double positionTolerance,
            double normalTolerance,
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
                        sourceToOrigin,
                        controlPointCount,
                        positionTolerance,
                        normalTolerance,
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
            FbxAffineSpaceTransform sourceToOrigin,
            int controlPointCount,
            double positionTolerance,
            double normalTolerance,
            IFbxMeshGeometry sourceMesh,
            ref Vector3d[] cachedSourceBaseNormals,
            ref bool sourceBaseNormalsLoaded)
        {
            if (source == null || origin == null)
            {
                return source == null && origin == null;
            }

            if (Math.Abs(source.TargetWeight - origin.TargetWeight) > k_FrameWeightComparisonTolerance)
            {
                return false;
            }

            Vector3d[] sourceBaseNormals = null;
            if (!sourceToOrigin.IsIdentity && source.Shape?.HasNormalOffsets == true)
            {
                if (!sourceBaseNormalsLoaded)
                {
                    cachedSourceBaseNormals = sourceMesh?.GetFirstCornerNormals() ?? Array.Empty<Vector3d>();
                    sourceBaseNormalsLoaded = true;
                }

                sourceBaseNormals = cachedSourceBaseNormals;
            }

            var sourceDeltas = CopyDeltas(source.Shape, sourceToOrigin, controlPointCount, sourceBaseNormals);
            var originDeltas = CopyDeltas(origin.Shape, FbxAffineSpaceTransform.Identity, controlPointCount, null);

            foreach (var pair in sourceDeltas)
            {
                originDeltas.TryGetValue(pair.Key, out var originDelta);
                if (!BlendShapeDeltaEquals(pair.Value, originDelta, positionTolerance, normalTolerance))
                {
                    return false;
                }
            }

            foreach (var pair in originDeltas)
            {
                if (!sourceDeltas.ContainsKey(pair.Key) &&
                    !BlendShapeDeltaEquals(default, pair.Value, positionTolerance, normalTolerance))
                {
                    return false;
                }
            }

            return true;
        }

        private static Dictionary<int, BlendShapeDelta> CopyDeltas(
            IFbxBlendShape frame,
            FbxAffineSpaceTransform spaceTransform,
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
            double positionTolerance,
            double normalTolerance)
        {
            return VectorEquals(left.Position, right.Position, positionTolerance) &&
                   VectorEquals(left.Normal, right.Normal, normalTolerance);
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
