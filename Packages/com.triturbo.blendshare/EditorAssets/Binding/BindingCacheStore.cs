using System;
using System.Collections.Generic;
using System.IO;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Model.Documents;
using System.Security.Cryptography;
using System.Text;
using Triturbo.BlendShare.Mapping;
using Triturbo.BlendShare.Primitives;

namespace Triturbo.BlendShare.EditorAssets.Binding
{
    /// <summary>
    /// Layer 2 index entry mapping a conservative resolution key to a content-addressed Layer 1 CSR blob ID
    /// plus matched FBX-to-Unity importer parameters (excluding per-use SpaceContract).
    /// </summary>
    public sealed class BindingResolutionIndexEntry
    {
        public string ResolutionKeyHash { get; }
        public string CsrBlobId { get; }
        public int OriginControlPointCount { get; }
        public int UnityVertexCount { get; }
        public float MatchedScale { get; }
        public bool MatchedBakeAxisConversion { get; }

        public BindingResolutionIndexEntry(
            string resolutionKeyHash,
            string csrBlobId,
            int originControlPointCount,
            int unityVertexCount,
            float matchedScale,
            bool matchedBakeAxisConversion)
        {
            ResolutionKeyHash = resolutionKeyHash ?? string.Empty;
            CsrBlobId = csrBlobId ?? string.Empty;
            OriginControlPointCount = originControlPointCount;
            UnityVertexCount = unityVertexCount;
            MatchedScale = matchedScale;
            MatchedBakeAxisConversion = matchedBakeAxisConversion;
        }
    }

    /// <summary>
    /// On-disk + in-memory 2-layer geometry binding cache under <c>Library/BlendShare/Bindings/v2/</c>.
    /// Layer 1: Content-addressed CSR blobs (<c>blobs/&lt;csrBlobId&gt;.bin</c>) with strict SHA-256 and CSR bounds checks.
    /// Layer 2: Conservative resolution index (<c>index/&lt;resolutionKeyHash&gt;.bin</c>).
    /// Layer 3 (<c>OriginSpaceContract</c>) is NEVER cached in the CSR blob and is computed per resolution use.
    /// Feature selection/settings changes NEVER invalidate this cache.
    /// </summary>
    public sealed class BindingCacheStore
    {
        public const string DefaultRelativeRoot = "Library/BlendShare/Bindings/v2";

        private const int k_BindingSpaceAlgoRevision = 2;

        internal static void WriteSpaceKey(BinaryWriter writer, MeshSpaceProvenance stored)
        {
            var axes = stored?.OriginAxes ?? CanonicalMeshSpace.CanonicalAxes;
            writer.Write(k_BindingSpaceAlgoRevision);
            writer.Write((int)axes.Right);
            writer.Write((int)axes.Up);
            writer.Write((int)axes.Front);
            writer.Write(stored?.OriginRawMetersPerUnit ?? 1d);
        }

        private static readonly byte[] s_BlobMagic = Encoding.ASCII.GetBytes("BSCSR001");
        private static readonly byte[] s_IndexMagic = Encoding.ASCII.GetBytes("BSIDX001");
        private const int k_FormatVersion = 2;
        private const int k_MaxCachedMemoryEntries = 256;

        private readonly object m_Lock = new object();
        private readonly string m_RootDirectory;
        private readonly string m_BlobsDirectory;
        private readonly string m_IndexDirectory;
        private readonly Dictionary<string, CsrVertexMap> m_MemoryBlobs =
            new Dictionary<string, CsrVertexMap>(StringComparer.Ordinal);
        private readonly Dictionary<string, BindingResolutionIndexEntry> m_MemoryIndex =
            new Dictionary<string, BindingResolutionIndexEntry>(StringComparer.Ordinal);

        public string RootDirectory => m_RootDirectory;

        public BindingCacheStore(string rootDirectory = null)
        {
            m_RootDirectory = string.IsNullOrWhiteSpace(rootDirectory)
                ? Path.GetFullPath(DefaultRelativeRoot)
                : Path.GetFullPath(rootDirectory);
            m_BlobsDirectory = Path.Combine(m_RootDirectory, "blobs");
            m_IndexDirectory = Path.Combine(m_RootDirectory, "index");
        }

        public void ClearMemoryCache()
        {
            lock (m_Lock)
            {
                m_MemoryBlobs.Clear();
                m_MemoryIndex.Clear();
            }
        }

