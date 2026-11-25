using System;
using System.Collections.Generic;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Model.Features;
using Triturbo.BlendShare.Model.Serialization;
using Triturbo.BlendShare.Primitives;

namespace Triturbo.BlendShare.Model.Validation
{
    public enum FeatureValidationSeverity : byte
    {
        Warning = 0,
        Error = 1
    }

    public readonly struct FeatureValidationIssue
    {
        public FeatureValidationSeverity Severity { get; }
        public string Code { get; }
        public string Message { get; }

        public FeatureValidationIssue(FeatureValidationSeverity severity, string code, string message)
        {
            Severity = severity;
            Code = code ?? string.Empty;
            Message = message ?? string.Empty;
        }

        public override string ToString() => $"[{Severity}] {Code}: {Message}";
    }

    public sealed class FeatureValidationContext
    {
        public string MeshId { get; }
        public int ControlPointCount { get; }
        public int FaceCornerCount { get; }
        public ILazyPayloadSource PayloadSource { get; }
        public bool ValidatePayloads { get; }

        public FeatureValidationContext(
            string meshId,
            int controlPointCount,
            int faceCornerCount = -1,
            ILazyPayloadSource payloadSource = null,
            bool validatePayloads = false)
        {
            MeshId = meshId ?? string.Empty;
            ControlPointCount = controlPointCount;
            FaceCornerCount = faceCornerCount;
            PayloadSource = payloadSource;
            ValidatePayloads = validatePayloads;
        }
    }

    public sealed class FeatureValidationResult
    {
        private readonly List<FeatureValidationIssue> m_Issues = new List<FeatureValidationIssue>();

