using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Fbx;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Model.Documents;
using Triturbo.BlendShare.Model.Features;
using Triturbo.BlendShare.Pipeline.Export;
using Triturbo.BlendShare.Pipeline.Skeleton;
using Triturbo.BlendShare.Primitives;

namespace Triturbo.BlendShare.FbxExport
{
    /// <summary>
    /// Autodesk FBX SDK writer and ufbx round-trip verifier for <see cref="SkinWeightFeature"/>.
    /// Accumulates signed weight deltas across patches, preserves untouched base weights and cluster bind matrices on weight-only edits,
    /// delegates transaction-wide <see cref="SkeletonDocument"/> solving, node creation/updating, and post-edit <c>D_frame(b)</c>
    /// endpoint basis calibration to <see cref="SkeletonFbxWriter"/>, and verifies weights, link modes, and cluster bind matrices via ufbx round-trip.
    /// </summary>
    public sealed class SkinWeightFbxWriter : IFeatureFbxWriter
    {
        private const string k_ExpectedLinkModesStatePrefix = "fbx-export.expected-link-modes:";
        private const string k_SkinDefaultLinkModeStatePrefix = "fbx-export.skin-default-link-mode:";
        private const string k_TargetSkinDeformerIndexStatePrefix = "fbx-export.skin-deformer-index:";
        private const double k_BindMatrixCompareTolerance = 1e-3d;
        private const double k_MinWeightEpsilon = 1e-6d;

        public string FeatureTypeId => SkinWeightFeature.FeatureTypeId;
        public int ExecutionOrder => 50;

        public void RegisterTransactionInputs(FbxExportMeshContext context, IReadOnlyList<FbxFeatureWriteInput> inputs)
        {
            if (context == null || inputs == null || inputs.Count == 0)
            {
                return;
            }

            var skeletonWriter = SkeletonFbxWriter.GetOrCreate(context.SessionState);
            for (int i = 0; i < inputs.Count; i++)
            {
                var input = inputs[i];
                if (input?.Skeleton != null && input.Skeleton.HasContent)
                {
                    skeletonWriter.Register(input.PatchItem.PatchId, input.PatchSpace, input.Skeleton);
                }
            }
        }

