using System;
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

namespace Triturbo.BlendShare.EditorAssets.Binding
{
    public enum BindingSourceKind : byte
    {
        None = 0,
        OriginBindingFastPath = 1,
        OriginBindingCoincidentFallback = 2,
        FbxInverseMapComputed = 3,
        FbxInverseMapCached = 4,
        ComposedReferenceMeshComputed = 5,
        ComposedReferenceMeshCached = 6
    }

    /// <summary>
    /// Immutable verified vertex binding + per-use coordinate space contract consumed by Unity appliers.
    /// </summary>
    public sealed class ResolvedVertexBinding
    {
        public CsrVertexMap VertexMap { get; }
        public OriginSpaceContract SpaceContract { get; }
        public BindingSourceKind SourceKind { get; }
        public string CsrBlobId { get; }
        public bool CacheHit { get; }
        public string Diagnostic { get; }

        public ResolvedVertexBinding(
            CsrVertexMap vertexMap,
            OriginSpaceContract spaceContract,
            BindingSourceKind sourceKind,
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

    public sealed class BindingResolveRequest
    {
        public MeshDocument MeshHeader { get; set; }
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
        public bool PreferOriginBinding { get; set; }
        public bool AllowDiskCache { get; set; } = true;
    }

    public sealed class BindingResolveResult
    {
        public bool Succeeded { get; }
        public ResolvedVertexBinding Binding { get; }
        public string FailureCode { get; }
        public string Diagnostic { get; }
        public System.Collections.Generic.IReadOnlyList<int> ConflictingTargetVertices { get; }

        private BindingResolveResult(
            bool succeeded,
            ResolvedVertexBinding binding,
            string failureCode,
            string diagnostic,
            System.Collections.Generic.IReadOnlyList<int> conflictingTargetVertices = null)
        {
            Succeeded = succeeded;
            Binding = binding;
            FailureCode = failureCode ?? string.Empty;
            Diagnostic = diagnostic ?? string.Empty;
            ConflictingTargetVertices = conflictingTargetVertices ?? Array.Empty<int>();
        }

        public static BindingResolveResult Success(ResolvedVertexBinding binding) =>
            new BindingResolveResult(true, binding, string.Empty, binding.Diagnostic);

        public static BindingResolveResult Fail(
            string code,
            string diagnostic,
            System.Collections.Generic.IReadOnlyList<int> conflictingTargetVertices = null) =>
            new BindingResolveResult(false, null, code, diagnostic, conflictingTargetVertices);
    }

    /// <summary>
    /// Single unified gateway for resolving <c>UnityVertex -> Origin ControlPoint</c> bindings and
    /// per-use <c>OriginRaw -> UnityMeshLocal</c> space contracts.
    /// Supports non-readable Unity meshes, 2-stage <see cref="OriginBindingDocument"/> verification without FBX,
    /// and 3-layer cached FBX inverse mapping.
    /// </summary>
    public sealed class BindingService
    {
        private const string k_ResolutionKeyDomain = "BlendShare.BindingResolutionKey.v1";

        public static readonly BindingService Shared = new BindingService(new BindingCacheStore());

        private readonly BindingCacheStore m_CacheStore;

        public BindingCacheStore CacheStore => m_CacheStore;

        public BindingService(BindingCacheStore cacheStore)
        {
            m_CacheStore = cacheStore ?? throw new ArgumentNullException(nameof(cacheStore));
        }

        public BindingResolveResult Resolve(BindingResolveRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (request.MeshHeader == null)
            {
                return BindingResolveResult.Fail("BIND_NO_MESH_HEADER", "MeshDocument header is required.");
            }

            var stored = request.MeshHeader.SpaceProvenance;
            if (stored != null && !stored.IsValid)
                return BindingResolveResult.Fail("BIND_INVALID_STORED_SPACE", "Invalid origin raw axes or unit scale.");

            UnityMeshSnapshot snapshot = request.TargetSnapshotOverride;
            if (snapshot == null)
            {
                if (request.TargetUnityMesh == null)
                {
                    return BindingResolveResult.Fail("BIND_NO_TARGET_MESH", "Target Unity Mesh or UnityMeshSnapshot is required.");
                }

                if (!UnityMeshReaderUtility.TryReadMeshSnapshot(request.TargetUnityMesh, out snapshot))
                {
                    return BindingResolveResult.Fail(
                        "BIND_READ_MESH_FAILED",
                        $"Failed to read CPU geometry from Unity mesh '{request.TargetUnityMesh.name}'.");
                }
            }

            bool hasOriginBinding = request.MeshHeader.OriginBinding != null && request.MeshHeader.OriginBinding.HasPayload;
            bool hasTargetFbx = request.TargetFbxSceneOverride != null || request.TargetFbxAsset != null;
            bool hasReferenceMesh = request.ReferenceSnapshotOverride != null || request.ReferenceUnityMesh != null;

            string originFailureDiag = null;
            if (hasOriginBinding && (request.PreferOriginBinding || !hasTargetFbx))
            {
                var originRes = TryResolveFromOriginBinding(request, snapshot);
                if (originRes.Succeeded)
                {
                    return originRes;
                }

                originFailureDiag = originRes.Diagnostic;
                if (!hasTargetFbx && !hasReferenceMesh)
                {
                    return originRes;
                }
            }

            BindingResolveResult directFbxFailure = null;
            if (hasTargetFbx)
            {
                var fbxRes = TryResolveFromTargetFbx(request, snapshot);
                if (fbxRes.Succeeded)
                {
                    return fbxRes;
                }

                directFbxFailure = fbxRes;

                // Fallback to OriginBinding if we didn't already try it.
                if (hasOriginBinding && originFailureDiag == null)
                {
                    var originFallback = TryResolveFromOriginBinding(request, snapshot);
                    if (originFallback.Succeeded)
                    {
                        return originFallback;
                    }

                    originFailureDiag = originFallback.Diagnostic;
                }
            }

            // Reference-mesh composed binding: resolve Reference -> Origin CP, then compose with Target -> Reference.
            if (hasReferenceMesh)
            {
                return TryResolveViaReferenceMesh(request, snapshot);
            }

            if (directFbxFailure != null)
            {
                return originFailureDiag != null
                    ? BindingResolveResult.Fail(
                        directFbxFailure.FailureCode,
                        $"{directFbxFailure.Diagnostic} (OriginBinding fallback also failed: {originFailureDiag})")
                    : directFbxFailure;
            }

            return BindingResolveResult.Fail(
                "BIND_NO_SOURCE",
                "Neither a target FBX, a reference Unity mesh, nor an embedded OriginBinding payload is available to bind this mesh.");
        }

        private BindingResolveResult TryResolveViaReferenceMesh(
            BindingResolveRequest request,
            UnityMeshSnapshot targetSnapshot)
        {
            UnityMeshSnapshot refSnapshot = request.ReferenceSnapshotOverride;
            if (refSnapshot == null)
            {
                if (request.ReferenceUnityMesh == null ||
                    !UnityMeshReaderUtility.TryReadMeshSnapshot(request.ReferenceUnityMesh, out refSnapshot))
                {
                    return BindingResolveResult.Fail(
                        "BIND_REF_MESH_READ_FAILED",
                        "Failed to read CPU geometry from reference Unity mesh.");
                }
            }

            // Resolve Reference Unity Mesh -> Origin ControlPoints (without recursing into ReferenceMesh again).
            var refRes = Resolve(new BindingResolveRequest
            {
                MeshHeader = request.MeshHeader,
                TargetSnapshotOverride = refSnapshot,
                TargetFbxAsset = request.TargetFbxAsset,
                TargetFbxSceneOverride = request.TargetFbxSceneOverride,
                TargetFbxNodePathOverride = request.TargetFbxNodePathOverride,
                TargetImportSettingsOverride = request.TargetImportSettingsOverride,
                PayloadSource = request.PayloadSource,
                PreferOriginBinding = request.PreferOriginBinding,
                AllowDiskCache = request.AllowDiskCache
            });

            if (!refRes.Succeeded || refRes.Binding == null)
            {
                return BindingResolveResult.Fail(
                    "BIND_REF_RESOLUTION_FAILED",
                    $"Could not bind reference mesh to patch origin: {refRes.FailureCode} ({refRes.Diagnostic})");
            }

            var targetDig = UnityMeshGeometryDigests.Compute(targetSnapshot);
            var refDig = UnityMeshGeometryDigests.Compute(refSnapshot);
            string composedKey = ComputeComposedResolutionKeyHash(
                refRes.Binding.CsrBlobId,
                refDig.CombinedGeometryKey,
                targetDig.CombinedGeometryKey,
                request.ComposePolicy, request.MeshHeader.SpaceProvenance);

            if (request.AllowDiskCache &&
                m_CacheStore.TryLoadResolution(composedKey, out var cachedIdx, out var cachedCsr))
            {
                return BindingResolveResult.Success(new ResolvedVertexBinding(
                    cachedCsr,
                    refRes.Binding.SpaceContract,
                    BindingSourceKind.ComposedReferenceMeshCached,
                    cachedIdx.CsrBlobId,
                    cacheHit: true));
            }

            // Track whether UnityMeshToMeshMapper consulted single-frame blendshapes on either snapshot to break ties.
            // If so, bypass cache storage under the geometry-only key so swapped deltas never return a stale CSR.
            TrackingBlendShapeSnapshot trackedRefShapes = refSnapshot.BlendShapes != null
                ? new TrackingBlendShapeSnapshot(refSnapshot.BlendShapes)
                : null;
            TrackingBlendShapeSnapshot trackedTargetShapes = targetSnapshot.BlendShapes != null
                ? new TrackingBlendShapeSnapshot(targetSnapshot.BlendShapes)
                : null;
            UnityMeshSnapshot mapRefSnap = trackedRefShapes != null
                ? new UnityMeshSnapshot(refSnapshot.Vertices, refSnapshot.Triangles, refSnapshot.SubmeshTriangleOffsets, refSnapshot.Uv0, trackedRefShapes)
                : refSnapshot;
            UnityMeshSnapshot mapTargetSnap = trackedTargetShapes != null
                ? new UnityMeshSnapshot(targetSnapshot.Vertices, targetSnapshot.Triangles, targetSnapshot.SubmeshTriangleOffsets, targetSnapshot.Uv0, trackedTargetShapes)
                : targetSnapshot;

            var correspondence = UnityMeshToMeshMapper.Map(mapRefSnap, mapTargetSnap);
            if (!correspondence.Succeeded)
            {
                return BindingResolveResult.Fail(
                    "BIND_COMPOSED_CORRESPONDENCE_FAILED",
                    $"Reference-to-target Unity mesh mapping failed: {correspondence.Diagnostic}");
            }

            var composed = VertexMapComposer.Compose(
                refRes.Binding.VertexMap,
                correspondence,
                request.ComposePolicy);

            if (!composed.Succeeded)
            {
                if (composed.ConflictingMergedTargetCount > 0)
                {
                    return BindingResolveResult.Fail(
                        "BIND_COMPOSED_MERGE_CONFLICT",
                        $"Reference mesh composition rejected {composed.ConflictingMergedTargetCount} target vertex(es) with conflicting merged FBX control-point groups ({composed.Diagnostic}).",
                        composed.ConflictingTargetVertices);
                }

                return BindingResolveResult.Fail(
                    "BIND_COMPOSED_UNRESOLVED",
                    composed.Diagnostic,
                    composed.ConflictingTargetVertices);
            }

            var composedCsr = CsrVertexMapFactory.FromJaggedGroups(
                composed.TargetFbxGroups,
                refRes.Binding.VertexMap.TargetCount,
                IndexDomain.UnityVertex,
                IndexDomain.ControlPoint);

            bool usedBlendShapeTieBreaker = (trackedRefShapes != null && trackedRefShapes.WasConsulted) ||
                                            (trackedTargetShapes != null && trackedTargetShapes.WasConsulted);
            string blobId = usedBlendShapeTieBreaker
                ? CsrVertexMapFactory.ComputeCsrBlobId(composedCsr)
                : m_CacheStore.StoreResolution(
                    composedKey,
                    composedCsr,
                    matchedScale: 1f,
                    matchedBakeAxisConversion: false,
                    writeToDisk: request.AllowDiskCache);

            return BindingResolveResult.Success(new ResolvedVertexBinding(
                composedCsr,
                refRes.Binding.SpaceContract,
                BindingSourceKind.ComposedReferenceMeshComputed,
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
            VertexMapComposePolicy policy, MeshSpaceProvenance stored)
        {
            using (var sha = SHA256.Create())
            using (var cs = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write))
            using (var bw = new BinaryWriter(cs, Encoding.UTF8, leaveOpen: true))
            {
                bw.Write("BlendShare.ComposedBindingKey.v1");
                bw.Write(referenceCsrBlobId ?? string.Empty);
                bw.Write(referenceGeometryKey ?? string.Empty);
                bw.Write(targetGeometryKey ?? string.Empty);
                bw.Write((int)policy);
                BindingCacheStore.WriteSpaceKey(bw, stored);
                bw.Flush();
                cs.FlushFinalBlock();
                return ToLowerHex(sha.Hash);
            }
        }

        private BindingResolveResult TryResolveFromOriginBinding(
            BindingResolveRequest request,
            UnityMeshSnapshot targetSnapshot)
        {
            var payload = request.MeshHeader.OriginBinding;
            if (payload == null || !payload.HasPayload)
            {
                return BindingResolveResult.Fail("BIND_ORIGIN_ABSENT", "Mesh has no embedded OriginBinding payload.");
            }

            var payloadSource = request.PayloadSource ?? BlendShareSourceRepository.Shared.PayloadStore;
            OriginBindingDocument doc;
            try
            {
                using (var lease = payload.Acquire(payloadSource))
                {
                    doc = lease.Value;
                }
            }
            catch (Exception ex)
            {
                return BindingResolveResult.Fail("BIND_ORIGIN_LOAD_FAILED", $"Failed to acquire OriginBinding payload: {ex.Message}");
            }

            int expectedCpCount = request.MeshHeader.Topology != null && request.MeshHeader.Topology.IsValid
                ? request.MeshHeader.Topology.ControlPointCount
                : -1;

            var verify = OriginBindingCodec.Verify(doc, targetSnapshot, expectedCpCount,
                request.MeshHeader.SpaceProvenance);
            if (!verify.Succeeded)
            {
                return BindingResolveResult.Fail($"BIND_ORIGIN_{verify.Status}", verify.Diagnostic);
            }

            var sourceKind = verify.Status == OriginBindingVerificationStatus.ExactFastPath
                ? BindingSourceKind.OriginBindingFastPath
                : BindingSourceKind.OriginBindingCoincidentFallback;

            string blobId = CsrVertexMapFactory.ComputeCsrBlobId(verify.VertexMap);
            return BindingResolveResult.Success(new ResolvedVertexBinding(
                verify.VertexMap,
                verify.SpaceContract,
                sourceKind,
                blobId,
                cacheHit: false,
                diagnostic: verify.Diagnostic));
        }

        private BindingResolveResult TryResolveFromTargetFbx(
            BindingResolveRequest request,
            UnityMeshSnapshot targetSnapshot)
        {
            string nodePath = !string.IsNullOrWhiteSpace(request.TargetFbxNodePathOverride)
                ? FbxNameUtility.NormalizePath(request.TargetFbxNodePathOverride)
                : FbxNameUtility.NormalizePath(request.MeshHeader.NodePath);

            if (string.IsNullOrEmpty(nodePath))
            {
                return BindingResolveResult.Fail("BIND_FBX_EMPTY_NODE_PATH", "Target FBX node path is empty.");
            }

            var targetGeometry = UnityMeshGeometryDigests.Compute(targetSnapshot);
            var importSettings = request.TargetImportSettingsOverride ??
                                 (request.TargetFbxAsset != null
                                     ? FbxUnityAssetReader.GetImportSettings(request.TargetFbxAsset)
                                     : FbxUnityImportSettings.Default);

            // Fast check if we can resolve FBX physical file fingerprint before loading ufbx scene.
            string fbxFileIdentity = null;
            if (request.TargetFbxSceneOverride == null && request.TargetFbxAsset != null)
            {
                string assetPath = AssetDatabase.GetAssetPath(request.TargetFbxAsset);
                string physicalPath = BlendSharePathUtility.ResolvePhysicalPath(assetPath);
                if (!string.IsNullOrEmpty(physicalPath) && File.Exists(physicalPath))
                {
                    fbxFileIdentity = ComputeFileFingerprint(physicalPath);
                }
            }

            IFbxScene ownedScene = null;
            try
            {
                IFbxScene scene = request.TargetFbxSceneOverride;
                if (scene == null)
                {
                    var read = FbxUnityAssetReader.ReadScene(request.TargetFbxAsset, UfbxLoadOptions.Default);
                    if (!read.Success || read.Value == null)
                    {
                        return BindingResolveResult.Fail("BIND_FBX_LOAD_FAILED", $"Failed to load target FBX: {read.Message}");
                    }

                    ownedScene = read.Value;
                    scene = ownedScene;
                }

                var fbxMesh = scene.FindMeshByNodePath(nodePath);
                if (fbxMesh == null)
                {
                    return BindingResolveResult.Fail(
                        "BIND_FBX_MESH_NOT_FOUND",
                        $"Target FBX mesh not found at node path '{nodePath}'.");
                }

                var targetTopo = FbxTopologyUtility.ComputeDigest(fbxMesh);
                if (!targetTopo.IsValid)
                {
                    return BindingResolveResult.Fail("BIND_FBX_INVALID_TOPOLOGY", $"Target FBX mesh '{nodePath}' has invalid topology.");
                }

                var expectedTopo = request.MeshHeader.Topology;
                if (expectedTopo != null && expectedTopo.IsValid)
                {
                    if (targetTopo.ControlPointCount < expectedTopo.ControlPointCount)
                    {
                        return BindingResolveResult.Fail(
                            "BIND_FBX_TOPOLOGY_MISMATCH",
                            $"Target FBX control-point count ({targetTopo.ControlPointCount}) is smaller than patch origin ({expectedTopo.ControlPointCount}).");
                    }

                    if (!string.Equals(targetTopo.Hash, expectedTopo.Hash, StringComparison.Ordinal))
                    {
                        // Also allow prefix-compatible topology when target FBX appended extra control points.
                        var prefixDigest = FbxTopologyUtility.ComputeDigest(fbxMesh, expectedTopo.ControlPointCount);
                        if (!prefixDigest.IsValid || !string.Equals(prefixDigest.Hash, expectedTopo.Hash, StringComparison.Ordinal))
                        {
                            return BindingResolveResult.Fail(
                                "BIND_FBX_TOPOLOGY_MISMATCH",
                                $"Target FBX topology digest ({targetTopo.Hash}) does not match patch origin ({expectedTopo.Hash}).");
                        }
                    }
                }

                int patchControlPointCount = expectedTopo != null && expectedTopo.IsValid
                    ? expectedTopo.ControlPointCount
                    : fbxMesh.ControlPointCount;

                string fbxIdentity = fbxFileIdentity ?? BuildSceneMeshIdentity(scene, fbxMesh, nodePath, targetTopo);
                string resolutionKey = ComputeResolutionKeyHash(
                    expectedTopo != null ? expectedTopo.Hash : targetTopo.Hash,
                    patchControlPointCount,
                    targetGeometry.CombinedGeometryKey,
                    fbxIdentity,
                    nodePath,
                    importSettings, request.MeshHeader.SpaceProvenance);

                if (request.AllowDiskCache &&
                    m_CacheStore.TryLoadResolution(resolutionKey, out var cachedIdx, out var cachedCsr))
                {
                    var cachedSpace = OriginBindingCodec.BuildSpaceContract(
                        request.MeshHeader.SpaceProvenance,
                        scene.Metadata,
                        cachedIdx.MatchedScale,
                        cachedIdx.MatchedBakeAxisConversion,
                        nodePath);
                    return BindingResolveResult.Success(new ResolvedVertexBinding(
                        cachedCsr,
                        cachedSpace,
                        BindingSourceKind.FbxInverseMapCached,
                        cachedIdx.CsrBlobId,
                        cacheHit: true));
                }

                // Track whether FbxUnityInverseMapper had to consult target mesh blendshape deltas to break ties.
                // Resolutions that depend on blendshape tie-breakers bypass reuse under the geometry-only key.
                TrackingBlendShapeSnapshot trackedShapes = targetSnapshot.BlendShapes != null
                    ? new TrackingBlendShapeSnapshot(targetSnapshot.BlendShapes)
                    : null;
                UnityMeshSnapshot mapSnapshot = trackedShapes != null
                    ? new UnityMeshSnapshot(
                        targetSnapshot.Vertices,
                        targetSnapshot.Triangles,
                        targetSnapshot.SubmeshTriangleOffsets,
                        targetSnapshot.Uv0,
                        trackedShapes)
                    : targetSnapshot;

                var hints = FbxInverseMapHints.FromImportSettings(importSettings);
                var mapped = FbxUnityInverseMapper.Map(fbxMesh, mapSnapshot, hints);
                if (!mapped.Succeeded)
                {
                    return BindingResolveResult.Fail("BIND_FBX_INVERSE_MAP_FAILED", mapped.Diagnostic);
                }

                // Filter any control points beyond patchControlPointCount if target FBX had extra trailing CPs.
                int[][] groups = FilterControlPointGroups(mapped.VertexControlPointGroups, patchControlPointCount);
                var csr = CsrVertexMapFactory.FromJaggedGroups(
                    groups,
                    patchControlPointCount,
                    IndexDomain.UnityVertex,
                    IndexDomain.ControlPoint);

                var spaceContract = OriginBindingCodec.BuildSpaceContract(
                    request.MeshHeader.SpaceProvenance,
                    scene.Metadata,
                    mapped.MatchedScale,
                    mapped.MatchedBakeAxisConversion,
                    nodePath);

                bool usedBlendShapeTieBreaker = trackedShapes != null && trackedShapes.WasConsulted;
                string blobId = usedBlendShapeTieBreaker
                    ? CsrVertexMapFactory.ComputeCsrBlobId(csr)
                    : m_CacheStore.StoreResolution(
                        resolutionKey,
                        csr,
                        mapped.MatchedScale,
                        mapped.MatchedBakeAxisConversion,
                        writeToDisk: request.AllowDiskCache);

                return BindingResolveResult.Success(new ResolvedVertexBinding(
                    csr,
                    spaceContract,
                    BindingSourceKind.FbxInverseMapComputed,
                    blobId,
                    cacheHit: false,
                    diagnostic: mapped.Diagnostic));
            }
            catch (ArgumentException ex)
            {
                return BindingResolveResult.Fail("BIND_FBX_INVALID_CONTRACT", ex.Message);
            }
            finally
            {
                ownedScene?.Dispose();
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

                var kept = new System.Collections.Generic.List<int>(row.Length);
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
            FbxUnityImportSettings importSettings, MeshSpaceProvenance stored)
        {
            using (var sha = SHA256.Create())
            using (var cs = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write))
            using (var bw = new BinaryWriter(cs, Encoding.UTF8, leaveOpen: true))
            {
                bw.Write(k_ResolutionKeyDomain);
                BindingCacheStore.WriteSpaceKey(bw, stored);
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

        private static string ComputeFileFingerprint(string physicalPath)
        {
            var info = new FileInfo(physicalPath);
            using (var sha = SHA256.Create())
            using (var fs = new FileStream(physicalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                byte[] digest = sha.ComputeHash(fs);
                return $"file:{info.Length}:{ToLowerHex(digest)}";
            }
        }

        private static string BuildSceneMeshIdentity(IFbxScene scene, IFbxMesh fbxMesh, string nodePath, FbxTopologyDigest topo)
        {
            var cps = fbxMesh.GetControlPoints();
            string cpDigest;
            using (var sha = SHA256.Create())
            using (var cs = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write))
            using (var bw = new BinaryWriter(cs, Encoding.UTF8, leaveOpen: true))
            {
                bw.Write(cps.Length);
                for (int i = 0; i < cps.Length; i++)
                {
                    bw.Write(cps[i].x);
                    bw.Write(cps[i].y);
                    bw.Write(cps[i].z);
                }

                bw.Flush();
                cs.FlushFinalBlock();
                cpDigest = ToLowerHex(sha.Hash);
            }

            return $"scene:{scene.AssetPath}|{nodePath}|{topo.Hash}|{cpDigest}|{scene.Metadata.UnitMeters:G17}|{(int)scene.Metadata.Axes.Right}|{(int)scene.Metadata.Axes.Up}|{(int)scene.Metadata.Axes.Front}";
        }

        private sealed class TrackingBlendShapeSnapshot : IUnityBlendShapeSnapshot
        {
            private readonly IUnityBlendShapeSnapshot m_Inner;

            public bool WasConsulted { get; private set; }

            public TrackingBlendShapeSnapshot(IUnityBlendShapeSnapshot inner)
            {
                m_Inner = inner;
            }

            public System.Collections.Generic.IReadOnlyList<string> SingleFrameShapeNames => m_Inner.SingleFrameShapeNames;

            public bool TryGetSingleFrameDeltaVertices(string shapeName, out Vector3[] deltaVertices)
            {
                WasConsulted = true;
                return m_Inner.TryGetSingleFrameDeltaVertices(shapeName, out deltaVertices);
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
