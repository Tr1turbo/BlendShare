using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Triturbo.BlendShare.Container.Serialization;
using Triturbo.BlendShare.Container.Wire;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Model.Documents;
using Triturbo.BlendShare.Model.Features;
using Triturbo.BlendShare.Primitives;

namespace Triturbo.BlendShare.Container.Format
{
    public sealed class BlendShareContainerReader
    {
        private readonly IBlendShareRecordSerializer m_Serializer;
        public BlendShareContainerReader(IBlendShareRecordSerializer serializer = null)
        {
            m_Serializer = serializer ?? BlendShareSerializer.Default;
        }

        public ContainerReadResult<ContainerToc> ReadToc(Stream stream) => Run(() =>
        {
            var header = ReadHeader(stream, null);
            var manifest = FormatIO.Read(stream, header.Manifest);
            var index = FormatIO.Read(stream, header.Index);
            Array.Clear(header.Bytes, BlendShareContainerWriter.RevisionOffset, 32);
            FormatIO.Check(FormatIO.Hash(header.Bytes, manifest, index).SequenceEqual(header.Revision), "Incorrect metadata digest.");
            var metadata = new WireReader(manifest);
            ReadManifestPrefix(ref metadata, out string patch, out var spaceProvenance);
            var entries = ParseIndex(index, header, header.Manifest.Offset + metadata.Position);
            return new ContainerToc(patch, spaceProvenance, header.Revision, header.Manifest, header.Index, header.Payload, entries);
        });

        public ContainerReadResult<SkeletonDocument> ReadSkeleton(Stream stream, ContainerToc expectedToc) =>
            Target(stream, expectedToc, () => Skeleton(stream, expectedToc));

        public ContainerReadResult<MeshDocument> ReadMeshHeader(Stream stream, ContainerToc expectedToc, string meshId) =>
            Target(stream, expectedToc, () =>
            {
                if (!expectedToc.TryGetMesh(meshId, out var mesh)) throw Missing("mesh", meshId);
                return MeshHeader(stream, expectedToc, mesh);
            });

        public ContainerReadResult<BlendShareFeatureData> ReadFeature(Stream stream, ContainerToc expectedToc, string meshId, string featureId) =>
            Target(stream, expectedToc, () =>
            {
                if (!expectedToc.TryGetFeature(meshId, featureId, out var entry)) throw Missing("feature", featureId);
                return Feature(stream, expectedToc, entry);
            });

        public ContainerReadResult<byte[]> ReadVerifiedPayloadBytes(Stream stream, ContainerToc expectedToc, int payloadIndex) =>
            Target(stream, expectedToc, () =>
            {
                if (!expectedToc.TryGetPayload(payloadIndex, out var entry)) throw Missing("payload", payloadIndex.ToString());
                var bytes = Verified(stream, expectedToc, entry);
                FormatIO.Check(CompressionEnvelope.PeekUncompressedLength(bytes) == entry.UncompressedBytes, "Incorrect payload length.");
                return bytes;
            });

        public ContainerReadResult<PatchDocument> ReadPatchDocument(Stream stream, ContainerToc expectedToc) =>
            Target(stream, expectedToc, () =>
            {
                var doc = new PatchDocument
                {
                    PatchId = expectedToc.PatchId,
                    SpaceProvenance = PatchSpaceProvenance.FromMetadata(
                        expectedToc.SpaceProvenance.Axes,
                        expectedToc.SpaceProvenance.MetersPerUnit),
                    Skeleton = Skeleton(stream, expectedToc)
                };
                foreach (var item in expectedToc.Meshes)
                {
                    var mesh = MeshHeader(stream, expectedToc, item);
                    doc.Meshes.Add(mesh);
                    foreach (var feature in item.Features) mesh.Features.Add(Feature(stream, expectedToc, feature.Entry));
                }
                return doc;
            });

