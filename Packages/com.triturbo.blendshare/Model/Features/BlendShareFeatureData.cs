using Triturbo.BlendShare.Model.Serialization;

namespace Triturbo.BlendShare.Model.Features
{
    /// <summary>
    /// Base class for all BlendShare feature data records.
    /// Field tags 1..15 are reserved for base class metadata; derived features MUST start at tag >= 16.
    /// </summary>
    public abstract class BlendShareFeatureData
    {
        public const int MinDerivedFieldTag = 16;

        [BlendShareField(1)] public string FeatureId = string.Empty;
        [BlendShareField(2)] public string TypeId = string.Empty;
        [BlendShareField(3)] public int SchemaVersion = 1;
        [BlendShareField(4)] public bool Required = true;
    }

    /// <summary>
    /// Fallback representation preserving an unsupported or future feature's envelope metadata without failing import.
    /// </summary>
    public sealed class UnknownFeatureData : BlendShareFeatureData
    {
        public byte[] RawEnvelopeBytes { get; }
        public long EstimatedPayloadBytes { get; }

        public UnknownFeatureData(
            string featureId,
            string typeId,
            int schemaVersion,
            bool required,
            byte[] rawEnvelopeBytes,
            long estimatedPayloadBytes = 0L)
        {
            FeatureId = featureId ?? string.Empty;
            TypeId = typeId ?? string.Empty;
            SchemaVersion = schemaVersion;
            Required = required;
            RawEnvelopeBytes = rawEnvelopeBytes;
            EstimatedPayloadBytes = estimatedPayloadBytes;
        }
    }
}
