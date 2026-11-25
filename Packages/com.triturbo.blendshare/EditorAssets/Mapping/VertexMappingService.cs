using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Fbx.Ufbx;
using Triturbo.BlendShare.Fbx.Unity;
using Triturbo.BlendShare.Mapping;
using Triturbo.BlendShare.Model.Documents;
using Triturbo.BlendShare.Model.Serialization;
using Triturbo.BlendShare.Primitives;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.EditorAssets.Mapping
{
    public enum VertexMappingSourceKind : byte
    {
        None = 0,
        EmbeddedMappingFastPath = 1,
        EmbeddedMappingCoincidentFallback = 2,
        FbxInverseMapComputed = 3,
        FbxInverseMapCached = 4,
        ComposedReferenceMeshComputed = 5,
        ComposedReferenceMeshCached = 6
    }

    public enum VertexMappingCacheMode : byte
    {
        /// <summary>Do not read from or write to any vertex mapping cache.</summary>
        Disabled = 0,

        /// <summary>
        /// Read existing memory/disk cache if present, but never store newly computed entries or promote disk hits
        /// into memory (best-effort access timestamp updates and corrupt-file self-healing may still touch disk).
        /// Used by NDMF Build.
        /// </summary>
        ReadOnly = 1,

        /// <summary>Read from and write to both memory and disk cache.</summary>
        ReadWrite = 2,

        /// <summary>
        /// Read existing memory/disk cache; persist newly computed entries (and eligible memory hits) to disk only when
        /// the resolved mesh snapshot(s) came directly from persisted Unity assets, otherwise cache in memory only.
        /// Used by NDMF Preview and authoring workflows.
        /// </summary>
        PersistAssetsOnly = 3
    }

    /// <summary>
    /// Immutable verified vertex mapping + per-use coordinate space contract consumed by Unity appliers.
    /// </summary>
    public sealed class ResolvedVertexMapping
    {
        public CsrVertexMap VertexMap { get; }
        public MappingSpaceContract SpaceContract { get; }
        public VertexMappingSourceKind SourceKind { get; }
        public string CsrBlobId { get; }
        public bool CacheHit { get; }
        public string Diagnostic { get; }

        public ResolvedVertexMapping(
            CsrVertexMap vertexMap,
            MappingSpaceContract spaceContract,
            VertexMappingSourceKind sourceKind,
            string csrBlobId,
            bool cacheHit,
            string diagnostic = null)
        {
            VertexMap = vertexMap ?? throw new ArgumentNullException(nameof(vertexMap));
            SpaceContract = spaceContract ?? throw new ArgumentNullException(nameof(spaceContract));
            SourceKind = sourceKind;
            CsrBlobId = csrBlobId ?? string.Empty;
            CacheHit = cacheHit;
            Diagnostic = diagnostic ?? string.Empty;
        }
    }

    public sealed class VertexMappingResolveRequest
    {
        public MeshDocument MeshHeader { get; set; }
        public PatchSpaceProvenance SpaceProvenance { get; set; }
        public Mesh TargetUnityMesh { get; set; }
        public UnityMeshSnapshot TargetSnapshotOverride { get; set; }
        public Mesh ReferenceUnityMesh { get; set; }
        public UnityMeshSnapshot ReferenceSnapshotOverride { get; set; }
        public GameObject TargetFbxAsset { get; set; }
        public IFbxScene TargetFbxSceneOverride { get; set; }
        public string TargetFbxNodePathOverride { get; set; }
        public FbxUnityImportSettings? TargetImportSettingsOverride { get; set; }
        public ILazyPayloadSource PayloadSource { get; set; }
        public VertexMapComposePolicy ComposePolicy { get; set; } = VertexMapComposePolicy.RejectConflictingGroups;
        public bool PreferEmbeddedMapping { get; set; }
        public VertexMappingCacheMode CacheMode { get; set; } = VertexMappingCacheMode.PersistAssetsOnly;
    }

    public sealed class VertexMappingResolveResult
    {
        public bool Succeeded { get; }
        public ResolvedVertexMapping Mapping { get; }
        public string FailureCode { get; }
        public string Diagnostic { get; }
        public IReadOnlyList<int> ConflictingTargetVertices { get; }

        private VertexMappingResolveResult(
            bool succeeded,
            ResolvedVertexMapping mapping,
            string failureCode,
            string diagnostic,
            IReadOnlyList<int> conflictingTargetVertices = null)
        {
            Succeeded = succeeded;
            Mapping = mapping;
            FailureCode = failureCode ?? string.Empty;
            Diagnostic = diagnostic ?? string.Empty;
            ConflictingTargetVertices = conflictingTargetVertices ?? Array.Empty<int>();
        }

        public static VertexMappingResolveResult Success(ResolvedVertexMapping mapping) =>
            new VertexMappingResolveResult(true, mapping, string.Empty, mapping.Diagnostic);

        public static VertexMappingResolveResult Fail(
            string code,
            string diagnostic,
            IReadOnlyList<int> conflictingTargetVertices = null) =>
            new VertexMappingResolveResult(false, null, code, diagnostic, conflictingTargetVertices);
    }

    /// <summary>
    /// Single unified gateway for resolving <c>UnityVertex -> FBX ControlPoint</c> mappings and
    /// per-use <c>Stored -> UnityMeshLocal</c> space contracts.
    /// Supports non-readable Unity meshes, multi-entry embedded <see cref="VertexMappingEntry"/> verification without FBX,
    /// content-addressed in-memory FBX metadata caching, and bounded LRU cached FBX inverse / reference-mesh composed mapping.
    /// </summary>
    public sealed class VertexMappingService
    {
        private const string k_ResolutionKeyDomain = "BlendShare.VertexMappingResolutionKey.v2";
        private const string k_ComposedKeyDomain = "BlendShare.ComposedMappingKey.v2";
        private const int k_MaxCachedFbxFiles = 64;
        private const int k_MaxCachedNodesPerFbxFile = 256;

        public static readonly VertexMappingService Shared = new VertexMappingService(new VertexMappingCacheStore());

        private sealed class CachedFbxNodeEntry
        {
            public bool MeshFound;
            public FbxTopologyDigest FullTopology;
            public Dictionary<int, FbxTopologyDigest> PrefixTopologies;
        }

        private sealed class CachedFbxFileMetadata
        {
            public FbxSceneMetadata SceneMetadata;
            public long LastAccessSeq;
            public readonly Dictionary<string, CachedFbxNodeEntry> Nodes =
                new Dictionary<string, CachedFbxNodeEntry>(StringComparer.Ordinal);
        }

        private readonly VertexMappingCacheStore m_CacheStore;
        private readonly object m_FbxMetadataLock = new object();
        private readonly Dictionary<string, CachedFbxFileMetadata> m_FbxMetadataByFingerprint =
            new Dictionary<string, CachedFbxFileMetadata>(StringComparer.Ordinal);
        private long m_FbxMetadataAccessSeq;

        public VertexMappingCacheStore CacheStore => m_CacheStore;

        public VertexMappingService(VertexMappingCacheStore cacheStore)
        {
            m_CacheStore = cacheStore ?? throw new ArgumentNullException(nameof(cacheStore));
            m_CacheStore.MemoryCacheCleared += ClearFbxMetadataCache;
        }

        internal void ClearFbxMetadataCache()
        {
            lock (m_FbxMetadataLock)
            {
                m_FbxMetadataByFingerprint.Clear();
            }
        }

        public VertexMappingResolveResult Resolve(VertexMappingResolveRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (request.MeshHeader == null)
            {
                return VertexMappingResolveResult.Fail("MAP_NO_MESH_HEADER", "MeshDocument header is required.");
            }

            var stored = request.SpaceProvenance;
            if (stored != null && !stored.IsValid)
            {
                return VertexMappingResolveResult.Fail("MAP_INVALID_STORED_SPACE", "Invalid patch coordinate axes or unit scale.");
            }

            bool targetFromPersistedAsset;
            UnityMeshSnapshot snapshot = request.TargetSnapshotOverride;
            if (snapshot != null)
            {
                // Caller-supplied snapshot overrides always have override provenance (never persisted to disk under PersistAssetsOnly).
                targetFromPersistedAsset = false;
            }
            else
            {
                if (request.TargetUnityMesh == null)
                {
                    return VertexMappingResolveResult.Fail("MAP_NO_TARGET_MESH", "Target Unity Mesh or UnityMeshSnapshot is required.");
                }

                if (!UnityMeshReaderUtility.TryReadMeshSnapshot(request.TargetUnityMesh, out snapshot))
                {
                    return VertexMappingResolveResult.Fail(
                        "MAP_READ_MESH_FAILED",
                        $"Failed to read CPU geometry from Unity mesh '{request.TargetUnityMesh.name}'.");
                }

                targetFromPersistedAsset = AssetDatabase.Contains(request.TargetUnityMesh);
            }

            return ResolveCore(request, snapshot, targetFromPersistedAsset);
        }

        private VertexMappingResolveResult ResolveCore(
            VertexMappingResolveRequest request,
            UnityMeshSnapshot snapshot,
            bool targetFromPersistedAsset)
        {
            bool hasEmbeddedCandidates = request.MeshHeader.Mappings != null && request.MeshHeader.Mappings.Count > 0;
            bool hasTargetFbx = request.TargetFbxSceneOverride != null || request.TargetFbxAsset != null;
            bool hasReferenceMesh = request.ReferenceSnapshotOverride != null || request.ReferenceUnityMesh != null;

            VertexMappingResolveResult embeddedFailure = null;
            if (hasEmbeddedCandidates && (request.PreferEmbeddedMapping || !hasTargetFbx))
            {
                var embeddedRes = TryResolveFromEmbeddedMappings(request, snapshot);
                if (embeddedRes.Succeeded)
                {
                    return embeddedRes;
                }

                embeddedFailure = embeddedRes;
                if (!hasTargetFbx && !hasReferenceMesh)
                {
                    return embeddedRes;
                }
            }

            VertexMappingResolveResult directFbxFailure = null;
            if (hasTargetFbx)
            {
                var fbxRes = TryResolveFromTargetFbx(request, snapshot, targetFromPersistedAsset);
                if (fbxRes.Succeeded)
                {
                    return fbxRes;
                }

                directFbxFailure = fbxRes;

                // Fallback to embedded mappings if we didn't already try them.
                if (hasEmbeddedCandidates && embeddedFailure == null)
                {
                    var embeddedFallback = TryResolveFromEmbeddedMappings(request, snapshot);
                    if (embeddedFallback.Succeeded)
                    {
                        return embeddedFallback;
                    }

                    embeddedFailure = embeddedFallback;
                }
            }

            // Reference-mesh composed mapping: resolve Reference -> ControlPoint, then compose with Target -> Reference.
            if (hasReferenceMesh)
            {
                var composedRes = TryResolveViaReferenceMesh(request, snapshot, targetFromPersistedAsset);
                if (composedRes.Succeeded)
                {
                    return composedRes;
                }

                return CombineFailureDiagnostics(composedRes, directFbxFailure, embeddedFailure);
            }

            if (directFbxFailure != null)
            {
                return CombineFailureDiagnostics(directFbxFailure, null, embeddedFailure);
            }

            if (embeddedFailure != null)
            {
                return embeddedFailure;
            }

            return VertexMappingResolveResult.Fail(
                "MAP_NO_SOURCE",
                "Neither a target FBX, a reference Unity mesh, nor an embedded mapping payload is available to map this mesh.");
        }

        private static VertexMappingResolveResult CombineFailureDiagnostics(
            VertexMappingResolveResult primary,
            VertexMappingResolveResult fbxFailure,
            VertexMappingResolveResult embeddedFailure)
        {
            if (fbxFailure == null && embeddedFailure == null)
            {
                return primary;
            }

            var sb = new StringBuilder(primary.Diagnostic);
            if (fbxFailure != null)
            {
                sb.Append($" [Direct FBX failed ({fbxFailure.FailureCode}): {fbxFailure.Diagnostic}]");
            }

            if (embeddedFailure != null)
            {
                sb.Append($" [Embedded mapping failed ({embeddedFailure.FailureCode}): {embeddedFailure.Diagnostic}]");
            }

            return VertexMappingResolveResult.Fail(primary.FailureCode, sb.ToString(), primary.ConflictingTargetVertices);
        }

        private static void ResolveCacheFlags(
            VertexMappingCacheMode cacheMode,
            bool snapshotFromPersistedAsset,
            out bool canReadCache,
            out bool canWriteMemory,
            out bool canWriteDisk)
        {
            switch (cacheMode)
            {
                case VertexMappingCacheMode.Disabled:
                    canReadCache = false;
                    canWriteMemory = false;
                    canWriteDisk = false;
                    return;
                case VertexMappingCacheMode.ReadOnly:
                    canReadCache = true;
                    canWriteMemory = false;
                    canWriteDisk = false;
                    return;
                case VertexMappingCacheMode.ReadWrite:
                    canReadCache = true;
                    canWriteMemory = true;
                    canWriteDisk = true;
                    return;
                case VertexMappingCacheMode.PersistAssetsOnly:
                default:
                    canReadCache = true;
                    canWriteMemory = true;
                    canWriteDisk = snapshotFromPersistedAsset;
                    return;
            }
        }

        private VertexMappingResolveResult TryResolveViaReferenceMesh(
            VertexMappingResolveRequest request,
            UnityMeshSnapshot targetSnapshot,
            bool targetFromPersistedAsset)
        {
            bool refFromPersistedAsset;
            UnityMeshSnapshot refSnapshot = request.ReferenceSnapshotOverride;
            if (refSnapshot != null)
            {
                refFromPersistedAsset = false;
            }
            else
            {
                if (request.ReferenceUnityMesh == null ||
                    !UnityMeshReaderUtility.TryReadMeshSnapshot(request.ReferenceUnityMesh, out refSnapshot))
                {
                    return VertexMappingResolveResult.Fail(
                        "MAP_REF_MESH_READ_FAILED",
                        "Failed to read CPU geometry from reference Unity mesh.");
                }

                refFromPersistedAsset = AssetDatabase.Contains(request.ReferenceUnityMesh);
            }

            // Resolve Reference Unity Mesh -> ControlPoints (without recursing into ReferenceMesh again),
            // carrying refSnapshot's real asset provenance explicitly.
            var refRes = ResolveCore(
                new VertexMappingResolveRequest
                {
                    MeshHeader = request.MeshHeader,
                    SpaceProvenance = request.SpaceProvenance,
                    TargetUnityMesh = request.ReferenceUnityMesh,
                    TargetFbxAsset = request.TargetFbxAsset,
                    TargetFbxSceneOverride = request.TargetFbxSceneOverride,
                    TargetFbxNodePathOverride = request.TargetFbxNodePathOverride,
                    TargetImportSettingsOverride = request.TargetImportSettingsOverride,
                    PayloadSource = request.PayloadSource,
                    PreferEmbeddedMapping = request.PreferEmbeddedMapping,
                    CacheMode = request.CacheMode
                },
                refSnapshot,
                refFromPersistedAsset);

            if (!refRes.Succeeded || refRes.Mapping == null)
            {
                return VertexMappingResolveResult.Fail(
                    "MAP_REF_RESOLUTION_FAILED",
                    $"Could not map reference mesh to patch topology: {refRes.FailureCode} ({refRes.Diagnostic})");
            }

            ResolveCacheFlags(
                request.CacheMode,
                targetFromPersistedAsset && refFromPersistedAsset,
                out bool canReadCache,
                out bool canWriteMemory,
                out bool canWriteDisk);

            var targetDig = UnityMeshGeometryDigests.Compute(targetSnapshot);
            var refDig = UnityMeshGeometryDigests.Compute(refSnapshot);
            string composedKey = ComputeComposedResolutionKeyHash(
                refRes.Mapping.CsrBlobId,
                refDig.CombinedGeometryKey,
                targetDig.CombinedGeometryKey,
                request.ComposePolicy,
                request.SpaceProvenance);

            if (canReadCache &&
                m_CacheStore.TryLoadResolution(
                    composedKey,
                    out var cachedIdx,
                    out var cachedCsr,
                    promoteToMemory: canWriteMemory,
                    persistToDisk: canWriteDisk))
            {
                return VertexMappingResolveResult.Success(new ResolvedVertexMapping(
                    cachedCsr,
                    refRes.Mapping.SpaceContract,
                    VertexMappingSourceKind.ComposedReferenceMeshCached,
                    cachedIdx.CsrBlobId,
                    cacheHit: true));
            }

            var correspondence = UnityMeshToMeshMapper.Map(refSnapshot, targetSnapshot);
            if (!correspondence.Succeeded)
            {
                return VertexMappingResolveResult.Fail(
                    "MAP_COMPOSED_CORRESPONDENCE_FAILED",
                    $"Reference-to-target Unity mesh mapping failed: {correspondence.Diagnostic}");
            }

            var composed = VertexMapComposer.Compose(
                refRes.Mapping.VertexMap,
                correspondence,
                request.ComposePolicy);

            if (!composed.Succeeded)
            {
                if (composed.ConflictingMergedTargetCount > 0)
                {
                    return VertexMappingResolveResult.Fail(
                        "MAP_COMPOSED_MERGE_CONFLICT",
                        $"Reference mesh composition rejected {composed.ConflictingMergedTargetCount} target vertex(es) with conflicting merged FBX control-point groups ({composed.Diagnostic}).",
                        composed.ConflictingTargetVertices);
                }

                return VertexMappingResolveResult.Fail(
                    "MAP_COMPOSED_UNRESOLVED",
                    composed.Diagnostic,
                    composed.ConflictingTargetVertices);
            }

            var composedCsr = CsrVertexMapFactory.FromJaggedGroups(
                composed.TargetFbxGroups,
                refRes.Mapping.VertexMap.TargetCount,
                IndexDomain.UnityVertex,
                IndexDomain.ControlPoint);

            string blobId = (!correspondence.IsBlendShapeSensitive && (canWriteMemory || canWriteDisk))
                ? m_CacheStore.StoreResolution(
                    composedKey,
                    composedCsr,
                    matchedScale: 1f,
                    matchedBakeAxisConversion: false,
                    writeToDisk: canWriteDisk,
                    writeToMemory: canWriteMemory)
                : CsrVertexMapFactory.ComputeCsrBlobId(composedCsr);

            return VertexMappingResolveResult.Success(new ResolvedVertexMapping(
                composedCsr,
                refRes.Mapping.SpaceContract,
                VertexMappingSourceKind.ComposedReferenceMeshComputed,
                blobId,
                cacheHit: false,
                diagnostic: composed.ConflictingMergedTargetCount > 0
                    ? $"Composed with {composed.ConflictingMergedTargetCount} merged union vertex(es) under AllowMergedUnion policy."
                    : string.Empty));
        }

        private static string ComputeComposedResolutionKeyHash(
            string referenceCsrBlobId,
            string referenceGeometryKey,
            string targetGeometryKey,
            VertexMapComposePolicy policy,
            PatchSpaceProvenance stored)
        {
            using (var sha = SHA256.Create())
            using (var cs = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write))
            using (var bw = new BinaryWriter(cs, Encoding.UTF8, leaveOpen: true))
            {
                bw.Write(k_ComposedKeyDomain);
                bw.Write(referenceCsrBlobId ?? string.Empty);
                bw.Write(referenceGeometryKey ?? string.Empty);
                bw.Write(targetGeometryKey ?? string.Empty);
                bw.Write((int)policy);
                VertexMappingCacheStore.WriteSpaceKey(bw, stored);
                bw.Flush();
                cs.FlushFinalBlock();
                return ToLowerHex(sha.Hash);
            }
        }

        private VertexMappingResolveResult TryResolveFromEmbeddedMappings(
            VertexMappingResolveRequest request,
            UnityMeshSnapshot targetSnapshot)
        {
            var mappings = request.MeshHeader.Mappings;
            if (mappings == null || mappings.Count == 0)
            {
                return VertexMappingResolveResult.Fail("MAP_EMBEDDED_ABSENT", "Mesh has no embedded mapping payloads.");
            }

            int nonNullEntryCount = 0;
            int validPayloadEntryCount = 0;
            VertexMappingEntry singleNonNull = null;
            for (int i = 0; i < mappings.Count; i++)
            {
                var e = mappings[i];
                if (e != null)
                {
                    nonNullEntryCount++;
                    singleNonNull ??= e;
                    if (e.HasPayload)
                    {
                        validPayloadEntryCount++;
                    }
                }
            }

            if (nonNullEntryCount == 0)
            {
                return VertexMappingResolveResult.Fail("MAP_EMBEDDED_ABSENT", "Mesh has no embedded mapping payloads.");
            }

            var payloadSource = request.PayloadSource ?? BlendShareSourceRepository.Shared.PayloadStore;
            int expectedCpCount = request.MeshHeader.Topology != null && request.MeshHeader.Topology.IsValid
                ? request.MeshHeader.Topology.ControlPointCount
                : -1;
            int targetVertexCount = targetSnapshot.Vertices.Length;
            var targetDigests = UnityMeshGeometryDigests.Compute(targetSnapshot);

            // Pass 1: Stage-1 exact UV0 header matches (zero payload I/O for non-matching headers).
            var pass1Candidates = new List<VertexMappingEntry>();
            for (int i = 0; i < mappings.Count; i++)
            {
                var entry = mappings[i];
                if (VertexMappingCodec.MatchesHeaderExact(entry, targetDigests, targetVertexCount, expectedCpCount))
                {
                    pass1Candidates.Add(entry);
                }
            }

            if (pass1Candidates.Count > 0)
            {
                // Once exact-header candidates exist, their integrity or ambiguity failure MUST NOT fall through to Pass 2.
                return EvaluateEmbeddedPass(
                    pass1Candidates,
                    targetSnapshot,
                    expectedCpCount,
                    request.SpaceProvenance,
                    payloadSource,
                    targetDigests,
                    allowCoincidentConflictNonMatch: false);
            }

            // Pass 2: Stage-2 geometry header matches with UV0/SubMesh coincident-class fallback.
            var pass2Candidates = new List<VertexMappingEntry>();
            for (int i = 0; i < mappings.Count; i++)
            {
                var entry = mappings[i];
                if (VertexMappingCodec.MatchesHeaderGeometry(entry, targetDigests, targetVertexCount, expectedCpCount))
                {
                    pass2Candidates.Add(entry);
                }
            }

            if (pass2Candidates.Count > 0)
            {
                return EvaluateEmbeddedPass(
                    pass2Candidates,
                    targetSnapshot,
                    expectedCpCount,
                    request.SpaceProvenance,
                    payloadSource,
                    targetDigests,
                    allowCoincidentConflictNonMatch: true);
            }

            if (validPayloadEntryCount == 0)
            {
                return VertexMappingResolveResult.Fail("MAP_EMBEDDED_ABSENT", "Mesh has no embedded mapping payloads.");
            }

            if (nonNullEntryCount == 1)
            {
                var status = VertexMappingCodec.DiagnoseHeaderMismatch(singleNonNull, targetDigests, targetVertexCount, expectedCpCount, out string reason);
                return VertexMappingResolveResult.Fail($"MAP_EMBEDDED_{status}", reason);
            }

            return VertexMappingResolveResult.Fail(
                "MAP_EMBEDDED_NO_MATCH",
                $"None of the {validPayloadEntryCount} embedded mapping(s) matched the target Unity mesh.");
        }

        private static VertexMappingResolveResult EvaluateEmbeddedPass(
            List<VertexMappingEntry> candidates,
            UnityMeshSnapshot targetSnapshot,
            int expectedCpCount,
            PatchSpaceProvenance spaceProvenance,
            ILazyPayloadSource payloadSource,
            in UnityMeshGeometryDigest targetDigests,
            bool allowCoincidentConflictNonMatch)
        {
            var verified = new List<ResolvedVertexMapping>(candidates.Count);
            string bestIntegrityCode = null;
            int bestIntegrityRank = int.MaxValue;
            var integrityDiagnostics = new List<string>();
            var conflictDiagnostics = new List<string>();

            for (int i = 0; i < candidates.Count; i++)
            {
                var entry = candidates[i];
                if (!entry.HasPayload)
                {
                    RecordIntegrityFailure(
                        "MAP_EMBEDDED_MissingPayload",
                        "Embedded mapping matched header digests but has no payload.",
                        ref bestIntegrityCode,
                        ref bestIntegrityRank,
                        integrityDiagnostics);
                    continue;
                }

                VertexMappingPayload payload;
                try
                {
                    using (var lease = entry.Payload.Acquire(payloadSource))
                    {
                        payload = lease.Value;
                    }
                }
                catch (Exception ex)
                {
                    RecordIntegrityFailure(
                        "MAP_EMBEDDED_LOAD_FAILED",
                        $"Failed to acquire embedded mapping payload: {ex.Message}",
                        ref bestIntegrityCode,
                        ref bestIntegrityRank,
                        integrityDiagnostics);
                    continue;
                }

                var verify = VertexMappingCodec.Verify(entry, payload, targetSnapshot, expectedCpCount, spaceProvenance, targetDigests);
                if (!verify.Succeeded)
                {
                    if (allowCoincidentConflictNonMatch && verify.Status == VertexMappingVerificationStatus.CoincidentClassConflict)
                    {
                        conflictDiagnostics.Add(verify.Diagnostic);
                        continue;
                    }

                    RecordIntegrityFailure(
                        $"MAP_EMBEDDED_{verify.Status}",
                        verify.Diagnostic,
                        ref bestIntegrityCode,
                        ref bestIntegrityRank,
                        integrityDiagnostics);
                    continue;
                }

                var sourceKind = verify.Status == VertexMappingVerificationStatus.ExactFastPath
                    ? VertexMappingSourceKind.EmbeddedMappingFastPath
                    : VertexMappingSourceKind.EmbeddedMappingCoincidentFallback;
                string blobId = CsrVertexMapFactory.ComputeCsrBlobId(verify.VertexMap);
                verified.Add(new ResolvedVertexMapping(
                    verify.VertexMap,
                    verify.SpaceContract,
                    sourceKind,
                    blobId,
                    cacheHit: false,
                    diagnostic: verify.Diagnostic));
            }

            // Any integrity failure among header-matching candidates in this pass fails closed deterministically
            // (regardless of candidate list order).
            if (bestIntegrityCode != null)
            {
                integrityDiagnostics.Sort(StringComparer.Ordinal);
                return VertexMappingResolveResult.Fail(bestIntegrityCode, string.Join(" | ", integrityDiagnostics));
            }

            if (verified.Count == 0)
            {
                conflictDiagnostics.Sort(StringComparer.Ordinal);
                return VertexMappingResolveResult.Fail(
                    "MAP_EMBEDDED_CoincidentClassConflict",
                    conflictDiagnostics.Count > 0
                        ? string.Join(" | ", conflictDiagnostics)
                        : "Embedded mapping coincident-vertex equivalence check failed.");
            }

            var canonical = verified[0];
            string canonicalTargetIdentity = canonical.SpaceContract.TargetIdentity ?? string.Empty;
            for (int i = 1; i < verified.Count; i++)
            {
                var other = verified[i];
                if (!AreSemanticallyEquivalent(canonical, other))
                {
                    return VertexMappingResolveResult.Fail(
                        "MAP_EMBEDDED_AMBIGUOUS",
                        $"Multiple embedded mappings ({verified.Count}) matched the target Unity mesh with conflicting CSR or coordinate contracts.");
                }

                string otherIdentity = other.SpaceContract.TargetIdentity ?? string.Empty;
                if (string.CompareOrdinal(otherIdentity, canonicalTargetIdentity) < 0)
                {
                    canonicalTargetIdentity = otherIdentity;
                }
            }

            if (!string.Equals(canonical.SpaceContract.TargetIdentity ?? string.Empty, canonicalTargetIdentity, StringComparison.Ordinal))
            {
                var normalizedSpace = new MappingSpaceContract
                {
                    TargetIdentity = canonicalTargetIdentity,
                    StoredToUnityMeshLocal = canonical.SpaceContract.StoredToUnityMeshLocal,
                    StoredSpace = canonical.SpaceContract.StoredSpace != null
                        ? PatchSpaceProvenance.FromMetadata(canonical.SpaceContract.StoredSpace.Axes, canonical.SpaceContract.StoredSpace.MetersPerUnit)
                        : null
                };
                canonical = new ResolvedVertexMapping(
                    canonical.VertexMap,
                    normalizedSpace,
                    canonical.SourceKind,
                    canonical.CsrBlobId,
                    cacheHit: false,
                    diagnostic: canonical.Diagnostic);
            }

            return VertexMappingResolveResult.Success(canonical);
        }

        private static void RecordIntegrityFailure(
            string code,
            string diagnostic,
            ref string bestCode,
            ref int bestRank,
            List<string> diagnostics)
        {
            int rank = GetFailurePrecedenceRank(code);
            if (rank < bestRank || (rank == bestRank && string.CompareOrdinal(code, bestCode) < 0))
            {
                bestRank = rank;
                bestCode = code;
            }

            if (!string.IsNullOrEmpty(diagnostic))
            {
                diagnostics.Add(diagnostic);
            }
        }

        private static int GetFailurePrecedenceRank(string code)
        {
            return code switch
            {
                "MAP_EMBEDDED_LOAD_FAILED" => 0,
                "MAP_EMBEDDED_MissingPayload" => 1,
                "MAP_EMBEDDED_InvalidCsr" => 2,
                "MAP_EMBEDDED_InvalidSpaceContract" => 3,
                "MAP_EMBEDDED_CoincidentClassConflict" => 4,
                _ => 5
            };
        }

        private static bool AreSemanticallyEquivalent(ResolvedVertexMapping a, ResolvedVertexMapping b)
        {
            if (!string.Equals(a.CsrBlobId, b.CsrBlobId, StringComparison.Ordinal))
            {
                return false;
            }

            if (!MatrixEqualsBitwise(a.SpaceContract.StoredToUnityMeshLocal, b.SpaceContract.StoredToUnityMeshLocal))
            {
                return false;
            }

            var spaceA = a.SpaceContract.StoredSpace;
            var spaceB = b.SpaceContract.StoredSpace;
            if (spaceA == null && spaceB == null) return true;
            if (spaceA == null || spaceB == null) return false;
            return spaceA.MatchesSpace(spaceB);
        }

        private static bool MatrixEqualsBitwise(FbxMatrix4x4 a, FbxMatrix4x4 b)
        {
            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++)
                {
                    if (!DoubleBitsEqual(a[r, c], b[r, c]))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool DoubleBitsEqual(double a, double b)
        {
            long ba = BitConverter.DoubleToInt64Bits(a == 0d ? 0d : a);
            long bb = BitConverter.DoubleToInt64Bits(b == 0d ? 0d : b);
            return ba == bb;
        }

        private VertexMappingResolveResult TryResolveFromTargetFbx(
            VertexMappingResolveRequest request,
            UnityMeshSnapshot targetSnapshot,
            bool targetFromPersistedAsset)
        {
            string nodePath = !string.IsNullOrWhiteSpace(request.TargetFbxNodePathOverride)
                ? FbxNameUtility.NormalizePath(request.TargetFbxNodePathOverride)
                : FbxNameUtility.NormalizePath(request.MeshHeader.NodePath);

            if (string.IsNullOrEmpty(nodePath))
            {
                return VertexMappingResolveResult.Fail("MAP_FBX_EMPTY_NODE_PATH", "Target FBX node path is empty.");
            }

            ResolveCacheFlags(request.CacheMode, targetFromPersistedAsset, out bool canReadCache, out bool canWriteMemory, out bool canWriteDisk);

            var targetGeometry = UnityMeshGeometryDigests.Compute(targetSnapshot);
            var importSettings = request.TargetImportSettingsOverride ??
                                 (request.TargetFbxAsset != null
                                     ? FbxUnityAssetReader.GetImportSettings(request.TargetFbxAsset)
                                     : FbxUnityImportSettings.Default);
            var expectedTopo = request.MeshHeader.Topology;
            bool hasValidExpectedTopo = expectedTopo != null && expectedTopo.IsValid;

            // Content-addressed physical FBX path: read exact bytes once, compute content fingerprint, and check
            // in-memory FBX metadata cache so warm resolution cache hits skip native ufbx parsing completely.
            byte[] physicalFileBytes = null;
            string physicalAssetPath = null;
            string fbxFileIdentity = null;
            bool resolutionCacheChecked = false;

            if (request.TargetFbxSceneOverride == null && request.TargetFbxAsset != null)
            {
                physicalAssetPath = AssetDatabase.GetAssetPath(request.TargetFbxAsset);
                string physicalPath = BlendSharePathUtility.ResolvePhysicalPath(physicalAssetPath);
                if (!string.IsNullOrEmpty(physicalPath) && File.Exists(physicalPath))
                {
                    try
                    {
                        physicalFileBytes = File.ReadAllBytes(physicalPath);
                        fbxFileIdentity = ComputeBytesFingerprint(physicalFileBytes);
                    }
                    catch (Exception ex)
                    {
                        return VertexMappingResolveResult.Fail("MAP_FBX_LOAD_FAILED", $"Failed to read target FBX file '{physicalPath}': {ex.Message}");
                    }

                    if (canReadCache &&
                        TryGetCachedFbxMetadata(
                            fbxFileIdentity,
                            nodePath,
                            hasValidExpectedTopo ? expectedTopo.ControlPointCount : -1,
                            out var cachedSceneMeta,
                            out var cachedNodeFound,
                            out var cachedFullTopo,
                            out var cachedPrefixTopo))
                    {
                        if (!cachedNodeFound)
                        {
                            return VertexMappingResolveResult.Fail(
                                "MAP_FBX_MESH_NOT_FOUND",
                                $"Target FBX mesh not found at node path '{nodePath}'.");
                        }

                        var topoFailure = ValidateCachedTopology(nodePath, cachedFullTopo, expectedTopo, cachedPrefixTopo);
                        if (topoFailure != null)
                        {
                            return topoFailure;
                        }

                        int cachedPatchCpCount = hasValidExpectedTopo ? expectedTopo.ControlPointCount : cachedFullTopo.ControlPointCount;
                        string cachedResKey = ComputeResolutionKeyHash(
                            hasValidExpectedTopo ? expectedTopo.Hash : cachedFullTopo.Hash,
                            cachedPatchCpCount,
                            targetGeometry.CombinedGeometryKey,
                            fbxFileIdentity,
                            nodePath,
                            importSettings,
                            request.SpaceProvenance);
                        resolutionCacheChecked = true;

                        if (m_CacheStore.TryLoadResolution(
                                cachedResKey,
                                out var warmIdx,
                                out var warmCsr,
                                promoteToMemory: canWriteMemory,
                                persistToDisk: canWriteDisk))
                        {
                            try
                            {
                                var warmSpace = VertexMappingCodec.BuildSpaceContract(
                                    request.SpaceProvenance,
                                    cachedSceneMeta,
                                    warmIdx.MatchedScale,
                                    warmIdx.MatchedBakeAxisConversion,
                                    nodePath);
                                return VertexMappingResolveResult.Success(new ResolvedVertexMapping(
                                    warmCsr,
                                    warmSpace,
                                    VertexMappingSourceKind.FbxInverseMapCached,
                                    warmIdx.CsrBlobId,
                                    cacheHit: true));
                            }
                            catch (ArgumentException ex)
                            {
                                return VertexMappingResolveResult.Fail("MAP_FBX_INVALID_CONTRACT", ex.Message);
                            }
                        }
                    }
                }
            }

            IFbxScene ownedScene = null;
            try
            {
                IFbxScene scene = request.TargetFbxSceneOverride;
                if (scene == null)
                {
                    FbxReadResult<UfbxScene> read = physicalFileBytes != null
                        ? UfbxScene.TryLoadFromMemory(physicalFileBytes, UfbxLoadOptions.Default, physicalAssetPath)
                        : FbxUnityAssetReader.ReadScene(request.TargetFbxAsset, UfbxLoadOptions.Default);
                    if (!read.Success || read.Value == null)
                    {
                        return VertexMappingResolveResult.Fail("MAP_FBX_LOAD_FAILED", $"Failed to load target FBX: {read.Message}");
                    }

                    ownedScene = read.Value;
                    scene = ownedScene;
                }

                var fbxMesh = scene.FindMeshByNodePath(nodePath);
                if (fbxMesh == null)
                {
                    if (canWriteMemory && fbxFileIdentity != null)
                    {
                        StoreCachedFbxMetadata(fbxFileIdentity, scene.Metadata, nodePath, meshFound: false, FbxTopologyDigest.Invalid, -1, null);
                    }

                    return VertexMappingResolveResult.Fail(
                        "MAP_FBX_MESH_NOT_FOUND",
                        $"Target FBX mesh not found at node path '{nodePath}'.");
                }

                var targetTopo = FbxTopologyUtility.ComputeDigest(fbxMesh);
                if (!targetTopo.IsValid)
                {
                    return VertexMappingResolveResult.Fail("MAP_FBX_INVALID_TOPOLOGY", $"Target FBX mesh '{nodePath}' has invalid topology.");
                }

                if (hasValidExpectedTopo)
                {
                    if (targetTopo.ControlPointCount < expectedTopo.ControlPointCount)
                    {
                        if (canWriteMemory && fbxFileIdentity != null)
                        {
                            StoreCachedFbxMetadata(fbxFileIdentity, scene.Metadata, nodePath, meshFound: true, targetTopo, -1, null);
                        }

                        return VertexMappingResolveResult.Fail(
                            "MAP_FBX_TOPOLOGY_MISMATCH",
                            $"Target FBX control-point count ({targetTopo.ControlPointCount}) is smaller than patch mesh ({expectedTopo.ControlPointCount}).");
                    }

                    if (!string.Equals(targetTopo.Hash, expectedTopo.Hash, StringComparison.Ordinal))
                    {
                        // Also allow prefix-compatible topology when target FBX appended extra control points.
                        var prefixDigest = FbxTopologyUtility.ComputeDigest(fbxMesh, expectedTopo.ControlPointCount);
                        if (canWriteMemory && fbxFileIdentity != null)
                        {
                            StoreCachedFbxMetadata(fbxFileIdentity, scene.Metadata, nodePath, meshFound: true, targetTopo, expectedTopo.ControlPointCount, prefixDigest);
                        }

                        if (!prefixDigest.IsValid || !string.Equals(prefixDigest.Hash, expectedTopo.Hash, StringComparison.Ordinal))
                        {
                            return VertexMappingResolveResult.Fail(
                                "MAP_FBX_TOPOLOGY_MISMATCH",
                                $"Target FBX topology digest ({targetTopo.Hash}) does not match patch mesh ({expectedTopo.Hash}).");
                        }
                    }
                    else if (canWriteMemory && fbxFileIdentity != null)
                    {
                        StoreCachedFbxMetadata(fbxFileIdentity, scene.Metadata, nodePath, meshFound: true, targetTopo, -1, null);
                    }
                }
                else if (canWriteMemory && fbxFileIdentity != null)
                {
                    StoreCachedFbxMetadata(fbxFileIdentity, scene.Metadata, nodePath, meshFound: true, targetTopo, -1, null);
                }

                int patchControlPointCount = hasValidExpectedTopo
                    ? expectedTopo.ControlPointCount
                    : fbxMesh.ControlPointCount;

                string fbxIdentity = fbxFileIdentity ?? BuildSceneMeshIdentity(scene, fbxMesh, nodePath);
                string resolutionKey = ComputeResolutionKeyHash(
                    hasValidExpectedTopo ? expectedTopo.Hash : targetTopo.Hash,
                    patchControlPointCount,
                    targetGeometry.CombinedGeometryKey,
                    fbxIdentity,
                    nodePath,
                    importSettings,
                    request.SpaceProvenance);

                if (canReadCache && !resolutionCacheChecked &&
                    m_CacheStore.TryLoadResolution(
                        resolutionKey,
                        out var cachedIdx,
                        out var cachedCsr,
                        promoteToMemory: canWriteMemory,
                        persistToDisk: canWriteDisk))
                {
                    var cachedSpace = VertexMappingCodec.BuildSpaceContract(
                        request.SpaceProvenance,
                        scene.Metadata,
                        cachedIdx.MatchedScale,
                        cachedIdx.MatchedBakeAxisConversion,
                        nodePath);
                    return VertexMappingResolveResult.Success(new ResolvedVertexMapping(
                        cachedCsr,
                        cachedSpace,
                        VertexMappingSourceKind.FbxInverseMapCached,
                        cachedIdx.CsrBlobId,
                        cacheHit: true));
                }

                var hints = FbxInverseMapHints.FromImportSettings(importSettings);
                var mapped = FbxUnityInverseMapper.Map(fbxMesh, targetSnapshot, hints);
                if (!mapped.Succeeded)
                {
                    return VertexMappingResolveResult.Fail("MAP_FBX_INVERSE_MAP_FAILED", mapped.Diagnostic);
                }

                // Filter any control points beyond patchControlPointCount if target FBX had extra trailing CPs.
                int[][] groups = FilterControlPointGroups(mapped.VertexControlPointGroups, patchControlPointCount);
                var csr = CsrVertexMapFactory.FromJaggedGroups(
                    groups,
                    patchControlPointCount,
                    IndexDomain.UnityVertex,
                    IndexDomain.ControlPoint);

                var spaceContract = VertexMappingCodec.BuildSpaceContract(
                    request.SpaceProvenance,
                    scene.Metadata,
                    mapped.MatchedScale,
                    mapped.MatchedBakeAxisConversion,
                    nodePath);

                bool canCacheResult = !mapped.IsBlendShapeSensitive && !mapped.ExceededLayoutDfsBudget;
                string blobId = (canCacheResult && (canWriteMemory || canWriteDisk))
                    ? m_CacheStore.StoreResolution(
                        resolutionKey,
                        csr,
                        mapped.MatchedScale,
                        mapped.MatchedBakeAxisConversion,
                        writeToDisk: canWriteDisk,
                        writeToMemory: canWriteMemory)
                    : CsrVertexMapFactory.ComputeCsrBlobId(csr);

                return VertexMappingResolveResult.Success(new ResolvedVertexMapping(
                    csr,
                    spaceContract,
                    VertexMappingSourceKind.FbxInverseMapComputed,
                    blobId,
                    cacheHit: false,
                    diagnostic: mapped.Diagnostic));
            }
            catch (ArgumentException ex)
            {
                return VertexMappingResolveResult.Fail("MAP_FBX_INVALID_CONTRACT", ex.Message);
            }
            finally
            {
                ownedScene?.Dispose();
            }
        }

        private static VertexMappingResolveResult ValidateCachedTopology(
            string nodePath,
            FbxTopologyDigest fullTopo,
            TopologyDigestRecord expectedTopo,
            FbxTopologyDigest? prefixTopo)
        {
            if (!fullTopo.IsValid)
            {
                return VertexMappingResolveResult.Fail("MAP_FBX_INVALID_TOPOLOGY", $"Target FBX mesh '{nodePath}' has invalid topology.");
            }

            if (expectedTopo == null || !expectedTopo.IsValid)
            {
                return null;
            }

            if (fullTopo.ControlPointCount < expectedTopo.ControlPointCount)
            {
                return VertexMappingResolveResult.Fail(
                    "MAP_FBX_TOPOLOGY_MISMATCH",
                    $"Target FBX control-point count ({fullTopo.ControlPointCount}) is smaller than patch mesh ({expectedTopo.ControlPointCount}).");
            }

            if (string.Equals(fullTopo.Hash, expectedTopo.Hash, StringComparison.Ordinal))
            {
                return null;
            }

            if (!prefixTopo.HasValue || !prefixTopo.Value.IsValid ||
                !string.Equals(prefixTopo.Value.Hash, expectedTopo.Hash, StringComparison.Ordinal))
            {
                return VertexMappingResolveResult.Fail(
                    "MAP_FBX_TOPOLOGY_MISMATCH",
                    $"Target FBX topology digest ({fullTopo.Hash}) does not match patch mesh ({expectedTopo.Hash}).");
            }

            return null;
        }

        private bool TryGetCachedFbxMetadata(
            string fileFingerprint,
            string nodePath,
            int prefixControlPointCount,
            out FbxSceneMetadata sceneMetadata,
            out bool meshFound,
            out FbxTopologyDigest fullTopo,
            out FbxTopologyDigest? prefixTopo)
        {
            sceneMetadata = default;
            meshFound = false;
            fullTopo = FbxTopologyDigest.Invalid;
            prefixTopo = null;

            lock (m_FbxMetadataLock)
            {
                if (!m_FbxMetadataByFingerprint.TryGetValue(fileFingerprint, out var fileEntry))
                {
                    return false;
                }

                fileEntry.LastAccessSeq = ++m_FbxMetadataAccessSeq;
                if (!fileEntry.Nodes.TryGetValue(nodePath, out var nodeEntry))
                {
                    return false;
                }

                sceneMetadata = fileEntry.SceneMetadata;
                meshFound = nodeEntry.MeshFound;
                if (!meshFound)
                {
                    return true;
                }

                fullTopo = nodeEntry.FullTopology;
                if (prefixControlPointCount > 0 && fullTopo.ControlPointCount > prefixControlPointCount)
                {
                    if (nodeEntry.PrefixTopologies == null ||
                        !nodeEntry.PrefixTopologies.TryGetValue(prefixControlPointCount, out var cachedPrefix))
                    {
                        return false;
                    }

                    prefixTopo = cachedPrefix;
                }

                return true;
            }
        }

        private void StoreCachedFbxMetadata(
            string fileFingerprint,
            FbxSceneMetadata sceneMetadata,
            string nodePath,
            bool meshFound,
            FbxTopologyDigest fullTopo,
            int prefixControlPointCount,
            FbxTopologyDigest? prefixTopo)
        {
            lock (m_FbxMetadataLock)
            {
                if (!m_FbxMetadataByFingerprint.TryGetValue(fileFingerprint, out var fileEntry))
                {
                    if (m_FbxMetadataByFingerprint.Count >= k_MaxCachedFbxFiles)
                    {
                        EvictOldestFbxMetadataFileLocked();
                    }

                    fileEntry = new CachedFbxFileMetadata
                    {
                        SceneMetadata = sceneMetadata,
                        LastAccessSeq = ++m_FbxMetadataAccessSeq
                    };
                    m_FbxMetadataByFingerprint.Add(fileFingerprint, fileEntry);
                }
                else
                {
                    fileEntry.SceneMetadata = sceneMetadata;
                    fileEntry.LastAccessSeq = ++m_FbxMetadataAccessSeq;
                }

                if (!fileEntry.Nodes.TryGetValue(nodePath, out var nodeEntry))
                {
                    if (fileEntry.Nodes.Count >= k_MaxCachedNodesPerFbxFile)
                    {
                        fileEntry.Nodes.Clear();
                    }

                    nodeEntry = new CachedFbxNodeEntry
                    {
                        MeshFound = meshFound,
                        FullTopology = fullTopo
                    };
                    fileEntry.Nodes.Add(nodePath, nodeEntry);
                }
                else
                {
                    nodeEntry.MeshFound = meshFound;
                    nodeEntry.FullTopology = fullTopo;
                }

                if (meshFound && prefixControlPointCount > 0 && prefixTopo.HasValue)
                {
                    nodeEntry.PrefixTopologies ??= new Dictionary<int, FbxTopologyDigest>();
                    if (nodeEntry.PrefixTopologies.Count >= 32 && !nodeEntry.PrefixTopologies.ContainsKey(prefixControlPointCount))
                    {
                        nodeEntry.PrefixTopologies.Clear();
                    }

                    nodeEntry.PrefixTopologies[prefixControlPointCount] = prefixTopo.Value;
                }
            }
        }

        private void EvictOldestFbxMetadataFileLocked()
        {
            string oldestKey = null;
            long oldestSeq = long.MaxValue;
            foreach (var pair in m_FbxMetadataByFingerprint)
            {
                if (pair.Value.LastAccessSeq < oldestSeq)
                {
                    oldestSeq = pair.Value.LastAccessSeq;
                    oldestKey = pair.Key;
                }
            }

            if (oldestKey != null)
            {
                m_FbxMetadataByFingerprint.Remove(oldestKey);
            }
        }

        private static int[][] FilterControlPointGroups(int[][] groups, int maxControlPointCount)
        {
            if (groups == null)
            {
                return Array.Empty<int[]>();
            }

            var filtered = new int[groups.Length][];
            for (int i = 0; i < groups.Length; i++)
            {
                var row = groups[i];
                if (row == null || row.Length == 0)
                {
                    filtered[i] = Array.Empty<int>();
                    continue;
                }

                bool allInBounds = true;
                for (int j = 0; j < row.Length; j++)
                {
                    if ((uint)row[j] >= (uint)maxControlPointCount)
                    {
                        allInBounds = false;
                        break;
                    }
                }

                if (allInBounds)
                {
                    filtered[i] = row;
                    continue;
                }

                var kept = new List<int>(row.Length);
                for (int j = 0; j < row.Length; j++)
                {
                    if ((uint)row[j] < (uint)maxControlPointCount)
                    {
                        kept.Add(row[j]);
                    }
                }

                filtered[i] = kept.ToArray();
            }

            return filtered;
        }

        private static string ComputeResolutionKeyHash(
            string patchTopologyHash,
            int patchControlPointCount,
            string targetMeshGeometryKey,
            string targetFbxIdentity,
            string nodePath,
            FbxUnityImportSettings importSettings,
            PatchSpaceProvenance stored)
        {
            using (var sha = SHA256.Create())
            using (var cs = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write))
            using (var bw = new BinaryWriter(cs, Encoding.UTF8, leaveOpen: true))
            {
                bw.Write(k_ResolutionKeyDomain);
                bw.Write(VertexMappingCacheStore.FbxReaderAlgoRevision);
                VertexMappingCacheStore.WriteSpaceKey(bw, stored);
                bw.Write(patchTopologyHash ?? string.Empty);
                bw.Write(patchControlPointCount);
                bw.Write(targetMeshGeometryKey ?? string.Empty);
                bw.Write(targetFbxIdentity ?? string.Empty);
                bw.Write(nodePath ?? string.Empty);
                bw.Write(importSettings.HasImporter);
                bw.Write(importSettings.FileScale);
                bw.Write(importSettings.GlobalScale);
                bw.Write(importSettings.UseFileScale);
                bw.Write(importSettings.BakeAxisConversion);
                bw.Write(importSettings.WeldVertices);
                bw.Write(importSettings.ImportBlendShapes);
                bw.Flush();
                cs.FlushFinalBlock();
                return ToLowerHex(sha.Hash);
            }
        }

        private static string ComputeBytesFingerprint(byte[] fileBytes)
        {
            using (var sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(fileBytes);
                return $"file:r{VertexMappingCacheStore.FbxReaderAlgoRevision}:{fileBytes.Length}:{ToLowerHex(digest)}";
            }
        }

        /// <summary>
        /// Computes a complete fingerprint of every <see cref="IFbxMesh"/> input consulted by <see cref="FbxUnityInverseMapper"/>
        /// (control points, raw face sizes, raw face-corner control points, per-corner UV0, and eligible single-frame
        /// blendshape presence) for synthetic or in-memory <see cref="IFbxScene"/> overrides.
        /// </summary>
        private static string BuildSceneMeshIdentity(IFbxScene scene, IFbxMesh fbxMesh, string nodePath)
        {
            var cps = fbxMesh.GetControlPoints();
            var faceSizes = fbxMesh.GetFaceSizes();
            var cornerCps = fbxMesh.GetFaceCornerControlPoints();
            bool hasEligibleShapes = FbxUnityInverseMapper.HasEligibleSingleFrameBlendShape(fbxMesh);

            using (var sha = SHA256.Create())
            using (var cs = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write))
            using (var bw = new BinaryWriter(cs, Encoding.UTF8, leaveOpen: true))
            {
                bw.Write("BlendShare.FbxSceneOverrideIdentity.v1");
                bw.Write(nodePath ?? string.Empty);
                bw.Write(BitConverter.DoubleToInt64Bits(scene.Metadata.UnitMeters));
                bw.Write((int)scene.Metadata.Axes.Right);
                bw.Write((int)scene.Metadata.Axes.Up);
                bw.Write((int)scene.Metadata.Axes.Front);

                bw.Write(cps.Length);
                for (int i = 0; i < cps.Length; i++)
                {
                    bw.Write(BitConverter.DoubleToInt64Bits(cps[i].x));
                    bw.Write(BitConverter.DoubleToInt64Bits(cps[i].y));
                    bw.Write(BitConverter.DoubleToInt64Bits(cps[i].z));
                }

                bw.Write(faceSizes.Length);
                for (int i = 0; i < faceSizes.Length; i++)
                {
                    bw.Write(faceSizes[i]);
                }

                bw.Write(cornerCps.Length);
                for (int i = 0; i < cornerCps.Length; i++)
                {
                    bw.Write(cornerCps[i]);
                }

                // UV0 per-corner state: 0 = absent, 1 = present and full copy succeeded, 2 = present but copy failed/partial.
                if (fbxMesh.UvSets != null &&
                    fbxMesh.UvSets.Count > 0 &&
                    fbxMesh.UvSets[0]?.Uvs != null &&
                    fbxMesh.UvSets[0].Uvs.Exists)
                {
                    var uv0 = new Vector2d[cornerCps.Length];
                    if (fbxMesh.UvSets[0].Uvs.TryCopyPerCornerValues(uv0, out int uvWritten) &&
                        uvWritten == cornerCps.Length)
                    {
                        bw.Write((byte)1);
                        bw.Write(uvWritten);
                        for (int i = 0; i < uvWritten; i++)
                        {
                            bw.Write(BitConverter.DoubleToInt64Bits(uv0[i].x));
                            bw.Write(BitConverter.DoubleToInt64Bits(uv0[i].y));
                        }
                    }
                    else
                    {
                        bw.Write((byte)2);
                    }
                }
                else
                {
                    bw.Write((byte)0);
                }

                bw.Write(hasEligibleShapes);
                bw.Flush();
                cs.FlushFinalBlock();
                return "scene:" + ToLowerHex(sha.Hash);
            }
        }

        private static string ToLowerHex(byte[] bytes)
        {
            const string hex = "0123456789abcdef";
            var chars = new char[bytes.Length * 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                byte b = bytes[i];
                chars[i * 2] = hex[b >> 4];
                chars[i * 2 + 1] = hex[b & 0xF];
            }

            return new string(chars);
        }
    }
}
