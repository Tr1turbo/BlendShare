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
        FbxInverseMapCached = 4
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
        public GameObject TargetFbxAsset { get; set; }
        public IFbxScene TargetFbxSceneOverride { get; set; }
        public string TargetFbxNodePathOverride { get; set; }
        public FbxUnityImportSettings? TargetImportSettingsOverride { get; set; }
        public ILazyPayloadSource PayloadSource { get; set; }
        public bool PreferOriginBinding { get; set; }
        public bool AllowDiskCache { get; set; } = true;
    }

    public sealed class BindingResolveResult
    {
        public bool Succeeded { get; }
        public ResolvedVertexBinding Binding { get; }
        public string FailureCode { get; }
        public string Diagnostic { get; }

        private BindingResolveResult(bool succeeded, ResolvedVertexBinding binding, string failureCode, string diagnostic)
        {
            Succeeded = succeeded;
            Binding = binding;
            FailureCode = failureCode ?? string.Empty;
            Diagnostic = diagnostic ?? string.Empty;
        }

        public static BindingResolveResult Success(ResolvedVertexBinding binding) =>
            new BindingResolveResult(true, binding, string.Empty, binding.Diagnostic);

        public static BindingResolveResult Fail(string code, string diagnostic) =>
            new BindingResolveResult(false, null, code, diagnostic);
    }

    /// <summary>
    /// Single unified gateway for resolving <c>UnityVertex -> Origin ControlPoint</c> bindings and
    /// per-use <c>CanonicalMeshLocalV1 -> UnityMeshLocal</c> space contracts.
    /// Supports non-readable Unity meshes, 2-stage <see cref="OriginBindingDocument"/> verification without FBX,
    /// and 3-layer <c>Library/BlendShare/Bindings/v1/</c> cached FBX inverse mapping.
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

            string originFailureDiag = null;
            if (hasOriginBinding && (request.PreferOriginBinding || !hasTargetFbx))
            {
                var originRes = TryResolveFromOriginBinding(request, snapshot);
                if (originRes.Succeeded)
                {
                    return originRes;
                }

                originFailureDiag = originRes.Diagnostic;
                if (!hasTargetFbx)
                {
                    return originRes;
                }
            }

            if (hasTargetFbx)
            {
                var fbxRes = TryResolveFromTargetFbx(request, snapshot);
                if (fbxRes.Succeeded)
                {
                    return fbxRes;
                }

                // Fallback to OriginBinding if we didn't already try it.
                if (hasOriginBinding && originFailureDiag == null)
                {
                    var originFallback = TryResolveFromOriginBinding(request, snapshot);
                    if (originFallback.Succeeded)
                    {
                        return originFallback;
                    }

                    return BindingResolveResult.Fail(
                        fbxRes.FailureCode,
                        $"{fbxRes.Diagnostic} (OriginBinding fallback also failed: {originFallback.Diagnostic})");
                }

                return fbxRes;
            }

            return BindingResolveResult.Fail(
                "BIND_NO_SOURCE",
                "Neither a target FBX nor an embedded OriginBinding payload is available to bind this mesh.");
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

            var verify = OriginBindingCodec.Verify(doc, targetSnapshot, expectedCpCount);
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
                    importSettings);

                if (request.AllowDiskCache &&
                    m_CacheStore.TryLoadResolution(resolutionKey, out var cachedIdx, out var cachedCsr))
                {
                    var cachedSpace = BuildPerUseSpaceContract(
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

                bool usedBlendShapeTieBreaker = trackedShapes != null && trackedShapes.WasConsulted;
                string blobId = usedBlendShapeTieBreaker
                    ? CsrVertexMapFactory.ComputeCsrBlobId(csr)
                    : m_CacheStore.StoreResolution(
                        resolutionKey,
                        csr,
                        mapped.MatchedScale,
                        mapped.MatchedBakeAxisConversion,
                        writeToDisk: request.AllowDiskCache);

                var spaceContract = BuildPerUseSpaceContract(
                    scene.Metadata,
                    mapped.MatchedScale,
                    mapped.MatchedBakeAxisConversion,
                    nodePath);

                return BindingResolveResult.Success(new ResolvedVertexBinding(
                    csr,
                    spaceContract,
                    BindingSourceKind.FbxInverseMapComputed,
                    blobId,
                    cacheHit: false,
                    diagnostic: mapped.Diagnostic));
            }
            finally
            {
                ownedScene?.Dispose();
            }
        }

        private static OriginSpaceContract BuildPerUseSpaceContract(
            FbxSceneMetadata targetSceneMetadata,
            float matchedScale,
            bool matchedBakeAxisConversion,
            string targetIdentity)
        {
            var canonicalToUnity = UnitySpaceContractMath.BuildCanonicalToUnityMatrix(
                targetSceneMetadata,
                matchedScale,
                matchedBakeAxisConversion,
                out _);

            return new OriginSpaceContract
            {
                FrameVersion = MeshSpaceProvenance.CanonicalMeshLocalV1,
                BindingSpaceVersion = 1,
                TargetIdentity = targetIdentity ?? string.Empty,
                CanonicalToUnityMeshLocal = canonicalToUnity
            };
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
            FbxUnityImportSettings importSettings)
        {
            using (var sha = SHA256.Create())
            using (var cs = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write))
            using (var bw = new BinaryWriter(cs, Encoding.UTF8, leaveOpen: true))
            {
                bw.Write(k_ResolutionKeyDomain);
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
