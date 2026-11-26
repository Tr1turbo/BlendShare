using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Container.Serialization;
using Triturbo.BlendShare.EditorAssets;
using Triturbo.BlendShare.EditorAssets.Mapping;
using Triturbo.BlendShare.EditorAssets.Settings;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Fbx.Ufbx;
using Triturbo.BlendShare.Fbx.Unity;
using Triturbo.BlendShare.Model.Documents;
using Triturbo.BlendShare.Model.Features;
using Triturbo.BlendShare.Model.Serialization;
using Triturbo.BlendShare.Model.Validation;
using Triturbo.BlendShare.Pipeline.Skeleton;
using Triturbo.BlendShare.Assets;
using Triturbo.BlendShare.Components;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.Pipeline.Apply
{
    public sealed class AvatarRendererApplyRequest
    {
        /// <summary>
        /// Incoming mesh to transform (in NDMF Preview this MUST be <c>proxy.sharedMesh</c>;
        /// in Build this is <c>targetRenderer.sharedMesh</c>). Never mutated in place.
        /// </summary>
        public Mesh IncomingMesh { get; set; }

        /// <summary>
        /// Incoming bone array (in NDMF Preview: <c>proxy.bones</c>; in Build: <c>targetRenderer.bones</c>).
        /// </summary>
        public Transform[] IncomingBones { get; set; }

        /// <summary>
        /// Optional avatar-relative bone paths corresponding 1:1 to <see cref="IncomingBones"/> when incoming slots
        /// hold external upstream replacement transforms outside <see cref="TargetRootTransform"/> (e.g., NDMF Preview).
        /// </summary>
        public string[] IncomingBoneSlotPaths { get; set; }

        /// <summary>
        /// Incoming root bone transform (in NDMF Preview: <c>proxy.rootBone</c>; in Build: <c>targetRenderer.rootBone</c>).
        /// </summary>
        public Transform IncomingRootBone { get; set; }

        /// <summary>
        /// Avatar root transform used for bone hierarchy resolution.
        /// </summary>
        public Transform TargetRootTransform { get; set; }

        /// <summary>
        /// Optional live authoring root transform used to validate <see cref="BlendShareBoneProxy"/> bindings when
        /// <see cref="TargetRootTransform"/> is a preview-owned shadow rig. Defaults to <see cref="TargetRootTransform"/>.
        /// </summary>
        public Transform AuthoringRootTransform { get; set; }

        /// <summary>
        /// Optional bone transform remapper applied to validated <see cref="BlendShareBoneProxy"/> target transforms
        /// (e.g., mapping live authoring transforms onto preview-owned shadow rig transforms).
        /// </summary>
        public Func<Transform, Transform> RemapAuthoringBoneTransform { get; set; }

        /// <summary>
        /// Target SkinnedMeshRenderer transform (used as fallback bind-pose frame when cluster matrices are absent).
        /// </summary>
        public Transform RendererTransform { get; set; }

        /// <summary>
        /// Ordered authoring <see cref="BlendShareMesh"/> components targeting this renderer.
        /// Later entries win same-name blendshape and weight collisions.
        /// </summary>
        public IReadOnlyList<BlendShareMesh> Components { get; set; } = Array.Empty<BlendShareMesh>();

        /// <summary>
        /// Active <see cref="BlendShareBoneProxy"/> components in avatar scope, matched strictly by
        /// owning <see cref="BlendShareAsset"/> identity.
        /// </summary>
        public IReadOnlyList<BlendShareBoneProxy> BoneProxies { get; set; } = Array.Empty<BlendShareBoneProxy>();

        /// <summary>
        /// Whether missing bones may be instantiated under <see cref="TargetRootTransform"/>.
        /// Must be <c>false</c> in Preview mode so live scenes are never mutated or dirtied.
        /// </summary>
        public bool CreateMissingBones { get; set; }

        /// <summary>
        /// Whether dirty <see cref="BlendShareSettingsSession"/> buffers for referenced patches
        /// should be flushed to <c>.blendshare.meta</c> before applying. Keep <c>false</c> in NDMF Build and Preview
        /// passes (never invoke <c>SaveAndReimport</c> mid-pass; <see cref="EffectiveSettingsResolver"/> already reads
        /// live in-memory buffers). When <c>true</c>, <see cref="SettingsCommitResultStatus.QueuedForEditMode"/> and
        /// <see cref="SettingsCommitResultStatus.ReadOnlySessionOnly"/> emit a warning and continue with in-memory settings.
        /// </summary>
        public bool FlushDirtySettings { get; set; }

        public VertexMappingCacheMode VertexMappingCacheMode { get; set; } = VertexMappingCacheMode.PersistAssetsOnly;
        public bool PreserveExistingBlendShapes { get; set; } = true;
        public int MaxBonesPerVertex { get; set; } = 0;

        /// <summary>Optional repository override (defaults to <see cref="BlendShareSourceRepository.Shared"/>).</summary>
        public BlendShareSourceRepository Repository { get; set; }

        /// <summary>Optional vertex mapping service override (defaults to <see cref="VertexMappingService.Shared"/>).</summary>
        public VertexMappingService VertexMappingServiceOverride { get; set; }

        /// <summary>Optional applier registry override (defaults to <see cref="FeatureUnityApplierRegistry.Default"/>).</summary>
        public FeatureUnityApplierRegistry ApplierRegistry { get; set; }

        /// <summary>Optional avatar-wide shared skeleton scope across all renderers on <see cref="TargetRootTransform"/>.</summary>
        public UnitySkeletonScope SharedSkeletonScope { get; set; }
    }

    /// <summary>A proxy binding that actually supplied a bone slot on the generated renderer.</summary>
    public readonly struct AvatarBoneBindingUse
    {
        public BlendShareResolvedBone Bone { get; }
        public Matrix4x4 BindPose { get; }
        public Transform Renderer { get; }

        internal AvatarBoneBindingUse(BlendShareResolvedBone bone, Matrix4x4 bindPose, Transform renderer)
        {
            Bone = bone;
            BindPose = bindPose;
            Renderer = renderer;
        }
    }

    public sealed class AvatarRendererApplyResult
    {
        public IReadOnlyList<AvatarBoneBindingUse> BoneBindingUses { get; }
        public bool Succeeded { get; }
        public UnityMeshApplyResult MeshResult { get; }
        public IReadOnlyList<ApplyDiagnostic> Diagnostics { get; }
        public IReadOnlyList<KeyValuePair<string, float>> ResolvedBlendShapeWeights { get; }
        public IReadOnlyDictionary<string, long> ObservedSettingsRevisions { get; }

        public AvatarRendererApplyResult(
            bool succeeded,
            UnityMeshApplyResult meshResult,
            IReadOnlyList<ApplyDiagnostic> diagnostics,
            IReadOnlyList<KeyValuePair<string, float>> resolvedBlendShapeWeights,
            IReadOnlyDictionary<string, long> observedSettingsRevisions,
            IReadOnlyList<AvatarBoneBindingUse> boneBindingUses = null)
        {
            BoneBindingUses = boneBindingUses ?? Array.Empty<AvatarBoneBindingUse>();
            Succeeded = succeeded;
            MeshResult = meshResult;
            Diagnostics = diagnostics ?? Array.Empty<ApplyDiagnostic>();
            ResolvedBlendShapeWeights = resolvedBlendShapeWeights ?? Array.Empty<KeyValuePair<string, float>>();
            ObservedSettingsRevisions = observedSettingsRevisions ?? new Dictionary<string, long>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Single shared avatar mesh application gateway consumed by BOTH NDMF Build Pass and NDMF Preview Adapter.
    /// Guarantees Build/Preview parity by resolving incoming <c>sharedMesh</c>/bones, reading effective settings
    /// from <see cref="EffectiveSettingsResolver"/>, resolving vertices through <see cref="VertexMappingService"/>,
    /// scoping bone proxy overrides strictly by <see cref="BlendShareAsset"/> identity, and delegating mesh synthesis
    /// to <see cref="UnityMeshApplicationPipeline"/>.
    /// </summary>
    public static class BlendShareAvatarApplyService
    {
        private sealed class PreparedRendererPlan
        {
            public AvatarRendererApplyRequest Request;
            public UnityMeshApplyRequest MeshRequest;
            public List<ApplyDiagnostic> Diagnostics;
            public Dictionary<string, long> ObservedRevisions;
            public List<string> WeightOrder;
            public Dictionary<string, float> WeightOverrides;
            public Dictionary<string, BlendShareAsset> PatchByScopeKey;
            public Dictionary<BlendShareAsset, BlendShareBoneProxyResolution> Resolutions;
            public AvatarRendererApplyResult ImmediateFailure;
        }

        /// <summary>
        /// Applies BlendShare patches across all targeted renderers on <paramref name="targetRoot"/> using a single
        /// avatar-wide <see cref="UnitySkeletonScope"/> so pre-edit rest frames and ancestor bone compensation
        /// span across renderer boundaries regardless of renderer visit order.
        /// </summary>
        public static IReadOnlyList<AvatarRendererApplyResult> ApplyToAvatarRenderers(
            Transform targetRoot,
            IReadOnlyList<AvatarRendererApplyRequest> requests)
        {
            if (requests == null || requests.Count == 0)
            {
                return Array.Empty<AvatarRendererApplyResult>();
            }

            var openedRawTargetScenes = new Dictionary<GameObject, IFbxScene>();
            try
            {
                var plans = new PreparedRendererPlan[requests.Count];
                var validMeshRequests = new List<UnityMeshApplyRequest>(requests.Count);
                Transform scanHierarchyRoot = null;

                for (int i = 0; i < requests.Count; i++)
                {
                    if (scanHierarchyRoot == null && requests[i]?.AuthoringRootTransform != null)
                    {
                        scanHierarchyRoot = requests[i].AuthoringRootTransform;
                    }

                    var plan = PrepareRendererPlan(requests[i], openedRawTargetScenes);
                    plans[i] = plan;
                    if (plan.MeshRequest != null)
                    {
                        validMeshRequests.Add(plan.MeshRequest);
                    }
                }

                UnitySkeletonScope sharedScope = null;
                if (validMeshRequests.Count > 0)
                {
                    try
                    {
                        // Never mutate scene transforms before all renderer mesh applications validate cleanly.
                        sharedScope = UnitySkeletonScope.BuildForRequests(
                            targetRoot,
                            validMeshRequests,
                            scanHierarchyRoot: scanHierarchyRoot);
                    }
                    catch (Exception ex)
                    {
                        var failResults = new AvatarRendererApplyResult[requests.Count];
                        for (int i = 0; i < requests.Count; i++)
                        {
                            if (plans[i].ImmediateFailure != null)
                            {
                                failResults[i] = plans[i].ImmediateFailure;
                            }
                            else
                            {
                                plans[i].Diagnostics.Add(new ApplyDiagnostic(
                                    ApplyDiagnosticSeverity.Error,
                                    "SKIN_APPLY_FAILED",
                                    ex.Message));
                                failResults[i] = new AvatarRendererApplyResult(
                                    false,
                                    null,
                                    plans[i].Diagnostics,
                                    null,
                                    plans[i].ObservedRevisions);
                            }
                        }

                        return failResults;
                    }
                }

                var results = new AvatarRendererApplyResult[requests.Count];
                bool anyFailed = false;
                for (int i = 0; i < requests.Count; i++)
                {
                    var plan = plans[i];
                    if (plan.ImmediateFailure != null)
                    {
                        results[i] = plan.ImmediateFailure;
                        anyFailed = true;
                        continue;
                    }

                    plan.MeshRequest.SharedSkeletonScope = sharedScope;
                    results[i] = ExecutePreparedPlan(plan, publishInfluenceDiagnostics: false);
                    if (!results[i].Succeeded)
                    {
                        anyFailed = true;
                    }
                }

                // Make the avatar batch atomic: if any renderer failed, destroy all created bone roots AND all
                // transient output meshes, and mark previously-succeeded sibling results as rolled back.
                if (anyFailed)
                {
                    string failureDetail = results
                        .Where(r => r != null && !r.Succeeded)
                        .SelectMany(r => r.Diagnostics)
                        .Where(d => d.Severity == ApplyDiagnosticSeverity.Error)
                        .Select(d => d.Message)
                        .FirstOrDefault()
                        ?? "A sibling avatar renderer failed during coupled avatar application.";

                    for (int i = results.Length - 1; i >= 0; i--)
                    {
                        var createdRoots = results[i]?.MeshResult?.CreatedBoneRoots;
                        if (createdRoots != null)
                        {
                            for (int c = createdRoots.Count - 1; c >= 0; c--)
                            {
                                if (createdRoots[c] != null)
                                {
                                    UnityEngine.Object.DestroyImmediate(createdRoots[c].gameObject);
                                }
                            }
                        }

                        var outMesh = results[i]?.MeshResult?.OutputMesh;
                        if (outMesh != null && !AssetDatabase.Contains(outMesh))
                        {
                            UnityEngine.Object.DestroyImmediate(outMesh);
                        }

                        if (results[i] != null && results[i].Succeeded)
                        {
                            var rolledBackDiags = new List<ApplyDiagnostic>(results[i].Diagnostics)
                            {
                                new ApplyDiagnostic(
                                    ApplyDiagnosticSeverity.Error,
                                    "AVATAR_APPLY_BATCH_ROLLED_BACK",
                                    $"Rolled back avatar renderer output because another renderer in the coupled avatar batch failed: {failureDetail}")
                            };
                            results[i] = new AvatarRendererApplyResult(
                                false,
                                null,
                                rolledBackDiags,
                                null,
                                results[i].ObservedSettingsRevisions);
                        }
                    }

                    return results;
                }

                for (int i = 0; i < results.Length; i++)
                {
                    var meshRes = results[i]?.MeshResult;
                    if (meshRes != null && meshRes.AppliedBoneBindings.Count > 0 && plans[i]?.Request != null)
                    {
                        BlendShareSkinWeightInfluenceDiagnostics.Set(plans[i].Request.Components, meshRes.SkinWeightInfluenceSummary);
                    }
                }

                return results;
            }
            finally
            {
                foreach (var sc in openedRawTargetScenes.Values)
                {
                    sc?.Dispose();
                }
            }
        }

        public static AvatarRendererApplyResult ApplyToRenderer(AvatarRendererApplyRequest request)
        {
            var openedRawTargetScenes = new Dictionary<GameObject, IFbxScene>();
            try
            {
                var plan = PrepareRendererPlan(request, openedRawTargetScenes);
                if (plan.ImmediateFailure != null)
                {
                    return plan.ImmediateFailure;
                }

                return ExecutePreparedPlan(plan);
            }
            finally
            {
                foreach (var sc in openedRawTargetScenes.Values)
                {
                    sc?.Dispose();
                }
            }
        }

        private static PreparedRendererPlan PrepareRendererPlan(
            AvatarRendererApplyRequest request,
            Dictionary<GameObject, IFbxScene> openedRawTargetScenes)
        {
            var diagnostics = new List<ApplyDiagnostic>();
            var observedRevisions = new Dictionary<string, long>(StringComparer.Ordinal);
            var plan = new PreparedRendererPlan
            {
                Request = request,
                Diagnostics = diagnostics,
                ObservedRevisions = observedRevisions
            };

            if (request == null || request.IncomingMesh == null)
            {
                diagnostics.Add(new ApplyDiagnostic(
                    ApplyDiagnosticSeverity.Error,
                    "AVATAR_APPLY_NO_INCOMING_MESH",
                    "Incoming Unity Mesh is required."));
                plan.ImmediateFailure = new AvatarRendererApplyResult(false, null, diagnostics, null, observedRevisions);
                return plan;
            }

            if (request.Components == null || request.Components.Count == 0)
            {
                diagnostics.Add(new ApplyDiagnostic(
                    ApplyDiagnosticSeverity.Info,
                    "AVATAR_APPLY_NO_COMPONENTS",
                    "No BlendShareMesh components were provided."));
                plan.ImmediateFailure = new AvatarRendererApplyResult(false, null, diagnostics, null, observedRevisions);
                return plan;
            }

            var repo = request.Repository ?? BlendShareSourceRepository.Shared;
            var mappingService = request.VertexMappingServiceOverride ?? VertexMappingService.Shared;
            var applierRegistry = request.ApplierRegistry ?? FeatureUnityApplierRegistry.Default;

            var items = new List<PatchMeshApplicationItem>(request.Components.Count);
            var boneOverrides = new Dictionary<BoneOverrideKey, Transform>();
            var weightOverrides = new Dictionary<string, float>(StringComparer.Ordinal);
            var weightOrder = new List<string>();
            var committedGuids = new HashSet<string>(StringComparer.Ordinal);
            var resolutions = new Dictionary<BlendShareAsset, BlendShareBoneProxyResolution>();
            var patchByScopeKey = new Dictionary<string, BlendShareAsset>(StringComparer.Ordinal);
            var skeletonByPatch = new Dictionary<BlendShareAsset, SkeletonDocument>();

            void Fail()
            {
                plan.ImmediateFailure = new AvatarRendererApplyResult(false, null, diagnostics, null, observedRevisions);
            }

            for (int c = 0; c < request.Components.Count; c++)
            {
                var comp = request.Components[c];
                if (comp == null)
                {
                    continue;
                }

                // Reject mismatched Patch vs MeshAsset.Root to avoid silently applying the wrong patch.
                if (comp.Patch != null && comp.MeshAsset != null && comp.MeshAsset.Root != null &&
                    !ReferenceEquals(comp.Patch, comp.MeshAsset.Root))
                {
                    diagnostics.Add(new ApplyDiagnostic(
                        ApplyDiagnosticSeverity.Error,
                        "AVATAR_APPLY_PATCH_MESH_MISMATCH",
                        $"BlendShareMesh on '{comp.name}' references Patch '{comp.Patch.name}' but MeshAsset '{comp.MeshAsset.name}' belongs to '{comp.MeshAsset.Root.name}'.",
                        patchId: comp.Patch.PatchId,
                        meshId: comp.ResolvedMeshId));
                    Fail();
                    return plan;
                }

                var patch = comp.ResolvedPatch;
                string meshId = comp.ResolvedMeshId;
                if (patch == null)
                {
                    diagnostics.Add(new ApplyDiagnostic(
                        ApplyDiagnosticSeverity.Error,
                        "AVATAR_APPLY_MISSING_PATCH",
                        $"BlendShareMesh on '{comp.name}' has no BlendShareAsset assigned."));
                    Fail();
                    return plan;
                }

                if (string.IsNullOrWhiteSpace(meshId))
                {
                    diagnostics.Add(new ApplyDiagnostic(
                        ApplyDiagnosticSeverity.Error,
                        "AVATAR_APPLY_MISSING_MESH_ID",
                        $"BlendShareMesh on '{comp.name}' has no MeshId assigned.",
                        patchId: patch.PatchId));
                    Fail();
                    return plan;
                }

                string assetPath = AssetDatabase.GetAssetPath(patch);
                string assetGuid = !string.IsNullOrEmpty(assetPath) ? AssetDatabase.AssetPathToGUID(assetPath) : string.Empty;

                // When explicitly requested by direct EditMode workflows, flush dirty Settings buffers before applying.
                // Never abort on PlayMode-deferred or read-only session-only buffers since EffectiveSettingsResolver
                // already returns the live in-memory settings snapshot.
                var preSnapshot = EffectiveSettingsResolver.Resolve(patch);
                bool flushedToDisk = false;
                if (request.FlushDirtySettings && preSnapshot.IsDirty && !string.IsNullOrEmpty(assetGuid) && committedGuids.Add(assetGuid))
                {
                    var commitRes = BlendShareSettingsSession.Flush(assetGuid);
                    if (commitRes.Success)
                    {
                        flushedToDisk = true;
                    }
                    else if (commitRes.Status == SettingsCommitResultStatus.QueuedForEditMode ||
                             commitRes.Status == SettingsCommitResultStatus.ReadOnlySessionOnly)
                    {
                        diagnostics.Add(new ApplyDiagnostic(
                            ApplyDiagnosticSeverity.Warning,
                            "AVATAR_APPLY_SETTINGS_DEFERRED",
                            $"Using in-memory settings for patch '{patch.name}' ({commitRes.Status}): {commitRes.Message}",
                            patchId: patch.PatchId,
                            meshId: meshId));
                    }
                    else
                    {
                        diagnostics.Add(new ApplyDiagnostic(
                            ApplyDiagnosticSeverity.Error,
                            "AVATAR_APPLY_SETTINGS_COMMIT_FAILED",
                            $"Failed committing dirty settings for patch '{patch.name}' ({commitRes.Status}): {commitRes.Message}",
                            patchId: patch.PatchId,
                            meshId: meshId));
                        Fail();
                        return plan;
                    }
                }

                var effectiveSnapshot = flushedToDisk
                    ? EffectiveSettingsResolver.Resolve(patch)
                    : preSnapshot;
                string revKey = !string.IsNullOrEmpty(assetGuid) ? assetGuid : patch.GetInstanceID().ToString();
                observedRevisions[revKey] = effectiveSnapshot.SettingsRevision;

                // Scope PatchId uniquely by BlendShareAsset identity so two distinct BlendShareAssets
                // that happen to share identical PatchId strings never collide!
                string patchScopeKey = BuildPatchScopeKey(patch, assetGuid);
                patchByScopeKey[patchScopeKey] = patch;

                if (!skeletonByPatch.TryGetValue(patch, out var patchSkeleton))
                {
                    var skelRes = repo.ReadSkeleton(patch);
                    if (!skelRes.Success)
                    {
                        diagnostics.Add(new ApplyDiagnostic(
                            ApplyDiagnosticSeverity.Error,
                            skelRes.Status == Container.Format.ContainerReadStatus.UnsupportedVersion
                                ? "AVATAR_APPLY_UNSUPPORTED_SKELETON_VERSION"
                                : "AVATAR_APPLY_CONTAINER_READ_FAILED",
                            $"Failed reading skeleton for patch '{patch.name}': {skelRes.Message}",
                            patchId: patch.PatchId,
                            meshId: meshId,
                            featureTypeId: SkeletonDocument.SectionTypeId));
                        Fail();
                        return plan;
                    }

                    patchSkeleton = skelRes.Value;
                    if (patchSkeleton != null)
                    {
                        var skelVal = SkeletonDocumentValidator.Validate(patchSkeleton);
                        if (!skelVal.IsValid)
                        {
                            string msg = string.Join("; ", skelVal.Issues.Select(i => i.Message));
                            diagnostics.Add(new ApplyDiagnostic(
                                ApplyDiagnosticSeverity.Error,
                                "AVATAR_APPLY_SKELETON_VALIDATION_FAILED",
                                msg,
                                patchId: patch.PatchId,
                                meshId: meshId,
                                featureTypeId: SkeletonDocument.SectionTypeId));
                            Fail();
                            return plan;
                        }
                    }

                    skeletonByPatch.Add(patch, patchSkeleton);
                }

                var meshHeaderRes = repo.ReadMeshHeader(patch, meshId);
                if (!meshHeaderRes.Success || meshHeaderRes.Value == null)
                {
                    diagnostics.Add(new ApplyDiagnostic(
                        ApplyDiagnosticSeverity.Error,
                        "AVATAR_APPLY_MESH_HEADER_READ_FAILED",
                        $"Failed reading mesh header '{meshId}' from patch '{patch.name}': {meshHeaderRes.Message}",
                        patchId: patch.PatchId,
                        meshId: meshId));
                    Fail();
                    return plan;
                }

                MeshDocument meshHeader = meshHeaderRes.Value;
                var toc = BlendShareAssetTocMapper.ToContainerToc(patch);
                if (toc == null || !toc.TryGetMesh(meshId, out var meshToc))
                {
                    diagnostics.Add(new ApplyDiagnostic(
                        ApplyDiagnosticSeverity.Error,
                        "AVATAR_APPLY_TOC_MESH_MISSING",
                        $"Mesh '{meshId}' not found in TOC of patch '{patch.name}'.",
                        patchId: patch.PatchId,
                        meshId: meshId));
                    Fail();
                    return plan;
                }

                var features = new List<BlendShareFeatureData>(meshToc.Features.Count);
                for (int f = 0; f < meshToc.Features.Count; f++)
                {
                    var featToc = meshToc.Features[f];
                    var entry = featToc.Entry;
                    string typeId = entry.TypeId;

                    BlendShareFeatureAttribute featureAttr = null;
                    bool hasSchema = FeatureSchemaRegistry.Default.TryGetFeatureType(typeId, out var featureClrType) &&
                                     FeatureSchemaRegistry.Default.TryGetFeatureAttribute(featureClrType, out featureAttr);
                    bool versionSupported = hasSchema && entry.SchemaVersion <= featureAttr.SchemaVersion;
                    bool hasApplier = applierRegistry.TryGet(typeId, out _);

                    if (!hasSchema || !versionSupported || !hasApplier)
                    {
                        if (entry.Required)
                        {
                            diagnostics.Add(new ApplyDiagnostic(
                                ApplyDiagnosticSeverity.Error,
                                "AVATAR_APPLY_UNSUPPORTED_REQUIRED_FEATURE",
                                $"Required feature '{featToc.FeatureId}' (type '{typeId}', schema v{entry.SchemaVersion}) is not supported.",
                                patchId: patch.PatchId,
                                meshId: meshId,
                                featureTypeId: typeId));
                            Fail();
                            return plan;
                        }

                        diagnostics.Add(new ApplyDiagnostic(
                            ApplyDiagnosticSeverity.Warning,
                            "AVATAR_APPLY_UNSUPPORTED_OPTIONAL_FEATURE",
                            $"Skipping optional unsupported feature '{featToc.FeatureId}' (type '{typeId}', schema v{entry.SchemaVersion}).",
                            patchId: patch.PatchId,
                            meshId: meshId,
                            featureTypeId: typeId));
                        continue;
                    }

                    var featRes = repo.ReadFeature(patch, meshId, featToc.FeatureId);
                    if (!featRes.Success || featRes.Value == null)
                    {
                        diagnostics.Add(new ApplyDiagnostic(
                            ApplyDiagnosticSeverity.Error,
                            "AVATAR_APPLY_FEATURE_READ_FAILED",
                            $"Failed reading feature '{featToc.FeatureId}' on mesh '{meshId}': {featRes.Message}",
                            patchId: patch.PatchId,
                            meshId: meshId,
                            featureTypeId: typeId));
                        Fail();
                        return plan;
                    }

                    if (FeatureValidatorRegistry.Default.TryGetValidator(typeId, out var validator))
                    {
                        int cpCount = meshHeader.Topology != null && meshHeader.Topology.IsValid
                            ? meshHeader.Topology.ControlPointCount
                            : -1;
                        var validationContext = new FeatureValidationContext(
                            meshId,
                            cpCount,
                            -1,
                            repo.PayloadStore,
                            validatePayloads: false);
                        var validationResult = new FeatureValidationResult();
                        validator.Validate(featRes.Value, validationContext, validationResult);
                        if (!validationResult.IsValid)
                        {
                            string msg = string.Join("; ", validationResult.Issues.Select(i => i.Message));
                            diagnostics.Add(new ApplyDiagnostic(
                                ApplyDiagnosticSeverity.Error,
                                "AVATAR_APPLY_FEATURE_VALIDATION_FAILED",
                                msg,
                                patchId: patch.PatchId,
                                meshId: meshId,
                                featureTypeId: typeId));
                            Fail();
                            return plan;
                        }
                    }

                    features.Add(featRes.Value);
                }

                // Resolve vertex mapping against the INCOMING mesh (supports embedded mappings, FBX inverse map, and ReferenceMesh).
                GameObject targetFbx = comp.TargetFbxOverride != null
                    ? comp.TargetFbxOverride
                    : effectiveSnapshot.Settings?.DefaultTargetFbx;

                var mapRes = mappingService.Resolve(new VertexMappingResolveRequest
                {
                    MeshHeader = meshHeader,
                    SpaceProvenance = toc.SpaceProvenance,
                    TargetUnityMesh = request.IncomingMesh,
                    ReferenceUnityMesh = comp.ReferenceMesh,
                    TargetFbxAsset = targetFbx,
                    PayloadSource = repo.PayloadStore,
                    CacheMode = request.VertexMappingCacheMode
                });

                if (!mapRes.Succeeded)
                {
                    diagnostics.Add(new ApplyDiagnostic(
                        ApplyDiagnosticSeverity.Error,
                        string.IsNullOrEmpty(mapRes.FailureCode) ? "AVATAR_APPLY_MAPPING_FAILED" : mapRes.FailureCode,
                        $"Failed mapping patch '{patch.name}' mesh '{meshId}': {mapRes.Diagnostic}",
                        patchId: patch.PatchId,
                        meshId: meshId));
                    Fail();
                    return plan;
                }

                VerifiedRawTargetSkinContext verifiedRawTargetSkin = null;
                if (targetFbx != null && features.OfType<SkinWeightFeature>().Any())
                {
                    if (!openedRawTargetScenes.TryGetValue(targetFbx, out var rawScene))
                    {
                        var rawSceneRes = FbxUnityAssetReader.ReadScene(targetFbx, UfbxLoadOptions.Default);
                        rawScene = rawSceneRes.Success ? rawSceneRes.Value : null;
                        openedRawTargetScenes[targetFbx] = rawScene;
                    }

                    verifiedRawTargetSkin = VerifiedRawTargetSkinResolver.TryResolve(
                        request.IncomingMesh,
                        targetFbx,
                        rawScene,
                        meshHeader,
                        mapRes.Mapping);
                }

                if (features.OfType<SkinWeightFeature>().Any() && !resolutions.TryGetValue(patch, out var resolution))
                {
                    resolution = BlendShareBoneProxyResolver.Resolve(patch, request.BoneProxies);
                    if (!resolution.Success)
                    {
                        diagnostics.Add(new ApplyDiagnostic(ApplyDiagnosticSeverity.Error,
                            "AVATAR_APPLY_INVALID_SKELETON", string.Join("\n", resolution.Diagnostics)));
                        Fail();
                        return plan;
                    }
                    resolutions.Add(patch, resolution);
                    foreach (var proxy in resolution.SelectedProxies)
                    foreach (var binding in proxy.Bindings.Where(b => resolution.IsEffective(proxy, b)))
                    {
                        var parent = proxy.GetEffectiveParent(binding);
                        var hierarchyRoot = request.AuthoringRootTransform != null
                            ? request.AuthoringRootTransform
                            : request.TargetRootTransform;
                        if (hierarchyRoot && (!binding.Transform.IsChildOf(hierarchyRoot) || binding.Transform == hierarchyRoot ||
                            (parent && (!parent.IsChildOf(hierarchyRoot) || parent.IsChildOf(binding.Transform)))))
                        {
                            diagnostics.Add(new ApplyDiagnostic(ApplyDiagnosticSeverity.Error,
                                "AVATAR_APPLY_INVALID_PROXY_HIERARCHY", $"Invalid parent or bone on proxy '{proxy.name}'."));
                            Fail();
                            return plan;
                        }
                        var key = new BoneOverrideKey(patchScopeKey,
                            FbxNameUtility.NormalizePath(binding.SourceBonePath));
                        var mappedTransform = request.RemapAuthoringBoneTransform != null
                            ? request.RemapAuthoringBoneTransform(binding.Transform)
                            : binding.Transform;
                        boneOverrides[key] = mappedTransform;
                    }
                }

                Dictionary<string, Matrix4x4> itemBindPoseOverrides = null;
                var compBindPoseOverrides = comp.BindPoseOverrides;
                if (compBindPoseOverrides != null && compBindPoseOverrides.Count > 0)
                {
                    itemBindPoseOverrides = new Dictionary<string, Matrix4x4>(StringComparer.Ordinal);
                    for (int b = 0; b < compBindPoseOverrides.Count; b++)
                    {
                        var ov = compBindPoseOverrides[b];
                        if (ov == null || string.IsNullOrWhiteSpace(ov.SourceBonePath)) continue;
                        string norm = FbxNameUtility.NormalizePath(ov.SourceBonePath);
                        if (!string.IsNullOrEmpty(norm))
                        {
                            itemBindPoseOverrides[norm] = ov.BindPose;
                        }
                    }
                }

                // Collect blendshape weight overrides (later component wins collisions).
                var compWeights = comp.BlendShapeWeights;
                for (int w = 0; w < compWeights.Count; w++)
                {
                    var entry = compWeights[w];
                    if (entry == null || string.IsNullOrWhiteSpace(entry.ShapeName))
                    {
                        continue;
                    }

                    if (!weightOverrides.ContainsKey(entry.ShapeName))
                    {
                        weightOrder.Add(entry.ShapeName);
                    }

                    weightOverrides[entry.ShapeName] = entry.Weight;
                }

                items.Add(new PatchMeshApplicationItem
                {
                    PatchId = patchScopeKey,
                    MeshId = meshId,
                    SpaceProvenance = toc.SpaceProvenance,
                    MeshHeader = meshHeader,
                    Skeleton = patchSkeleton,
                    EffectiveSettings = effectiveSnapshot.Settings,
                    PayloadSource = repo.PayloadStore,
                    VertexMapping = mapRes.Mapping,
                    VerifiedRawTargetSkin = verifiedRawTargetSkin,
                    CreateMissingBones = request.CreateMissingBones && comp.CreateMissingBones,
                    BindPoseOverrides = itemBindPoseOverrides,
                    Features = features
                });
            }

            if (items.Count == 0)
            {
                diagnostics.Add(new ApplyDiagnostic(
                    ApplyDiagnosticSeverity.Info,
                    "AVATAR_APPLY_EMPTY_ITEMS",
                    "No valid patch items to apply."));
                Fail();
                return plan;
            }

            int effectiveMaxBones = request.MaxBonesPerVertex > 0
                ? request.MaxBonesPerVertex
                : BlendShareSkinWeightSettings.EffectiveMaxBonesPerVertex;

            plan.MeshRequest = new UnityMeshApplyRequest
            {
                BaseMesh = request.IncomingMesh,
                IncomingBones = request.IncomingBones,
                IncomingBoneSlotPaths = request.IncomingBoneSlotPaths,
                IncomingRootBone = request.IncomingRootBone,
                TargetRootTransform = request.TargetRootTransform,
                MeshRendererTransform = request.RendererTransform,
                SharedSkeletonScope = request.SharedSkeletonScope,
                BoneTransformOverrides = boneOverrides,
                MaxBonesPerVertex = effectiveMaxBones,
                CreateMissingBones = request.CreateMissingBones,
                PreserveExistingBlendShapes = request.PreserveExistingBlendShapes,
                Items = items,
                Registry = applierRegistry
            };
            plan.WeightOrder = weightOrder;
            plan.WeightOverrides = weightOverrides;
            plan.PatchByScopeKey = patchByScopeKey;
            plan.Resolutions = resolutions;
            return plan;
        }

        private static AvatarRendererApplyResult ExecutePreparedPlan(
            PreparedRendererPlan plan,
            bool publishInfluenceDiagnostics = true)
        {
            var request = plan.Request;
            var diagnostics = plan.Diagnostics;
            var observedRevisions = plan.ObservedRevisions;
            var meshApplyRes = UnityMeshApplicationPipeline.Apply(plan.MeshRequest);

            if (meshApplyRes.Diagnostics != null)
            {
                diagnostics.AddRange(meshApplyRes.Diagnostics);
            }

            if (!meshApplyRes.Succeeded)
            {
                if (meshApplyRes.OutputMesh != null && !AssetDatabase.Contains(meshApplyRes.OutputMesh))
                {
                    UnityEngine.Object.DestroyImmediate(meshApplyRes.OutputMesh);
                }

                return new AvatarRendererApplyResult(
                    false,
                    null,
                    diagnostics,
                    null,
                    observedRevisions);
            }

            if (publishInfluenceDiagnostics && meshApplyRes.AppliedBoneBindings.Count > 0)
            {
                BlendShareSkinWeightInfluenceDiagnostics.Set(request.Components, meshApplyRes.SkinWeightInfluenceSummary);
            }

            var uses = new List<AvatarBoneBindingUse>();
            if (meshApplyRes.AppliedBoneBindings.Count > 0)
            {
                var records = meshApplyRes.AppliedBoneBindings;
                for (int i = 0; i < records.Count; i++)
                {
                    var record = records[i];
                    if (!plan.PatchByScopeKey.TryGetValue(record.PatchId, out var ownerPatch)) continue;
                    if (plan.Resolutions.TryGetValue(ownerPatch, out var resolution) &&
                        resolution.TryGetBinding(record.SourceBonePath, out var bone))
                    {
                        uses.Add(new AvatarBoneBindingUse(bone, record.BindPose, request.RendererTransform));
                    }
                }
            }

            var resolvedWeights = new List<KeyValuePair<string, float>>(plan.WeightOrder.Count);
            for (int i = 0; i < plan.WeightOrder.Count; i++)
            {
                string shapeName = plan.WeightOrder[i];
                resolvedWeights.Add(new KeyValuePair<string, float>(shapeName, plan.WeightOverrides[shapeName]));
            }

            return new AvatarRendererApplyResult(
                true,
                meshApplyRes,
                diagnostics,
                resolvedWeights,
                observedRevisions,
                uses);
        }

        /// <summary>
        /// Computes a unique, collision-free patch scope key for a <see cref="BlendShareAsset"/>
        /// (including subasset local file ID) so distinct assets with identical <c>PatchId</c> strings never collide.
        /// </summary>
        public static string BuildPatchScopeKey(BlendShareAsset patch, string assetGuid = null)
        {
            if (patch == null)
            {
                return string.Empty;
            }

            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(patch, out string guid, out long localId) &&
                !string.IsNullOrEmpty(guid))
            {
                return $"guid:{guid}:{localId}";
            }

            if (!string.IsNullOrEmpty(assetGuid))
            {
                return $"guid:{assetGuid}:{patch.GetInstanceID()}";
            }

            return $"inst:{patch.GetInstanceID()}";
        }
    }
}