        private SkeletonDocument Skeleton(Stream stream, ContainerToc toc)
        {
            var value = m_Serializer.DeserializeRecord<SkeletonDocument>(Verified(stream, toc, toc.SkeletonEntry), null);
            FormatIO.Check(value != null, "Missing skeleton document.");
            value.Bones ??= new List<SkeletonBoneRecord>();
            var seenPaths = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < value.Bones.Count; i++)
            {
                string normalized = SkeletonDocument.NormalizeBonePath(value.Bones[i]?.Path);
                FormatIO.Check(!string.IsNullOrEmpty(normalized) && seenPaths.Add(normalized),
                    $"Invalid or duplicate skeleton bone path '{value.Bones[i]?.Path}'.");
            }
            return value;
        }

        private MeshDocument MeshHeader(Stream stream, ContainerToc toc, ContainerMeshToc mesh)
        {
            var value = m_Serializer.DeserializeRecord<MeshDocument>(Verified(stream, toc, mesh.HeaderEntry),
                new OwnerResolver(toc, mesh.HeaderEntry));
            FormatIO.Check(value.MeshId == mesh.MeshId && value.Features?.Count == 0, "Invalid mesh header.");
            value.Mappings ??= new List<VertexMappingEntry>();
            return value;
        }

        private BlendShareFeatureData Feature(Stream stream, ContainerToc toc, ContainerIndexEntry e)
        {
            var value = m_Serializer.DeserializeFeature(e.FeatureId, e.TypeId, e.SchemaVersion, e.Required,
                Verified(stream, toc, e), new OwnerResolver(toc, e), toc.FeaturePayloadBytes(e));
            FormatIO.Check(value.FeatureId == e.FeatureId, "Incorrect feature ID.");
            return value;
        }

        private sealed class OwnerResolver : IPayloadRefResolver
        {
            private readonly ContainerToc m_Toc;
            private readonly ContainerIndexEntry m_Owner;
            internal OwnerResolver(ContainerToc toc, ContainerIndexEntry owner) { m_Toc = toc; m_Owner = owner; }
            public PayloadRef ResolvePayloadRef(int payloadIndex)
            {
                FormatIO.Check(m_Toc.TryGetPayload(payloadIndex, out var payload) && payload.OwnerMeshId == m_Owner.OwnerMeshId &&
                    payload.FeatureId == m_Owner.FeatureId, "Invalid payload owner.");
                return m_Toc.ResolvePayloadRef(payloadIndex);
            }
        }

        private static byte[] Verified(Stream stream, ContainerToc toc, ContainerIndexEntry entry)
        {
            var section = entry.SectionKind == ContainerSectionKind.Payload ? toc.PayloadSectionSlice : toc.ManifestSlice;
            FormatIO.Check(entry.Slice.IsWithin(section), "Entry lies outside owning section.");
            var bytes = FormatIO.Read(stream, entry.Slice);
            FormatIO.Check(FormatIO.Hash(bytes).SequenceEqual(entry.Sha256), "Incorrect record digest.");
            return bytes;
        }

        private sealed class Header
        {
            internal byte[] Bytes, Revision;
            internal ByteSlice Manifest, Index, Payload;
        }

