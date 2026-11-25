using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Fbx;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Model.Documents;
using Triturbo.BlendShare.Model.Features;
using Triturbo.BlendShare.Pipeline.Export;
using Triturbo.BlendShare.Primitives;

namespace Triturbo.BlendShare.FbxExport
{
    /// <summary>
    /// Autodesk FBX SDK writer and ufbx round-trip verifier for <see cref="SkinWeightFeature"/>.
    /// Accumulates signed weight deltas across patches, preserves untouched base weights,
    /// creates missing skeleton nodes when <see cref="SkeletonBoneRecord.CreateIfMissing"/> is true,
    /// writes lossless raw FBX transform properties (when in identical space) or conjugated local bind matrices
    /// to both created and existing armature bones, and verifies raw weights, cluster bind matrices,
    /// and armature bone transforms via ufbx round-trip.
    /// </summary>
    public sealed class SkinWeightFbxWriter : IFeatureFbxWriter
    {
        private const string k_ArmatureRegistryStateKey = "fbx-export.armature-bones";
        private const double k_BindMatrixCompareTolerance = 1e-3d;
        private const double k_MinWeightEpsilon = 1e-6d;

        public string FeatureTypeId => SkinWeightFeature.FeatureTypeId;
        public int ExecutionOrder => 50;

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

            // Fail closed on multi-skin meshes instead of silently destroying secondary skin deformers.
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

            // Validate original skin clusters can be read without short/failed copies before mutating anything.
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

            int cpCount = context.ReaderMesh.ControlPointCount;
            var boneRegistry = GetOrCreateArmatureBoneRegistry(context.SessionState);
            var relativeBindPoseByBone = new Dictionary<string, (string PatchId, FbxMatrix4x4 RelativeBind)>(StringComparer.Ordinal);
            bool hasErrors = false;

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

                var skeleton = input.Skeleton;
                if (skeleton != null)
                {
                    if (!RegisterAndValidateSkeletonBones(
                            context.RootNode,
                            skeleton,
                            input.PatchItem.PatchId,
                            input.PatchSpace,
                            context.ReaderScene.Metadata.UnitMeters,
                            context.NumericVerificationToleranceMeters,
                            boneRegistry,
                            diagnostics))
                    {
                        hasErrors = true;
                    }
                }