        public FbxFeatureWriteResult Preflight(FbxExportMeshContext context, IReadOnlyList<FbxFeatureWriteInput> inputs)
        {
            var diagnostics = new List<FbxExportDiagnostic>();
            if (context == null || context.TargetMesh == null || context.ReaderMesh == null)
            {
                diagnostics.Add(new FbxExportDiagnostic(
                    FbxExportDiagnosticSeverity.Error,
                    "FBX_EXPORT_SKIN_NO_MESH",
                    "Skin weight FBX preflight requires valid SDK and ufbx meshes.",
                    featureTypeId: FeatureTypeId));
                return FbxFeatureWriteResult.Failed(diagnostics.ToArray());
            }

            int weightedSkinCount = CountWeightedSkins(context.ReaderMesh);
            if (weightedSkinCount > 1)
            {
                diagnostics.Add(new FbxExportDiagnostic(
                    FbxExportDiagnosticSeverity.Error,
                    "FBX_EXPORT_MULTIPLE_SKINS_UNSUPPORTED",
                    $"Target FBX mesh '{context.TargetNodePath}' has {weightedSkinCount} weighted skin deformers; rebuilding multiple skin deformers is not supported.",
                    meshId: context.TargetNodePath,
                    featureTypeId: FeatureTypeId));
                return FbxFeatureWriteResult.Failed(diagnostics.ToArray());
            }

            var primaryPreflightSkin = FbxSdkNodeUtility.FindPrimarySkinDeformer(context.TargetMesh, out _);
            var existingSdkClusters = FbxSdkNodeUtility.GetClustersOfSkin(primaryPreflightSkin);
            FbxCluster.ELinkMode? detectedSkinMode = null;
            for (int c = 0; c < existingSdkClusters.Count; c++)
            {
                var sdkCluster = existingSdkClusters[c];
                if (sdkCluster == null || sdkCluster.GetLink() == null) continue;
                var mode = sdkCluster.GetLinkMode();
                if (mode != FbxCluster.ELinkMode.eNormalize && mode != FbxCluster.ELinkMode.eTotalOne)
                {
                    diagnostics.Add(new FbxExportDiagnostic(
                        FbxExportDiagnosticSeverity.Error,
                        "FBX_EXPORT_UNSUPPORTED_LINK_MODE",
                        $"Target FBX mesh '{context.TargetNodePath}' cluster '{sdkCluster.GetName()}' uses unsupported link mode '{mode}'.",
                        meshId: context.TargetNodePath,
                        featureTypeId: FeatureTypeId));
                    return FbxFeatureWriteResult.Failed(diagnostics.ToArray());
                }

                if (!detectedSkinMode.HasValue)
                {
                    detectedSkinMode = mode;
                }
                else if (detectedSkinMode.Value != mode)
                {
                    diagnostics.Add(new FbxExportDiagnostic(
                        FbxExportDiagnosticSeverity.Error,
                        "FBX_EXPORT_MIXED_LINK_MODES_UNSUPPORTED",
                        $"Target FBX mesh '{context.TargetNodePath}' mixes cluster link modes ('{detectedSkinMode.Value}' and '{mode}').",
                        meshId: context.TargetNodePath,
                        featureTypeId: FeatureTypeId));
                    return FbxFeatureWriteResult.Failed(diagnostics.ToArray());
                }
            }

            var baseSkin = FindPrimarySkin(context.ReaderMesh);
            if (baseSkin != null &&
                !TryReadRawSkinWeightsByCp(baseSkin, context.ReaderMesh.ControlPointCount, out _, out string readErr))
            {
                diagnostics.Add(new FbxExportDiagnostic(
                    FbxExportDiagnosticSeverity.Error,
                    "FBX_EXPORT_ORIGINAL_SKIN_READ_FAILED",
                    readErr,
                    meshId: context.TargetNodePath,
                    featureTypeId: FeatureTypeId));
                return FbxFeatureWriteResult.Failed(diagnostics.ToArray());
            }

            bool anyPatchClusters = false;
            for (int i = 0; i < inputs.Count; i++)
            {
                if (inputs[i].Feature is SkinWeightFeature swCheck &&
                    swCheck.Clusters != null &&
                    swCheck.Clusters.Count > 0)
                {
                    anyPatchClusters = true;
                    break;
                }
            }

            if (anyPatchClusters &&
                baseSkin != null &&
                !TryValidateAndCollectBaseClusterMatrices(
                    baseSkin,
                    context.ReaderScene.Metadata.UnitMeters,
                    context.NumericVerificationToleranceMeters,
                    out _,
                    out string baseClusterDiagCode,
                    out string baseClusterDiagMsg))
            {
                diagnostics.Add(new FbxExportDiagnostic(
                    FbxExportDiagnosticSeverity.Error,
                    baseClusterDiagCode,
                    baseClusterDiagMsg,
                    meshId: context.TargetNodePath,
                    featureTypeId: FeatureTypeId));
                return FbxFeatureWriteResult.Failed(diagnostics.ToArray());
            }

            int cpCount = context.ReaderMesh.ControlPointCount;
            var targetGeomToNode = ResolveTargetMeshGeomToNode(context.ReaderScene, context.ReaderMesh, context.TargetNodePath);
            var skeletonWriter = SkeletonFbxWriter.GetOrCreate(context.SessionState);
            RegisterTransactionInputs(context, inputs);

            bool hasErrors = false;
            if (!skeletonWriter.EnsureSolvedAndValidated(
                    context.RootNode,
                    context.ReaderScene,
                    string.IsNullOrEmpty(context.TargetNodePath) ? null : new[] { context.TargetNodePath },
                    context.NumericVerificationToleranceMeters,
                    diagnostics))
            {
                hasErrors = true;
            }

            var relativeBindPosesByBone = new Dictionary<string, List<(string PatchId, FbxMatrix4x4 RelativeBind)>>(StringComparer.Ordinal);
            FbxCluster.ELinkMode? requestedPatchSkinMode = null;
            string requestedPatchSkinModePatchId = null;

            for (int i = 0; i < inputs.Count; i++)
            {
                var input = inputs[i];
                var feature = input.Feature as SkinWeightFeature;
                if (feature == null)
                {
                    diagnostics.Add(new FbxExportDiagnostic(
                        FbxExportDiagnosticSeverity.Error,
                        "FBX_EXPORT_SKIN_WRONG_TYPE",
                        $"Expected SkinWeightFeature but received '{input.Feature?.GetType().Name}'.",
                        patchId: input.PatchItem.PatchId,
                        meshId: input.MeshHeader.MeshId,
                        featureTypeId: FeatureTypeId));
                    hasErrors = true;
                    continue;
                }

                if (!Enum.IsDefined(typeof(SkinWeightLinkMode), feature.LinkMode))
                {
                    diagnostics.Add(new FbxExportDiagnostic(
                        FbxExportDiagnosticSeverity.Error,
                        "FBX_EXPORT_SKIN_INVALID_LINK_MODE",
                        $"Patch '{input.PatchItem.PatchId}' specifies an invalid skin weight link mode '{feature.LinkMode}'.",
                        patchId: input.PatchItem.PatchId,
                        meshId: input.MeshHeader.MeshId,
                        featureTypeId: FeatureTypeId));
                    hasErrors = true;
                    continue;
                }

                if (feature.Clusters == null || feature.Clusters.Count == 0)
                {
                    continue;
                }

                var patchSdkMode = ToSdkLinkMode(feature.LinkMode);
                if (requestedPatchSkinMode.HasValue && requestedPatchSkinMode.Value != patchSdkMode)
                {
                    diagnostics.Add(new FbxExportDiagnostic(
                        FbxExportDiagnosticSeverity.Error,
                        "FBX_EXPORT_SKIN_LINK_MODE_CONFLICT",
                        $"Conflicting skin weight link modes on '{context.TargetNodePath}' between patch '{requestedPatchSkinModePatchId}' ({requestedPatchSkinMode.Value}) and patch '{input.PatchItem.PatchId}' ({patchSdkMode}).",
                        patchId: input.PatchItem.PatchId,
                        meshId: input.MeshHeader.MeshId,
                        featureTypeId: FeatureTypeId));
                    hasErrors = true;
                }
                else
                {
                    requestedPatchSkinMode = patchSdkMode;
                    requestedPatchSkinModePatchId = input.PatchItem.PatchId;
                }

                if (detectedSkinMode.HasValue && detectedSkinMode.Value != patchSdkMode)
                {
                    diagnostics.Add(new FbxExportDiagnostic(
                        FbxExportDiagnosticSeverity.Error,
                        "FBX_EXPORT_SKIN_LINK_MODE_MISMATCH",
                        $"Target FBX mesh '{context.TargetNodePath}' uses skin link mode '{detectedSkinMode.Value}', which is incompatible with patch '{input.PatchItem.PatchId}' link mode '{patchSdkMode}'.",
                        patchId: input.PatchItem.PatchId,
                        meshId: input.MeshHeader.MeshId,
                        featureTypeId: FeatureTypeId));
                    hasErrors = true;
                }

                for (int c = 0; c < feature.Clusters.Count; c++)
                {
                    var cluster = feature.Clusters[c];
                    string bonePath = FbxNameUtility.NormalizePath(cluster?.BonePath ?? string.Empty);
                    if (string.IsNullOrEmpty(bonePath))
                    {
                        diagnostics.Add(new FbxExportDiagnostic(
                            FbxExportDiagnosticSeverity.Error,
                            "FBX_EXPORT_SKIN_EMPTY_BONE_PATH",
                            "Skin weight cluster has an empty bone path.",
                            patchId: input.PatchItem.PatchId,
                            meshId: input.MeshHeader.MeshId,
                            featureTypeId: FeatureTypeId));
                        hasErrors = true;
                        continue;
                    }

                    if (FbxSdkNodeUtility.FindNodeByPath(context.RootNode, bonePath) == null &&
                        !skeletonWriter.CanCreateNodeHierarchy(context.RootNode, bonePath, out string boneError))
                    {
                        diagnostics.Add(new FbxExportDiagnostic(
                            FbxExportDiagnosticSeverity.Error,
                            "FBX_EXPORT_SKIN_MISSING_BONE",
                            boneError,
                            patchId: input.PatchItem.PatchId,
                            meshId: input.MeshHeader.MeshId,
                            featureTypeId: FeatureTypeId));
                        hasErrors = true;
                    }

                    if (skeletonWriter.TargetRestFrame != null &&
                        !skeletonWriter.TargetRestFrame.TryValidateChain(bonePath, out string restConflictErr))
                    {
                        diagnostics.Add(new FbxExportDiagnostic(
                            FbxExportDiagnosticSeverity.Error,
                            "FBX_EXPORT_CONFLICTING_TARGET_REST",
                            restConflictErr,
                            patchId: input.PatchItem.PatchId,
                            meshId: input.MeshHeader.MeshId,
                            featureTypeId: FeatureTypeId));
                        hasErrors = true;
                    }

                    if (cluster.HasClusterMatrices)
                    {
                        if (!TryComputeCalibratedRawClusterMatrices(
                                cluster,
                                input.Skeleton,
                                skeletonWriter.AlignmentResult,
                                input.PatchSpace,
                                targetGeomToNode,
                                out var rawMeshMat,
                                out var rawLinkMat,
                                out string calibError) ||
                            !FbxAffineSpaceTransform.TryCreate(rawMeshMat, out _) ||
                            !FbxAffineSpaceTransform.TryCreate(rawLinkMat, out var rawLinkXform))
                        {
                            diagnostics.Add(new FbxExportDiagnostic(
                                FbxExportDiagnosticSeverity.Error,
                                "FBX_EXPORT_SKIN_INVALID_MATRIX",
                                calibError ?? $"Skin cluster '{bonePath}' has singular or non-finite bind matrices.",
                                patchId: input.PatchItem.PatchId,
                                meshId: input.MeshHeader.MeshId,
                                featureTypeId: FeatureTypeId));
                            hasErrors = true;
                        }
                        else
                        {
                            var relativeBind = rawMeshMat * rawLinkXform.InverseOffset;
                            if (!relativeBindPosesByBone.TryGetValue(bonePath, out var existingBinds))
                            {
                                existingBinds = new List<(string PatchId, FbxMatrix4x4 RelativeBind)>(2);
                                relativeBindPosesByBone.Add(bonePath, existingBinds);
                            }

                            existingBinds.Add((input.PatchItem.PatchId ?? string.Empty, relativeBind));
                        }
                    }

                    if (cluster.Payload.HasPayload)
                    {
                        using var lease = cluster.Payload.Acquire(input.PayloadSource);
                        var buf = lease.Value?.DeltaWeights;
                        if (buf == null || buf.Domain != IndexDomain.ControlPoint || buf.DomainCount != cpCount)
                        {
                            diagnostics.Add(new FbxExportDiagnostic(
                                FbxExportDiagnosticSeverity.Error,
                                "FBX_EXPORT_SKIN_DOMAIN_MISMATCH",
                                $"Skin cluster '{bonePath}' payload domain does not match target control point count {cpCount}.",
                                patchId: input.PatchItem.PatchId,
                                meshId: input.MeshHeader.MeshId,
                                featureTypeId: FeatureTypeId));
                            hasErrors = true;
                            continue;
                        }

                        for (int k = 0; k < buf.Count; k++)
                        {
                            double delta = buf.Values[k];
                            if (double.IsNaN(delta) || double.IsInfinity(delta) || delta < -1d || delta > 1d)
                            {
                                diagnostics.Add(new FbxExportDiagnostic(
                                    FbxExportDiagnosticSeverity.Error,
                                    "FBX_EXPORT_SKIN_INVALID_WEIGHT",
                                    $"Skin cluster '{bonePath}' contains invalid weight delta {delta} at control point {buf.Indices[k]}.",
                                    patchId: input.PatchItem.PatchId,
                                    meshId: input.MeshHeader.MeshId,
                                    featureTypeId: FeatureTypeId));
                                hasErrors = true;
                                break;
                            }
                        }
                    }
                }
            }

            foreach (var bindKv in relativeBindPosesByBone.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var list = bindKv.Value;
                (string FirstPatchId, string SecondPatchId)? bestConflict = null;
                for (int a = 0; a < list.Count; a++)
                {
                    for (int b = a + 1; b < list.Count; b++)
                    {
                        if (!MatricesMatch(
                                list[a].RelativeBind,
                                list[b].RelativeBind,
                                context.ReaderScene.Metadata.UnitMeters,
                                context.NumericVerificationToleranceMeters))
                        {
                            string pA = list[a].PatchId;
                            string pB = list[b].PatchId;
                            if (StringComparer.Ordinal.Compare(pA, pB) > 0)
                            {
                                (pA, pB) = (pB, pA);
                            }

                            if (!bestConflict.HasValue ||
                                StringComparer.Ordinal.Compare(pA, bestConflict.Value.FirstPatchId) < 0 ||
                                (string.Equals(pA, bestConflict.Value.FirstPatchId, StringComparison.Ordinal) &&
                                 StringComparer.Ordinal.Compare(pB, bestConflict.Value.SecondPatchId) < 0))
                            {
                                bestConflict = (pA, pB);
                            }
                        }
                    }
                }

                if (bestConflict.HasValue)
                {
                    diagnostics.Add(new FbxExportDiagnostic(
                        FbxExportDiagnosticSeverity.Error,
                        "FBX_EXPORT_SKIN_BINDPOSE_CONFLICT",
                        $"Conflicting cluster bindpose for bone '{bindKv.Key}' between patch '{bestConflict.Value.FirstPatchId}' and patch '{bestConflict.Value.SecondPatchId}'.",
                        patchId: bestConflict.Value.SecondPatchId,
                        meshId: context.TargetNodePath,
                        featureTypeId: FeatureTypeId));
                    hasErrors = true;
                }
            }

            return hasErrors
                ? FbxFeatureWriteResult.Failed(diagnostics.ToArray())
                : FbxFeatureWriteResult.Succeeded(modifiedMesh: false, diagnostics.ToArray());
        }