        private static Header ReadHeader(Stream stream, ContainerToc expected)
        {
            const int size = BlendShareContainerWriter.FixedHeaderSize;
            if (expected != null && expected.TotalFileLength != stream.Length)
                throw new ReadFailure(ContainerReadStatus.SourceRevisionMismatch, "Source length changed.");
            if (stream.Length < BlendShareContainerWriter.Magic.Length + 4)
                throw new ReadFailure(ContainerReadStatus.InvalidFormat, "Missing container header.");
            var bytes = FormatIO.Read(stream, new ByteSlice(0, Math.Min(stream.Length, size)));
            var r = new WireReader(bytes);
            if (!r.ReadSlice(BlendShareContainerWriter.Magic.Length).SequenceEqual(BlendShareContainerWriter.Magic))
                throw new ReadFailure(ContainerReadStatus.InvalidFormat, "Invalid container magic.");
            if (r.ReadFixedInt32() != BlendShareContainerWriter.CurrentFormatVersion)
                throw new ReadFailure(ContainerReadStatus.UnsupportedVersion, "Unsupported container version.");
            FormatIO.Check(r.ReadFixedInt32() == 0 && r.ReadFixedInt32() == size, "Invalid flags or header size.");
            var h = new Header { Bytes = bytes, Revision = r.ReadSlice(32).ToArray() };
            if (expected != null && FormatIO.Hex(h.Revision) != expected.SourceRevision)
                throw new ReadFailure(ContainerReadStatus.SourceRevisionMismatch, "Source revision changed.");

            h.Manifest = new ByteSlice(r.ReadFixedInt64(), r.ReadFixedInt64());
            h.Index = new ByteSlice(r.ReadFixedInt64(), r.ReadFixedInt64());
            h.Payload = new ByteSlice(r.ReadFixedInt64(), r.ReadFixedInt64());
            FormatIO.Check(h.Manifest.Offset == size && h.Manifest.End == h.Index.Offset && h.Index.End == h.Payload.Offset &&
                h.Payload.End == stream.Length && r.ReadFixedInt64() == stream.Length && r.IsAtEnd, "Invalid section boundaries.");
            if (expected != null)
            {
                FormatIO.Check(h.Manifest.Equals(expected.ManifestSlice) && h.Index.Equals(expected.IndexTableSlice) &&
                    h.Payload.Equals(expected.PayloadSectionSlice), "Section layout changed.");
                FormatIO.Check(expected.Entries != null && expected.Entries.Count > 0, "Cached TOC has no entries.");
                long prefixEnd = expected.Entries[0].Slice.Offset;
                FormatIO.Check(prefixEnd > h.Manifest.Offset && prefixEnd <= h.Manifest.End, "Invalid cached manifest prefix bounds.");
                var prefixBytes = FormatIO.Read(stream, new ByteSlice(h.Manifest.Offset, prefixEnd - h.Manifest.Offset));
                var prefixReader = new WireReader(prefixBytes);
                ReadManifestPrefix(ref prefixReader, out string filePatchId, out var fileSpace);
                FormatIO.Check(prefixReader.IsAtEnd, "Manifest prefix length does not match cached entry offset.");
                if (!string.Equals(filePatchId, expected.PatchId, StringComparison.Ordinal) ||
                    expected.SpaceProvenance == null ||
                    !fileSpace.MatchesSpace(expected.SpaceProvenance))
                {
                    throw new ReadFailure(ContainerReadStatus.SourceRevisionMismatch,
                        "Cached TOC patch metadata or space provenance does not match the container file.");
                }
            }
            return h;
        }

        private static void ReadManifestPrefix(ref WireReader reader, out string patchId, out PatchSpaceProvenance spaceProvenance)
        {
            patchId = reader.ReadString();
            FormatIO.Check(!string.IsNullOrWhiteSpace(patchId), "Missing patch ID.");
            double unitMeters = reader.ReadDouble();
            var rightAxis = (FbxCoordinateAxis)reader.ReadByte();
            var upAxis = (FbxCoordinateAxis)reader.ReadByte();
            var frontAxis = (FbxCoordinateAxis)reader.ReadByte();
            spaceProvenance = new PatchSpaceProvenance
            {
                MetersPerUnit = unitMeters,
                RightAxis = rightAxis,
                UpAxis = upAxis,
                FrontAxis = frontAxis
            };
            FormatIO.Check(spaceProvenance.IsValid, "Invalid patch space provenance.");
        }

