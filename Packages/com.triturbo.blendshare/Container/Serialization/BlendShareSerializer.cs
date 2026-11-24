using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Triturbo.BlendShare.Container.Wire;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Model.Features;
using Triturbo.BlendShare.Model.Serialization;
using Triturbo.BlendShare.Model.Validation;
using Triturbo.BlendShare.Primitives;

namespace Triturbo.BlendShare.Container.Serialization
{
    /// <summary>
    /// Declarative, tag-based binary serializer for BlendShare records, polymorphic features, and heavy payloads.
    /// Enforces pre-allocation DecodeBudget, exact (Type, CodecId, CodecVersion) dispatch, strict trailing-byte checks,
    /// transitive nested LazyPayload prohibition, symmetric writer/reader limits, and lease isolation cloning.
    /// </summary>
    public sealed class BlendShareSerializer : IBlendShareRecordSerializer
    {
        public const string DefaultPayloadCodecId = "bs.record.v1";
        public const int DefaultPayloadCodecVersion = 1;

        public static readonly BlendShareSerializer Default = new BlendShareSerializer(
            FeatureSchemaRegistry.Default,
            FeatureValidatorRegistry.Default,
            PayloadCodecRegistry.Default);

        private readonly FeatureSchemaRegistry m_FeatureSchemas;
        private readonly FeatureValidatorRegistry m_FeatureValidators;
        private readonly PayloadCodecRegistry m_CustomCodecs;
        private readonly ConcurrentDictionary<Type, RecordSchema> m_SchemaCache = new ConcurrentDictionary<Type, RecordSchema>();

        public string RecordPayloadCodecId => DefaultPayloadCodecId;
        public int RecordPayloadCodecVersion => DefaultPayloadCodecVersion;

        public BlendShareSerializer(
            FeatureSchemaRegistry featureSchemas = null,
            FeatureValidatorRegistry featureValidators = null,
            PayloadCodecRegistry customCodecs = null)
        {
            m_FeatureSchemas = featureSchemas ?? FeatureSchemaRegistry.Default;
            m_FeatureValidators = featureValidators ?? FeatureValidatorRegistry.Default;
            m_CustomCodecs = customCodecs ?? PayloadCodecRegistry.Default;
        }

        public byte[] SerializeRecord<T>(
            T value,
            IPayloadWriteSink payloadSink,
            PayloadEncodeOptions options,
            string ownerMeshId = "",
            string ownerFeatureId = "") where T : class
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            using (var ms = new MemoryStream(256))
            {
                var writer = new WireWriter(ms);
                var ctx = new WriteContext(payloadSink, options, ownerMeshId ?? string.Empty, ownerFeatureId ?? string.Empty, insidePayload: false);
                WriteRecordBody(writer, value.GetType(), value, ref ctx, depth: 0);
                return ms.ToArray();
            }
        }

        public T DeserializeRecord<T>(ReadOnlySpan<byte> bytes, IPayloadRefResolver refResolver) where T : class
        {
            var reader = new WireReader(bytes);
            var budget = new DecodeBudget(WireLimits.MaxUncompressedEnvelopeBytes);
            var result = (T)ReadRecordBody(ref reader, typeof(T), refResolver, depth: 0, ref budget);
            if (!reader.IsAtEnd)
            {
                throw new InvalidDataException($"Unexpected trailing bytes ({reader.Remaining}) after record '{typeof(T).Name}'.");
            }

            return result;
        }

        public byte[] SerializeFeature(
            BlendShareFeatureData feature,
            IPayloadWriteSink payloadSink,
            PayloadEncodeOptions options,
            string ownerMeshId)
        {
            if (feature == null)
            {
                throw new ArgumentNullException(nameof(feature));
            }

            var ctx = new WriteContext(payloadSink, options, ownerMeshId ?? string.Empty, feature.FeatureId ?? string.Empty, insidePayload: false);
            return SerializeFeatureInternal(feature, ref ctx, depth: 0);
        }

        public BlendShareFeatureData DeserializeFeature(
            string featureId,
            string typeId,
            int schemaVersion,
            bool required,
            ReadOnlySpan<byte> bytes,
            IPayloadRefResolver refResolver,
            long estimatedPayloadBytes = 0L)
        {
            var budget = new DecodeBudget(WireLimits.MaxUncompressedEnvelopeBytes);
            return DeserializeFeatureInternal(featureId, typeId, schemaVersion, required, bytes, refResolver, depth: 0, ref budget, estimatedPayloadBytes);
        }

        public FeatureValidationResult ValidateFeature(BlendShareFeatureData feature, FeatureValidationContext context)
        {
            var result = new FeatureValidationResult();
            if (feature == null)
            {
                result.AddError("BS_VAL_NULL_FEATURE", "Feature is null.");
                return result;
            }

            if (feature is UnknownFeatureData unknown)
            {
                if (unknown.Required)
                {
                    result.AddError("BS_VAL_UNSUPPORTED_REQUIRED", $"Required feature '{unknown.TypeId}' (v{unknown.SchemaVersion}) is not supported.");
                }
                else
                {
                    result.AddWarning("BS_VAL_UNSUPPORTED_OPTIONAL", $"Optional feature '{unknown.TypeId}' (v{unknown.SchemaVersion}) is not supported.");
                }

                return result;
            }

            if (m_FeatureValidators.TryGetValidator(feature.TypeId, out var validator))
            {
                validator.Validate(feature, context, result);
            }

            return result;
        }

        public void SerializePayloadRecord<T>(T value, Stream output, PayloadEncodeOptions options) where T : class
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            if (output == null)
            {
                throw new ArgumentNullException(nameof(output));
            }

            if (m_CustomCodecs.TryGetWriterCodec<T>(out var customCodec))
            {
                using (var rawMs = new MemoryStream(256))
                {
                    customCodec.Encode(value, rawMs, options);
                    CompressionEnvelope.Compress(rawMs.ToArray(), output, options.Compression);
                    return;
                }
            }

