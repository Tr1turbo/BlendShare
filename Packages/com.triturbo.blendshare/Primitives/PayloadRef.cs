using System;

namespace Triturbo.BlendShare.Primitives
{
    /// <summary>
    /// Immutable value handle pointing to one indexed payload entry in a specific BlendShare container revision.
    /// </summary>
    public readonly struct PayloadRef : IEquatable<PayloadRef>
    {
        public static readonly PayloadRef Invalid = default;

        public string SourceRevision { get; }
        public int PayloadIndex { get; }
        public string CodecId { get; }
        public int CodecVersion { get; }
        public long UncompressedBytes { get; }

        public bool IsValid =>
            !string.IsNullOrEmpty(SourceRevision) &&
            PayloadIndex >= 0 &&
            !string.IsNullOrEmpty(CodecId) &&
            CodecVersion > 0 &&
            UncompressedBytes >= 0L;

        public PayloadRef(
            string sourceRevision,
            int payloadIndex,
            string codecId,
            int codecVersion,
            long uncompressedBytes)
        {
            if (string.IsNullOrWhiteSpace(sourceRevision))
            {
                throw new ArgumentException("SourceRevision must be non-empty.", nameof(sourceRevision));
            }

            if (payloadIndex < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(payloadIndex), "PayloadIndex must be non-negative.");
            }

            if (string.IsNullOrWhiteSpace(codecId))
            {
                throw new ArgumentException("CodecId must be non-empty.", nameof(codecId));
            }

            if (codecVersion <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(codecVersion), "CodecVersion must be positive.");
            }

            if (uncompressedBytes < 0L)
            {
                throw new ArgumentOutOfRangeException(nameof(uncompressedBytes), "UncompressedBytes must be non-negative.");
            }

            SourceRevision = sourceRevision;
            PayloadIndex = payloadIndex;
            CodecId = codecId;
            CodecVersion = codecVersion;
            UncompressedBytes = uncompressedBytes;
        }

        public bool Equals(PayloadRef other) =>
            string.Equals(SourceRevision, other.SourceRevision, StringComparison.Ordinal) &&
            PayloadIndex == other.PayloadIndex &&
            string.Equals(CodecId, other.CodecId, StringComparison.Ordinal) &&
            CodecVersion == other.CodecVersion &&
            UncompressedBytes == other.UncompressedBytes;

        public override bool Equals(object obj) => obj is PayloadRef other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(SourceRevision, PayloadIndex, CodecId, CodecVersion, UncompressedBytes);
        public static bool operator ==(PayloadRef left, PayloadRef right) => left.Equals(right);
        public static bool operator !=(PayloadRef left, PayloadRef right) => !left.Equals(right);
        public override string ToString() => $"PayloadRef(Rev={SourceRevision}, Index={PayloadIndex}, Codec={CodecId}@v{CodecVersion}, Bytes={UncompressedBytes})";
    }
}