        private static List<ContainerIndexEntry> ParseIndex(byte[] bytes, Header header, long manifestCursor)
        {
            var r = new WireReader(bytes);
            int count = r.ReadFixedInt32();
            FormatIO.Check(count > 0 && count <= WireLimits.MaxPayloadCount && count <= r.Remaining / 74, "Invalid index count.");
            var entries = new List<ContainerIndexEntry>(count);
            long payloadCursor = header.Payload.Offset;
            bool payloadStarted = false;
            for (int i = 0; i < count; i++)
            {
                FormatIO.Check(r.ReadFixedInt32() == i, "Invalid entry ordinal.");
                var kind = (ContainerSectionKind)r.ReadByte();
                string mesh = r.ReadString(), feature = r.ReadString(), type = r.ReadString();
                int schema = r.ReadFixedInt32();
                bool required = r.ReadBool();
                string codec = r.ReadString();
                int version = r.ReadFixedInt32();
                var slice = new ByteSlice(r.ReadFixedInt64(), r.ReadFixedInt64());
                long size = r.ReadFixedInt64();
                var digest = r.ReadSlice(32).ToArray();
                FormatIO.Check(mesh != null && feature != null && type != null && codec != null && schema > 0 &&
                    size >= 0 && size <= WireLimits.MaxUncompressedEnvelopeBytes &&
                    slice.Length <= WireLimits.MaxUncompressedEnvelopeBytes, "Invalid index metadata.");
                if (kind == ContainerSectionKind.Payload)
                {
                    payloadStarted = true;
                    FormatIO.Check(!string.IsNullOrWhiteSpace(codec) && version > 0 && slice.IsWithin(header.Payload) &&
                        slice.Offset == payloadCursor, "Invalid payload entry.");
                    payloadCursor = slice.End;
                }
                else
                {
                    FormatIO.Check(!payloadStarted && slice.IsWithin(header.Manifest) && slice.Offset == manifestCursor &&
                        size == slice.Length && codec == "" && version == 0, "Invalid manifest entry.");
                    bool valid = kind == ContainerSectionKind.Skeleton && mesh == "" && feature == "" ||
                        kind == ContainerSectionKind.MeshHeader && !string.IsNullOrWhiteSpace(mesh) && feature == "" ||
                        kind == ContainerSectionKind.Feature && !string.IsNullOrWhiteSpace(feature) && !string.IsNullOrWhiteSpace(type);
                    FormatIO.Check(valid, "Invalid record kind or identity.");
                    manifestCursor = slice.End;
                }
                entries.Add(new ContainerIndexEntry(i, kind, mesh, feature, type, schema, required, codec, version, slice, size, digest));
            }
            FormatIO.Check(r.IsAtEnd && manifestCursor == header.Manifest.End && payloadCursor == header.Payload.End,
                "Incomplete index or trailing data.");
            return entries;
        }

        private sealed class ReadFailure : Exception
        {
            internal readonly ContainerReadStatus Status;
            internal ReadFailure(ContainerReadStatus status, string message) : base(message) { Status = status; }
        }

        private static ReadFailure Missing(string kind, string id) =>
            new ReadFailure(ContainerReadStatus.NotFound, $"Missing {kind} '{id}'.");

        private static ContainerReadResult<T> Target<T>(Stream stream, ContainerToc expected, Func<T> action) => Run(() =>
        {
            if (expected == null) throw new ArgumentNullException(nameof(expected));
            ReadHeader(stream, expected);
            return action();
        });

        private static ContainerReadResult<T> Run<T>(Func<T> action)
        {
            try { return new ContainerReadResult<T>(ContainerReadStatus.Success, action()); }
            catch (ReadFailure e) { return new ContainerReadResult<T>(e.Status, default, e.Message); }
            catch (Exception e) when (e is InvalidDataException || e is IOException || e is ArgumentException ||
                e is OverflowException || e is InvalidOperationException || e is TargetInvocationException ||
                e is FormatException || e is NotSupportedException)
            {
                return new ContainerReadResult<T>(ContainerReadStatus.CorruptData, default, e.GetBaseException().Message);
            }
        }
    }
}
