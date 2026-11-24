using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
        public const int FixedHeaderSize = 112;
        internal static readonly byte[] Magic = Encoding.ASCII.GetBytes("BLSHARE1");
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
            Validate(document, payloadSource ?? (rawCopySource as ILazyPayloadSource));
            var encoding = options ?? PayloadEncodeOptions.Default;
            var sink = new PayloadSink(m_Serializer, allowRawCopy ? rawCopySource : null, payloadSource);
            using var manifest = new MemoryStream();
            var writer = new WireWriter(manifest);
            writer.WriteString(document.PatchId);
            writer.WriteString(document.DisplayName);
            var entries = new List<ContainerIndexEntry>();

            void AddRecord(ContainerSectionKind kind, byte[] bytes, string mesh = "", BlendShareFeatureData feature = null)
            {
                var slice = new ByteSlice(FixedHeaderSize + manifest.Position, bytes.Length);
                entries.Add(new ContainerIndexEntry(entries.Count, kind, mesh, feature?.FeatureId ?? "",
                    feature?.TypeId ?? "", feature?.SchemaVersion ?? 1, feature?.Required ?? true, "", 0,
                    slice, bytes.Length, FormatIO.Hash(bytes)));
                manifest.Write(bytes, 0, bytes.Length);
            }

            AddRecord(ContainerSectionKind.Armatures, m_Serializer.SerializeRecord(
                new ArmatureTable { Armatures = document.Armatures }, sink, encoding));
            foreach (var mesh in document.Meshes)
            {
                var header = new MeshDocument
                {
                    MeshId = mesh.MeshId, Name = mesh.Name, NodePath = mesh.NodePath, Topology = mesh.Topology,
                    SpaceProvenance = mesh.SpaceProvenance, OriginBinding = mesh.OriginBinding
                };
                AddRecord(ContainerSectionKind.MeshHeader, m_Serializer.SerializeRecord(header, sink, encoding, mesh.MeshId), mesh.MeshId);
                foreach (var feature in mesh.Features)
                    AddRecord(ContainerSectionKind.Feature, m_Serializer.SerializeFeature(feature, sink, encoding, mesh.MeshId), mesh.MeshId, feature);
            }

            var manifestSlice = new ByteSlice(FixedHeaderSize, manifest.Length);
            var recordCount = entries.Count;
            entries.AddRange(sink.Entries);
            FormatIO.Check(entries.Count <= WireLimits.MaxPayloadCount, "Too many index entries.");
            // Offsets and entry ordinals are fixed-width, so relocation cannot change the index length.
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
            Array.Copy(revision, 0, headerBytes, 20, revision.Length);
            var toc = new ContainerToc(document.PatchId, document.DisplayName, revision, manifestSlice, indexSlice, payloadSlice, entries);

            stream.Position = 0;
            stream.Write(headerBytes, 0, headerBytes.Length);
            stream.Write(manifestBytes, 0, manifestBytes.Length);
            stream.Write(indexBytes, 0, indexBytes.Length);
            foreach (var bytes in sink.Blobs) stream.Write(bytes, 0, bytes.Length);
            if (stream.Length != toc.TotalFileLength) stream.SetLength(toc.TotalFileLength);
            return toc;
        }

        private void Validate(PatchDocument document, ILazyPayloadSource payloadSource)
        {
            FormatIO.Check(!string.IsNullOrWhiteSpace(document?.PatchId) && document.Meshes != null &&
                document.Armatures != null, "Invalid patch document.");
            var armatures = new HashSet<string>(StringComparer.Ordinal);
            foreach (var armature in document.Armatures)
            {
                FormatIO.Check(!string.IsNullOrWhiteSpace(armature?.ArmatureId) && armatures.Add(armature.ArmatureId),
                    "Missing or duplicate armature ID.");
            }
            var meshes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var mesh in document.Meshes)
            {
                FormatIO.Check(!string.IsNullOrWhiteSpace(mesh?.MeshId) && meshes.Add(mesh.MeshId) &&
                    mesh.Features != null, "Missing or duplicate mesh ID or missing features.");
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var feature in mesh.Features)
                {
                    FormatIO.Check(!string.IsNullOrWhiteSpace(feature?.FeatureId) && ids.Add(feature.FeatureId) &&
                        !string.IsNullOrWhiteSpace(feature.TypeId) && feature.SchemaVersion > 0, "Invalid or duplicate feature.");
                    if (feature is UnknownFeatureData unknown)
                    {
                        // Opaque bytes may contain absolute payload ordinals which cannot be remapped.
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
            w.WriteFixedInt32(5);
            w.WriteFixedInt32(0);
            w.WriteFixedInt32(FixedHeaderSize);
            w.WriteBytesRaw(new byte[32]);
            foreach (var slice in new[] { manifest, index, payload })
            {
                w.WriteFixedInt64(slice.Offset);
                w.WriteFixedInt64(slice.Length);
            }
            w.WriteFixedInt64(payload.End);
            w.WriteFixedInt32(0);
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

    [BlendShareRecord]
    internal sealed class ArmatureTable
    {
        [BlendShareField(1)] public List<ArmatureDocument> Armatures = new List<ArmatureDocument>();
    }
}
