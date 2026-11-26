using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Triturbo.BlendShare.Container.Serialization;
using Triturbo.BlendShare.Container.Wire;
using Triturbo.BlendShare.Model.Documents;
using Triturbo.BlendShare.Model.Features;
using Triturbo.BlendShare.Model.Serialization;
using Triturbo.BlendShare.Model.Validation;
using Triturbo.BlendShare.Primitives;

namespace Triturbo.BlendShare.Container.Format
{
    public sealed class BlendShareContainerWriter
    {
        public const int CurrentFormatVersion = 1;
        public const int FixedHeaderSize = 104;
        public const int RevisionOffset = 16;

        internal static readonly byte[] Magic = { (byte)'B', (byte)'L', (byte)'S', (byte)'H' };
        private readonly IBlendShareRecordSerializer m_Serializer;

        public BlendShareContainerWriter(IBlendShareRecordSerializer serializer = null)
        {
            m_Serializer = serializer ?? BlendShareSerializer.Default;
        }

        public ContainerToc Write(Stream stream, PatchDocument document, PayloadEncodeOptions? options = null,
            IRawPayloadCopySource rawCopySource = null, ILazyPayloadSource payloadSource = null, bool allowRawCopy = true)
        {
            if (stream == null || !stream.CanSeek || !stream.CanWrite)
                throw new ArgumentException("A seekable writable stream is required.", nameof(stream));
            var effectivePayloadSource = payloadSource ?? (rawCopySource as ILazyPayloadSource);
            Validate(document, effectivePayloadSource);
            bool effectiveAllowRawCopy = allowRawCopy && !options.HasValue;
            var encoding = options ?? PayloadEncodeOptions.Default;
            var sink = new PayloadSink(m_Serializer, effectiveAllowRawCopy ? rawCopySource : null, effectivePayloadSource);
            var provenance = document.SpaceProvenance;
            var patchEncoding = ResolvePatchEncodeOptions(encoding, provenance);
            using var manifest = new MemoryStream();
            var writer = new WireWriter(manifest);
            writer.WriteString(document.PatchId);
            writer.WriteDouble(provenance.MetersPerUnit);
            writer.WriteByte((byte)provenance.RightAxis);
            writer.WriteByte((byte)provenance.UpAxis);
            writer.WriteByte((byte)provenance.FrontAxis);
            var entries = new List<ContainerIndexEntry>();

            void AddRecord(ContainerSectionKind kind, byte[] bytes, string mesh = "", string featureId = "",
                string typeId = "", int schemaVersion = 1, bool required = true)
            {
                var slice = new ByteSlice(FixedHeaderSize + manifest.Position, bytes.Length);
                entries.Add(new ContainerIndexEntry(entries.Count, kind, mesh, featureId,
                    typeId, schemaVersion, required, "", 0, slice, bytes.Length, FormatIO.Hash(bytes)));
                manifest.Write(bytes, 0, bytes.Length);
            }

            if (document.Skeleton != null && document.Skeleton.HasContent)
            {
                AddRecord(
                    ContainerSectionKind.Skeleton,
                    m_Serializer.SerializeRecord(document.Skeleton, sink, patchEncoding, ownerMeshId: "", ownerFeatureId: SkeletonDocument.SectionFeatureId),
                    mesh: "",
                    featureId: SkeletonDocument.SectionFeatureId,
                    typeId: SkeletonDocument.SectionTypeId,
                    schemaVersion: SkeletonDocument.CurrentSchemaVersion,
                    required: true);
            }

            foreach (var mesh in document.Meshes)
            {
                var meshEncoding = ResolveMeshEncodeOptions(encoding, provenance, mesh);
                var header = new MeshDocument
                {
                    MeshId = mesh.MeshId,
                    Name = mesh.Name,
                    NodePath = mesh.NodePath,
                    Topology = mesh.Topology,
                    Mappings = mesh.Mappings ?? new List<VertexMappingEntry>(),
                    Sketch = mesh.Sketch,
                    EffectivePositionUnitMeters = mesh.EffectivePositionUnitMeters
                };
                AddRecord(ContainerSectionKind.MeshHeader, m_Serializer.SerializeRecord(header, sink, meshEncoding, mesh.MeshId), mesh.MeshId);
                foreach (var feature in mesh.Features)
                {
                    AddRecord(
                        ContainerSectionKind.Feature,
                        m_Serializer.SerializeFeature(feature, sink, meshEncoding, mesh.MeshId),
                        mesh.MeshId,
                        feature.FeatureId,
                        feature.TypeId,
                        feature.SchemaVersion,
                        feature.Required);
                }
            }

            var manifestSlice = new ByteSlice(FixedHeaderSize, manifest.Length);
            var recordCount = entries.Count;
            entries.AddRange(sink.Entries);
            FormatIO.Check(entries.Count <= WireLimits.MaxPayloadCount, "Too many index entries.");
            using var index = new MemoryStream();
            WriteIndex(index, entries);
            var indexSlice = new ByteSlice(manifestSlice.End, index.Length);
            var payloadSlice = new ByteSlice(indexSlice.End, sink.Length);
            for (int i = recordCount; i < entries.Count; i++)
                entries[i] = entries[i].Relocate(i, payloadSlice.Offset);
            index.Position = 0;
            WriteIndex(index, entries);
            FormatIO.Check(manifest.Length <= WireLimits.MaxUncompressedEnvelopeBytes &&
                index.Length <= WireLimits.MaxUncompressedEnvelopeBytes, "Metadata exceeds format limits.");
            var headerBytes = Header(manifestSlice, indexSlice, payloadSlice);
            var manifestBytes = manifest.ToArray();
            var indexBytes = index.ToArray();
            var revision = FormatIO.Hash(headerBytes, manifestBytes, indexBytes);
            Array.Copy(revision, 0, headerBytes, RevisionOffset, revision.Length);
            var clonedProvenance = PatchSpaceProvenance.FromMetadata(provenance.Axes, provenance.MetersPerUnit);
            var toc = new ContainerToc(document.PatchId, clonedProvenance, revision, manifestSlice, indexSlice, payloadSlice, entries);

            stream.Position = 0;
            stream.Write(headerBytes, 0, headerBytes.Length);
            stream.Write(manifestBytes, 0, manifestBytes.Length);
            stream.Write(indexBytes, 0, indexBytes.Length);
            foreach (var bytes in sink.Blobs) stream.Write(bytes, 0, bytes.Length);
            if (stream.Length != toc.TotalFileLength) stream.SetLength(toc.TotalFileLength);
            return toc;
        }

