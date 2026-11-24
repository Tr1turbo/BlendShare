using System;
using System.Collections.Generic;
using System.Globalization;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Model.Features;
using Triturbo.BlendShare.Model.Serialization;
using Triturbo.BlendShare.Primitives;

namespace Triturbo.BlendShare.Pipeline.Extraction
{
    public enum BlendShapeBaseMode
    {
        /// <summary>Preserves deltas relative to the source mesh's own base shape (in stored Origin FBX local space).</summary>
        PreserveSourceRelative = 0,

        /// <summary>Rebases target shapes onto the origin mesh's base positions: (sourceBase + delta) - originBase.</summary>
        RebaseOntoOriginal = 1
    }

    /// <summary>
    /// Extraction options for <see cref="BlendShapeFeatureExtractor"/>.
    /// </summary>
    public sealed class BlendShapeExtractionOptions : IFeatureExtractionOptions
    {
        private readonly Dictionary<string, List<string>> m_SelectedShapesByMesh =
            new Dictionary<string, List<string>>(StringComparer.Ordinal);
        private readonly HashSet<string> m_DisabledMeshes =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, BlendShapeBaseMode> m_BaseModeByMesh =
            new Dictionary<string, BlendShapeBaseMode>(StringComparer.Ordinal);

        public string FeatureTypeId => BlendShapeFeature.FeatureTypeId;
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// When true and no explicit shape list is set for a mesh, extracts all blendshape channels on that mesh.
        /// When false, only shapes explicitly listed in <see cref="SelectedShapeNames"/> or per-mesh lists are extracted.
        /// </summary>
        public bool ExtractAllWhenSelectionEmpty { get; set; } = true;

        public BlendShapeBaseMode DefaultBaseMode { get; set; } = BlendShapeBaseMode.PreserveSourceRelative;
        public bool IncludeNormalDeltas { get; set; } = true;
        public List<string> SelectedShapeNames { get; } = new List<string>();

        /// <summary>
        /// Exact sparsity threshold (in physical meters) applied BEFORE quantization; scaled into the mesh's
        /// stored Origin FBX coordinate units. Default 0 keeps every non-zero finite delta.
        /// </summary>
        public double PositionDeltaEpsilon { get; set; } = 0d;

        /// <summary>
        /// Sparsity threshold for normal deltas applied BEFORE quantization; non-finite normals are always dropped.
        /// </summary>
        public double NormalDeltaEpsilon { get; set; } = 0d;

        public bool IsMeshEnabled(string nodePath)
        {
            return !m_DisabledMeshes.Contains(NormalizeKey(nodePath));
        }

        public void SetMeshEnabled(string nodePath, bool enabled)
        {
            string key = NormalizeKey(nodePath);
            if (enabled)
            {
                m_DisabledMeshes.Remove(key);
            }
            else
            {
                m_DisabledMeshes.Add(key);
            }
        }

        public BlendShapeBaseMode GetBaseMode(string nodePath)
        {
            return m_BaseModeByMesh.TryGetValue(NormalizeKey(nodePath), out var mode)
                ? mode
                : DefaultBaseMode;
        }

        public bool HasBaseModeOverride(string nodePath)
        {
            return m_BaseModeByMesh.ContainsKey(NormalizeKey(nodePath));
        }

        public void SetBaseModeOverride(string nodePath, BlendShapeBaseMode? mode)
        {
            string key = NormalizeKey(nodePath);
            if (mode.HasValue)
            {
                m_BaseModeByMesh[key] = mode.Value;
            }
            else
            {
                m_BaseModeByMesh.Remove(key);
            }
        }

        public bool HasSelectedShapeNamesForMesh(string nodePath)
        {
            return m_SelectedShapesByMesh.ContainsKey(NormalizeKey(nodePath));
        }

        public IReadOnlyList<string> GetSelectedShapeNames(string nodePath)
        {
            if (m_SelectedShapesByMesh.TryGetValue(NormalizeKey(nodePath), out var list))
            {
                return list;
            }

            return SelectedShapeNames;
        }

        public void SetSelectedShapeNames(string nodePath, IEnumerable<string> shapeNames)
        {
            string key = NormalizeKey(nodePath);
            var cleaned = new List<string>();
            if (shapeNames != null)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (string name in shapeNames)
                {
                    if (!string.IsNullOrWhiteSpace(name) && seen.Add(name))
                    {
                        cleaned.Add(name);
                    }
                }
            }