        public FbxFeatureWriteResult Write(FbxExportMeshContext context, IReadOnlyList<FbxFeatureWriteInput> inputs)
        {
            var diagnostics = new List<FbxExportDiagnostic>();
            if (inputs == null || inputs.Count == 0)
            {
                return FbxFeatureWriteResult.Skipped();
            }

            var targetMesh = context.TargetMesh;
            var primaryTargetSkin = FbxSdkNodeUtility.FindPrimarySkinDeformer(targetMesh, out int primarySkinIndex);
            var existingLinkModes = new Dictionary<string, FbxCluster.ELinkMode>(StringComparer.Ordinal);
            FbxCluster.ELinkMode? existingUniformMode = null;
            var preWriteClusters = FbxSdkNodeUtility.GetClustersOfSkin(primaryTargetSkin);
            for (int c = 0; c < preWriteClusters.Count; c++)
            {
                var cl = preWriteClusters[c];
                if (cl == null) continue;
                var linkNode = cl.GetLink();
                if (linkNode == null) continue;
                var clMode = cl.GetLinkMode();
                existingUniformMode ??= clMode;
                string linkPath = FbxSdkNodeUtility.GetNormalizedNodePath(linkNode);
                if (!string.IsNullOrEmpty(linkPath) && !existingLinkModes.ContainsKey(linkPath))
                {
                    existingLinkModes[linkPath] = clMode;
                }
            }

            FbxCluster.ELinkMode? requestedPatchSkinMode = null;
            for (int i = 0; i < inputs.Count; i++)
            {
                if (inputs[i].Feature is SkinWeightFeature swFeat &&
                    swFeat.Clusters != null &&
                    swFeat.Clusters.Count > 0)
                {
                    var patchMode = ToSdkLinkMode(swFeat.LinkMode);
                    if (requestedPatchSkinMode.HasValue && requestedPatchSkinMode.Value != patchMode)
                    {
                        diagnostics.Add(new FbxExportDiagnostic(
                            FbxExportDiagnosticSeverity.Error,
                            "FBX_EXPORT_SKIN_LINK_MODE_CONFLICT",
                            $"Conflicting skin weight link modes across patches on '{context.TargetNodePath}'.",
                            meshId: context.TargetNodePath,
                            featureTypeId: FeatureTypeId));
                        return FbxFeatureWriteResult.Failed(diagnostics.ToArray());
                    }

                    requestedPatchSkinMode = patchMode;
                }
            }

            if (existingUniformMode.HasValue &&
                requestedPatchSkinMode.HasValue &&
                existingUniformMode.Value != requestedPatchSkinMode.Value)
            {
                diagnostics.Add(new FbxExportDiagnostic(
                    FbxExportDiagnosticSeverity.Error,
                    "FBX_EXPORT_SKIN_LINK_MODE_MISMATCH",
                    $"Target FBX mesh '{context.TargetNodePath}' uses skin link mode '{existingUniformMode.Value}', which is incompatible with patch link mode '{requestedPatchSkinMode.Value}'.",
                    meshId: context.TargetNodePath,
                    featureTypeId: FeatureTypeId));
                return FbxFeatureWriteResult.Failed(diagnostics.ToArray());
            }

            var skinDefaultLinkMode = existingUniformMode ?? requestedPatchSkinMode ?? FbxCluster.ELinkMode.eNormalize;
            context.SessionState[k_SkinDefaultLinkModeStatePrefix + context.TargetNodePath] = skinDefaultLinkMode;

            var targetGeomToNode = ResolveTargetMeshGeomToNode(context.ReaderScene, context.ReaderMesh, context.TargetNodePath);
            var skeletonWriter = SkeletonFbxWriter.GetOrCreate(context.SessionState);
            RegisterTransactionInputs(context, inputs);

            if (!skeletonWriter.EnsureSolvedAndValidated(
                    context.RootNode,
                    context.ReaderScene,
                    string.IsNullOrEmpty(context.TargetNodePath) ? null : new[] { context.TargetNodePath },
                    context.NumericVerificationToleranceMeters,
                    diagnostics))
            {
                return FbxFeatureWriteResult.Failed(diagnostics.ToArray());
            }

            var state = BuildExpectedSkinState(
                context.ReaderScene,
                context.ReaderMesh,
                context.TargetNodePath,
                targetGeomToNode,
                inputs,
                skeletonWriter.AlignmentResult,
                skinDefaultLinkMode,
                context.ReaderScene.Metadata.UnitMeters,
                context.NumericVerificationToleranceMeters,
                out bool anyFeatureApplied,
                out string stateError);
            if (state == null)
            {
                diagnostics.Add(new FbxExportDiagnostic(
                    FbxExportDiagnosticSeverity.Error,
                    "FBX_EXPORT_SKIN_BUILD_FAILED",
                    stateError ?? "Failed building skin weight state.",
                    meshId: context.TargetNodePath,
                    featureTypeId: FeatureTypeId));
                return FbxFeatureWriteResult.Failed(diagnostics.ToArray());
            }

            // Ensure skeleton edits are written to SDK scene even when invoked standalone outside FbxSdkPatchExportBackend.
            if (!skeletonWriter.AppliedToSdkScene)
            {
                if (!skeletonWriter.Write(context.Manager, context.RootNode, diagnostics, out _, out _))
                {
                    return FbxFeatureWriteResult.Failed(diagnostics.ToArray());
                }
            }

            if (!anyFeatureApplied)
            {
                return FbxFeatureWriteResult.Skipped();
            }

            if (!state.HasSkinChanges)
            {
                return FbxFeatureWriteResult.Succeeded(modifiedMesh: true, diagnostics.ToArray());
            }

            var boneNodesByPath = new Dictionary<string, FbxNode>(StringComparer.Ordinal);
            foreach (string bonePath in state.OrderedBonePaths)
            {
                var boneNode = FbxSdkNodeUtility.FindNodeByPath(context.RootNode, bonePath);
                if (boneNode == null)
                {
                    diagnostics.Add(new FbxExportDiagnostic(
                        FbxExportDiagnosticSeverity.Error,
                        "FBX_EXPORT_SKIN_BONE_CREATE_FAILED",
                        $"Target FBX bone node '{bonePath}' was not found after applying skeleton edits.",
                        meshId: context.TargetNodePath,
                        featureTypeId: FeatureTypeId));
                    return FbxFeatureWriteResult.Failed(diagnostics.ToArray());
                }

                boneNodesByPath[bonePath] = boneNode;
            }

            FbxSkin sdkSkin = primaryTargetSkin;
            int writtenSkinIndex = primarySkinIndex;
            if (sdkSkin != null)
            {
                var oldClusters = FbxSdkNodeUtility.GetClustersOfSkin(sdkSkin);
                for (int i = oldClusters.Count - 1; i >= 0; i--)
                {
                    var oldCl = oldClusters[i];
                    if (oldCl != null)
                    {
                        sdkSkin.DisconnectSrcObject(oldCl);
                        oldCl.Destroy();
                    }
                }
            }
            else
            {
                string skinName = string.IsNullOrWhiteSpace(state.SkinName) ? "Skin" : state.SkinName;
                sdkSkin = FbxSkin.Create(context.Manager, skinName);
                targetMesh.AddDeformer(sdkSkin);
                writtenSkinIndex = targetMesh.GetDeformerCount(FbxDeformer.EDeformerType.eSkin) - 1;
            }

            context.SessionState[k_TargetSkinDeformerIndexStatePrefix + context.TargetNodePath] = writtenSkinIndex;

            var fallbackMeshGlobal = ResolveTargetMeshRestWorld(
                state.FallbackMeshBindWorld,
                skeletonWriter.TargetRestFrame,
                context.TargetNodePath,
                context.TargetNode);
            var expectedLinkModesByBone = new Dictionary<string, FbxCluster.ELinkMode>(StringComparer.Ordinal);

            foreach (string bonePath in state.OrderedBonePaths)
            {
                if (!state.ClusterWeightsByBone.TryGetValue(bonePath, out var cpWeights) || cpWeights.Count == 0)
                {
                    if (!state.OriginalBoneSet.Contains(bonePath))
                    {
                        continue;
                    }
                }

                var boneNode = boneNodesByPath[bonePath];
                string clusterName = FbxSdkNodeUtility.GetLeafName(bonePath);
                var sdkCluster = FbxCluster.Create(context.Manager, clusterName);
                sdkCluster.SetLink(boneNode);
                var linkMode = existingLinkModes.TryGetValue(bonePath, out var preservedMode)
                    ? preservedMode
                    : skinDefaultLinkMode;
                sdkCluster.SetLinkMode(linkMode);
                expectedLinkModesByBone[bonePath] = linkMode;

                if (state.RawClusterMatricesByBone.TryGetValue(bonePath, out var matrices))
                {
                    sdkCluster.SetTransformMatrix(FbxSdkNodeUtility.ToFbxAMatrix(matrices.TransformMatrix));
                    sdkCluster.SetTransformLinkMatrix(FbxSdkNodeUtility.ToFbxAMatrix(matrices.TransformLinkMatrix));
                }
                else
                {
                    // Fallback for clusters without explicit matrices: bind in post-edit REST pose (never live A-pose).
                    FbxMatrix4x4 postRestGlobal = FbxMatrix4x4.Identity;
                    bool hasPostRest = false;
                    if (skeletonWriter.AlignmentResult != null)
                    {
                        hasPostRest = skeletonWriter.AlignmentResult.TryGetPostEditRestGlobal(bonePath, out postRestGlobal, out string restConflictErr);
                        if (!string.IsNullOrEmpty(restConflictErr))
                        {
                            diagnostics.Add(new FbxExportDiagnostic(
                                FbxExportDiagnosticSeverity.Error,
                                "FBX_EXPORT_CONFLICTING_TARGET_REST",
                                restConflictErr,
                                meshId: context.TargetNodePath,
                                featureTypeId: FeatureTypeId));
                            return FbxFeatureWriteResult.Failed(diagnostics.ToArray());
                        }
                    }

                    FbxAMatrix fallbackLink = hasPostRest
                        ? FbxSdkNodeUtility.ToFbxAMatrix(postRestGlobal)
                        : boneNode.EvaluateGlobalTransform();
                    sdkCluster.SetTransformMatrix(fallbackMeshGlobal);
                    sdkCluster.SetTransformLinkMatrix(fallbackLink);
                }

                if (cpWeights != null)
                {
                    foreach (var kv in cpWeights.OrderBy(pair => pair.Key))
                    {
                        if (kv.Value > 0d)
                        {
                            sdkCluster.AddControlPointIndex(kv.Key, kv.Value);
                        }
                    }
                }

                sdkSkin.AddCluster(sdkCluster);
            }

            context.SessionState[k_ExpectedLinkModesStatePrefix + context.TargetNodePath] = expectedLinkModesByBone;
            return FbxFeatureWriteResult.Succeeded(modifiedMesh: true, diagnostics.ToArray());
        }

