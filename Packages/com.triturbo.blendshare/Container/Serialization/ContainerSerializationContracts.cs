using System;
using System.IO;
using Triturbo.BlendShare.Model.Features;
using Triturbo.BlendShare.Model.Serialization;
using Triturbo.BlendShare.Model.Validation;
using Triturbo.BlendShare.Primitives;

namespace Triturbo.BlendShare.Container.Serialization
{
    /// <summary>
    /// Thrown before allocation when decoding a payload would exceed the configured byte budget.
    /// </summary>
    public sealed class PayloadBudgetExceededException : InvalidOperationException
    {
        public long RequestedBytes { get; }
        public long RemainingBytes { get; }

        public PayloadBudgetExceededException(long requestedBytes, long remainingBytes)
            : base($"Decoded payload allocation ({requestedBytes} bytes) exceeds remaining decode budget ({remainingBytes} bytes).")
        {
            RequestedBytes = requestedBytes;
            RemainingBytes = remainingBytes;
        }
    }

    /// <summary>
    /// Tracks pre-allocation decoded byte charges during payload deserialization.
    /// </summary>
    public struct DecodeBudget
    {
        public long MaxBytes { get; }
        public long ChargedBytes { get; private set; }
        public long RemainingBytes => MaxBytes - ChargedBytes;

        public DecodeBudget(long maxBytes)
        {
            if (maxBytes < 0L)
            {
                throw new ArgumentOutOfRangeException(nameof(maxBytes));
            }

            MaxBytes = maxBytes;
            ChargedBytes = 0L;
        }

        public void Charge(long bytes)
        {
            if (bytes < 0L)
            {
                throw new InvalidDataException($"Negative allocation charge ({bytes}).");
            }

            if (bytes > RemainingBytes)
            {
                throw new PayloadBudgetExceededException(bytes, RemainingBytes);
            }

            ChargedBytes = checked(ChargedBytes + bytes);
        }
    }

    /// <summary>
    /// Contract implemented by BlendShareContainerWriter and invoked by BlendShareSerializer
    /// whenever a LazyPayload&lt;T&gt; field is encountered while serializing a record.
    /// </summary>
    public interface IPayloadWriteSink
    {
        /// <summary>
        /// Writes or raw-copies a payload entry and returns its assigned 0-based PayloadIndex.
        /// </summary>
        int WritePayload<T>(
            LazyPayload<T> payload,
            PayloadEncodeOptions options,
            string ownerMeshId,
            string ownerFeatureId,
            string codecId,
            int codecVersion,
            Action<Stream, PayloadEncodeOptions> encodeValue) where T : class;
    }

    /// <summary>
    /// Contract implemented by BlendShareContainerReader and passed to BlendShareSerializer
    /// when deserializing records that contain LazyPayload&lt;T&gt; fields.
    /// </summary>
    public interface IPayloadRefResolver
    {
        PayloadRef ResolvePayloadRef(int payloadIndex);
    }

    /// <summary>
    /// Narrow serialization adapter used by BlendShareContainerWriter, BlendShareContainerReader, and PayloadStore.
    /// </summary>
    public interface IBlendShareRecordSerializer
    {
        string RecordPayloadCodecId { get; }
        int RecordPayloadCodecVersion { get; }

        byte[] SerializeRecord<T>(T value, IPayloadWriteSink payloadSink, PayloadEncodeOptions options, string ownerMeshId = "", string ownerFeatureId = "") where T : class;
        T DeserializeRecord<T>(ReadOnlySpan<byte> bytes, IPayloadRefResolver refResolver) where T : class;

        byte[] SerializeFeature(BlendShareFeatureData feature, IPayloadWriteSink payloadSink, PayloadEncodeOptions options, string ownerMeshId);
        BlendShareFeatureData DeserializeFeature(
            string featureId,
            string typeId,
            int schemaVersion,
            bool required,
            ReadOnlySpan<byte> bytes,
            IPayloadRefResolver refResolver,
            long estimatedPayloadBytes = 0L);

        FeatureValidationResult ValidateFeature(BlendShareFeatureData feature, FeatureValidationContext context);

        void SerializePayloadRecord<T>(T value, Stream output, PayloadEncodeOptions options) where T : class;

        /// <summary>
        /// Decodes a verified payload record using the exact stored codec identity while enforcing pre-allocation byte budget.
        /// </summary>
        T DeserializePayloadRecord<T>(
            ReadOnlySpan<byte> bytes,
            string expectedCodecId,
            int expectedCodecVersion,
            long maxDecodedBytes,
            out long actualDecodedBytes) where T : class;

        /// <summary>
        /// Produces an isolated lease view from a cached payload instance so callers cannot mutate cached entries.
        /// Shares immutable IndexedBuffer&lt;T&gt; instances zero-copy while cloning wrapper records and mutable arrays/lists.
        /// </summary>
        T CloneForLease<T>(T cachedValue) where T : class;
    }

    /// <summary>
    /// Escape-hatch interface for rare types requiring custom binary encoding instead of declarative field tags.
    /// Must honor the passed DecodeBudget before allocating buffers.
    /// </summary>
    public interface ICustomBlendShareCodec<T> where T : class
    {
        string CodecId { get; }
        int CodecVersion { get; }
        void Encode(T value, Stream output, PayloadEncodeOptions options);
        T Decode(ReadOnlySpan<byte> bytes, ref DecodeBudget budget);
    }
}
