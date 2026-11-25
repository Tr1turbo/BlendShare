using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Autodesk.Fbx;
using Triturbo.BlendShare.Container.Format;
using Triturbo.BlendShare.Container.Serialization;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Fbx.Ufbx;
using Triturbo.BlendShare.Model.Documents;
using Triturbo.BlendShare.Model.Features;
using Triturbo.BlendShare.Model.Serialization;
using Triturbo.BlendShare.Model.Validation;
using Triturbo.BlendShare.Pipeline.Export;

[assembly: RegisterBlendShareFbxExportBackend(typeof(Triturbo.BlendShare.FbxExport.FbxSdkPatchExportBackend))]

namespace Triturbo.BlendShare.FbxExport
{
    /// <summary>
    /// Transactional Autodesk FBX SDK export backend implementing <see cref="IFbxPatchExportBackend"/>.
    /// Supports multi-patch single-file single-transaction export with joint Preflight,
    /// shared SDK mesh conflict detection, post-export ufbx numeric/channel round-trip verification,
    /// external source-file modification protection, and atomic backup + replacement.
    /// </summary>
    public sealed class FbxSdkPatchExportBackend : IFbxPatchExportBackend
    {
        public const string DefaultBackendId = "autodesk.fbx-sdk";

        private readonly FeatureFbxWriterRegistry m_WriterRegistry;
        private readonly BlendShareContainerReader m_ContainerReader;

        public string BackendId => DefaultBackendId;
        public string DisplayName => "Autodesk FBX SDK Exporter";

        /// <summary>
        /// Internal test hook invoked immediately before the second source-file generation check
        /// and atomic file replacement.
        /// </summary>
        internal Action OnBeforeAtomicReplaceForTesting { get; set; }

        public FbxSdkPatchExportBackend()
            : this(FeatureFbxWriterRegistry.Default)
        {
        }

        public FbxSdkPatchExportBackend(FeatureFbxWriterRegistry writerRegistry)
        {
            m_WriterRegistry = writerRegistry ?? FeatureFbxWriterRegistry.Default;
            m_ContainerReader = new BlendShareContainerReader(BlendShareSerializer.Default);
        }

        public FbxPatchExportPreflightResult Preflight(FbxPatchExportRequest request)
        {
            var diagnostics = new List<FbxExportDiagnostic>();
            if (!ValidateBasicRequest(request, diagnostics))
            {
                return new FbxPatchExportPreflightResult(false, diagnostics, Array.Empty<string>(), null);
            }

            string targetPath = Path.GetFullPath(request.TargetFbxPath);
            string token = ComputeSourceFileGenerationToken(targetPath, diagnostics);
            if (token == null)
            {
                return new FbxPatchExportPreflightResult(false, diagnostics, Array.Empty<string>(), null);
            }

            PayloadStore ownedStore = null;
            IFbxScene readerScene = null;
            FbxManager sdkManager = null;
            try
            {
                PayloadStore payloadStore = ResolvePayloadStore(request, out ownedStore);
                if (!LoadScenes(targetPath, diagnostics, out readerScene, out sdkManager, out var sdkScene))
                {
                    return new FbxPatchExportPreflightResult(false, diagnostics, Array.Empty<string>(), token);
                }

                if (!PrepareTransaction(
                        request,
                        readerScene,
                        sdkManager,
                        sdkScene,
                        payloadStore,
                        diagnostics,
                        out var plan))
                {
                    return new FbxPatchExportPreflightResult(false, diagnostics, Array.Empty<string>(), token);
                }

                bool preflightOk = RunWriterPreflights(plan, diagnostics);
                return new FbxPatchExportPreflightResult(
                    preflightOk,
                    diagnostics,
                    plan.AffectedNodePaths,
                    token);
            }
            catch (Exception ex)
            {
                diagnostics.Add(new FbxExportDiagnostic(
                    FbxExportDiagnosticSeverity.Error,
                    "FBX_EXPORT_PREFLIGHT_EXCEPTION",
                    ex.Message));
                return new FbxPatchExportPreflightResult(false, diagnostics, Array.Empty<string>(), token);
            }
            finally
            {
                sdkManager?.Destroy();
                readerScene?.Dispose();
                ownedStore?.Dispose();
            }
        }