        public FbxFeatureWriteResult Verify(FbxExportVerifyMeshContext context, IReadOnlyList<FbxFeatureWriteInput> inputs)
        {
            var diagnostics = new List<FbxExportDiagnostic>();
            if (inputs == null || inputs.Count == 0)
            {
                return FbxFeatureWriteResult.Skipped();
            }

            var skinDefaultMode = FbxCluster.ELinkMode.eNormalize;
            if (context.SessionState != null &&
                context.SessionState.TryGetValue(k_SkinDefaultLinkModeStatePrefix + context.TargetNodePath, out var savedModeObj) &&
                savedModeObj is FbxCluster.ELinkMode savedMode)
            {
                skinDefaultMode = savedMode;
            }
            else
            {
                for (int i = 0; i < inputs.Count; i++)
                {
                    if (inputs[i].Feature is SkinWeightFeature swFeat &&
                        swFeat.Clusters != null &&
                        swFeat.Clusters.Count > 0)
                    {
                        skinDefaultMode = ToSdkLinkMode(swFeat.LinkMode);
                        break;
                    }
                }
            }

            var targetGeomToNode = ResolveTargetMeshGeomToNode(
                context.OriginalReaderScene,
                context.OriginalReaderMesh,
                context.TargetNodePath);

            var skeletonWriter = SkeletonFbxWriter.GetOrCreate(context.SessionState);
            for (int i = 0; i < inputs.Count; i++)
            {
                var input = inputs[i];
                if (input?.Skeleton != null && input.Skeleton.HasContent)
                {
                    skeletonWriter.Register(input.PatchItem.PatchId, input.PatchSpace, input.Skeleton);
                }
            }

            if (!skeletonWriter.EnsureSolvedAndValidated(
                    rootNode: null,
                    context.OriginalReaderScene,
                    string.IsNullOrEmpty(context.TargetNodePath) ? null : new[] { context.TargetNodePath },
                    context.NumericVerificationToleranceMeters,
                    diagnostics))
            {
                return FbxFeatureWriteResult.Failed(diagnostics.ToArray());
            }

            if (!skeletonWriter.VerifiedRoundTrip &&
                !skeletonWriter.Verify(
                    context.VerifiedScene,
                    context.OriginalReaderScene,
                    context.VerifyNumerics,
                    context.NumericVerificationToleranceMeters,
                    diagnostics))
            {
                return FbxFeatureWriteResult.Failed(diagnostics.ToArray());
            }

            var expected = BuildExpectedSkinState(
                context.OriginalReaderScene,
                context.OriginalReaderMesh,
                context.TargetNodePath,
                targetGeomToNode,
                inputs,
                skeletonWriter.AlignmentResult,
                skinDefaultMode,
                context.OriginalReaderScene.Metadata.UnitMeters,
                context.NumericVerificationToleranceMeters,
                out bool anyFeatureApplied,
                out string stateError);
            if (expected == null)
            {
                diagnostics.Add(new FbxExportDiagnostic(
                    FbxExportDiagnosticSeverity.Error,
                    "FBX_EXPORT_VERIFY_SKIN_EXPECTED_FAILED",
                    stateError ?? "Failed computing expected skin weights for verification.",
                    meshId: context.TargetNodePath,
                    featureTypeId: FeatureTypeId));
                return FbxFeatureWriteResult.Failed(diagnostics.ToArray());
            }

            if (!anyFeatureApplied)
            {
                return FbxFeatureWriteResult.Skipped();
            }

            if (!expected.HasSkinChanges)
            {
                return FbxFeatureWriteResult.Succeeded(modifiedMesh: false, diagnostics.ToArray());
            }

            var verifiedNodeMap = SkeletonFbxWriter.BuildNodeMap(context.VerifiedScene);
            int? expectedSkinIndex = null;
            if (context.SessionState != null &&
                context.SessionState.TryGetValue(k_TargetSkinDeformerIndexStatePrefix + context.TargetNodePath, out var savedSkinIdxObj) &&
                savedSkinIdxObj is int savedSkinIdx && savedSkinIdx >= 0)
            {
                expectedSkinIndex = savedSkinIdx;
            }

            IFbxSkinDeformer verifiedSkin = (expectedSkinIndex.HasValue &&
                                             context.VerifiedMesh?.SkinDeformers != null &&
                                             expectedSkinIndex.Value < context.VerifiedMesh.SkinDeformers.Count)
                ? context.VerifiedMesh.SkinDeformers[expectedSkinIndex.Value]
                : FindPrimarySkin(context.VerifiedMesh);
            if (verifiedSkin == null)
            {
                diagnostics.Add(new FbxExportDiagnostic(
                    FbxExportDiagnosticSeverity.Error,
                    "FBX_EXPORT_VERIFY_SKIN_MISSING",
                    $"Verified FBX mesh '{context.TargetNodePath}' is missing a skin deformer.",
                    meshId: context.TargetNodePath,
                    featureTypeId: FeatureTypeId));
                return FbxFeatureWriteResult.Failed(diagnostics.ToArray());
            }

            var verifiedClustersByBone = new Dictionary<string, IFbxSkinCluster>(StringComparer.Ordinal);
            for (int c = 0; c < verifiedSkin.Clusters.Count; c++)
            {
                var cl = verifiedSkin.Clusters[c];
                if (cl != null && !string.IsNullOrEmpty(cl.BonePath))
                {
                    verifiedClustersByBone[FbxNameUtility.NormalizePath(cl.BonePath)] = cl;
                }
            }

            foreach (var kv in expected.ClusterWeightsByBone)
            {
                if (kv.Value.Count > 0 && !verifiedClustersByBone.ContainsKey(kv.Key))
                {
                    diagnostics.Add(new FbxExportDiagnostic(
                        FbxExportDiagnosticSeverity.Error,
                        "FBX_EXPORT_VERIFY_SKIN_BONE_MISSING",
                        $"Verified FBX mesh '{context.TargetNodePath}' is missing skin cluster for bone '{kv.Key}'.",
                        meshId: context.TargetNodePath,
                        featureTypeId: FeatureTypeId));
                    return FbxFeatureWriteResult.Failed(diagnostics.ToArray());
                }
            }

            foreach (string bonePath in expected.OrderedBonePaths)
            {
                if (!verifiedNodeMap.ContainsKey(bonePath))
                {
                    diagnostics.Add(new FbxExportDiagnostic(
                        FbxExportDiagnosticSeverity.Error,
                        "FBX_EXPORT_VERIFY_BONE_NODE_MISSING",
                        $"Verified FBX scene is missing bone node '{bonePath}'.",
                        meshId: context.TargetNodePath,
                        featureTypeId: FeatureTypeId));
                    return FbxFeatureWriteResult.Failed(diagnostics.ToArray());
                }
            }

            if (!string.IsNullOrEmpty(context.VerifiedFbxPath) &&
                context.SessionState != null &&
                context.SessionState.TryGetValue(k_ExpectedLinkModesStatePrefix + context.TargetNodePath, out var expectedModesObj) &&
                expectedModesObj is Dictionary<string, FbxCluster.ELinkMode> expectedModes &&
                expectedModes.Count > 0)
            {
                if (!VerifyReopenedClusterLinkModes(
                        context.GetVerifiedSdkScene(),
                        verifiedClustersByBone,
                        context.VerifiedFbxPath,
                        context.TargetNodePath,
                        expectedSkinIndex,
                        expectedModes,
                        out string linkModeError))
                {
                    diagnostics.Add(new FbxExportDiagnostic(
                        FbxExportDiagnosticSeverity.Error,
                        "FBX_EXPORT_VERIFY_LINK_MODE_MISMATCH",
                        linkModeError,
                        meshId: context.TargetNodePath,
                        featureTypeId: FeatureTypeId));
                    return FbxFeatureWriteResult.Failed(diagnostics.ToArray());
                }
            }

            if (!context.VerifyNumerics)
            {
                return FbxFeatureWriteResult.Succeeded(modifiedMesh: false, diagnostics.ToArray());
            }

            int cpCount = context.VerifiedMesh.ControlPointCount;
            if (!TryReadRawSkinWeightsByCp(verifiedSkin, cpCount, out var actualRawByCp, out string readErr))
            {
                diagnostics.Add(new FbxExportDiagnostic(
                    FbxExportDiagnosticSeverity.Error,
                    "FBX_EXPORT_VERIFY_SKIN_READ_FAILED",
                    readErr,
                    meshId: context.TargetNodePath,
                    featureTypeId: FeatureTypeId));
                return FbxFeatureWriteResult.Failed(diagnostics.ToArray());
            }

            const double weightTol = 1e-3d;
            double maxDiff = 0d;
            int worstCp = -1;
            string worstBone = string.Empty;

            for (int cp = 0; cp < cpCount; cp++)
            {
                var expMap = expected.WeightsByCp[cp];
                var actMap = actualRawByCp[cp];
                if ((expMap == null || expMap.Count == 0) && (actMap == null || actMap.Count == 0))
                {
                    continue;
                }

                if (expMap != null)
                {
                    foreach (var kv in expMap)
                    {
                        double act = 0d;
                        if (actMap != null)
                        {
                            actMap.TryGetValue(kv.Key, out act);
                        }

                        double diff = Math.Abs(kv.Value - act);
                        if (diff > maxDiff)
                        {
                            maxDiff = diff;
                            worstCp = cp;
                            worstBone = kv.Key;
                        }
                    }
                }

                if (actMap != null)
                {
                    foreach (var kv in actMap)
                    {
                        if (expMap != null && expMap.ContainsKey(kv.Key))
                        {
                            continue;
                        }

                        double diff = Math.Abs(kv.Value);
                        if (diff > maxDiff)
                        {
                            maxDiff = diff;
                            worstCp = cp;
                            worstBone = kv.Key;
                        }
                    }
                }
            }

            if (maxDiff > weightTol)
            {
                diagnostics.Add(new FbxExportDiagnostic(
                    FbxExportDiagnosticSeverity.Error,
                    "FBX_EXPORT_VERIFY_SKIN_MISMATCH",
                    $"Skin weight round-trip verification failed on '{context.TargetNodePath}': max raw weight diff {maxDiff:E3} at CP {worstCp} bone '{worstBone}' exceeds tolerance {weightTol:E3}.",
                    meshId: context.TargetNodePath,
                    featureTypeId: FeatureTypeId));
                return FbxFeatureWriteResult.Failed(diagnostics.ToArray());
            }

            foreach (var kv in expected.RawClusterMatricesByBone)
            {
                if (!verifiedClustersByBone.TryGetValue(kv.Key, out var verifiedCluster))
                {
                    continue;
                }

                if (!MatricesMatch(verifiedCluster.MeshBindWorld, kv.Value.TransformMatrix, context.OriginalReaderScene.Metadata.UnitMeters, context.NumericVerificationToleranceMeters) ||
                    !MatricesMatch(verifiedCluster.BindToWorld, kv.Value.TransformLinkMatrix, context.OriginalReaderScene.Metadata.UnitMeters, context.NumericVerificationToleranceMeters))
                {
                    diagnostics.Add(new FbxExportDiagnostic(
                        FbxExportDiagnosticSeverity.Error,
                        "FBX_EXPORT_VERIFY_CLUSTER_MATRIX_MISMATCH",
                        $"Cluster bind matrices for bone '{kv.Key}' on '{context.TargetNodePath}' did not round-trip within tolerance.",
                        meshId: context.TargetNodePath,
                        featureTypeId: FeatureTypeId));
                    return FbxFeatureWriteResult.Failed(diagnostics.ToArray());
                }
            }

            return FbxFeatureWriteResult.Succeeded(modifiedMesh: false, diagnostics.ToArray());
        }