        public bool TryLoadResolution(
            string resolutionKeyHash,
            out BindingResolutionIndexEntry indexEntry,
            out CsrVertexMap csrMap)
        {
            indexEntry = null;
            csrMap = null;
            if (!IsValidHexKey(resolutionKeyHash))
            {
                return false;
            }

            lock (m_Lock)
            {
                if (m_MemoryIndex.TryGetValue(resolutionKeyHash, out var memIdx) &&
                    m_MemoryBlobs.TryGetValue(memIdx.CsrBlobId, out var memBlob))
                {
                    indexEntry = memIdx;
                    csrMap = memBlob;
                    return true;
                }
            }

            if (!TryReadIndexFromDisk(resolutionKeyHash, out var diskIdx))
            {
                return false;
            }

            if (!TryLoadCsrBlob(diskIdx.CsrBlobId, out var diskBlob))
            {
                return false;
            }

            if (diskBlob.SourceCount != diskIdx.UnityVertexCount ||
                diskBlob.TargetCount != diskIdx.OriginControlPointCount)
            {
                return false;
            }

            lock (m_Lock)
            {
                TrimMemoryIfNeededLocked();
                m_MemoryIndex[resolutionKeyHash] = diskIdx;
                m_MemoryBlobs[diskIdx.CsrBlobId] = diskBlob;
            }

            indexEntry = diskIdx;
            csrMap = diskBlob;
            return true;
        }

        public string StoreResolution(
            string resolutionKeyHash,
            CsrVertexMap csrMap,
            float matchedScale,
            bool matchedBakeAxisConversion,
            bool writeToDisk = true)
        {
            if (!IsValidHexKey(resolutionKeyHash))
            {
                throw new ArgumentException("Resolution key hash must be a valid hex string.", nameof(resolutionKeyHash));
            }

            if (csrMap == null)
            {
                throw new ArgumentNullException(nameof(csrMap));
            }

            string blobId = CsrVertexMapFactory.ComputeCsrBlobId(csrMap);
            var indexEntry = new BindingResolutionIndexEntry(
                resolutionKeyHash,
                blobId,
                csrMap.TargetCount,
                csrMap.SourceCount,
                matchedScale,
                matchedBakeAxisConversion);

            if (writeToDisk)
            {
                TryWriteCsrBlobToDisk(blobId, csrMap);
                TryWriteIndexToDisk(indexEntry);
            }

            lock (m_Lock)
            {
                TrimMemoryIfNeededLocked();
                m_MemoryBlobs[blobId] = csrMap;
                m_MemoryIndex[resolutionKeyHash] = indexEntry;
            }

            return blobId;
        }

        public bool TryLoadCsrBlob(string csrBlobId, out CsrVertexMap csrMap)
        {
            csrMap = null;
            if (!IsValidHexKey(csrBlobId))
            {
                return false;
            }

            lock (m_Lock)
            {
                if (m_MemoryBlobs.TryGetValue(csrBlobId, out var memBlob))
                {
                    csrMap = memBlob;
                    return true;
                }
            }

            if (!TryReadCsrBlobFromDisk(csrBlobId, out var diskBlob))
            {
                return false;
            }

            lock (m_Lock)
            {
                TrimMemoryIfNeededLocked();
                m_MemoryBlobs[csrBlobId] = diskBlob;
            }

            csrMap = diskBlob;
            return true;
        }

        private void TrimMemoryIfNeededLocked()
        {
            if (m_MemoryIndex.Count >= k_MaxCachedMemoryEntries)
            {
                m_MemoryIndex.Clear();
            }

            if (m_MemoryBlobs.Count >= k_MaxCachedMemoryEntries)
            {
                m_MemoryBlobs.Clear();
            }
        }

        private string GetBlobPath(string blobId) => Path.Combine(m_BlobsDirectory, blobId + ".bin");
        private string GetIndexPath(string keyHash) => Path.Combine(m_IndexDirectory, keyHash + ".bin");