        public IReadOnlyList<FeatureValidationIssue> Issues => m_Issues;
        public bool IsValid
        {
            get
            {
                for (int i = 0; i < m_Issues.Count; i++)
                {
                    if (m_Issues[i].Severity == FeatureValidationSeverity.Error)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        public void AddError(string code, string message)
        {
            m_Issues.Add(new FeatureValidationIssue(FeatureValidationSeverity.Error, code, message));
        }

        public void AddWarning(string code, string message)
        {
            m_Issues.Add(new FeatureValidationIssue(FeatureValidationSeverity.Warning, code, message));
        }
    }

    /// <summary>
    /// Semantic validator for a single BlendShare feature type, run on both write and read paths before application.
    /// </summary>
    public interface IFeatureValidator
    {
        string FeatureTypeId { get; }
        void Validate(BlendShareFeatureData feature, FeatureValidationContext context, FeatureValidationResult result);
    }

    public sealed class BlendShapeFeatureValidator : IFeatureValidator
    {
        public string FeatureTypeId => BlendShapeFeature.FeatureTypeId;

        public void Validate(BlendShareFeatureData feature, FeatureValidationContext context, FeatureValidationResult result)
        {
            if (!(feature is BlendShapeFeature blendShapes))
            {
                result.AddError("BS_VAL_TYPE_MISMATCH", $"Expected {nameof(BlendShapeFeature)} but received {feature?.GetType().Name ?? "null"}.");
                return;
            }

            if (string.IsNullOrWhiteSpace(blendShapes.FeatureId))
            {
                result.AddError("BS_VAL_EMPTY_FEATURE_ID", "BlendShapeFeature.FeatureId must be non-empty.");
            }

            var shapes = blendShapes.Shapes;
            if (shapes == null)
            {
                return;
            }

            var seenShapeIds = new HashSet<string>(StringComparer.Ordinal);
            var seenShapeNames = new HashSet<string>(StringComparer.Ordinal);

            for (int s = 0; s < shapes.Count; s++)
            {
                var shape = shapes[s];
                if (shape == null)
                {
                    result.AddError("BS_VAL_NULL_SHAPE", $"Shape at index {s} is null.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(shape.ShapeId))
                {
                    result.AddError("BS_VAL_EMPTY_SHAPE_ID", $"Shape at index {s} has empty ShapeId.");
                }
                else if (!seenShapeIds.Add(shape.ShapeId))
                {
                    result.AddError("BS_VAL_DUP_SHAPE_ID", $"Duplicate ShapeId '{shape.ShapeId}'.");
                }

                if (string.IsNullOrWhiteSpace(shape.Name))
                {
                    result.AddError("BS_VAL_EMPTY_SHAPE_NAME", $"Shape '{shape.ShapeId}' has empty Name.");
                }
                else if (!seenShapeNames.Add(shape.Name))
                {
                    result.AddError("BS_VAL_DUP_SHAPE_NAME", $"Duplicate shape name '{shape.Name}'.");
                }

                var frames = shape.Frames;
                if (frames == null || frames.Count == 0)
                {
                    result.AddError("BS_VAL_NO_FRAMES", $"Shape '{shape.Name}' has no frames.");
                    continue;
                }

                var seenFrameIds = new HashSet<string>(StringComparer.Ordinal);
                double previousWeight = double.NegativeInfinity;

                for (int f = 0; f < frames.Count; f++)
                {
                    var frame = frames[f];
                    if (frame == null)
                    {
                        result.AddError("BS_VAL_NULL_FRAME", $"Frame {f} of shape '{shape.Name}' is null.");
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(frame.FrameId))
                    {
                        result.AddError("BS_VAL_EMPTY_FRAME_ID", $"Frame {f} of shape '{shape.Name}' has empty FrameId.");
                    }
                    else if (!seenFrameIds.Add(frame.FrameId))
                    {
                        result.AddError("BS_VAL_DUP_FRAME_ID", $"Duplicate FrameId '{frame.FrameId}' in shape '{shape.Name}'.");
                    }

                    if (double.IsNaN(frame.Weight) || double.IsInfinity(frame.Weight))
                    {
                        result.AddError("BS_VAL_NONFINITE_WEIGHT", $"Frame '{frame.FrameId}' has non-finite weight.");
                    }
                    else if (frame.Weight <= previousWeight)
                    {
                        result.AddError("BS_VAL_NONINC_WEIGHT", $"Frame weights in shape '{shape.Name}' must be strictly increasing ({frame.Weight} after {previousWeight}).");
                    }
                    else
                    {
                        previousWeight = frame.Weight;
                    }

                    if (context != null && context.ValidatePayloads && frame.Payload != null && frame.Payload.HasPayload)
                    {
                        if (frame.Payload.State == LazyPayloadState.Ref && context.PayloadSource == null)
                        {
                            result.AddError("BS_VAL_UNRESOLVED_PAYLOAD",
                                $"Cannot validate Ref-backed payload for '{shape.Name}/{frame.FrameId}' without an ILazyPayloadSource.");
                        }
                        else
                        {
                            using (var lease = frame.Payload.Acquire(context.PayloadSource))
                            {
                                ValidateFramePayload(lease.Value, shape.Name, frame.FrameId, context, result);
                            }
                        }
                    }
                }
            }
        }

        private static void ValidateFramePayload(
            BlendShapeFramePayload payload,
            string shapeName,
            string frameId,
            FeatureValidationContext context,
            FeatureValidationResult result)
        {
            if (payload == null)
            {
                return;
            }

            ValidateVectorBuffer(payload.DeltaPositions, "DeltaPositions", shapeName, frameId, context, result);
            ValidateVectorBuffer(payload.DeltaNormals, "DeltaNormals", shapeName, frameId, context, result);
        }

        private static void ValidateVectorBuffer(
            IndexedBuffer<Vector3d> buffer,
            string fieldName,
            string shapeName,
            string frameId,
            FeatureValidationContext context,
            FeatureValidationResult result)
        {
            if (buffer == null)
            {
                return;
            }

            if (buffer.Domain != IndexDomain.ControlPoint)
            {
                result.AddError("BS_VAL_DOMAIN_MISMATCH", $"{shapeName}/{frameId}.{fieldName} must use ControlPoint domain, found {buffer.Domain}.");
            }

            if (context.ControlPointCount >= 0 && buffer.DomainCount != context.ControlPointCount)
            {
                result.AddError("BS_VAL_DOMAIN_COUNT", $"{shapeName}/{frameId}.{fieldName} DomainCount ({buffer.DomainCount}) != mesh ControlPointCount ({context.ControlPointCount}).");
            }

            var values = buffer.Values;
            for (int i = 0; i < values.Length; i++)
            {
                var v = values[i];
                if (double.IsNaN(v.x) || double.IsInfinity(v.x) ||
                    double.IsNaN(v.y) || double.IsInfinity(v.y) ||
                    double.IsNaN(v.z) || double.IsInfinity(v.z))
                {
                    result.AddError("BS_VAL_NONFINITE_VEC", $"{shapeName}/{frameId}.{fieldName}[{i}] contains NaN or Infinity.");
                    break;
                }
            }
        }
    }

    public sealed class SkinWeightFeatureValidator : IFeatureValidator
    {
        public string FeatureTypeId => SkinWeightFeature.FeatureTypeId;

        public void Validate(BlendShareFeatureData feature, FeatureValidationContext context, FeatureValidationResult result)
        {
            if (!(feature is SkinWeightFeature skin))
            {
                result.AddError("SW_VAL_TYPE_MISMATCH", $"Expected {nameof(SkinWeightFeature)} but received {feature?.GetType().Name ?? "null"}.");
                return;
            }

            if (string.IsNullOrWhiteSpace(skin.FeatureId))
            {
                result.AddError("SW_VAL_EMPTY_FEATURE_ID", "SkinWeightFeature.FeatureId must be non-empty.");
            }

            var clusters = skin.Clusters;
            if (clusters == null)
            {
                return;
            }

            var seenBonePaths = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < clusters.Count; i++)
            {
                var cluster = clusters[i];
                if (cluster == null)
                {
                    result.AddError("SW_VAL_NULL_CLUSTER", $"Cluster at index {i} is null.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(cluster.BonePath))
                {
                    result.AddError("SW_VAL_EMPTY_BONE", $"Cluster at index {i} has empty BonePath.");
                }
                else if (!seenBonePaths.Add(cluster.BonePath))
                {
                    result.AddError("SW_VAL_DUP_BONE", $"Duplicate BonePath '{cluster.BonePath}'.");
                }

                if (context != null && context.ValidatePayloads && cluster.Payload != null && cluster.Payload.HasPayload)
                {
                    if (cluster.Payload.State == LazyPayloadState.Ref && context.PayloadSource == null)
                    {
                        result.AddError("SW_VAL_UNRESOLVED_PAYLOAD",
                            $"Cannot validate Ref-backed skin payload for cluster '{cluster.BonePath}' without an ILazyPayloadSource.");
                    }
                    else
                    {
                        using (var lease = cluster.Payload.Acquire(context.PayloadSource))
                        {
                            var weights = lease.Value?.DeltaWeights;
                            if (weights == null)
                            {
                                continue;
                            }

                            if (weights.Domain != IndexDomain.ControlPoint)
                            {
                                result.AddError("SW_VAL_DOMAIN_MISMATCH", $"Cluster '{cluster.BonePath}' must use ControlPoint domain, found {weights.Domain}.");
                            }

                            if (context.ControlPointCount >= 0 && weights.DomainCount != context.ControlPointCount)
                            {
                                result.AddError("SW_VAL_DOMAIN_COUNT", $"Cluster '{cluster.BonePath}' DomainCount ({weights.DomainCount}) != mesh ControlPointCount ({context.ControlPointCount}).");
                            }

                            var vals = weights.Values;
                            for (int w = 0; w < vals.Length; w++)
                            {
                                if (double.IsNaN(vals[w]) || double.IsInfinity(vals[w]) || vals[w] < -1d || vals[w] > 1d)
                                {
                                    result.AddError("SW_VAL_WEIGHT_RANGE", $"Cluster '{cluster.BonePath}' weight delta[{w}] ({vals[w]}) must be finite and within [-1, 1].");
                                    break;
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}