        private static FbxAMatrix ResolveTargetMeshRestWorld(
            FbxMatrix4x4? baseSkinMeshBindWorld,
            SkeletonRestFrame targetRestFrame,
            string targetNodePath,
            FbxNode targetNode)
        {
            if (baseSkinMeshBindWorld.HasValue)
            {
                return FbxSdkNodeUtility.ToFbxAMatrix(baseSkinMeshBindWorld.Value);
            }

            if (targetRestFrame != null &&
                targetRestFrame.TryGetNode(targetNodePath, out var meshRestNode) &&
                meshRestNode != null)
            {
                return FbxSdkNodeUtility.ToFbxAMatrix(meshRestNode.RestGlobal);
            }

            return targetNode.EvaluateGlobalTransform();
        }

        private static bool MatricesMatch(FbxMatrix4x4 actual, FbxMatrix4x4 expected, double unitMeters, double toleranceMeters) =>
            SkeletonFbxWriter.MatricesMatch(actual, expected, unitMeters, toleranceMeters);

        private static FbxMatrix4x4 ResolveTargetMeshGeomToNode(
            IFbxScene readerScene,
            IFbxMesh readerMesh,
            string targetNodePath)
        {
            var meshNode = readerScene?.FindNodeByPath(FbxNameUtility.NormalizePath(targetNodePath ?? string.Empty)) ??
                           readerMesh?.PrimaryNode;
            return meshNode != null ? meshNode.GeometryToNodeMatrix : FbxMatrix4x4.Identity;
        }