        private static PayloadEncodeOptions ResolvePatchEncodeOptions(PayloadEncodeOptions baseOptions, PatchSpaceProvenance provenance)
        {
            if (provenance == null)
            {
                return baseOptions;
            }

            double unitMeters = provenance.MetersPerUnit;
            FormatIO.Check(!double.IsNaN(unitMeters) && !double.IsInfinity(unitMeters) && unitMeters > 0d,
                "Patch space provenance requires a finite strictly positive MetersPerUnit.");
            return baseOptions.WithPositionUnitMeters(unitMeters);
        }

        private static PayloadEncodeOptions ResolveMeshEncodeOptions(
            PayloadEncodeOptions baseOptions,
            PatchSpaceProvenance provenance,
            MeshDocument mesh)
        {
            if (mesh != null && mesh.EffectivePositionUnitMeters > 0d)
            {
                FormatIO.Check(!double.IsNaN(mesh.EffectivePositionUnitMeters) && !double.IsInfinity(mesh.EffectivePositionUnitMeters),
                    "Mesh EffectivePositionUnitMeters must be finite.");
                return baseOptions.WithPositionUnitMeters(mesh.EffectivePositionUnitMeters);
            }

            return ResolvePatchEncodeOptions(baseOptions, provenance);
        }

        private void Validate(PatchDocument document, ILazyPayloadSource payloadSource)
        {
            FormatIO.Check(!string.IsNullOrWhiteSpace(document?.PatchId) && document.Meshes != null,
                "Invalid patch document.");
            FormatIO.Check(document.SpaceProvenance != null && document.SpaceProvenance.IsValid,
                "Patch document requires a valid shared SpaceProvenance.");

            if (document.Skeleton != null)
            {
                var skelResult = SkeletonDocumentValidator.Validate(document.Skeleton);
                if (!skelResult.IsValid)
                {
                    throw new InvalidDataException(string.Join(", ", skelResult.Issues));
                }
            }

            var meshes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var mesh in document.Meshes)
            {
                FormatIO.Check(!string.IsNullOrWhiteSpace(mesh?.MeshId) && meshes.Add(mesh.MeshId) &&
                    mesh.Features != null, "Missing or duplicate mesh ID or missing features.");
                FormatIO.Check(!double.IsNaN(mesh.EffectivePositionUnitMeters) &&
                    !double.IsInfinity(mesh.EffectivePositionUnitMeters) &&
                    mesh.EffectivePositionUnitMeters >= 0d,
                    $"Invalid EffectivePositionUnitMeters ({mesh.EffectivePositionUnitMeters}) on mesh '{mesh.MeshId}'.");

                if (mesh.Sketch != null)
                {
                    int expectedCpCount = mesh.Topology != null && mesh.Topology.IsValid ? mesh.Topology.ControlPointCount : -1;
                    FormatIO.Check(mesh.Sketch.TryValidate(expectedCpCount, out string sketchError), sketchError);
                }

                if (mesh.Mappings != null && mesh.Mappings.Count > 0)
                {
                    FormatIO.Check(
                        VertexMappingEntry.TryValidateEntries(mesh.Mappings, mesh.Topology, out string mappingError),
                        mappingError);
                }

                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var feature in mesh.Features)
                {
                    FormatIO.Check(!string.IsNullOrWhiteSpace(feature?.FeatureId) && ids.Add(feature.FeatureId) &&
                        !string.IsNullOrWhiteSpace(feature.TypeId) && feature.SchemaVersion > 0, "Invalid or duplicate feature.");
                    if (feature is UnknownFeatureData unknown)
                    {
                        FormatIO.Check(unknown.EstimatedPayloadBytes == 0, "Cannot rewrite an opaque feature with external payloads.");
                        continue;
                    }
                    var context = new FeatureValidationContext(mesh.MeshId, mesh.Topology?.ControlPointCount ?? -1, payloadSource: payloadSource, validatePayloads: true);
                    var result = m_Serializer.ValidateFeature(feature, context);
                    if (!result.IsValid) throw new InvalidDataException(string.Join(", ", result.Issues));
                }
            }
        }

        private static byte[] Header(ByteSlice manifest, ByteSlice index, ByteSlice payload)
        {
            using var buffer = new MemoryStream();
            var w = new WireWriter(buffer);
            w.WriteBytesRaw(Magic);
            w.WriteFixedInt32(CurrentFormatVersion);
            w.WriteFixedInt32(0);
            w.WriteFixedInt32(FixedHeaderSize);
            w.WriteBytesRaw(new byte[32]);
            foreach (var slice in new[] { manifest, index, payload })
            {
                w.WriteFixedInt64(slice.Offset);
                w.WriteFixedInt64(slice.Length);
            }
            w.WriteFixedInt64(payload.End);
            return buffer.ToArray();
        }

        private static void WriteIndex(Stream stream, List<ContainerIndexEntry> entries)
        {
            var w = new WireWriter(stream);
            w.WriteFixedInt32(entries.Count);
            foreach (var e in entries)
            {
                w.WriteFixedInt32(e.EntryIndex);
                w.WriteByte((byte)e.SectionKind);
                w.WriteString(e.OwnerMeshId);
                w.WriteString(e.FeatureId);
                w.WriteString(e.TypeId);
                w.WriteFixedInt32(e.SchemaVersion);
                w.WriteBool(e.Required);
                w.WriteString(e.CodecId);
                w.WriteFixedInt32(e.CodecVersion);
                w.WriteFixedInt64(e.Slice.Offset);
                w.WriteFixedInt64(e.Slice.Length);
                w.WriteFixedInt64(e.UncompressedBytes);
                w.WriteBytesRaw(e.Sha256);
            }
        }

        private sealed class PayloadSink : IPayloadWriteSink
        {
            internal readonly List<byte[]> Blobs = new List<byte[]>();
            internal readonly List<ContainerIndexEntry> Entries = new List<ContainerIndexEntry>();
            internal long Length;
            private readonly IBlendShareRecordSerializer m_Serializer;
            private readonly IRawPayloadCopySource m_Raw;
            private readonly ILazyPayloadSource m_Source;

            internal PayloadSink(IBlendShareRecordSerializer serializer, IRawPayloadCopySource raw, ILazyPayloadSource source)
            {
                m_Serializer = serializer;
                m_Raw = raw;
                m_Source = source;
            }

            public int WritePayload<T>(LazyPayload<T> payload, PayloadEncodeOptions options, string ownerMeshId,
                string ownerFeatureId, string codecId, int codecVersion, Action<Stream, PayloadEncodeOptions> encodeValue) where T : class
            {
                using var buffer = new MemoryStream();
                byte[] claimedHash = null;
                long claimedSize = -1;
                bool copied = false;
                if (payload.State == LazyPayloadState.Ref)
                {
                    var source = payload.Reference;
                    if (m_Raw != null && source.CodecId == codecId && source.CodecVersion == codecVersion)
                        copied = m_Raw.TryCopyRawPayload(source, buffer, out claimedHash, out claimedSize);
                    if (!copied)
                    {
                        if (m_Source == null) throw new InvalidOperationException("Ref payload requires a raw copy source or a decoded payload source.");
                        buffer.SetLength(0);
                        using var lease = payload.Acquire(m_Source);
                        m_Serializer.SerializePayloadRecord(lease.Value, buffer, options);
                    }
                }
                else encodeValue(buffer, options);

                var bytes = buffer.ToArray();
                FormatIO.Check(bytes.Length <= WireLimits.MaxUncompressedEnvelopeBytes, "Payload exceeds format limit.");
                var hash = FormatIO.Hash(bytes);
                var size = CompressionEnvelope.PeekUncompressedLength(bytes);
                if (copied) FormatIO.Check(claimedHash != null && hash.SequenceEqual(claimedHash) &&
                    size == claimedSize && size == payload.Reference.UncompressedBytes, "Incorrect raw payload digest or length.");
                int i = Entries.Count;
                var slice = new ByteSlice(Length, bytes.Length);
                Entries.Add(new ContainerIndexEntry(i, ContainerSectionKind.Payload, ownerMeshId, ownerFeatureId,
                    "", 1, true, codecId, codecVersion, slice, size, hash));
                Blobs.Add(bytes);
                Length = slice.End;
                return i;
            }
        }
    }
}
