using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Mapping;
using Triturbo.BlendShare.Model.Documents;
using Triturbo.BlendShare.Primitives;

namespace Triturbo.BlendShare.EditorAssets.Mapping
{
    /// <summary>
    /// Layer 2 index entry mapping a conservative resolution key to a content-addressed Layer 1 CSR blob ID
    /// plus matched FBX-to-Unity importer parameters (excluding per-use SpaceContract).
    /// </summary>
    public sealed class VertexMappingIndexEntry
    {
        public string ResolutionKeyHash { get; }
        public string CsrBlobId { get; }
        public int ControlPointCount { get; }
        public int UnityVertexCount { get; }
        public float MatchedScale { get; }
        public bool MatchedBakeAxisConversion { get; }

        public VertexMappingIndexEntry(
            string resolutionKeyHash,
            string csrBlobId,
            int controlPointCount,
            int unityVertexCount,
            float matchedScale,
            bool matchedBakeAxisConversion)
        {
            ResolutionKeyHash = resolutionKeyHash ?? string.Empty;
            CsrBlobId = csrBlobId ?? string.Empty;
            ControlPointCount = controlPointCount;
            UnityVertexCount = unityVertexCount;
            MatchedScale = matchedScale;
            MatchedBakeAxisConversion = matchedBakeAxisConversion;
        }
    }

    public readonly struct VertexMappingDiskCacheStats
    {
        public int IndexCount { get; }
        public int BlobCount { get; }
        public long TotalBytes { get; }

        public VertexMappingDiskCacheStats(int indexCount, int blobCount, long totalBytes)
        {
            IndexCount = indexCount;
            BlobCount = blobCount;
            TotalBytes = totalBytes;
        }
    }

    /// <summary>
    /// On-disk + in-memory 2-layer vertex mapping cache under <c>Library/BlendShare/VertexMappings/v1/</c>.
    /// Layer 1: Content-addressed CSR blobs (<c>blobs/&lt;csrBlobId&gt;.bin</c>) with strict SHA-256 and CSR bounds checks.
    /// Layer 2: Conservative resolution index (<c>index/&lt;resolutionKeyHash&gt;.bin</c>).
    /// Layer 3 (<see cref="MappingSpaceContract"/>) is NEVER cached in the CSR blob and is computed per resolution use.
    /// Bounded by hard LRU count + byte caps (75% low-watermark hysteresis on breached dimensions),
    /// Mark-and-Sweep orphan blob GC, and process-wide per-root locking.
    /// Feature selection/settings changes NEVER invalidate this cache.
    /// </summary>
    public sealed class VertexMappingCacheStore
    {
        public const string DefaultRelativeRoot = "Library/BlendShare/VertexMappings/v1";
        public const int DefaultMaxMemoryEntries = 256;
        /// <summary>Default in-memory CSR-data byte budget (counts distinct retained CSR index/offset arrays; excludes CLR object/dictionary overhead).</summary>
        public const long DefaultMaxMemoryTotalBytes = 32L * 1024L * 1024L;
        public const int DefaultMaxDiskIndexCount = 512;
        public const long DefaultMaxDiskTotalBytes = 64L * 1024L * 1024L;
        public const int DefaultPruneWriteInterval = 16;
        public static readonly TimeSpan DefaultAccessTouchInterval = TimeSpan.FromHours(1);
        public static readonly TimeSpan DefaultStaleTempFileAge = TimeSpan.FromMinutes(10);

        internal const int FbxReaderAlgoRevision = 1;
        private const int k_MappingSpaceAlgoRevision = 3;
        private const int k_FormatVersion = 2;
        private const double k_LowWatermarkRatio = 0.75d;

        private static readonly byte[] s_BlobMagic = Encoding.ASCII.GetBytes("BSCSR001");
        private static readonly byte[] s_IndexMagic = Encoding.ASCII.GetBytes("BSIDX001");

        private static readonly object s_RootStatesLock = new object();
        private static readonly Dictionary<string, RootDiskState> s_RootStates =
            new Dictionary<string, RootDiskState>(StringComparer.Ordinal);

        private sealed class RootDiskState
        {
            public readonly object Lock = new object();
            public bool Initialized;
            public int EstimatedIndexCount;
            public long EstimatedTotalBytes;
            public int WritesSinceLastPrune;
            public long DiskMutationGeneration;
        }

        private sealed class MemoryIndexSlot
        {
            public VertexMappingIndexEntry Entry;
            public long LastAccessSeq;
            public DateTime LastDiskTouchUtc;
            public bool IsPersistedOnDisk;
            public long PersistedAtGeneration;
        }

        private enum BlobEnsureOutcome : byte
        {
            Failed = 0,
            AlreadyPresentValid = 1,
            CreatedNew = 2,
            ReplacedExisting = 3
        }

        private enum DiskReadStatus : byte
        {
            NotFound = 0,
            Success = 1,
            Corrupt = 2,
            TransientIoError = 3,
            PermissionDenied = 4
        }

        private readonly object m_MemoryLock = new object();
        private readonly RootDiskState m_RootState;
        private readonly string m_RootDirectory;
        private readonly string m_BlobsDirectory;
        private readonly string m_IndexDirectory;
        private readonly int m_MaxMemoryEntries;
        private readonly long m_MaxMemoryTotalBytes;
        private readonly int m_MaxDiskIndexCount;
        private readonly long m_MaxDiskTotalBytes;
        private readonly int m_PruneWriteInterval;
        private readonly TimeSpan m_AccessTouchInterval;
        private readonly TimeSpan m_StaleTempFileAge;

        private readonly Dictionary<string, CsrVertexMap> m_MemoryBlobs =
            new Dictionary<string, CsrVertexMap>(StringComparer.Ordinal);
        private readonly Dictionary<string, MemoryIndexSlot> m_MemoryIndex =
            new Dictionary<string, MemoryIndexSlot>(StringComparer.Ordinal);
        private long m_MemoryBlobBytes;
        private long m_MemoryAccessSeq;

        internal event Action MemoryCacheCleared;

        public string RootDirectory => m_RootDirectory;
        public int MaxMemoryEntries => m_MaxMemoryEntries;
        public long MaxMemoryTotalBytes => m_MaxMemoryTotalBytes;
        public int MaxDiskIndexCount => m_MaxDiskIndexCount;
        public long MaxDiskTotalBytes => m_MaxDiskTotalBytes;

        public int MemoryEntryCount
        {
            get
            {
                lock (m_MemoryLock)
                {
                    return m_MemoryIndex.Count;
                }
            }
        }

        public long MemoryBlobBytes
        {
            get
            {
                lock (m_MemoryLock)
                {
                    return m_MemoryBlobBytes;
                }
            }
        }

        internal static void WriteSpaceKey(BinaryWriter writer, PatchSpaceProvenance stored)
        {
            var axes = stored?.Axes ?? CanonicalMeshSpace.CanonicalAxes;
            writer.Write(k_MappingSpaceAlgoRevision);
            writer.Write((int)axes.Right);
            writer.Write((int)axes.Up);
            writer.Write((int)axes.Front);
            writer.Write(stored?.MetersPerUnit ?? 1d);
        }