        private static bool TryComputeCalibratedRawClusterMatrices(
            SkinWeightClusterEntry cluster,
            SkeletonDocument skeleton,
            SkeletonAlignment alignmentResult,
            FbxExportSpace space,
            FbxMatrix4x4 targetGeomToNode,
            out FbxMatrix4x4 rawMeshMat,
            out FbxMatrix4x4 rawLinkMat,
            out string error)
        {
            rawMeshMat = FbxMatrix4x4.Identity;
            rawLinkMat = FbxMatrix4x4.Identity;
            error = null;

            if (!FbxAffineSpaceTransform.TryCreate(targetGeomToNode, out var geomXform))
            {
                error = "Target mesh GeometryToNodeMatrix is singular or non-finite.";
                return false;
            }

            string bonePath = FbxNameUtility.NormalizePath(cluster.BonePath ?? string.Empty);
            FbxMatrix4x4 endpointD;
            if (alignmentResult != null)
            {
                if (!alignmentResult.TryResolveEndpointBasis(
                        skeleton,
                        space.StoredWorldToTargetWorld,
                        bonePath,
                        out endpointD,
                        out error))
                {
                    return false;
                }
            }
            else
            {
                endpointD = FbxLinearAlgebra3d.ExtractLinear(space.StoredWorldToTargetWorld);
            }

            if (!FbxAffineSpaceTransform.TryCreate(endpointD, out var dXform))
            {
                error = $"Endpoint calibration basis for bone '{bonePath}' is singular.";
                return false;
            }

            // T_fbx = E_t^-1 * M_meshRaw^-1 * T_{geom,s} * H
            // L_fbx = D_frame(b)^-1 * L_s * H
            rawMeshMat = geomXform.InverseOffset *
                         space.TargetRawToStored *
                         cluster.TransformMatrix *
                         space.StoredWorldToTargetWorld;
            rawLinkMat = dXform.InverseOffset *
                         cluster.TransformLinkMatrix *
                         space.StoredWorldToTargetWorld;
            return true;
        }

        private static FbxCluster.ELinkMode ToSdkLinkMode(SkinWeightLinkMode mode) =>
            mode == SkinWeightLinkMode.TotalOne
                ? FbxCluster.ELinkMode.eTotalOne
                : FbxCluster.ELinkMode.eNormalize;

        private static bool VerifyReopenedClusterLinkModes(
            FbxScene sharedVerifiedSdkScene,
            IReadOnlyDictionary<string, IFbxSkinCluster> verifiedReaderClustersByBone,
            string verifiedFbxPath,
            string targetNodePath,
            int? expectedSkinIndex,
            Dictionary<string, FbxCluster.ELinkMode> expectedModes,
            out string error)
        {
            error = null;
            if (sharedVerifiedSdkScene == null)
            {
                error = $"Failed opening transaction verification scene for '{verifiedFbxPath}' during link-mode verification.";
                return false;
            }

            var node = FbxSdkNodeUtility.FindNodeByPath(sharedVerifiedSdkScene.GetRootNode(), targetNodePath);
            var mesh = node?.GetMesh();
            if (mesh == null)
            {
                error = $"Reopened FBX is missing mesh at '{targetNodePath}' during link-mode verification.";
                return false;
            }

            var actualModes = new Dictionary<string, FbxCluster.ELinkMode>(StringComparer.Ordinal);
            FbxSkin primaryVerifiedSkin = null;
            if (expectedSkinIndex.HasValue &&
                expectedSkinIndex.Value >= 0 &&
                expectedSkinIndex.Value < mesh.GetDeformerCount(FbxDeformer.EDeformerType.eSkin))
            {
                FbxSdkNodeUtility.TryWrapSkinDeformer(
                    mesh.GetDeformer(expectedSkinIndex.Value, FbxDeformer.EDeformerType.eSkin),
                    out primaryVerifiedSkin);
            }

            primaryVerifiedSkin ??= FbxSdkNodeUtility.FindPrimarySkinDeformer(mesh, out _);
            var clusters = FbxSdkNodeUtility.GetClustersOfSkin(primaryVerifiedSkin);
            for (int c = 0; c < clusters.Count; c++)
            {
                var cl = clusters[c];
                var link = cl?.GetLink();
                if (cl != null && link != null)
                {
                    string path = FbxSdkNodeUtility.GetNormalizedNodePath(link);
                    if (!string.IsNullOrEmpty(path))
                    {
                        actualModes[path] = cl.GetLinkMode();
                    }
                }
            }

            foreach (var kv in expectedModes)
            {
                if (!actualModes.TryGetValue(kv.Key, out var actualMode))
                {
                    error = $"Reopened FBX mesh '{targetNodePath}' is missing expected cluster '{kv.Key}' during link-mode verification.";
                    return false;
                }

                if (actualMode != kv.Value)
                {
                    error = $"Reopened FBX cluster '{kv.Key}' on '{targetNodePath}' has link mode '{actualMode}', expected '{kv.Value}'.";
                    return false;
                }

                if (verifiedReaderClustersByBone != null &&
                    verifiedReaderClustersByBone.TryGetValue(kv.Key, out var readerCluster) &&
                    readerCluster != null)
                {
                    var expectedReaderMode = kv.Value == FbxCluster.ELinkMode.eTotalOne
                        ? FbxClusterLinkMode.TotalOne
                        : FbxClusterLinkMode.Normalize;
                    if (readerCluster.LinkMode != expectedReaderMode)
                    {
                        error = $"Reopened reader cluster '{kv.Key}' on '{targetNodePath}' reported link mode '{readerCluster.LinkMode}', expected '{expectedReaderMode}'.";
                        return false;
                    }
                }
            }

            return true;
        }

