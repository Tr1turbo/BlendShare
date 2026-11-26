using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Assets;
using Triturbo.BlendShare.EditorAssets.Mapping;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Mapping;
using Triturbo.BlendShare.Model.Documents;
using Triturbo.BlendShare.Model.Features;
using Triturbo.BlendShare.Model.Serialization;
using Triturbo.BlendShare.Pipeline.Extraction;
using Triturbo.BlendShare.Pipeline.Skeleton;
using Triturbo.BlendShare.Primitives;
using Unity.Collections;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.Pipeline.Apply
{
    /// <summary>
    /// Applies one or more <see cref="SkinWeightFeature"/> patches to a Unity Mesh using signed delta
    /// weight accumulation across all inputs before finalization, order-independent two-stage bindpose resolution
    /// (explicit/proxy slot definitions settled before descendant fallback derivation), avatar/artifact-scoped
    /// hierarchy compensation via <see cref="UnitySkeletonScope"/>, and verified <c>TotalOne</c> residual checks.
    /// </summary>
    public sealed class SkinWeightUnityApplier : IFeatureUnityApplier
    {
        public string FeatureTypeId => SkinWeightFeature.FeatureTypeId;
        public int ExecutionOrder => 50;

        public FeatureApplyResult Apply(UnityMeshApplyContext context, IReadOnlyList<FeatureApplyInput> inputs)
        {
            var mesh = context.WorkingMesh;
            var boneTable = SeedBoneTable(context.WorkingBones, mesh.bindposes, context.TargetRootTransform, context.IncomingBoneSlotPaths);
            var initialIncomingBindPoses = boneTable.BindPoses.ToArray();
            var explicitClusterBindPoseBySlot = new Dictionary<int, (string PatchId, Matrix4x4 BindPose)>();
            var weightsByVertex = ReadInitialWeights(mesh, boneTable, out bool initialHasAnyWeights);
            var records = new List<AppliedBoneBinding>();
            var createdRoots = new List<Transform>();

            Transform outputRootBone = context.IncomingRootBone;
            string outputRootBonePath = outputRootBone && context.TargetRootTransform
                ? FbxNameUtility.NormalizePath(AnimationUtility.CalculateTransformPath(outputRootBone, context.TargetRootTransform))
                : string.Empty;

            bool anyWeightOrBindposeModified = false;
            bool anyLocalSkeletonInput = false;
            SkinWeightLinkMode? effectiveSkinLinkMode = null;
            string effectiveSkinLinkModePatchId = null;

            try
            {
                // Pass 1: Validate all inputs & link modes up front.
                var localAuthoredBonePaths = new HashSet<string>(StringComparer.Ordinal);
                var localScopeCandidates = new List<SkeletonScopeCandidate>();
                VerifiedRawTargetSkinContext verifiedRawSkin = null;
                IFbxMesh verifiedRawMesh = null;
                CsrVertexMap verifiedSeedMap = null;
                Dictionary<string, IReadOnlyList<int>> mergedImportedSlotsByFbxBonePath = null;
                bool hasAnyImportedSlotDictionary = false;
                bool conflictingRawTargetMesh = false;

                for (int i = 0; i < inputs.Count; i++)
                {
                    var input = inputs[i];
                    var feature = (SkinWeightFeature)input.Feature;
                    var mapping = input.VertexMapping;
                    var map = mapping.VertexMap;
                    var meshLocalSpace = mapping.SpaceContract.StoredToUnityMeshLocal;
                    var sceneLocalSpace = mapping.SpaceContract.EffectiveStoredToUnitySceneLocal;
                    if (map.SourceDomain != IndexDomain.UnityVertex || map.TargetDomain != IndexDomain.ControlPoint ||
                        map.SourceCount != mesh.vertexCount ||
                        mapping.SpaceContract.StoredSpace == null || !mapping.SpaceContract.StoredSpace.IsValid ||
                        (input.SpaceProvenance != null && !mapping.SpaceContract.StoredSpace.MatchesSpace(input.SpaceProvenance)) ||
                        !FbxAffineSpaceTransform.TryCreate(meshLocalSpace, out _) ||
                        !FbxAffineSpaceTransform.TryCreate(sceneLocalSpace, out _))
                    {
                        throw new InvalidOperationException("Invalid skin vertex mapping.");
                    }

                    if (!Enum.IsDefined(typeof(SkinWeightLinkMode), feature.LinkMode))
                    {
                        return FeatureApplyResult.Failed(new ApplyDiagnostic(
                            ApplyDiagnosticSeverity.Error,
                            "SKIN_APPLY_INVALID_LINK_MODE",
                            $"Patch '{input.PatchId}' specifies an unsupported skin weight link mode '{feature.LinkMode}'."));
                    }

                    if (feature.Clusters != null && feature.Clusters.Count > 0)
                    {
                        if (effectiveSkinLinkMode.HasValue && effectiveSkinLinkMode.Value != feature.LinkMode)
                        {
                            return FeatureApplyResult.Failed(new ApplyDiagnostic(
                                ApplyDiagnosticSeverity.Error,
                                "SKIN_APPLY_LINK_MODE_CONFLICT",
                                $"Conflicting skin weight link modes between patch '{effectiveSkinLinkModePatchId}' ({effectiveSkinLinkMode.Value}) and patch '{input.PatchId}' ({feature.LinkMode})."));
                        }

                        effectiveSkinLinkMode = feature.LinkMode;
                        effectiveSkinLinkModePatchId = input.PatchId;
                    }

                    if (input.VerifiedRawTargetSkin != null)
                    {
                        bool isDirectFbxMap =
                            mapping.SourceKind == VertexMappingSourceKind.FbxInverseMapComputed ||
                            mapping.SourceKind == VertexMappingSourceKind.FbxInverseMapCached;
                        var candidateSeedMap = input.VerifiedRawTargetSkin.FullTargetVertexMap
                                               ?? (isDirectFbxMap ? (mapping.FullTargetVertexMap ?? map) : null);
                        if (verifiedRawMesh == null)
                        {
                            verifiedRawMesh = input.VerifiedRawTargetSkin.RawMesh;
                        }
                        else if (!ReferenceEquals(verifiedRawMesh, input.VerifiedRawTargetSkin.RawMesh))
                        {
                            conflictingRawTargetMesh = true;
                        }

                        if (!conflictingRawTargetMesh && candidateSeedMap != null)
                        {
                            if (verifiedRawMesh == null ||
                                candidateSeedMap.SourceCount != mesh.vertexCount ||
                                candidateSeedMap.TargetCount > verifiedRawMesh.ControlPointCount)
                            {
                                conflictingRawTargetMesh = true;
                            }
                            else if (verifiedSeedMap == null)
                            {
                                verifiedSeedMap = candidateSeedMap;
                            }
                            else if (candidateSeedMap.TargetCount <= verifiedSeedMap.TargetCount)
                            {
                                if (!EqualsFilteredPrefix(candidateSeedMap, verifiedSeedMap))
                                {
                                    conflictingRawTargetMesh = true;
                                }
                            }
                            else
                            {
                                if (!EqualsFilteredPrefix(verifiedSeedMap, candidateSeedMap))
                                {
                                    conflictingRawTargetMesh = true;
                                }
                                else
                                {
                                    verifiedSeedMap = candidateSeedMap;
                                }
                            }
                        }

                        if (input.VerifiedRawTargetSkin.ImportedSlotsByFbxBonePath != null)
                        {
                            hasAnyImportedSlotDictionary = true;
                            mergedImportedSlotsByFbxBonePath ??= new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal);
                            foreach (var slotKv in input.VerifiedRawTargetSkin.ImportedSlotsByFbxBonePath)
                            {
                                string normSlotKey = FbxNameUtility.NormalizePath(slotKv.Key ?? string.Empty);
                                if (string.IsNullOrEmpty(normSlotKey) || slotKv.Value == null || slotKv.Value.Count == 0)
                                {
                                    continue;
                                }

                                int[] canonicalSlots = slotKv.Value.Distinct().OrderBy(s => s).ToArray();
                                if (mergedImportedSlotsByFbxBonePath.TryGetValue(normSlotKey, out var existingImpSlots))
                                {
                                    if (!existingImpSlots.SequenceEqual(canonicalSlots))
                                    {
                                        conflictingRawTargetMesh = true;
                                    }
                                }
                                else
                                {
                                    mergedImportedSlotsByFbxBonePath.Add(normSlotKey, canonicalSlots);
                                }
                            }
                        }
                    }

                    var skeleton = input.Skeleton;
                    if (skeleton?.Nodes != null && skeleton.Nodes.Count > 0)
                    {
                        anyLocalSkeletonInput = true;
                        for (int b = 0; b < skeleton.Nodes.Count; b++)
                        {
                            var sb = skeleton.Nodes[b];
                            if (sb != null && !string.IsNullOrEmpty(sb.Path))
                            {
                                localAuthoredBonePaths.Add(FbxNameUtility.NormalizePath(sb.Path));
                            }
                        }
                    }

                    if (skeleton != null && skeleton.HasContent)
                    {
                        localScopeCandidates.Add(new SkeletonScopeCandidate(
                            input.PatchId,
                            skeleton,
                            sceneLocalSpace));
                    }
                }

                if (verifiedRawMesh != null && !conflictingRawTargetMesh)
                {
                    verifiedRawSkin = VerifiedRawTargetSkinContext.WithSlotLists(
                        verifiedRawMesh,
                        verifiedSeedMap,
                        hasAnyImportedSlotDictionary ? mergedImportedSlotsByFbxBonePath : null);
                    boneTable.RegisterVerifiedImportedSlotOwners(verifiedRawSkin.ImportedSlotsByFbxBonePath);
                }

                // Validate verified raw target skin link mode up front (before bone creation or path selection)
                // so Normalize-on-TotalOne, TotalOne-on-Normalize, and bindpose-only mismatches fail closed identically to FBX export.
                if (effectiveSkinLinkMode.HasValue)
                {
                    if (conflictingRawTargetMesh)
                    {
                        return FeatureApplyResult.Failed(new ApplyDiagnostic(
                            ApplyDiagnosticSeverity.Error,
                            effectiveSkinLinkMode.Value == SkinWeightLinkMode.TotalOne
                                ? "SKIN_APPLY_PARTIAL_TOTAL_ONE_UNSUPPORTED"
                                : "SKIN_APPLY_LINK_MODE_CONFLICT",
                            "Conflicting verified raw target FBX meshes were supplied across skin weight inputs."));
                    }

                    if (verifiedRawSkin != null)
                    {
                        var primaryTargetSkin = SkinWeightFeatureExtractor.FindPrimarySkinDeformer(verifiedRawSkin.RawMesh);
                        if (!SkinWeightFeatureExtractor.TryDetectSkinLinkMode(
                                primaryTargetSkin,
                                "Target",
                                out var verifiedTargetLinkMode,
                                out _,
                                out string rawModeError))
                        {
                            return FeatureApplyResult.Failed(new ApplyDiagnostic(
                                ApplyDiagnosticSeverity.Error,
                                "SKIN_APPLY_LINK_MODE_MISMATCH",
                                rawModeError));
                        }

                        if (verifiedTargetLinkMode.HasValue)
                        {
                            var expectedFbxMode = effectiveSkinLinkMode.Value == SkinWeightLinkMode.TotalOne
                                ? FbxClusterLinkMode.TotalOne
                                : FbxClusterLinkMode.Normalize;
                            if (verifiedTargetLinkMode.Value != expectedFbxMode)
                            {
                                return FeatureApplyResult.Failed(new ApplyDiagnostic(
                                    ApplyDiagnosticSeverity.Error,
                                    "SKIN_APPLY_LINK_MODE_MISMATCH",
                                    $"Target FBX mesh uses link mode '{verifiedTargetLinkMode.Value}', which is incompatible with a '{effectiveSkinLinkMode.Value}' skin weight patch."));
                            }
                        }
                    }
                }

                // Resolve or reuse avatar/artifact-wide skeleton scope without mutating scene transforms before validation passes.
                UnitySkeletonScope skeletonScope = context.SharedSkeletonScope;
                if (skeletonScope == null)
                {
                    string[] effectiveSeedSlotPaths = UnitySkeletonScope.ResolveEffectiveSeedSlotPaths(
                        context.WorkingBones?.Length ?? 0,
                        context.IncomingBoneSlotPaths,
                        !conflictingRawTargetMesh ? mergedImportedSlotsByFbxBonePath : null);
                    var seeds = new[]
                    {
                        new RendererRestSeed(
                            context.MeshRendererTransform,
                            context.WorkingBones,
                            mesh.bindposes,
                            effectiveSeedSlotPaths)
                    };
                    skeletonScope = UnitySkeletonScope.Build(
                        context.TargetRootTransform,
                        seeds,
                        localScopeCandidates,
                        context.BoneTransformOverrides);
                }

                var mergedSkeletonBonesByPath = skeletonScope.SolvedBonesByPath;

                // Pass 2: Three-stage order-independent bone slot & bindpose resolution:
                //   2A-1: Direct non-proxy authored cluster-matrix definitions settle mutable incoming slots first.
                //   2A-2: Proxy and per-patch BindPoseOverride definitions allocate/reuse immutable matching-(Transform, BindPose)
                //         slots without mutating pre-existing incoming slots or canonical lookup tables.
                //   2B  : Per-request fallback definitions evaluate every non-incoming / override-chained request independently
                //         in parent-before-child order before sharing slots.
                var slotByInputIndex = new Dictionary<string, int>[inputs.Count];
                var finalizedPostEditSlots = new HashSet<int>();
                var slotSpecs = new List<PendingBoneSlotSpec>();

                for (int i = 0; i < inputs.Count; i++)
                {
                    var input = inputs[i];
                    var feature = (SkinWeightFeature)input.Feature;
                    var mapping = input.VertexMapping;
                    var meshLocalSpace = mapping.SpaceContract.StoredToUnityMeshLocal;
                    var sceneLocalSpace = mapping.SpaceContract.EffectiveStoredToUnitySceneLocal;

                    var skeleton = input.Skeleton;

                    var neededPaths = feature.GetNeededBonePathsInSkeletonOrder(skeleton);
                    slotByInputIndex[i] = new Dictionary<string, int>(StringComparer.Ordinal);

                    for (int n = 0; n < neededPaths.Count; n++)
                    {
                        string sourcePath = neededPaths[n];
                        var overrideKey = new BoneOverrideKey(input.PatchId, sourcePath);

                        Transform resolvedBone = null;
                        bool isProxyOverride = false;
                        bool hasExplicitNullIncomingSlot = false;
                        int? importedPreboundSlot = null;
                        int[] agreedCandidateSlots = null;
                        bool hasAuthoritativeDonorSet = false;
                        int[] verifiedDonorSlots = null;
                        bool donorIsAmbiguousMultiSlot = false;
                        if (context.BoneTransformOverrides != null &&
                            context.BoneTransformOverrides.TryGetValue(overrideKey, out var overrideTransform) &&
                            overrideTransform != null)
                        {
                            resolvedBone = overrideTransform;
                            isProxyOverride = true;
                        }

                        if (!conflictingRawTargetMesh &&
                            verifiedRawSkin?.ImportedSlotsByFbxBonePath != null &&
                            verifiedRawSkin.ImportedSlotsByFbxBonePath.TryGetValue(sourcePath, out var origImportedSlots) &&
                            origImportedSlots != null && origImportedSlots.Count > 0)
                        {
                            if (!resolvedBone)
                            {
                                boneTable.ResolveImportedCandidateSlots(
                                    origImportedSlots,
                                    sourcePath,
                                    out int candSlot,
                                    out var candTransform,
                                    out agreedCandidateSlots,
                                    out bool candIsNullOrAmbiguous);
                                importedPreboundSlot = candSlot;
                                if (!candIsNullOrAmbiguous && candTransform != null)
                                {
                                    resolvedBone = candTransform;
                                    hasAuthoritativeDonorSet = true;
                                    verifiedDonorSlots = agreedCandidateSlots;
                                }
                                else
                                {
                                    hasExplicitNullIncomingSlot = true;
                                    hasAuthoritativeDonorSet = true;
                                    verifiedDonorSlots = origImportedSlots.Count == 1 &&
                                                         boneTable.TryRemapOriginalIndex(origImportedSlots[0], out int singleOrigRem) &&
                                                         (uint)singleOrigRem < (uint)boneTable.Transforms.Count
                                        ? new[] { singleOrigRem }
                                        : Array.Empty<int>();
                                    donorIsAmbiguousMultiSlot = origImportedSlots.Count > 1;
                                }
                            }
                            else
                            {
                                hasAuthoritativeDonorSet = true;
                                boneTable.ResolveDonorCandidateSlots(
                                    origImportedSlots,
                                    isImportedOriginal: true,
                                    out verifiedDonorSlots,
                                    out donorIsAmbiguousMultiSlot);
                            }
                        }
                        else if (boneTable.TryGetExplicitIncomingSlotsByPath(sourcePath, out var explicitIncomingSlots) &&
                                 explicitIncomingSlots.Count > 0)
                        {
                            if (!resolvedBone && !hasExplicitNullIncomingSlot)
                            {
                                boneTable.ResolveExplicitCandidateSlots(
                                    explicitIncomingSlots,
                                    sourcePath,
                                    out int expSlot,
                                    out var expTransform,
                                    out agreedCandidateSlots,
                                    out bool expIsNullOrAmbiguous);
                                importedPreboundSlot = expSlot;
                                if (!expIsNullOrAmbiguous && expTransform != null)
                                {
                                    resolvedBone = expTransform;
                                    hasAuthoritativeDonorSet = true;
                                    verifiedDonorSlots = agreedCandidateSlots;
                                }
                                else
                                {
                                    hasExplicitNullIncomingSlot = true;
                                    hasAuthoritativeDonorSet = true;
                                    verifiedDonorSlots = explicitIncomingSlots.Count == 1 &&
                                                         (uint)explicitIncomingSlots[0] < (uint)boneTable.Transforms.Count
                                        ? new[] { explicitIncomingSlots[0] }
                                        : Array.Empty<int>();
                                    donorIsAmbiguousMultiSlot = explicitIncomingSlots.Count > 1;
                                }
                            }
                            else
                            {
                                hasAuthoritativeDonorSet = true;
                                boneTable.ResolveDonorCandidateSlots(
                                    explicitIncomingSlots,
                                    isImportedOriginal: false,
                                    out verifiedDonorSlots,
                                    out donorIsAmbiguousMultiSlot);
                            }
                        }

                        if (!resolvedBone && !hasExplicitNullIncomingSlot && context.TargetRootTransform)
                        {
                            resolvedBone = context.TargetRootTransform.Find(sourcePath);
                        }

                        if (!resolvedBone && !hasExplicitNullIncomingSlot && boneTable.TryGetTransform(sourcePath, out var tableTransform))
                        {
                            resolvedBone = tableTransform;
                        }

                        if (!resolvedBone && !hasExplicitNullIncomingSlot && context.CreateMissingBones && input.CreateMissingBones && context.TargetRootTransform)
                        {
                            if (!UnitySkeletonWriter.EnsureBoneHierarchy(
                                    context.TargetRootTransform,
                                    skeleton,
                                    sourcePath,
                                    sceneLocalSpace,
                                    skeletonScope,
                                    out resolvedBone,
                                    out var topmostCreated,
                                    out var createError,
                                    input.PatchId,
                                    context.BoneTransformOverrides))
                            {
                                throw new InvalidOperationException(createError);
                            }

                            if (topmostCreated && !createdRoots.Contains(topmostCreated))
                            {
                                createdRoots.Add(topmostCreated);
                            }
                        }

                        string targetPath = resolvedBone && context.TargetRootTransform
                            ? FbxNameUtility.NormalizePath(AnimationUtility.CalculateTransformPath(resolvedBone, context.TargetRootTransform))
                            : sourcePath;

                        bool isNew = resolvedBone && createdRoots.Any(t => t && (resolvedBone == t || resolvedBone.IsChildOf(t)));

                        bool hasBindPoseOverride = TryGetBindPoseOverride(input, sourcePath, out var authoredBindPose);
                        bool hasClusterMatrices = feature.TryGetBindPoseFbxClusterMatrices(sourcePath, out _, out _);

                        slotSpecs.Add(new PendingBoneSlotSpec(
                            slotSpecs.Count,
                            i,
                            input,
                            feature,
                            skeleton,
                            sourcePath,
                            targetPath,
                            resolvedBone,
                            isNew,
                            isProxyOverride,
                            importedPreboundSlot,
                            agreedCandidateSlots,
                            hasAuthoritativeDonorSet,
                            verifiedDonorSlots,
                            donorIsAmbiguousMultiSlot,
                            hasBindPoseOverride,
                            authoredBindPose,
                            hasClusterMatrices,
                            meshLocalSpace,
                            sceneLocalSpace));
                        anyWeightOrBindposeModified = true;
                    }
                }

                // Stage 2A-1: Group direct non-proxy, non-override authored cluster-matrix definitions per target slot/bone,
                // validate full group spread (1e-3f) so 3+ patch conflict checks are strictly order-independent, and pick
                // a deterministic representative bindpose before mutating any slot.
                var canonicalFinalizedSlots = new HashSet<int>();
                var directClusterSpecs = slotSpecs
                    .Where(sp => !sp.IsProxyOverride && !sp.HasBindPoseOverride && sp.HasClusterMatrices)
                    .ToList();

                if (directClusterSpecs.Count > 0)
                {
                    var clusterGroups = new Dictionary<string, ClusterBindPoseGroup>(StringComparer.Ordinal);
                    for (int s = 0; s < directClusterSpecs.Count; s++)
                    {
                        var spec = directClusterSpecs[s];
                        if (!TryResolveExplicitOrProxyBindPose(
                                context,
                                spec.Feature,
                                spec.Skeleton,
                                skeletonScope,
                                spec.SourcePath,
                                spec.ResolvedBone,
                                hasBindPoseOverride: false,
                                spec.AuthoredBindPose,
                                spec.MeshLocalSpace,
                                spec.SceneLocalSpace,
                                out var explicitBindPose,
                                out var bindError))
                        {
                            throw new InvalidOperationException(bindError);
                        }

                        int candidateSlot = -1;
                        if (spec.ImportedPreboundSlot.HasValue)
                        {
                            candidateSlot = spec.ImportedPreboundSlot.Value;
                        }
                        else if (boneTable.TryGetIndex(spec.ResolvedBone, spec.TargetPath, out int existingSlot))
                        {
                            candidateSlot = existingSlot;
                        }

                        string groupKey = candidateSlot >= 0
                            ? $"slot:{candidateSlot}"
                            : (spec.ResolvedBone != null
                                ? $"tr:{spec.ResolvedBone.GetInstanceID()}"
                                : $"null:{spec.TargetPath}");
                        if (!clusterGroups.TryGetValue(groupKey, out var grp))
                        {
                            grp = new ClusterBindPoseGroup(groupKey, candidateSlot, spec.ResolvedBone, spec.TargetPath);
                            clusterGroups.Add(groupKey, grp);
                        }

                        grp.Add(spec, CanonicalizeMatrixSignedZeros(explicitBindPose));
                    }

                    foreach (var grp in clusterGroups.Values
                                 .OrderBy(g => g.CandidateSlot >= 0 ? 0 : 1)
                                 .ThenBy(g => g.CandidateSlot)
                                 .ThenBy(g => g.TargetPath, StringComparer.Ordinal))
                    {
                        if (grp.ExceedsSpreadTolerance(1e-3f, out string firstPatchId, out string conflictingPatchId))
                        {
                            throw new InvalidOperationException(
                                $"Conflicting explicit cluster bindposes for bone '{grp.TargetPath}' between patch '{firstPatchId}' and patch '{conflictingPatchId}'.");
                        }

                        Matrix4x4 repBindPose = grp.SelectDeterministicRepresentative();
                        int slotIndex;
                        if (grp.CandidateSlot >= 0)
                        {
                            slotIndex = grp.CandidateSlot;
                            boneTable.SetDefinition(slotIndex, grp.ResolvedBone, grp.TargetPath, repBindPose);
                            for (int k = 0; k < grp.Specs.Count; k++)
                            {
                                boneTable.SyncAgreedSiblingBindPoses(
                                    grp.Specs[k].Spec.AgreedCandidateSlots,
                                    slotIndex,
                                    grp.ResolvedBone,
                                    grp.TargetPath,
                                    repBindPose,
                                    canonicalFinalizedSlots,
                                    finalizedPostEditSlots);
                            }
                        }
                        else
                        {
                            bool foundMatching = grp.ResolvedBone != null
                                ? boneTable.TryFindCanonicalSlotByTransformAndBindPose(grp.ResolvedBone, repBindPose, 1e-4f, out slotIndex)
                                : boneTable.TryFindCanonicalNullSlotByPathAndBindPose(grp.TargetPath, repBindPose, 1e-4f, out slotIndex);
                            if (!foundMatching)
                            {
                                slotIndex = boneTable.AddDistinctSlot(grp.ResolvedBone, grp.TargetPath, repBindPose);
                            }
                        }

                        explicitClusterBindPoseBySlot[slotIndex] = (grp.Specs[0].Spec.Input.PatchId, repBindPose);
                        canonicalFinalizedSlots.Add(slotIndex);
                        finalizedPostEditSlots.Add(slotIndex);
                        for (int k = 0; k < grp.Specs.Count; k++)
                        {
                            var member = grp.Specs[k].Spec;
                            boneTable.AddAlias(member.SourcePath, slotIndex);
                            slotByInputIndex[member.InputIndex][member.SourcePath] = slotIndex;
                            member.AssignedSlotIndex = slotIndex;
                        }
                    }
                }

                // Stage 2B-Canonical: Settle all non-override fallback requests level-by-level in parent-before-child depth order
                // BEFORE override slots run so an override matching pre-edit I can never suppress a canonical skeleton bindpose update,
                // and group/sort deterministically at each depth so slot indices and incoming-slot updates never depend on patch order.
                var canonicalFallbackByDepth = slotSpecs
                    .Where(sp => !sp.DefinesExplicitBindPose && !HasProxyOrBindPoseOverrideInChain(context, sp.Input, sp.SourcePath))
                    .GroupBy(sp => CountPathDepth(sp.SourcePath))
                    .OrderBy(g => g.Key);

                foreach (var depthGroup in canonicalFallbackByDepth)
                {
                    var keepUnchangedSpecs = new List<(PendingBoneSlotSpec Spec, int IncomingSlot)>();
                    var evaluatedFallbacks = new List<(PendingBoneSlotSpec Spec, int IncomingCandidate, Matrix4x4 BindPose)>();
                    var claimGroupsByIncoming = new Dictionary<int, ClusterBindPoseGroup>();

                    foreach (var spec in depthGroup)
                    {
                        bool hasChainSkeletonEdit = HasAuthoredSkeletonEditInChain(spec.SourcePath, spec.Skeleton, mergedSkeletonBonesByPath);
                        int incomingCandidate = -1;
                        if (spec.ImportedPreboundSlot.HasValue)
                        {
                            incomingCandidate = spec.ImportedPreboundSlot.Value;
                        }
                        else if (boneTable.TryGetIncomingIndex(spec.ResolvedBone, spec.TargetPath, out int existingIncSlot))
                        {
                            incomingCandidate = existingIncSlot;
                        }

                        if (incomingCandidate >= 0 && spec.ResolvedBone == null)
                        {
                            if (!canonicalFinalizedSlots.Contains(incomingCandidate))
                            {
                                keepUnchangedSpecs.Add((spec, incomingCandidate));
                            }
                            else
                            {
                                evaluatedFallbacks.Add((spec, incomingCandidate, CanonicalizeMatrixSignedZeros(boneTable.BindPoses[incomingCandidate])));
                            }

                            continue;
                        }

                        if (incomingCandidate >= 0 && !hasChainSkeletonEdit && !canonicalFinalizedSlots.Contains(incomingCandidate))
                        {
                            keepUnchangedSpecs.Add((spec, incomingCandidate));
                            continue;
                        }

                        if (!TryResolveFallbackBindPose(
                                context,
                                spec.Input,
                                slotByInputIndex[spec.InputIndex],
                                slotSpecs,
                                spec.Skeleton,
                                skeletonScope,
                                mergedSkeletonBonesByPath,
                                boneTable,
                                canonicalFinalizedSlots,
                                spec.SourcePath,
                                spec.ResolvedBone,
                                spec.SceneLocalSpace,
                                out var rawFallbackBindPose,
                                out string fallbackError))
                        {
                            throw new InvalidOperationException(fallbackError);
                        }

                        Matrix4x4 fallbackBindPose = CanonicalizeMatrixSignedZeros(rawFallbackBindPose);
                        evaluatedFallbacks.Add((spec, incomingCandidate, fallbackBindPose));
                        if (incomingCandidate >= 0 && !canonicalFinalizedSlots.Contains(incomingCandidate))
                        {
                            if (!claimGroupsByIncoming.TryGetValue(incomingCandidate, out var grp))
                            {
                                grp = new ClusterBindPoseGroup(
                                    $"inc:{incomingCandidate}",
                                    incomingCandidate,
                                    spec.ResolvedBone,
                                    spec.TargetPath);
                                claimGroupsByIncoming.Add(incomingCandidate, grp);
                            }

                            grp.Add(spec, fallbackBindPose);
                        }
                    }

                    foreach (var kv in claimGroupsByIncoming.OrderBy(p => p.Key))
                    {
                        var grp = kv.Value;
                        if (grp.ExceedsSpreadTolerance(1e-3f, out string firstId, out string conflictId))
                        {
                            throw new InvalidOperationException(
                                $"Conflicting canonical fallback bindposes for bone '{grp.TargetPath}' across patches '{firstId}' and '{conflictId}'.");
                        }

                        Matrix4x4 rep = grp.SelectDeterministicRepresentative();
                        boneTable.SetDefinition(kv.Key, grp.ResolvedBone, grp.TargetPath, rep);
                        canonicalFinalizedSlots.Add(kv.Key);
                        finalizedPostEditSlots.Add(kv.Key);
                        for (int k = 0; k < grp.Specs.Count; k++)
                        {
                            boneTable.SyncAgreedSiblingBindPoses(
                                grp.Specs[k].Spec.AgreedCandidateSlots,
                                kv.Key,
                                grp.ResolvedBone,
                                grp.TargetPath,
                                rep,
                                canonicalFinalizedSlots,
                                finalizedPostEditSlots);
                        }
                    }

                    for (int u = 0; u < keepUnchangedSpecs.Count; u++)
                    {
                        var (spec, incSlot) = keepUnchangedSpecs[u];
                        if (!canonicalFinalizedSlots.Contains(incSlot))
                        {
                            boneTable.AddAlias(spec.SourcePath, incSlot);
                            slotByInputIndex[spec.InputIndex][spec.SourcePath] = incSlot;
                            spec.AssignedSlotIndex = incSlot;
                        }
                        else if (spec.ResolvedBone == null)
                        {
                            evaluatedFallbacks.Add((spec, incSlot, CanonicalizeMatrixSignedZeros(boneTable.BindPoses[incSlot])));
                        }
                        else
                        {
                            // Another canonical fallback at the same depth finalized `incSlot`; route through evaluated settlement.
                            if (!TryResolveFallbackBindPose(
                                    context,
                                    spec.Input,
                                    slotByInputIndex[spec.InputIndex],
                                    slotSpecs,
                                    spec.Skeleton,
                                    skeletonScope,
                                    mergedSkeletonBonesByPath,
                                    boneTable,
                                    canonicalFinalizedSlots,
                                    spec.SourcePath,
                                    spec.ResolvedBone,
                                    spec.SceneLocalSpace,
                                    out var rawFb,
                                    out string fbErr))
                            {
                                throw new InvalidOperationException(fbErr);
                            }

                            evaluatedFallbacks.Add((spec, incSlot, CanonicalizeMatrixSignedZeros(rawFb)));
                        }
                    }

                    evaluatedFallbacks.Sort((a, b) =>
                    {
                        int c = StringComparer.Ordinal.Compare(a.Spec.TargetPath, b.Spec.TargetPath);
                        if (c != 0) return c;
                        c = CompareMatrixLexicographic(a.BindPose, b.BindPose);
                        if (c != 0) return c;
                        c = StringComparer.Ordinal.Compare(a.Spec.SourcePath, b.Spec.SourcePath);
                        return c != 0 ? c : a.Spec.SpecIndex.CompareTo(b.Spec.SpecIndex);
                    });

                    for (int f = 0; f < evaluatedFallbacks.Count; f++)
                    {
                        var (spec, incomingCandidate, fallbackBindPose) = evaluatedFallbacks[f];
                        int slotIndex;
                        if (incomingCandidate >= 0 &&
                            BindPosesApproximatelyEqual(boneTable.BindPoses[incomingCandidate], fallbackBindPose, 1e-4f))
                        {
                            slotIndex = incomingCandidate;
                        }
                        else
                        {
                            bool foundMatching = spec.ResolvedBone != null
                                ? boneTable.TryFindCanonicalSlotByTransformAndBindPose(spec.ResolvedBone, fallbackBindPose, 1e-4f, out slotIndex)
                                : boneTable.TryFindCanonicalNullSlotByPathAndBindPose(spec.TargetPath, fallbackBindPose, 1e-4f, out slotIndex);
                            if (!foundMatching)
                            {
                                slotIndex = boneTable.AddDistinctSlot(spec.ResolvedBone, spec.TargetPath, fallbackBindPose);
                            }
                        }

                        canonicalFinalizedSlots.Add(slotIndex);
                        finalizedPostEditSlots.Add(slotIndex);
                        boneTable.AddAlias(spec.SourcePath, slotIndex);
                        slotByInputIndex[spec.InputIndex][spec.SourcePath] = slotIndex;
                        spec.AssignedSlotIndex = slotIndex;
                    }
                }

                // Helper to allocate/reuse an override-derived slot safely after all canonical slots are settled.
                int AllocateOrReuseOverrideSlot(PendingBoneSlotSpec spec, Matrix4x4 desiredBindPose)
                {
                    desiredBindPose = CanonicalizeMatrixSignedZeros(desiredBindPose);
                    if (!spec.IsProxyOverride &&
                        spec.ResolvedBone == null &&
                        spec.ImportedPreboundSlot.HasValue &&
                        boneTable.IsScratchNullSlot(spec.ImportedPreboundSlot.Value))
                    {
                        int scratchSlot = spec.ImportedPreboundSlot.Value;
                        finalizedPostEditSlots.Add(scratchSlot);
                        return scratchSlot;
                    }

                    int slotIdx = -1;
                    bool found = spec.ResolvedBone != null
                        ? boneTable.TryFindSlotForOverride(
                            spec.ResolvedBone,
                            desiredBindPose,
                            1e-4f,
                            canonicalFinalizedSlots,
                            mergedSkeletonBonesByPath,
                            out slotIdx)
                        : boneTable.TryFindNullSlotByPathAndBindPose(spec.TargetPath, desiredBindPose, 1e-4f, out slotIdx);
                    if (!found)
                    {
                        slotIdx = boneTable.AddOverrideDerivedSlot(spec.ResolvedBone, spec.TargetPath, desiredBindPose);
                    }

                    finalizedPostEditSlots.Add(slotIdx);
                    return slotIdx;
                }

                // Stage 2C-1: Resolve all explicit override definitions (IsProxyOverride || HasBindPoseOverride), then sort
                // deterministically by (TargetPath, BindPose) BEFORE allocating slots so slot indices never depend on patch order.
                var resolvedExplicitOverrides = new List<(PendingBoneSlotSpec Spec, Matrix4x4 BindPose)>();
                for (int s = 0; s < slotSpecs.Count; s++)
                {
                    var spec = slotSpecs[s];
                    if (!spec.DefinesExplicitBindPose || (!spec.IsProxyOverride && !spec.HasBindPoseOverride))
                    {
                        continue;
                    }

                    if (!TryResolveExplicitOrProxyBindPose(
                            context,
                            spec.Feature,
                            spec.Skeleton,
                            skeletonScope,
                            spec.SourcePath,
                            spec.ResolvedBone,
                            spec.HasBindPoseOverride,
                            spec.AuthoredBindPose,
                            spec.MeshLocalSpace,
                            spec.SceneLocalSpace,
                            out var explicitBindPose,
                            out var bindError))
                    {
                        throw new InvalidOperationException(bindError);
                    }

                    resolvedExplicitOverrides.Add((spec, CanonicalizeMatrixSignedZeros(explicitBindPose)));
                }

                resolvedExplicitOverrides.Sort((a, b) =>
                {
                    int c = StringComparer.Ordinal.Compare(a.Spec.TargetPath, b.Spec.TargetPath);
                    if (c != 0) return c;
                    c = CompareMatrixLexicographic(a.BindPose, b.BindPose);
                    if (c != 0) return c;
                    c = StringComparer.Ordinal.Compare(a.Spec.SourcePath, b.Spec.SourcePath);
                    return c != 0 ? c : a.Spec.SpecIndex.CompareTo(b.Spec.SpecIndex);
                });

                for (int s = 0; s < resolvedExplicitOverrides.Count; s++)
                {
                    var (spec, bindPose) = resolvedExplicitOverrides[s];
                    int slotIndex = AllocateOrReuseOverrideSlot(spec, bindPose);
                    slotByInputIndex[spec.InputIndex][spec.SourcePath] = slotIndex;
                    spec.AssignedSlotIndex = slotIndex;
                }

                // Stage 2C-2: Resolve override-chained fallback requests level-by-level in parent-before-child depth order,
                // sorting deterministically within each depth level before allocating slots.
                var overrideFallbackByDepth = slotSpecs
                    .Where(sp => !sp.DefinesExplicitBindPose && HasProxyOrBindPoseOverrideInChain(context, sp.Input, sp.SourcePath))
                    .GroupBy(sp => CountPathDepth(sp.SourcePath))
                    .OrderBy(g => g.Key);

                foreach (var depthGroup in overrideFallbackByDepth)
                {
                    var resolvedAtDepth = new List<(PendingBoneSlotSpec Spec, Matrix4x4 BindPose)>();
                    foreach (var spec in depthGroup)
                    {
                        if (!TryResolveFallbackBindPose(
                                context,
                                spec.Input,
                                slotByInputIndex[spec.InputIndex],
                                slotSpecs,
                                spec.Skeleton,
                                skeletonScope,
                                mergedSkeletonBonesByPath,
                                boneTable,
                                finalizedPostEditSlots,
                                spec.SourcePath,
                                spec.ResolvedBone,
                                spec.SceneLocalSpace,
                                out var fallbackBindPose,
                                out string fallbackError))
                        {
                            throw new InvalidOperationException(fallbackError);
                        }

                        resolvedAtDepth.Add((spec, CanonicalizeMatrixSignedZeros(fallbackBindPose)));
                    }

                    resolvedAtDepth.Sort((a, b) =>
                    {
                        int c = StringComparer.Ordinal.Compare(a.Spec.TargetPath, b.Spec.TargetPath);
                        if (c != 0) return c;
                        c = CompareMatrixLexicographic(a.BindPose, b.BindPose);
                        if (c != 0) return c;
                        c = StringComparer.Ordinal.Compare(a.Spec.SourcePath, b.Spec.SourcePath);
                        return c != 0 ? c : a.Spec.SpecIndex.CompareTo(b.Spec.SpecIndex);
                    });

                    for (int s = 0; s < resolvedAtDepth.Count; s++)
                    {
                        var (spec, bindPose) = resolvedAtDepth[s];
                        int slotIndex = AllocateOrReuseOverrideSlot(spec, bindPose);
                        slotByInputIndex[spec.InputIndex][spec.SourcePath] = slotIndex;
                        spec.AssignedSlotIndex = slotIndex;
                    }
                }

                // Resolve optional feature.RootBonePath overrides after boneTable is finalized.
                for (int i = 0; i < inputs.Count; i++)
                {
                    var input = inputs[i];
                    var feature = (SkinWeightFeature)input.Feature;
                    string featureRootBonePath = FbxNameUtility.NormalizePath(feature.RootBonePath ?? string.Empty);
                    if (string.IsNullOrEmpty(featureRootBonePath))
                    {
                        continue;
                    }

                    var rootKey = new BoneOverrideKey(input.PatchId, featureRootBonePath);
                    Transform resolvedRoot = null;
                    if (context.BoneTransformOverrides != null &&
                        context.BoneTransformOverrides.TryGetValue(rootKey, out var overrideRoot) &&
                        overrideRoot != null)
                    {
                        resolvedRoot = overrideRoot;
                    }
                    else if (boneTable.TryGetTransform(featureRootBonePath, out var tableRoot) && tableRoot != null)
                    {
                        resolvedRoot = tableRoot;
                    }
                    else if (context.TargetRootTransform)
                    {
                        resolvedRoot = context.TargetRootTransform.Find(featureRootBonePath);
                    }

                    if (resolvedRoot != null)
                    {
                        outputRootBone = resolvedRoot;
                        outputRootBonePath = context.TargetRootTransform
                            ? FbxNameUtility.NormalizePath(AnimationUtility.CalculateTransformPath(resolvedRoot, context.TargetRootTransform))
                            : featureRootBonePath;
                    }
                    else
                    {
                        outputRootBonePath = featureRootBonePath;
                    }
                }

                var donorByInputPath = new Dictionary<string, DonorGroupKey>[inputs.Count];
                for (int i = 0; i < inputs.Count; i++)
                {
                    donorByInputPath[i] = new Dictionary<string, DonorGroupKey>(StringComparer.Ordinal);
                }

                for (int b = 0; b < slotSpecs.Count; b++)
                {
                    var spec = slotSpecs[b];
                    var candidateDonors = boneTable.ResolveSpecBaselineDonors(
                        spec.ImportedPreboundSlot,
                        spec.AgreedCandidateSlots,
                        spec.HasAuthoritativeDonorSet,
                        spec.VerifiedDonorSlots,
                        spec.DonorIsAmbiguousMultiSlot,
                        spec.ResolvedBone,
                        spec.SourcePath,
                        spec.TargetPath,
                        spec.IsProxyOverride);
                    bool pointsDirectlyToSingleDonor =
                        candidateDonors.Kind == DonorGroupKind.Slots &&
                        candidateDonors.Slots.Length == 1 &&
                        candidateDonors.Slots[0] == spec.AssignedSlotIndex;
                    donorByInputPath[spec.InputIndex][spec.SourcePath] = pointsDirectlyToSingleDonor
                        ? DonorGroupKey.Direct
                        : candidateDonors;

                    records.Add(new AppliedBoneBinding(
                        spec.Input.PatchId,
                        spec.SourcePath,
                        spec.TargetPath,
                        spec.ResolvedBone,
                        boneTable.BindPoses[spec.AssignedSlotIndex],
                        spec.AssignedSlotIndex,
                        spec.IsNew));
                }

                // Pass 3: Prepare delta indices and verify TotalOne baseline weights before accumulating signed deltas.
                var deltaIndicesByInput = new Dictionary<int, List<(int BoneIndex, double Delta)>>[inputs.Count];
                bool anyWeightDeltas = false;
                for (int i = 0; i < inputs.Count; i++)
                {
                    var input = inputs[i];
                    var feature = (SkinWeightFeature)input.Feature;
                    var deltaIndex = BuildDeltaIndex(
                        feature,
                        input.PayloadSource,
                        slotByInputIndex[i],
                        input.VertexMapping.VertexMap.TargetCount);
                    deltaIndicesByInput[i] = deltaIndex;
                    if (deltaIndex.Count > 0)
                    {
                        anyWeightDeltas = true;
                    }
                }

                HashSet<int>[] touchedSlotsByVertex = null;
                if (effectiveSkinLinkMode == SkinWeightLinkMode.TotalOne && (anyWeightDeltas || anyWeightOrBindposeModified))
                {
                    if (conflictingRawTargetMesh)
                    {
                        CleanupCreatedRoots(createdRoots);
                        return FeatureApplyResult.Failed(new ApplyDiagnostic(
                            ApplyDiagnosticSeverity.Error,
                            "SKIN_APPLY_PARTIAL_TOTAL_ONE_UNSUPPORTED",
                            "Conflicting verified raw target FBX meshes were supplied across TotalOne skin weight inputs."));
                    }

                    if (verifiedRawSkin == null && initialHasAnyWeights)
                    {
                        // Unity's Mesh.boneWeights stores normalized proportions only (e.g. raw 0.4 was already stretched to 1.0 on import),
                        // so adding signed TotalOne deltas onto normalized proportions cannot establish true post-patch influence totals.
                        CleanupCreatedRoots(createdRoots);
                        return FeatureApplyResult.Failed(new ApplyDiagnostic(
                            ApplyDiagnosticSeverity.Error,
                            "SKIN_APPLY_PARTIAL_TOTAL_ONE_UNSUPPORTED",
                            "Cannot apply TotalOne skin weight deltas to an already-skinned Unity Mesh without verified raw target FBX weights because Unity Mesh stores normalized proportions only."));
                    }

                    if (!TryEvaluateAndProjectTotalOneWeights(
                            context,
                            verifiedRawSkin,
                            verifiedSeedMap,
                            inputs,
                            slotByInputIndex,
                            deltaIndicesByInput,
                            boneTable,
                            mesh.vertexCount,
                            out var totalOneProjectedWeights,
                            out string totalOneEvalError))
                    {
                        CleanupCreatedRoots(createdRoots);
                        return FeatureApplyResult.Failed(new ApplyDiagnostic(
                            ApplyDiagnosticSeverity.Error,
                            "SKIN_APPLY_PARTIAL_TOTAL_ONE_UNSUPPORTED",
                            totalOneEvalError));
                    }

                    weightsByVertex = totalOneProjectedWeights;
                }
                else if (anyWeightDeltas)
                {
                    touchedSlotsByVertex = new HashSet<int>[weightsByVertex.Length];
                    var bucketIndicesByInput = new Dictionary<int, List<(NormalizeBucketKey Key, double Delta)>>[inputs.Count];
                    for (int i = 0; i < inputs.Count; i++)
                    {
                        var input = inputs[i];
                        var feature = (SkinWeightFeature)input.Feature;
                        bucketIndicesByInput[i] = BuildNormalizeBucketDeltaIndex(
                            feature,
                            input.PayloadSource,
                            slotByInputIndex[i],
                            donorByInputPath[i],
                            input.VertexMapping.VertexMap.TargetCount);
                    }

                    var perVertexBuckets = new Dictionary<NormalizeBucketKey, List<double>>[weightsByVertex.Length];
                    for (int i = 0; i < inputs.Count; i++)
                    {
                        var bucketIndex = bucketIndicesByInput[i];
                        if (bucketIndex == null || bucketIndex.Count == 0)
                        {
                            continue;
                        }

                        var mapping = inputs[i].VertexMapping;
                        var fullMap = mapping.FullTargetVertexMap ?? mapping.VertexMap;
                        int fullDomainCount = fullMap.TargetCount;
                        for (int v = 0; v < weightsByVertex.Length; v++)
                        {
                            var row = fullMap.GetRow(v);
                            var deltas = CalculateIndexedBucketDeltas(row, fullDomainCount, bucketIndex);
                            if (deltas == null || deltas.Count == 0)
                            {
                                continue;
                            }

                            var vBuckets = perVertexBuckets[v] ??= new Dictionary<NormalizeBucketKey, List<double>>();
                            foreach (var pair in deltas)
                            {
                                if (!vBuckets.TryGetValue(pair.Key, out var termList))
                                {
                                    termList = new List<double>(2);
                                    vBuckets.Add(pair.Key, termList);
                                }

                                termList.Add(pair.Value);
                            }
                        }
                    }

                    for (int v = 0; v < weightsByVertex.Length; v++)
                    {
                        var vBuckets = perVertexBuckets[v];
                        if (vBuckets == null || vBuckets.Count == 0)
                        {
                            continue;
                        }

                        var aggregate = weightsByVertex[v];

                        // Step 1: Settle all direct (non-redirected) buckets (`Donors.IsDirect`) onto `aggregate` FIRST using
                        // sorted-term summation. Any spec redirected to another incoming or split slot (including `NoDonors`)
                        // goes through `redirectedKeys` keyed by `SourcePath` so unrelated source paths never cancel each other
                        // or subtract from an unrelated bone sharing `kv.Key.Slot`.
                        var directSlotTerms = new Dictionary<int, List<double>>();
                        var redirectedKeys = new List<NormalizeBucketKey>();
                        foreach (var kv in vBuckets)
                        {
                            if (kv.Key.Donors.IsDirect)
                            {
                                if (!directSlotTerms.TryGetValue(kv.Key.Slot, out var dirTerms))
                                {
                                    dirTerms = new List<double>(kv.Value.Count + 1);
                                    directSlotTerms.Add(kv.Key.Slot, dirTerms);
                                }

                                dirTerms.AddRange(kv.Value);
                            }
                            else
                            {
                                redirectedKeys.Add(kv.Key);
                            }
                        }

                        foreach (var kv in directSlotTerms)
                        {
                            double netDeltaOnly = SumSortedTerms(kv.Value);
                            if (Math.Abs(netDeltaOnly) > 1e-12d)
                            {
                                (touchedSlotsByVertex[v] ??= new HashSet<int>()).Add(kv.Key);
                            }

                            if (boneTable.IsAmbiguousPathSlot(kv.Key) && Math.Abs(netDeltaOnly) > 1e-6d)
                            {
                                CleanupCreatedRoots(createdRoots);
                                return FeatureApplyResult.Failed(new ApplyDiagnostic(
                                    ApplyDiagnosticSeverity.Error,
                                    "SKIN_APPLY_AMBIGUOUS_BONE_SLOT",
                                    $"Skin weight patch applies non-zero delta ({netDeltaOnly:F4}) at vertex {v} to bone '{boneTable.GetSlotLabel(kv.Key)}' with ambiguous target bone slots."));
                            }

                            if (aggregate.TryGetValue(kv.Key, out double baselineVal) && baselineVal != 0d)
                            {
                                kv.Value.Add(baselineVal);
                            }

                            // Clamp negative direct balances at zero so an over-drained direct slot (e.g. B = 0.5 - 0.5 - 0.5)
                            // never erases an independent bone's positive redirected contribution sharing `kv.Key` in Step 3.
                            double settledDirect = SumSortedTerms(kv.Value);
                            aggregate[kv.Key] = settledDirect > 0d ? settledDirect : 0d;
                        }

                        if (redirectedKeys.Count == 0)
                        {
                            continue;
                        }

                        // Step 2: Drain negative redirected/split-slot buckets from their settled baseline donor groups
                        // in strict narrower-before-broader canonical order, never subtracting residual deficits from
                        // unrelated weights that happen to share `bk.Slot`.
                        redirectedKeys.Sort((a, b) =>
                        {
                            int c = a.Donors.Slots.Length.CompareTo(b.Donors.Slots.Length);
                            if (c != 0) return c;
                            c = a.Donors.Kind.CompareTo(b.Donors.Kind);
                            if (c != 0) return c;
                            c = StringComparer.Ordinal.Compare(a.Donors.Key, b.Donors.Key);
                            if (c != 0) return c;
                            c = StringComparer.Ordinal.Compare(a.SourcePath, b.SourcePath);
                            return c != 0 ? c : a.Slot.CompareTo(b.Slot);
                        });

                        var postDrainSlotTerms = new Dictionary<int, List<double>>();
                        for (int k = 0; k < redirectedKeys.Count; k++)
                        {
                            var bk = redirectedKeys[k];
                            double bucketSum = SumSortedTerms(vBuckets[bk]);
                            if ((boneTable.IsAmbiguousPathSlot(bk.Slot) && Math.Abs(bucketSum) > 1e-6d) ||
                                (bk.Donors.IsAmbiguous && bucketSum < -1e-6d))
                            {
                                CleanupCreatedRoots(createdRoots);
                                return FeatureApplyResult.Failed(new ApplyDiagnostic(
                                    ApplyDiagnosticSeverity.Error,
                                    "SKIN_APPLY_AMBIGUOUS_BONE_SLOT",
                                    $"Skin weight patch applies non-zero delta ({bucketSum:F4}) at vertex {v} to bone '{boneTable.GetSlotLabel(bk.Slot)}' with ambiguous target bone slots."));
                            }

                            if (bucketSum < -1e-12d)
                            {
                                var dSlots = bk.Donors.Slots;
                                for (int ds = 0; ds < dSlots.Length; ds++)
                                {
                                    if (aggregate.TryGetValue(dSlots[ds], out double dwBefore) && dwBefore > 1e-12d)
                                    {
                                        (touchedSlotsByVertex[v] ??= new HashSet<int>()).Add(dSlots[ds]);
                                    }
                                }

                                bucketSum = DrainBucketDeficitFromDonors(aggregate, dSlots, bucketSum);
                            }

                            if (bucketSum > 0d)
                            {
                                if (bucketSum > 1e-12d)
                                {
                                    (touchedSlotsByVertex[v] ??= new HashSet<int>()).Add(bk.Slot);
                                }

                                if (!postDrainSlotTerms.TryGetValue(bk.Slot, out var sTerms))
                                {
                                    sTerms = new List<double>(2);
                                    postDrainSlotTerms.Add(bk.Slot, sTerms);
                                }

                                sTerms.Add(bucketSum);
                            }
                        }

                        // Step 3: Settle positive post-drain redirected/split-slot terms onto `aggregate`.
                        foreach (var kv in postDrainSlotTerms)
                        {
                            if (aggregate.TryGetValue(kv.Key, out double existingVal) && existingVal > 0d)
                            {
                                kv.Value.Add(existingVal);
                            }

                            aggregate[kv.Key] = SumSortedTerms(kv.Value);
                        }
                    }
                }

                if (!anyWeightOrBindposeModified && !anyLocalSkeletonInput)
                {
                    return FeatureApplyResult.Skipped();
                }

                // Finalize accumulated weights ONCE after all patches have contributed signed deltas.
                if (anyWeightOrBindposeModified)
                {
                    int maxBonesPerVertex = Mathf.Clamp(context.MaxBonesPerVertex, 1, byte.MaxValue);
                    HashSet<int> bindposeModifiedNullSlots = null;
                    if (effectiveSkinLinkMode == SkinWeightLinkMode.TotalOne)
                    {
                        var checkList = new List<(int Index, double Weight)>(maxBonesPerVertex);
                        for (int v = 0; v < weightsByVertex.Length; v++)
                        {
                            if (!TryValidateTotalOneVertexWeights(weightsByVertex[v], boneTable, maxBonesPerVertex, checkList, v, out string vertErr))
                            {
                                CleanupCreatedRoots(createdRoots);
                                return FeatureApplyResult.Failed(new ApplyDiagnostic(
                                    ApplyDiagnosticSeverity.Error,
                                    "SKIN_APPLY_PARTIAL_TOTAL_ONE_UNSUPPORTED",
                                    vertErr));
                            }
                        }
                    }
                    else
                    {
                        for (int s = 0; s < initialIncomingBindPoses.Length && s < boneTable.Transforms.Count; s++)
                        {
                            if (boneTable.IsIncomingSlot(s) &&
                                boneTable.Transforms[s] == null &&
                                !BindPosesApproximatelyEqual(initialIncomingBindPoses[s], boneTable.BindPoses[s], 1e-5f))
                            {
                                (bindposeModifiedNullSlots ??= new HashSet<int>()).Add(s);
                            }
                        }

                        for (int v = 0; v < weightsByVertex.Length; v++)
                        {
                            var vw = weightsByVertex[v];
                            if (vw == null) continue;
                            var vTouched = touchedSlotsByVertex != null ? touchedSlotsByVertex[v] : null;
                            foreach (var pair in vw)
                            {
                                if (pair.Value <= 1e-6d) continue;
                                if (IsDisallowedNullSlotForVertex(pair.Key, boneTable, vTouched, bindposeModifiedNullSlots))
                                {
                                    CleanupCreatedRoots(createdRoots);
                                    return FeatureApplyResult.Failed(new ApplyDiagnostic(
                                        ApplyDiagnosticSeverity.Error,
                                        "SKIN_APPLY_UNRESOLVED_BONE_SLOT",
                                        $"Skin weight patch assigns positive weight {pair.Value:F4} at vertex {v} to unresolved or null bone '{boneTable.GetSlotLabel(pair.Key)}'."));
                                }
                            }
                        }
                    }

                    bool limitToFour = maxBonesPerVertex <= 4;
                    var selectedWeights = new List<(int Index, double Weight)>(maxBonesPerVertex);
                    using var outputWeights = new NativeList<BoneWeight1>(Allocator.Temp);
                    using var outputCounts = new NativeList<byte>(Allocator.Temp);

                    int verticesOverFour = 0;
                    int verticesOverByteLimit = 0;
                    int maxInfluences = 0;

                    for (int v = 0; v < weightsByVertex.Length; v++)
                    {
                        var vTouched = touchedSlotsByVertex != null ? touchedSlotsByVertex[v] : null;
                        int positiveCount = KeepTopWeights(
                            maxBonesPerVertex,
                            weightsByVertex[v],
                            boneTable,
                            vTouched,
                            bindposeModifiedNullSlots,
                            selectedWeights);
                        if (positiveCount > maxInfluences) maxInfluences = positiveCount;
                        if (positiveCount > 4) verticesOverFour++;
                        if (positiveCount > byte.MaxValue) verticesOverByteLimit++;

                        for (int i = 0; i < selectedWeights.Count; i++)
                        {
                            var entry = selectedWeights[i];
                            outputWeights.Add(new BoneWeight1 { boneIndex = entry.Index, weight = (float)entry.Weight });
                        }

                        outputCounts.Add((byte)selectedWeights.Count);
                    }

                    if (!limitToFour &&
                        maxInfluences > 4 &&
                        UnitySkinWeightMeshLayoutUtility.RequiresUnlimitedLayoutRebuild(mesh))
                    {
                        UnitySkinWeightMeshLayoutUtility.RemoveSkinWeightVertexAttributes(mesh);
                    }

                    using var outputCountArray = outputCounts.ToArray(Allocator.Temp);
                    using var outputWeightArray = outputWeights.ToArray(Allocator.Temp);
                    boneTable.CompactUnreferencedScratchSlots(outputWeightArray, records);
                    mesh.bindposes = boneTable.BindPoses.ToArray();
                    if (outputWeightArray.Length == 0)
                    {
                        if (mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.BlendWeight) ||
                            mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.BlendIndices))
                        {
                            UnitySkinWeightMeshLayoutUtility.RemoveSkinWeightVertexAttributes(mesh);
                        }
                    }
                    else
                    {
                        mesh.SetBoneWeights(outputCountArray, outputWeightArray);
                    }
                    context.WorkingBones = boneTable.Transforms.ToArray();
                    context.AppliedBoneBindings.AddRange(records);
                    context.SkinWeightInfluenceSummary = new BlendShareSkinWeightInfluenceSummary(
                        verticesOverFour,
                        verticesOverByteLimit,
                        maxInfluences);
                }

                // Materialize any remaining missing unweighted skeleton bones ONLY AFTER all weight/bindpose validations succeed.
                if (anyLocalSkeletonInput && context.CreateMissingBones && context.TargetRootTransform != null)
                {
                    for (int i = 0; i < inputs.Count; i++)
                    {
                        var input = inputs[i];
                        if (!input.CreateMissingBones) continue;
                        var skel = input.Skeleton;
                        if (skel?.Nodes == null || skel.Nodes.Count == 0) continue;

                        var sceneLocal = input.VertexMapping.SpaceContract.EffectiveStoredToUnitySceneLocal;
                        var orderedPaths = skel.GetNodePathsInHierarchyOrder();
                        for (int b = 0; b < orderedPaths.Count; b++)
                        {
                            string bonePath = orderedPaths[b];
                            if (context.BoneTransformOverrides != null &&
                                context.BoneTransformOverrides.TryGetValue(new BoneOverrideKey(input.PatchId, bonePath), out var ov) &&
                                ov != null)
                            {
                                continue;
                            }

                            if (context.TargetRootTransform.Find(bonePath) == null)
                            {
                                if (!UnitySkeletonWriter.EnsureBoneHierarchy(
                                        context.TargetRootTransform,
                                        skel,
                                        bonePath,
                                        sceneLocal,
                                        skeletonScope,
                                        out _,
                                        out var topmostCreated,
                                        out string createErr,
                                        input.PatchId,
                                        context.BoneTransformOverrides))
                                {
                                    throw new InvalidOperationException(createErr);
                                }

                                if (topmostCreated && !createdRoots.Contains(topmostCreated))
                                {
                                    createdRoots.Add(topmostCreated);
                                }
                            }
                        }
                    }
                }

                context.OutputRootBone = outputRootBone;
                context.OutputRootBonePath = outputRootBonePath;
                for (int r = 0; r < createdRoots.Count; r++)
                {
                    if (createdRoots[r] && !context.CreatedBoneRoots.Contains(createdRoots[r]))
                    {
                        context.CreatedBoneRoots.Add(createdRoots[r]);
                    }
                }

                if (anyLocalSkeletonInput)
                {
                    foreach (var entry in mergedSkeletonBonesByPath.Values
                                 .Where(item => (context.SharedSkeletonScope == null || localAuthoredBonePaths.Contains(item.Record.Path)) &&
                                                (context.TargetRootTransform == null || context.TargetRootTransform.Find(item.Record.Path) != null))
                                 .OrderBy(item => CountPathDepth(item.Record.Path))
                                 .ThenBy(item => item.Record.Path, StringComparer.Ordinal))
                    {
                        context.AppliedSkeletonBones.Add(entry.Record);
                    }
                }

                return FeatureApplyResult.Applied();
            }
            catch (Exception ex)
            {
                CleanupCreatedRoots(createdRoots);
                return FeatureApplyResult.Failed(new ApplyDiagnostic(
                    ApplyDiagnosticSeverity.Error,
                    "SKIN_APPLY_FAILED",
                    ex.Message));
            }
        }

        private sealed class PendingBoneSlotSpec
        {
            public readonly int SpecIndex;
            public readonly int InputIndex;
            public readonly FeatureApplyInput Input;
            public readonly SkinWeightFeature Feature;
            public readonly SkeletonDocument Skeleton;
            public readonly string SourcePath;
            public readonly string TargetPath;
            public readonly Transform ResolvedBone;
            public readonly bool IsNew;
            public readonly bool IsProxyOverride;
            public readonly int? ImportedPreboundSlot;
            public readonly int[] AgreedCandidateSlots;
            public readonly bool HasAuthoritativeDonorSet;
            public readonly int[] VerifiedDonorSlots;
            public readonly bool DonorIsAmbiguousMultiSlot;
            public readonly bool HasBindPoseOverride;
            public readonly Matrix4x4 AuthoredBindPose;
            public readonly bool HasClusterMatrices;
            public readonly FbxMatrix4x4 MeshLocalSpace;
            public readonly FbxMatrix4x4 SceneLocalSpace;
            public int AssignedSlotIndex = -1;

            public bool DefinesExplicitBindPose => IsProxyOverride || HasBindPoseOverride || HasClusterMatrices;

            public PendingBoneSlotSpec(
                int specIndex,
                int inputIndex,
                FeatureApplyInput input,
                SkinWeightFeature feature,
                SkeletonDocument skeleton,
                string sourcePath,
                string targetPath,
                Transform resolvedBone,
                bool isNew,
                bool isProxyOverride,
                int? importedPreboundSlot,
                int[] agreedCandidateSlots,
                bool hasAuthoritativeDonorSet,
                int[] verifiedDonorSlots,
                bool donorIsAmbiguousMultiSlot,
                bool hasBindPoseOverride,
                Matrix4x4 authoredBindPose,
                bool hasClusterMatrices,
                FbxMatrix4x4 meshLocalSpace,
                FbxMatrix4x4 sceneLocalSpace)
            {
                SpecIndex = specIndex;
                InputIndex = inputIndex;
                Input = input;
                Feature = feature;
                Skeleton = skeleton;
                SourcePath = sourcePath;
                TargetPath = targetPath;
                ResolvedBone = resolvedBone;
                IsNew = isNew;
                IsProxyOverride = isProxyOverride;
                ImportedPreboundSlot = importedPreboundSlot;
                AgreedCandidateSlots = agreedCandidateSlots;
                HasAuthoritativeDonorSet = hasAuthoritativeDonorSet;
                VerifiedDonorSlots = verifiedDonorSlots;
                DonorIsAmbiguousMultiSlot = donorIsAmbiguousMultiSlot;
                HasBindPoseOverride = hasBindPoseOverride;
                AuthoredBindPose = authoredBindPose;
                HasClusterMatrices = hasClusterMatrices;
                MeshLocalSpace = meshLocalSpace;
                SceneLocalSpace = sceneLocalSpace;
            }
        }

        private static bool HasProxyOverrideInChain(
            UnityMeshApplyContext context,
            string patchId,
            string sourcePath)
        {
            if (context.BoneTransformOverrides == null || context.BoneTransformOverrides.Count == 0)
            {
                return false;
            }

            string curr = FbxNameUtility.NormalizePath(sourcePath ?? string.Empty);
            while (!string.IsNullOrEmpty(curr))
            {
                if (context.BoneTransformOverrides.TryGetValue(new BoneOverrideKey(patchId, curr), out var ov) && ov != null)
                {
                    return true;
                }

                curr = GetParentPath(curr);
            }

            return false;
        }

        private static bool HasProxyOrBindPoseOverrideInChain(
            UnityMeshApplyContext context,
            FeatureApplyInput input,
            string sourcePath)
        {
            string curr = FbxNameUtility.NormalizePath(sourcePath ?? string.Empty);
            while (!string.IsNullOrEmpty(curr))
            {
                if (context.BoneTransformOverrides != null &&
                    context.BoneTransformOverrides.TryGetValue(new BoneOverrideKey(input.PatchId, curr), out var ov) &&
                    ov != null)
                {
                    return true;
                }

                if (input.BindPoseOverrides != null && input.BindPoseOverrides.ContainsKey(curr))
                {
                    return true;
                }

                curr = GetParentPath(curr);
            }

            return false;
        }

        private static bool HasAuthoredSkeletonEditInChain(
            string sourcePath,
            SkeletonDocument skeleton,
            IReadOnlyDictionary<string, (string PatchId, GeneratedSkeletonBoneRecord Record)> mergedSkeletonBonesByPath)
        {
            string curr = FbxNameUtility.NormalizePath(sourcePath ?? string.Empty);
            while (!string.IsNullOrEmpty(curr))
            {
                if ((mergedSkeletonBonesByPath != null && mergedSkeletonBonesByPath.ContainsKey(curr)) ||
                    skeleton?.GetNode(curr) != null)
                {
                    return true;
                }

                curr = GetParentPath(curr);
            }

            return false;
        }

        private static void CleanupCreatedRoots(List<Transform> createdRoots)
        {
            if (createdRoots == null) return;
            for (int i = 0; i < createdRoots.Count; i++)
            {
                if (createdRoots[i]) UnityEngine.Object.DestroyImmediate(createdRoots[i].gameObject);
            }
        }

        private static bool TryValidateTotalOneVertexWeights(
            Dictionary<int, double> vertexWeights,
            BoneTable boneTable,
            int maxBonesPerVertex,
            List<(int Index, double Weight)> scratchList,
            int vertexIndex,
            out string error)
        {
            error = null;
            scratchList.Clear();
            var positiveTerms = new List<double>(vertexWeights != null ? vertexWeights.Count : 0);

            if (vertexWeights != null)
            {
                foreach (var pair in vertexWeights)
                {
                    if (double.IsNaN(pair.Value) || double.IsInfinity(pair.Value))
                    {
                        error = $"Patch skin weight mode is TotalOne and vertex {vertexIndex} has non-finite post-patch bone weight.";
                        return false;
                    }

                    if (pair.Value <= 0d)
                    {
                        continue;
                    }

                    if (pair.Key < 0 || pair.Key >= boneTable.BonePaths.Count || boneTable.Transforms[pair.Key] == null)
                    {
                        if (pair.Value <= 1e-6d)
                        {
                            continue;
                        }

                        string boneLabel = (pair.Key >= 0 && pair.Key < boneTable.BonePaths.Count)
                            ? boneTable.BonePaths[pair.Key]
                            : $"slot#{pair.Key}";
                        error = $"Patch skin weight mode is TotalOne and vertex {vertexIndex} assigns positive weight {pair.Value:F4} to unresolved or null bone '{boneLabel}'.";
                        return false;
                    }

                    positiveTerms.Add(pair.Value);
                    scratchList.Add((pair.Key, pair.Value));
                }
            }

            const double k_PostProjectionSumTolerance = 1e-4d + 1e-12d;
            double positiveSum = SumSortedTerms(positiveTerms);
            if (Math.Abs(positiveSum - 1d) > k_PostProjectionSumTolerance)
            {
                error = $"Patch skin weight mode is TotalOne and vertex {vertexIndex} has post-patch bone weight sum {positiveSum:F4} (!= 1.0); Unity SkinnedMeshRenderer cannot preserve unnormalized residual bind-shape influence.";
                return false;
            }

            if (scratchList.Count > maxBonesPerVertex)
            {
                scratchList.Sort((left, right) => CompareInfluencesDeterministic(left, right, boneTable));
                var keptTerms = new List<double>(maxBonesPerVertex);
                for (int k = 0; k < maxBonesPerVertex; k++)
                {
                    keptTerms.Add(scratchList[k].Weight);
                }

                double keptSum = SumSortedTerms(keptTerms);
                if (Math.Abs(keptSum - 1d) > k_PostProjectionSumTolerance)
                {
                    error = $"Patch skin weight mode is TotalOne and clamping vertex {vertexIndex} to MaxBonesPerVertex={maxBonesPerVertex} reduces bone weight sum to {keptSum:F4} (< 1.0).";
                    return false;
                }
            }

            return true;
        }

        private static bool TryEvaluateAndProjectTotalOneWeights(
            UnityMeshApplyContext context,
            VerifiedRawTargetSkinContext verifiedRawSkin,
            CsrVertexMap verifiedSeedMap,
            IReadOnlyList<FeatureApplyInput> inputs,
            Dictionary<string, int>[] slotByInputIndex,
            Dictionary<int, List<(int BoneIndex, double Delta)>>[] deltaIndicesByInput,
            BoneTable boneTable,
            int vertexCount,
            out Dictionary<int, double>[] projectedWeightsByVertex,
            out string error)
        {
            projectedWeightsByVertex = new Dictionary<int, double>[vertexCount];
            for (int v = 0; v < vertexCount; v++)
            {
                projectedWeightsByVertex[v] = new Dictionary<int, double>();
            }

            error = null;
            IFbxMesh fbxDomainRawMesh = verifiedRawSkin?.RawMesh;
            CsrVertexMap fbxDomainMap = null;
            int fbxDomainCpCount = 0;

            if (verifiedRawSkin != null)
            {
                fbxDomainMap = verifiedSeedMap ?? verifiedRawSkin.FullTargetVertexMap;
                fbxDomainCpCount = verifiedRawSkin.RawMesh.ControlPointCount;
                if (fbxDomainMap == null || fbxDomainMap.SourceCount != vertexCount || fbxDomainMap.TargetCount != fbxDomainCpCount)
                {
                    error = "Verified raw target FBX mesh control point count does not match full-target vertex mapping domain.";
                    return false;
                }
            }
            else
            {
                for (int i = 0; i < inputs.Count; i++)
                {
                    var inRaw = inputs[i].VerifiedRawTargetSkin;
                    var srcKind = inputs[i].VertexMapping.SourceKind;
                    if (inRaw?.RawMesh != null &&
                        (srcKind == VertexMappingSourceKind.FbxInverseMapComputed ||
                         srcKind == VertexMappingSourceKind.FbxInverseMapCached))
                    {
                        var candMap = inRaw.FullTargetVertexMap
                                      ?? inputs[i].VertexMapping.FullTargetVertexMap
                                      ?? inputs[i].VertexMapping.VertexMap;
                        int candCpCount = inRaw.RawMesh.ControlPointCount;
                        if (candMap != null && candMap.SourceCount == vertexCount && candMap.TargetCount == candCpCount)
                        {
                            fbxDomainRawMesh = inRaw.RawMesh;
                            fbxDomainMap = candMap;
                            fbxDomainCpCount = candCpCount;
                            break;
                        }
                    }
                }
            }

            var rawCpWeightsBySlot = new Dictionary<int, List<(int SlotIndex, double Weight)>>();
            if (verifiedRawSkin != null)
            {
                var mappedCpSet = new HashSet<int>();
                for (int v = 0; v < vertexCount; v++)
                {
                    var row = fbxDomainMap.GetRow(v);
                    for (int r = 0; r < row.Length; r++)
                    {
                        if ((uint)row[r] < (uint)fbxDomainCpCount)
                        {
                            mappedCpSet.Add(row[r]);
                        }
                    }
                }

                var targetSkin = SkinWeightFeatureExtractor.FindPrimarySkinDeformer(verifiedRawSkin.RawMesh);
                if (!TryReadRawTotalOneSkinWeights(
                        targetSkin,
                        fbxDomainCpCount,
                        mappedCpSet,
                        out var weightsByBonePath,
                        out var unlinkedWeightsByCp,
                        out var targetLinkMode,
                        out error))
                {
                    return false;
                }

                if (targetLinkMode.HasValue && targetLinkMode.Value != FbxClusterLinkMode.TotalOne)
                {
                    error = $"Target FBX mesh uses link mode '{targetLinkMode.Value}', which is incompatible with a TotalOne skin weight patch.";
                    return false;
                }

                if (unlinkedWeightsByCp.Count > 0)
                {
                    int unlinkedNullSlot = boneTable.AddScratchNullSlot(".__unlinked_raw_cluster");
                    foreach (var uKv in unlinkedWeightsByCp)
                    {
                        if (uKv.Value <= 0d) continue;
                        if (!rawCpWeightsBySlot.TryGetValue(uKv.Key, out var cpList))
                        {
                            cpList = new List<(int, double)>();
                            rawCpWeightsBySlot[uKv.Key] = cpList;
                        }

                        cpList.Add((unlinkedNullSlot, uKv.Value));
                    }
                }

                foreach (var kv in weightsByBonePath)
                {
                    string normFbxBonePath = FbxNameUtility.NormalizePath(kv.Key);
                    if (!TryResolveUnifiedRawBoneSlot(
                            context,
                            inputs,
                            slotByInputIndex,
                            verifiedRawSkin,
                            boneTable,
                            normFbxBonePath,
                            out int slotIndex,
                            out bool resolvedSlot,
                            out string slotConflictError))
                    {
                        foreach (var cpKv in kv.Value)
                        {
                            if (cpKv.Value > 0d && mappedCpSet.Contains(cpKv.Key))
                            {
                                error = slotConflictError;
                                return false;
                            }
                        }

                        continue;
                    }

                    bool hasAnyPositiveMappedWeight = false;
                    foreach (var cpKv in kv.Value)
                    {
                        if (cpKv.Value <= 0d || !mappedCpSet.Contains(cpKv.Key))
                        {
                            continue;
                        }

                        if (!resolvedSlot || slotIndex < 0 || slotIndex >= boneTable.Transforms.Count)
                        {
                            slotIndex = boneTable.AddScratchNullSlot(normFbxBonePath);
                            resolvedSlot = true;
                        }

                        hasAnyPositiveMappedWeight = true;
                        if (!rawCpWeightsBySlot.TryGetValue(cpKv.Key, out var cpList))
                        {
                            cpList = new List<(int, double)>();
                            rawCpWeightsBySlot[cpKv.Key] = cpList;
                        }

                        cpList.Add((slotIndex, cpKv.Value));
                    }

                    if (hasAnyPositiveMappedWeight && slotByInputIndex != null)
                    {
                        for (int i = 0; i < inputs.Count && i < slotByInputIndex.Length; i++)
                        {
                            if (slotByInputIndex[i] != null &&
                                slotByInputIndex[i].TryGetValue(normFbxBonePath, out int inputSlot) &&
                                inputSlot != slotIndex)
                            {
                                error = $"Incompatible bone slot destinations for positive TotalOne target bone '{normFbxBonePath}' across patches (baseline resolved to slot {slotIndex}, whereas patch '{inputs[i].PatchId}' resolved '{normFbxBonePath}' to slot {inputSlot}).";
                                return false;
                            }
                        }
                    }
                }
            }

            // Partition inputs into:
            //  - `fbxDomainInputs`: inputs with proven direct FbxInverseMap correspondence against `fbxDomainRawMesh`
            //    (combined per-CP directly with `rawCpWeightsBySlot`).
            //  - `separateInputs`: each non-direct input (`EmbeddedMapping*` / `ComposedReferenceMesh*`) stays in its own
            //    independent control-point domain (cached `CsrVertexMap` instances only share integer row structure, not
            //    guaranteed physical CP numbering across distinct reference FBXs).
            var fbxDomainInputs = new List<int>();
            var separateInputs = new List<int>();
            var provenSharedFbxDomainByInput = new bool[inputs.Count];
            for (int i = 0; i < inputs.Count; i++)
            {
                bool provenSharedFbxDomain =
                    fbxDomainRawMesh != null &&
                    fbxDomainMap != null &&
                    inputs[i].VerifiedRawTargetSkin != null &&
                    ReferenceEquals(inputs[i].VerifiedRawTargetSkin.RawMesh, fbxDomainRawMesh) &&
                    (inputs[i].VertexMapping.SourceKind == VertexMappingSourceKind.FbxInverseMapComputed ||
                     inputs[i].VertexMapping.SourceKind == VertexMappingSourceKind.FbxInverseMapCached) &&
                    (inputs[i].VertexMapping.FullTargetVertexMap == null ||
                     EqualsFilteredPrefix(inputs[i].VertexMapping.FullTargetVertexMap, fbxDomainMap)) &&
                    EqualsFilteredPrefix(inputs[i].VertexMapping.VertexMap, fbxDomainMap);
                provenSharedFbxDomainByInput[i] = provenSharedFbxDomain;

                if (provenSharedFbxDomain)
                {
                    if (deltaIndicesByInput[i] != null && deltaIndicesByInput[i].Count > 0)
                    {
                        fbxDomainInputs.Add(i);
                    }
                }
                else if (deltaIndicesByInput[i] != null && deltaIndicesByInput[i].Count > 0)
                {
                    separateInputs.Add(i);
                }
            }

            for (int i = 0; i < inputs.Count; i++)
            {
                var checkMap = provenSharedFbxDomainByInput[i]
                    ? fbxDomainMap
                    : (inputs[i].VertexMapping.FullTargetVertexMap ?? inputs[i].VertexMapping.VertexMap);
                int checkDomain = checkMap.TargetCount;
                for (int v = 0; v < vertexCount; v++)
                {
                    var checkRow = checkMap.GetRow(v);
                    bool anyValid = false;
                    for (int r = 0; r < checkRow.Length; r++)
                    {
                        if ((uint)checkRow[r] < (uint)checkDomain)
                        {
                            anyValid = true;
                            break;
                        }
                    }

                    if (!anyValid)
                    {
                        error = $"Unity vertex {v} has no mapped FBX control points for TotalOne skin weight evaluation.";
                        return false;
                    }
                }
            }

            const int k_MaxUnalignedCrossDomainCombinations = 256;
            bool includeFbxDomain = verifiedRawSkin != null || fbxDomainInputs.Count > 0;
            CsrVertexMap fallbackCheckMap = inputs[0].VertexMapping.FullTargetVertexMap ?? inputs[0].VertexMapping.VertexMap;
            var cpSlotTermsScratch = new Dictionary<int, List<double>>();
            var samplePositiveTermsScratch = new List<double>();
            var combinedSampleScratch = new Dictionary<int, double>();
            var comboSlotTermsScratch = new Dictionary<int, List<double>>();

            for (int v = 0; v < vertexCount; v++)
            {
                var constantTermsBySlot = new Dictionary<int, List<double>>();
                var varyingDomains = new List<List<Dictionary<int, double>>>();
                bool hasAnyDomain = false;

                if (includeFbxDomain)
                {
                    var baseRow = fbxDomainMap.GetRow(v);
                    var fbxVecs = new List<Dictionary<int, double>>(baseRow.Length);

                    for (int r = 0; r < baseRow.Length; r++)
                    {
                        int cp = baseRow[r];
                        if ((uint)cp >= (uint)fbxDomainCpCount) continue;

                        cpSlotTermsScratch.Clear();
                        if (rawCpWeightsBySlot.TryGetValue(cp, out var rawList))
                        {
                            for (int k = 0; k < rawList.Count; k++)
                            {
                                var item = rawList[k];
                                if (item.Weight != 0d)
                                {
                                    AddSlotTerm(cpSlotTermsScratch, item.SlotIndex, item.Weight);
                                }
                            }
                        }

                        for (int m = 0; m < fbxDomainInputs.Count; m++)
                        {
                            int inputIdx = fbxDomainInputs[m];
                            var dIdx = deltaIndicesByInput[inputIdx];
                            int inPatchDomain = inputs[inputIdx].VertexMapping.VertexMap.TargetCount;
                            if ((uint)cp >= (uint)inPatchDomain || !dIdx.TryGetValue(cp, out var dList) || dList == null)
                            {
                                continue;
                            }

                            for (int d = 0; d < dList.Count; d++)
                            {
                                var item = dList[d];
                                if (item.BoneIndex >= 0 && item.Delta != 0d)
                                {
                                    AddSlotTerm(cpSlotTermsScratch, item.BoneIndex, item.Delta);
                                }
                            }
                        }

                        fbxVecs.Add(BuildSettledSlotVector(cpSlotTermsScratch));
                    }

                    if (fbxVecs.Count <= 0)
                    {
                        error = $"Unity vertex {v} has no mapped FBX control points for TotalOne skin weight evaluation.";
                        return false;
                    }

                    hasAnyDomain = true;
                    if (AreAllVectorsIdentical(fbxVecs))
                    {
                        foreach (var kv in fbxVecs[0])
                        {
                            AddSlotTerm(constantTermsBySlot, kv.Key, kv.Value);
                        }
                    }
                    else
                    {
                        varyingDomains.Add(fbxVecs);
                    }
                }

                for (int s = 0; s < separateInputs.Count; s++)
                {
                    int inputIdx = separateInputs[s];
                    var dIdx = deltaIndicesByInput[inputIdx];
                    var inFullMap = inputs[inputIdx].VertexMapping.FullTargetVertexMap ?? inputs[inputIdx].VertexMapping.VertexMap;
                    int inFullDomain = inFullMap.TargetCount;
                    int inPatchDomain = inputs[inputIdx].VertexMapping.VertexMap.TargetCount;
                    var inRow = inFullMap.GetRow(v);
                    var inVecs = new List<Dictionary<int, double>>(inRow.Length);
                    bool anyNonZero = false;

                    for (int r = 0; r < inRow.Length; r++)
                    {
                        int cp = inRow[r];
                        if ((uint)cp >= (uint)inFullDomain) continue;

                        cpSlotTermsScratch.Clear();
                        if ((uint)cp < (uint)inPatchDomain && dIdx.TryGetValue(cp, out var dList) && dList != null)
                        {
                            for (int d = 0; d < dList.Count; d++)
                            {
                                var item = dList[d];
                                if (item.BoneIndex >= 0 && item.Delta != 0d)
                                {
                                    AddSlotTerm(cpSlotTermsScratch, item.BoneIndex, item.Delta);
                                }
                            }
                        }

                        var settled = BuildSettledSlotVector(cpSlotTermsScratch);
                        if (settled.Count > 0)
                        {
                            anyNonZero = true;
                        }

                        inVecs.Add(settled);
                    }

                    if (inVecs.Count <= 0)
                    {
                        error = $"Unity vertex {v} has no mapped FBX control points for TotalOne skin weight evaluation.";
                        return false;
                    }

                    if (!anyNonZero && hasAnyDomain)
                    {
                        continue;
                    }

                    hasAnyDomain = true;
                    if (AreAllVectorsIdentical(inVecs))
                    {
                        foreach (var kv in inVecs[0])
                        {
                            AddSlotTerm(constantTermsBySlot, kv.Key, kv.Value);
                        }
                    }
                    else
                    {
                        varyingDomains.Add(inVecs);
                    }
                }

                if (!hasAnyDomain)
                {
                    var fbRow = fallbackCheckMap.GetRow(v);
                    int validFb = 0;
                    for (int r = 0; r < fbRow.Length; r++)
                    {
                        if ((uint)fbRow[r] < (uint)fallbackCheckMap.TargetCount) validFb++;
                    }
                    if (validFb <= 0)
                    {
                        error = $"Unity vertex {v} has no mapped FBX control points for TotalOne skin weight evaluation.";
                        return false;
                    }

                    error = $"Patch skin weight mode is TotalOne and Unity vertex {v} has post-patch bone weight sum 0.0000 (!= 1.0); Unity SkinnedMeshRenderer cannot preserve partial or unnormalized TotalOne weights.";
                    return false;
                }

                var constantOffsetBySlot = BuildSettledSlotVector(constantTermsBySlot);
                var slotStats = new Dictionary<int, TotalOneSlotSampleStats>();

                if (varyingDomains.Count == 0)
                {
                    if (!TryValidateAndRecordTotalOneSample(
                            constantOffsetBySlot,
                            sampleWeight: 1d,
                            isUniformSample: true,
                            boneTable,
                            v,
                            slotStats,
                            samplePositiveTermsScratch,
                            out error) ||
                        !TryFinalizeProjectedTotalOneVertex(
                            sampleCount: 1,
                            isUniformSample: true,
                            v,
                            slotStats,
                            projectedWeightsByVertex[v],
                            out error))
                    {
                        return false;
                    }
                }
                else if (varyingDomains.Count == 1)
                {
                    var vecs = varyingDomains[0];
                    for (int r = 0; r < vecs.Count; r++)
                    {
                        combinedSampleScratch.Clear();
                        foreach (var ckv in constantOffsetBySlot)
                        {
                            combinedSampleScratch[ckv.Key] = ckv.Value;
                        }

                        foreach (var rkv in vecs[r])
                        {
                            if (combinedSampleScratch.TryGetValue(rkv.Key, out double baseVal))
                            {
                                double sVal = baseVal + rkv.Value;
                                combinedSampleScratch[rkv.Key] = sVal == 0d ? 0d : sVal;
                            }
                            else
                            {
                                combinedSampleScratch[rkv.Key] = rkv.Value;
                            }
                        }

                        if (!TryValidateAndRecordTotalOneSample(
                                combinedSampleScratch,
                                sampleWeight: 1d,
                                isUniformSample: true,
                                boneTable,
                                v,
                                slotStats,
                                samplePositiveTermsScratch,
                                out error))
                        {
                            return false;
                        }
                    }

                    if (!TryFinalizeProjectedTotalOneVertex(
                            vecs.Count,
                            isUniformSample: true,
                            v,
                            slotStats,
                            projectedWeightsByVertex[v],
                            out error))
                    {
                        return false;
                    }
                }
                else
                {
                    // Multiple unaligned domains vary across welded control points at the same Unity vertex.
                    // Deduplicate identical control-point vectors per domain; evaluate small seams directly (<= 256 tuples)
                    // and fail closed when correspondence across large unaligned multi-patch seams is undetermined.
                    int kDom = varyingDomains.Count;
                    var dedupDomains = new (Dictionary<int, double> Vec, double Probability)[kDom][];
                    int totalCombinations = 1;
                    for (int d = 0; d < kDom; d++)
                    {
                        if (!TryDeduplicateVaryingDomain(
                                varyingDomains[d],
                                k_MaxUnalignedCrossDomainCombinations / totalCombinations,
                                out var states))
                        {
                            error = $"Patch skin weight mode is TotalOne and multiple unaligned mappings vary across welded control points at Unity vertex {v}; individual control-point correspondence cannot be established.";
                            return false;
                        }

                        dedupDomains[d] = states;
                        totalCombinations *= states.Length;
                    }

                    var probFactors = new double[kDom];
                    for (int combo = 0; combo < totalCombinations; combo++)
                    {
                        comboSlotTermsScratch.Clear();
                        foreach (var ckv in constantOffsetBySlot)
                        {
                            AddSlotTerm(comboSlotTermsScratch, ckv.Key, ckv.Value);
                        }

                        int rem = combo;
                        for (int d = 0; d < kDom; d++)
                        {
                            var domStates = dedupDomains[d];
                            int stIdx = rem % domStates.Length;
                            rem /= domStates.Length;
                            probFactors[d] = domStates[stIdx].Probability;
                            foreach (var rkv in domStates[stIdx].Vec)
                            {
                                AddSlotTerm(comboSlotTermsScratch, rkv.Key, rkv.Value);
                            }
                        }

                        Array.Sort(probFactors);
                        double comboProb = 1d;
                        for (int d = 0; d < kDom; d++)
                        {
                            comboProb *= probFactors[d];
                        }

                        combinedSampleScratch.Clear();
                        foreach (var skv in comboSlotTermsScratch)
                        {
                            double settledVal = SumSortedTerms(skv.Value);
                            if (settledVal != 0d)
                            {
                                combinedSampleScratch[skv.Key] = settledVal;
                            }
                        }

                        if (!TryValidateAndRecordTotalOneSample(
                                combinedSampleScratch,
                                comboProb,
                                isUniformSample: false,
                                boneTable,
                                v,
                                slotStats,
                                samplePositiveTermsScratch,
                                out error))
                        {
                            return false;
                        }
                    }

                    if (!TryFinalizeProjectedTotalOneVertex(
                            totalCombinations,
                            isUniformSample: false,
                            v,
                            slotStats,
                            projectedWeightsByVertex[v],
                            out error))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private sealed class TotalOneSlotSampleStats
        {
            public int PositiveSampleCount;
            public double MinPositive;
            public double MaxPositive;
            public readonly List<double> WeightedTerms = new List<double>();
        }

        private readonly struct DomainVectorKey : IEquatable<DomainVectorKey>
        {
            private readonly (int Slot, long Bits)[] m_Entries;
            private readonly int m_Hash;

            public DomainVectorKey(Dictionary<int, double> vec)
            {
                if (vec == null || vec.Count == 0)
                {
                    m_Entries = Array.Empty<(int, long)>();
                    m_Hash = 0;
                    return;
                }

                var entries = new List<(int Slot, long Bits)>(vec.Count);
                foreach (var kv in vec)
                {
                    double val = kv.Value == 0d ? 0d : kv.Value;
                    if (val != 0d)
                    {
                        entries.Add((kv.Key, BitConverter.DoubleToInt64Bits(val)));
                    }
                }

                entries.Sort((a, b) => a.Slot.CompareTo(b.Slot));
                m_Entries = entries.ToArray();
                unchecked
                {
                    int h = 17;
                    for (int i = 0; i < m_Entries.Length; i++)
                    {
                        h = (h * 31) ^ m_Entries[i].Slot;
                        h = (h * 31) ^ m_Entries[i].Bits.GetHashCode();
                    }

                    m_Hash = h;
                }
            }

            public bool Equals(DomainVectorKey other)
            {
                if (m_Entries.Length != other.m_Entries.Length)
                {
                    return false;
                }

                for (int i = 0; i < m_Entries.Length; i++)
                {
                    if (m_Entries[i].Slot != other.m_Entries[i].Slot ||
                        m_Entries[i].Bits != other.m_Entries[i].Bits)
                    {
                        return false;
                    }
                }

                return true;
            }

            public override bool Equals(object obj) => obj is DomainVectorKey other && Equals(other);

            public override int GetHashCode() => m_Hash;
        }

        private static void AddSlotTerm(Dictionary<int, List<double>> termsBySlot, int slot, double value)
        {
            if (!termsBySlot.TryGetValue(slot, out var list))
            {
                list = new List<double>(2);
                termsBySlot.Add(slot, list);
            }

            list.Add(value);
        }

        private static Dictionary<int, double> BuildSettledSlotVector(Dictionary<int, List<double>> termsBySlot)
        {
            var settled = new Dictionary<int, double>(termsBySlot.Count);
            foreach (var kv in termsBySlot)
            {
                double val = SumSortedTerms(kv.Value);
                if (val != 0d)
                {
                    settled[kv.Key] = val;
                }
            }

            return settled;
        }

        private static bool AreAllVectorsIdentical(List<Dictionary<int, double>> vecs)
        {
            if (vecs == null || vecs.Count <= 1)
            {
                return true;
            }

            var first = vecs[0];
            for (int i = 1; i < vecs.Count; i++)
            {
                var cur = vecs[i];
                if (cur.Count != first.Count)
                {
                    return false;
                }

                foreach (var kv in first)
                {
                    if (!cur.TryGetValue(kv.Key, out double otherVal) ||
                        BitConverter.DoubleToInt64Bits(kv.Value == 0d ? 0d : kv.Value) !=
                        BitConverter.DoubleToInt64Bits(otherVal == 0d ? 0d : otherVal))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool TryDeduplicateVaryingDomain(
            List<Dictionary<int, double>> vecs,
            int maxDistinctStates,
            out (Dictionary<int, double> Vec, double Probability)[] states)
        {
            states = null;
            if (maxDistinctStates <= 0)
            {
                return false;
            }

            var indexByKey = new Dictionary<DomainVectorKey, int>();
            var items = new List<(Dictionary<int, double> Vec, int Count)>();
            for (int r = 0; r < vecs.Count; r++)
            {
                var vec = vecs[r];
                var key = new DomainVectorKey(vec);
                if (indexByKey.TryGetValue(key, out int idx))
                {
                    var prev = items[idx];
                    items[idx] = (prev.Vec, prev.Count + 1);
                }
                else
                {
                    if (items.Count >= maxDistinctStates)
                    {
                        return false;
                    }

                    indexByKey.Add(key, items.Count);
                    items.Add((vec, 1));
                }
            }

            double invTotal = 1d / vecs.Count;
            states = new (Dictionary<int, double> Vec, double Probability)[items.Count];
            for (int i = 0; i < items.Count; i++)
            {
                states[i] = (items[i].Vec, items[i].Count * invTotal);
            }

            return true;
        }

        private static bool TryValidateAndRecordTotalOneSample(
            Dictionary<int, double> signedSample,
            double sampleWeight,
            bool isUniformSample,
            BoneTable boneTable,
            int vertexIndex,
            Dictionary<int, TotalOneSlotSampleStats> slotStats,
            List<double> positiveTermsScratch,
            out string error)
        {
            error = null;
            positiveTermsScratch.Clear();

            foreach (var kv in signedSample)
            {
                int slot = kv.Key;
                double val = kv.Value;
                if (double.IsNaN(val) || double.IsInfinity(val))
                {
                    error = $"Patch skin weight mode is TotalOne and Unity vertex {vertexIndex} has non-finite post-patch bone weight.";
                    return false;
                }

                if (slot < 0 || slot >= boneTable.Transforms.Count || boneTable.Transforms[slot] == null)
                {
                    if (val > 1e-6d)
                    {
                        error = $"TotalOne skin weight evaluation at Unity vertex {vertexIndex} references an unresolved or null Unity bone transform.";
                        return false;
                    }

                    continue;
                }

                if (val <= 0d)
                {
                    continue;
                }

                positiveTermsScratch.Add(val);
                if (!slotStats.TryGetValue(slot, out var st))
                {
                    st = new TotalOneSlotSampleStats
                    {
                        PositiveSampleCount = 1,
                        MinPositive = val,
                        MaxPositive = val
                    };
                    slotStats.Add(slot, st);
                }
                else
                {
                    st.PositiveSampleCount++;
                    if (val < st.MinPositive) st.MinPositive = val;
                    if (val > st.MaxPositive) st.MaxPositive = val;
                }

                st.WeightedTerms.Add(isUniformSample ? val : (sampleWeight * val));
            }

            double cpSum = SumSortedTerms(positiveTermsScratch);
            if (Math.Abs(cpSum - 1d) > 1e-4d)
            {
                error = $"Patch skin weight mode is TotalOne and a control point at Unity vertex {vertexIndex} has post-patch bone weight sum {cpSum:F4} (!= 1.0); Unity SkinnedMeshRenderer cannot preserve partial or unnormalized TotalOne weights.";
                return false;
            }

            return true;
        }

        private static bool TryFinalizeProjectedTotalOneVertex(
            int sampleCount,
            bool isUniformSample,
            int vertexIndex,
            Dictionary<int, TotalOneSlotSampleStats> slotStats,
            Dictionary<int, double> projectedVertexWeights,
            out string error)
        {
            error = null;
            foreach (var kv in slotStats)
            {
                var st = kv.Value;
                double minClamped = st.PositiveSampleCount < sampleCount ? 0d : st.MinPositive;
                double maxClamped = st.MaxPositive;
                if (maxClamped - minClamped > 1e-4d)
                {
                    error = $"Patch skin weight mode is TotalOne and welded control points at Unity vertex {vertexIndex} disagree on post-patch bone weights.";
                    return false;
                }

                double sum = SumSortedTerms(st.WeightedTerms);
                double mean = isUniformSample && sampleCount > 1 ? (sum / sampleCount) : sum;
                if (mean > 0d)
                {
                    projectedVertexWeights[kv.Key] = mean;
                }
            }

            return true;
        }

        private static bool TryReadRawTotalOneSkinWeights(
            IFbxSkinDeformer skin,
            int domainCount,
            HashSet<int> mappedCpSet,
            out Dictionary<string, Dictionary<int, double>> weightsByBonePath,
            out Dictionary<int, double> unlinkedWeightsByCp,
            out FbxClusterLinkMode? skinLinkMode,
            out string errorMessage)
        {
            weightsByBonePath = new Dictionary<string, Dictionary<int, double>>(StringComparer.Ordinal);
            unlinkedWeightsByCp = new Dictionary<int, double>();
            skinLinkMode = null;
            errorMessage = null;
            if (skin?.Clusters == null || skin.Clusters.Count == 0 || domainCount <= 0)
            {
                return true;
            }

            var termsByBonePath = new Dictionary<string, Dictionary<int, List<double>>>(StringComparer.Ordinal);
            var unlinkedTermsByCp = new Dictionary<int, List<double>>();
            int[] idxScratch = Array.Empty<int>();
            double[] wScratch = Array.Empty<double>();

            for (int c = 0; c < skin.Clusters.Count; c++)
            {
                var cluster = skin.Clusters[c];
                if (cluster == null) continue;

                var mode = cluster.LinkMode;
                if (mode != FbxClusterLinkMode.Normalize && mode != FbxClusterLinkMode.TotalOne)
                {
                    errorMessage = $"Target skin cluster '{cluster.Name}' uses unsupported FBX link mode '{mode}' (only Normalize and TotalOne are supported).";
                    return false;
                }

                if (!skinLinkMode.HasValue)
                {
                    skinLinkMode = mode;
                }
                else if (skinLinkMode.Value != mode)
                {
                    errorMessage = $"Target skin mixes cluster link modes ('{skinLinkMode.Value}' and '{mode}'), which cannot be safely evaluated.";
                    return false;
                }

                int weightCount = cluster.WeightCount;
                if (weightCount < 0)
                {
                    errorMessage = $"Skin cluster {c} reported negative weight count ({weightCount}).";
                    return false;
                }

                if (weightCount == 0)
                {
                    continue;
                }

                if (idxScratch.Length < weightCount)
                {
                    idxScratch = new int[weightCount];
                    wScratch = new double[weightCount];
                }

                string bonePath = FbxNameUtility.NormalizePath(
                    cluster.BoneNode != null ? cluster.BoneNode.Path : cluster.BonePath);
                if (!cluster.TryCopyControlPointIndices(idxScratch.AsSpan(0, weightCount), out int idxWritten) ||
                    !cluster.TryCopyWeights(wScratch.AsSpan(0, weightCount), out int wWritten) ||
                    idxWritten != weightCount || wWritten != weightCount)
                {
                    errorMessage = $"Failed to copy skin weights for cluster {c} ('{bonePath}').";
                    return false;
                }

                bool isUnlinked = string.IsNullOrEmpty(bonePath);
                Dictionary<int, List<double>> map = null;
                if (!isUnlinked && !termsByBonePath.TryGetValue(bonePath, out map))
                {
                    map = new Dictionary<int, List<double>>();
                    termsByBonePath[bonePath] = map;
                }

                for (int i = 0; i < weightCount; i++)
                {
                    int cp = idxScratch[i];
                    if ((uint)cp >= (uint)domainCount)
                    {
                        continue;
                    }

                    double w = wScratch[i];
                    if (double.IsNaN(w) || double.IsInfinity(w) || w < 0d)
                    {
                        errorMessage = $"Skin cluster '{bonePath}' control point {cp} has invalid weight {w}.";
                        return false;
                    }

                    if (w <= 0d || (mappedCpSet != null && !mappedCpSet.Contains(cp)))
                    {
                        continue;
                    }

                    if (isUnlinked)
                    {
                        AddSlotTerm(unlinkedTermsByCp, cp, w);
                    }
                    else
                    {
                        AddSlotTerm(map, cp, w);
                    }
                }
            }

            foreach (var bkv in termsByBonePath)
            {
                weightsByBonePath[bkv.Key] = BuildSettledSlotVector(bkv.Value);
            }

            unlinkedWeightsByCp = BuildSettledSlotVector(unlinkedTermsByCp);
            return true;
        }

        private static bool TryResolveUnifiedRawBoneSlot(
            UnityMeshApplyContext context,
            IReadOnlyList<FeatureApplyInput> inputs,
            Dictionary<string, int>[] slotByInputIndex,
            VerifiedRawTargetSkinContext verifiedRawSkin,
            BoneTable boneTable,
            string normFbxBonePath,
            out int slotIndex,
            out bool resolvedSlot,
            out string conflictError)
        {
            slotIndex = -1;
            resolvedSlot = false;
            conflictError = null;

            // 1. Explicit BoneTransformOverrides (proxy precedence) across inputs:
            if (context.BoneTransformOverrides != null)
            {
                int? proxySlot = null;
                for (int i = 0; i < inputs.Count; i++)
                {
                    if (context.BoneTransformOverrides.TryGetValue(
                            new BoneOverrideKey(inputs[i].PatchId, normFbxBonePath),
                            out var overrideTransform) &&
                        overrideTransform != null)
                    {
                        int candidate = -1;
                        if (slotByInputIndex != null &&
                            i < slotByInputIndex.Length &&
                            slotByInputIndex[i] != null &&
                            slotByInputIndex[i].TryGetValue(normFbxBonePath, out int mappedSlot))
                        {
                            candidate = mappedSlot;
                        }
                        else
                        {
                            string ovPath = context.TargetRootTransform
                                ? FbxNameUtility.NormalizePath(AnimationUtility.CalculateTransformPath(overrideTransform, context.TargetRootTransform))
                                : overrideTransform.name;
                            if (!boneTable.TryGetIndex(overrideTransform, ovPath, out candidate))
                            {
                                candidate = -1;
                            }
                        }

                        if (candidate >= 0)
                        {
                            if (proxySlot.HasValue && proxySlot.Value != candidate)
                            {
                                conflictError = $"Conflicting bone proxy overrides for TotalOne bone '{normFbxBonePath}' across patches.";
                                return false;
                            }

                            proxySlot = candidate;
                        }
                    }
                }

                if (proxySlot.HasValue)
                {
                    slotIndex = proxySlot.Value;
                    resolvedSlot = true;
                    return true;
                }
            }

            // 2. Imported renderer bone-slot correspondence (evaluates agreement strictly within this bone's candidate slots,
            //    shares one unresolved slot for absent original indices, and shares one ambiguous null slot on divergent candidates):
            if (verifiedRawSkin?.ImportedSlotsByFbxBonePath != null &&
                verifiedRawSkin.ImportedSlotsByFbxBonePath.TryGetValue(normFbxBonePath, out var importedOriginalSlots) &&
                importedOriginalSlots != null && importedOriginalSlots.Count > 0)
            {
                boneTable.ResolveImportedCandidateSlots(
                    importedOriginalSlots,
                    normFbxBonePath,
                    out slotIndex,
                    out _,
                    out _,
                    out _);
                resolvedSlot = true;
                return true;
            }

            // 2b. Explicit IncomingBoneSlotPaths correspondence (uses the exact same candidate-subset rule so mixed/null
            //     duplicate incoming paths drain cleanly to 0 while net-positive weights fail closed):
            if (boneTable.TryGetExplicitIncomingSlotsByPath(normFbxBonePath, out var explicitIncSlots) &&
                explicitIncSlots.Count > 0)
            {
                boneTable.ResolveExplicitCandidateSlots(
                    explicitIncSlots,
                    normFbxBonePath,
                    out slotIndex,
                    out _,
                    out _,
                    out _);
                resolvedSlot = true;
                return true;
            }

            // 3. Input-resolved or pre-existing BoneTable slot:
            if (slotByInputIndex != null)
            {
                for (int i = 0; i < slotByInputIndex.Length; i++)
                {
                    if (slotByInputIndex[i] != null &&
                        slotByInputIndex[i].TryGetValue(normFbxBonePath, out int inputSlot) &&
                        inputSlot >= 0 && inputSlot < boneTable.Transforms.Count)
                    {
                        slotIndex = inputSlot;
                        resolvedSlot = true;
                        return true;
                    }
                }
            }

            if (boneTable.TryGetExistingIndexByPath(normFbxBonePath, out int existingSlot))
            {
                slotIndex = existingSlot;
                resolvedSlot = true;
            }

            return true;
        }

        private static bool EqualsFilteredPrefix(CsrVertexMap patchMap, CsrVertexMap fullMap)
        {
            if (patchMap == null || fullMap == null ||
                patchMap.SourceCount != fullMap.SourceCount ||
                patchMap.TargetCount > fullMap.TargetCount)
            {
                return false;
            }

            if (ReferenceEquals(patchMap, fullMap))
            {
                return true;
            }

            int prefixCount = patchMap.TargetCount;
            for (int v = 0; v < patchMap.SourceCount; v++)
            {
                var pRow = patchMap.GetRow(v);
                var fRow = fullMap.GetRow(v);

                int expectedInPrefixCount = 0;
                bool fSorted = true;
                for (int j = 0; j < fRow.Length; j++)
                {
                    if (j > 0 && fRow[j] <= fRow[j - 1])
                    {
                        fSorted = false;
                    }

                    if ((uint)fRow[j] < (uint)prefixCount)
                    {
                        expectedInPrefixCount++;
                    }
                }

                if (pRow.Length != expectedInPrefixCount)
                {
                    return false;
                }

                if (pRow.Length == 0)
                {
                    continue;
                }

                bool pSorted = true;
                for (int i = 1; i < pRow.Length; i++)
                {
                    if (pRow[i] <= pRow[i - 1])
                    {
                        pSorted = false;
                        break;
                    }
                }

                if (pSorted && fSorted)
                {
                    int fi = 0;
                    for (int pi = 0; pi < pRow.Length; pi++)
                    {
                        int cp = pRow[pi];
                        while (fi < fRow.Length && fRow[fi] < cp)
                        {
                            fi++;
                        }

                        if (fi >= fRow.Length || fRow[fi] != cp)
                        {
                            return false;
                        }

                        fi++;
                    }
                }
                else
                {
                    var fSet = new HashSet<int>();
                    for (int j = 0; j < fRow.Length; j++)
                    {
                        if ((uint)fRow[j] < (uint)prefixCount)
                        {
                            fSet.Add(fRow[j]);
                        }
                    }

                    for (int i = 0; i < pRow.Length; i++)
                    {
                        if (!fSet.Contains(pRow[i]))
                        {
                            return false;
                        }
                    }
                }
            }

            return true;
        }

        private static BoneTable SeedBoneTable(
            Transform[] existingBones,
            Matrix4x4[] existingBindPoses,
            Transform targetRoot,
            string[] explicitSlotPaths = null)
        {
            var table = new BoneTable();
            var bones = existingBones ?? Array.Empty<Transform>();
            var poses = existingBindPoses ?? Array.Empty<Matrix4x4>();
            int count = Math.Max(bones.Length, poses.Length);

            for (int i = 0; i < count; i++)
            {
                var bone = i < bones.Length ? bones[i] : null;
                var pose = i < poses.Length ? poses[i] : Matrix4x4.identity;
                string explicitPath = explicitSlotPaths != null && i < explicitSlotPaths.Length
                    ? FbxNameUtility.NormalizePath(explicitSlotPaths[i] ?? string.Empty)
                    : string.Empty;
                if (bone == null)
                {
                    if (!string.IsNullOrEmpty(explicitPath))
                    {
                        table.AddIncomingSlot(null, explicitPath, pose, i, hasExplicitIncomingPath: true);
                    }
                    else
                    {
                        table.AddSynthetic($".__missing_bone_{i}", pose, i);
                    }

                    continue;
                }

                string path = !string.IsNullOrEmpty(explicitPath)
                    ? explicitPath
                    : (targetRoot
                        ? FbxNameUtility.NormalizePath(AnimationUtility.CalculateTransformPath(bone, targetRoot))
                        : bone.name);
                table.AddIncomingSlot(bone, path, pose, i, !string.IsNullOrEmpty(explicitPath));
            }

            return table;
        }

        private static Dictionary<int, double>[] ReadInitialWeights(Mesh mesh, BoneTable boneTable, out bool hasAnyPositiveWeights)
        {
            hasAnyPositiveWeights = false;
            var result = new Dictionary<int, double>[mesh.vertexCount];
            for (int v = 0; v < result.Length; v++)
            {
                result[v] = new Dictionary<int, double>();
            }

            var counts = mesh.GetBonesPerVertex();
            var weights = mesh.GetAllBoneWeights();
            int offset = 0;
            for (int v = 0; v < result.Length; v++)
            {
                int count = v < counts.Length ? counts[v] : 0;
                var map = result[v];
                for (int i = 0; i < count; i++)
                {
                    var bw = weights[offset++];
                    if (bw.boneIndex < 0 || bw.weight <= 0f) continue;
                    hasAnyPositiveWeights = true;
                    if (!boneTable.TryRemapOriginalIndex(bw.boneIndex, out int slot))
                    {
                        slot = boneTable.AddSynthetic($".__missing_bone_{bw.boneIndex}", Matrix4x4.identity, bw.boneIndex);
                    }
                    map.TryGetValue(slot, out double existing);
                    map[slot] = existing + bw.weight;
                }
            }

            return result;
        }

        private static bool IsFiniteMatrix(Matrix4x4 m)
        {
            for (int i = 0; i < 16; i++)
            {
                if (float.IsNaN(m[i]) || float.IsInfinity(m[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool BindPosesApproximatelyEqual(Matrix4x4 a, Matrix4x4 b, float tolerance)
        {
            if (!IsFiniteMatrix(a) || !IsFiniteMatrix(b))
            {
                return false;
            }

            for (int i = 0; i < 16; i++)
            {
                if (!(Mathf.Abs(a[i] - b[i]) <= tolerance))
                {
                    return false;
                }
            }

            return true;
        }

        private static double ComputePositiveWeightSum(Dictionary<int, double> aggregate, int boneCount)
        {
            if (aggregate == null || aggregate.Count == 0)
            {
                return 0d;
            }

            double sum = 0d;
            foreach (var pair in aggregate)
            {
                if (pair.Key >= 0 && pair.Key < boneCount &&
                    pair.Value > 0d && !double.IsNaN(pair.Value) && !double.IsInfinity(pair.Value))
                {
                    sum += pair.Value;
                }
            }

            return sum;
        }

        private static bool TryGetBindPoseOverride(FeatureApplyInput input, string sourcePath, out Matrix4x4 bindPose)
        {
            bindPose = Matrix4x4.identity;
            return input.BindPoseOverrides != null &&
                   input.BindPoseOverrides.TryGetValue(sourcePath, out bindPose);
        }

        private static bool TryResolveExplicitOrProxyBindPose(
            UnityMeshApplyContext context,
            SkinWeightFeature feature,
            SkeletonDocument skeleton,
            UnitySkeletonScope skeletonScope,
            string sourcePath,
            Transform resolvedBone,
            bool hasBindPoseOverride,
            Matrix4x4 authoredBindPose,
            FbxMatrix4x4 storedToUnityMeshLocal,
            FbxMatrix4x4 storedToUnitySceneLocal,
            out Matrix4x4 bindPose,
            out string error)
        {
            error = null;
            if (hasBindPoseOverride)
            {
                bindPose = authoredBindPose;
                return true;
            }

            if (feature.TryGetBindPoseFbxClusterMatrices(sourcePath, out var transformMatrix, out var transformLinkMatrix))
            {
                FbxMatrix4x4 endpointD = skeletonScope != null
                    ? skeletonScope.ResolveEndpointBasis(skeleton, sourcePath, storedToUnitySceneLocal)
                    : FbxLinearAlgebra3d.ExtractLinear(storedToUnitySceneLocal);
                if (!SkeletonTransformMath.ConvertCalibratedBindPoseToUnity(
                        transformMatrix,
                        transformLinkMatrix,
                        storedToUnityMeshLocal,
                        endpointD,
                        out bindPose))
                {
                    bindPose = Matrix4x4.identity;
                    error = $"Invalid bind matrices for bone '{sourcePath}'.";
                    return false;
                }

                return true;
            }

            if (resolvedBone == null)
            {
                bindPose = Matrix4x4.identity;
                error = $"Proxy bone override for '{sourcePath}' resolved to null.";
                return false;
            }

            var rendererLocalToWorld = context.MeshRendererTransform
                ? context.MeshRendererTransform.localToWorldMatrix
                : Matrix4x4.identity;
            bindPose = resolvedBone.localToWorldMatrix.inverse * rendererLocalToWorld;
            return true;
        }

        private static bool TryResolveFallbackBindPose(
            UnityMeshApplyContext context,
            FeatureApplyInput input,
            Dictionary<string, int> inputSlotBySourcePath,
            IReadOnlyList<PendingBoneSlotSpec> slotSpecs,
            SkeletonDocument skeleton,
            UnitySkeletonScope skeletonScope,
            IReadOnlyDictionary<string, (string PatchId, GeneratedSkeletonBoneRecord Record)> mergedSkeletonBonesByPath,
            BoneTable boneTable,
            HashSet<int> finalizedPostEditSlots,
            string sourcePath,
            Transform resolvedBone,
            FbxMatrix4x4 storedToUnitySceneLocal,
            out Matrix4x4 bindPose,
            out string error)
        {
            error = null;
            if (TryDeriveBindPoseFromAncestorSlot(
                    context,
                    input,
                    inputSlotBySourcePath,
                    slotSpecs,
                    skeleton,
                    skeletonScope,
                    mergedSkeletonBonesByPath,
                    boneTable,
                    finalizedPostEditSlots,
                    sourcePath,
                    storedToUnitySceneLocal,
                    out bindPose))
            {
                return true;
            }

            // When no proxy override affects the chain, derive fallback bindpose from clean post-edit rest space
            // (R'_b relative to TargetRootTransform) so posed A-pose avatars never bake live pose rotations into bindposes.
            if (!HasProxyOverrideInChain(context, input.PatchId, sourcePath) &&
                context.TargetRootTransform != null &&
                skeletonScope != null &&
                skeletonScope.TryGetPostEditRestGlobal(sourcePath, out var postRestGlobal) &&
                FbxAffineSpaceTransform.TryCreate(postRestGlobal, out _))
            {
                Matrix4x4 boneRestWorld = context.TargetRootTransform.localToWorldMatrix * postRestGlobal.ToUnityColumnMatrix();
                Matrix4x4 rendererLocalToWorld = context.MeshRendererTransform
                    ? context.MeshRendererTransform.localToWorldMatrix
                    : Matrix4x4.identity;
                if (Mathf.Abs(boneRestWorld.determinant) > 1e-12f)
                {
                    bindPose = boneRestWorld.inverse * rendererLocalToWorld;
                    return true;
                }
            }

            if (TryResolveBoneLocalToWorld(
                    context,
                    input,
                    skeleton,
                    skeletonScope,
                    mergedSkeletonBonesByPath,
                    sourcePath,
                    resolvedBone,
                    storedToUnitySceneLocal,
                    new HashSet<string>(StringComparer.Ordinal),
                    out var boneLocalToWorld,
                    out error))
            {
                var rendererLocalToWorld = context.MeshRendererTransform
                    ? context.MeshRendererTransform.localToWorldMatrix
                    : Matrix4x4.identity;
                bindPose = boneLocalToWorld.inverse * rendererLocalToWorld;
                return true;
            }

            bindPose = Matrix4x4.identity;
            return false;
        }

        private static bool TryDeriveBindPoseFromAncestorSlot(
            UnityMeshApplyContext context,
            FeatureApplyInput input,
            Dictionary<string, int> inputSlotBySourcePath,
            IReadOnlyList<PendingBoneSlotSpec> slotSpecs,
            SkeletonDocument skeleton,
            UnitySkeletonScope skeletonScope,
            IReadOnlyDictionary<string, (string PatchId, GeneratedSkeletonBoneRecord Record)> mergedSkeletonBonesByPath,
            BoneTable boneTable,
            HashSet<int> finalizedPostEditSlots,
            string sourcePath,
            FbxMatrix4x4 storedToUnitySceneLocal,
            out Matrix4x4 bindPose)
        {
            bindPose = Matrix4x4.identity;
            if (boneTable == null)
            {
                return false;
            }

            var chainLocals = new Stack<Matrix4x4>();
            string curr = FbxNameUtility.NormalizePath(sourcePath ?? string.Empty);
            var visited = new HashSet<string>(StringComparer.Ordinal);

            while (!string.IsNullOrEmpty(curr) && visited.Add(curr))
            {
                Transform currProxyTransform = null;
                bool hasCurrProxyOverride = context.BoneTransformOverrides != null &&
                                            context.BoneTransformOverrides.TryGetValue(new BoneOverrideKey(input.PatchId, curr), out currProxyTransform) &&
                                            currProxyTransform != null;

                bool hasMergedAuthored = mergedSkeletonBonesByPath != null &&
                                         mergedSkeletonBonesByPath.ContainsKey(curr);
                var authoredBone = skeleton?.GetNode(curr);

                // Stop at the nearest strict ancestor slot BEFORE falling back to a live transform, resolving through
                // THIS patch's own slot or proxy override transform first so another patch's proxy can never hijack curr!
                // Never match chainLocals.Count == 0 (sourcePath itself), which holds a temporary placeholder slot during Pass 2B.
                if (chainLocals.Count > 0)
                {
                    Matrix4x4? anchoredAncestorPose = null;
                    if (inputSlotBySourcePath != null &&
                        inputSlotBySourcePath.TryGetValue(curr, out int patchAncSlot) &&
                        patchAncSlot >= 0 && patchAncSlot < boneTable.BindPoses.Count &&
                        ((finalizedPostEditSlots != null && finalizedPostEditSlots.Contains(patchAncSlot)) ||
                         (!hasMergedAuthored && authoredBone == null && !hasCurrProxyOverride)) &&
                        Mathf.Abs(boneTable.BindPoses[patchAncSlot].determinant) > 1e-12f)
                    {
                        anchoredAncestorPose = boneTable.BindPoses[patchAncSlot];
                    }
                    else if (TryGetBindPoseOverride(input, curr, out var ancOverrideBindPose) &&
                             Mathf.Abs(ancOverrideBindPose.determinant) > 1e-12f)
                    {
                        anchoredAncestorPose = ancOverrideBindPose;
                    }
                    else if (hasCurrProxyOverride)
                    {
                        if (TryResolveFinalizedProxyAncestorBindPose(
                                slotSpecs,
                                boneTable,
                                finalizedPostEditSlots,
                                curr,
                                currProxyTransform,
                                1e-3f,
                                out var finalizedProxyPose) &&
                            Mathf.Abs(finalizedProxyPose.determinant) > 1e-12f)
                        {
                            anchoredAncestorPose = finalizedProxyPose;
                        }
                        else
                        {
                            var rendererLocalToWorld = context.MeshRendererTransform
                                ? context.MeshRendererTransform.localToWorldMatrix
                                : Matrix4x4.identity;
                            var candidateProxyPose = currProxyTransform.localToWorldMatrix.inverse * rendererLocalToWorld;
                            if (Mathf.Abs(candidateProxyPose.determinant) > 1e-12f)
                            {
                                anchoredAncestorPose = candidateProxyPose;
                            }
                        }
                    }
                    else if (!HasProxyOrBindPoseOverrideInChain(context, input, curr) &&
                             boneTable.TryGetSlotAndBindPoseByPath(curr, out int ancSlot, out var ancBindPose) &&
                             ((finalizedPostEditSlots != null && finalizedPostEditSlots.Contains(ancSlot)) ||
                              (!hasMergedAuthored && authoredBone == null)) &&
                             Mathf.Abs(ancBindPose.determinant) > 1e-12f)
                    {
                        anchoredAncestorPose = ancBindPose;
                    }

                    if (anchoredAncestorPose.HasValue)
                    {
                        Matrix4x4 currentPose = anchoredAncestorPose.Value;
                        while (chainLocals.Count > 0)
                        {
                            Matrix4x4 childLocal = chainLocals.Pop();
                            if (Mathf.Abs(childLocal.determinant) <= 1e-12f)
                            {
                                return false;
                            }

                            currentPose = childLocal.inverse * currentPose;
                        }

                        bindPose = currentPose;
                        return true;
                    }
                }

                if (hasCurrProxyOverride)
                {
                    return false;
                }

                Matrix4x4 stepLocal;
                string parentPath = GetParentPath(curr);
                if (skeletonScope != null && skeletonScope.TryGetPostEditRestLocal(curr, out var postRestLocal))
                {
                    // Always step through clean post-edit rest local transforms (never posed WriteLocal/LiveLocal).
                    stepLocal = postRestLocal.ToUnityColumnMatrix();
                }
                else if (authoredBone != null)
                {
                    var onDemand = SkeletonAlignment.Solve(
                        skeletonScope?.PreEditRestFrame,
                        new[] { new SkeletonAlignmentPatchInput(input.PatchId, skeleton, storedToUnitySceneLocal) });
                    if (onDemand.HasErrors || !onDemand.SolvedNodesByPath.TryGetValue(curr, out var solvedNode))
                    {
                        return false;
                    }

                    stepLocal = solvedNode.Local.ToUnityColumnMatrix();
                }
                else if (context.TargetRootTransform != null)
                {
                    var existingT = context.TargetRootTransform.Find(curr);
                    if (existingT == null)
                    {
                        return false;
                    }

                    stepLocal = Matrix4x4.TRS(existingT.localPosition, existingT.localRotation, existingT.localScale);
                }
                else
                {
                    return false;
                }

                chainLocals.Push(stepLocal);
                curr = parentPath;
            }

            return false;
        }

        private static bool TryResolveFinalizedProxyAncestorBindPose(
            IReadOnlyList<PendingBoneSlotSpec> slotSpecs,
            BoneTable boneTable,
            HashSet<int> finalizedPostEditSlots,
            string ancestorSourcePath,
            Transform proxyTransform,
            float tolerance,
            out Matrix4x4 bindPose)
        {
            bindPose = Matrix4x4.identity;
            if (boneTable == null || proxyTransform == null || finalizedPostEditSlots == null || finalizedPostEditSlots.Count == 0)
            {
                return false;
            }

            var candidates = new List<Matrix4x4>();
            if (slotSpecs != null)
            {
                for (int i = 0; i < slotSpecs.Count; i++)
                {
                    var sp = slotSpecs[i];
                    if (sp.AssignedSlotIndex >= 0 &&
                        sp.AssignedSlotIndex < boneTable.BindPoses.Count &&
                        finalizedPostEditSlots.Contains(sp.AssignedSlotIndex) &&
                        sp.ResolvedBone == proxyTransform &&
                        string.Equals(sp.SourcePath, ancestorSourcePath, StringComparison.Ordinal))
                    {
                        candidates.Add(CanonicalizeMatrixSignedZeros(boneTable.BindPoses[sp.AssignedSlotIndex]));
                    }
                }
            }

            if (candidates.Count == 0)
            {
                foreach (int slot in finalizedPostEditSlots)
                {
                    if ((uint)slot < (uint)boneTable.Transforms.Count &&
                        boneTable.Transforms[slot] == proxyTransform)
                    {
                        candidates.Add(CanonicalizeMatrixSignedZeros(boneTable.BindPoses[slot]));
                    }
                }
            }

            if (candidates.Count == 0)
            {
                return false;
            }

            var min = new float[16];
            var max = new float[16];
            if (!IsFiniteMatrix(candidates[0]))
            {
                return false;
            }

            for (int c = 0; c < 16; c++)
            {
                min[c] = max[c] = candidates[0][c];
            }

            for (int i = 1; i < candidates.Count; i++)
            {
                Matrix4x4 m = candidates[i];
                if (!IsFiniteMatrix(m))
                {
                    return false;
                }

                for (int c = 0; c < 16; c++)
                {
                    if (m[c] < min[c]) min[c] = m[c];
                    if (m[c] > max[c]) max[c] = m[c];
                }
            }

            for (int c = 0; c < 16; c++)
            {
                if (max[c] - min[c] > tolerance)
                {
                    return false;
                }
            }

            int best = 0;
            double bestDist = ComputeCandidateMidpointDistance(candidates[0], min, max);
            for (int i = 1; i < candidates.Count; i++)
            {
                double dist = ComputeCandidateMidpointDistance(candidates[i], min, max);
                int cmp = dist.CompareTo(bestDist);
                if (cmp < 0 || (cmp == 0 && CompareMatrixLexicographic(candidates[i], candidates[best]) < 0))
                {
                    best = i;
                    bestDist = dist;
                }
            }

            bindPose = candidates[best];
            return true;
        }

        private static double ComputeCandidateMidpointDistance(Matrix4x4 m, float[] min, float[] max)
        {
            double maxD = 0d;
            for (int c = 0; c < 16; c++)
            {
                double mid = 0.5d * ((double)min[c] + (double)max[c]);
                double d = Math.Abs((double)m[c] - mid);
                if (d > maxD) maxD = d;
            }

            return maxD;
        }

        private static bool TryResolveBoneLocalToWorld(
            UnityMeshApplyContext context,
            FeatureApplyInput input,
            SkeletonDocument skeleton,
            UnitySkeletonScope skeletonScope,
            IReadOnlyDictionary<string, (string PatchId, GeneratedSkeletonBoneRecord Record)> mergedSkeletonBonesByPath,
            string path,
            Transform directTransform,
            FbxMatrix4x4 storedToUnitySceneLocal,
            HashSet<string> resolving,
            out Matrix4x4 localToWorld,
            out string error)
        {
            error = null;
            path = FbxNameUtility.NormalizePath(path ?? string.Empty);
            if (string.IsNullOrEmpty(path))
            {
                localToWorld = context.TargetRootTransform ? context.TargetRootTransform.localToWorldMatrix : Matrix4x4.identity;
                return true;
            }

            var overrideKey = new BoneOverrideKey(input.PatchId, path);
            if (context.BoneTransformOverrides != null &&
                context.BoneTransformOverrides.TryGetValue(overrideKey, out var overrideTransform) &&
                overrideTransform != null)
            {
                localToWorld = overrideTransform.localToWorldMatrix;
                return true;
            }

            (string PatchId, GeneratedSkeletonBoneRecord Record) mergedAuthored = default;
            bool hasMergedAuthored = mergedSkeletonBonesByPath != null &&
                                     mergedSkeletonBonesByPath.TryGetValue(path, out mergedAuthored);
            var skeletonBone = skeleton?.GetNode(path);
            if (!hasMergedAuthored && skeletonBone == null)
            {
                var existing = directTransform != null
                    ? directTransform
                    : (context.TargetRootTransform ? context.TargetRootTransform.Find(path) : null);
                if (existing != null)
                {
                    localToWorld = existing.localToWorldMatrix;
                    return true;
                }

                localToWorld = Matrix4x4.identity;
                error = $"Cannot resolve needed skin bone '{path}'.";
                return false;
            }

            if (!resolving.Add(path))
            {
                localToWorld = Matrix4x4.identity;
                error = $"Skeleton contains a parent cycle at '{path}'.";
                return false;
            }

            string parentPath = GetParentPath(path);
            if (!TryResolveBoneLocalToWorld(
                    context,
                    input,
                    skeleton,
                    skeletonScope,
                    mergedSkeletonBonesByPath,
                    parentPath,
                    directTransform: null,
                    storedToUnitySceneLocal,
                    resolving,
                    out var parentLocalToWorld,
                    out error))
            {
                resolving.Remove(path);
                localToWorld = Matrix4x4.identity;
                return false;
            }

            resolving.Remove(path);
            if (hasMergedAuthored)
            {
                localToWorld = parentLocalToWorld * Matrix4x4.TRS(
                    mergedAuthored.Record.LocalPosition,
                    mergedAuthored.Record.LocalRotation,
                    mergedAuthored.Record.LocalScale);
                return true;
            }

            if (!UnitySkeletonWriter.TrySolveUnityLocalTransform(
                    skeleton,
                    skeletonBone,
                    storedToUnitySceneLocal,
                    context.TargetRootTransform,
                    out var pos,
                    out var rot,
                    out var scale,
                    out error))
            {
                localToWorld = Matrix4x4.identity;
                error = $"Cannot convert skeleton bone '{path}': {error ?? "missing or invalid transform data"}";
                return false;
            }

            localToWorld = parentLocalToWorld * Matrix4x4.TRS(pos, rot, scale);
            return true;
        }

        private static Dictionary<int, List<(int BoneIndex, double Delta)>> BuildDeltaIndex(
            SkinWeightFeature feature,
            ILazyPayloadSource payloadSource,
            Dictionary<string, int> slotBySourcePath,
            int controlPointCount)
        {
            var result = new Dictionary<int, List<(int BoneIndex, double Delta)>>();
            if (feature?.Clusters == null)
            {
                return result;
            }

            for (int c = 0; c < feature.Clusters.Count; c++)
            {
                var cluster = feature.Clusters[c];
                if (cluster == null || cluster.Payload == null || !cluster.Payload.HasPayload)
                {
                    continue;
                }

                string path = FbxNameUtility.NormalizePath(cluster.BonePath);
                int boneIndex = slotBySourcePath.TryGetValue(path, out int slot) ? slot : -1;

                using var lease = cluster.Payload.Acquire(payloadSource);
                var buffer = lease.Value?.DeltaWeights;
                if (buffer == null || buffer.Count == 0)
                {
                    continue;
                }

                for (int i = 0; i < buffer.Count; i++)
                {
                    int cp = buffer.Indices[i];
                    double delta = buffer.Values[i];
                    if ((uint)cp >= (uint)controlPointCount)
                    {
                        continue;
                    }

                    if (double.IsNaN(delta) || double.IsInfinity(delta) || delta < -1d || delta > 1d)
                    {
                        throw new InvalidOperationException(
                            $"Skin weight delta out of [-1, 1] for bone '{path}' at control point {cp}: {delta}.");
                    }

                    if (delta == 0d)
                    {
                        continue;
                    }

                    if (!result.TryGetValue(cp, out var list))
                    {
                        list = new List<(int, double)>();
                        result[cp] = list;
                    }

                    list.Add((boneIndex, delta));
                }
            }

            return result;
        }

        private static Dictionary<int, double> CalculateIndexedDeltas(
            ReadOnlySpan<int> controlPoints,
            int domainControlPointCount,
            Dictionary<int, List<(int BoneIndex, double Delta)>> deltaIndex)
        {
            int validControlPoints = 0;
            Dictionary<int, double> deltaBySlot = null;

            for (int i = 0; i < controlPoints.Length; i++)
            {
                int cp = controlPoints[i];
                if ((uint)cp >= (uint)domainControlPointCount)
                {
                    continue;
                }

                validControlPoints++;
                if (!deltaIndex.TryGetValue(cp, out var deltas) || deltas == null || deltas.Count == 0)
                {
                    continue;
                }

                deltaBySlot ??= new Dictionary<int, double>();
                for (int d = 0; d < deltas.Count; d++)
                {
                    var item = deltas[d];
                    if (item.BoneIndex < 0) continue;
                    deltaBySlot.TryGetValue(item.BoneIndex, out double existing);
                    deltaBySlot[item.BoneIndex] = existing + item.Delta;
                }
            }

            if (deltaBySlot != null && validControlPoints > 1)
            {
                var keys = deltaBySlot.Keys.ToArray();
                for (int i = 0; i < keys.Length; i++)
                {
                    deltaBySlot[keys[i]] /= validControlPoints;
                }
            }

            return deltaBySlot;
        }

        private enum DonorGroupKind : byte
        {
            Direct = 0,
            NoDonors = 1,
            Slots = 2,
            Ambiguous = 3
        }

        private readonly struct DonorGroupKey : IEquatable<DonorGroupKey>
        {
            public static readonly DonorGroupKey Direct = new DonorGroupKey(DonorGroupKind.Direct, Array.Empty<int>(), "<direct>");
            public static readonly DonorGroupKey NoDonors = new DonorGroupKey(DonorGroupKind.NoDonors, Array.Empty<int>(), "<none>");
            public static readonly DonorGroupKey Ambiguous = new DonorGroupKey(DonorGroupKind.Ambiguous, Array.Empty<int>(), "<ambiguous>");

            public readonly DonorGroupKind Kind;
            public readonly int[] Slots;
            public readonly string Key;

            public bool IsDirect => Kind == DonorGroupKind.Direct;
            public bool IsAmbiguous => Kind == DonorGroupKind.Ambiguous;

            private DonorGroupKey(DonorGroupKind kind, int[] slots, string key)
            {
                Kind = kind;
                Slots = slots ?? Array.Empty<int>();
                Key = key ?? string.Empty;
            }

            public static DonorGroupKey FromSingle(int slot)
            {
                return new DonorGroupKey(
                    DonorGroupKind.Slots,
                    new[] { slot },
                    slot.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            public static DonorGroupKey FromSortedSlots(List<int> sortedDistinctSlots)
            {
                if (sortedDistinctSlots == null || sortedDistinctSlots.Count == 0)
                {
                    return NoDonors;
                }

                if (sortedDistinctSlots.Count == 1)
                {
                    return FromSingle(sortedDistinctSlots[0]);
                }

                int[] arr = sortedDistinctSlots.ToArray();
                string key = string.Join(",", arr.Select(s => s.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                return new DonorGroupKey(DonorGroupKind.Slots, arr, key);
            }

            public bool Equals(DonorGroupKey other) =>
                Kind == other.Kind && string.Equals(Key, other.Key, StringComparison.Ordinal);

            public override bool Equals(object obj) => obj is DonorGroupKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    return ((int)Kind * 397) ^ StringComparer.Ordinal.GetHashCode(Key ?? string.Empty);
                }
            }
        }

        private readonly struct NormalizeBucketKey : IEquatable<NormalizeBucketKey>
        {
            public readonly int Slot;
            public readonly string SourcePath;
            public readonly DonorGroupKey Donors;

            public NormalizeBucketKey(int slot, string sourcePath, DonorGroupKey donors)
            {
                Slot = slot;
                SourcePath = donors.IsDirect ? string.Empty : (sourcePath ?? string.Empty);
                Donors = donors;
            }

            public bool Equals(NormalizeBucketKey other) =>
                Slot == other.Slot &&
                Donors.Equals(other.Donors) &&
                string.Equals(SourcePath, other.SourcePath, StringComparison.Ordinal);

            public override bool Equals(object obj) => obj is NormalizeBucketKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = Slot;
                    h = (h * 397) ^ Donors.GetHashCode();
                    h = (h * 397) ^ StringComparer.Ordinal.GetHashCode(SourcePath ?? string.Empty);
                    return h;
                }
            }
        }

        private static Dictionary<int, List<(NormalizeBucketKey Key, double Delta)>> BuildNormalizeBucketDeltaIndex(
            SkinWeightFeature feature,
            ILazyPayloadSource payloadSource,
            Dictionary<string, int> slotBySourcePath,
            Dictionary<string, DonorGroupKey> donorBySourcePath,
            int controlPointCount)
        {
            var result = new Dictionary<int, List<(NormalizeBucketKey Key, double Delta)>>();
            if (feature?.Clusters == null)
            {
                return result;
            }

            for (int c = 0; c < feature.Clusters.Count; c++)
            {
                var cluster = feature.Clusters[c];
                if (cluster == null || cluster.Payload == null || !cluster.Payload.HasPayload)
                {
                    continue;
                }

                string path = FbxNameUtility.NormalizePath(cluster.BonePath);
                int boneIndex = slotBySourcePath.TryGetValue(path, out int slot) ? slot : -1;
                DonorGroupKey donors = donorBySourcePath != null && donorBySourcePath.TryGetValue(path, out var dg)
                    ? dg
                    : DonorGroupKey.Direct;
                var bucketKey = new NormalizeBucketKey(boneIndex, path, donors);

                using var lease = cluster.Payload.Acquire(payloadSource);
                var buffer = lease.Value?.DeltaWeights;
                if (buffer == null || buffer.Count == 0)
                {
                    continue;
                }

                for (int i = 0; i < buffer.Count; i++)
                {
                    int cp = buffer.Indices[i];
                    double delta = buffer.Values[i];
                    if ((uint)cp >= (uint)controlPointCount)
                    {
                        continue;
                    }

                    if (double.IsNaN(delta) || double.IsInfinity(delta) || delta < -1d || delta > 1d)
                    {
                        throw new InvalidOperationException(
                            $"Skin weight delta out of [-1, 1] for bone '{path}' at control point {cp}: {delta}.");
                    }

                    if (delta == 0d)
                    {
                        continue;
                    }

                    if (!result.TryGetValue(cp, out var list))
                    {
                        list = new List<(NormalizeBucketKey, double)>();
                        result[cp] = list;
                    }

                    list.Add((bucketKey, delta));
                }
            }

            return result;
        }

        private static Dictionary<NormalizeBucketKey, double> CalculateIndexedBucketDeltas(
            ReadOnlySpan<int> controlPoints,
            int domainControlPointCount,
            Dictionary<int, List<(NormalizeBucketKey Key, double Delta)>> bucketIndex)
        {
            int validControlPoints = 0;
            Dictionary<NormalizeBucketKey, List<double>> termsByBucket = null;

            for (int i = 0; i < controlPoints.Length; i++)
            {
                int cp = controlPoints[i];
                if ((uint)cp >= (uint)domainControlPointCount)
                {
                    continue;
                }

                validControlPoints++;
                if (!bucketIndex.TryGetValue(cp, out var deltas) || deltas == null || deltas.Count == 0)
                {
                    continue;
                }

                termsByBucket ??= new Dictionary<NormalizeBucketKey, List<double>>();
                for (int d = 0; d < deltas.Count; d++)
                {
                    var item = deltas[d];
                    if (item.Key.Slot < 0) continue;
                    if (!termsByBucket.TryGetValue(item.Key, out var list))
                    {
                        list = new List<double>(2);
                        termsByBucket.Add(item.Key, list);
                    }

                    list.Add(item.Delta);
                }
            }

            if (termsByBucket == null || validControlPoints <= 0)
            {
                return null;
            }

            var result = new Dictionary<NormalizeBucketKey, double>(termsByBucket.Count);
            foreach (var kv in termsByBucket)
            {
                double sum = SumSortedTerms(kv.Value);
                result[kv.Key] = validControlPoints > 1 ? (sum / validControlPoints) : sum;
            }

            return result;
        }

        private static double SumSortedTerms(List<double> terms)
        {
            if (terms == null || terms.Count == 0)
            {
                return 0d;
            }

            if (terms.Count == 1)
            {
                double single = terms[0];
                return single == 0d ? 0d : single;
            }

            terms.Sort();
            double sum = 0d;
            double compensation = 0d;
            for (int i = 0; i < terms.Count; i++)
            {
                double x = terms[i];
                double t = sum + x;
                if (Math.Abs(sum) >= Math.Abs(x))
                {
                    compensation += (sum - t) + x;
                }
                else
                {
                    compensation += (x - t) + sum;
                }

                sum = t;
            }

            double result = sum + compensation;
            return result == 0d ? 0d : result;
        }

        private static double DrainBucketDeficitFromDonors(
            Dictionary<int, double> aggregate,
            int[] donorSlots,
            double negativeBucketSum)
        {
            if (aggregate == null || donorSlots == null || donorSlots.Length == 0 || negativeBucketSum >= -1e-12d)
            {
                return negativeBucketSum;
            }

            var availableTerms = new List<double>(donorSlots.Length);
            for (int i = 0; i < donorSlots.Length; i++)
            {
                if (aggregate.TryGetValue(donorSlots[i], out double dw) && dw > 1e-12d)
                {
                    availableTerms.Add(dw);
                }
            }

            double totalDonor = SumSortedTerms(availableTerms);
            if (totalDonor <= 1e-12d)
            {
                return negativeBucketSum;
            }

            double drain = Math.Min(-negativeBucketSum, totalDonor);
            for (int i = 0; i < donorSlots.Length; i++)
            {
                int donorIdx = donorSlots[i];
                if (aggregate.TryGetValue(donorIdx, out double dw) && dw > 1e-12d)
                {
                    double remaining = dw - (drain * (dw / totalDonor));
                    aggregate[donorIdx] = remaining > 1e-15d ? remaining : 0d;
                }
            }

            double residual = negativeBucketSum + drain;
            return Math.Abs(residual) > 1e-15d ? residual : 0d;
        }

        private static bool IsDisallowedNullSlotForVertex(
            int slot,
            BoneTable boneTable,
            HashSet<int> vertexTouchedSlots,
            HashSet<int> bindposeModifiedNullSlots)
        {
            if (boneTable.IsScratchNullSlot(slot))
            {
                return true;
            }

            if ((uint)slot >= (uint)boneTable.Transforms.Count)
            {
                return true;
            }

            if (boneTable.Transforms[slot] != null)
            {
                return false;
            }

            if (!boneTable.IsIncomingSlot(slot))
            {
                return true;
            }

            if (bindposeModifiedNullSlots != null && bindposeModifiedNullSlots.Contains(slot))
            {
                return true;
            }

            return vertexTouchedSlots != null && vertexTouchedSlots.Contains(slot);
        }

        private static int KeepTopWeights(
            int maxInfluences,
            Dictionary<int, double> aggregate,
            BoneTable boneTable,
            HashSet<int> vertexTouchedSlots,
            HashSet<int> bindposeModifiedNullSlots,
            List<(int Index, double Weight)> output)
        {
            int boneCount = boneTable.BonePaths.Count;
            output.Clear();
            if (aggregate != null)
            {
                foreach (var pair in aggregate)
                {
                    if (pair.Key >= 0 && pair.Key < boneCount &&
                        !IsDisallowedNullSlotForVertex(pair.Key, boneTable, vertexTouchedSlots, bindposeModifiedNullSlots) &&
                        pair.Value > 0d && !double.IsNaN(pair.Value) && !double.IsInfinity(pair.Value))
                    {
                        output.Add((pair.Key, pair.Value));
                    }
                }
            }

            output.Sort((left, right) => CompareInfluencesDeterministic(left, right, boneTable));
            int positiveCount = output.Count;
            int limit = Mathf.Clamp(maxInfluences, 1, byte.MaxValue);
            if (output.Count > limit)
            {
                output.RemoveRange(limit, output.Count - limit);
            }

            if (output.Count == 0)
            {
                int fallbackSlot = boneTable.FirstResolvedTransformSlotIndex;
                if (fallbackSlot >= 0)
                {
                    output.Add((fallbackSlot, 1d));
                }

                return positiveCount;
            }

            var normTerms = new List<double>(output.Count);
            for (int i = 0; i < output.Count; i++)
            {
                normTerms.Add(output[i].Weight);
            }

            double total = SumSortedTerms(normTerms);
            if (!(total > 0d) || double.IsInfinity(total))
            {
                int strongest = output[0].Index;
                output.Clear();
                output.Add((strongest, 1d));
                return positiveCount;
            }

            for (int i = 0; i < output.Count; i++)
            {
                output[i] = (output[i].Index, output[i].Weight / total);
            }

            return positiveCount;
        }

        private static double QuantizeWeightForOrder(double weight)
        {
            double rounded = Math.Round(weight * 1e12d, MidpointRounding.AwayFromZero);
            return rounded == 0d ? 0d : rounded;
        }

        private static int CompareInfluencesDeterministic(
            (int Index, double Weight) left,
            (int Index, double Weight) right,
            BoneTable boneTable)
        {
            int weightCmp = QuantizeWeightForOrder(right.Weight).CompareTo(QuantizeWeightForOrder(left.Weight));
            if (weightCmp != 0) return weightCmp;

            string lp = (uint)left.Index < (uint)boneTable.BonePaths.Count ? boneTable.BonePaths[left.Index] : string.Empty;
            string rp = (uint)right.Index < (uint)boneTable.BonePaths.Count ? boneTable.BonePaths[right.Index] : string.Empty;
            int pathCmp = StringComparer.Ordinal.Compare(lp, rp);
            if (pathCmp != 0) return pathCmp;

            if ((uint)left.Index < (uint)boneTable.BindPoses.Count &&
                (uint)right.Index < (uint)boneTable.BindPoses.Count)
            {
                int bpCmp = CompareMatrixLexicographic(boneTable.BindPoses[left.Index], boneTable.BindPoses[right.Index]);
                if (bpCmp != 0) return bpCmp;
            }

            return left.Index.CompareTo(right.Index);
        }

        private static Matrix4x4 CanonicalizeMatrixSignedZeros(Matrix4x4 m)
        {
            for (int i = 0; i < 16; i++)
            {
                if (m[i] == 0f) m[i] = 0f;
            }

            return m;
        }

        private static int CompareMatrixLexicographic(Matrix4x4 a, Matrix4x4 b)
        {
            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++)
                {
                    float va = a[r, c] == 0f ? 0f : a[r, c];
                    float vb = b[r, c] == 0f ? 0f : b[r, c];
                    int cmp = va.CompareTo(vb);
                    if (cmp != 0) return cmp;
                }
            }

            return 0;
        }

        private sealed class ClusterBindPoseGroup
        {
            public readonly string GroupKey;
            public readonly int CandidateSlot;
            public readonly Transform ResolvedBone;
            public readonly string TargetPath;
            public readonly float[] Min = new float[16];
            public readonly float[] Max = new float[16];
            public readonly List<(PendingBoneSlotSpec Spec, Matrix4x4 BindPose)> Specs = new List<(PendingBoneSlotSpec, Matrix4x4)>(2);
            public bool HasNonFinite;
            public string NonFinitePatchId = string.Empty;

            public ClusterBindPoseGroup(string groupKey, int candidateSlot, Transform resolvedBone, string targetPath)
            {
                GroupKey = groupKey;
                CandidateSlot = candidateSlot;
                ResolvedBone = resolvedBone;
                TargetPath = targetPath ?? string.Empty;
            }

            public void Add(PendingBoneSlotSpec spec, Matrix4x4 bindPose)
            {
                if (!IsFiniteMatrix(bindPose))
                {
                    if (!HasNonFinite)
                    {
                        HasNonFinite = true;
                        NonFinitePatchId = spec.Input.PatchId ?? string.Empty;
                    }
                    else
                    {
                        string pid = spec.Input.PatchId ?? string.Empty;
                        if (StringComparer.Ordinal.Compare(pid, NonFinitePatchId) < 0)
                        {
                            NonFinitePatchId = pid;
                        }
                    }

                    Specs.Add((spec, bindPose));
                    return;
                }

                bool isFirstFinite = true;
                for (int s = 0; s < Specs.Count; s++)
                {
                    if (IsFiniteMatrix(Specs[s].BindPose))
                    {
                        isFirstFinite = false;
                        break;
                    }
                }

                if (isFirstFinite)
                {
                    for (int i = 0; i < 16; i++)
                    {
                        Min[i] = Max[i] = bindPose[i];
                    }
                }
                else
                {
                    for (int i = 0; i < 16; i++)
                    {
                        float v = bindPose[i];
                        if (v < Min[i]) Min[i] = v;
                        if (v > Max[i]) Max[i] = v;
                    }
                }

                Specs.Add((spec, bindPose));
            }

            public bool ExceedsSpreadTolerance(float tolerance, out string firstPatchId, out string conflictingPatchId)
            {
                firstPatchId = Specs.Count > 0 ? Specs[0].Spec.Input.PatchId : string.Empty;
                conflictingPatchId = firstPatchId;
                if (HasNonFinite)
                {
                    firstPatchId = NonFinitePatchId;
                    conflictingPatchId = NonFinitePatchId;
                    return true;
                }

                for (int i = 0; i < 16; i++)
                {
                    if (Max[i] - Min[i] > tolerance)
                    {
                        string minPatch = null;
                        for (int s = 0; s < Specs.Count; s++)
                        {
                            if (Specs[s].BindPose[i] == Min[i])
                            {
                                string pid = Specs[s].Spec.Input.PatchId ?? string.Empty;
                                if (minPatch == null || StringComparer.Ordinal.Compare(pid, minPatch) < 0)
                                {
                                    minPatch = pid;
                                }
                            }
                        }

                        string maxPatch = null;
                        for (int s = 0; s < Specs.Count; s++)
                        {
                            if (Specs[s].BindPose[i] == Max[i])
                            {
                                string pid = Specs[s].Spec.Input.PatchId ?? string.Empty;
                                if (string.Equals(pid, minPatch, StringComparison.Ordinal))
                                {
                                    continue;
                                }

                                if (maxPatch == null || StringComparer.Ordinal.Compare(pid, maxPatch) < 0)
                                {
                                    maxPatch = pid;
                                }
                            }
                        }

                        if (maxPatch == null)
                        {
                            maxPatch = minPatch ?? string.Empty;
                        }
                        minPatch ??= string.Empty;

                        if (StringComparer.Ordinal.Compare(minPatch, maxPatch) <= 0)
                        {
                            firstPatchId = minPatch;
                            conflictingPatchId = maxPatch;
                        }
                        else
                        {
                            firstPatchId = maxPatch;
                            conflictingPatchId = minPatch;
                        }

                        return true;
                    }
                }

                return false;
            }

            public Matrix4x4 SelectDeterministicRepresentative()
            {
                Matrix4x4 best = Specs[0].BindPose;
                double bestDist = MaxMidpointDistance(best);
                for (int i = 1; i < Specs.Count; i++)
                {
                    Matrix4x4 cand = Specs[i].BindPose;
                    double d = MaxMidpointDistance(cand);
                    int dCmp = d.CompareTo(bestDist);
                    if (dCmp < 0 || (dCmp == 0 && CompareMatrixLexicographic(cand, best) < 0))
                    {
                        best = cand;
                        bestDist = d;
                    }
                }

                return CanonicalizeMatrixSignedZeros(best);
            }

            private double MaxMidpointDistance(Matrix4x4 m)
            {
                double maxD = 0d;
                for (int i = 0; i < 16; i++)
                {
                    double mid = 0.5d * ((double)Min[i] + (double)Max[i]);
                    double d = Math.Abs((double)m[i] - mid);
                    if (d > maxD) maxD = d;
                }

                return maxD;
            }
        }

        private static string FindTopmostMissingPath(Transform root, string path)
        {
            string current = path;
            while (current.Contains("/"))
            {
                string parent = current.Substring(0, current.LastIndexOf('/'));
                if (root.Find(parent) != null)
                {
                    break;
                }

                current = parent;
            }

            return current;
        }

        private static string GetParentPath(string normalizedPath)
        {
            if (string.IsNullOrEmpty(normalizedPath)) return string.Empty;
            int slash = normalizedPath.LastIndexOf('/');
            return slash > 0 ? normalizedPath.Substring(0, slash) : string.Empty;
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

        private sealed class BoneTable
        {
            private readonly Dictionary<string, int> m_IndexByPath = new Dictionary<string, int>(StringComparer.Ordinal);
            private readonly Dictionary<Transform, int> m_IndexByTransform = new Dictionary<Transform, int>();
            private readonly Dictionary<string, int> m_ExplicitIncomingSlotByPath = new Dictionary<string, int>(StringComparer.Ordinal);
            private readonly Dictionary<string, List<int>> m_AllExplicitIncomingSlotsByPath = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            private readonly Dictionary<int, HashSet<string>> m_AuthoritativeOwnersByIncomingSlot = new Dictionary<int, HashSet<string>>();
            private readonly Dictionary<int, string> m_InitialBonePathByIncomingSlot = new Dictionary<int, string>();
            private readonly HashSet<string> m_AmbiguousPaths = new HashSet<string>(StringComparer.Ordinal);
            private readonly HashSet<string> m_AmbiguousExplicitIncomingPaths = new HashSet<string>(StringComparer.Ordinal);
            private readonly HashSet<Transform> m_AmbiguousTransforms = new HashSet<Transform>();
            private readonly HashSet<int> m_IncomingSlots = new HashSet<int>();
            private readonly HashSet<int> m_OverrideDerivedSlots = new HashSet<int>();
            private readonly HashSet<int> m_ScratchNullSlots = new HashSet<int>();
            private readonly HashSet<int> m_AmbiguousPathSlots = new HashSet<int>();
            private readonly Dictionary<int, int> m_OriginalIndexMap = new Dictionary<int, int>();
            private readonly Dictionary<int, int> m_UnresolvedOriginalSlotMap = new Dictionary<int, int>();
            private readonly Dictionary<string, int> m_AmbiguousImportedPathSlotMap = new Dictionary<string, int>(StringComparer.Ordinal);

            public List<string> BonePaths { get; } = new List<string>();
            public List<Matrix4x4> BindPoses { get; } = new List<Matrix4x4>();
            public List<Transform> Transforms { get; } = new List<Transform>();

            public bool IsIncomingSlot(int index) => m_IncomingSlots.Contains(index);
            public bool HasScratchNullSlots => m_ScratchNullSlots.Count > 0;
            public bool IsScratchNullSlot(int index) => m_ScratchNullSlots.Contains(index);
            public bool IsAmbiguousPathSlot(int index) => m_AmbiguousPathSlots.Contains(index);

            public int FirstResolvedTransformSlotIndex
            {
                get
                {
                    for (int i = 0; i < Transforms.Count; i++)
                    {
                        if (!m_ScratchNullSlots.Contains(i) && Transforms[i] != null) return i;
                    }

                    return -1;
                }
            }

            public string GetSlotLabel(int index)
            {
                return (index >= 0 && index < BonePaths.Count && !string.IsNullOrEmpty(BonePaths[index]))
                    ? BonePaths[index]
                    : $"slot#{index}";
            }

            public bool TryGetIndex(Transform transform, string path, out int index)
            {
                if (transform != null)
                {
                    if (m_AmbiguousTransforms.Contains(transform))
                    {
                        index = -1;
                        return false;
                    }

                    if (m_IndexByTransform.TryGetValue(transform, out index))
                    {
                        return true;
                    }
                }

                string normalized = FbxNameUtility.NormalizePath(path ?? string.Empty);
                if (!string.IsNullOrEmpty(normalized) && !m_AmbiguousPaths.Contains(normalized) &&
                    m_IndexByPath.TryGetValue(normalized, out index) &&
                    Transforms[index] == transform)
                {
                    return true;
                }

                index = -1;
                return false;
            }

            public bool TryGetIncomingIndex(Transform transform, string path, out int index)
            {
                if (TryGetIndex(transform, path, out index) && m_IncomingSlots.Contains(index))
                {
                    return true;
                }

                index = -1;
                return false;
            }

            public bool TryGetExplicitIncomingSlotsByPath(string path, out IReadOnlyList<int> slots)
            {
                string normalized = FbxNameUtility.NormalizePath(path ?? string.Empty);
                if (!string.IsNullOrEmpty(normalized) &&
                    m_AllExplicitIncomingSlotsByPath.TryGetValue(normalized, out var list) &&
                    list.Count > 0)
                {
                    slots = list;
                    return true;
                }

                slots = null;
                return false;
            }

            public bool TryFindSlotByTransformAndBindPose(Transform transform, Matrix4x4 bindPose, float tolerance, out int slotIndex)
            {
                if (transform != null)
                {
                    for (int i = 0; i < Transforms.Count; i++)
                    {
                        if (Transforms[i] == transform && BindPosesApproximatelyEqual(BindPoses[i], bindPose, tolerance))
                        {
                            slotIndex = i;
                            return true;
                        }
                    }
                }

                slotIndex = -1;
                return false;
            }

            public bool TryFindCanonicalSlotByTransformAndBindPose(Transform transform, Matrix4x4 bindPose, float tolerance, out int slotIndex)
            {
                if (transform != null)
                {
                    for (int i = 0; i < Transforms.Count; i++)
                    {
                        if (!m_OverrideDerivedSlots.Contains(i) &&
                            Transforms[i] == transform &&
                            BindPosesApproximatelyEqual(BindPoses[i], bindPose, tolerance))
                        {
                            slotIndex = i;
                            return true;
                        }
                    }
                }

                slotIndex = -1;
                return false;
            }

            public bool TryFindCanonicalNullSlotByPathAndBindPose(string path, Matrix4x4 bindPose, float tolerance, out int slotIndex)
            {
                string normalized = FbxNameUtility.NormalizePath(path ?? string.Empty);
                for (int i = 0; i < Transforms.Count; i++)
                {
                    if (!m_OverrideDerivedSlots.Contains(i) &&
                        Transforms[i] == null &&
                        string.Equals(BonePaths[i], normalized, StringComparison.Ordinal) &&
                        BindPosesApproximatelyEqual(BindPoses[i], bindPose, tolerance))
                    {
                        slotIndex = i;
                        return true;
                    }
                }

                slotIndex = -1;
                return false;
            }

            public bool TryFindSlotForOverride(
                Transform transform,
                Matrix4x4 bindPose,
                float tolerance,
                HashSet<int> canonicalFinalizedSlots,
                IReadOnlyDictionary<string, (string PatchId, GeneratedSkeletonBoneRecord Record)> mergedSkeletonBonesByPath,
                out int slotIndex)
            {
                if (transform != null)
                {
                    for (int i = 0; i < Transforms.Count; i++)
                    {
                        if (Transforms[i] != transform || !BindPosesApproximatelyEqual(BindPoses[i], bindPose, tolerance))
                        {
                            continue;
                        }

                        // Do not reuse a pre-existing incoming slot whose chain has an authored skeleton edit unless
                        // canonical post-edit bindpose settlement already finalized it to this same bindpose.
                        if (m_IncomingSlots.Contains(i) &&
                            (canonicalFinalizedSlots == null || !canonicalFinalizedSlots.Contains(i)) &&
                            HasAuthoredSkeletonEditInChain(BonePaths[i], null, mergedSkeletonBonesByPath))
                        {
                            continue;
                        }

                        slotIndex = i;
                        return true;
                    }
                }

                slotIndex = -1;
                return false;
            }

            public DonorGroupKey ResolveSpecBaselineDonors(
                int? importedPreboundSlot,
                int[] agreedCandidateSlots,
                bool hasAuthoritativeDonorSet,
                int[] verifiedDonorSlots,
                bool donorIsAmbiguousMultiSlot,
                Transform resolvedBone,
                string sourcePath,
                string targetPath,
                bool isProxyOverride)
            {
                if (donorIsAmbiguousMultiSlot)
                {
                    return DonorGroupKey.Ambiguous;
                }

                if (hasAuthoritativeDonorSet)
                {
                    if (verifiedDonorSlots == null || verifiedDonorSlots.Length == 0)
                    {
                        return DonorGroupKey.NoDonors;
                    }

                    var validVerified = new List<int>(verifiedDonorSlots.Length);
                    for (int i = 0; i < verifiedDonorSlots.Length; i++)
                    {
                        if (m_IncomingSlots.Contains(verifiedDonorSlots[i]))
                        {
                            validVerified.Add(verifiedDonorSlots[i]);
                        }
                    }

                    return validVerified.Count > 0
                        ? DonorGroupKey.FromSortedSlots(validVerified)
                        : DonorGroupKey.NoDonors;
                }

                if (importedPreboundSlot.HasValue && m_ScratchNullSlots.Contains(importedPreboundSlot.Value))
                {
                    return DonorGroupKey.NoDonors;
                }

                if (agreedCandidateSlots != null && agreedCandidateSlots.Length > 1)
                {
                    var validIncoming = new List<int>(agreedCandidateSlots.Length);
                    for (int i = 0; i < agreedCandidateSlots.Length; i++)
                    {
                        if (m_IncomingSlots.Contains(agreedCandidateSlots[i]))
                        {
                            validIncoming.Add(agreedCandidateSlots[i]);
                        }
                    }

                    if (validIncoming.Count > 0)
                    {
                        return DonorGroupKey.FromSortedSlots(validIncoming);
                    }
                }

                // Tier 1: Exact imported FBX-to-Unity slot correspondence.
                if (importedPreboundSlot.HasValue &&
                    importedPreboundSlot.Value >= 0 &&
                    m_IncomingSlots.Contains(importedPreboundSlot.Value))
                {
                    return DonorGroupKey.FromSingle(importedPreboundSlot.Value);
                }

                var orderedIncoming = m_IncomingSlots.OrderBy(k => k).ToArray();

                // Tier 2: Exact initial incoming slot path match (identifies original donor even when a proxy redirects output to another transform).
                string normSource = FbxNameUtility.NormalizePath(sourcePath ?? string.Empty);
                if (!string.IsNullOrEmpty(normSource))
                {
                    var sourceMatches = new List<int>();
                    for (int i = 0; i < orderedIncoming.Length; i++)
                    {
                        int inc = orderedIncoming[i];
                        if (CanClaimUnverifiedIncomingDonor(inc, normSource) &&
                            m_InitialBonePathByIncomingSlot.TryGetValue(inc, out string initPath) &&
                            string.Equals(initPath, normSource, StringComparison.Ordinal))
                        {
                            sourceMatches.Add(inc);
                        }
                    }

                    if (sourceMatches.Count > 0)
                    {
                        return DonorGroupKey.FromSortedSlots(sourceMatches);
                    }
                }

                // Tier 3: Fallback incoming slots matching ResolvedBone or normalized TargetPath (excluding slots authoritatively owned by another path).
                string normTarget = FbxNameUtility.NormalizePath(targetPath ?? string.Empty);
                var fallbackMatches = new List<int>();
                for (int i = 0; i < orderedIncoming.Length; i++)
                {
                    int inc = orderedIncoming[i];
                    if (!CanClaimUnverifiedIncomingDonor(inc, normSource))
                    {
                        continue;
                    }

                    if (resolvedBone != null && Transforms[inc] == resolvedBone)
                    {
                        fallbackMatches.Add(inc);
                        continue;
                    }

                    if (!string.IsNullOrEmpty(normTarget) &&
                        m_InitialBonePathByIncomingSlot.TryGetValue(inc, out string initPath) &&
                        string.Equals(initPath, normTarget, StringComparison.Ordinal))
                    {
                        fallbackMatches.Add(inc);
                    }
                }

                return fallbackMatches.Count > 0
                    ? DonorGroupKey.FromSortedSlots(fallbackMatches)
                    : DonorGroupKey.NoDonors;
            }

            public void RegisterVerifiedImportedSlotOwners(IReadOnlyDictionary<string, IReadOnlyList<int>> importedSlotsByFbxBonePath)
            {
                if (importedSlotsByFbxBonePath == null)
                {
                    return;
                }

                foreach (var kv in importedSlotsByFbxBonePath)
                {
                    string normOwner = FbxNameUtility.NormalizePath(kv.Key ?? string.Empty);
                    if (string.IsNullOrEmpty(normOwner) || kv.Value == null)
                    {
                        continue;
                    }

                    for (int i = 0; i < kv.Value.Count; i++)
                    {
                        if (TryRemapOriginalIndex(kv.Value[i], out int incSlot) && m_IncomingSlots.Contains(incSlot))
                        {
                            RegisterAuthoritativeIncomingOwner(incSlot, normOwner);
                        }
                    }
                }
            }

            private void RegisterAuthoritativeIncomingOwner(int incSlot, string normalizedOwnerPath)
            {
                if (string.IsNullOrEmpty(normalizedOwnerPath))
                {
                    return;
                }

                if (!m_AuthoritativeOwnersByIncomingSlot.TryGetValue(incSlot, out var owners))
                {
                    owners = new HashSet<string>(StringComparer.Ordinal);
                    m_AuthoritativeOwnersByIncomingSlot.Add(incSlot, owners);
                }

                owners.Add(normalizedOwnerPath);
            }

            private bool CanClaimUnverifiedIncomingDonor(int incSlot, string normalizedSourcePath)
            {
                if (!m_AuthoritativeOwnersByIncomingSlot.TryGetValue(incSlot, out var owners) || owners.Count == 0)
                {
                    return true;
                }

                return !string.IsNullOrEmpty(normalizedSourcePath) && owners.Contains(normalizedSourcePath);
            }

            public bool TryFindNullSlotByPathAndBindPose(string path, Matrix4x4 bindPose, float tolerance, out int slotIndex)
            {
                string normalized = FbxNameUtility.NormalizePath(path ?? string.Empty);
                for (int i = 0; i < Transforms.Count; i++)
                {
                    if (Transforms[i] == null &&
                        string.Equals(BonePaths[i], normalized, StringComparison.Ordinal) &&
                        BindPosesApproximatelyEqual(BindPoses[i], bindPose, tolerance))
                    {
                        slotIndex = i;
                        return true;
                    }
                }

                slotIndex = -1;
                return false;
            }

            public bool TryGetTransform(string path, out Transform transform)
            {
                string normalized = FbxNameUtility.NormalizePath(path ?? string.Empty);
                if (!string.IsNullOrEmpty(normalized) &&
                    !m_AmbiguousPaths.Contains(normalized) &&
                    m_IndexByPath.TryGetValue(normalized, out int index) &&
                    index >= 0 && index < Transforms.Count)
                {
                    transform = Transforms[index];
                    return transform != null;
                }

                transform = null;
                return false;
            }

            public bool TryGetExistingIndexByPath(string path, out int index)
            {
                string normalized = FbxNameUtility.NormalizePath(path ?? string.Empty);
                if (!string.IsNullOrEmpty(normalized) &&
                    !m_AmbiguousPaths.Contains(normalized) &&
                    m_IndexByPath.TryGetValue(normalized, out index))
                {
                    return true;
                }

                index = -1;
                return false;
            }

            public bool TryGetSlotAndBindPoseByPath(string path, out int slotIndex, out Matrix4x4 bindPose)
            {
                string normalized = FbxNameUtility.NormalizePath(path ?? string.Empty);
                if (!string.IsNullOrEmpty(normalized) &&
                    !m_AmbiguousPaths.Contains(normalized) &&
                    m_IndexByPath.TryGetValue(normalized, out slotIndex) &&
                    slotIndex >= 0 && slotIndex < BindPoses.Count)
                {
                    bindPose = BindPoses[slotIndex];
                    return true;
                }

                slotIndex = -1;
                bindPose = Matrix4x4.identity;
                return false;
            }

            public bool TryGetSlotAndBindPoseByTransform(Transform transform, out int slotIndex, out Matrix4x4 bindPose)
            {
                if (transform != null &&
                    !m_AmbiguousTransforms.Contains(transform) &&
                    m_IndexByTransform.TryGetValue(transform, out slotIndex) &&
                    slotIndex >= 0 && slotIndex < BindPoses.Count)
                {
                    bindPose = BindPoses[slotIndex];
                    return true;
                }

                slotIndex = -1;
                bindPose = Matrix4x4.identity;
                return false;
            }

            public void SetDefinition(int index, Transform transform, string path, Matrix4x4 bindPose)
            {
                string normalized = FbxNameUtility.NormalizePath(path ?? string.Empty);
                BindPoses[index] = bindPose;
                BonePaths[index] = normalized;
                if (transform != null)
                {
                    Transforms[index] = transform;
                    RegisterTransformIndex(transform, index, bindPose);
                }

                if (!string.IsNullOrEmpty(normalized))
                {
                    RegisterPathIndex(normalized, index, bindPose);
                }
            }

            public int AddIncomingSlot(
                Transform transform,
                string path,
                Matrix4x4 bindPose,
                int originalIndex,
                bool hasExplicitIncomingPath = false)
            {
                string normalized = FbxNameUtility.NormalizePath(path ?? string.Empty);
                int index = BonePaths.Count;
                BonePaths.Add(normalized);
                BindPoses.Add(bindPose);
                Transforms.Add(transform);
                if (originalIndex >= 0)
                {
                    m_OriginalIndexMap[originalIndex] = index;
                    m_IncomingSlots.Add(index);
                    m_InitialBonePathByIncomingSlot[index] = normalized;
                }

                if (hasExplicitIncomingPath && !string.IsNullOrEmpty(normalized))
                {
                    RegisterAuthoritativeIncomingOwner(index, normalized);
                    if (!m_AllExplicitIncomingSlotsByPath.TryGetValue(normalized, out var allExpList))
                    {
                        allExpList = new List<int>(2);
                        m_AllExplicitIncomingSlotsByPath.Add(normalized, allExpList);
                    }

                    allExpList.Add(index);
                    if (m_ExplicitIncomingSlotByPath.TryGetValue(normalized, out int prevExp) && prevExp != index)
                    {
                        m_AmbiguousExplicitIncomingPaths.Add(normalized);
                        m_ExplicitIncomingSlotByPath.Remove(normalized);
                    }
                    else if (!m_AmbiguousExplicitIncomingPaths.Contains(normalized))
                    {
                        m_ExplicitIncomingSlotByPath[normalized] = index;
                    }
                }

                if (transform != null)
                {
                    RegisterTransformIndex(transform, index, bindPose);
                }

                if (!string.IsNullOrEmpty(normalized))
                {
                    RegisterPathIndex(normalized, index, bindPose);
                }

                return index;
            }

            public int AddDistinctSlot(Transform transform, string path, Matrix4x4 bindPose)
            {
                return AddIncomingSlot(transform, path, bindPose, -1);
            }

            public int AddOverrideDerivedSlot(Transform transform, string path, Matrix4x4 bindPose)
            {
                string normalized = FbxNameUtility.NormalizePath(path ?? string.Empty);
                int index = BonePaths.Count;
                BonePaths.Add(normalized);
                BindPoses.Add(bindPose);
                Transforms.Add(transform);
                m_OverrideDerivedSlots.Add(index);
                return index;
            }

            public int Add(Transform transform, string path, Matrix4x4 bindPose, int originalIndex = -1)
            {
                string normalized = FbxNameUtility.NormalizePath(path ?? string.Empty);
                if ((transform != null && !m_AmbiguousTransforms.Contains(transform) && m_IndexByTransform.TryGetValue(transform, out int existing)) ||
                    (transform == null && !string.IsNullOrEmpty(normalized) && !m_AmbiguousPaths.Contains(normalized) && m_IndexByPath.TryGetValue(normalized, out existing)))
                {
                    SetDefinition(existing, transform, normalized, bindPose);
                    if (originalIndex >= 0) m_OriginalIndexMap[originalIndex] = existing;
                    return existing;
                }

                return AddIncomingSlot(transform, normalized, bindPose, originalIndex);
            }

            public int AddSynthetic(string key, Matrix4x4 bindPose, int originalIndex = -1)
            {
                int index = BonePaths.Count;
                if (!string.IsNullOrEmpty(key)) RegisterPathIndex(key, index, bindPose);
                BonePaths.Add(key ?? string.Empty);
                BindPoses.Add(bindPose);
                Transforms.Add(null);
                if (originalIndex >= 0)
                {
                    m_OriginalIndexMap[originalIndex] = index;
                    m_IncomingSlots.Add(index);
                }
                return index;
            }

            public void AddAlias(string sourcePath, int targetIndex)
            {
                if (m_OverrideDerivedSlots.Contains(targetIndex))
                {
                    return;
                }

                string normalized = FbxNameUtility.NormalizePath(sourcePath ?? string.Empty);
                if (!string.IsNullOrEmpty(normalized) && targetIndex >= 0 && targetIndex < BindPoses.Count)
                {
                    RegisterPathIndex(normalized, targetIndex, BindPoses[targetIndex]);
                }
            }

            public bool TryRemapOriginalIndex(int originalIndex, out int slotIndex)
            {
                return m_OriginalIndexMap.TryGetValue(originalIndex, out slotIndex);
            }

            public void ResolveImportedCandidateSlots(
                IReadOnlyList<int> importedOriginalSlots,
                string path,
                out int primarySlot,
                out Transform agreedTransform,
                out int[] agreedSlots,
                out bool isNullOrAmbiguous)
            {
                if (importedOriginalSlots.Count == 1)
                {
                    int origSlot = importedOriginalSlots[0];
                    if (TryRemapOriginalIndex(origSlot, out int remSlot) &&
                        (uint)remSlot < (uint)Transforms.Count)
                    {
                        primarySlot = remSlot;
                        agreedTransform = Transforms[remSlot];
                        agreedSlots = agreedTransform != null ? new[] { remSlot } : null;
                        isNullOrAmbiguous = agreedTransform == null;
                        return;
                    }

                    primarySlot = GetOrAddUnresolvedOriginalSlot(origSlot, path);
                    agreedTransform = null;
                    agreedSlots = null;
                    isNullOrAmbiguous = true;
                    return;
                }

                var remapped = new List<int>(importedOriginalSlots.Count);
                bool anyMissing = false;
                for (int i = 0; i < importedOriginalSlots.Count; i++)
                {
                    if (TryRemapOriginalIndex(importedOriginalSlots[i], out int remSlot) &&
                        (uint)remSlot < (uint)Transforms.Count)
                    {
                        remapped.Add(remSlot);
                    }
                    else
                    {
                        anyMissing = true;
                    }
                }

                if (!anyMissing && TryResolveUnanimousCandidateSubset(remapped, out primarySlot, out agreedTransform, out agreedSlots))
                {
                    isNullOrAmbiguous = false;
                    return;
                }

                primarySlot = GetOrAddAmbiguousImportedPathSlot(path);
                agreedTransform = null;
                agreedSlots = null;
                isNullOrAmbiguous = true;
            }

            public void ResolveExplicitCandidateSlots(
                IReadOnlyList<int> explicitSlots,
                string path,
                out int primarySlot,
                out Transform agreedTransform,
                out int[] agreedSlots,
                out bool isNullOrAmbiguous)
            {
                if (explicitSlots.Count == 1)
                {
                    int singleSlot = explicitSlots[0];
                    if ((uint)singleSlot < (uint)Transforms.Count)
                    {
                        primarySlot = singleSlot;
                        agreedTransform = Transforms[singleSlot];
                        agreedSlots = agreedTransform != null ? new[] { singleSlot } : null;
                        isNullOrAmbiguous = agreedTransform == null;
                        return;
                    }
                }

                if (TryResolveUnanimousCandidateSubset(explicitSlots, out primarySlot, out agreedTransform, out agreedSlots))
                {
                    isNullOrAmbiguous = false;
                    return;
                }

                primarySlot = GetOrAddAmbiguousImportedPathSlot(path);
                agreedTransform = null;
                agreedSlots = null;
                isNullOrAmbiguous = true;
            }

            public void ResolveDonorCandidateSlots(
                IReadOnlyList<int> candidates,
                bool isImportedOriginal,
                out int[] validIncomingDonors,
                out bool isAmbiguousMultiSlot)
            {
                validIncomingDonors = Array.Empty<int>();
                isAmbiguousMultiSlot = false;
                if (candidates == null || candidates.Count == 0)
                {
                    return;
                }

                if (candidates.Count == 1)
                {
                    int slot = candidates[0];
                    if (isImportedOriginal)
                    {
                        if (TryRemapOriginalIndex(slot, out int remSlot) && (uint)remSlot < (uint)Transforms.Count)
                        {
                            validIncomingDonors = new[] { remSlot };
                        }
                    }
                    else if ((uint)slot < (uint)Transforms.Count)
                    {
                        validIncomingDonors = new[] { slot };
                    }

                    return;
                }

                var remapped = new List<int>(candidates.Count);
                bool anyMissing = false;
                for (int i = 0; i < candidates.Count; i++)
                {
                    int c = candidates[i];
                    if (isImportedOriginal)
                    {
                        if (TryRemapOriginalIndex(c, out int remSlot) && (uint)remSlot < (uint)Transforms.Count)
                        {
                            remapped.Add(remSlot);
                        }
                        else
                        {
                            anyMissing = true;
                        }
                    }
                    else if ((uint)c < (uint)Transforms.Count)
                    {
                        remapped.Add(c);
                    }
                    else
                    {
                        anyMissing = true;
                    }
                }

                if (!anyMissing && TryResolveUnanimousCandidateSubset(remapped, out _, out _, out var agreed))
                {
                    validIncomingDonors = agreed;
                    return;
                }

                isAmbiguousMultiSlot = true;
            }

            private bool TryResolveUnanimousCandidateSubset(
                IReadOnlyList<int> candidates,
                out int primarySlot,
                out Transform agreedTransform,
                out int[] sortedUniqueSlots)
            {
                primarySlot = -1;
                agreedTransform = null;
                sortedUniqueSlots = null;
                if (candidates == null || candidates.Count == 0)
                {
                    return false;
                }

                var unique = new SortedSet<int>();
                for (int i = 0; i < candidates.Count; i++)
                {
                    int s = candidates[i];
                    if ((uint)s >= (uint)Transforms.Count || Transforms[s] == null)
                    {
                        return false;
                    }

                    unique.Add(s);
                }

                int[] arr = unique.ToArray();
                Transform sharedTr = Transforms[arr[0]];
                var minCell = new float[16];
                var maxCell = new float[16];
                Matrix4x4 firstPose = BindPoses[arr[0]];
                if (!IsFiniteMatrix(firstPose))
                {
                    return false;
                }

                for (int c = 0; c < 16; c++)
                {
                    minCell[c] = maxCell[c] = firstPose[c];
                }

                for (int i = 1; i < arr.Length; i++)
                {
                    int s = arr[i];
                    if (Transforms[s] != sharedTr)
                    {
                        return false;
                    }

                    Matrix4x4 pose = BindPoses[s];
                    if (!IsFiniteMatrix(pose))
                    {
                        return false;
                    }

                    for (int c = 0; c < 16; c++)
                    {
                        float v = pose[c];
                        if (v < minCell[c]) minCell[c] = v;
                        if (v > maxCell[c]) maxCell[c] = v;
                    }
                }

                for (int c = 0; c < 16; c++)
                {
                    if ((double)maxCell[c] - (double)minCell[c] > 1e-5d)
                    {
                        return false;
                    }
                }

                int bestSlot = arr[0];
                double bestMidDist = ComputeMidpointChebyshevDistance(BindPoses[bestSlot], minCell, maxCell);
                for (int i = 1; i < arr.Length; i++)
                {
                    int candSlot = arr[i];
                    double d = ComputeMidpointChebyshevDistance(BindPoses[candSlot], minCell, maxCell);
                    int dCmp = d.CompareTo(bestMidDist);
                    if (dCmp < 0 || (dCmp == 0 && CompareMatrixLexicographic(BindPoses[candSlot], BindPoses[bestSlot]) < 0))
                    {
                        bestSlot = candSlot;
                        bestMidDist = d;
                    }
                }

                primarySlot = bestSlot;
                agreedTransform = sharedTr;
                sortedUniqueSlots = arr;
                return true;
            }

            private static double ComputeMidpointChebyshevDistance(Matrix4x4 m, float[] minCell, float[] maxCell)
            {
                double maxD = 0d;
                for (int c = 0; c < 16; c++)
                {
                    double mid = 0.5d * ((double)minCell[c] + (double)maxCell[c]);
                    double d = Math.Abs((double)m[c] - mid);
                    if (d > maxD) maxD = d;
                }

                return maxD;
            }

            public void SyncAgreedSiblingBindPoses(
                int[] agreedSlots,
                int primarySlot,
                Transform resolvedBone,
                string targetPath,
                Matrix4x4 bindPose,
                HashSet<int> canonicalFinalizedSlots,
                HashSet<int> finalizedPostEditSlots)
            {
                if (agreedSlots == null || agreedSlots.Length <= 1)
                {
                    return;
                }

                for (int i = 0; i < agreedSlots.Length; i++)
                {
                    int sib = agreedSlots[i];
                    if (sib == primarySlot || (uint)sib >= (uint)Transforms.Count)
                    {
                        continue;
                    }

                    SetDefinition(sib, resolvedBone, targetPath, bindPose);
                    canonicalFinalizedSlots?.Add(sib);
                    finalizedPostEditSlots?.Add(sib);
                }
            }

            public int GetOrAddUnresolvedOriginalSlot(int originalIndex, string path)
            {
                if (m_UnresolvedOriginalSlotMap.TryGetValue(originalIndex, out int existing))
                {
                    return existing;
                }

                string normalized = FbxNameUtility.NormalizePath(path ?? string.Empty);
                int index = BonePaths.Count;
                BonePaths.Add(normalized);
                BindPoses.Add(Matrix4x4.identity);
                Transforms.Add(null);
                m_ScratchNullSlots.Add(index);
                m_UnresolvedOriginalSlotMap.Add(originalIndex, index);
                return index;
            }

            public int GetOrAddAmbiguousImportedPathSlot(string path)
            {
                string normalized = FbxNameUtility.NormalizePath(path ?? string.Empty);
                if (m_AmbiguousImportedPathSlotMap.TryGetValue(normalized, out int existing))
                {
                    return existing;
                }

                int index = BonePaths.Count;
                BonePaths.Add(normalized);
                BindPoses.Add(Matrix4x4.identity);
                Transforms.Add(null);
                m_ScratchNullSlots.Add(index);
                m_AmbiguousPathSlots.Add(index);
                m_AmbiguousImportedPathSlotMap.Add(normalized, index);
                return index;
            }

            public int AddScratchNullSlot(string path)
            {
                string normalized = FbxNameUtility.NormalizePath(path ?? string.Empty);
                int index = BonePaths.Count;
                BonePaths.Add(normalized);
                BindPoses.Add(Matrix4x4.identity);
                Transforms.Add(null);
                m_ScratchNullSlots.Add(index);
                return index;
            }

            public void CompactUnreferencedScratchSlots(NativeArray<BoneWeight1> outputWeights, List<AppliedBoneBinding> records)
            {
                bool anyCandidateNullSlot = m_ScratchNullSlots.Count > 0;
                if (!anyCandidateNullSlot)
                {
                    for (int i = 0; i < Transforms.Count; i++)
                    {
                        if (!m_IncomingSlots.Contains(i) && Transforms[i] == null)
                        {
                            anyCandidateNullSlot = true;
                            break;
                        }
                    }
                }

                if (!anyCandidateNullSlot)
                {
                    return;
                }

                var referenced = new HashSet<int>();
                for (int i = 0; i < outputWeights.Length; i++)
                {
                    if (outputWeights[i].weight > 0f)
                    {
                        referenced.Add(outputWeights[i].boneIndex);
                    }
                }

                int oldCount = Transforms.Count;
                var remap = new int[oldCount];
                int writeIdx = 0;
                for (int i = 0; i < oldCount; i++)
                {
                    if ((m_ScratchNullSlots.Contains(i) || !m_IncomingSlots.Contains(i)) &&
                        Transforms[i] == null &&
                        !referenced.Contains(i))
                    {
                        remap[i] = -1;
                        continue;
                    }

                    if (writeIdx != i)
                    {
                        Transforms[writeIdx] = Transforms[i];
                        BindPoses[writeIdx] = BindPoses[i];
                        BonePaths[writeIdx] = BonePaths[i];
                    }

                    remap[i] = writeIdx;
                    writeIdx++;
                }

                if (writeIdx == oldCount)
                {
                    return;
                }

                Transforms.RemoveRange(writeIdx, oldCount - writeIdx);
                BindPoses.RemoveRange(writeIdx, oldCount - writeIdx);
                BonePaths.RemoveRange(writeIdx, oldCount - writeIdx);

                for (int i = 0; i < outputWeights.Length; i++)
                {
                    var bw = outputWeights[i];
                    if ((uint)bw.boneIndex < (uint)oldCount && remap[bw.boneIndex] >= 0)
                    {
                        bw.boneIndex = remap[bw.boneIndex];
                        outputWeights[i] = bw;
                    }
                }

                if (records != null)
                {
                    for (int r = records.Count - 1; r >= 0; r--)
                    {
                        var rec = records[r];
                        if ((uint)rec.BoneIndex < (uint)oldCount)
                        {
                            int newIdx = remap[rec.BoneIndex];
                            if (newIdx < 0)
                            {
                                records.RemoveAt(r);
                            }
                            else if (newIdx != rec.BoneIndex)
                            {
                                records[r] = new AppliedBoneBinding(
                                    rec.PatchId,
                                    rec.SourceBonePath,
                                    rec.TargetBonePath,
                                    rec.TargetTransform,
                                    rec.BindPose,
                                    newIdx,
                                    rec.IsNewBone);
                            }
                        }
                    }
                }
            }

            private void RegisterTransformIndex(Transform transform, int slotIndex, Matrix4x4 bindPose)
            {
                if (transform == null || m_OverrideDerivedSlots.Contains(slotIndex) || m_AmbiguousTransforms.Contains(transform))
                {
                    return;
                }

                for (int i = 0; i < Transforms.Count; i++)
                {
                    if (i != slotIndex &&
                        !m_OverrideDerivedSlots.Contains(i) &&
                        Transforms[i] == transform &&
                        !BindPosesApproximatelyEqual(BindPoses[i], bindPose, 1e-5f))
                    {
                        m_AmbiguousTransforms.Add(transform);
                        m_IndexByTransform.Remove(transform);
                        return;
                    }
                }

                if (!m_IndexByTransform.ContainsKey(transform))
                {
                    m_IndexByTransform[transform] = slotIndex;
                }
            }

            private void RegisterPathIndex(string normalizedPath, int slotIndex, Matrix4x4 bindPose)
            {
                if (string.IsNullOrEmpty(normalizedPath) ||
                    m_OverrideDerivedSlots.Contains(slotIndex) ||
                    m_AmbiguousPaths.Contains(normalizedPath))
                {
                    return;
                }

                if (m_IndexByPath.TryGetValue(normalizedPath, out int existing) && existing != slotIndex &&
                    existing >= 0 && existing < BindPoses.Count &&
                    !m_OverrideDerivedSlots.Contains(existing) &&
                    !BindPosesApproximatelyEqual(BindPoses[existing], bindPose, 1e-5f))
                {
                    m_AmbiguousPaths.Add(normalizedPath);
                    m_IndexByPath.Remove(normalizedPath);
                    return;
                }

                for (int i = 0; i < BonePaths.Count; i++)
                {
                    if (i != slotIndex &&
                        !m_OverrideDerivedSlots.Contains(i) &&
                        string.Equals(BonePaths[i], normalizedPath, StringComparison.Ordinal) &&
                        !BindPosesApproximatelyEqual(BindPoses[i], bindPose, 1e-5f))
                    {
                        m_AmbiguousPaths.Add(normalizedPath);
                        m_IndexByPath.Remove(normalizedPath);
                        return;
                    }
                }

                m_IndexByPath[normalizedPath] = slotIndex;
            }
        }
    }
}
