using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Container.Serialization;
using Triturbo.BlendShare.EditorAssets;
using Triturbo.BlendShare.EditorAssets.Binding;
using Triturbo.BlendShare.EditorAssets.Settings;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Model.Documents;
using Triturbo.BlendShare.Model.Features;
using Triturbo.BlendShare.Model.Serialization;
using Triturbo.BlendShare.Model.Validation;
using Triturbo.BlendShare.Runtime.Assets;
using Triturbo.BlendShare.Runtime.Components;
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
        /// Avatar root transform used for bone hierarchy resolution.
        /// </summary>
        public Transform TargetRootTransform { get; set; }

        /// <summary>
        /// Target SkinnedMeshRenderer transform (used as fallback bind-pose frame when cluster matrices are absent).
        /// </summary>
        public Transform RendererTransform { get; set; }

        /// <summary>
        /// Ordered authoring <see cref="BlendSharePatchMesh"/> components targeting this renderer.
        /// Later entries win same-name blendshape and weight collisions.
        /// </summary>
        public IReadOnlyList<BlendSharePatchMesh> Components { get; set; } = Array.Empty<BlendSharePatchMesh>();

        /// <summary>
        /// Active <see cref="BlendSharePatchBoneProxy"/> components in avatar scope, matched strictly by
        /// <c>(BlendShareAsset, ArmatureId)</c> identity.
        /// </summary>
        public IReadOnlyList<BlendSharePatchBoneProxy> BoneProxies { get; set; } = Array.Empty<BlendSharePatchBoneProxy>();

        /// <summary>
        /// Whether missing bones may be instantiated under <see cref="TargetRootTransform"/>.
        /// Must be <c>false</c> in Preview mode so live scenes are never mutated or dirtied.
        /// </summary>
        public bool CreateMissingBones { get; set; }

        /// <summary>
        /// Whether dirty <see cref="BlendShareSettingsSession"/> buffers for referenced patches
        /// should be flushed to <c>.blendshare.meta</c> before applying (true at Build boundary, false in Preview).
        /// </summary>
        public bool FlushDirtySettings { get; set; }

        public bool AllowDiskBindingCache { get; set; } = true;
        public bool PreserveExistingBlendShapes { get; set; } = true;
        public int MaxBonesPerVertex { get; set; } = 4;

        /// <summary>Optional repository override (defaults to <see cref="BlendShareSourceRepository.Shared"/>).</summary>
        public BlendShareSourceRepository Repository { get; set; }

        /// <summary>Optional binding service override (defaults to <see cref="BindingService.Shared"/>).</summary>
        public BindingService BindingServiceOverride { get; set; }

        /// <summary>Optional applier registry override (defaults to <see cref="FeatureUnityApplierRegistry.Default"/>).</summary>
        public FeatureUnityApplierRegistry ApplierRegistry { get; set; }
    }

    public sealed class AvatarRendererApplyResult
    {
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
            IReadOnlyDictionary<string, long> observedSettingsRevisions)
        {
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
    /// from <see cref="EffectiveSettingsResolver"/>, resolving vertices through <see cref="BindingService"/>,
    /// scoping bone proxy overrides strictly by <c>(BlendShareAsset, ArmatureId)</c>, and delegating mesh synthesis
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
                    "No BlendSharePatchMesh components were provided."));
                return new AvatarRendererApplyResult(false, null, diagnostics, null, observedRevisions);
            }

            var repo = request.Repository ?? BlendShareSourceRepository.Shared;
            var bindingService = request.BindingServiceOverride ?? BindingService.Shared;
            var applierRegistry = request.ApplierRegistry ?? FeatureUnityApplierRegistry.Default;

            var items = new List<PatchMeshApplicationItem>(request.Components.Count);
            var boneOverrides = new Dictionary<BoneOverrideKey, Transform>();
            var armatureRootOverrides = new Dictionary<(string PatchId, string ArmatureId), Transform>();
            var weightOverrides = new Dictionary<string, float>(StringComparer.Ordinal);
            var weightOrder = new List<string>();
            var committedGuids = new HashSet<string>(StringComparer.Ordinal);
            var collectedProxyPatches = new HashSet<string>(StringComparer.Ordinal);

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
                        $"BlendSharePatchMesh on '{comp.name}' references Patch '{comp.Patch.name}' but MeshAsset '{comp.MeshAsset.name}' belongs to '{comp.MeshAsset.Root.name}'.",
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
                        $"BlendSharePatchMesh on '{comp.name}' has no BlendShareAsset assigned."));
                    return new AvatarRendererApplyResult(false, null, diagnostics, null, observedRevisions);
                }

                if (string.IsNullOrWhiteSpace(meshId))
                {
                    diagnostics.Add(new ApplyDiagnostic(
                        ApplyDiagnosticSeverity.Error,
                        "AVATAR_APPLY_MISSING_MESH_ID",
                        $"BlendSharePatchMesh on '{comp.name}' has no MeshId assigned.",
                        patchId: patch.PatchId));
                    return new AvatarRendererApplyResult(false, null, diagnostics, null, observedRevisions);
                }

                string assetPath = AssetDatabase.GetAssetPath(patch);
                string assetGuid = !string.IsNullOrEmpty(assetPath) ? AssetDatabase.AssetPathToGUID(assetPath) : string.Empty;

                // At Build boundaries, commit ONLY dirty Settings buffers so clean patches never abort in Play Mode.
                var preSnapshot = EffectiveSettingsResolver.Resolve(patch);
                if (request.FlushDirtySettings && preSnapshot.IsDirty && !string.IsNullOrEmpty(assetGuid) && committedGuids.Add(assetGuid))
                {
                    var commitRes = BlendShareSettingsSession.Flush(assetGuid);
                    if (!commitRes.Success)
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

                var effectiveSnapshot = request.FlushDirtySettings && preSnapshot.IsDirty
                    ? EffectiveSettingsResolver.Resolve(patch)
                    : preSnapshot;
                string revKey = !string.IsNullOrEmpty(assetGuid) ? assetGuid : patch.GetInstanceID().ToString();
                observedRevisions[revKey] = effectiveSnapshot.SettingsRevision;

                // Scope PatchId uniquely by BlendShareAsset identity so two distinct BlendShareAssets
                // that happen to share identical PatchId strings AND identical ArmatureIds never collide!
                string patchScopeKey = BuildPatchScopeKey(patch, assetGuid);

                var armaturesRes = repo.ReadArmatures(patch);
                if (!armaturesRes.Success)
                {
                    diagnostics.Add(new ApplyDiagnostic(
                        ApplyDiagnosticSeverity.Error,
                        "AVATAR_APPLY_CONTAINER_READ_FAILED",
                        $"Failed reading armatures for patch '{patch.name}': {armaturesRes.Message}",
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

                // Resolve vertex binding against the INCOMING mesh (supports OriginBinding, FBX inverse map, and ReferenceMesh).
                GameObject targetFbx = comp.TargetFbxOverride != null
                    ? comp.TargetFbxOverride
                    : effectiveSnapshot.Settings?.DefaultTargetFbx;

                var bindRes = bindingService.Resolve(new BindingResolveRequest
                {
                    MeshHeader = meshHeader,
                    TargetUnityMesh = request.IncomingMesh,
                    ReferenceUnityMesh = comp.ReferenceMesh,
                    TargetFbxAsset = targetFbx,
                    PayloadSource = repo.PayloadStore,
                    AllowDiskCache = request.AllowDiskBindingCache
                });

                if (!bindRes.Succeeded)
                {
                    diagnostics.Add(new ApplyDiagnostic(
                        ApplyDiagnosticSeverity.Error,
                        string.IsNullOrEmpty(bindRes.FailureCode) ? "AVATAR_APPLY_BINDING_FAILED" : bindRes.FailureCode,
                        $"Failed binding patch '{patch.name}' mesh '{meshId}': {bindRes.Diagnostic}",
                        patchId: patch.PatchId,
                        meshId: meshId));
                    return new AvatarRendererApplyResult(false, null, diagnostics, null, observedRevisions);
                }

                // Collect bone proxy overrides and armature root overrides once per patch, scoped strictly by (BlendShareAsset, ArmatureId).
                if (collectedProxyPatches.Add(patchScopeKey))
                {
                    CollectBoneOverridesForPatch(patch, patchScopeKey, request.BoneProxies, boneOverrides, armatureRootOverrides, diagnostics);
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
                    MeshHeader = meshHeader,
                    Armatures = armaturesRes.Value,
                    EffectiveSettings = effectiveSnapshot.Settings,
                    PayloadSource = repo.PayloadStore,
                    VertexBinding = bindRes.Binding,
                    CreateMissingBones = request.CreateMissingBones && comp.CreateMissingBones,
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

            var meshApplyRes = UnityMeshApplicationPipeline.Apply(new UnityMeshApplyRequest
            {
                BaseMesh = request.IncomingMesh,
                IncomingBones = request.IncomingBones,
                TargetRootTransform = request.TargetRootTransform,
                MeshRendererTransform = request.RendererTransform,
                BoneTransformOverrides = boneOverrides,
                ArmatureRootOverrides = armatureRootOverrides,
                MaxBonesPerVertex = request.MaxBonesPerVertex,
                CreateMissingBones = request.CreateMissingBones,
                PreserveExistingBlendShapes = request.PreserveExistingBlendShapes,
                Items = items,
                Registry = applierRegistry
            });

            if (meshApplyRes.Diagnostics != null)
            {
                diagnostics.AddRange(meshApplyRes.Diagnostics);
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
                observedRevisions);
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

        private static void CollectBoneOverridesForPatch(
            BlendShareAsset patch,
            string patchScopeKey,
            IReadOnlyList<BlendSharePatchBoneProxy> boneProxies,
            Dictionary<BoneOverrideKey, Transform> target,
            Dictionary<(string PatchId, string ArmatureId), Transform> armatureRootOverrides,
            List<ApplyDiagnostic> diagnostics)
        {
            if (patch == null || boneProxies == null || boneProxies.Count == 0)
            {
                return;
            }

            for (int p = 0; p < boneProxies.Count; p++)
            {
                var proxy = boneProxies[p];
                if (proxy == null || !ReferenceEquals(proxy.Patch, patch))
                {
                    continue;
                }

                string armatureId = proxy.ArmatureId ?? string.Empty;
                Transform effectiveParent = proxy.UseHierarchyParent ? proxy.transform.parent : proxy.TargetParent;
                if (effectiveParent != null)
                {
                    armatureRootOverrides[(patchScopeKey, armatureId)] = effectiveParent;
                }

                var bindings = proxy.Bindings;
                for (int b = 0; b < bindings.Count; b++)
                {
                    var binding = bindings[b];
                    if (binding == null || !binding.IsConfigured)
                    {
                        continue;
                    }

                    string normalizedBonePath = FbxNameUtility.NormalizePath(binding.SourceBonePath);
                    if (string.IsNullOrEmpty(normalizedBonePath))
                    {
                        continue;
                    }

                    var key = new BoneOverrideKey(patchScopeKey, armatureId, normalizedBonePath);
                    if (target.TryGetValue(key, out var existing) && existing != null && existing != binding.TargetTransform)
                    {
                        diagnostics.Add(new ApplyDiagnostic(
                            ApplyDiagnosticSeverity.Warning,
                            "AVATAR_APPLY_BONE_PROXY_CONFLICT",
                            $"Conflicting bone proxy bindings for patch '{patch.name}' armature '{armatureId}' bone '{normalizedBonePath}'; later proxy '{proxy.name}' overrides '{existing.name}' with '{binding.TargetTransform.name}'.",
                            patchId: patch.PatchId));
                    }

                    target[key] = binding.TargetTransform;
                }
            }
        }
    }
}