        private static SkinBuildState BuildExpectedSkinState(
            IFbxScene readerScene,
            IFbxMesh readerMesh,
            string targetNodePath,
            FbxMatrix4x4 targetGeomToNode,
            IReadOnlyList<FbxFeatureWriteInput> inputs,
            SkeletonAlignment alignmentResult,
            FbxCluster.ELinkMode skinLinkMode,
            double unitMeters,
            double toleranceMeters,
            out bool anyFeatureApplied,
            out string error)
        {
            anyFeatureApplied = false;
            error = null;

            int cpCount = readerMesh.ControlPointCount;
            var state = new SkinBuildState(cpCount);
            var cpTouched = new bool[cpCount];

            bool preservePartialTotalOne = skinLinkMode == FbxCluster.ELinkMode.eTotalOne;
            bool anyPatchClusters = false;
            for (int i = 0; i < inputs.Count; i++)
            {
                if (inputs[i].Feature is SkinWeightFeature swCheck &&
                    swCheck.Clusters != null &&
                    swCheck.Clusters.Count > 0)
                {
                    anyPatchClusters = true;
                    break;
                }
            }

            var baseSkin = FindPrimarySkin(readerMesh);
            if (baseSkin != null && anyPatchClusters)
            {
                state.SkinName = baseSkin.Name;
                if (!TryReadRawSkinWeightsByCp(baseSkin, cpCount, out var rawBaseWeights, out error))
                {
                    return null;
                }

                if (!TryValidateAndCollectBaseClusterMatrices(
                        baseSkin,
                        unitMeters,
                        toleranceMeters,
                        out var baseMatricesByBone,
                        out _,
                        out error))
                {
                    return null;
                }

                for (int cp = 0; cp < cpCount; cp++)
                {
                    var normalized = NormalizeInitialCpWeights(rawBaseWeights[cp], preservePartialTotalOne);
                    if (normalized != null && normalized.Count > 0)
                    {
                        state.WeightsByCp[cp] = normalized;
                    }
                }

                for (int c = 0; c < baseSkin.Clusters.Count; c++)
                {
                    var cl = baseSkin.Clusters[c];
                    if (cl == null || string.IsNullOrEmpty(cl.BonePath))
                    {
                        continue;
                    }

                    string bonePath = FbxNameUtility.NormalizePath(cl.BonePath);
                    if (!string.IsNullOrEmpty(bonePath))
                    {
                        state.AddBonePath(bonePath, isOriginal: true);
                    }
                }

                foreach (var kv in baseMatricesByBone)
                {
                    state.RawClusterMatricesByBone[kv.Key] = kv.Value;
                    state.FallbackMeshBindWorld ??= kv.Value.TransformMatrix;
                }
            }

            var patchMatricesByBone = new Dictionary<string, List<(string PatchId, FbxMatrix4x4 RelativeBind, RawClusterMatrices Raw)>>(StringComparer.Ordinal);
            for (int i = 0; i < inputs.Count; i++)
            {
                var input = inputs[i];
                var feature = input.Feature as SkinWeightFeature;
                if (feature == null)
                {
                    continue;
                }

                if (input.Skeleton?.Nodes != null && input.Skeleton.Nodes.Count > 0)
                {
                    anyFeatureApplied = true;
                }

                if (feature.Clusters == null || feature.Clusters.Count == 0)
                {
                    continue;
                }

                state.HasSkinChanges = true;
                anyFeatureApplied = true;

                for (int c = 0; c < feature.Clusters.Count; c++)
                {
                    var cl = feature.Clusters[c];
                    if (cl == null) continue;
                    string bonePath = FbxNameUtility.NormalizePath(cl.BonePath);
                    if (string.IsNullOrEmpty(bonePath)) continue;

                    state.AddBonePath(bonePath, isOriginal: false);

                    if (cl.HasClusterMatrices)
                    {
                        if (!TryComputeCalibratedRawClusterMatrices(
                                cl,
                                input.Skeleton,
                                alignmentResult,
                                input.PatchSpace,
                                targetGeomToNode,
                                out var rawMeshMat,
                                out var rawLinkMat,
                                out error) ||
                            !FbxAffineSpaceTransform.TryCreate(rawMeshMat, out _) ||
                            !FbxAffineSpaceTransform.TryCreate(rawLinkMat, out var rawLinkXform))
                        {
                            error ??= $"Failed computing calibrated cluster matrices for '{bonePath}'.";
                            return null;
                        }

                        if (!patchMatricesByBone.TryGetValue(bonePath, out var pList))
                        {
                            pList = new List<(string, FbxMatrix4x4, RawClusterMatrices)>(2);
                            patchMatricesByBone.Add(bonePath, pList);
                        }

                        pList.Add((
                            input.PatchItem?.PatchId ?? string.Empty,
                            rawMeshMat * rawLinkXform.InverseOffset,
                            new RawClusterMatrices(rawMeshMat, rawLinkMat)));
                    }

                    if (!cl.Payload.HasPayload)
                    {
                        continue;
                    }

                    using var lease = cl.Payload.Acquire(input.PayloadSource);
                    var buf = lease.Value?.DeltaWeights;
                    if (buf == null)
                    {
                        error = $"Missing decoded payload for skin cluster '{cl.BonePath}'.";
                        return null;
                    }

                    for (int k = 0; k < buf.Count; k++)
                    {
                        int cp = buf.Indices[k];
                        double delta = buf.Values[k];
                        if ((uint)cp >= (uint)cpCount || double.IsNaN(delta) || double.IsInfinity(delta) || delta == 0d)
                        {
                            continue;
                        }

                        cpTouched[cp] = true;
                        var map = state.WeightsByCp[cp];
                        if (map == null)
                        {
                            map = new Dictionary<string, double>(StringComparer.Ordinal);
                            state.WeightsByCp[cp] = map;
                        }

                        map.TryGetValue(bonePath, out double existing);
                        map[bonePath] = existing + delta;
                    }
                }
            }

            foreach (var kv in patchMatricesByBone)
            {
                var list = kv.Value;
                for (int a = 0; a < list.Count; a++)
                {
                    for (int b = a + 1; b < list.Count; b++)
                    {
                        if (!MatricesMatch(list[a].RelativeBind, list[b].RelativeBind, unitMeters, toleranceMeters))
                        {
                            error = $"Conflicting cluster bindpose for bone '{kv.Key}' across patches.";
                            return null;
                        }
                    }
                }

                state.RawClusterMatricesByBone[kv.Key] = SelectDeterministicRawClusterMatrices(list);
            }

            for (int cp = 0; cp < cpCount; cp++)
            {
                var cpMap = cpTouched[cp]
                    ? FinalizeCpWeights(state.WeightsByCp[cp], preservePartialTotalOne)
                    : state.WeightsByCp[cp];
                state.WeightsByCp[cp] = cpMap;
                if (cpMap == null || cpMap.Count == 0)
                {
                    continue;
                }

                foreach (var kv in cpMap)
                {
                    if (!state.ClusterWeightsByBone.TryGetValue(kv.Key, out var boneDict))
                    {
                        boneDict = new Dictionary<int, double>();
                        state.ClusterWeightsByBone[kv.Key] = boneDict;
                    }

                    boneDict[cp] = kv.Value;
                }
            }

            return state;
        }

        private static Dictionary<string, double> NormalizeInitialCpWeights(
            Dictionary<string, double> weights,
            bool preservePartialTotalOne)
        {
            if (weights == null || weights.Count == 0)
            {
                return null;
            }

            var positiveValues = new List<double>(weights.Count);
            foreach (var kv in weights)
            {
                if (kv.Value > 0d && !double.IsNaN(kv.Value) && !double.IsInfinity(kv.Value))
                {
                    positiveValues.Add(kv.Value);
                }
            }

            if (positiveValues.Count == 0)
            {
                return null;
            }

            positiveValues.Sort();
            double sum = 0d;
            for (int i = 0; i < positiveValues.Count; i++)
            {
                sum += positiveValues[i];
            }

            if (!(sum > 0d) || double.IsInfinity(sum))
            {
                return null;
            }

            double divisor = (preservePartialTotalOne && sum <= 1d) ? 1d : sum;
            var result = new Dictionary<string, double>(weights.Count, StringComparer.Ordinal);
            foreach (var kv in weights)
            {
                if (kv.Value > 0d && !double.IsNaN(kv.Value) && !double.IsInfinity(kv.Value))
                {
                    double eff = kv.Value / divisor;
                    if (eff > 0d && !double.IsNaN(eff) && !double.IsInfinity(eff))
                    {
                        result[kv.Key] = preservePartialTotalOne ? eff : Math.Min(1d, eff);
                    }
                }
            }

            return result.Count > 0 ? result : null;
        }

        private static bool TryValidateAndCollectBaseClusterMatrices(
            IFbxSkinDeformer baseSkin,
            double unitMeters,
            double toleranceMeters,
            out Dictionary<string, RawClusterMatrices> matricesByBone,
            out string diagnosticCode,
            out string errorMessage)
        {
            matricesByBone = new Dictionary<string, RawClusterMatrices>(StringComparer.Ordinal);
            diagnosticCode = null;
            errorMessage = null;
            if (baseSkin?.Clusters == null)
            {
                return true;
            }

            var groupsByBone = new Dictionary<string, List<(string PatchId, FbxMatrix4x4 RelativeBind, RawClusterMatrices Raw)>>(StringComparer.Ordinal);
            for (int c = 0; c < baseSkin.Clusters.Count; c++)
            {
                var cl = baseSkin.Clusters[c];
                if (cl == null || string.IsNullOrEmpty(cl.BonePath))
                {
                    continue;
                }

                string bonePath = FbxNameUtility.NormalizePath(cl.BonePath);
                if (string.IsNullOrEmpty(bonePath))
                {
                    continue;
                }

                var meshMat = cl.MeshBindWorld;
                var linkMat = cl.BindToWorld;
                if (!FbxAffineSpaceTransform.TryCreate(meshMat, out _) ||
                    !FbxAffineSpaceTransform.TryCreate(linkMat, out var linkXform))
                {
                    diagnosticCode = "FBX_EXPORT_SKIN_INVALID_MATRIX";
                    errorMessage = $"Target FBX skin cluster '{cl.Name}' for bone '{bonePath}' has singular or non-finite bind matrices.";
                    return false;
                }

                var relBind = meshMat * linkXform.InverseOffset;
                if (!groupsByBone.TryGetValue(bonePath, out var list))
                {
                    list = new List<(string, FbxMatrix4x4, RawClusterMatrices)>(1);
                    groupsByBone.Add(bonePath, list);
                }

                list.Add((string.Empty, relBind, new RawClusterMatrices(meshMat, linkMat)));
            }

            foreach (var kv in groupsByBone.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var list = kv.Value;
                for (int i = 0; i < list.Count; i++)
                {
                    for (int j = i + 1; j < list.Count; j++)
                    {
                        if (!MatricesMatch(list[i].RelativeBind, list[j].RelativeBind, unitMeters, toleranceMeters) ||
                            !MatricesMatch(list[i].Raw.TransformMatrix, list[j].Raw.TransformMatrix, unitMeters, toleranceMeters) ||
                            !MatricesMatch(list[i].Raw.TransformLinkMatrix, list[j].Raw.TransformLinkMatrix, unitMeters, toleranceMeters))
                        {
                            diagnosticCode = "FBX_EXPORT_DUPLICATE_CLUSTER_BINDPOSE_CONFLICT";
                            errorMessage = $"Target FBX skin contains multiple clusters for bone '{kv.Key}' with incompatible bind matrices, which cannot be merged into one cluster without altering skin deformation.";
                            return false;
                        }
                    }
                }

                matricesByBone[kv.Key] = SelectDeterministicRawClusterMatrices(list);
            }

            return true;
        }