            var schema = GetOrBuildSchema(typeof(T), forbidLazyPayloads: true);
            using (var rawMs = new MemoryStream(512))
            {
                var writer = new WireWriter(rawMs);
                var ctx = new WriteContext(null, options, string.Empty, string.Empty, insidePayload: true);
                WriteRecordBody(writer, schema, value, ref ctx, depth: 0);
                CompressionEnvelope.Compress(rawMs.ToArray(), output, options.Compression);
            }
        }

        public T DeserializePayloadRecord<T>(
            ReadOnlySpan<byte> bytes,
            string expectedCodecId,
            int expectedCodecVersion,
            long maxDecodedBytes,
            out long actualDecodedBytes) where T : class
        {
            if (string.IsNullOrWhiteSpace(expectedCodecId) || expectedCodecVersion <= 0)
            {
                throw new InvalidDataException($"Invalid payload codec identity '{expectedCodecId}@v{expectedCodecVersion}'.");
            }

            if (maxDecodedBytes < 0L)
            {
                throw new ArgumentOutOfRangeException(nameof(maxDecodedBytes));
            }

            long uncompressedLen = CompressionEnvelope.PeekUncompressedLength(bytes);
            if (uncompressedLen > WireLimits.MaxUncompressedEnvelopeBytes)
            {
                throw new InvalidDataException($"Uncompressed payload size {uncompressedLen} exceeds wire cap.");
            }

            // Decompress within WireLimits cap; actual decoded object budget is strictly enforced via DecodeBudget below.
            byte[] raw = CompressionEnvelope.Decompress(bytes, WireLimits.MaxUncompressedEnvelopeBytes);
            var budget = new DecodeBudget(maxDecodedBytes);

            if (string.Equals(expectedCodecId, DefaultPayloadCodecId, StringComparison.Ordinal) &&
                expectedCodecVersion == DefaultPayloadCodecVersion)
            {
                var schema = GetOrBuildSchema(typeof(T), forbidLazyPayloads: true);
                var reader = new WireReader(raw);
                var result = (T)ReadRecordBody(ref reader, schema, refResolver: null, depth: 0, ref budget);
                if (!reader.IsAtEnd)
                {
                    throw new InvalidDataException($"Unexpected trailing bytes ({reader.Remaining}) in payload record '{typeof(T).Name}'.");
                }

                actualDecodedBytes = budget.ChargedBytes;
                return result;
            }

            if (m_CustomCodecs.TryGetDecoderCodec<T>(expectedCodecId, expectedCodecVersion, out var customCodec))
            {
                var result = customCodec.Decode(raw, ref budget);
                actualDecodedBytes = budget.ChargedBytes;
                return result;
            }

            throw new NotSupportedException(
                $"Unsupported payload codec '{expectedCodecId}@v{expectedCodecVersion}' for payload type '{typeof(T).Name}'.");
        }

        public T DeserializePayloadRecord<T>(ReadOnlySpan<byte> bytes, out long actualDecodedBytes) where T : class
        {
            string codecId = RecordPayloadCodecId;
            int codecVersion = RecordPayloadCodecVersion;
            if (m_CustomCodecs.TryGetWriterCodec<T>(out var custom))
            {
                codecId = custom.CodecId;
                codecVersion = custom.CodecVersion;
            }

            return DeserializePayloadRecord<T>(bytes, codecId, codecVersion, WireLimits.MaxUncompressedEnvelopeBytes, out actualDecodedBytes);
        }

        public T CloneForLease<T>(T cachedValue) where T : class
        {
            if (cachedValue == null)
            {
                return null;
            }

            if (m_CustomCodecs.TryGetWriterCodec<T>(out _))
            {
                // For custom-codec payloads that aren't declarative records, return as-is if not a BlendShareRecord.
                if (typeof(T).GetCustomAttribute<BlendShareRecordAttribute>(inherit: false) == null &&
                    !typeof(BlendShareFeatureData).IsAssignableFrom(typeof(T)))
                {
                    return cachedValue;
                }
            }

            return (T)CloneRecordInstance(cachedValue.GetType(), cachedValue, depth: 0);
        }

        // ============================================================
        // Lease defensive cloning (zero-copy for immutable IndexedBuffer<T>)
        // ============================================================

        private object CloneRecordInstance(Type type, object instance, int depth)
        {
            if (instance == null) return null;
            if (depth > WireLimits.MaxNestingDepth)
            {
                throw new InvalidDataException($"Exceeded max nesting depth while cloning '{type.Name}'.");
            }

            var schema = GetOrBuildSchema(type, forbidLazyPayloads: false);
            object clone = Activator.CreateInstance(schema.RecordType);
            var fields = schema.OrderedFields;
            for (int i = 0; i < fields.Length; i++)
            {
                var f = fields[i];
                object val = f.FieldInfo.GetValue(instance);
                if (val == null)
                {
                    f.FieldInfo.SetValue(clone, null);
                    continue;
                }

                switch (f.Kind)
                {
                    case FieldKind.IndexedVector3d:
                    case FieldKind.IndexedDouble:
                        // IndexedBuffer<T> is strictly immutable -> zero-copy share!
                        f.FieldInfo.SetValue(clone, val);
                        break;
                    case FieldKind.ByteArray:
                        f.FieldInfo.SetValue(clone, ((byte[])val).Clone());
                        break;
                    case FieldKind.Int32Array:
                        f.FieldInfo.SetValue(clone, ((int[])val).Clone());
                        break;
                    case FieldKind.DoubleArray:
                        f.FieldInfo.SetValue(clone, ((double[])val).Clone());
                        break;
                    case FieldKind.Vector3dArray:
                        f.FieldInfo.SetValue(clone, ((Vector3d[])val).Clone());
                        break;
                    case FieldKind.Record:
                        f.FieldInfo.SetValue(clone, CloneRecordInstance(f.FieldType, val, depth + 1));
                        break;
                    case FieldKind.List:
                    {
                        var srcList = (IList)val;
                        var dstList = (IList)Activator.CreateInstance(f.FieldType, srcList.Count);
                        bool elemIsRecord = f.ElementType.GetCustomAttribute<BlendShareRecordAttribute>(inherit: false) != null;
                        for (int j = 0; j < srcList.Count; j++)
                        {
                            object elem = srcList[j];
                            dstList.Add(elemIsRecord && elem != null
                                ? CloneRecordInstance(f.ElementType, elem, depth + 1)
                                : elem);
                        }
                        f.FieldInfo.SetValue(clone, dstList);
                        break;
                    }
                    default:
                        // Value types, strings, enums, LazyPayload<T> (immutable)
                        f.FieldInfo.SetValue(clone, val);
                        break;
                }
            }

            return clone;
        }

        // ============================================================
        // Internal feature serialization/deserialization (preserves depth & budget)
        // ============================================================

        private byte[] SerializeFeatureInternal(BlendShareFeatureData feature, ref WriteContext ctx, int depth)
        {
            if (depth > WireLimits.MaxNestingDepth)
            {
                throw new InvalidDataException($"Exceeded maximum feature nesting depth ({WireLimits.MaxNestingDepth}).");
            }

            if (feature is UnknownFeatureData unknown)
            {
                return unknown.RawEnvelopeBytes != null
                    ? (byte[])unknown.RawEnvelopeBytes.Clone()
                    : Array.Empty<byte>();
            }

            using (var ms = new MemoryStream(256))
            {
                var writer = new WireWriter(ms);
                var featureCtx = new WriteContext(ctx.PayloadSink, ctx.Options, ctx.OwnerMeshId, feature.FeatureId ?? string.Empty, ctx.InsidePayload);
                WriteRecordBody(writer, feature.GetType(), feature, ref featureCtx, depth);
                return ms.ToArray();
            }
        }

        private BlendShareFeatureData DeserializeFeatureInternal(
            string featureId,
            string typeId,
            int schemaVersion,
            bool required,
            ReadOnlySpan<byte> bytes,
            IPayloadRefResolver refResolver,
            int depth,
            ref DecodeBudget budget,
            long estimatedPayloadBytes = 0L)
        {
            if (depth > WireLimits.MaxNestingDepth)
            {
                throw new InvalidDataException($"Exceeded maximum feature nesting depth ({WireLimits.MaxNestingDepth}).");
            }

            if (!m_FeatureSchemas.TryGetFeatureType(typeId, out var clrType))
            {
                budget.Charge(checked(64L + bytes.Length));
                return new UnknownFeatureData(featureId, typeId, schemaVersion, required, bytes.ToArray(), estimatedPayloadBytes);
            }

            if (m_FeatureSchemas.TryGetFeatureAttribute(clrType, out var attr) && schemaVersion > attr.SchemaVersion)
            {
                budget.Charge(checked(64L + bytes.Length));
                return new UnknownFeatureData(featureId, typeId, schemaVersion, required, bytes.ToArray(), estimatedPayloadBytes);
            }

            var reader = new WireReader(bytes);
            var feature = (BlendShareFeatureData)ReadRecordBody(ref reader, clrType, refResolver, depth, ref budget);
            if (!reader.IsAtEnd)
            {
                throw new InvalidDataException($"Unexpected trailing bytes ({reader.Remaining}) in feature '{typeId}'.");
            }

            if (string.IsNullOrEmpty(feature.FeatureId))
            {
                feature.FeatureId = featureId ?? string.Empty;
            }

            feature.TypeId = typeId ?? feature.TypeId;
            feature.SchemaVersion = schemaVersion;
            feature.Required = required;
            return feature;
        }

        // ============================================================
        // Record writing & reading
        // ============================================================

        private struct WriteContext
        {
            public readonly IPayloadWriteSink PayloadSink;
            public readonly PayloadEncodeOptions Options;
            public readonly string OwnerMeshId;
            public readonly string OwnerFeatureId;
            public readonly bool InsidePayload;

            public WriteContext(
                IPayloadWriteSink payloadSink,
                PayloadEncodeOptions options,
                string ownerMeshId,
                string ownerFeatureId,
                bool insidePayload)
            {
                PayloadSink = payloadSink;
                Options = options;
                OwnerMeshId = ownerMeshId;
                OwnerFeatureId = ownerFeatureId;
                InsidePayload = insidePayload;
            }
        }

        private void WriteRecordBody(WireWriter writer, Type type, object instance, ref WriteContext ctx, int depth)
        {
            var schema = GetOrBuildSchema(type, forbidLazyPayloads: ctx.InsidePayload);
            WriteRecordBody(writer, schema, instance, ref ctx, depth);
        }

        private void WriteRecordBody(WireWriter writer, RecordSchema schema, object instance, ref WriteContext ctx, int depth)
        {
            if (depth > WireLimits.MaxNestingDepth)
            {
                throw new InvalidDataException($"Exceeded maximum record nesting depth ({WireLimits.MaxNestingDepth}).");
            }

            var fields = schema.OrderedFields;
            for (int i = 0; i < fields.Length; i++)
            {
                var field = fields[i];
                object fieldValue = field.FieldInfo.GetValue(instance);
                WriteField(writer, field, fieldValue, ref ctx, depth);
            }
        }

        private void WriteField(WireWriter writer, FieldEntry field, object value, ref WriteContext ctx, int depth)
        {
            switch (field.Kind)
            {
                case FieldKind.Bool:
                    writer.WriteTag(field.Tag, WireType.Varint);
                    writer.WriteBool((bool)value);
                    break;
                case FieldKind.Byte:
                    writer.WriteTag(field.Tag, WireType.Varint);
                    writer.WriteVarUInt32((byte)value);
                    break;
                case FieldKind.Int32:
                    writer.WriteTag(field.Tag, WireType.Varint);
                    writer.WriteVarInt32((int)value);
                    break;
                case FieldKind.Int64:
                    writer.WriteTag(field.Tag, WireType.Varint);
                    writer.WriteVarInt64((long)value);
                    break;
                case FieldKind.Enum:
                    writer.WriteTag(field.Tag, WireType.Varint);
                    writer.WriteVarInt64(Convert.ToInt64(value));
                    break;
                case FieldKind.Float32:
                    writer.WriteTag(field.Tag, WireType.Fixed32);
                    writer.WriteSingle((float)value);
                    break;
                case FieldKind.Float64:
                    writer.WriteTag(field.Tag, WireType.Fixed64);
                    writer.WriteDouble((double)value);
                    break;
                case FieldKind.String:
                    if (value == null) return;
                    writer.WriteTag(field.Tag, WireType.LengthDelimited);
                    using (var ms = new MemoryStream())
                    {
                        new WireWriter(ms).WriteString((string)value);
                        WriteCheckedDelimited(writer, ms);
                    }
                    break;
                case FieldKind.ByteArray:
                    if (value == null) return;
                    var rawBytes = (byte[])value;
                    if (rawBytes.Length > WireLimits.MaxByteArrayBytes)
                    {
                        throw new InvalidDataException($"byte[] length {rawBytes.Length} exceeds limit ({WireLimits.MaxByteArrayBytes}).");
                    }
                    writer.WriteTag(field.Tag, WireType.LengthDelimited);
                    writer.WriteBytesWithLength(rawBytes);
                    break;
                case FieldKind.Vector2d:
                    writer.WriteTag(field.Tag, WireType.LengthDelimited);
                    using (var ms = new MemoryStream(16))
                    {
                        new WireWriter(ms).WriteVector2d((Vector2d)value);
                        writer.WriteBytesWithLength(ms.ToArray());
                    }
                    break;
                case FieldKind.Vector3d:
                    writer.WriteTag(field.Tag, WireType.LengthDelimited);
                    using (var ms = new MemoryStream(24))
                    {
                        new WireWriter(ms).WriteVector3d((Vector3d)value);
                        writer.WriteBytesWithLength(ms.ToArray());
                    }
                    break;
                case FieldKind.Vector4d:
                    writer.WriteTag(field.Tag, WireType.LengthDelimited);
                    using (var ms = new MemoryStream(32))
                    {
                        new WireWriter(ms).WriteVector4d((Vector4d)value);
                        writer.WriteBytesWithLength(ms.ToArray());
                    }
                    break;
                case FieldKind.Matrix4x4:
                    writer.WriteTag(field.Tag, WireType.LengthDelimited);
                    using (var ms = new MemoryStream(128))
                    {
                        new WireWriter(ms).WriteMatrix4x4((FbxMatrix4x4)value);
                        writer.WriteBytesWithLength(ms.ToArray());
                    }
                    break;
                case FieldKind.Int32Array:
                    if (value == null) return;
                    var intArr = (int[])value;
                    if (intArr.Length > WireLimits.MaxCollectionCount)
                    {
                        throw new InvalidDataException($"int[] length {intArr.Length} exceeds limit ({WireLimits.MaxCollectionCount}).");
                    }
                    writer.WriteTag(field.Tag, WireType.LengthDelimited);
                    using (var ms = new MemoryStream())
                    {
                        var w = new WireWriter(ms);
                        w.WriteVarUInt32(checked((uint)intArr.Length));
                        for (int i = 0; i < intArr.Length; i++) w.WriteVarInt32(intArr[i]);
                        WriteCheckedDelimited(writer, ms);
                    }
                    break;
                case FieldKind.DoubleArray:
                    if (value == null) return;
                    writer.WriteTag(field.Tag, WireType.LengthDelimited);
                    using (var ms = new MemoryStream())
                    {
                        SemanticArrayCodecs.WriteDenseOrAdaptiveDoubleArray(new WireWriter(ms), (double[])value, field.Semantic, ctx.Options);
                        WriteCheckedDelimited(writer, ms);
                    }
                    break;
                case FieldKind.Vector3dArray:
                    if (value == null) return;
                    writer.WriteTag(field.Tag, WireType.LengthDelimited);
                    using (var ms = new MemoryStream())
                    {
                        SemanticArrayCodecs.WriteDenseOrAdaptiveVector3dArray(new WireWriter(ms), (Vector3d[])value, field.Semantic, ctx.Options);
                        WriteCheckedDelimited(writer, ms);
                    }
                    break;
                case FieldKind.IndexedVector3d:
                    if (value == null) return;
                    writer.WriteTag(field.Tag, WireType.LengthDelimited);
                    using (var ms = new MemoryStream())
                    {
                        SemanticArrayCodecs.WriteIndexedVector3d(new WireWriter(ms), (IndexedBuffer<Vector3d>)value, field.Semantic, ctx.Options);
                        WriteCheckedDelimited(writer, ms);
                    }
                    break;
                case FieldKind.IndexedDouble:
                    if (value == null) return;
                    writer.WriteTag(field.Tag, WireType.LengthDelimited);
                    using (var ms = new MemoryStream())
                    {
                        SemanticArrayCodecs.WriteIndexedDouble(new WireWriter(ms), (IndexedBuffer<double>)value, field.Semantic, ctx.Options);
                        WriteCheckedDelimited(writer, ms);
                    }
                    break;
                case FieldKind.Record:
                    if (value == null) return;
                    writer.WriteTag(field.Tag, WireType.LengthDelimited);
                    using (var ms = new MemoryStream())
                    {
                        WriteRecordBody(new WireWriter(ms), field.FieldType, value, ref ctx, depth + 1);
                        WriteCheckedDelimited(writer, ms);
                    }
                    break;
                case FieldKind.List:
                    if (value == null) return;
                    writer.WriteTag(field.Tag, WireType.LengthDelimited);
                    using (var ms = new MemoryStream())
                    {
                        WriteListBody(new WireWriter(ms), field.ElementType, (IList)value, ref ctx, depth + 1);
                        WriteCheckedDelimited(writer, ms);
                    }
                    break;
                case FieldKind.LazyPayload:
                    if (value == null) return;
                    if (ctx.InsidePayload)
                    {
                        throw new InvalidOperationException($"Nested LazyPayload<{field.ElementType.Name}> is forbidden inside a payload record.");
                    }

                    int payloadIndex = DispatchWriteLazyPayload(field.ElementType, value, ref ctx);
                    if (payloadIndex >= 0)
                    {
                        writer.WriteTag(field.Tag, WireType.Varint);
                        writer.WriteVarInt32(payloadIndex);
                    }
                    break;
                default:
                    throw new NotSupportedException($"Unsupported field kind {field.Kind} on field '{field.FieldInfo.Name}'.");
            }
        }

        private static void WriteCheckedDelimited(WireWriter writer, MemoryStream ms)
        {
            if (ms.Length > WireLimits.MaxByteArrayBytes)
            {
                throw new InvalidDataException($"Encoded field length {ms.Length} exceeds limit ({WireLimits.MaxByteArrayBytes}).");
            }

            writer.WriteBytesWithLength(ms.ToArray());
        }

        private void WriteListBody(WireWriter writer, Type elementType, IList list, ref WriteContext ctx, int depth)
        {
            if (list.Count > WireLimits.MaxCollectionCount)
            {
                throw new InvalidDataException($"List count {list.Count} exceeds limit ({WireLimits.MaxCollectionCount}).");
            }

            writer.WriteVarUInt32(checked((uint)list.Count));
            if (elementType == typeof(string))
            {
                for (int i = 0; i < list.Count; i++) writer.WriteString((string)list[i]);
                return;
            }

            if (elementType == typeof(int))
            {
                for (int i = 0; i < list.Count; i++) writer.WriteVarInt32((int)list[i]);
                return;
            }

            if (typeof(BlendShareFeatureData).IsAssignableFrom(elementType))
            {
                for (int i = 0; i < list.Count; i++)
                {
                    var feat = (BlendShareFeatureData)list[i];
                    if (feat == null)
                    {
                        writer.WriteByte(0);
                        continue;
                    }

                    writer.WriteByte(1);
                    writer.WriteString(feat.FeatureId ?? string.Empty);
                    writer.WriteString(feat.TypeId ?? string.Empty);
                    writer.WriteVarInt32(feat.SchemaVersion);
                    writer.WriteBool(feat.Required);
                    byte[] featBytes = SerializeFeatureInternal(feat, ref ctx, depth + 1);
                    if (featBytes.Length > WireLimits.MaxByteArrayBytes)
                    {
                        throw new InvalidDataException($"Encoded feature length {featBytes.Length} exceeds limit.");
                    }
                    writer.WriteBytesWithLength(featBytes);
                }
                return;
            }

            for (int i = 0; i < list.Count; i++)
            {
                object item = list[i];
                if (item == null)
                {
                    writer.WriteByte(0);
                    continue;
                }

                writer.WriteByte(1);
                using (var ms = new MemoryStream())
                {
                    WriteRecordBody(new WireWriter(ms), elementType, item, ref ctx, depth + 1);
                    WriteCheckedDelimited(writer, ms);
                }
            }
        }

        private object ReadRecordBody(ref WireReader reader, Type type, IPayloadRefResolver refResolver, int depth, ref DecodeBudget budget)
        {
            var schema = GetOrBuildSchema(type, forbidLazyPayloads: refResolver == null);
            return ReadRecordBody(ref reader, schema, refResolver, depth, ref budget);
        }

        private object ReadRecordBody(ref WireReader reader, RecordSchema schema, IPayloadRefResolver refResolver, int depth, ref DecodeBudget budget)
        {
            if (depth > WireLimits.MaxNestingDepth)
            {
                throw new InvalidDataException($"Exceeded maximum record nesting depth ({WireLimits.MaxNestingDepth}).");
            }

            budget.Charge(64L);
            object instance = Activator.CreateInstance(schema.RecordType);

            var seenTags = new HashSet<int>();
            while (reader.TryReadTag(out int tag, out var wireType))
            {
                if (!seenTags.Add(tag))
                {
                    throw new InvalidDataException($"Duplicate field tag {tag} in record '{schema.RecordType.Name}'.");
                }

                if (!schema.ByTag.TryGetValue(tag, out var field))
                {
                    reader.SkipField(wireType);
                    continue;
                }

                if (wireType != field.ExpectedWireType)
                {
                    throw new InvalidDataException(
                        $"Wire type mismatch for field '{schema.RecordType.Name}.{field.FieldInfo.Name}' (tag {tag}): expected {field.ExpectedWireType}, got {wireType}.");
                }

                object fieldValue = ReadField(ref reader, field, refResolver, depth, ref budget);
                field.FieldInfo.SetValue(instance, fieldValue);
            }

            return instance;
        }

        private object ReadField(ref WireReader reader, FieldEntry field, IPayloadRefResolver refResolver, int depth, ref DecodeBudget budget)
        {
            switch (field.Kind)
            {
                case FieldKind.Bool:
                    return reader.ReadBool();
                case FieldKind.Byte:
                {
                    uint v = reader.ReadVarUInt32();
                    if (v > byte.MaxValue) throw new InvalidDataException($"Byte field overflow: {v}.");
                    return (byte)v;
                }
                case FieldKind.Int32:
                    return reader.ReadVarInt32();
                case FieldKind.Int64:
                    return reader.ReadVarInt64();
                case FieldKind.Enum:
                    return Enum.ToObject(field.FieldType, reader.ReadVarInt64());
                case FieldKind.Float32:
                    return reader.ReadSingle();
                case FieldKind.Float64:
                    return reader.ReadDouble();
                case FieldKind.String:
                {
                    var slice = reader.ReadLengthDelimitedSlice(WireLimits.MaxStringBytes + 16);
                    var sub = new WireReader(slice);
                    string s = sub.ReadString();
                    EnsureExactEnd(ref sub, field.FieldInfo.Name);
                    if (s != null) budget.Charge(checked((long)s.Length * 2L));
                    return s;
                }
                case FieldKind.ByteArray:
                {
                    var slice = reader.ReadLengthDelimitedSlice(WireLimits.MaxByteArrayBytes);
                    budget.Charge(slice.Length);
                    return slice.ToArray();
                }
                case FieldKind.Vector2d:
                {
                    var slice = reader.ReadLengthDelimitedSlice(16);
                    if (slice.Length != 16) throw new InvalidDataException($"Vector2d requires exactly 16 bytes, got {slice.Length}.");
                    var sub = new WireReader(slice);
                    return sub.ReadVector2d();
                }
                case FieldKind.Vector3d:
                {
                    var slice = reader.ReadLengthDelimitedSlice(24);
                    if (slice.Length != 24) throw new InvalidDataException($"Vector3d requires exactly 24 bytes, got {slice.Length}.");
                    var sub = new WireReader(slice);
                    return sub.ReadVector3d();
                }
                case FieldKind.Vector4d:
                {
                    var slice = reader.ReadLengthDelimitedSlice(32);
                    if (slice.Length != 32) throw new InvalidDataException($"Vector4d requires exactly 32 bytes, got {slice.Length}.");
                    var sub = new WireReader(slice);
                    return sub.ReadVector4d();
                }
                case FieldKind.Matrix4x4:
                {
                    var slice = reader.ReadLengthDelimitedSlice(128);
                    if (slice.Length != 128) throw new InvalidDataException($"FbxMatrix4x4 requires exactly 128 bytes, got {slice.Length}.");
                    budget.Charge(128L);
                    var sub = new WireReader(slice);
                    return sub.ReadMatrix4x4();
                }
                case FieldKind.Int32Array:
                {
                    var sub = new WireReader(reader.ReadLengthDelimitedSlice(WireLimits.MaxByteArrayBytes));
                    int count = checked((int)sub.ReadVarUInt32());
                    if (count > WireLimits.MaxCollectionCount)
                    {
                        throw new InvalidDataException($"int[] count {count} exceeds limit.");
                    }

                    budget.Charge(checked((long)count * sizeof(int)));
                    var arr = count == 0 ? Array.Empty<int>() : new int[count];
                    for (int i = 0; i < count; i++) arr[i] = sub.ReadVarInt32();
                    EnsureExactEnd(ref sub, field.FieldInfo.Name);
                    return arr;
                }
                case FieldKind.DoubleArray:
                {
                    var sub = new WireReader(reader.ReadLengthDelimitedSlice(WireLimits.MaxByteArrayBytes));
                    var arr = SemanticArrayCodecs.ReadDenseOrAdaptiveDoubleArray(ref sub, ref budget);
                    EnsureExactEnd(ref sub, field.FieldInfo.Name);
                    return arr;
                }
                case FieldKind.Vector3dArray:
                {
                    var sub = new WireReader(reader.ReadLengthDelimitedSlice(WireLimits.MaxByteArrayBytes));
                    var arr = SemanticArrayCodecs.ReadDenseOrAdaptiveVector3dArray(ref sub, ref budget);
                    EnsureExactEnd(ref sub, field.FieldInfo.Name);
                    return arr;
                }
                case FieldKind.IndexedVector3d:
                {
                    var sub = new WireReader(reader.ReadLengthDelimitedSlice(WireLimits.MaxByteArrayBytes));
                    var buf = SemanticArrayCodecs.ReadIndexedVector3d(ref sub, ref budget);
                    EnsureExactEnd(ref sub, field.FieldInfo.Name);
                    return buf;
                }
                case FieldKind.IndexedDouble:
                {
                    var sub = new WireReader(reader.ReadLengthDelimitedSlice(WireLimits.MaxByteArrayBytes));
                    var buf = SemanticArrayCodecs.ReadIndexedDouble(ref sub, ref budget);
                    EnsureExactEnd(ref sub, field.FieldInfo.Name);
                    return buf;
                }
                case FieldKind.Record:
                {
                    var sub = new WireReader(reader.ReadLengthDelimitedSlice(WireLimits.MaxByteArrayBytes));
                    var obj = ReadRecordBody(ref sub, field.FieldType, refResolver, depth + 1, ref budget);
                    EnsureExactEnd(ref sub, field.FieldInfo.Name);
                    return obj;
                }
                case FieldKind.List:
                {
                    var sub = new WireReader(reader.ReadLengthDelimitedSlice(WireLimits.MaxByteArrayBytes));
                    var list = ReadListBody(ref sub, field.FieldType, field.ElementType, refResolver, depth + 1, ref budget);
                    EnsureExactEnd(ref sub, field.FieldInfo.Name);
                    return list;
                }
                case FieldKind.LazyPayload:
                {
                    int payloadIndex = reader.ReadVarInt32();
                    if (payloadIndex < 0)
                    {
                        throw new InvalidDataException($"Invalid negative PayloadIndex {payloadIndex}.");
                    }

                    if (refResolver == null)
                    {
                        throw new InvalidOperationException("Cannot deserialize LazyPayload<T> without an IPayloadRefResolver.");
                    }

                    var payloadRef = refResolver.ResolvePayloadRef(payloadIndex);
                    return CreateLazyPayloadFromRef(field.ElementType, payloadRef);
                }
                default:
                    throw new NotSupportedException($"Unsupported field kind {field.Kind}.");
            }
        }

        private object ReadListBody(ref WireReader reader, Type listType, Type elementType, IPayloadRefResolver refResolver, int depth, ref DecodeBudget budget)
        {
            int count = checked((int)reader.ReadVarUInt32());
            if (count > WireLimits.MaxCollectionCount)
            {
                throw new InvalidDataException($"List count {count} exceeds limit ({WireLimits.MaxCollectionCount}).");
            }

            // Charge list backing array capacity BEFORE allocation (8 bytes/ref or 4 bytes/int).
            long elemBackingSize = elementType == typeof(int) ? sizeof(int) : 8L;
            budget.Charge(checked((long)count * elemBackingSize));

            var list = (IList)Activator.CreateInstance(listType, count);
            if (elementType == typeof(string))
            {
                for (int i = 0; i < count; i++)
                {
                    string s = reader.ReadString();
                    if (s != null) budget.Charge(checked((long)s.Length * 2L));
                    list.Add(s);
                }
                return list;
            }

            if (elementType == typeof(int))
            {
                for (int i = 0; i < count; i++) list.Add(reader.ReadVarInt32());
                return list;
            }

            if (typeof(BlendShareFeatureData).IsAssignableFrom(elementType))
            {
                for (int i = 0; i < count; i++)
                {
                    byte present = reader.ReadByte();
                    if (present == 0)
                    {
                        list.Add(null);
                        continue;
                    }

                    if (present != 1) throw new InvalidDataException($"Invalid list presence marker {present}.");
                    string featureId = reader.ReadString();
                    string typeId = reader.ReadString();
                    int schemaVersion = reader.ReadVarInt32();
                    bool required = reader.ReadBool();
                    var featBytes = reader.ReadLengthDelimitedSlice(WireLimits.MaxByteArrayBytes);
                    list.Add(DeserializeFeatureInternal(featureId, typeId, schemaVersion, required, featBytes, refResolver, depth + 1, ref budget));
                }
                return list;
            }

            for (int i = 0; i < count; i++)
            {
                byte present = reader.ReadByte();
                if (present == 0)
                {
                    list.Add(null);
                    continue;
                }

                if (present != 1) throw new InvalidDataException($"Invalid list presence marker {present}.");
                var sub = new WireReader(reader.ReadLengthDelimitedSlice(WireLimits.MaxByteArrayBytes));
                var item = ReadRecordBody(ref sub, elementType, refResolver, depth + 1, ref budget);
                EnsureExactEnd(ref sub, elementType.Name);
                list.Add(item);
            }

            return list;
        }

        private static void EnsureExactEnd(ref WireReader reader, string fieldName)
        {
            if (!reader.IsAtEnd)
            {
                throw new InvalidDataException($"Unexpected trailing bytes ({reader.Remaining}) in field '{fieldName}'.");
            }
        }

        // ============================================================
        // LazyPayload<T> generic dispatch helpers
        // ============================================================

        private static readonly MethodInfo s_WriteLazyPayloadMethod =
            typeof(BlendShareSerializer).GetMethod(nameof(WriteLazyPayloadTyped), BindingFlags.Instance | BindingFlags.NonPublic);

        private int DispatchWriteLazyPayload(Type payloadType, object lazyPayloadObj, ref WriteContext ctx)
        {
            var closed = s_WriteLazyPayloadMethod.MakeGenericMethod(payloadType);
            return (int)closed.Invoke(this, new object[] { lazyPayloadObj, ctx });
        }

        private int WriteLazyPayloadTyped<T>(LazyPayload<T> payload, WriteContext ctx) where T : class
        {
            if (payload == null || payload.State == LazyPayloadState.Absent)
            {
                return -1;
            }

            if (ctx.PayloadSink == null)
            {
                throw new InvalidOperationException($"Cannot serialize non-empty LazyPayload<{typeof(T).Name}> without an IPayloadWriteSink.");
            }

            string codecId = RecordPayloadCodecId;
            int codecVersion = RecordPayloadCodecVersion;
            if (m_CustomCodecs.TryGetWriterCodec<T>(out var customCodec))
            {
                codecId = customCodec.CodecId;
                codecVersion = customCodec.CodecVersion;
            }

            return ctx.PayloadSink.WritePayload(
                payload,
                ctx.Options,
                ctx.OwnerMeshId,
                ctx.OwnerFeatureId,
                codecId,
                codecVersion,
                (stream, opts) => SerializePayloadRecord(payload.DirectValue, stream, opts));
        }

        private static object CreateLazyPayloadFromRef(Type payloadType, PayloadRef payloadRef)
        {
            var closed = typeof(LazyPayload<>).MakeGenericType(payloadType);
            var method = closed.GetMethod(nameof(LazyPayload<object>.FromRef), BindingFlags.Public | BindingFlags.Static);
            return method.Invoke(null, new object[] { payloadRef });
        }

        // ============================================================
        // Schema reflection, caching & transitive LazyPayload check
        // ============================================================

        private RecordSchema GetOrBuildSchema(Type type, bool forbidLazyPayloads)
        {
            var schema = m_SchemaCache.GetOrAdd(type, BuildSchema);
            if (forbidLazyPayloads && HasTransitiveLazyPayload(schema, new HashSet<Type>()))
            {
                throw new InvalidOperationException($"Payload record type '{type.FullName}' cannot contain a direct or nested LazyPayload<T>.");
            }

            return schema;
        }

        private bool HasTransitiveLazyPayload(RecordSchema schema, HashSet<Type> visited)
        {
            if (!visited.Add(schema.RecordType))
            {
                return false;
            }

            if (schema.ContainsDirectLazyPayload)
            {
                return true;
            }

            var fields = schema.OrderedFields;
            for (int i = 0; i < fields.Length; i++)
            {
                var f = fields[i];
                if (f.Kind == FieldKind.Record)
                {
                    var child = m_SchemaCache.GetOrAdd(f.FieldType, BuildSchema);
                    if (HasTransitiveLazyPayload(child, visited))
                    {
                        return true;
                    }
                }
                else if (f.Kind == FieldKind.List && f.ElementType != null &&
                         f.ElementType != typeof(string) && f.ElementType != typeof(int) &&
                         !typeof(BlendShareFeatureData).IsAssignableFrom(f.ElementType))
                {
                    var child = m_SchemaCache.GetOrAdd(f.ElementType, BuildSchema);
                    if (HasTransitiveLazyPayload(child, visited))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static RecordSchema BuildSchema(Type type)
        {
            bool isFeature = typeof(BlendShareFeatureData).IsAssignableFrom(type);
            if (!isFeature && type.GetCustomAttribute<BlendShareRecordAttribute>(inherit: false) == null)
            {
                throw new InvalidOperationException($"Type '{type.FullName}' must be marked with [{nameof(BlendShareRecordAttribute)}] or inherit {nameof(BlendShareFeatureData)}.");
            }

            var fields = new List<FieldEntry>();
            var byTag = new Dictionary<int, FieldEntry>();
            bool containsLazyPayload = false;

            var hierarchy = new Stack<Type>();
            for (var current = type; current != null && current != typeof(object); current = current.BaseType)
            {
                hierarchy.Push(current);
            }

            while (hierarchy.Count > 0)
            {
                var currentType = hierarchy.Pop();
                var rawFields = currentType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                for (int i = 0; i < rawFields.Length; i++)
                {
                    var fi = rawFields[i];
                    var attr = fi.GetCustomAttribute<BlendShareFieldAttribute>(inherit: false);
                    if (attr == null)
                    {
                        continue;
                    }

                    if (isFeature && currentType != typeof(BlendShareFeatureData) && attr.Tag < BlendShareFeatureData.MinDerivedFieldTag)
                    {
                        throw new InvalidOperationException(
                            $"Derived feature field '{currentType.Name}.{fi.Name}' uses reserved tag {attr.Tag} (< {BlendShareFeatureData.MinDerivedFieldTag}).");
                    }

                    if (byTag.ContainsKey(attr.Tag))
                    {
                        throw new InvalidOperationException(
                            $"Duplicate [BlendShareField({attr.Tag})] tag on '{type.FullName}.{fi.Name}'.");
                    }

                    ClassifyField(fi, out var kind, out var expectedWireType, out var elementType);
                    if (kind == FieldKind.LazyPayload)
                    {
                        containsLazyPayload = true;
                    }

                    var entry = new FieldEntry(attr.Tag, attr.Semantic, fi, kind, expectedWireType, elementType);
                    fields.Add(entry);
                    byTag.Add(attr.Tag, entry);
                }
            }

            fields.Sort((a, b) => a.Tag.CompareTo(b.Tag));
            return new RecordSchema(type, fields.ToArray(), byTag, containsLazyPayload);
        }

        private static void ClassifyField(
            FieldInfo fi,
            out FieldKind kind,
            out WireType expectedWireType,
            out Type elementType)
        {
            Type ft = fi.FieldType;
            elementType = null;

            if (ft == typeof(bool)) { kind = FieldKind.Bool; expectedWireType = WireType.Varint; return; }
            if (ft == typeof(byte)) { kind = FieldKind.Byte; expectedWireType = WireType.Varint; return; }
            if (ft == typeof(int)) { kind = FieldKind.Int32; expectedWireType = WireType.Varint; return; }
            if (ft == typeof(long)) { kind = FieldKind.Int64; expectedWireType = WireType.Varint; return; }
            if (ft.IsEnum) { kind = FieldKind.Enum; expectedWireType = WireType.Varint; return; }
            if (ft == typeof(float)) { kind = FieldKind.Float32; expectedWireType = WireType.Fixed32; return; }
            if (ft == typeof(double)) { kind = FieldKind.Float64; expectedWireType = WireType.Fixed64; return; }
            if (ft == typeof(string)) { kind = FieldKind.String; expectedWireType = WireType.LengthDelimited; return; }
            if (ft == typeof(byte[])) { kind = FieldKind.ByteArray; expectedWireType = WireType.LengthDelimited; return; }
            if (ft == typeof(Vector2d)) { kind = FieldKind.Vector2d; expectedWireType = WireType.LengthDelimited; return; }
            if (ft == typeof(Vector3d)) { kind = FieldKind.Vector3d; expectedWireType = WireType.LengthDelimited; return; }
            if (ft == typeof(Vector4d)) { kind = FieldKind.Vector4d; expectedWireType = WireType.LengthDelimited; return; }
            if (ft == typeof(FbxMatrix4x4)) { kind = FieldKind.Matrix4x4; expectedWireType = WireType.LengthDelimited; return; }
            if (ft == typeof(int[])) { kind = FieldKind.Int32Array; expectedWireType = WireType.LengthDelimited; return; }
            if (ft == typeof(double[])) { kind = FieldKind.DoubleArray; expectedWireType = WireType.LengthDelimited; return; }
            if (ft == typeof(Vector3d[])) { kind = FieldKind.Vector3dArray; expectedWireType = WireType.LengthDelimited; return; }
            if (ft == typeof(IndexedBuffer<Vector3d>)) { kind = FieldKind.IndexedVector3d; expectedWireType = WireType.LengthDelimited; return; }
            if (ft == typeof(IndexedBuffer<double>)) { kind = FieldKind.IndexedDouble; expectedWireType = WireType.LengthDelimited; return; }

            if (ft.IsGenericType && ft.GetGenericTypeDefinition() == typeof(LazyPayload<>))
            {
                kind = FieldKind.LazyPayload;
                expectedWireType = WireType.Varint;
                elementType = ft.GetGenericArguments()[0];
                return;
            }

            if (ft.IsGenericType && ft.GetGenericTypeDefinition() == typeof(List<>))
            {
                kind = FieldKind.List;
                expectedWireType = WireType.LengthDelimited;
                elementType = ft.GetGenericArguments()[0];
                return;
            }

            if (ft.GetCustomAttribute<BlendShareRecordAttribute>(inherit: false) != null)
            {
                kind = FieldKind.Record;
                expectedWireType = WireType.LengthDelimited;
                return;
            }

            throw new NotSupportedException($"Field '{fi.DeclaringType?.Name}.{fi.Name}' has unsupported BlendShare field type '{ft.FullName}'.");
        }

        private enum FieldKind : byte
        {
            Bool,
            Byte,
            Int32,
            Int64,
            Enum,
            Float32,
            Float64,
            String,
            ByteArray,
            Vector2d,
            Vector3d,
            Vector4d,
            Matrix4x4,
            Int32Array,
            DoubleArray,
            Vector3dArray,
            IndexedVector3d,
            IndexedDouble,
            Record,
            List,
            LazyPayload
        }

        private sealed class FieldEntry
        {
            public readonly int Tag;
            public readonly DataSemantic Semantic;
            public readonly FieldInfo FieldInfo;
            public readonly Type FieldType;
            public readonly FieldKind Kind;
            public readonly WireType ExpectedWireType;
            public readonly Type ElementType;

            public FieldEntry(int tag, DataSemantic semantic, FieldInfo fieldInfo, FieldKind kind, WireType expectedWireType, Type elementType)
            {
                Tag = tag;
                Semantic = semantic;
                FieldInfo = fieldInfo;
                FieldType = fieldInfo.FieldType;
                Kind = kind;
                ExpectedWireType = expectedWireType;
                ElementType = elementType;
            }
        }

        private sealed class RecordSchema
        {
            public readonly Type RecordType;
            public readonly FieldEntry[] OrderedFields;
            public readonly Dictionary<int, FieldEntry> ByTag;
            public readonly bool ContainsDirectLazyPayload;

            public RecordSchema(Type recordType, FieldEntry[] orderedFields, Dictionary<int, FieldEntry> byTag, bool containsDirectLazyPayload)
            {
                RecordType = recordType;
                OrderedFields = orderedFields;
                ByTag = byTag;
                ContainsDirectLazyPayload = containsDirectLazyPayload;
            }
        }
    }
}