                if (feature.Clusters == null || feature.Clusters.Count == 0)
                {
                    continue;
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

                    if (FbxSdkNodeUtility.FindNodeByPath(context.RootNode, bonePath) == null)
                    {
                        if (!CanCreateBoneHierarchyFromSkeleton(
                                context.RootNode,
                                skeleton,
                                bonePath,
                                input.PatchSpace,
                                out string boneError))
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
                    }

                    if (cluster.HasClusterMatrices)
                    {
                        var rawMeshMat = input.PatchSpace.BindMatrixToRaw(cluster.TransformMatrix);
                        var rawLinkMat = input.PatchSpace.BindMatrixToRaw(cluster.TransformLinkMatrix);
                        if (!FbxAffineSpaceTransform.TryCreate(rawMeshMat, out _) ||
                            !FbxAffineSpaceTransform.TryCreate(rawLinkMat, out var rawLinkXform))
                        {
                            diagnostics.Add(new FbxExportDiagnostic(
                                FbxExportDiagnosticSeverity.Error,
                                "FBX_EXPORT_SKIN_INVALID_MATRIX",
                                $"Skin cluster '{bonePath}' has singular or non-finite bind matrices.",
                                patchId: input.PatchItem.PatchId,
                                meshId: input.MeshHeader.MeshId,
                                featureTypeId: FeatureTypeId));
                            hasErrors = true;
                        }
                        else
                        {
                            var relativeBind = rawMeshMat * rawLinkXform.InverseOffset;
                            if (relativeBindPoseByBone.TryGetValue(bonePath, out var existingBind) &&
                                !MatricesMatch(
                                    existingBind.RelativeBind,
                                    relativeBind,
                                    context.ReaderScene.Metadata.UnitMeters,
                                    context.NumericVerificationToleranceMeters))
                            {
                                diagnostics.Add(new FbxExportDiagnostic(
                                    FbxExportDiagnosticSeverity.Error,
                                    "FBX_EXPORT_SKIN_BINDPOSE_CONFLICT",
                                    $"Conflicting cluster bindpose for bone '{bonePath}' between patch '{existingBind.PatchId}' and patch '{input.PatchItem.PatchId}'.",
                                    patchId: input.PatchItem.PatchId,
                                    meshId: input.MeshHeader.MeshId,
                                    featureTypeId: FeatureTypeId));
                                hasErrors = true;
                            }
                            else
                            {
                                relativeBindPoseByBone[bonePath] = (input.PatchItem.PatchId, relativeBind);
                            }
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

            var state = BuildExpectedSkinState(
                context.ReaderMesh,
                inputs,
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

            if (!anyFeatureApplied)
            {
                return FbxFeatureWriteResult.Skipped();
            }

            // 1. Apply all SkeletonDocument bone nodes in parent-before-child hierarchy order
            // (creating missing nodes when CreateIfMissing is true, and updating transforms on both new and existing nodes).
            if (!ApplyArmatureNodesToFbx(context.Manager, context.RootNode, state.ArmatureBonesByPath, out string armatureError))
            {
                diagnostics.Add(new FbxExportDiagnostic(
                    FbxExportDiagnosticSeverity.Error,
                    "FBX_EXPORT_SKIN_ARMATURE_APPLY_FAILED",
                    armatureError,
                    meshId: context.TargetNodePath,
                    featureTypeId: FeatureTypeId));
                return FbxFeatureWriteResult.Failed(diagnostics.ToArray());
            }

            if (!state.HasSkinChanges)
            {
                return FbxFeatureWriteResult.Succeeded(modifiedMesh: true, diagnostics.ToArray());
            }

            // 2. Resolve all skin cluster bone nodes (creating missing ones if permitted by SkeletonDocument).
            var boneNodesByPath = new Dictionary<string, FbxNode>(StringComparer.Ordinal);
            foreach (string bonePath in state.OrderedBonePaths)
            {
                var boneNode = FbxSdkNodeUtility.FindNodeByPath(context.RootNode, bonePath);
                if (boneNode == null)
                {
                    if (!TryResolveOrCreateBoneNode(
                            context.Manager,
                            context.RootNode,
                            state.ArmatureBonesByPath,
                            bonePath,
                            new HashSet<string>(StringComparer.Ordinal),
                            out boneNode,
                            out string createError))
                    {
                        diagnostics.Add(new FbxExportDiagnostic(
                            FbxExportDiagnosticSeverity.Error,
                            "FBX_EXPORT_SKIN_BONE_CREATE_FAILED",
                            createError,
                            meshId: context.TargetNodePath,
                            featureTypeId: FeatureTypeId));
                        return FbxFeatureWriteResult.Failed(diagnostics.ToArray());
                    }
                }

                boneNodesByPath[bonePath] = boneNode;
            }

            // 3. Rebuild skin deformer on TargetMesh.
            var targetMesh = context.TargetMesh;
            for (int i = targetMesh.GetDeformerCount(FbxDeformer.EDeformerType.eSkin) - 1; i >= 0; i--)
            {
                var existingSkin = targetMesh.GetDeformer(i, FbxDeformer.EDeformerType.eSkin);
                existingSkin?.Destroy();
            }

            string skinName = string.IsNullOrWhiteSpace(state.SkinName) ? "Skin" : state.SkinName;
            var sdkSkin = FbxSkin.Create(context.Manager, skinName);
            targetMesh.AddDeformer(sdkSkin);

            var fallbackMeshGlobal = context.TargetNode.EvaluateGlobalTransform();

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
                sdkCluster.SetLinkMode(FbxCluster.ELinkMode.eNormalize);

                if (state.RawClusterMatricesByBone.TryGetValue(bonePath, out var matrices))
                {
                    sdkCluster.SetTransformMatrix(FbxSdkNodeUtility.ToFbxAMatrix(matrices.TransformMatrix));
                    sdkCluster.SetTransformLinkMatrix(FbxSdkNodeUtility.ToFbxAMatrix(matrices.TransformLinkMatrix));
                }
                else
                {
                    sdkCluster.SetTransformMatrix(fallbackMeshGlobal);
                    sdkCluster.SetTransformLinkMatrix(boneNode.EvaluateGlobalTransform());
                }

                if (cpWeights != null)
                {
                    foreach (var kv in cpWeights.OrderBy(pair => pair.Key))
                    {
                        if (kv.Value > k_MinWeightEpsilon)
                        {
                            sdkCluster.AddControlPointIndex(kv.Key, kv.Value);
                        }
                    }
                }

                sdkSkin.AddCluster(sdkCluster);
            }

            return FbxFeatureWriteResult.Succeeded(modifiedMesh: true, diagnostics.ToArray());
        }

        public FbxFeatureWriteResult Verify(FbxExportVerifyMeshContext context, IReadOnlyList<FbxFeatureWriteInput> inputs)
        {
            var diagnostics = new List<FbxExportDiagnostic>();
            if (inputs == null || inputs.Count == 0)
            {
                return FbxFeatureWriteResult.Skipped();
            }

            var expected = BuildExpectedSkinState(
                context.OriginalReaderMesh,
                inputs,
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

            // 1. Verify all applied armature bone nodes exist and match expected local bind matrices.
            var verifiedNodeMap = BuildNodeMap(context.VerifiedScene);
            var originalNodeMap = BuildNodeMap(context.OriginalReaderScene);
            foreach (var kv in expected.ArmatureBonesByPath)
            {
                string bonePath = kv.Key;
                var armEntry = kv.Value;
                bool existedInOriginal = originalNodeMap.ContainsKey(bonePath);
                if (!existedInOriginal && !armEntry.Record.CreateIfMissing)
                {
                    continue;
                }

                if (!verifiedNodeMap.TryGetValue(bonePath, out var verifiedBoneNode))
                {
                    diagnostics.Add(new FbxExportDiagnostic(
                        FbxExportDiagnosticSeverity.Error,
                        "FBX_EXPORT_VERIFY_BONE_NODE_MISSING",
                        $"Verified FBX scene is missing armature bone node '{bonePath}'.",
                        meshId: context.TargetNodePath,
                        featureTypeId: FeatureTypeId));
                    return FbxFeatureWriteResult.Failed(diagnostics.ToArray());
                }

                if (context.VerifyNumerics &&
                    !MatricesMatch(
                        verifiedBoneNode.NodeToParentMatrix,
                        armEntry.RawLocalMatrix,
                        context.OriginalReaderScene.Metadata.UnitMeters,
                        context.NumericVerificationToleranceMeters))
                {
                    diagnostics.Add(new FbxExportDiagnostic(
                        FbxExportDiagnosticSeverity.Error,
                        "FBX_EXPORT_VERIFY_BONE_TRANSFORM_MISMATCH",
                        $"Armature bone '{bonePath}' local bind transform does not match expected matrix.",
                        meshId: context.TargetNodePath,
                        featureTypeId: FeatureTypeId));
                    return FbxFeatureWriteResult.Failed(diagnostics.ToArray());
                }
            }

            if (!expected.HasSkinChanges)
            {
                return FbxFeatureWriteResult.Succeeded(modifiedMesh: false, diagnostics.ToArray());
            }

            var verifiedSkin = FindPrimarySkin(context.VerifiedMesh);
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

            // 2. Verify every expected weighted bone has a cluster and node in verifiedSkin.
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

            if (!context.VerifyNumerics)
            {
                return FbxFeatureWriteResult.Succeeded(modifiedMesh: false, diagnostics.ToArray());
            }

            // 3. Verify raw weights.
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

            // 4. Verify cluster bind matrices (MeshBindWorld & BindToWorld) for every cluster with explicit matrices.
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

        private static bool MatricesMatch(FbxMatrix4x4 actual, FbxMatrix4x4 expected, double unitMeters, double toleranceMeters)
        {
            for (int row = 0; row < 4; row++)
            for (int column = 0; column < 4; column++)
            {
                double tolerance = row == 3 && column < 3 ? toleranceMeters / unitMeters : k_BindMatrixCompareTolerance;
                if (!(Math.Abs(actual[row, column] - expected[row, column]) <= tolerance)) return false;
            }
            return true;
        }

        private static SkinBuildState BuildExpectedSkinState(
            IFbxMesh readerMesh,
            IReadOnlyList<FbxFeatureWriteInput> inputs,
            out bool anyFeatureApplied,
            out string error)
        {
            anyFeatureApplied = false;
            error = null;

            int cpCount = readerMesh.ControlPointCount;
            var state = new SkinBuildState(cpCount);

            var baseSkin = FindPrimarySkin(readerMesh);
            if (baseSkin != null)
            {
                state.SkinName = baseSkin.Name;
                if (!TryReadRawSkinWeightsByCp(baseSkin, cpCount, out var rawBaseWeights, out error))
                {
                    return null;
                }

                for (int cp = 0; cp < cpCount; cp++)
                {
                    var normalized = NormalizeCpWeights(rawBaseWeights[cp]);
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
                    if (string.IsNullOrEmpty(bonePath))
                    {
                        continue;
                    }

                    state.AddBonePath(bonePath, isOriginal: true);
                    var meshMat = cl.MeshBindWorld;
                    var linkMat = cl.BindToWorld;
                    if (FbxAffineSpaceTransform.TryCreate(meshMat, out _) &&
                        FbxAffineSpaceTransform.TryCreate(linkMat, out _))
                    {
                        state.RawClusterMatricesByBone[bonePath] = new RawClusterMatrices(meshMat, linkMat);
                    }
                }
            }

            // Accumulate signed delta weights across all patches BEFORE pruning non-positive entries or renormalizing.
            for (int i = 0; i < inputs.Count; i++)
            {
                var input = inputs[i];
                var feature = input.Feature as SkinWeightFeature;
                if (feature == null)
                {
                    continue;
                }

                var skeleton = input.Skeleton;
                if (skeleton != null && skeleton.Bones != null && skeleton.Bones.Count > 0)
                {
                    state.RegisterSkeleton(skeleton, input.PatchSpace);
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
                        var rawMeshMat = input.PatchSpace.BindMatrixToRaw(cl.TransformMatrix);
                        var rawLinkMat = input.PatchSpace.BindMatrixToRaw(cl.TransformLinkMatrix);
                        state.RawClusterMatricesByBone[bonePath] = new RawClusterMatrices(rawMeshMat, rawLinkMat);
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

            // Finalize: prune non-positive accumulated weights, renormalize per control point, and populate cluster map.
            for (int cp = 0; cp < cpCount; cp++)
            {
                var cpMap = NormalizeCpWeights(state.WeightsByCp[cp]);
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

        private static bool ApplyArmatureNodesToFbx(
            FbxManager manager,
            FbxNode rootNode,
            Dictionary<string, PreparedArmatureBone> armatureBonesByPath,
            out string error)
        {
            error = null;
            if (armatureBonesByPath == null || armatureBonesByPath.Count == 0)
            {
                return true;
            }

            var orderedPaths = armatureBonesByPath.Keys
                .OrderBy(CountPathDepth)
                .ThenBy(p => p, StringComparer.Ordinal)
                .ToArray();

            for (int i = 0; i < orderedPaths.Length; i++)
            {
                string path = orderedPaths[i];
                var entry = armatureBonesByPath[path];
                var node = FbxSdkNodeUtility.FindNodeByPath(rootNode, path);
                if (node == null)
                {
                    if (!entry.Record.CreateIfMissing)
                    {
                        continue;
                    }

                    if (!TryResolveOrCreateBoneNode(
                            manager,
                            rootNode,
                            armatureBonesByPath,
                            path,
                            new HashSet<string>(StringComparer.Ordinal),
                            out node,
                            out error))
                    {
                        return false;
                    }
                }
                else
                {
                    if (!ApplyPreparedTransformToNode(node, entry, out error))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool ApplyPreparedTransformToNode(
            FbxNode node,
            PreparedArmatureBone entry,
            out string error)
        {
            error = null;
            if (!entry.Record.HasTransformData)
            {
                error = $"Armature bone '{entry.Path}' is missing local bind transform data.";
                return false;
            }

            if (entry.CanUseRawProperties)
            {
                FbxSdkNodeUtility.ApplyRawTransformPropertiesToNode(node, entry.Record);
                return true;
            }

            return FbxSdkNodeUtility.TryApplyRawLocalMatrixToNode(node, entry.RawLocalMatrix, out error);
        }

        private static Dictionary<string, double> NormalizeCpWeights(Dictionary<string, double> weights)
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

            var result = new Dictionary<string, double>(weights.Count, StringComparer.Ordinal);
            foreach (var kv in weights)
            {
                if (kv.Value > k_MinWeightEpsilon && !double.IsNaN(kv.Value) && !double.IsInfinity(kv.Value))
                {
                    result[kv.Key] = kv.Value / sum;
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

                    if (w <= k_MinWeightEpsilon)
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

        private static IFbxSkinDeformer FindPrimarySkin(IFbxMesh mesh)
        {
            if (mesh == null || mesh.SkinDeformers == null || mesh.SkinDeformers.Count == 0)
            {
                return null;
            }

            for (int i = 0; i < mesh.SkinDeformers.Count; i++)
            {
                var skin = mesh.SkinDeformers[i];
                if (skin != null && skin.Clusters != null && skin.Clusters.Any(c => c != null && c.WeightCount > 0))
                {
                    return skin;
                }
            }

            return mesh.SkinDeformers[0];
        }

        private static Dictionary<string, IFbxNode> BuildNodeMap(IFbxScene scene)
        {
            var map = new Dictionary<string, IFbxNode>(StringComparer.Ordinal);
            if (scene?.Nodes == null)
            {
                return map;
            }

            for (int i = 0; i < scene.Nodes.Count; i++)
            {
                var n = scene.Nodes[i];
                if (n == null || n.IsRoot)
                {
                    continue;
                }

                string norm = FbxNameUtility.NormalizePath(n.Path);
                if (!string.IsNullOrEmpty(norm) && !map.ContainsKey(norm))
                {
                    map.Add(norm, n);
                }
            }

            return map;
        }

        private static SkeletonBoneRecord FindBoneRecord(SkeletonDocument skeleton, string normalizedPath)
        {
            if (skeleton?.Bones == null)
            {
                return null;
            }

            for (int i = 0; i < skeleton.Bones.Count; i++)
            {
                var b = skeleton.Bones[i];
                if (b != null && string.Equals(FbxNameUtility.NormalizePath(b.Path), normalizedPath, StringComparison.Ordinal))
                {
                    return b;
                }
            }

            return null;
        }

        private static bool CanCreateBoneHierarchyFromSkeleton(
            FbxNode rootNode,
            SkeletonDocument skeleton,
            string bonePath,
            FbxExportSpace space,
            out string error)
        {
            error = null;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            string current = bonePath;

            while (!string.IsNullOrEmpty(current))
            {
                if (FbxSdkNodeUtility.FindNodeByPath(rootNode, current) != null)
                {
                    return true;
                }

                if (!visited.Add(current))
                {
                    error = $"Skeleton bone hierarchy contains a cycle at '{current}'.";
                    return false;
                }

                var record = FindBoneRecord(skeleton, current);
                if (record == null || !record.CreateIfMissing)
                {
                    error = record == null
                        ? $"Missing target FBX bone '{current}' and no SkeletonBoneRecord was found to create it."
                        : $"Target FBX bone '{current}' is missing and SkeletonBoneRecord has CreateIfMissing = false.";
                    return false;
                }

                if (!record.HasTransformData)
                {
                    error = $"Skeleton bone '{current}' is missing local bind transform data.";
                    return false;
                }

                var rawLocal = space.BindMatrixToRaw(record.LocalBindMatrix);
                if (!FbxAffineSpaceTransform.TryCreate(rawLocal, out _))
                {
                    error = $"Skeleton bone '{current}' has a singular or non-finite local bind matrix.";
                    return false;
                }

                current = FbxNameUtility.NormalizePath(
                    !string.IsNullOrEmpty(record.ParentPath)
                        ? record.ParentPath
                        : FbxSdkNodeUtility.GetParentPath(current));
            }

            return true;
        }

        private static bool TryResolveOrCreateBoneNode(
            FbxManager manager,
            FbxNode rootNode,
            Dictionary<string, PreparedArmatureBone> boneRecords,
            string bonePath,
            HashSet<string> resolving,
            out FbxNode node,
            out string error)
        {
            error = null;
            node = FbxSdkNodeUtility.FindNodeByPath(rootNode, bonePath);
            if (node != null)
            {
                return true;
            }

            if (!resolving.Add(bonePath))
            {
                error = $"Armature hierarchy cycle detected while creating FBX bone '{bonePath}'.";
                return false;
            }

            if (!boneRecords.TryGetValue(bonePath, out var entry) || !entry.Record.CreateIfMissing)
            {
                resolving.Remove(bonePath);
                error = entry == null
                    ? $"Cannot create missing FBX bone '{bonePath}': no SkeletonBoneRecord available."
                    : $"Cannot create missing FBX bone '{bonePath}': SkeletonBoneRecord has CreateIfMissing = false.";
                return false;
            }

            string parentPath = FbxNameUtility.NormalizePath(
                !string.IsNullOrEmpty(entry.Record.ParentPath)
                    ? entry.Record.ParentPath
                    : FbxSdkNodeUtility.GetParentPath(bonePath));

            FbxNode parentNode;
            if (string.IsNullOrEmpty(parentPath))
            {
                parentNode = rootNode;
            }
            else if (!TryResolveOrCreateBoneNode(
                         manager,
                         rootNode,
                         boneRecords,
                         parentPath,
                         resolving,
                         out parentNode,
                         out error))
            {
                resolving.Remove(bonePath);
                return false;
            }

            resolving.Remove(bonePath);

            string leafName = FbxSdkNodeUtility.GetLeafName(bonePath);
            var created = FbxNode.Create(manager, leafName);
            var skeleton = FbxSkeleton.Create(manager, leafName);
            skeleton.SetSkeletonType(parentNode == rootNode
                ? FbxSkeleton.EType.eRoot
                : FbxSkeleton.EType.eLimbNode);
            created.SetNodeAttribute(skeleton);

            if (!ApplyPreparedTransformToNode(created, entry, out error))
            {
                created.Destroy();
                return false;
            }

            parentNode.AddChild(created);
            node = created;
            return true;
        }

        private static Dictionary<string, (string PatchId, PreparedArmatureBone Bone)> GetOrCreateArmatureBoneRegistry(
            IDictionary<string, object> sessionState)
        {
            if (sessionState.TryGetValue(k_ArmatureRegistryStateKey, out var existing) &&
                existing is Dictionary<string, (string, PreparedArmatureBone)> dict)
            {
                return dict;
            }

            var created = new Dictionary<string, (string, PreparedArmatureBone)>(StringComparer.Ordinal);
            sessionState[k_ArmatureRegistryStateKey] = created;
            return created;
        }

        private static bool RegisterAndValidateSkeletonBones(
            FbxNode rootNode,
            SkeletonDocument skeleton,
            string patchId,
            FbxExportSpace space,
            double unitMeters,
            double toleranceMeters,
            Dictionary<string, (string PatchId, PreparedArmatureBone Bone)> registry,
            List<FbxExportDiagnostic> diagnostics)
        {
            bool ok = true;
            if (skeleton?.Bones == null)
            {
                return true;
            }

            for (int i = 0; i < skeleton.Bones.Count; i++)
            {
                var bone = skeleton.Bones[i];
                if (bone == null || string.IsNullOrWhiteSpace(bone.Path))
                {
                    continue;
                }

                string path = FbxNameUtility.NormalizePath(bone.Path);
                if (!bone.HasTransformData)
                {
                    diagnostics.Add(new FbxExportDiagnostic(
                        FbxExportDiagnosticSeverity.Error,
                        "FBX_EXPORT_SKELETON_NO_TRANSFORM",
                        $"Skeleton bone '{path}' is missing local bind transform data.",
                        patchId: patchId,
                        featureTypeId: SkinWeightFeature.FeatureTypeId));
                    ok = false;
                    continue;
                }

                if (bone.CreateIfMissing && FbxSdkNodeUtility.FindNodeByPath(rootNode, path) == null)
                {
                    if (!CanCreateBoneHierarchyFromSkeleton(rootNode, skeleton, path, space, out string createError))
                    {
                        diagnostics.Add(new FbxExportDiagnostic(
                            FbxExportDiagnosticSeverity.Error,
                            "FBX_EXPORT_SKIN_MISSING_BONE",
                            createError,
                            patchId: patchId,
                            featureTypeId: SkinWeightFeature.FeatureTypeId));
                        ok = false;
                        continue;
                    }
                }

                var prepared = new PreparedArmatureBone(path, bone, space);
                if (!FbxAffineSpaceTransform.TryCreate(prepared.RawLocalMatrix, out _))
                {
                    diagnostics.Add(new FbxExportDiagnostic(
                        FbxExportDiagnosticSeverity.Error,
                        "FBX_EXPORT_SKELETON_INVALID_MATRIX",
                        $"Skeleton bone '{path}' has a singular or non-finite local bind matrix.",
                        patchId: patchId,
                        featureTypeId: SkinWeightFeature.FeatureTypeId));
                    ok = false;
                    continue;
                }

                if (registry.TryGetValue(path, out var existing))
                {
                    bool conflict = !MatricesMatch(existing.Bone.RawLocalMatrix, prepared.RawLocalMatrix, unitMeters, toleranceMeters);
                    if (!conflict && existing.Bone.CanUseRawProperties && prepared.CanUseRawProperties)
                    {
                        double transTol = toleranceMeters / unitMeters;
                        conflict = !existing.Bone.Record.ApproximatelyTransform(prepared.Record, k_BindMatrixCompareTolerance, transTol);
                    }

                    if (conflict)
                    {
                        diagnostics.Add(new FbxExportDiagnostic(
                            FbxExportDiagnosticSeverity.Error,
                            "FBX_EXPORT_SKELETON_CONFLICT",
                            $"Bone '{path}' has conflicting bind transforms between patch '{existing.PatchId}' and patch '{patchId}'.",
                            patchId: patchId,
                            featureTypeId: SkinWeightFeature.FeatureTypeId));
                        ok = false;
                    }
                }
                else
                {
                    registry[path] = (patchId, prepared);
                }
            }

            return ok;
        }

        private static int CountPathDepth(string path)
        {
            if (string.IsNullOrEmpty(path)) return 0;
            int depth = 0;
            for (int i = 0; i < path.Length; i++)
            {
                if (path[i] == '/') depth++;
            }

            return depth;
        }

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

        private sealed class PreparedArmatureBone
        {
            public string Path { get; }
            public SkeletonBoneRecord Record { get; }
            public FbxMatrix4x4 RawLocalMatrix { get; }
            public bool CanUseRawProperties { get; }

            public PreparedArmatureBone(string path, SkeletonBoneRecord record, FbxExportSpace space)
            {
                Path = path;
                Record = record;
                RawLocalMatrix = space != null ? space.BindMatrixToRaw(record.LocalBindMatrix) : record.LocalBindMatrix;
                CanUseRawProperties = space != null && space.IsExactIdentity && record.HasRawTransformProperties;
            }
        }

        private sealed class SkinBuildState
        {
            public string SkinName { get; set; } = "Skin";
            public bool HasSkinChanges { get; set; }
            public Dictionary<string, double>[] WeightsByCp { get; }
            public List<string> OrderedBonePaths { get; } = new List<string>();
            public HashSet<string> OriginalBoneSet { get; } = new HashSet<string>(StringComparer.Ordinal);
            private readonly HashSet<string> m_SeenBones = new HashSet<string>(StringComparer.Ordinal);
            public Dictionary<string, RawClusterMatrices> RawClusterMatricesByBone { get; } =
                new Dictionary<string, RawClusterMatrices>(StringComparer.Ordinal);
            public Dictionary<string, Dictionary<int, double>> ClusterWeightsByBone { get; } =
                new Dictionary<string, Dictionary<int, double>>(StringComparer.Ordinal);
            public Dictionary<string, PreparedArmatureBone> ArmatureBonesByPath { get; } =
                new Dictionary<string, PreparedArmatureBone>(StringComparer.Ordinal);

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

            public void RegisterSkeleton(SkeletonDocument skeleton, FbxExportSpace space)
            {
                if (skeleton?.Bones == null)
                {
                    return;
                }

                for (int i = 0; i < skeleton.Bones.Count; i++)
                {
                    var b = skeleton.Bones[i];
                    if (b == null || string.IsNullOrWhiteSpace(b.Path))
                    {
                        continue;
                    }

                    string path = FbxNameUtility.NormalizePath(b.Path);
                    if (!ArmatureBonesByPath.TryGetValue(path, out var existing) ||
                        (b.CreateIfMissing && !existing.Record.CreateIfMissing))
                    {
                        ArmatureBonesByPath[path] = new PreparedArmatureBone(path, b, space);
                    }
                }
            }
        }
    }
}
