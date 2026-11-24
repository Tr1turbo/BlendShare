using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Triturbo.BlendShare.Container.Serialization;
using Triturbo.BlendShare.Container.Wire;
using Triturbo.BlendShare.Primitives;

namespace Triturbo.BlendShare.Container.Format
{
    public enum ContainerSectionKind { Manifest = 1, IndexTable, Payload, Feature, MeshHeader, Armatures }
    public enum ContainerReadStatus { Success, InvalidFormat, UnsupportedVersion, SourceRevisionMismatch, CorruptData, NotFound, BudgetExceeded }

    public sealed class ContainerReadResult<T>
    {
        public ContainerReadStatus Status { get; }
        public bool Success => Status == ContainerReadStatus.Success;
        public T Value { get; }
        public string Message { get; }

        internal ContainerReadResult(ContainerReadStatus status, T value, string message = "")
        {
            Status = status;
            Value = value;
            Message = message;
        }

        public static ContainerReadResult<T> Fail(ContainerReadStatus status, string message)
        {
            if (status == ContainerReadStatus.Success)
            {
                throw new ArgumentException("Fail status cannot be Success.", nameof(status));
            }

            return new ContainerReadResult<T>(status, default, message ?? string.Empty);
        }
    }

    public sealed class ContainerIndexEntry
    {
        public int EntryIndex { get; }
        public ContainerSectionKind SectionKind { get; }
        public string OwnerMeshId { get; }
        public string FeatureId { get; }
        public string TypeId { get; }
        public int SchemaVersion { get; }
        public bool Required { get; }
        public string CodecId { get; }
        public int CodecVersion { get; }
        public ByteSlice Slice { get; }
        public long UncompressedBytes { get; }
        private readonly byte[] m_Hash;
        public byte[] Sha256 => (byte[])m_Hash.Clone();

        internal ContainerIndexEntry(int index, ContainerSectionKind kind, string mesh, string feature,
            string type, int schema, bool required, string codec, int version, ByteSlice slice, long size, byte[] hash)
        {
            EntryIndex = index;
            SectionKind = kind;
            OwnerMeshId = mesh;
            FeatureId = feature;
            TypeId = type;
            SchemaVersion = schema;
            Required = required;
            CodecId = codec;
            CodecVersion = version;
            Slice = slice;
            UncompressedBytes = size;
            m_Hash = (byte[])hash.Clone();
        }

        public static ContainerIndexEntry Create(
            int index,
            ContainerSectionKind kind,
            string ownerMeshId,
            string featureId,
            string typeId,
            int schemaVersion,
            bool required,
            string codecId,
            int codecVersion,
            ByteSlice slice,
            long uncompressedBytes,
            byte[] sha256)
        {
            if (sha256 == null || sha256.Length != 32)
            {
                throw new ArgumentException("SHA-256 digest must be exactly 32 bytes.", nameof(sha256));
            }

            return new ContainerIndexEntry(
                index,
                kind,
                ownerMeshId ?? string.Empty,
                featureId ?? string.Empty,
                typeId ?? string.Empty,
                schemaVersion,
                required,
                codecId ?? string.Empty,
                codecVersion,
                slice,
                uncompressedBytes,
                sha256);
        }

        internal ContainerIndexEntry Relocate(int index, long baseOffset) =>
            new ContainerIndexEntry(index, SectionKind, OwnerMeshId, FeatureId, TypeId, SchemaVersion, Required,
                CodecId, CodecVersion, new ByteSlice(checked(baseOffset + Slice.Offset), Slice.Length), UncompressedBytes, m_Hash);
    }

    public sealed class ContainerFeatureToc
    {
        public ContainerIndexEntry Entry { get; }
        public string FeatureId => Entry.FeatureId;
        internal ContainerFeatureToc(ContainerIndexEntry entry) { Entry = entry; }
    }

    public sealed class ContainerMeshToc
    {
        public string MeshId => HeaderEntry.OwnerMeshId;
        public ContainerIndexEntry HeaderEntry { get; }
        public IReadOnlyList<ContainerFeatureToc> Features { get; }
        internal ContainerMeshToc(ContainerIndexEntry header, IEnumerable<ContainerFeatureToc> features)
        {
            HeaderEntry = header;
            Features = Array.AsReadOnly(features.ToArray());
        }
    }

    public sealed class ContainerToc : IPayloadRefResolver
    {
        public int FormatVersion => 5;
        public string PatchId { get; }
        public string DisplayName { get; }
        public string SourceRevision { get; }
        public long TotalFileLength => PayloadSectionSlice.End;
        public ByteSlice ManifestSlice { get; }
        public ByteSlice IndexTableSlice { get; }
        public ByteSlice PayloadSectionSlice { get; }
        public ContainerIndexEntry ArmaturesEntry { get; }
        public IReadOnlyList<ContainerMeshToc> Meshes { get; }
        public IReadOnlyList<ContainerIndexEntry> Entries { get; }

        private readonly ContainerIndexEntry[] m_Payloads;
        private readonly Dictionary<(string, string), long> m_FeaturePayloadBytes =
            new Dictionary<(string, string), long>();
        private readonly Dictionary<string, ContainerMeshToc> m_Meshes;
        private readonly Dictionary<(string, string), ContainerIndexEntry> m_Features;

