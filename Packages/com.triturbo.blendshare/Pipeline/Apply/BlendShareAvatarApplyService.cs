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
        /// <c>(BlendShareAsset, ArmatureId)</c> identity.
        /// </summary>
        public IReadOnlyList<BlendShareBoneProxy> BoneProxies { get; set; } = Array.Empty<BlendShareBoneProxy>();

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
        public int MaxBonesPerVertex { get; set; } = 0;

        /// <summary>Optional repository override (defaults to <see cref="BlendShareSourceRepository.Shared"/>).</summary>
        public BlendShareSourceRepository Repository { get; set; }

        /// <summary>Optional binding service override (defaults to <see cref="BindingService.Shared"/>).</summary>
        public BindingService BindingServiceOverride { get; set; }

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
                    "No BlendShareMesh components were provided."));
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
            var resolutions = new Dictionary<BlendShareArmatureKey, BlendShareArmatureBoneProxyResolution>();
            var clusterOwners = new Dictionary<BoneOverrideKey, Queue<BlendShareMesh>>();

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

                foreach (var skin in features.OfType<SkinWeightFeature>())
                {
                    var armature = new BlendShareArmatureKey(patch, skin.ArmatureId);
                    if (!resolutions.TryGetValue(armature, out var resolution))
                    {
                        resolution = BlendShareBoneProxyResolver.Resolve(armature, request.BoneProxies);
                        if (!resolution.Success)
                        {
                            diagnostics.Add(new ApplyDiagnostic(ApplyDiagnosticSeverity.Error,
                                "AVATAR_APPLY_INVALID_ARMATURE", string.Join("\n", resolution.Diagnostics)));
                            return new AvatarRendererApplyResult(false, null, diagnostics, null, observedRevisions);
                        }
                        resolutions.Add(armature, resolution);
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
                            var key = new BoneOverrideKey(patchScopeKey, skin.ArmatureId,
                                FbxNameUtility.NormalizePath(binding.SourceBonePath));
                            boneOverrides[key] = binding.Transform;
                            if (parent && proxy.IsRootBinding(binding))
                                armatureRootOverrides[(patchScopeKey, skin.ArmatureId)] = parent;
                        }
                    }
                    foreach (var cluster in skin.Clusters)
                    {
                        var key = new BoneOverrideKey(patchScopeKey, skin.ArmatureId, FbxNameUtility.NormalizePath(cluster.BonePath));
                        if (!clusterOwners.TryGetValue(key, out var owners))
                            clusterOwners.Add(key, owners = new Queue<BlendShareMesh>());
                        owners.Enqueue(comp);
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

            int effectiveMaxBones = request.MaxBonesPerVertex > 0
                ? request.MaxBonesPerVertex
                : BlendShareSkinWeightSettings.EffectiveMaxBonesPerVertex;

            var meshApplyRes = UnityMeshApplicationPipeline.Apply(new UnityMeshApplyRequest
            {
                BaseMesh = request.IncomingMesh,
                IncomingBones = request.IncomingBones,
                TargetRootTransform = request.TargetRootTransform,
                MeshRendererTransform = request.RendererTransform,
                BoneTransformOverrides = boneOverrides,
                ArmatureRootOverrides = armatureRootOverrides,
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
                // Skin application emits a record per cluster in component order. A later
                // cluster can reuse a slot, so its pose must also win when it has no override.
                var poses = meshApplyRes.OutputMesh.bindposes;
                var records = meshApplyRes.AppliedBoneBindings.ToArray();
                for (int i = 0; i < records.Length; i++)
                {
                    var record = records[i];
                    var key = new BoneOverrideKey(record.PatchId, record.ArmatureId, record.SourceBonePath);
                    if (!clusterOwners.TryGetValue(key, out var owners) || owners.Count == 0) continue;
                    var owner = owners.Dequeue();
                    var pose = owner.TryGetBindPoseOverride(record.SourceBonePath, out var authored) ? authored : record.BindPose;
                    poses[record.BoneIndex] = pose;
                    records[i] = new AppliedBoneBinding(record.PatchId, record.ArmatureId, record.SourceBonePath,
                        record.TargetBonePath, record.TargetTransform, pose, record.BoneIndex, record.IsNewBone);
                    var resolution = resolutions[new BlendShareArmatureKey(owner.ResolvedPatch, record.ArmatureId)];
                    if (resolution.TryGetBinding(record.SourceBonePath, out var bone))
                        uses.Add(new AvatarBoneBindingUse(bone, pose, request.RendererTransform));
                }
                meshApplyRes.OutputMesh.bindposes = poses;
                meshApplyRes.AppliedBoneBindings = records;
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