        public FbxPatchExportResult Export(FbxPatchExportRequest request)
        {
            var diagnostics = new List<FbxExportDiagnostic>();
            if (!ValidateBasicRequest(request, diagnostics))
            {
                return new FbxPatchExportResult(false, null, diagnostics, 0, 0);
            }

            string targetPath = Path.GetFullPath(request.TargetFbxPath);
            string initialToken = ComputeSourceFileGenerationToken(targetPath, diagnostics);
            if (initialToken == null)
            {
                return new FbxPatchExportResult(false, null, diagnostics, 0, 0);
            }

            PayloadStore ownedStore = null;
            IFbxScene readerScene = null;
            FbxManager sdkManager = null;
            string tempFbxPath = null;
            string transientOldBackupPath = null;

            try
            {
                PayloadStore payloadStore = ResolvePayloadStore(request, out ownedStore);
                if (!LoadScenes(targetPath, diagnostics, out readerScene, out sdkManager, out var sdkScene))
                {
                    return new FbxPatchExportResult(false, null, diagnostics, 0, 0);
                }

                if (!PrepareTransaction(
                        request,
                        readerScene,
                        sdkManager,
                        sdkScene,
                        payloadStore,
                        diagnostics,
                        out var plan))
                {
                    return new FbxPatchExportResult(false, null, diagnostics, 0, 0);
                }

                if (!RunWriterPreflights(plan, diagnostics))
                {
                    return new FbxPatchExportResult(false, null, diagnostics, 0, 0);
                }

                // Execute all writers across all targeted meshes in ExecutionOrder.
                var modifiedNodePaths = new HashSet<string>(StringComparer.Ordinal);
                var appliedPatchIds = new HashSet<string>(StringComparer.Ordinal);
                var writers = m_WriterRegistry.GetAllOrdered();

                for (int m = 0; m < plan.MeshPlans.Count; m++)
                {
                    var meshPlan = plan.MeshPlans[m];
                    for (int w = 0; w < writers.Count; w++)
                    {
                        var writer = writers[w];
                        if (!meshPlan.InputsByFeatureType.TryGetValue(writer.FeatureTypeId, out var writerInputs) ||
                            writerInputs.Count == 0)
                        {
                            continue;
                        }

                        var writeResult = writer.Write(meshPlan.ExportContext, writerInputs);
                        AddDiagnostics(diagnostics, writeResult.Diagnostics);
                        if (writeResult.Status == FbxFeatureWriteStatus.Failed)
                        {
                            if (writeResult.Diagnostics.Count == 0)
                            {
                                diagnostics.Add(new FbxExportDiagnostic(
                                    FbxExportDiagnosticSeverity.Error,
                                    "FBX_EXPORT_WRITER_FAILED",
                                    $"FBX writer '{writer.FeatureTypeId}' failed for mesh '{meshPlan.TargetNodePath}'.",
                                    meshId: meshPlan.TargetNodePath,
                                    featureTypeId: writer.FeatureTypeId));
                            }

                            return new FbxPatchExportResult(false, null, diagnostics, 0, 0);
                        }

                        if (writeResult.ModifiedMesh)
                        {
                            modifiedNodePaths.Add(meshPlan.TargetNodePath);
                            for (int i = 0; i < writerInputs.Count; i++)
                            {
                                appliedPatchIds.Add(writerInputs[i].PatchItem.PatchId);
                            }
                        }
                    }
                }

                // Export modified SDK scene to a same-directory temp file for atomic replacement.
                string targetDir = Path.GetDirectoryName(targetPath) ?? string.Empty;
                string targetFileName = Path.GetFileName(targetPath);
                tempFbxPath = Path.Combine(
                    targetDir,
                    $".{targetFileName}.blendshare-tmp-{Guid.NewGuid():N}.fbx");

                if (!ExportSdkSceneToFile(sdkManager, sdkScene, tempFbxPath, diagnostics))
                {
                    return new FbxPatchExportResult(false, null, diagnostics, 0, 0);
                }

                // Release SDK manager before reopening and replacing to ensure all file handles are closed.
                sdkManager.Destroy();
                sdkManager = null;

                // Round-trip verification via ufbx (channels + numeric position/normal/skin checks).
                if (!VerifyTempExportFile(tempFbxPath, readerScene, plan, request.VerifyRoundTripNumerics, diagnostics))
                {
                    return new FbxPatchExportResult(false, null, diagnostics, 0, 0);
                }

                // Close readerScene before atomic file replace on Windows/macOS.
                readerScene.Dispose();
                readerScene = null;

                // Test hook for simulating concurrent external edits.
                OnBeforeAtomicReplaceForTesting?.Invoke();

                // Second generation check + atomic publish under a per-target publish lock,
                // followed by verifying the displaced backup file's SHA-256 matches initialToken
                // (rolling back immediately if a concurrent writer modified targetPath in the OS race window).
                string publishedBackupPath = null;
                string lockPath = Path.Combine(targetDir, $".{targetFileName}.blendshare.lock");
                using (var publishLock = AcquirePublishLock(lockPath, diagnostics))
                {
                    if (publishLock == null)
                    {
                        return new FbxPatchExportResult(false, null, diagnostics, 0, 0);
                    }

                    string currentToken = ComputeSourceFileGenerationToken(targetPath, diagnostics);
                    if (currentToken == null || !string.Equals(currentToken, initialToken, StringComparison.Ordinal))
                    {
                        diagnostics.Add(new FbxExportDiagnostic(
                            FbxExportDiagnosticSeverity.Error,
                            "FBX_EXPORT_SOURCE_MODIFIED",
                            $"Source FBX file '{targetPath}' was modified externally during export; aborting without overwriting."));
                        return new FbxPatchExportResult(false, null, diagnostics, 0, 0);
                    }

                    string swapBackupPath;
                    if (request.CreateBackup)
                    {
                        publishedBackupPath = ResolveNonClobberingBackupPath(targetPath);
                        swapBackupPath = publishedBackupPath;
                    }
                    else
                    {
                        transientOldBackupPath = Path.Combine(
                            targetDir,
                            $".{targetFileName}.blendshare-old-{Guid.NewGuid():N}.bak");
                        swapBackupPath = transientOldBackupPath;
                    }

                    File.Replace(tempFbxPath, targetPath, swapBackupPath, ignoreMetadataErrors: true);
                    tempFbxPath = null;

                    // Post-swap proof: the displaced file must have the exact initial SHA-256 hash.
                    string displacedHash = ComputeFileSha256(swapBackupPath);
                    string expectedHash = ExtractSha256FromToken(initialToken);
                    if (!string.Equals(displacedHash, expectedHash, StringComparison.Ordinal))
                    {
                        // Concurrent external write slipped in between check and replace: restore it immediately!
                        File.Replace(swapBackupPath, targetPath, null, ignoreMetadataErrors: true);
                        transientOldBackupPath = null;
                        diagnostics.Add(new FbxExportDiagnostic(
                            FbxExportDiagnosticSeverity.Error,
                            "FBX_EXPORT_SOURCE_MODIFIED",
                            $"Source FBX file '{targetPath}' was modified concurrently during atomic replace; restored external file and aborted."));
                        return new FbxPatchExportResult(false, null, diagnostics, 0, 0);
                    }
                }

                return new FbxPatchExportResult(
                    true,
                    publishedBackupPath,
                    diagnostics,
                    appliedPatchIds.Count,
                    modifiedNodePaths.Count);
            }
            catch (Exception ex)
            {
                diagnostics.Add(new FbxExportDiagnostic(
                    FbxExportDiagnosticSeverity.Error,
                    "FBX_EXPORT_EXCEPTION",
                    ex.Message));
                return new FbxPatchExportResult(false, null, diagnostics, 0, 0);
            }
            finally
            {
                sdkManager?.Destroy();
                readerScene?.Dispose();
                ownedStore?.Dispose();
                TryDeleteFileQuietly(tempFbxPath);
                TryDeleteFileQuietly(transientOldBackupPath);
            }
        }