        internal ContainerToc(string patch, string display, byte[] revision, ByteSlice manifest,
            ByteSlice index, ByteSlice payload, IEnumerable<ContainerIndexEntry> entries)
        {
            PatchId = patch;
            DisplayName = display;
            SourceRevision = FormatIO.Hex(revision);
            ManifestSlice = manifest;
            IndexTableSlice = index;
            PayloadSectionSlice = payload;
            Entries = Array.AsReadOnly(entries.ToArray());
            var armatures = Entries.Where(e => e.SectionKind == ContainerSectionKind.Armatures).ToArray();
            FormatIO.Check(armatures.Length == 1, "Container must contain exactly one Armatures entry.");
            ArmaturesEntry = armatures[0];
            m_Payloads = Entries.Where(e => e.SectionKind == ContainerSectionKind.Payload).ToArray();
            var features = Entries.Where(e => e.SectionKind == ContainerSectionKind.Feature).ToList();
            m_Features = new Dictionary<(string, string), ContainerIndexEntry>();
            foreach (var f in features)
            {
                var key = (f.OwnerMeshId, f.FeatureId);
                FormatIO.Check(!m_Features.ContainsKey(key), $"Duplicate feature entry '{f.OwnerMeshId}/{f.FeatureId}'.");
                m_Features.Add(key, f);
            }
            var grouped = features.ToLookup(e => e.OwnerMeshId);
            var meshList = new List<ContainerMeshToc>();
            m_Meshes = new Dictionary<string, ContainerMeshToc>(StringComparer.Ordinal);
            foreach (var h in Entries.Where(e => e.SectionKind == ContainerSectionKind.MeshHeader))
            {
                FormatIO.Check(!m_Meshes.ContainsKey(h.OwnerMeshId), $"Duplicate mesh header entry '{h.OwnerMeshId}'.");
                var meshToc = new ContainerMeshToc(h, grouped[h.OwnerMeshId].Select(f => new ContainerFeatureToc(f)));
                meshList.Add(meshToc);
                m_Meshes.Add(meshToc.MeshId, meshToc);
            }
            Meshes = Array.AsReadOnly(meshList.ToArray());
            foreach (var e in features.Concat(m_Payloads))
            {
                FormatIO.Check(m_Meshes.ContainsKey(e.OwnerMeshId), "Missing owner mesh.");
                if (e.SectionKind == ContainerSectionKind.Payload && e.FeatureId.Length != 0)
                {
                    var key = (e.OwnerMeshId, e.FeatureId);
                    FormatIO.Check(m_Features.ContainsKey(key), "Missing owner feature.");
                    m_FeaturePayloadBytes.TryGetValue(key, out var total);
                    m_FeaturePayloadBytes[key] = checked(total + e.UncompressedBytes);
                }
            }
        }

        public static ContainerToc FromCachedEntries(
            string patchId,
            string displayName,
            byte[] sourceRevisionBytes,
            ByteSlice manifestSlice,
            ByteSlice indexTableSlice,
            ByteSlice payloadSectionSlice,
            IEnumerable<ContainerIndexEntry> entries)
        {
            if (sourceRevisionBytes == null || sourceRevisionBytes.Length != 32)
            {
                throw new ArgumentException("SourceRevision must be 32 bytes.", nameof(sourceRevisionBytes));
            }

            return new ContainerToc(
                patchId ?? string.Empty,
                displayName ?? string.Empty,
                sourceRevisionBytes,
                manifestSlice,
                indexTableSlice,
                payloadSectionSlice,
                entries ?? Array.Empty<ContainerIndexEntry>());
        }

        internal long FeaturePayloadBytes(ContainerIndexEntry entry) =>
            m_FeaturePayloadBytes.TryGetValue((entry.OwnerMeshId, entry.FeatureId), out var size) ? size : 0L;

        public bool TryGetMesh(string meshId, out ContainerMeshToc mesh) => m_Meshes.TryGetValue(meshId ?? "", out mesh);
        public bool TryGetFeature(string meshId, string featureId, out ContainerIndexEntry entry) =>
            m_Features.TryGetValue((meshId ?? "", featureId ?? ""), out entry);
        public bool TryGetPayload(int payloadIndex, out ContainerIndexEntry entry)
        {
            entry = (uint)payloadIndex < (uint)m_Payloads.Length ? m_Payloads[payloadIndex] : null;
            return entry != null;
        }
        public PayloadRef ResolvePayloadRef(int payloadIndex)
        {
            if (!TryGetPayload(payloadIndex, out var entry)) throw new InvalidDataException("Invalid payload index.");
            return new PayloadRef(SourceRevision, payloadIndex, entry.CodecId, entry.CodecVersion, entry.UncompressedBytes);
        }
    }

    public interface IRawPayloadCopySource
    {
        bool TryCopyRawPayload(PayloadRef sourceRef, Stream destination, out byte[] sha256, out long uncompressedBytes);
    }

    public interface IContainerStreamProvider
    {
        Stream OpenRead(string sourceRevision, out ContainerToc toc);
    }

    internal static class FormatIO
    {
        internal static void Check(bool ok, string message)
        {
            if (!ok) throw new InvalidDataException(message);
        }

        internal static string Hex(byte[] digest) => BitConverter.ToString(digest).Replace("-", "").ToLowerInvariant();

        internal static byte[] Hash(params byte[][] parts)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var part in parts) hash.AppendData(part);
            return hash.GetHashAndReset();
        }

        internal static byte[] Read(Stream stream, ByteSlice slice)
        {
            Check(slice.IsWithin(stream.Length) && slice.Length <= WireLimits.MaxUncompressedEnvelopeBytes, "Invalid read range.");
            stream.Position = slice.Offset;
            var bytes = new byte[(int)slice.Length];
            int offset = 0;
            while (offset < bytes.Length)
            {
                int n = stream.Read(bytes, offset, bytes.Length - offset);
                if (n == 0) throw new EndOfStreamException();
                offset += n;
            }
            return bytes;
        }
    }
}
