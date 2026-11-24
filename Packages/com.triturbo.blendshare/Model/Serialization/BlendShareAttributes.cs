using System;

namespace Triturbo.BlendShare.Model.Serialization
{
    /// <summary>
    /// Semantic hint guiding bottom-layer codec selection (quantization, palette deduplication, renormalization).
    /// </summary>
    public enum DataSemantic : byte
    {
        Default = 0,
        PositionDelta = 1,
        NormalDelta = 2,
        NormalizedWeight = 3,
        UvCoord = 4,
        UvDelta = 5
    }

    /// <summary>
    /// Author-selected precision mode for semantic vector/weight payload codecs.
    /// </summary>
    public enum QuantizationMode : byte
    {
        /// <summary>Full binary64 precision for lossless FBX round-trips.</summary>
        LosslessFloat64 = 0,

        /// <summary>Full binary32 precision without quantization.</summary>
        FullFloat32 = 1,

        /// <summary>Default 16-bit symmetric range quantization with outlier fallback to FullFloat32.</summary>
        BalancedQuant16 = 2,

        /// <summary>Compact 12-bit symmetric range quantization with relaxed error bound.</summary>
        CompactQuant12 = 3
    }

    /// <summary>
    /// Outer lossless compression envelope for container payload records.
    /// Deflate is mandatory on all platforms; Brotli is optional where supported.
    /// </summary>
    public enum CompressionAlgorithm : byte
    {
        None = 0,
        Deflate = 1,
        Brotli = 2
    }