        private static bool ValidateBasicRequest(FbxPatchExportRequest request, List<FbxExportDiagnostic> diagnostics)
        {
            if (request == null ||
                string.IsNullOrWhiteSpace(request.TargetFbxPath) ||
                request.Patches == null ||
                request.Patches.Count == 0 ||
                request.Patches.Any(p => p == null) ||
                !(request.NumericVerificationToleranceMeters >= 0d) ||
                double.IsInfinity(request.NumericVerificationToleranceMeters))
            {
                diagnostics.Add(new FbxExportDiagnostic(
                    FbxExportDiagnosticSeverity.Error,
                    "FBX_EXPORT_INVALID_REQUEST",
                    "Invalid FBX export request."));
                return false;
            }

            if (!File.Exists(request.TargetFbxPath))
            {
                diagnostics.Add(new FbxExportDiagnostic(
                    FbxExportDiagnosticSeverity.Error,
                    "FBX_EXPORT_TARGET_NOT_FOUND",
                    $"Target FBX file not found: '{request.TargetFbxPath}'."));
                return false;
            }

            return true;
        }

        private static string ComputeSourceFileGenerationToken(string path, List<FbxExportDiagnostic> diagnostics)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    diagnostics.Add(new FbxExportDiagnostic(
                        FbxExportDiagnosticSeverity.Error,
                        "FBX_EXPORT_TARGET_NOT_FOUND",
                        $"Target FBX file not found: '{path}'."));
                    return null;
                }