            m_SelectedShapesByMesh[key] = cleaned;
        }

        public void ClearSelectedShapeNamesForMesh(string nodePath)
        {
            m_SelectedShapesByMesh.Remove(NormalizeKey(nodePath));
        }

        public bool ShouldExtractMesh(string normalizedNodePath)
        {
            if (!Enabled || !IsMeshEnabled(normalizedNodePath))
            {
                return false;
            }

            if (HasSelectedShapeNamesForMesh(normalizedNodePath))
            {
                return m_SelectedShapesByMesh[NormalizeKey(normalizedNodePath)].Count > 0;
            }

            return ExtractAllWhenSelectionEmpty || SelectedShapeNames.Count > 0;
        }

        private static string NormalizeKey(string nodePath)
        {
            return FbxNameUtility.NormalizePath(nodePath ?? string.Empty);
        }
    }

    /// <summary>
    /// Built-in <see cref="IFeatureExtractor"/> for <see cref="BlendShapeFeature"/>.
    /// Extracts sparse <c>ControlPoint</c>-domain deltas in stored Origin FBX local space with sparsity
    /// determined prior to container quantization.
    /// </summary>
    public sealed class BlendShapeFeatureExtractor : IFeatureExtractor
    {
        public const string DefaultFeatureId = "blend-shapes";
        private const double k_UfbxWeightToUnityPercent = 100d;

        public string FeatureTypeId => BlendShapeFeature.FeatureTypeId;
        public int ExecutionOrder => 100;
        public Type OptionsType => typeof(BlendShapeExtractionOptions);

        public IFeatureExtractionOptions CreateDefaultOptions() => new BlendShapeExtractionOptions();

        public FeatureExtractionResult Extract(MeshExtractionContext context, IFeatureExtractionOptions options)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            var bsOptions = options as BlendShapeExtractionOptions ?? new BlendShapeExtractionOptions();
            if (!bsOptions.ShouldExtractMesh(context.NodePath))
            {
                return FeatureExtractionResult.Skipped(
                    "BS_EXT_DISABLED",
                    "BlendShape extraction is disabled or has no selected shapes for this mesh.",
                    context.MeshId,
                    FeatureTypeId);
            }

            var sourceMesh = context.NormalizedSourceMesh;
            var originMesh = context.NormalizedOriginMesh;
            if (sourceMesh == null || originMesh == null)
            {
                return FeatureExtractionResult.Failed(
                    "BS_EXT_MISSING_MESH",
                    "Both normalized source and origin FBX meshes are required for blendshape extraction.",
                    context.MeshId,
                    FeatureTypeId);
            }

            int domainCount = originMesh.ControlPointCount;
            if (domainCount <= 0)
            {
                return FeatureExtractionResult.Skipped(
                    "BS_EXT_EMPTY_MESH",
                    "Origin mesh has zero control points.",
                    context.MeshId,
                    FeatureTypeId);
            }

            var blendDeformers = sourceMesh.BlendDeformers;
            if (blendDeformers == null || blendDeformers.Count == 0)
            {
                return FeatureExtractionResult.Skipped(
                    "BS_EXT_NO_DEFORMERS",
                    "Source FBX mesh contains no blendshape deformers.",
                    context.MeshId,
                    FeatureTypeId);
            }

            bool filterBySelection = bsOptions.HasSelectedShapeNamesForMesh(context.NodePath) ||
                                     (!bsOptions.ExtractAllWhenSelectionEmpty) ||
                                     bsOptions.SelectedShapeNames.Count > 0;
            HashSet<string> selectedSet = null;
            if (filterBySelection)
            {
                var names = bsOptions.GetSelectedShapeNames(context.NodePath);
                selectedSet = new HashSet<string>(names, StringComparer.Ordinal);
                if (selectedSet.Count == 0)
                {
                    return FeatureExtractionResult.Skipped(
                        "BS_EXT_NO_SELECTION",
                        "No blendshapes selected for this mesh.",
                        context.MeshId,
                        FeatureTypeId);
                }
            }

            BlendShapeBaseMode baseMode = bsOptions.GetBaseMode(context.NodePath);
            Vector3d[] baseDeltaOffset = null;
            if (baseMode == BlendShapeBaseMode.RebaseOntoOriginal)
            {
                var srcPoints = sourceMesh.GetControlPoints();
                var orgPoints = originMesh.GetControlPoints();
                int count = Math.Min(domainCount, Math.Min(srcPoints.Length, orgPoints.Length));
                baseDeltaOffset = new Vector3d[domainCount];
                for (int i = 0; i < count; i++)
                {
                    baseDeltaOffset[i] = srcPoints[i] - orgPoints[i];
                }
            }

            double posEpsUnits = bsOptions.PositionDeltaEpsilon;
            if (posEpsUnits > 0d &&
                context.SpaceProvenance != null &&
                CanonicalMeshSpace.IsValidUnitMeters(context.SpaceProvenance.OriginRawMetersPerUnit))
            {
                posEpsUnits /= context.SpaceProvenance.OriginRawMetersPerUnit;
            }

            double posEpsSq = posEpsUnits > 0d
                ? posEpsUnits * posEpsUnits
                : 0d;
            double nrmEpsSq = bsOptions.NormalDeltaEpsilon > 0d
                ? bsOptions.NormalDeltaEpsilon * bsOptions.NormalDeltaEpsilon
                : 0d;

            var diagnostics = new List<ExtractionDiagnostic>();
            var seenNames = new HashSet<string>(StringComparer.Ordinal);
            var seenShapeIds = new HashSet<string>(StringComparer.Ordinal);
            var shapes = new List<BlendShapeEntry>();

            int[] idxScratch = Array.Empty<int>();
            Vector3d[] posScratch = Array.Empty<Vector3d>();
            Vector3d[] nrmScratch = Array.Empty<Vector3d>();

            for (int d = 0; d < blendDeformers.Count; d++)
            {
                var deformer = blendDeformers[d];
                if (deformer == null || deformer.Channels == null)
                {
                    continue;
                }

                for (int c = 0; c < deformer.Channels.Count; c++)
                {
                    var channel = deformer.Channels[c];
                    if (channel == null)
                    {
                        continue;
                    }

                    string channelName = channel.Name ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(channelName))
                    {
                        diagnostics.Add(new ExtractionDiagnostic(
                            ExtractionDiagnosticSeverity.Warning,
                            "BS_EXT_UNNAMED_CHANNEL",
                            $"Skipped blendshape channel at deformer {d}, channel {c} with empty name.",
                            context.MeshId,
                            FeatureTypeId));
                        continue;
                    }

                    if (selectedSet != null && !selectedSet.Contains(channelName))
                    {
                        continue;
                    }

                    if (!seenNames.Add(channelName))
                    {
                        diagnostics.Add(new ExtractionDiagnostic(
                            ExtractionDiagnosticSeverity.Warning,
                            "BS_EXT_DUP_CHANNEL_NAME",
                            $"Skipped duplicate blendshape channel name '{channelName}'.",
                            context.MeshId,
                            FeatureTypeId));
                        continue;
                    }

                    var keyframes = channel.Keyframes;
                    if (keyframes == null || keyframes.Count == 0)
                    {
                        diagnostics.Add(new ExtractionDiagnostic(
                            ExtractionDiagnosticSeverity.Warning,
                            "BS_EXT_EMPTY_CHANNEL",
                            $"Skipped blendshape channel '{channelName}' with zero keyframes.",
                            context.MeshId,
                            FeatureTypeId));
                        continue;
                    }

                    // Convert ufbx target weights (0..1) into BlendShare percentage weights (0..100) and sort ascending.
                    var orderedKeyframes = new List<(double Weight, IFbxBlendKeyframe Keyframe, int SourceIndex)>(keyframes.Count);
                    for (int k = 0; k < keyframes.Count; k++)
                    {
                        var kf = keyframes[k];
                        if (kf == null)
                        {
                            continue;
                        }

                        double weightPercent = kf.TargetWeight * k_UfbxWeightToUnityPercent;
                        if (double.IsNaN(weightPercent) || double.IsInfinity(weightPercent))
                        {
                            diagnostics.Add(new ExtractionDiagnostic(
                                ExtractionDiagnosticSeverity.Warning,
                                "BS_EXT_NONFINITE_WEIGHT",
                                $"Skipped keyframe {k} in '{channelName}' with non-finite target weight.",
                                context.MeshId,
                                FeatureTypeId));
                            continue;
                        }

                        orderedKeyframes.Add((weightPercent, kf, k));
                    }

                    if (orderedKeyframes.Count == 0)
                    {
                        continue;
                    }

                    orderedKeyframes.Sort((a, b) =>
                    {
                        int cmp = a.Weight.CompareTo(b.Weight);
                        return cmp != 0 ? cmp : a.SourceIndex.CompareTo(b.SourceIndex);
                    });

                    var frames = new List<BlendShapeFrame>(orderedKeyframes.Count);
                    var seenFrameIds = new HashSet<string>(StringComparer.Ordinal);
                    double lastWeight = double.NegativeInfinity;

                    for (int k = 0; k < orderedKeyframes.Count; k++)
                    {
                        var item = orderedKeyframes[k];
                        if (item.Weight <= lastWeight)
                        {
                            diagnostics.Add(new ExtractionDiagnostic(
                                ExtractionDiagnosticSeverity.Warning,
                                "BS_EXT_DUP_FRAME_WEIGHT",
                                $"Skipped duplicate/non-increasing keyframe weight {item.Weight} in '{channelName}'.",
                                context.MeshId,
                                FeatureTypeId));
                            continue;
                        }

                        lastWeight = item.Weight;
                        string frameId = MakeStableFrameId(item.Weight, seenFrameIds);

                        if (!TryBuildFramePayload(
                                item.Keyframe.Shape,
                                domainCount,
                                baseDeltaOffset,
                                bsOptions.IncludeNormalDeltas,
                                posEpsSq,
                                nrmEpsSq,
                                ref idxScratch,
                                ref posScratch,
                                ref nrmScratch,
                                out var payload,
                                out int droppedNonFiniteNormals,
                                out string failureCode,
                                out string failureDetail))
                        {
                            return FeatureExtractionResult.Failed(
                                failureCode,
                                $"BlendShape '{channelName}' frame '{frameId}' (weight {item.Weight}): {failureDetail}",
                                context.MeshId,
                                FeatureTypeId,
                                diagnostics);
                        }

                        if (droppedNonFiniteNormals > 0)
                        {
                            diagnostics.Add(new ExtractionDiagnostic(
                                ExtractionDiagnosticSeverity.Warning,
                                "BS_EXT_DROPPED_NONFINITE_NORMALS",
                                $"Filtered {droppedNonFiniteNormals} non-finite normal delta(s) in '{channelName}' frame '{frameId}'.",
                                context.MeshId,
                                FeatureTypeId));
                        }

                        frames.Add(new BlendShapeFrame
                        {
                            FrameId = frameId,
                            Weight = item.Weight,
                            Payload = LazyPayload<BlendShapeFramePayload>.FromValue(payload)
                        });
                    }

                    if (frames.Count == 0)
                    {
                        continue;
                    }

                    string shapeId = MakeUniqueShapeId(channelName, seenShapeIds);
                    shapes.Add(new BlendShapeEntry
                    {
                        ShapeId = shapeId,
                        Name = channelName,
                        Frames = frames
                    });
                }
            }

            if (shapes.Count == 0)
            {
                return FeatureExtractionResult.Skipped(
                    "BS_EXT_NO_SHAPES_EXTRACTED",
                    "No matching blendshape channels were extracted.",
                    context.MeshId,
                    FeatureTypeId);
            }

            var feature = new BlendShapeFeature
            {
                FeatureId = DefaultFeatureId,
                Required = true,
                Shapes = shapes
            };

            return FeatureExtractionResult.Success(feature, diagnostics);
        }

        private static bool TryBuildFramePayload(
            IFbxBlendShape shape,
            int domainCount,
            Vector3d[] baseDeltaOffset,
            bool includeNormals,
            double posEpsSq,
            double nrmEpsSq,
            ref int[] idxScratch,
            ref Vector3d[] posScratch,
            ref Vector3d[] nrmScratch,
            out BlendShapeFramePayload payload,
            out int droppedNonFiniteNormals,
            out string failureCode,
            out string failureDetail)
        {
            payload = null;
            droppedNonFiniteNormals = 0;
            failureCode = null;
            failureDetail = null;

            bool hasNormals = includeNormals && shape != null && shape.HasNormalOffsets;
            int offsetCount;
            try
            {
                offsetCount = shape != null ? shape.OffsetCount : 0;
            }
            catch (Exception ex)
            {
                failureCode = "BS_EXT_OFFSET_COPY_FAILED";
                failureDetail = $"Failed to inspect blendshape offsets: {ex.Message}";
                return false;
            }

            if (offsetCount < 0)
            {
                failureCode = "BS_EXT_OFFSET_COPY_FAILED";
                failureDetail = $"Invalid negative offset count ({offsetCount}).";
                return false;
            }

            if (offsetCount > 0)
            {
                if (idxScratch.Length < offsetCount)
                {
                    idxScratch = new int[offsetCount];
                    posScratch = new Vector3d[offsetCount];
                }

                if (hasNormals && nrmScratch.Length < offsetCount)
                {
                    nrmScratch = new Vector3d[offsetCount];
                }
            }

            int written = 0;
            if (offsetCount > 0)
            {
                var idxSpan = idxScratch.AsSpan(0, offsetCount);
                var posSpan = posScratch.AsSpan(0, offsetCount);
                var nrmSpan = hasNormals ? nrmScratch.AsSpan(0, offsetCount) : Span<Vector3d>.Empty;
                bool copied;
                try
                {
                    copied = shape.TryCopyOffsets(idxSpan, posSpan, nrmSpan, Span<double>.Empty, out written);
                }
                catch (Exception ex)
                {
                    failureCode = "BS_EXT_OFFSET_COPY_FAILED";
                    failureDetail = $"Exception while copying blendshape offsets: {ex.Message}";
                    return false;
                }

                if (!copied || written != offsetCount)
                {
                    failureCode = "BS_EXT_OFFSET_COPY_FAILED";
                    failureDetail = $"TryCopyOffsets failed or returned truncated count ({written}/{offsetCount}).";
                    return false;
                }
            }

            var posIndices = new List<int>(written);
            var posValues = new List<Vector3d>(written);
            var nrmIndices = hasNormals ? new List<int>(written) : null;
            var nrmValues = hasNormals ? new List<Vector3d>(written) : null;

            if (baseDeltaOffset == null)
            {
                // PreserveSourceRelative: sparse directly from source shape offsets.
                bool sortedUnique = true;
                int prevCp = -1;
                for (int i = 0; i < written; i++)
                {
                    int cp = idxScratch[i];
                    if ((uint)cp >= (uint)domainCount)
                    {
                        continue;
                    }

                    if (cp <= prevCp)
                    {
                        sortedUnique = false;
                        break;
                    }

                    prevCp = cp;
                }

                if (sortedUnique)
                {
                    for (int i = 0; i < written; i++)
                    {
                        int cp = idxScratch[i];
                        if ((uint)cp >= (uint)domainCount)
                        {
                            continue;
                        }

                        var pos = posScratch[i];
                        if (!IsFinite(pos))
                        {
                            failureCode = "BS_EXT_NONFINITE_POSITION_DELTA";
                            failureDetail = $"Control point {cp} has non-finite position delta ({pos.x}, {pos.y}, {pos.z}).";
                            return false;
                        }

                        if (ExceedsThreshold(pos, posEpsSq))
                        {
                            posIndices.Add(cp);
                            posValues.Add(pos);
                        }

                        if (hasNormals)
                        {
                            var nrm = nrmScratch[i];
                            if (!IsFinite(nrm))
                            {
                                droppedNonFiniteNormals++;
                            }
                            else if (ExceedsThreshold(nrm, nrmEpsSq))
                            {
                                nrmIndices.Add(cp);
                                nrmValues.Add(nrm);
                            }
                        }
                    }
                }
                else
                {
                    var posMap = new SortedDictionary<int, Vector3d>();
                    var nrmMap = hasNormals ? new SortedDictionary<int, Vector3d>() : null;
                    for (int i = 0; i < written; i++)
                    {
                        int cp = idxScratch[i];
                        if ((uint)cp >= (uint)domainCount)
                        {
                            continue;
                        }

                        posMap[cp] = posScratch[i];
                        if (hasNormals)
                        {
                            nrmMap[cp] = nrmScratch[i];
                        }
                    }

                    foreach (var kv in posMap)
                    {
                        if (!IsFinite(kv.Value))
                        {
                            failureCode = "BS_EXT_NONFINITE_POSITION_DELTA";
                            failureDetail = $"Control point {kv.Key} has non-finite position delta ({kv.Value.x}, {kv.Value.y}, {kv.Value.z}).";
                            return false;
                        }

                        if (ExceedsThreshold(kv.Value, posEpsSq))
                        {
                            posIndices.Add(kv.Key);
                            posValues.Add(kv.Value);
                        }
                    }

                    if (nrmMap != null)
                    {
                        foreach (var kv in nrmMap)
                        {
                            if (!IsFinite(kv.Value))
                            {
                                droppedNonFiniteNormals++;
                            }
                            else if (ExceedsThreshold(kv.Value, nrmEpsSq))
                            {
                                nrmIndices.Add(kv.Key);
                                nrmValues.Add(kv.Value);
                            }
                        }
                    }
                }
            }
            else
            {
                // RebaseOntoOriginal: targetDelta[cp] = (sourceBase[cp] - originBase[cp]) + sourceDelta[cp].
                var sourceDeltaByCp = new Dictionary<int, Vector3d>(written);
                var nrmMap = hasNormals ? new SortedDictionary<int, Vector3d>() : null;
                for (int i = 0; i < written; i++)
                {
                    int cp = idxScratch[i];
                    if ((uint)cp >= (uint)domainCount)
                    {
                        continue;
                    }

                    sourceDeltaByCp[cp] = posScratch[i];
                    if (hasNormals)
                    {
                        nrmMap[cp] = nrmScratch[i];
                    }
                }

                for (int cp = 0; cp < domainCount; cp++)
                {
                    sourceDeltaByCp.TryGetValue(cp, out var srcDelta);
                    var total = baseDeltaOffset[cp] + srcDelta;
                    if (!IsFinite(total))
                    {
                        failureCode = "BS_EXT_NONFINITE_POSITION_DELTA";
                        failureDetail = $"Control point {cp} has non-finite rebased position delta ({total.x}, {total.y}, {total.z}).";
                        return false;
                    }

                    if (ExceedsThreshold(total, posEpsSq))
                    {
                        posIndices.Add(cp);
                        posValues.Add(total);
                    }
                }

                if (nrmMap != null)
                {
                    foreach (var kv in nrmMap)
                    {
                        if (!IsFinite(kv.Value))
                        {
                            droppedNonFiniteNormals++;
                        }
                        else if (ExceedsThreshold(kv.Value, nrmEpsSq))
                        {
                            nrmIndices.Add(kv.Key);
                            nrmValues.Add(kv.Value);
                        }
                    }
                }
            }

            var deltaPositions = new IndexedBuffer<Vector3d>(
                IndexDomain.ControlPoint,
                domainCount,
                posIndices.ToArray(),
                posValues.ToArray());

            IndexedBuffer<Vector3d> deltaNormals = null;
            if (nrmIndices != null && nrmIndices.Count > 0)
            {
                deltaNormals = new IndexedBuffer<Vector3d>(
                    IndexDomain.ControlPoint,
                    domainCount,
                    nrmIndices.ToArray(),
                    nrmValues.ToArray());
            }

            payload = new BlendShapeFramePayload
            {
                DeltaPositions = deltaPositions,
                DeltaNormals = deltaNormals
            };
            return true;
        }

        private static string MakeStableFrameId(double weightPercent, HashSet<string> seenFrameIds)
        {
            double normalized = weightPercent == 0d ? 0d : weightPercent;
            string baseId = "w" + normalized.ToString("0.######", CultureInfo.InvariantCulture);
            if (seenFrameIds.Add(baseId))
            {
                return baseId;
            }

            string exactId = "w" + normalized.ToString("G17", CultureInfo.InvariantCulture);
            if (seenFrameIds.Add(exactId))
            {
                return exactId;
            }

            for (int suffix = 2; ; suffix++)
            {
                string candidate = $"{exactId}_{suffix}";
                if (seenFrameIds.Add(candidate))
                {
                    return candidate;
                }
            }
        }

        private static bool IsFinite(Vector3d v)
        {
            return !double.IsNaN(v.x) && !double.IsInfinity(v.x) &&
                   !double.IsNaN(v.y) && !double.IsInfinity(v.y) &&
                   !double.IsNaN(v.z) && !double.IsInfinity(v.z);
        }

        private static bool ExceedsThreshold(Vector3d v, double epsSq)
        {
            if (epsSq <= 0d)
            {
                return v.x != 0d || v.y != 0d || v.z != 0d;
            }

            return v.sqrMagnitude > epsSq;
        }

        private static string MakeUniqueShapeId(string channelName, HashSet<string> seenShapeIds)
        {
            if (seenShapeIds.Add(channelName))
            {
                return channelName;
            }

            for (int suffix = 2; ; suffix++)
            {
                string candidate = $"{channelName}_{suffix}";
                if (seenShapeIds.Add(candidate))
                {
                    return candidate;
                }
            }
        }
    }
}
