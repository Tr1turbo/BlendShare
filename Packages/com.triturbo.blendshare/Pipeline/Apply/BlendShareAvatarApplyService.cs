using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Container.Serialization;
using Triturbo.BlendShare.EditorAssets;
using Triturbo.BlendShare.EditorAssets.Mapping;
using Triturbo.BlendShare.EditorAssets.Settings;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Model.Documents;
using Triturbo.BlendShare.Model.Features;
using Triturbo.BlendShare.Model.Serialization;
using Triturbo.BlendShare.Model.Validation;
using Triturbo.BlendShare.Runtime.Assets;
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
        /// Incoming root bone transform (in NDMF Preview: <c>proxy.rootBone</c>; in Build: <c>targetRenderer.rootBone</c>).
        /// </summary>
        public Transform IncomingRootBone { get; set; }

        /// <summary>
        /// Avatar root transform used for bone hierarchy resolution.
        /// </summary>
        public Transform TargetRootTransform { get; set; }

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
        public static AvatarRendererApplyResult ApplyToRenderer(AvatarRendererApplyRequest request)
        {
            var diagnostics = new List<ApplyDiagnostic>();
            var observedRevisions = new Dictionary<string, long>(StringComparer.Ordinal);

            if (request == null || request.IncomingMesh == null)
            {
                diagnostics.Add(new ApplyDiagnostic(
                    ApplyDiagnosticSeverity.Error,
                    "AVATAR_APPLY_NO_INCOMING_MESH",
                    "Incoming Unity Mesh is required."));
                return new AvatarRendererApplyResult(false, null, diagnostics, null, observedRevisions);
            }

            if (request.Components == null || request.Components.Count == 0)
            {
                diagnostics.Add(new ApplyDiagnostic(
                    ApplyDiagnosticSeverity.Info,
                    "AVATAR_APPLY_NO_COMPONENTS",
                    "No BlendShareMesh components were provided."));
                return new AvatarRendererApplyResult(false, null, diagnostics, null, observedRevisions);
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
                    return new AvatarRendererApplyResult(false, null, diagnostics, null, observedRevisions);
                }

                var patch = comp.ResolvedPatch;
                string meshId = comp.ResolvedMeshId;
                if (patch == null)
                {
                    diagnostics.Add(new ApplyDiagnostic(
                        ApplyDiagnosticSeverity.Error,
                        "AVATAR_APPLY_MISSING_PATCH",
                        $"BlendShareMesh on '{comp.name}' has no BlendShareAsset assigned."));
                    return new AvatarRendererApplyResult(false, null, diagnostics, null, observedRevisions);
                }

                if (string.IsNullOrWhiteSpace(meshId))
                {
                    diagnostics.Add(new ApplyDiagnostic(
                        ApplyDiagnosticSeverity.Error,
                        "AVATAR_APPLY_MISSING_MESH_ID",
                        $"BlendShareMesh on '{comp.name}' has no MeshId assigned.",
                        patchId: patch.PatchId));
                    return new AvatarRendererApplyResult(false, null, diagnostics, null, observedRevisions);
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
                        return new AvatarRendererApplyResult(false, null, diagnostics, null, observedRevisions);
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

                var skeletonRes = repo.ReadSkeleton(patch);
                if (!skeletonRes.Success)
                {
                    diagnostics.Add(new ApplyDiagnostic(
                        ApplyDiagnosticSeverity.Error,
                        "AVATAR_APPLY_CONTAINER_READ_FAILED",
                        $"Failed reading skeleton for patch '{patch.name}': {skeletonRes.Message}",
                        patchId: patch.PatchId,
                        meshId: meshId));
                    return new AvatarRendererApplyResult(false, null, diagnostics, null, observedRevisions);
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
                    return new AvatarRendererApplyResult(false, null, diagnostics, null, observedRevisions);
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
                    return new AvatarRendererApplyResult(false, null, diagnostics, null, observedRevisions);
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
                            return new AvatarRendererApplyResult(false, null, diagnostics, null, observedRevisions);
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
                        return new AvatarRendererApplyResult(false, null, diagnostics, null, observedRevisions);
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
                            return new AvatarRendererApplyResult(false, null, diagnostics, null, observedRevisions);
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
                    return new AvatarRendererApplyResult(false, null, diagnostics, null, observedRevisions);
                }

                if (features.OfType<SkinWeightFeature>().Any() && !resolutions.TryGetValue(patch, out var resolution))
                {
                    resolution = BlendShareBoneProxyResolver.Resolve(patch, request.BoneProxies);
                    if (!resolution.Success)
                    {
                        diagnostics.Add(new ApplyDiagnostic(ApplyDiagnosticSeverity.Error,
                            "AVATAR_APPLY_INVALID_SKELETON", string.Join("\n", resolution.Diagnostics)));
                        return new AvatarRendererApplyResult(false, null, diagnostics, null, observedRevisions);
                    }
                    resolutions.Add(patch, resolution);
                    foreach (var proxy in resolution.SelectedProxies)
                    foreach (var binding in proxy.Bindings.Where(b => resolution.IsEffective(proxy, b)))
                    {
                        var parent = proxy.GetEffectiveParent(binding);
                        var root = request.TargetRootTransform;
                        if (root && (!binding.Transform.IsChildOf(root) || binding.Transform == root ||
                            (parent && (!parent.IsChildOf(root) || parent.IsChildOf(binding.Transform)))))
                        {
                            diagnostics.Add(new ApplyDiagnostic(ApplyDiagnosticSeverity.Error,
                                "AVATAR_APPLY_INVALID_PROXY_HIERARCHY", $"Invalid parent or bone on proxy '{proxy.name}'."));
                            return new AvatarRendererApplyResult(false, null, diagnostics, null, observedRevisions);
                        }
                        var key = new BoneOverrideKey(patchScopeKey,
                            FbxNameUtility.NormalizePath(binding.SourceBonePath));
                        boneOverrides[key] = binding.Transform;
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
                    Skeleton = skeletonRes.Value,
                    EffectiveSettings = effectiveSnapshot.Settings,
                    PayloadSource = repo.PayloadStore,
                    VertexMapping = mapRes.Mapping,
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
                return new AvatarRendererApplyResult(false, null, diagnostics, null, observedRevisions);
            }

            int effectiveMaxBones = request.MaxBonesPerVertex > 0
                ? request.MaxBonesPerVertex
                : BlendShareSkinWeightSettings.EffectiveMaxBonesPerVertex;

            var meshApplyRes = UnityMeshApplicationPipeline.Apply(new UnityMeshApplyRequest
            {
                BaseMesh = request.IncomingMesh,
                IncomingBones = request.IncomingBones,
                IncomingRootBone = request.IncomingRootBone,
                TargetRootTransform = request.TargetRootTransform,
                MeshRendererTransform = request.RendererTransform,
                BoneTransformOverrides = boneOverrides,
                MaxBonesPerVertex = effectiveMaxBones,
                CreateMissingBones = request.CreateMissingBones,
                PreserveExistingBlendShapes = request.PreserveExistingBlendShapes,
                Items = items,
                Registry = applierRegistry
            });

            if (meshApplyRes.Diagnostics != null)
            {
                diagnostics.AddRange(meshApplyRes.Diagnostics);
            }

            if (meshApplyRes.Succeeded && meshApplyRes.AppliedBoneBindings.Count > 0)
            {
                BlendShareSkinWeightInfluenceDiagnostics.Set(request.Components, meshApplyRes.SkinWeightInfluenceSummary);
            }

            var uses = new List<AvatarBoneBindingUse>();
            if (meshApplyRes.Succeeded && meshApplyRes.AppliedBoneBindings.Count > 0)
            {
                var records = meshApplyRes.AppliedBoneBindings;
                for (int i = 0; i < records.Count; i++)
                {
                    var record = records[i];
                    if (!patchByScopeKey.TryGetValue(record.PatchId, out var ownerPatch)) continue;
                    if (resolutions.TryGetValue(ownerPatch, out var resolution) &&
                        resolution.TryGetBinding(record.SourceBonePath, out var bone))
                    {
                        uses.Add(new AvatarBoneBindingUse(bone, record.BindPose, request.RendererTransform));
                    }
                }
            }

            var resolvedWeights = new List<KeyValuePair<string, float>>(weightOrder.Count);
            for (int i = 0; i < weightOrder.Count; i++)
            {
                string shapeName = weightOrder[i];
                resolvedWeights.Add(new KeyValuePair<string, float>(shapeName, weightOverrides[shapeName]));
            }

            return new AvatarRendererApplyResult(
                meshApplyRes.Succeeded,
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