                using var sha = SHA256.Create();
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                byte[] hash = sha.ComputeHash(stream);
                string hex = BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
                return $"{info.Length}:{info.LastWriteTimeUtc.Ticks}:{hex}";
            }
            catch (Exception ex)
            {
                diagnostics.Add(new FbxExportDiagnostic(
                    FbxExportDiagnosticSeverity.Error,
                    "FBX_EXPORT_TARGET_READ_FAILED",
                    $"Failed reading source FBX '{path}': {ex.Message}"));
                return null;
            }
        }

        private static PayloadStore ResolvePayloadStore(FbxPatchExportRequest request, out PayloadStore ownedStore)
        {
            if (request.PayloadStore != null)
            {
                ownedStore = null;
                return request.PayloadStore;
            }

            var provider = new ExportBatchStreamProvider(request.Patches);
            ownedStore = new PayloadStore(provider);
            return ownedStore;
        }

        private static bool LoadScenes(
            string targetPath,
            List<FbxExportDiagnostic> diagnostics,
            out IFbxScene readerScene,
            out FbxManager sdkManager,
            out FbxScene sdkScene)
        {
            readerScene = null;
            sdkManager = null;
            sdkScene = null;

            var ufbxResult = UfbxScene.TryLoad(targetPath);
            if (!ufbxResult.Success || ufbxResult.Value == null)
            {
                diagnostics.Add(new FbxExportDiagnostic(
                    FbxExportDiagnosticSeverity.Error,
                    "FBX_EXPORT_UFBX_LOAD_FAILED",
                    $"Failed to load target FBX with ufbx: {ufbxResult.Message}"));
                return false;
            }

            readerScene = ufbxResult.Value;

            FbxImporter importer = null;
            try
            {
                sdkManager = FbxManager.Create();
                var ios = FbxIOSettings.Create(sdkManager, Globals.IOSROOT);
                sdkManager.SetIOSettings(ios);
                sdkScene = FbxScene.Create(sdkManager, Path.GetFileNameWithoutExtension(targetPath));
                importer = FbxImporter.Create(sdkManager, string.Empty);

                if (!importer.Initialize(targetPath, -1, sdkManager.GetIOSettings()))
                {
                    diagnostics.Add(new FbxExportDiagnostic(
                        FbxExportDiagnosticSeverity.Error,
                        "FBX_EXPORT_SDK_IMPORT_INIT_FAILED",
                        $"Autodesk FBX SDK failed to initialize importer for '{targetPath}'."));
                    return false;
                }

                if (!importer.Import(sdkScene))
                {
                    diagnostics.Add(new FbxExportDiagnostic(
                        FbxExportDiagnosticSeverity.Error,
                        "FBX_EXPORT_SDK_IMPORT_FAILED",
                        $"Autodesk FBX SDK failed to import scene '{targetPath}'."));
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                diagnostics.Add(new FbxExportDiagnostic(
                    FbxExportDiagnosticSeverity.Error,
                    "FBX_EXPORT_SDK_LOAD_EXCEPTION",
                    ex.Message));
                return false;
            }
            finally
            {
                importer?.Destroy();
            }
        }

        private bool PrepareTransaction(
            FbxPatchExportRequest request,
            IFbxScene readerScene,
            FbxManager sdkManager,
            FbxScene sdkScene,
            PayloadStore payloadStore,
            List<FbxExportDiagnostic> diagnostics,
            out ExportTransactionPlan plan)
        {
            plan = null;

            var target = readerScene.Metadata;
            if (!CanonicalMeshSpace.TryBuildSpaceToSpace(target.Axes, target.UnitMeters,
                    CanonicalMeshSpace.CanonicalAxes, 1d, out var rawToCanonical, out _) ||
                !FbxAffineSpaceTransform.TryCreate(rawToCanonical, out var targetRawToCanonical) ||
                !FbxAffineSpaceTransform.TryCreate(targetRawToCanonical.InverseOffset, out var canonicalToTargetRaw))
            {
                diagnostics.Add(new FbxExportDiagnostic(
                    FbxExportDiagnosticSeverity.Error,
                    "FBX_EXPORT_SINGULAR_TARGET_SPACE",
                    "Invalid target FBX axes, unit scale, or coordinate-space transform."));
                return false;
            }

            FbxNode rootNode = sdkScene.GetRootNode();
            if (rootNode == null)
            {
                diagnostics.Add(new FbxExportDiagnostic(
                    FbxExportDiagnosticSeverity.Error,
                    "FBX_EXPORT_NO_ROOT_NODE",
                    "Target FBX SDK scene has no root node."));
                return false;
            }

            var sessionState = new Dictionary<string, object>(StringComparer.Ordinal);
            var meshPlanByTargetPath = new Dictionary<string, ExportMeshPlan>(StringComparer.Ordinal);
            var orderedMeshPlans = new List<ExportMeshPlan>();
            var seenSdkMeshes = new List<(FbxMesh Mesh, string TargetPath)>();
            var readerDigestCache = new Dictionary<string, FbxTopologyDigest>(StringComparer.Ordinal);

            bool hasErrors = false;

            for (int p = 0; p < request.Patches.Count; p++)
            {
                var patchItem = request.Patches[p];
                if (!File.Exists(patchItem.SourceFilePath))
                {
                    diagnostics.Add(new FbxExportDiagnostic(
                        FbxExportDiagnosticSeverity.Error,
                        "FBX_EXPORT_PATCH_FILE_NOT_FOUND",
                        $"Patch file not found: '{patchItem.SourceFilePath}'.",
                        patchId: patchItem.PatchId));
                    hasErrors = true;
                    continue;
                }

                using var patchStream = new FileStream(
                    patchItem.SourceFilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read);

                var skeletonResult = m_ContainerReader.ReadSkeleton(patchStream, patchItem.Toc);
                if (!skeletonResult.Success)
                {
                    diagnostics.Add(new FbxExportDiagnostic(
                        FbxExportDiagnosticSeverity.Error,
                        "FBX_EXPORT_CONTAINER_READ_FAILED",
                        $"Failed reading skeleton from patch '{patchItem.PatchId}': {skeletonResult.Message}",
                        patchId: patchItem.PatchId));
                    hasErrors = true;
                    continue;
                }

                SkeletonDocument skeleton = skeletonResult.Value;

                if (!FbxExportSpace.TryCreate(patchItem.Toc.SpaceProvenance, target, out var patchSpace))
                {
                    diagnostics.Add(new FbxExportDiagnostic(FbxExportDiagnosticSeverity.Error, "FBX_EXPORT_PATCH_SPACE",
                        $"Invalid stored frame, axes or unit for patch '{patchItem.PatchId}'.", patchId: patchItem.PatchId));
                    return false;
                }

                for (int m = 0; m < patchItem.Toc.Meshes.Count; m++)
                {
                    var meshToc = patchItem.Toc.Meshes[m];
                    var meshHeaderRes = m_ContainerReader.ReadMeshHeader(patchStream, patchItem.Toc, meshToc.MeshId);
                    if (!meshHeaderRes.Success || meshHeaderRes.Value == null)
                    {
                        diagnostics.Add(new FbxExportDiagnostic(
                            FbxExportDiagnosticSeverity.Error,
                            "FBX_EXPORT_CONTAINER_READ_FAILED",
                            $"Failed reading mesh header '{meshToc.MeshId}': {meshHeaderRes.Message}",
                            patchId: patchItem.PatchId,
                            meshId: meshToc.MeshId));
                        hasErrors = true;
                        continue;
                    }

                    MeshDocument meshHeader = meshHeaderRes.Value;

                    string targetNodePath = ResolveTargetNodePath(patchItem, meshHeader);

                    // Resolve target node in both SDK scene and ufbx reader scene.
                    FbxNode sdkNode = FbxSdkNodeUtility.FindNodeByPath(rootNode, targetNodePath);
                    FbxMesh sdkMesh = sdkNode?.GetMesh();
                    IFbxMesh readerMesh = FindReaderMeshByNodePath(readerScene, targetNodePath);

                    if (sdkNode == null || sdkMesh == null || readerMesh == null)
                    {
                        diagnostics.Add(new FbxExportDiagnostic(
                            FbxExportDiagnosticSeverity.Error,
                            "FBX_EXPORT_MESH_NOT_FOUND",
                            $"Target FBX mesh node '{targetNodePath}' was not found in '{request.TargetFbxPath}'.",
                            patchId: patchItem.PatchId,
                            meshId: meshHeader.MeshId));
                        hasErrors = true;
                        continue;
                    }

                    // Shared SDK mesh conflict detection:
                    // 1) Two distinct targeted node paths alias the same FbxMesh attribute.
                    // 2) Target FbxMesh is instanced across multiple nodes in the SDK scene.
                    string conflictingTargetPath = null;
                    for (int s = 0; s < seenSdkMeshes.Count; s++)
                    {
                        if (seenSdkMeshes[s].Mesh.Equals(sdkMesh) &&
                            !string.Equals(seenSdkMeshes[s].TargetPath, targetNodePath, StringComparison.Ordinal))
                        {
                            conflictingTargetPath = seenSdkMeshes[s].TargetPath;
                            break;
                        }
                    }

                    if (conflictingTargetPath != null)
                    {
                        diagnostics.Add(new FbxExportDiagnostic(
                            FbxExportDiagnosticSeverity.Error,
                            "FBX_EXPORT_SHARED_MESH_CONFLICT",
                            $"Target nodes '{conflictingTargetPath}' and '{targetNodePath}' share the same underlying FBX SDK mesh attribute.",
                            patchId: patchItem.PatchId,
                            meshId: meshHeader.MeshId));
                        hasErrors = true;
                        continue;
                    }

                    if (sdkMesh.GetNodeCount() > 1)
                    {
                        diagnostics.Add(new FbxExportDiagnostic(
                            FbxExportDiagnosticSeverity.Error,
                            "FBX_EXPORT_SHARED_MESH_CONFLICT",
                            $"Target node '{targetNodePath}' shares its underlying FBX SDK mesh attribute with {sdkMesh.GetNodeCount() - 1} other scene node(s).",
                            patchId: patchItem.PatchId,
                            meshId: meshHeader.MeshId));
                        hasErrors = true;
                        continue;
                    }

                    seenSdkMeshes.Add((sdkMesh, targetNodePath));

                    // Verify control-point count matches between SDK and ufbx, and check patch topology compatibility.
                    if (sdkMesh.GetControlPointsCount() != readerMesh.ControlPointCount)
                    {
                        diagnostics.Add(new FbxExportDiagnostic(
                            FbxExportDiagnosticSeverity.Error,
                            "FBX_EXPORT_SDK_UFBX_MISMATCH",
                            $"SDK control point count ({sdkMesh.GetControlPointsCount()}) does not match ufbx ({readerMesh.ControlPointCount}) for '{targetNodePath}'.",
                            patchId: patchItem.PatchId,
                            meshId: meshHeader.MeshId));
                        hasErrors = true;
                        continue;
                    }

                    if (!ValidateTopologyCompatibility(meshHeader, readerMesh, targetNodePath, readerDigestCache, patchItem.PatchId, diagnostics))
                    {
                        hasErrors = true;
                        continue;
                    }

                    if (!meshPlanByTargetPath.TryGetValue(targetNodePath, out var meshPlan))
                    {
                        var exportContext = new FbxExportMeshContext(
                            sdkManager,
                            sdkScene,
                            rootNode,
                            sdkNode,
                            sdkMesh,
                            readerScene,
                            readerMesh,
                            targetNodePath,
                            canonicalToTargetRaw,
                            targetRawToCanonical,
                            request.NumericVerificationToleranceMeters,
                            sessionState,
                            diagnostics);

                        meshPlan = new ExportMeshPlan(targetNodePath, exportContext);
                        meshPlanByTargetPath.Add(targetNodePath, meshPlan);
                        orderedMeshPlans.Add(meshPlan);
                    }

                    // Read & validate all features for this mesh.
                    for (int f = 0; f < meshToc.Features.Count; f++)
                    {
                        var featToc = meshToc.Features[f];
                        var featEntry = featToc.Entry;
                        string typeId = featEntry.TypeId;

                        BlendShareFeatureAttribute featureAttr = null;
                        bool hasSchema = FeatureSchemaRegistry.Default.TryGetFeatureType(typeId, out var featureClrType) &&
                                         FeatureSchemaRegistry.Default.TryGetFeatureAttribute(featureClrType, out featureAttr);
                        bool versionSupported = hasSchema && featEntry.SchemaVersion <= featureAttr.SchemaVersion;
                        bool hasWriter = m_WriterRegistry.TryGet(typeId, out _);

                        if (!hasSchema || !versionSupported || !hasWriter)
                        {
                            if (featEntry.Required)
                            {
                                diagnostics.Add(new FbxExportDiagnostic(
                                    FbxExportDiagnosticSeverity.Error,
                                    "FBX_EXPORT_UNSUPPORTED_REQUIRED_FEATURE",
                                    $"Required feature '{featToc.FeatureId}' (type '{typeId}', schema v{featEntry.SchemaVersion}) is not supported by this FBX exporter.",
                                    patchId: patchItem.PatchId,
                                    meshId: meshHeader.MeshId,
                                    featureTypeId: typeId));
                                hasErrors = true;
                            }
                            else
                            {
                                diagnostics.Add(new FbxExportDiagnostic(
                                    FbxExportDiagnosticSeverity.Warning,
                                    "FBX_EXPORT_UNSUPPORTED_OPTIONAL_FEATURE",
                                    $"Skipping optional unsupported feature '{featToc.FeatureId}' (type '{typeId}', schema v{featEntry.SchemaVersion}).",
                                    patchId: patchItem.PatchId,
                                    meshId: meshHeader.MeshId,
                                    featureTypeId: typeId));
                            }

                            continue;
                        }

                        var featRes = m_ContainerReader.ReadFeature(patchStream, patchItem.Toc, meshToc.MeshId, featToc.FeatureId);
                        if (!featRes.Success || featRes.Value == null)
                        {
                            diagnostics.Add(new FbxExportDiagnostic(
                                FbxExportDiagnosticSeverity.Error,
                                "FBX_EXPORT_CONTAINER_READ_FAILED",
                                $"Failed reading feature '{featToc.FeatureId}' on mesh '{meshToc.MeshId}': {featRes.Message}",
                                patchId: patchItem.PatchId,
                                meshId: meshHeader.MeshId,
                                featureTypeId: typeId));
                            hasErrors = true;
                            continue;
                        }

                        var featureData = featRes.Value;
                        if (FeatureValidatorRegistry.Default.TryGetValidator(typeId, out var validator))
                        {
                            var validationContext = new FeatureValidationContext(
                                meshHeader.MeshId,
                                readerMesh.ControlPointCount,
                                readerMesh.FaceCornerCount,
                                payloadStore,
                                validatePayloads: true);
                            var validation = new FeatureValidationResult();
                            validator.Validate(featureData, validationContext, validation);
                            if (!validation.IsValid)
                            {
                                string msg = validation.Issues.Count > 0
                                    ? string.Join("; ", validation.Issues.Select(i => i.Message))
                                    : "Feature semantic validation failed.";
                                diagnostics.Add(new FbxExportDiagnostic(
                                    FbxExportDiagnosticSeverity.Error,
                                    "FBX_EXPORT_FEATURE_VALIDATION_FAILED",
                                    msg,
                                    patchId: patchItem.PatchId,
                                    meshId: meshHeader.MeshId,
                                    featureTypeId: typeId));
                                hasErrors = true;
                                continue;
                            }
                        }

                        var input = new FbxFeatureWriteInput(
                            patchItem,
                            meshHeader,
                            skeleton,
                            featureData,
                            payloadStore,
                            targetNodePath,
                            patchSpace);
                        meshPlan.AddInput(typeId, input);
                    }
                }
            }

            if (hasErrors)
            {
                return false;
            }

            plan = new ExportTransactionPlan(
                orderedMeshPlans,
                canonicalToTargetRaw,
                targetRawToCanonical,
                sessionState,
                request.NumericVerificationToleranceMeters);
            return true;
        }

        private bool RunWriterPreflights(ExportTransactionPlan plan, List<FbxExportDiagnostic> diagnostics)
        {
            var writers = m_WriterRegistry.GetAllOrdered();
            bool ok = true;

            for (int m = 0; m < plan.MeshPlans.Count; m++)
            {
                var meshPlan = plan.MeshPlans[m];
                for (int w = 0; w < writers.Count; w++)
                {
                    var writer = writers[w];
                    if (!meshPlan.InputsByFeatureType.TryGetValue(writer.FeatureTypeId, out var writerInputs) ||
                        writerInputs.Count == 0)
                    {
                        continue;
                    }

                    var preflightResult = writer.Preflight(meshPlan.ExportContext, writerInputs);
                    AddDiagnostics(diagnostics, preflightResult.Diagnostics);
                    if (preflightResult.Status == FbxFeatureWriteStatus.Failed)
                    {
                        if (preflightResult.Diagnostics.Count == 0)
                        {
                            diagnostics.Add(new FbxExportDiagnostic(
                                FbxExportDiagnosticSeverity.Error,
                                "FBX_EXPORT_WRITER_PREFLIGHT_FAILED",
                                $"Preflight failed for feature '{writer.FeatureTypeId}' on mesh '{meshPlan.TargetNodePath}'.",
                                meshId: meshPlan.TargetNodePath,
                                featureTypeId: writer.FeatureTypeId));
                        }

                        ok = false;
                    }
                }
            }

            return ok;
        }

        private bool VerifyTempExportFile(
            string tempFbxPath,
            IFbxScene originalReaderScene,
            ExportTransactionPlan plan,
            bool verifyNumerics,
            List<FbxExportDiagnostic> diagnostics)
        {
            var loadRes = UfbxScene.TryLoad(tempFbxPath);
            if (!loadRes.Success || loadRes.Value == null)
            {
                diagnostics.Add(new FbxExportDiagnostic(
                    FbxExportDiagnosticSeverity.Error,
                    "FBX_EXPORT_VERIFY_RELOAD_FAILED",
                    $"Failed to reopen exported temporary FBX with ufbx: {loadRes.Message}"));
                return false;
            }

            using var verifiedScene = loadRes.Value;
            if (!verifiedScene.Metadata.Axes.Equals(originalReaderScene.Metadata.Axes) ||
                verifiedScene.Metadata.UnitMeters != originalReaderScene.Metadata.UnitMeters)
            {
                diagnostics.Add(new FbxExportDiagnostic(FbxExportDiagnosticSeverity.Error, "FBX_EXPORT_VERIFY_SPACE_MISMATCH",
                    "Export changed the target FBX axes or unit scale."));
                return false;
            }
            var writers = m_WriterRegistry.GetAllOrdered();

            for (int m = 0; m < plan.MeshPlans.Count; m++)
            {
                var meshPlan = plan.MeshPlans[m];
                IFbxMesh verifiedMesh = FindReaderMeshByNodePath(verifiedScene, meshPlan.TargetNodePath);
                if (verifiedMesh == null)
                {
                    diagnostics.Add(new FbxExportDiagnostic(
                        FbxExportDiagnosticSeverity.Error,
                        "FBX_EXPORT_VERIFY_MESH_MISSING",
                        $"Exported FBX is missing target mesh node '{meshPlan.TargetNodePath}'.",
                        meshId: meshPlan.TargetNodePath));
                    return false;
                }

                IFbxMesh originalMesh = meshPlan.ExportContext.ReaderMesh;
                var origDigest = FbxTopologyUtility.ComputeDigest(originalMesh);
                var verDigest = FbxTopologyUtility.ComputeDigest(verifiedMesh);
                if (verifiedMesh.ControlPointCount != originalMesh.ControlPointCount ||
                    verifiedMesh.FaceCount != originalMesh.FaceCount ||
                    !origDigest.IsValid || !verDigest.IsValid ||
                    !string.Equals(origDigest.Hash, verDigest.Hash, StringComparison.Ordinal))
                {
                    diagnostics.Add(new FbxExportDiagnostic(
                        FbxExportDiagnosticSeverity.Error,
                        "FBX_EXPORT_VERIFY_TOPOLOGY_CHANGED",
                        $"Exported mesh '{meshPlan.TargetNodePath}' topology changed (CPs {originalMesh.ControlPointCount}->{verifiedMesh.ControlPointCount}, faces {originalMesh.FaceCount}->{verifiedMesh.FaceCount}, digest {origDigest.Hash}->{verDigest.Hash}).",
                        meshId: meshPlan.TargetNodePath));
                    return false;
                }

                var verifyContext = new FbxExportVerifyMeshContext(
                    verifiedScene,
                    verifiedMesh,
                    originalReaderScene,
                    originalMesh,
                    meshPlan.TargetNodePath,
                    plan.CanonicalToTargetRaw,
                    plan.TargetRawToCanonical,
                    plan.NumericVerificationToleranceMeters,
                    plan.SessionState,
                    diagnostics,
                    verifyNumerics);

                for (int w = 0; w < writers.Count; w++)
                {
                    var writer = writers[w];
                    if (!meshPlan.InputsByFeatureType.TryGetValue(writer.FeatureTypeId, out var writerInputs) ||
                        writerInputs.Count == 0)
                    {
                        continue;
                    }

                    var verifyResult = writer.Verify(verifyContext, writerInputs);
                    AddDiagnostics(diagnostics, verifyResult.Diagnostics);
                    if (verifyResult.Status == FbxFeatureWriteStatus.Failed)
                    {
                        if (verifyResult.Diagnostics.Count == 0)
                        {
                            diagnostics.Add(new FbxExportDiagnostic(
                                FbxExportDiagnosticSeverity.Error,
                                "FBX_EXPORT_VERIFY_FAILED",
                                $"Round-trip verification failed for feature '{writer.FeatureTypeId}' on mesh '{meshPlan.TargetNodePath}'.",
                                meshId: meshPlan.TargetNodePath,
                                featureTypeId: writer.FeatureTypeId));
                        }

                        return false;
                    }
                }
            }

            return true;
        }

        private static bool ExportSdkSceneToFile(
            FbxManager manager,
            FbxScene scene,
            string outputPath,
            List<FbxExportDiagnostic> diagnostics)
        {
            FbxExporter exporter = null;
            try
            {
                exporter = FbxExporter.Create(manager, string.Empty);
                int binaryFormat = manager.GetIOPluginRegistry().FindWriterIDByDescription("FBX binary (*.fbx)");
                if (!exporter.Initialize(outputPath, binaryFormat, manager.GetIOSettings()))
                {
                    diagnostics.Add(new FbxExportDiagnostic(
                        FbxExportDiagnosticSeverity.Error,
                        "FBX_EXPORT_SDK_EXPORTER_INIT_FAILED",
                        $"Autodesk FBX SDK exporter failed to initialize for '{outputPath}'."));
                    return false;
                }

                if (!exporter.Export(scene))
                {
                    diagnostics.Add(new FbxExportDiagnostic(
                        FbxExportDiagnosticSeverity.Error,
                        "FBX_EXPORT_SDK_EXPORT_FAILED",
                        $"Autodesk FBX SDK failed exporting scene to '{outputPath}'."));
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                diagnostics.Add(new FbxExportDiagnostic(
                    FbxExportDiagnosticSeverity.Error,
                    "FBX_EXPORT_SDK_EXPORT_EXCEPTION",
                    ex.Message));
                return false;
            }
            finally
            {
                exporter?.Destroy();
            }
        }

        private static string ResolveTargetNodePath(FbxPatchExportItem item, MeshDocument meshHeader)
        {
            if (item.MeshTargetNodeOverrides != null)
            {
                if (!string.IsNullOrEmpty(meshHeader.MeshId) &&
                    item.MeshTargetNodeOverrides.TryGetValue(meshHeader.MeshId, out string byId) &&
                    !string.IsNullOrWhiteSpace(byId))
                {
                    return FbxNameUtility.NormalizePath(byId);
                }

                if (!string.IsNullOrEmpty(meshHeader.NodePath) &&
                    item.MeshTargetNodeOverrides.TryGetValue(meshHeader.NodePath, out string byPath) &&
                    !string.IsNullOrWhiteSpace(byPath))
                {
                    return FbxNameUtility.NormalizePath(byPath);
                }
            }

            string candidate = !string.IsNullOrWhiteSpace(meshHeader.NodePath)
                ? meshHeader.NodePath
                : meshHeader.MeshId;
            return FbxNameUtility.NormalizePath(candidate);
        }

        private static IFbxMesh FindReaderMeshByNodePath(IFbxScene scene, string normalizedPath)
        {
            if (scene == null || string.IsNullOrEmpty(normalizedPath))
            {
                return null;
            }

            for (int i = 0; i < scene.Meshes.Count; i++)
            {
                var mesh = scene.Meshes[i];
                if (mesh == null)
                {
                    continue;
                }

                if (mesh.PrimaryNode != null &&
                    string.Equals(FbxNameUtility.NormalizePath(mesh.PrimaryNode.Path), normalizedPath, StringComparison.Ordinal))
                {
                    return mesh;
                }

                if (mesh.Instances != null)
                {
                    for (int n = 0; n < mesh.Instances.Count; n++)
                    {
                        var inst = mesh.Instances[n];
                        if (inst != null &&
                            string.Equals(FbxNameUtility.NormalizePath(inst.Path), normalizedPath, StringComparison.Ordinal))
                        {
                            return mesh;
                        }
                    }
                }
            }

            return null;
        }

        private static bool ValidateTopologyCompatibility(
            MeshDocument meshHeader,
            IFbxMesh readerMesh,
            string targetNodePath,
            Dictionary<string, FbxTopologyDigest> digestCache,
            string patchId,
            List<FbxExportDiagnostic> diagnostics)
        {
            var expected = meshHeader.Topology;
            if (expected == null || !expected.IsValid || string.IsNullOrEmpty(expected.Hash))
            {
                diagnostics.Add(new FbxExportDiagnostic(
                    FbxExportDiagnosticSeverity.Error,
                    "FBX_EXPORT_MISSING_TOPOLOGY_DIGEST",
                    $"Patch mesh '{meshHeader.MeshId}' is missing a valid topology digest; direct FBX export requires a verified topology digest.",
                    patchId: patchId,
                    meshId: meshHeader.MeshId));
                return false;
            }

            if (expected.ControlPointCount != readerMesh.ControlPointCount ||
                expected.FaceCount != readerMesh.FaceCount)
            {
                diagnostics.Add(new FbxExportDiagnostic(
                    FbxExportDiagnosticSeverity.Error,
                    "FBX_EXPORT_TOPOLOGY_MISMATCH",
                    $"Topology mismatch for '{targetNodePath}': patch expects {expected.ControlPointCount} CPs / {expected.FaceCount} faces, target FBX has {readerMesh.ControlPointCount} CPs / {readerMesh.FaceCount} faces.",
                    patchId: patchId,
                    meshId: meshHeader.MeshId));
                return false;
            }

            if (!digestCache.TryGetValue(targetNodePath, out var actualDigest))
            {
                actualDigest = FbxTopologyUtility.ComputeDigest(readerMesh);
                digestCache[targetNodePath] = actualDigest;
            }

            if (!actualDigest.IsValid || !string.Equals(actualDigest.Hash, expected.Hash, StringComparison.Ordinal))
            {
                diagnostics.Add(new FbxExportDiagnostic(
                    FbxExportDiagnosticSeverity.Error,
                    "FBX_EXPORT_TOPOLOGY_MISMATCH",
                    $"Topology digest mismatch for '{targetNodePath}': patch '{expected.Hash}', target FBX '{actualDigest.Hash}'.",
                    patchId: patchId,
                    meshId: meshHeader.MeshId));
                return false;
            }

            return true;
        }

        private static string ComputeFileSha256(string path)
        {
            using var sha = SHA256.Create();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            byte[] hash = sha.ComputeHash(stream);
            return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static string ExtractSha256FromToken(string token)
        {
            if (string.IsNullOrEmpty(token))
            {
                return string.Empty;
            }

            int lastColon = token.LastIndexOf(':');
            return lastColon >= 0 && lastColon + 1 < token.Length ? token.Substring(lastColon + 1) : string.Empty;
        }

        private static IDisposable AcquirePublishLock(string lockPath, List<FbxExportDiagnostic> diagnostics)
        {
            try
            {
                var stream = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                return new FileLockHandle(lockPath, stream);
            }
            catch (IOException ex)
            {
                diagnostics.Add(new FbxExportDiagnostic(
                    FbxExportDiagnosticSeverity.Error,
                    "FBX_EXPORT_CONCURRENT_PUBLISH",
                    $"Another BlendShare FBX export is currently publishing to this target ({ex.Message})."));
                return null;
            }
        }

        private sealed class FileLockHandle : IDisposable
        {
            private readonly string m_Path;
            private FileStream m_Stream;

            public FileLockHandle(string path, FileStream stream)
            {
                m_Path = path;
                m_Stream = stream;
            }

            public void Dispose()
            {
                if (m_Stream != null)
                {
                    m_Stream.Dispose();
                    m_Stream = null;
                    TryDeleteFileQuietly(m_Path);
                }
            }
        }

        private static string ResolveNonClobberingBackupPath(string targetPath)
        {
            string baseBackup = targetPath + ".blendshare.bak";
            if (!File.Exists(baseBackup))
            {
                return baseBackup;
            }

            string stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            return $"{targetPath}.blendshare.{stamp}.{Guid.NewGuid():N}.bak";
        }

        private static void TryDeleteFileQuietly(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Best-effort cleanup of transient files.
            }
        }

        private static void AddDiagnostics(List<FbxExportDiagnostic> target, IReadOnlyList<FbxExportDiagnostic> source)
        {
            if (source == null)
            {
                return;
            }

            for (int i = 0; i < source.Count; i++)
            {
                target.Add(source[i]);
            }
        }

        private sealed class ExportBatchStreamProvider : IContainerStreamProvider
        {
            private readonly Dictionary<string, (string Path, ContainerToc Toc)> m_Sources =
                new Dictionary<string, (string, ContainerToc)>(StringComparer.Ordinal);

            public ExportBatchStreamProvider(IReadOnlyList<FbxPatchExportItem> items)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    if (item?.Toc != null && !string.IsNullOrEmpty(item.Toc.SourceRevision))
                    {
                        m_Sources[item.Toc.SourceRevision] = (item.SourceFilePath, item.Toc);
                    }
                }
            }

            public Stream OpenRead(string sourceRevision, out ContainerToc toc)
            {
                if (string.IsNullOrEmpty(sourceRevision) || !m_Sources.TryGetValue(sourceRevision, out var entry))
                {
                    throw new FileNotFoundException($"No export source registered for revision '{sourceRevision}'.");
                }

                toc = entry.Toc;
                return new FileStream(entry.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
        }

        private sealed class ExportMeshPlan
        {
            public string TargetNodePath { get; }
            public FbxExportMeshContext ExportContext { get; }
            public Dictionary<string, List<FbxFeatureWriteInput>> InputsByFeatureType { get; } =
                new Dictionary<string, List<FbxFeatureWriteInput>>(StringComparer.Ordinal);

            public ExportMeshPlan(string targetNodePath, FbxExportMeshContext exportContext)
            {
                TargetNodePath = targetNodePath;
                ExportContext = exportContext;
            }

            public void AddInput(string featureTypeId, FbxFeatureWriteInput input)
            {
                if (!InputsByFeatureType.TryGetValue(featureTypeId, out var list))
                {
                    list = new List<FbxFeatureWriteInput>();
                    InputsByFeatureType.Add(featureTypeId, list);
                }

                list.Add(input);
            }
        }

        private sealed class ExportTransactionPlan
        {
            public IReadOnlyList<ExportMeshPlan> MeshPlans { get; }
            public IReadOnlyList<string> AffectedNodePaths { get; }
            public FbxAffineSpaceTransform CanonicalToTargetRaw { get; }
            public FbxAffineSpaceTransform TargetRawToCanonical { get; }
            public IDictionary<string, object> SessionState { get; }
            public double NumericVerificationToleranceMeters { get; }

            public ExportTransactionPlan(
                List<ExportMeshPlan> meshPlans,
                FbxAffineSpaceTransform canonicalToTargetRaw,
                FbxAffineSpaceTransform targetRawToCanonical,
                IDictionary<string, object> sessionState,
                double numericVerificationToleranceMeters)
            {
                MeshPlans = meshPlans;
                AffectedNodePaths = meshPlans.Select(m => m.TargetNodePath).ToArray();
                CanonicalToTargetRaw = canonicalToTargetRaw;
                TargetRawToCanonical = targetRawToCanonical;
                SessionState = sessionState;
                NumericVerificationToleranceMeters = numericVerificationToleranceMeters;
            }
        }
    }
}