        private static RawClusterMatrices SelectDeterministicRawClusterMatrices(
            List<(string PatchId, FbxMatrix4x4 RelativeBind, RawClusterMatrices Raw)> list)
        {
            if (list.Count == 1)
            {
                return list[0].Raw;
            }

            var minRel = new double[16];
            var maxRel = new double[16];
            for (int k = 0; k < 16; k++)
            {
                int r = k >> 2, c = k & 3;
                minRel[k] = maxRel[k] = list[0].RelativeBind[r, c];
            }

            for (int i = 1; i < list.Count; i++)
            {
                for (int k = 0; k < 16; k++)
                {
                    int r = k >> 2, c = k & 3;
                    double v = list[i].RelativeBind[r, c];
                    if (v < minRel[k]) minRel[k] = v;
                    if (v > maxRel[k]) maxRel[k] = v;
                }
            }

            int bestIdx = 0;
            double bestDist = ComputeFbxMatrixMidpointDistance(list[0].RelativeBind, minRel, maxRel);
            for (int i = 1; i < list.Count; i++)
            {
                double d = ComputeFbxMatrixMidpointDistance(list[i].RelativeBind, minRel, maxRel);
                int cmp = d.CompareTo(bestDist);
                if (cmp == 0)
                {
                    cmp = CompareFbxMatrixLexicographic(list[i].Raw.TransformLinkMatrix, list[bestIdx].Raw.TransformLinkMatrix);
                    if (cmp == 0)
                    {
                        cmp = CompareFbxMatrixLexicographic(list[i].Raw.TransformMatrix, list[bestIdx].Raw.TransformMatrix);
                    }
                }

                if (cmp < 0)
                {
                    bestIdx = i;
                    bestDist = d;
                }
            }

            return list[bestIdx].Raw;
        }

        private static double ComputeFbxMatrixMidpointDistance(FbxMatrix4x4 m, double[] minCell, double[] maxCell)
        {
            double maxD = 0d;
            for (int k = 0; k < 16; k++)
            {
                int r = k >> 2, c = k & 3;
                double mid = 0.5d * (minCell[k] + maxCell[k]);
                double d = Math.Abs(m[r, c] - mid);
                if (d > maxD) maxD = d;
            }

            return maxD;
        }

        private static int CompareFbxMatrixLexicographic(FbxMatrix4x4 a, FbxMatrix4x4 b)
        {
            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++)
                {
                    double va = a[r, c] == 0d ? 0d : a[r, c];
                    double vb = b[r, c] == 0d ? 0d : b[r, c];
                    int cmp = va.CompareTo(vb);
                    if (cmp != 0) return cmp;
                }
            }

            return 0;
        }

        private static Dictionary<string, double> FinalizeCpWeights(
            Dictionary<string, double> weights,
            bool preservePartialTotalOne)
        {
            if (weights == null || weights.Count == 0)
            {
                return null;
            }

            double sum = 0d;
            foreach (var kv in weights)
            {
                if (kv.Value > k_MinWeightEpsilon && !double.IsNaN(kv.Value) && !double.IsInfinity(kv.Value))
                {
                    sum += kv.Value;
                }
            }

            if (sum <= 0d)
            {
                return null;
            }

            double divisor = (preservePartialTotalOne && sum <= 1d) ? 1d : sum;
            var result = new Dictionary<string, double>(weights.Count, StringComparer.Ordinal);
            foreach (var kv in weights)
            {
                if (kv.Value > k_MinWeightEpsilon && !double.IsNaN(kv.Value) && !double.IsInfinity(kv.Value))
                {
                    result[kv.Key] = kv.Value / divisor;
                }
            }

            return result;
        }

        private static bool TryReadRawSkinWeightsByCp(
            IFbxSkinDeformer skin,
            int cpCount,
            out Dictionary<string, double>[] weightsByCp,
            out string error)
        {
            error = null;
            weightsByCp = new Dictionary<string, double>[cpCount];
            int[] idxScratch = Array.Empty<int>();
            double[] wScratch = Array.Empty<double>();

            for (int c = 0; c < skin.Clusters.Count; c++)
            {
                var cl = skin.Clusters[c];
                if (cl == null || cl.WeightCount == 0)
                {
                    continue;
                }

                if (cl.WeightCount < 0)
                {
                    error = $"Skin cluster {c} ('{cl.BonePath}') reported negative weight count ({cl.WeightCount}).";
                    weightsByCp = null;
                    return false;
                }

                string bonePath = FbxNameUtility.NormalizePath(cl.BonePath ?? string.Empty);
                if (string.IsNullOrEmpty(bonePath))
                {
                    error = $"Skin cluster {c} has weights ({cl.WeightCount}) but an empty bone path.";
                    weightsByCp = null;
                    return false;
                }

                int count = cl.WeightCount;
                if (idxScratch.Length < count)
                {
                    idxScratch = new int[count];
                    wScratch = new double[count];
                }

                int wWritten = 0;
                if (!cl.TryCopyControlPointIndices(idxScratch.AsSpan(0, count), out int idxWritten) ||
                    !cl.TryCopyWeights(wScratch.AsSpan(0, count), out wWritten) ||
                    idxWritten != count || wWritten != count)
                {
                    error = $"Failed reading skin cluster '{bonePath}' weights (expected {count}, copied {idxWritten}/{wWritten}).";
                    weightsByCp = null;
                    return false;
                }

                for (int i = 0; i < count; i++)
                {
                    int cp = idxScratch[i];
                    double w = wScratch[i];
                    if ((uint)cp >= (uint)cpCount || double.IsNaN(w) || double.IsInfinity(w) || w < 0d)
                    {
                        error = $"Skin cluster '{bonePath}' contains invalid control point index {cp} or weight {w}.";
                        weightsByCp = null;
                        return false;
                    }

                    if (w <= 0d)
                    {
                        continue;
                    }

                    var map = weightsByCp[cp];
                    if (map == null)
                    {
                        map = new Dictionary<string, double>(StringComparer.Ordinal);
                        weightsByCp[cp] = map;
                    }

                    map.TryGetValue(bonePath, out double existing);
                    map[bonePath] = existing + w;
                }
            }

            return true;
        }

        private static int CountWeightedSkins(IFbxMesh mesh)
        {
            if (mesh?.SkinDeformers == null)
            {
                return 0;
            }

            int count = 0;
            for (int i = 0; i < mesh.SkinDeformers.Count; i++)
            {
                var skin = mesh.SkinDeformers[i];
                if (skin?.Clusters != null && skin.Clusters.Any(c => c != null && c.WeightCount > 0))
                {
                    count++;
                }
            }

            return count;
        }

        private static IFbxSkinDeformer FindPrimarySkin(IFbxMesh mesh) =>
            FbxDeformerUtility.FindPrimarySkinDeformer(mesh);

        private readonly struct RawClusterMatrices
        {
            public FbxMatrix4x4 TransformMatrix { get; }
            public FbxMatrix4x4 TransformLinkMatrix { get; }

            public RawClusterMatrices(FbxMatrix4x4 transformMatrix, FbxMatrix4x4 transformLinkMatrix)
            {
                TransformMatrix = transformMatrix;
                TransformLinkMatrix = transformLinkMatrix;
            }
        }

        private sealed class SkinBuildState
        {
            public string SkinName { get; set; } = "Skin";
            public bool HasSkinChanges { get; set; }
            public FbxMatrix4x4? FallbackMeshBindWorld { get; set; }
            public Dictionary<string, double>[] WeightsByCp { get; }
            public List<string> OrderedBonePaths { get; } = new List<string>();
            public HashSet<string> OriginalBoneSet { get; } = new HashSet<string>(StringComparer.Ordinal);
            private readonly HashSet<string> m_SeenBones = new HashSet<string>(StringComparer.Ordinal);
            public Dictionary<string, RawClusterMatrices> RawClusterMatricesByBone { get; } =
                new Dictionary<string, RawClusterMatrices>(StringComparer.Ordinal);
            public Dictionary<string, Dictionary<int, double>> ClusterWeightsByBone { get; } =
                new Dictionary<string, Dictionary<int, double>>(StringComparer.Ordinal);

            public SkinBuildState(int cpCount)
            {
                WeightsByCp = new Dictionary<string, double>[cpCount];
            }

            public void AddBonePath(string bonePath, bool isOriginal)
            {
                if (string.IsNullOrEmpty(bonePath))
                {
                    return;
                }

                if (isOriginal)
                {
                    OriginalBoneSet.Add(bonePath);
                }

                if (m_SeenBones.Add(bonePath))
                {
                    OrderedBonePaths.Add(bonePath);
                }
            }
        }
    }
}