        public VertexMappingCacheStore(
            string rootDirectory = null,
            int maxMemoryEntries = DefaultMaxMemoryEntries,
            int maxDiskIndexCount = DefaultMaxDiskIndexCount,
            long maxDiskTotalBytes = DefaultMaxDiskTotalBytes,
            int pruneWriteInterval = DefaultPruneWriteInterval,
            TimeSpan? accessTouchInterval = null,
            TimeSpan? staleTempFileAge = null,
            long maxMemoryTotalBytes = DefaultMaxMemoryTotalBytes)
        {
            string fullRoot = string.IsNullOrWhiteSpace(rootDirectory)
                ? Path.GetFullPath(DefaultRelativeRoot)
                : Path.GetFullPath(rootDirectory);
            m_RootDirectory = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.IsNullOrEmpty(m_RootDirectory))
            {
                m_RootDirectory = fullRoot;
            }

            m_RootState = GetOrCreateRootState(m_RootDirectory);
            m_BlobsDirectory = Path.Combine(m_RootDirectory, "blobs");
            m_IndexDirectory = Path.Combine(m_RootDirectory, "index");
            m_MaxMemoryEntries = Math.Max(1, maxMemoryEntries);
            m_MaxMemoryTotalBytes = Math.Max(64L, maxMemoryTotalBytes);
            m_MaxDiskIndexCount = Math.Max(1, maxDiskIndexCount);
            m_MaxDiskTotalBytes = Math.Max(1024L, maxDiskTotalBytes);
            m_PruneWriteInterval = Math.Max(1, pruneWriteInterval);
            m_AccessTouchInterval = accessTouchInterval ?? DefaultAccessTouchInterval;
            m_StaleTempFileAge = staleTempFileAge ?? DefaultStaleTempFileAge;
        }

        private static RootDiskState GetOrCreateRootState(string normalizedRoot)
        {
            string key = CanonicalizeRootKey(normalizedRoot);
            lock (s_RootStatesLock)
            {
                if (!s_RootStates.TryGetValue(key, out var state))
                {
                    state = new RootDiskState();
                    s_RootStates.Add(key, state);
                }

                return state;
            }
        }