    /// <summary>
    /// Immutable encoding options passed to container writers and semantic codecs.
    /// </summary>
    public readonly struct PayloadEncodeOptions : IEquatable<PayloadEncodeOptions>
    {
        public static readonly PayloadEncodeOptions Default = new PayloadEncodeOptions(
            QuantizationMode.BalancedQuant16,
            CompressionAlgorithm.Deflate,
            enableAdaptiveVectorPalette: true,
            maxPositionQuantizationErrorMeters: 5e-5,
            weightZeroThreshold: 1e-5,
            positionUnitMeters: 1d);

        public static readonly PayloadEncodeOptions Lossless = new PayloadEncodeOptions(
            QuantizationMode.LosslessFloat64,
            CompressionAlgorithm.Deflate,
            enableAdaptiveVectorPalette: true,
            maxPositionQuantizationErrorMeters: 0d,
            weightZeroThreshold: 0d,
            positionUnitMeters: 1d);

        private readonly double m_PositionUnitMeters;

        public QuantizationMode Quantization { get; }
        public CompressionAlgorithm Compression { get; }
        public bool EnableAdaptiveVectorPalette { get; }

        /// <summary>
        /// Maximum acceptable per-axis physical error (in meters) for quantized PositionDelta.
        /// Scaled to stored coordinate units via <see cref="PositionUnitMeters"/>; if exceeded, encoder
        /// falls back to FullFloat32 (and promotes to LosslessFloat64 if Float32 also exceeds the budget).
        /// </summary>
        public double MaxPositionQuantizationErrorMeters { get; }

        /// <summary>
        /// Minimum weight magnitude kept during NormalizedWeight quantization; smaller values snap to zero.
        /// </summary>
        public double WeightZeroThreshold { get; }

        /// <summary>
        /// Physical length in meters of 1.0 coordinate unit in the payload's stored mesh space
        /// (e.g., <c>1.0</c> for meters / <c>CanonicalMeshLocalV1</c>, <c>0.01</c> for centimeters in <c>OriginRawLocalV2</c>).
        /// </summary>
        public double PositionUnitMeters => m_PositionUnitMeters > 0d ? m_PositionUnitMeters : 1d;

        /// <summary>
        /// Maximum acceptable per-axis absolute error expressed in the payload's stored coordinate units:
        /// <c>MaxPositionQuantizationErrorMeters / PositionUnitMeters</c>.
        /// </summary>
        public double EffectiveMaxPositionQuantizationErrorUnits =>
            MaxPositionQuantizationErrorMeters <= 0d
                ? 0d
                : MaxPositionQuantizationErrorMeters / PositionUnitMeters;

        public PayloadEncodeOptions(
            QuantizationMode quantization,
            CompressionAlgorithm compression = CompressionAlgorithm.Deflate,
            bool enableAdaptiveVectorPalette = true,
            double maxPositionQuantizationErrorMeters = 1e-4,
            double weightZeroThreshold = 1e-5,
            double positionUnitMeters = 1d)
        {
            if (double.IsNaN(maxPositionQuantizationErrorMeters) || double.IsInfinity(maxPositionQuantizationErrorMeters) || maxPositionQuantizationErrorMeters < 0d)
            {
                throw new ArgumentOutOfRangeException(nameof(maxPositionQuantizationErrorMeters));
            }

            if (double.IsNaN(weightZeroThreshold) || double.IsInfinity(weightZeroThreshold) || weightZeroThreshold < 0d)
            {
                throw new ArgumentOutOfRangeException(nameof(weightZeroThreshold));
            }

            if (double.IsNaN(positionUnitMeters) || double.IsInfinity(positionUnitMeters) || positionUnitMeters <= 0d)
            {
                throw new ArgumentOutOfRangeException(nameof(positionUnitMeters));
            }

            Quantization = quantization;
            Compression = compression;
            EnableAdaptiveVectorPalette = enableAdaptiveVectorPalette;
            MaxPositionQuantizationErrorMeters = maxPositionQuantizationErrorMeters;
            WeightZeroThreshold = weightZeroThreshold;
            m_PositionUnitMeters = positionUnitMeters;
        }

        public PayloadEncodeOptions WithPositionUnitMeters(double positionUnitMeters)
        {
            return new PayloadEncodeOptions(
                Quantization,
                Compression,
                EnableAdaptiveVectorPalette,
                MaxPositionQuantizationErrorMeters,
                WeightZeroThreshold,
                positionUnitMeters);
        }

        public bool Equals(PayloadEncodeOptions other) =>
            Quantization == other.Quantization &&
            Compression == other.Compression &&
            EnableAdaptiveVectorPalette == other.EnableAdaptiveVectorPalette &&
            MaxPositionQuantizationErrorMeters.Equals(other.MaxPositionQuantizationErrorMeters) &&
            WeightZeroThreshold.Equals(other.WeightZeroThreshold) &&
            PositionUnitMeters.Equals(other.PositionUnitMeters);

        public override bool Equals(object obj) => obj is PayloadEncodeOptions other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Quantization, Compression, EnableAdaptiveVectorPalette, MaxPositionQuantizationErrorMeters, WeightZeroThreshold, PositionUnitMeters);
        public static bool operator ==(PayloadEncodeOptions left, PayloadEncodeOptions right) => left.Equals(right);
        public static bool operator !=(PayloadEncodeOptions left, PayloadEncodeOptions right) => !left.Equals(right);
    }

    /// <summary>
    /// Marks a top-level BlendShare feature data class and binds it to a stable namespaced TypeId.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public sealed class BlendShareFeatureAttribute : Attribute
    {
        public new string TypeId { get; }
        public int SchemaVersion { get; set; } = 1;
        public bool DefaultRequired { get; set; } = true;

        public BlendShareFeatureAttribute(string typeId)
        {
            if (string.IsNullOrWhiteSpace(typeId))
            {
                throw new ArgumentException("Feature TypeId must be non-empty.", nameof(typeId));
            }

            TypeId = typeId;
        }
    }

    /// <summary>
    /// Marks a declarative BlendShare record class serialized via field tags by BlendShareSerializer.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public sealed class BlendShareRecordAttribute : Attribute
    {
        public int SchemaVersion { get; set; } = 1;
    }

    /// <summary>
    /// Declares a field tag and optional data semantic for BlendShareSerializer.
    /// Field tags on derived feature classes must be >= 16 (tags 1..15 are reserved for BlendShareFeatureData).
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, Inherited = true, AllowMultiple = false)]
    public sealed class BlendShareFieldAttribute : Attribute
    {
        /// <summary>Maximum 29-bit field tag encodable with 3-bit WireType in a 32-bit varint header.</summary>
        public const int MaxTag = 0x1FFFFFFF;

        public int Tag { get; }
        public DataSemantic Semantic { get; set; } = DataSemantic.Default;

        public BlendShareFieldAttribute(int tag)
        {
            if (tag <= 0 || tag > MaxTag)
            {
                throw new ArgumentOutOfRangeException(nameof(tag), $"Field tag must be in [1, {MaxTag}].");
            }

            Tag = tag;
        }
    }
}