        private void TryWriteCsrBlobToDisk(string blobId, CsrVertexMap csr)
        {
            try
            {
                string targetPath = GetBlobPath(blobId);
                if (File.Exists(targetPath))
                {
                    // Content-addressed: identical blob ID already on disk.
                    return;
                }

                Directory.CreateDirectory(m_BlobsDirectory);
                string tempPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    using (var bw = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true))
                    {
                        bw.Write(s_BlobMagic);
                        bw.Write(k_FormatVersion);
                        bw.Write((byte)csr.SourceDomain);
                        bw.Write((byte)csr.TargetDomain);
                        bw.Write(csr.SourceCount);
                        bw.Write(csr.TargetCount);

                        var offsets = csr.RowOffsets;
                        bw.Write(offsets.Length);
                        for (int i = 0; i < offsets.Length; i++)
                        {
                            bw.Write(offsets[i]);
                        }

                        var targets = csr.TargetIndices;
                        bw.Write(targets.Length);
                        for (int i = 0; i < targets.Length; i++)
                        {
                            bw.Write(targets[i]);
                        }

                        bw.Flush();
                        fs.Flush(flushToDisk: false);
                    }

                    AtomicPublish(tempPath, targetPath);
                }
                catch
                {
                    TryDeleteQuietly(tempPath);
                }
            }
            catch
            {
                // Disk cache writes are best-effort; memory cache still holds the entry.
            }
        }

        private bool TryReadCsrBlobFromDisk(string expectedBlobId, out CsrVertexMap csr)
        {
            csr = null;
            string path = GetBlobPath(expectedBlobId);
            if (!File.Exists(path))
            {
                return false;
            }

            bool corrupt = false;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: false))
                {
                    if (fs.Length < s_BlobMagic.Length + 4 + 2 + 12)
                    {
                        corrupt = true;
                        return false;
                    }

                    var magic = br.ReadBytes(s_BlobMagic.Length);
                    if (!BytesEqual(magic, s_BlobMagic))
                    {
                        corrupt = true;
                        return false;
                    }

                    int version = br.ReadInt32();
                    if (version != k_FormatVersion)
                    {
                        corrupt = true;
                        return false;
                    }

                    var srcDomain = (IndexDomain)br.ReadByte();
                    var dstDomain = (IndexDomain)br.ReadByte();
                    int srcCount = br.ReadInt32();
                    int dstCount = br.ReadInt32();
                    int offsetsLen = br.ReadInt32();
                    if (srcCount < 0 || dstCount < 0 || offsetsLen != srcCount + 1)
                    {
                        corrupt = true;
                        return false;
                    }

                    // Check declared byte count against remaining stream BEFORE allocating arrays.
                    long remainingBeforeOffsets = fs.Length - fs.Position;
                    long neededOffsetBytes = (long)offsetsLen * 4L + 4L;
                    if (neededOffsetBytes < 0L || neededOffsetBytes > remainingBeforeOffsets)
                    {
                        corrupt = true;
                        return false;
                    }

                    var offsets = new int[offsetsLen];
                    for (int i = 0; i < offsetsLen; i++)
                    {
                        offsets[i] = br.ReadInt32();
                    }

                    int targetsLen = br.ReadInt32();
                    if (targetsLen < 0 || targetsLen != offsets[srcCount])
                    {
                        corrupt = true;
                        return false;
                    }

                    long remainingBeforeTargets = fs.Length - fs.Position;
                    long neededTargetBytes = (long)targetsLen * 4L;
                    if (neededTargetBytes < 0L || neededTargetBytes != remainingBeforeTargets)
                    {
                        corrupt = true;
                        return false;
                    }

                    var targets = new int[targetsLen];
                    for (int i = 0; i < targetsLen; i++)
                    {
                        targets[i] = br.ReadInt32();
                    }

                    var candidate = new CsrVertexMap(srcDomain, dstDomain, srcCount, dstCount, offsets, targets, takeOwnership: true);
                    string actualBlobId = CsrVertexMapFactory.ComputeCsrBlobId(candidate);
                    if (!string.Equals(actualBlobId, expectedBlobId, StringComparison.Ordinal))
                    {
                        corrupt = true;
                        return false;
                    }

                    csr = candidate;
                    return true;
                }
            }
            catch
            {
                corrupt = true;
                return false;
            }
            finally
            {
                // Self-heal: remove known-corrupt disk entry so recomputation publishes a clean blob.
                if (corrupt)
                {
                    TryDeleteQuietly(path);
                }
            }
        }

        private void TryWriteIndexToDisk(BindingResolutionIndexEntry entry)
        {
            try
            {
                Directory.CreateDirectory(m_IndexDirectory);
                string targetPath = GetIndexPath(entry.ResolutionKeyHash);
                string tempPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    byte[] payloadBytes;
                    using (var ms = new MemoryStream(192))
                    using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
                    {
                        bw.Write(s_IndexMagic);
                        bw.Write(k_FormatVersion);
                        bw.Write(entry.ResolutionKeyHash);
                        bw.Write(entry.CsrBlobId);
                        bw.Write(entry.OriginControlPointCount);
                        bw.Write(entry.UnityVertexCount);
                        bw.Write(entry.MatchedScale);
                        bw.Write(entry.MatchedBakeAxisConversion);
                        bw.Flush();
                        payloadBytes = ms.ToArray();
                    }

                    byte[] integrityHash;
                    using (var sha = SHA256.Create())
                    {
                        integrityHash = sha.ComputeHash(payloadBytes);
                    }

                    using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        fs.Write(payloadBytes, 0, payloadBytes.Length);
                        fs.Write(integrityHash, 0, integrityHash.Length);
                        fs.Flush(flushToDisk: false);
                    }

                    AtomicPublish(tempPath, targetPath);
                }
                catch
                {
                    TryDeleteQuietly(tempPath);
                }
            }
            catch
            {
                // Best-effort disk cache.
            }
        }

        private bool TryReadIndexFromDisk(string expectedKeyHash, out BindingResolutionIndexEntry entry)
        {
            entry = null;
            string path = GetIndexPath(expectedKeyHash);
            if (!File.Exists(path))
            {
                return false;
            }

            bool corrupt = false;
            try
            {
                byte[] allBytes = File.ReadAllBytes(path);
                const int hashLen = 32;
                if (allBytes.Length <= s_IndexMagic.Length + 4 + hashLen)
                {
                    corrupt = true;
                    return false;
                }

                int payloadLen = allBytes.Length - hashLen;
                byte[] actualHash;
                using (var sha = SHA256.Create())
                {
                    actualHash = sha.ComputeHash(allBytes, 0, payloadLen);
                }

                for (int i = 0; i < hashLen; i++)
                {
                    if (actualHash[i] != allBytes[payloadLen + i])
                    {
                        corrupt = true;
                        return false;
                    }
                }

                using (var ms = new MemoryStream(allBytes, 0, payloadLen, writable: false))
                using (var br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: false))
                {
                    var magic = br.ReadBytes(s_IndexMagic.Length);
                    if (!BytesEqual(magic, s_IndexMagic))
                    {
                        corrupt = true;
                        return false;
                    }

                    int version = br.ReadInt32();
                    if (version != k_FormatVersion)
                    {
                        corrupt = true;
                        return false;
                    }

                    string keyHash = br.ReadString();
                    string blobId = br.ReadString();
                    int cpCount = br.ReadInt32();
                    int vtxCount = br.ReadInt32();
                    float scale = br.ReadSingle();
                    bool bakeAxis = br.ReadBoolean();

                    if (ms.Position != ms.Length ||
                        !string.Equals(keyHash, expectedKeyHash, StringComparison.Ordinal) ||
                        !IsValidHexKey(blobId) ||
                        cpCount < 0 ||
                        vtxCount < 0 ||
                        float.IsNaN(scale) ||
                        float.IsInfinity(scale) ||
                        scale <= 0f)
                    {
                        corrupt = true;
                        return false;
                    }

                    entry = new BindingResolutionIndexEntry(keyHash, blobId, cpCount, vtxCount, scale, bakeAxis);
                    return true;
                }
            }
            catch
            {
                corrupt = true;
                return false;
            }
            finally
            {
                if (corrupt)
                {
                    TryDeleteQuietly(path);
                }
            }
        }

        private static void AtomicPublish(string tempPath, string targetPath)
        {
            if (File.Exists(targetPath))
            {
                File.Replace(tempPath, targetPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, targetPath);
            }
        }

        private static void TryDeleteQuietly(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // Ignore temp cleanup errors.
            }
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
            {
                return false;
            }

            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }

            return true;
        }

        private static bool IsValidHexKey(string key)
        {
            if (string.IsNullOrEmpty(key) || key.Length != 64)
            {
                return false;
            }

            for (int i = 0; i < key.Length; i++)
            {
                char c = key[i];
                bool valid = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                if (!valid) return false;
            }

            return true;
        }
    }
}