        /// <summary>
        /// Builds a caller-spelling-independent root key that merges case-only variants on case-insensitive
        /// directories (both before and after the leaf directory is created) while keeping distinct keys on
        /// case-sensitive filesystems or case-sensitive directories (including Windows per-directory CS).
        /// </summary>
        internal static string CanonicalizeRootKey(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath))
            {
                return string.Empty;
            }

            try
            {
                string root = Path.GetPathRoot(fullPath) ?? string.Empty;
                string remainder = fullPath.Substring(root.Length);
                var segments = remainder.Split(
                    new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                    StringSplitOptions.RemoveEmptyEntries);

                bool fallbackCaseInsensitive = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
                                               RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
                string currentFsPath = root;
                bool currentDirCaseInsensitive = TryProbeDirectoryCaseInsensitive(currentFsPath, out bool rootCi)
                    ? rootCi
                    : fallbackCaseInsensitive;

                var keyBuilder = new StringBuilder(fullPath.Length);
                keyBuilder.Append(currentDirCaseInsensitive ? root.ToUpperInvariant() : root);

                for (int i = 0; i < segments.Length; i++)
                {
                    string seg = segments[i];
                    if (i > 0)
                    {
                        keyBuilder.Append(Path.DirectorySeparatorChar);
                    }

                    // Determine how currentFsPath matches children using its actual on-disk entries (independent of seg's spelling).
                    if (Directory.Exists(currentFsPath) &&
                        TryProbeDirectoryCaseInsensitive(currentFsPath, out bool probedCi))
                    {
                        currentDirCaseInsensitive = probedCi;
                    }

                    keyBuilder.Append(currentDirCaseInsensitive ? seg.ToUpperInvariant() : seg);

                    if (Directory.Exists(currentFsPath))
                    {
                        currentFsPath = ResolveActualChildPath(currentFsPath, seg, currentDirCaseInsensitive);
                    }
                    else
                    {
                        currentFsPath = Path.Combine(currentFsPath, seg);
                    }
                }

                return keyBuilder.ToString();
            }
            catch
            {
                return (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    ? fullPath.ToUpperInvariant()
                    : fullPath;
            }
        }

        private static string ResolveActualChildPath(string parentDir, string segment, bool parentIsCaseInsensitive)
        {
            string direct = Path.Combine(parentDir, segment);
            if (!parentIsCaseInsensitive)
            {
                return direct;
            }

            try
            {
                foreach (string entry in Directory.EnumerateFileSystemEntries(parentDir))
                {
                    string name = Path.GetFileName(entry);
                    if (string.Equals(name, segment, StringComparison.OrdinalIgnoreCase))
                    {
                        return entry;
                    }
                }
            }
            catch
            {
                // Ignore enumeration errors.
            }

            return direct;
        }

        /// <summary>
        /// Probes whether <paramref name="directory"/> resolves child names case-insensitively by flipping
        /// an actual on-disk child name (never the caller's spelling), guaranteeing symmetry across callers.
        /// </summary>
        private static bool TryProbeDirectoryCaseInsensitive(string directory, out bool isCaseInsensitive)
        {
            isCaseInsensitive = false;
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return false;
            }

            try
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    string name = Path.GetFileName(entry);
                    if (!string.IsNullOrEmpty(name))
                    {
                        names.Add(name);
                    }
                }

                foreach (string actualName in names)
                {
                    if (!TryFlipFirstAsciiLetterCase(actualName, out string flippedName))
                    {
                        continue;
                    }

                    // If both literal spellings actually exist as distinct entries, this directory is definitely case-sensitive.
                    if (names.Contains(flippedName))
                    {
                        isCaseInsensitive = false;
                        return true;
                    }

                    string flippedPath = Path.Combine(directory, flippedName);
                    isCaseInsensitive = Directory.Exists(flippedPath) || File.Exists(flippedPath);
                    return true;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private static bool TryFlipFirstAsciiLetterCase(string value, out string flipped)
        {
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c >= 'a' && c <= 'z')
                {
                    var chars = value.ToCharArray();
                    chars[i] = (char)(c - 'a' + 'A');
                    flipped = new string(chars);
                    return true;
                }

                if (c >= 'A' && c <= 'Z')
                {
                    var chars = value.ToCharArray();
                    chars[i] = (char)(c - 'A' + 'a');
                    flipped = new string(chars);
                    return true;
                }
            }

            flipped = null;
            return false;
        }

        public void ClearMemoryCache()
        {
            lock (m_MemoryLock)
            {
                m_MemoryBlobs.Clear();
                m_MemoryIndex.Clear();
                m_MemoryBlobBytes = 0L;
            }

            MemoryCacheCleared?.Invoke();
        }

        public void ClearDiskCache()
        {
            lock (m_RootState.Lock)
            {
                DeleteAllFilesQuietly(m_IndexDirectory);
                DeleteAllFilesQuietly(m_BlobsDirectory);
                RecountDiskEstimatesLocked();
                m_RootState.WritesSinceLastPrune = 0;
                Interlocked.Increment(ref m_RootState.DiskMutationGeneration);
            }

            lock (m_MemoryLock)
            {
                foreach (var slot in m_MemoryIndex.Values)
                {
                    slot.IsPersistedOnDisk = false;
                    slot.PersistedAtGeneration = -1L;
                    slot.LastDiskTouchUtc = DateTime.MinValue;
                }
            }
        }

        public VertexMappingDiskCacheStats GetDiskCacheStats()
        {
            lock (m_RootState.Lock)
            {
                int indexCount = 0;
                int blobCount = 0;
                long totalBytes = 0L;

                if (Directory.Exists(m_IndexDirectory))
                {
                    try
                    {
                        foreach (var path in Directory.EnumerateFiles(m_IndexDirectory, "*.bin"))
                        {
                            try
                            {
                                totalBytes += new FileInfo(path).Length;
                                indexCount++;
                            }
                            catch
                            {
                                // Ignore concurrent removal.
                            }
                        }
                    }
                    catch
                    {
                        // Ignore inaccessible directory.
                    }
                }

                if (Directory.Exists(m_BlobsDirectory))
                {
                    try
                    {
                        foreach (var path in Directory.EnumerateFiles(m_BlobsDirectory, "*.bin"))
                        {
                            try
                            {
                                totalBytes += new FileInfo(path).Length;
                                blobCount++;
                            }
                            catch
                            {
                                // Ignore concurrent removal.
                            }
                        }
                    }
                    catch
                    {
                        // Ignore inaccessible directory.
                    }
                }

                return new VertexMappingDiskCacheStats(indexCount, blobCount, totalBytes);
            }
        }

        public bool TryLoadResolution(
            string resolutionKeyHash,
            out VertexMappingIndexEntry indexEntry,
            out CsrVertexMap csrMap,
            bool promoteToMemory = true,
            bool persistToDisk = false)
        {
            indexEntry = null;
            csrMap = null;
            if (!IsValidHexKey(resolutionKeyHash))
            {
                return false;
            }

            DateTime nowUtc = DateTime.UtcNow;
            bool wasPersisted = false;
            long persistedGen = -1L;
            bool shouldTouchDisk = false;

            lock (m_MemoryLock)
            {
                if (m_MemoryIndex.TryGetValue(resolutionKeyHash, out var slot) &&
                    m_MemoryBlobs.TryGetValue(slot.Entry.CsrBlobId, out var memBlob))
                {
                    slot.LastAccessSeq = ++m_MemoryAccessSeq;
                    wasPersisted = slot.IsPersistedOnDisk;
                    persistedGen = slot.PersistedAtGeneration;
                    shouldTouchDisk = wasPersisted && (nowUtc - slot.LastDiskTouchUtc >= m_AccessTouchInterval);
                    indexEntry = slot.Entry;
                    csrMap = memBlob;
                }
            }

            if (indexEntry != null)
            {
                long currentRootGen = Interlocked.Read(ref m_RootState.DiskMutationGeneration);
                bool needsDiskEnsure = persistToDisk && (!wasPersisted || persistedGen != currentRootGen);

                if (needsDiskEnsure)
                {
                    bool nowPersisted = EnsureResolutionPersistedOnDisk(indexEntry, csrMap, nowUtc, out long newGen);
                    UpdateMemorySlotDiskState(resolutionKeyHash, indexEntry.CsrBlobId, nowPersisted, newGen, nowUtc);
                }
                else if (shouldTouchDisk)
                {
                    if (TryTouchIndexFileQuietly(resolutionKeyHash, nowUtc))
                    {
                        UpdateMemorySlotDiskTouch(resolutionKeyHash, indexEntry.CsrBlobId, nowUtc);
                    }
                    else if (persistToDisk)
                    {
                        bool nowPersisted = EnsureResolutionPersistedOnDisk(indexEntry, csrMap, nowUtc, out long newGen);
                        UpdateMemorySlotDiskState(resolutionKeyHash, indexEntry.CsrBlobId, nowPersisted, newGen, nowUtc);
                    }
                    else
                    {
                        UpdateMemorySlotDiskState(resolutionKeyHash, indexEntry.CsrBlobId, false, -1L, DateTime.MinValue);
                    }
                }

                return true;
            }

            long genBeforeDiskRead = Interlocked.Read(ref m_RootState.DiskMutationGeneration);
            if (!TryReadIndexFromDisk(resolutionKeyHash, out var diskIdx))
            {
                return false;
            }

            // Require an actual disk blob to exist for a disk index hit so a memory-only blob cannot mask a missing disk blob.
            CsrVertexMap diskBlob = null;
            string diskBlobPath = GetBlobPath(diskIdx.CsrBlobId);
            if (!File.Exists(diskBlobPath))
            {
                return false;
            }

            lock (m_MemoryLock)
            {
                m_MemoryBlobs.TryGetValue(diskIdx.CsrBlobId, out diskBlob);
            }

            if (diskBlob == null && !TryReadCsrBlobFromDisk(diskIdx.CsrBlobId, out diskBlob))
            {
                return false;
            }

            if (diskBlob.SourceCount != diskIdx.UnityVertexCount ||
                diskBlob.TargetCount != diskIdx.ControlPointCount)
            {
                return false;
            }

            TryTouchIndexFileIfStale(resolutionKeyHash, nowUtc);

            if (promoteToMemory && EstimateCsrBytes(diskBlob) <= m_MaxMemoryTotalBytes)
            {
                lock (m_MemoryLock)
                {
                    PutMemoryBlobLocked(diskIdx.CsrBlobId, diskBlob);
                    m_MemoryIndex[resolutionKeyHash] = new MemoryIndexSlot
                    {
                        Entry = diskIdx,
                        LastAccessSeq = ++m_MemoryAccessSeq,
                        LastDiskTouchUtc = nowUtc,
                        IsPersistedOnDisk = true,
                        PersistedAtGeneration = genBeforeDiskRead
                    };
                    TrimMemoryIfNeededLocked();
                }
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
            bool writeToDisk = true,
            bool writeToMemory = true)
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
            var indexEntry = new VertexMappingIndexEntry(
                resolutionKeyHash,
                blobId,
                csrMap.TargetCount,
                csrMap.SourceCount,
                matchedScale,
                matchedBakeAxisConversion);

            DateTime nowUtc = DateTime.UtcNow;
            bool persistedOnDisk = false;
            long persistedGen = -1L;

            if (writeToDisk)
            {
                persistedOnDisk = TryPublishResolutionToDisk(indexEntry, csrMap, out persistedGen);
            }

            if (writeToMemory && EstimateCsrBytes(csrMap) <= m_MaxMemoryTotalBytes)
            {
                lock (m_MemoryLock)
                {
                    PutMemoryBlobLocked(blobId, csrMap);
                    m_MemoryIndex[resolutionKeyHash] = new MemoryIndexSlot
                    {
                        Entry = indexEntry,
                        LastAccessSeq = ++m_MemoryAccessSeq,
                        LastDiskTouchUtc = persistedOnDisk ? nowUtc : DateTime.MinValue,
                        IsPersistedOnDisk = persistedOnDisk,
                        PersistedAtGeneration = persistedOnDisk ? persistedGen : -1L
                    };
                    TrimMemoryIfNeededLocked();
                }
            }

            return blobId;
        }

        public bool TryLoadCsrBlob(string csrBlobId, out CsrVertexMap csrMap, bool promoteToMemory = true)
        {
            csrMap = null;
            if (!IsValidHexKey(csrBlobId))
            {
                return false;
            }

            lock (m_MemoryLock)
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

            if (promoteToMemory && EstimateCsrBytes(diskBlob) <= m_MaxMemoryTotalBytes)
            {
                lock (m_MemoryLock)
                {
                    PutMemoryBlobLocked(csrBlobId, diskBlob);
                    TrimMemoryIfNeededLocked(pinnedBlobId: csrBlobId);
                }
            }

            csrMap = diskBlob;
            return true;
        }

        /// <summary>
        /// Enforces bounded disk limits using LRU eviction down to 75% low-watermark on breached dimensions
        /// and performs a full Mark-and-Sweep orphan blob GC.
        /// </summary>
        public void PruneDiskCache()
        {
            lock (m_RootState.Lock)
            {
                PruneDiskCacheLocked(forceSweepOrphans: true);
            }
        }

        private bool EnsureResolutionPersistedOnDisk(
            VertexMappingIndexEntry indexEntry,
            CsrVertexMap csrMap,
            DateTime nowUtc,
            out long persistedGeneration)
        {
            byte[] blobBytes = SerializeCsrBlobBytes(csrMap);
            byte[] indexBytes = SerializeIndexBytes(indexEntry);
            if ((long)blobBytes.Length + indexBytes.Length > m_MaxDiskTotalBytes)
            {
                persistedGeneration = -1L;
                return false;
            }

            lock (m_RootState.Lock)
            {
                string indexPath = GetIndexPath(indexEntry.ResolutionKeyHash);
                string blobPath = GetBlobPath(indexEntry.CsrBlobId);
                if (HasExpectedFileSize(indexPath, indexBytes.Length) &&
                    HasExpectedFileSize(blobPath, blobBytes.Length) &&
                    ReadIndexFileRaw(indexPath, indexEntry.ResolutionKeyHash, out var diskIdx) == DiskReadStatus.Success &&
                    IndexEntriesEqual(diskIdx, indexEntry) &&
                    ReadCsrBlobFileRaw(blobPath, indexEntry.CsrBlobId, out _) == DiskReadStatus.Success)
                {
                    TryTouchIndexFileIfStale(indexEntry.ResolutionKeyHash, nowUtc);
                    persistedGeneration = Interlocked.Read(ref m_RootState.DiskMutationGeneration);
                    return true;
                }

                return PublishSerializedResolutionLocked(indexEntry, blobBytes, indexBytes, out persistedGeneration);
            }
        }

        private bool TryPublishResolutionToDisk(
            VertexMappingIndexEntry indexEntry,
            CsrVertexMap csrMap,
            out long persistedGeneration)
        {
            byte[] blobBytes = SerializeCsrBlobBytes(csrMap);
            byte[] indexBytes = SerializeIndexBytes(indexEntry);
            if ((long)blobBytes.Length + indexBytes.Length > m_MaxDiskTotalBytes)
            {
                persistedGeneration = -1L;
                return false;
            }

            lock (m_RootState.Lock)
            {
                return PublishSerializedResolutionLocked(indexEntry, blobBytes, indexBytes, out persistedGeneration);
            }
        }

        private bool PublishSerializedResolutionLocked(
            VertexMappingIndexEntry indexEntry,
            byte[] blobBytes,
            byte[] indexBytes,
            out long persistedGeneration)
        {
            persistedGeneration = -1L;
            if (!m_RootState.Initialized)
            {
                RecountDiskEstimatesLocked();
            }

            var blobOutcome = EnsureCsrBlobOnDiskLocked(indexEntry.CsrBlobId, blobBytes, out long blobBytesDelta);
            if (blobOutcome == BlobEnsureOutcome.Failed)
            {
                return false;
            }

            m_RootState.EstimatedTotalBytes = Math.Max(0L, m_RootState.EstimatedTotalBytes + blobBytesDelta);

            if (!TryWriteIndexToDiskLocked(indexEntry.ResolutionKeyHash, indexBytes, out bool isNewIndex, out long indexBytesDelta))
            {
                // Directly roll back ONLY a brand-new blob we just created (never delete a repaired pre-existing
                // blob that another index may still reference), and reconcile caps if needed.
                if (blobOutcome == BlobEnsureOutcome.CreatedNew)
                {
                    if (TryDeleteQuietly(GetBlobPath(indexEntry.CsrBlobId)))
                    {
                        m_RootState.EstimatedTotalBytes = Math.Max(0L, m_RootState.EstimatedTotalBytes - blobBytesDelta);
                        Interlocked.Increment(ref m_RootState.DiskMutationGeneration);
                    }
                    else
                    {
                        PruneDiskCacheLocked(forceSweepOrphans: true);
                    }
                }
                else if (m_RootState.EstimatedTotalBytes > m_MaxDiskTotalBytes)
                {
                    PruneDiskCacheLocked(forceSweepOrphans: false);
                }

                return false;
            }

            if (isNewIndex)
            {
                m_RootState.EstimatedIndexCount++;
            }

            m_RootState.EstimatedTotalBytes = Math.Max(0L, m_RootState.EstimatedTotalBytes + indexBytesDelta);
            m_RootState.WritesSinceLastPrune++;

            bool exceedsCount = m_RootState.EstimatedIndexCount > m_MaxDiskIndexCount;
            bool exceedsBytes = m_RootState.EstimatedTotalBytes > m_MaxDiskTotalBytes;
            bool intervalDue = m_RootState.WritesSinceLastPrune >= m_PruneWriteInterval;

            if (exceedsCount || exceedsBytes || intervalDue)
            {
                PruneDiskCacheLocked(forceSweepOrphans: false);
            }

            string indexPath = GetIndexPath(indexEntry.ResolutionKeyHash);
            string blobPath = GetBlobPath(indexEntry.CsrBlobId);
            bool survived = File.Exists(indexPath) && File.Exists(blobPath);
            if (survived)
            {
                persistedGeneration = Interlocked.Read(ref m_RootState.DiskMutationGeneration);
            }

            return survived;
        }

        private void PruneDiskCacheLocked(bool forceSweepOrphans)
        {
            m_RootState.Initialized = true;
            m_RootState.WritesSinceLastPrune = 0;

            DateTime nowUtc = DateTime.UtcNow;
            CleanupStaleTempFiles(m_IndexDirectory, nowUtc);
            CleanupStaleTempFiles(m_BlobsDirectory, nowUtc);

            var indexFiles = CollectBinFiles(m_IndexDirectory, out bool indexScanComplete);
            var blobFiles = CollectBinFiles(m_BlobsDirectory, out bool blobScanComplete);

            var blobById = new Dictionary<string, FileInfo>(blobFiles.Count, StringComparer.Ordinal);
            long rawTotalBytes = 0L;
            bool deletedAny = false;

            for (int i = 0; i < indexFiles.Count; i++)
            {
                rawTotalBytes += indexFiles[i].Length;
            }

            for (int i = 0; i < blobFiles.Count; i++)
            {
                var fi = blobFiles[i];
                string blobId = Path.GetFileNameWithoutExtension(fi.Name);
                if (IsValidHexKey(blobId))
                {
                    blobById[blobId] = fi;
                    rawTotalBytes += fi.Length;
                }
                else if (TryDeleteQuietly(fi.FullName))
                {
                    deletedAny = true;
                }
                else
                {
                    rawTotalBytes += fi.Length;
                }
            }

            bool overRawIndexLimit = indexFiles.Count > m_MaxDiskIndexCount;
            bool overRawByteLimit = rawTotalBytes > m_MaxDiskTotalBytes;
            bool excessiveOrphanBlobs = blobById.Count > Math.Max(m_MaxDiskIndexCount * 2, 16);

            if (!forceSweepOrphans && !overRawIndexLimit && !overRawByteLimit && !excessiveOrphanBlobs)
            {
                m_RootState.EstimatedIndexCount = indexFiles.Count;
                m_RootState.EstimatedTotalBytes = rawTotalBytes;
                if (deletedAny)
                {
                    Interlocked.Increment(ref m_RootState.DiskMutationGeneration);
                }

                return;
            }

            // Newest accessed first; tie-break deterministically by filename.
            indexFiles.Sort((a, b) =>
            {
                int timeCmp = b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc);
                return timeCmp != 0 ? timeCmp : string.CompareOrdinal(a.Name, b.Name);
            });

            // Phase 1: Validate all index entries and compute live referenced count and bytes (ignoring orphans).
            var validIndices = new List<(FileInfo File, VertexMappingIndexEntry Entry, FileInfo BlobFile)>(indexFiles.Count);
            var liveReferencedBlobs = new HashSet<string>(StringComparer.Ordinal);
            long liveTotalBytes = 0L;
            bool hadTransientUnreadableIndex = !indexScanComplete;

            for (int i = 0; i < indexFiles.Count; i++)
            {
                var fi = indexFiles[i];
                string keyHash = Path.GetFileNameWithoutExtension(fi.Name);
                if (!IsValidHexKey(keyHash))
                {
                    if (TryDeleteQuietly(fi.FullName)) deletedAny = true;
                    continue;
                }

                var readStatus = ReadIndexFileRaw(fi.FullName, keyHash, out var entry);
                if (readStatus == DiskReadStatus.Corrupt)
                {
                    if (TryDeleteQuietly(fi.FullName)) deletedAny = true;
                    continue;
                }

                if (readStatus == DiskReadStatus.PermissionDenied)
                {
                    // Permission-denied index cannot be read on future attempts either; attempt cleanup
                    // and do NOT allow it to permanently disable orphan blob sweeping.
                    if (TryDeleteQuietly(fi.FullName)) deletedAny = true;
                    continue;
                }

                if (readStatus != DiskReadStatus.Success)
                {
                    if (readStatus == DiskReadStatus.TransientIoError)
                    {
                        hadTransientUnreadableIndex = true;
                    }

                    continue;
                }

                if (!blobById.TryGetValue(entry.CsrBlobId, out var blobFi))
                {
                    // Only treat as dangling when the blob directory scan was complete so transient blob-dir errors
                    // never delete valid indexes.
                    if (blobScanComplete && TryDeleteQuietly(fi.FullName)) deletedAny = true;
                    continue;
                }

                validIndices.Add((fi, entry, blobFi));
                liveTotalBytes += fi.Length;
                if (liveReferencedBlobs.Add(entry.CsrBlobId))
                {
                    liveTotalBytes += blobFi.Length;
                }
            }

            // Phase 2: Evict live entries ONLY if live count or live referenced bytes actually exceed hard caps.
            bool liveOverCount = validIndices.Count > m_MaxDiskIndexCount;
            bool liveOverBytes = liveTotalBytes > m_MaxDiskTotalBytes;

            HashSet<string> keptBlobs;
            if (!liveOverCount && !liveOverBytes)
            {
                keptBlobs = liveReferencedBlobs;
            }
            else
            {
                int targetIndexCount = liveOverCount
                    ? (m_MaxDiskIndexCount <= 1
                        ? m_MaxDiskIndexCount
                        : Math.Max(1, (int)Math.Floor(m_MaxDiskIndexCount * k_LowWatermarkRatio)))
                    : m_MaxDiskIndexCount;
                long targetTotalBytes = liveOverBytes
                    ? Math.Max(1L, (long)Math.Floor(m_MaxDiskTotalBytes * k_LowWatermarkRatio))
                    : m_MaxDiskTotalBytes;

                keptBlobs = new HashSet<string>(StringComparer.Ordinal);
                int keptCount = 0;
                long keptBytes = 0L;

                for (int i = 0; i < validIndices.Count; i++)
                {
                    var item = validIndices[i];
                    long marginalBytes = item.File.Length + (keptBlobs.Contains(item.Entry.CsrBlobId) ? 0L : item.BlobFile.Length);
                    bool fitsTarget = keptCount < targetIndexCount &&
                                      (keptBytes + marginalBytes <= targetTotalBytes ||
                                       (keptCount == 0 && marginalBytes <= m_MaxDiskTotalBytes));
                    if (fitsTarget)
                    {
                        keptCount++;
                        keptBytes += marginalBytes;
                        keptBlobs.Add(item.Entry.CsrBlobId);
                    }
                    else if (TryDeleteQuietly(item.File.FullName))
                    {
                        deletedAny = true;
                    }
                    else
                    {
                        // Failed deletion: keep its blob referenced so we don't leave a dangling index.
                        keptBlobs.Add(item.Entry.CsrBlobId);
                    }
                }
            }

            // Phase 3: Mark-and-Sweep unreferenced orphan blobs.
            // If index enumeration was incomplete or an index hit a transient I/O sharing error, preserve fresh
            // unreferenced blobs within the grace window (m_StaleTempFileAge) so we never delete a blob belonging
            // to a temporarily locked index, while still reaping stale orphans past the grace period so disk usage
            // remains bounded even under a stuck handle.
            foreach (var pair in blobById)
            {
                if (keptBlobs.Contains(pair.Key))
                {
                    continue;
                }

                if (hadTransientUnreadableIndex && nowUtc - pair.Value.LastWriteTimeUtc < m_StaleTempFileAge)
                {
                    continue;
                }

                if (TryDeleteQuietly(pair.Value.FullName))
                {
                    deletedAny = true;
                }
            }

            RecountDiskEstimatesLocked();
            if (deletedAny)
            {
                Interlocked.Increment(ref m_RootState.DiskMutationGeneration);
            }
        }

        private void RecountDiskEstimatesLocked()
        {
            int count = 0;
            long bytes = 0L;

            var idxFiles = CollectBinFiles(m_IndexDirectory, out _);
            count = idxFiles.Count;
            for (int i = 0; i < idxFiles.Count; i++)
            {
                bytes += idxFiles[i].Length;
            }

            var blbFiles = CollectBinFiles(m_BlobsDirectory, out _);
            for (int i = 0; i < blbFiles.Count; i++)
            {
                bytes += blbFiles[i].Length;
            }

            m_RootState.Initialized = true;
            m_RootState.EstimatedIndexCount = count;
            m_RootState.EstimatedTotalBytes = bytes;
        }

        private void UpdateMemorySlotDiskState(
            string keyHash,
            string expectedBlobId,
            bool isPersistedOnDisk,
            long persistedAtGeneration,
            DateTime lastDiskTouchUtc)
        {
            lock (m_MemoryLock)
            {
                if (m_MemoryIndex.TryGetValue(keyHash, out var slot) &&
                    string.Equals(slot.Entry.CsrBlobId, expectedBlobId, StringComparison.Ordinal))
                {
                    slot.IsPersistedOnDisk = isPersistedOnDisk;
                    slot.PersistedAtGeneration = persistedAtGeneration;
                    slot.LastDiskTouchUtc = lastDiskTouchUtc;
                }
            }
        }

        private void UpdateMemorySlotDiskTouch(string keyHash, string expectedBlobId, DateTime lastDiskTouchUtc)
        {
            lock (m_MemoryLock)
            {
                if (m_MemoryIndex.TryGetValue(keyHash, out var slot) &&
                    string.Equals(slot.Entry.CsrBlobId, expectedBlobId, StringComparison.Ordinal))
                {
                    slot.LastDiskTouchUtc = lastDiskTouchUtc;
                }
            }
        }

        private void PutMemoryBlobLocked(string blobId, CsrVertexMap csr)
        {
            if (m_MemoryBlobs.TryGetValue(blobId, out var existing))
            {
                m_MemoryBlobBytes -= EstimateCsrBytes(existing);
            }

            m_MemoryBlobs[blobId] = csr;
            m_MemoryBlobBytes += EstimateCsrBytes(csr);
        }

        private void RemoveMemoryBlobLocked(string blobId)
        {
            if (m_MemoryBlobs.TryGetValue(blobId, out var existing))
            {
                m_MemoryBlobs.Remove(blobId);
                m_MemoryBlobBytes = Math.Max(0L, m_MemoryBlobBytes - EstimateCsrBytes(existing));
            }
        }

        private static long EstimateCsrBytes(CsrVertexMap csr)
        {
            if (csr == null) return 0L;
            return ((long)csr.RowOffsets.Length + csr.TargetIndices.Length) * 4L;
        }

        private void TrimMemoryIfNeededLocked(string pinnedBlobId = null)
        {
            // Sweep unreferenced blobs FIRST so unreferenced entries from TryLoadCsrBlob never force eviction of live slots.
            if (m_MemoryBlobBytes > m_MaxMemoryTotalBytes || m_MemoryBlobs.Count > m_MaxMemoryEntries * 2)
            {
                SweepUnreferencedMemoryBlobsLocked(pinnedBlobId);
            }

            bool overCount = m_MemoryIndex.Count > m_MaxMemoryEntries;
            bool overBytes = m_MemoryBlobBytes > m_MaxMemoryTotalBytes;
            if (!overCount && !overBytes)
            {
                return;
            }

            int targetCount = overCount
                ? (m_MaxMemoryEntries <= 1
                    ? m_MaxMemoryEntries
                    : Math.Max(1, (int)Math.Floor(m_MaxMemoryEntries * k_LowWatermarkRatio)))
                : m_MaxMemoryEntries;
            long targetBytes = overBytes
                ? Math.Max(1L, (long)Math.Floor(m_MaxMemoryTotalBytes * k_LowWatermarkRatio))
                : m_MaxMemoryTotalBytes;

            var blobRefCounts = new Dictionary<string, int>(m_MemoryBlobs.Count, StringComparer.Ordinal);
            var ordered = new List<KeyValuePair<string, long>>(m_MemoryIndex.Count);
            foreach (var pair in m_MemoryIndex)
            {
                ordered.Add(new KeyValuePair<string, long>(pair.Key, pair.Value.LastAccessSeq));
                string bid = pair.Value.Entry.CsrBlobId;
                blobRefCounts.TryGetValue(bid, out int c);
                blobRefCounts[bid] = c + 1;
            }

            // Oldest accessed first.
            ordered.Sort((a, b) => a.Value.CompareTo(b.Value));

            for (int i = 0; i < ordered.Count; i++)
            {
                if (m_MemoryIndex.Count <= targetCount && m_MemoryBlobBytes <= targetBytes)
                {
                    break;
                }

                // Preserve the sole newest entry if it fits the hard byte cap (mirroring disk prune behavior).
                if (m_MemoryIndex.Count <= 1 && m_MemoryBlobBytes <= m_MaxMemoryTotalBytes)
                {
                    break;
                }

                string key = ordered[i].Key;
                if (!m_MemoryIndex.TryGetValue(key, out var slot))
                {
                    continue;
                }

                m_MemoryIndex.Remove(key);
                string bid = slot.Entry.CsrBlobId;
                if (blobRefCounts.TryGetValue(bid, out int remaining))
                {
                    remaining--;
                    if (remaining <= 0)
                    {
                        blobRefCounts.Remove(bid);
                        if (!string.Equals(bid, pinnedBlobId, StringComparison.Ordinal))
                        {
                            RemoveMemoryBlobLocked(bid);
                        }
                    }
                    else
                    {
                        blobRefCounts[bid] = remaining;
                    }
                }
            }

            SweepUnreferencedMemoryBlobsLocked(pinnedBlobId);
        }

        private void SweepUnreferencedMemoryBlobsLocked(string pinnedBlobId = null)
        {
            if (m_MemoryBlobs.Count == 0)
            {
                m_MemoryBlobBytes = 0L;
                return;
            }

            var referenced = new HashSet<string>(StringComparer.Ordinal);
            foreach (var slot in m_MemoryIndex.Values)
            {
                referenced.Add(slot.Entry.CsrBlobId);
            }

            if (!string.IsNullOrEmpty(pinnedBlobId))
            {
                referenced.Add(pinnedBlobId);
            }

            var stale = new List<string>();
            foreach (var blobId in m_MemoryBlobs.Keys)
            {
                if (!referenced.Contains(blobId))
                {
                    stale.Add(blobId);
                }
            }

            for (int i = 0; i < stale.Count; i++)
            {
                RemoveMemoryBlobLocked(stale[i]);
            }
        }

        private void CleanupStaleTempFiles(string directory, DateTime nowUtc)
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            try
            {
                foreach (var path in Directory.EnumerateFiles(directory, "*.tmp"))
                {
                    try
                    {
                        var fi = new FileInfo(path);
                        if (nowUtc - fi.LastWriteTimeUtc >= m_StaleTempFileAge)
                        {
                            TryDeleteQuietly(path);
                        }
                    }
                    catch
                    {
                        // Ignore concurrent removal.
                    }
                }
            }
            catch
            {
                // Ignore inaccessible directory.
            }
        }

        private static List<FileInfo> CollectBinFiles(string directory, out bool scanComplete)
        {
            scanComplete = true;
            var result = new List<FileInfo>();

            try
            {
                // Enumerate directly (without pre-gating on Directory.Exists, which swallows access/IO errors)
                // so only confirmed DirectoryNotFoundException is treated as an empty benign directory.
                foreach (var path in Directory.EnumerateFiles(directory, "*.bin"))
                {
                    try
                    {
                        var fi = new FileInfo(path);
                        // Reading Length refreshes metadata and throws FileNotFoundException on benign disappearance
                        // while surfacing IO/permission errors as incomplete scans.
                        _ = fi.Length;
                        result.Add(fi);
                    }
                    catch (FileNotFoundException)
                    {
                        // Concurrent removal is benign.
                    }
                    catch (DirectoryNotFoundException)
                    {
                        // Concurrent removal is benign.
                    }
                    catch
                    {
                        scanComplete = false;
                    }
                }
            }
            catch (DirectoryNotFoundException)
            {
                // Directory does not exist yet or was removed concurrently.
            }
            catch
            {
                scanComplete = false;
            }

            return result;
        }

        private void TryTouchIndexFileIfStale(string keyHash, DateTime nowUtc)
        {
            try
            {
                string path = GetIndexPath(keyHash);
                if (!File.Exists(path))
                {
                    return;
                }

                if (nowUtc - File.GetLastWriteTimeUtc(path) >= m_AccessTouchInterval)
                {
                    File.SetLastWriteTimeUtc(path, nowUtc);
                }
            }
            catch
            {
                // Best-effort LRU timestamp update.
            }
        }

        private bool TryTouchIndexFileQuietly(string keyHash, DateTime nowUtc)
        {
            try
            {
                string path = GetIndexPath(keyHash);
                if (!File.Exists(path))
                {
                    return false;
                }

                File.SetLastWriteTimeUtc(path, nowUtc);
                return true;
            }
            catch
            {
                return File.Exists(GetIndexPath(keyHash));
            }
        }

        private static bool HasExpectedFileSize(string path, long expectedLength)
        {
            try
            {
                var fi = new FileInfo(path);
                return fi.Exists && fi.Length == expectedLength;
            }
            catch
            {
                return false;
            }
        }

        private string GetBlobPath(string blobId) => Path.Combine(m_BlobsDirectory, blobId + ".bin");
        private string GetIndexPath(string keyHash) => Path.Combine(m_IndexDirectory, keyHash + ".bin");

        private static byte[] SerializeCsrBlobBytes(CsrVertexMap csr)
        {
            var offsets = csr.RowOffsets;
            var targets = csr.TargetIndices;
            int capacity = s_BlobMagic.Length + 4 + 2 + 16 + (offsets.Length + targets.Length) * 4;
            using (var ms = new MemoryStream(capacity))
            using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                bw.Write(s_BlobMagic);
                bw.Write(k_FormatVersion);
                bw.Write((byte)csr.SourceDomain);
                bw.Write((byte)csr.TargetDomain);
                bw.Write(csr.SourceCount);
                bw.Write(csr.TargetCount);

                bw.Write(offsets.Length);
                for (int i = 0; i < offsets.Length; i++)
                {
                    bw.Write(offsets[i]);
                }

                bw.Write(targets.Length);
                for (int i = 0; i < targets.Length; i++)
                {
                    bw.Write(targets[i]);
                }

                bw.Flush();
                return ms.ToArray();
            }
        }

        private static byte[] SerializeIndexBytes(VertexMappingIndexEntry entry)
        {
            byte[] payloadBytes;
            using (var ms = new MemoryStream(192))
            using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                bw.Write(s_IndexMagic);
                bw.Write(k_FormatVersion);
                bw.Write(entry.ResolutionKeyHash);
                bw.Write(entry.CsrBlobId);
                bw.Write(entry.ControlPointCount);
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

            var result = new byte[payloadBytes.Length + integrityHash.Length];
            Buffer.BlockCopy(payloadBytes, 0, result, 0, payloadBytes.Length);
            Buffer.BlockCopy(integrityHash, 0, result, payloadBytes.Length, integrityHash.Length);
            return result;
        }

        private BlobEnsureOutcome EnsureCsrBlobOnDiskLocked(string blobId, byte[] blobBytes, out long bytesDelta)
        {
            bytesDelta = 0L;
            try
            {
                string targetPath = GetBlobPath(blobId);
                long oldLength = 0L;
                bool existed = false;
                if (File.Exists(targetPath))
                {
                    var fi = new FileInfo(targetPath);
                    existed = fi.Exists;
                    oldLength = existed ? fi.Length : 0L;
                    if (existed && oldLength == blobBytes.Length &&
                        ReadCsrBlobFileRaw(targetPath, blobId, out _) == DiskReadStatus.Success)
                    {
                        return BlobEnsureOutcome.AlreadyPresentValid;
                    }
                }

                Directory.CreateDirectory(m_BlobsDirectory);
                string tempPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        fs.Write(blobBytes, 0, blobBytes.Length);
                        fs.Flush(flushToDisk: false);
                    }

                    AtomicPublish(tempPath, targetPath);
                    bytesDelta = blobBytes.Length - oldLength;
                    return existed ? BlobEnsureOutcome.ReplacedExisting : BlobEnsureOutcome.CreatedNew;
                }
                catch
                {
                    TryDeleteQuietly(tempPath);
                    return BlobEnsureOutcome.Failed;
                }
            }
            catch
            {
                return BlobEnsureOutcome.Failed;
            }
        }

        private bool TryWriteIndexToDiskLocked(string keyHash, byte[] indexBytes, out bool isNewIndex, out long bytesDelta)
        {
            isNewIndex = false;
            bytesDelta = 0L;
            try
            {
                Directory.CreateDirectory(m_IndexDirectory);
                string targetPath = GetIndexPath(keyHash);
                long oldLength = 0L;
                bool existed = File.Exists(targetPath);
                if (existed)
                {
                    try
                    {
                        oldLength = new FileInfo(targetPath).Length;
                    }
                    catch
                    {
                        existed = false;
                        oldLength = 0L;
                    }
                }

                string tempPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        fs.Write(indexBytes, 0, indexBytes.Length);
                        fs.Flush(flushToDisk: false);
                    }

                    AtomicPublish(tempPath, targetPath);
                    isNewIndex = !existed;
                    bytesDelta = indexBytes.Length - oldLength;
                    return true;
                }
                catch
                {
                    TryDeleteQuietly(tempPath);
                    return false;
                }
            }
            catch
            {
                return false;
            }
        }

        private bool TryReadCsrBlobFromDisk(string expectedBlobId, out CsrVertexMap csr)
        {
            string path = GetBlobPath(expectedBlobId);
            var status = ReadCsrBlobFileRaw(path, expectedBlobId, out csr);
            if (status == DiskReadStatus.Success)
            {
                return true;
            }

            if (status == DiskReadStatus.Corrupt)
            {
                // Revalidate under the root lock before deleting so a reader never deletes a newly replaced file.
                lock (m_RootState.Lock)
                {
                    var recheck = ReadCsrBlobFileRaw(path, expectedBlobId, out csr);
                    if (recheck == DiskReadStatus.Success)
                    {
                        return true;
                    }

                    if (recheck == DiskReadStatus.Corrupt && TryDeleteQuietly(path))
                    {
                        RecountDiskEstimatesLocked();
                        Interlocked.Increment(ref m_RootState.DiskMutationGeneration);
                    }
                }
            }

            csr = null;
            return false;
        }

        private static DiskReadStatus ReadCsrBlobFileRaw(string path, string expectedBlobId, out CsrVertexMap csr)
        {
            csr = null;
            if (!File.Exists(path))
            {
                return DiskReadStatus.NotFound;
            }

            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: false))
                {
                    if (fs.Length < s_BlobMagic.Length + 4 + 2 + 12)
                    {
                        return DiskReadStatus.Corrupt;
                    }

                    var magic = br.ReadBytes(s_BlobMagic.Length);
                    if (!BytesEqual(magic, s_BlobMagic))
                    {
                        return DiskReadStatus.Corrupt;
                    }

                    int version = br.ReadInt32();
                    if (version != k_FormatVersion)
                    {
                        return DiskReadStatus.Corrupt;
                    }

                    var srcDomain = (IndexDomain)br.ReadByte();
                    var dstDomain = (IndexDomain)br.ReadByte();
                    int srcCount = br.ReadInt32();
                    int dstCount = br.ReadInt32();
                    int offsetsLen = br.ReadInt32();
                    if (srcCount < 0 || dstCount < 0 || offsetsLen != srcCount + 1)
                    {
                        return DiskReadStatus.Corrupt;
                    }

                    // Check declared byte count against remaining stream BEFORE allocating arrays.
                    long remainingBeforeOffsets = fs.Length - fs.Position;
                    long neededOffsetBytes = (long)offsetsLen * 4L + 4L;
                    if (neededOffsetBytes < 0L || neededOffsetBytes > remainingBeforeOffsets)
                    {
                        return DiskReadStatus.Corrupt;
                    }

                    var offsets = new int[offsetsLen];
                    for (int i = 0; i < offsetsLen; i++)
                    {
                        offsets[i] = br.ReadInt32();
                    }

                    int targetsLen = br.ReadInt32();
                    if (targetsLen < 0 || targetsLen != offsets[srcCount])
                    {
                        return DiskReadStatus.Corrupt;
                    }

                    long remainingBeforeTargets = fs.Length - fs.Position;
                    long neededTargetBytes = (long)targetsLen * 4L;
                    if (neededTargetBytes < 0L || neededTargetBytes != remainingBeforeTargets)
                    {
                        return DiskReadStatus.Corrupt;
                    }

                    var targets = new int[targetsLen];
                    for (int i = 0; i < targetsLen; i++)
                    {
                        targets[i] = br.ReadInt32();
                    }

                    CsrVertexMap candidate;
                    try
                    {
                        candidate = new CsrVertexMap(srcDomain, dstDomain, srcCount, dstCount, offsets, targets, takeOwnership: true);
                    }
                    catch (ArgumentException)
                    {
                        return DiskReadStatus.Corrupt;
                    }

                    string actualBlobId = CsrVertexMapFactory.ComputeCsrBlobId(candidate);
                    if (!string.Equals(actualBlobId, expectedBlobId, StringComparison.Ordinal))
                    {
                        return DiskReadStatus.Corrupt;
                    }

                    csr = candidate;
                    return DiskReadStatus.Success;
                }
            }
            catch (FileNotFoundException)
            {
                return DiskReadStatus.NotFound;
            }
            catch (DirectoryNotFoundException)
            {
                return DiskReadStatus.NotFound;
            }
            catch (EndOfStreamException)
            {
                return DiskReadStatus.Corrupt;
            }
            catch (InvalidDataException)
            {
                return DiskReadStatus.Corrupt;
            }
            catch (IOException)
            {
                return DiskReadStatus.TransientIoError;
            }
            catch (UnauthorizedAccessException)
            {
                return DiskReadStatus.PermissionDenied;
            }
        }

        private bool TryReadIndexFromDisk(string expectedKeyHash, out VertexMappingIndexEntry entry)
        {
            string path = GetIndexPath(expectedKeyHash);
            var status = ReadIndexFileRaw(path, expectedKeyHash, out entry);
            if (status == DiskReadStatus.Success)
            {
                return true;
            }

            if (status == DiskReadStatus.Corrupt)
            {
                lock (m_RootState.Lock)
                {
                    var recheck = ReadIndexFileRaw(path, expectedKeyHash, out entry);
                    if (recheck == DiskReadStatus.Success)
                    {
                        return true;
                    }

                    if (recheck == DiskReadStatus.Corrupt && TryDeleteQuietly(path))
                    {
                        RecountDiskEstimatesLocked();
                        Interlocked.Increment(ref m_RootState.DiskMutationGeneration);
                    }
                }
            }

            entry = null;
            return false;
        }

        private static DiskReadStatus ReadIndexFileRaw(string path, string expectedKeyHash, out VertexMappingIndexEntry entry)
        {
            entry = null;
            if (!File.Exists(path))
            {
                return DiskReadStatus.NotFound;
            }

            try
            {
                byte[] allBytes;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    allBytes = new byte[fs.Length];
                    int readTotal = 0;
                    while (readTotal < allBytes.Length)
                    {
                        int n = fs.Read(allBytes, readTotal, allBytes.Length - readTotal);
                        if (n <= 0) break;
                        readTotal += n;
                    }

                    if (readTotal != allBytes.Length)
                    {
                        return DiskReadStatus.TransientIoError;
                    }
                }

                const int hashLen = 32;
                if (allBytes.Length <= s_IndexMagic.Length + 4 + hashLen)
                {
                    return DiskReadStatus.Corrupt;
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
                        return DiskReadStatus.Corrupt;
                    }
                }

                using (var ms = new MemoryStream(allBytes, 0, payloadLen, writable: false))
                using (var br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: false))
                {
                    var magic = br.ReadBytes(s_IndexMagic.Length);
                    if (!BytesEqual(magic, s_IndexMagic))
                    {
                        return DiskReadStatus.Corrupt;
                    }

                    int version = br.ReadInt32();
                    if (version != k_FormatVersion)
                    {
                        return DiskReadStatus.Corrupt;
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
                        return DiskReadStatus.Corrupt;
                    }

                    entry = new VertexMappingIndexEntry(keyHash, blobId, cpCount, vtxCount, scale, bakeAxis);
                    return DiskReadStatus.Success;
                }
            }
            catch (FileNotFoundException)
            {
                return DiskReadStatus.NotFound;
            }
            catch (DirectoryNotFoundException)
            {
                return DiskReadStatus.NotFound;
            }
            catch (EndOfStreamException)
            {
                return DiskReadStatus.Corrupt;
            }
            catch (FormatException)
            {
                return DiskReadStatus.Corrupt;
            }
            catch (InvalidDataException)
            {
                return DiskReadStatus.Corrupt;
            }
            catch (IOException)
            {
                return DiskReadStatus.TransientIoError;
            }
            catch (UnauthorizedAccessException)
            {
                return DiskReadStatus.PermissionDenied;
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

        private static void DeleteAllFilesQuietly(string directory)
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            try
            {
                foreach (var path in Directory.EnumerateFiles(directory))
                {
                    TryDeleteQuietly(path);
                }
            }
            catch
            {
                // Ignore directory errors.
            }
        }

        private static bool TryDeleteQuietly(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                File.Delete(path);
                return !File.Exists(path);
            }
            catch
            {
                return false;
            }
        }

        private static bool IndexEntriesEqual(VertexMappingIndexEntry a, VertexMappingIndexEntry b)
        {
            if (a == null || b == null) return false;
            return string.Equals(a.ResolutionKeyHash, b.ResolutionKeyHash, StringComparison.Ordinal) &&
                   string.Equals(a.CsrBlobId, b.CsrBlobId, StringComparison.Ordinal) &&
                   a.ControlPointCount == b.ControlPointCount &&
                   a.UnityVertexCount == b.UnityVertexCount &&
                   BitConverter.SingleToInt32Bits(a.MatchedScale) == BitConverter.SingleToInt32Bits(b.MatchedScale) &&
                   a.MatchedBakeAxisConversion == b.MatchedBakeAxisConversion;
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
