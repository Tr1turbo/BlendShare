using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Container.Serialization;
using Triturbo.BlendShare.EditorAssets.Binding;
using Triturbo.BlendShare.Model.Features;
using Triturbo.BlendShare.Model.Validation;
using UnityEngine;

namespace Triturbo.BlendShare.Pipeline.Apply
{
    public sealed class UnityMeshApplyRequest
    {
        public Mesh BaseMesh { get; set; }
        public Transform[] IncomingBones { get; set; }
        public Transform TargetRootTransform { get; set; }
        public Transform MeshRendererTransform { get; set; }
        public IReadOnlyDictionary<BoneOverrideKey, Transform> BoneTransformOverrides { get; set; }
        public IReadOnlyDictionary<(string PatchId, string ArmatureId), Transform> ArmatureRootOverrides { get; set; }
        public int MaxBonesPerVertex { get; set; } = 4;
        public bool CreateMissingBones { get; set; }
        public IReadOnlyList<PatchMeshApplicationItem> Items { get; set; } = Array.Empty<PatchMeshApplicationItem>();
        public bool CloneBaseMesh { get; set; } = true;
        public bool PreserveExistingBlendShapes { get; set; } = true;
        public FeatureUnityApplierRegistry Registry { get; set; }
    }

    public sealed class UnityMeshApplyResult
    {
        public bool Succeeded { get; internal set; }
        public Mesh OutputMesh { get; internal set; }
        public Transform[] OutputBones { get; internal set; }
        public IReadOnlyList<AppliedBoneBinding> AppliedBoneBindings { get; internal set; } = Array.Empty<AppliedBoneBinding>();
        public BlendShareSkinWeightInfluenceSummary SkinWeightInfluenceSummary { get; internal set; }
        public IReadOnlyList<ApplyDiagnostic> Diagnostics { get; internal set; }
        public int AppliedFeatureCount { get; internal set; }
    }

    public static class UnityMeshApplicationPipeline
    {
        public static UnityMeshApplyResult Apply(UnityMeshApplyRequest request)
        {
            var context = new UnityMeshApplyContext();
            var result = new UnityMeshApplyResult { Diagnostics = context.Diagnostics };
            Mesh clone = null;
            try
            {
                if (request == null || !request.BaseMesh) throw new ArgumentException("A base mesh is required.");
                var registry = request.Registry ?? FeatureUnityApplierRegistry.Default;
                var appliers = registry.GetAllOrdered();
                var groups = appliers.ToDictionary(a => a.FeatureTypeId, a => new List<FeatureApplyInput>(), StringComparer.Ordinal);
                context.Items = request.Items ?? Array.Empty<PatchMeshApplicationItem>();
                foreach (var item in context.Items)
                {
                    if (item == null) continue;
                    foreach (var feature in item.Features ?? item.MeshHeader?.Features ?? Array.Empty<BlendShareFeatureData>() as IReadOnlyList<BlendShareFeatureData>)
                    {
                        if (feature == null) continue;
                        string typeId = feature.TypeId ?? string.Empty;
                        void Report(ApplyDiagnosticSeverity severity, string code, string message) =>
                            context.Diagnostics.Add(new ApplyDiagnostic(severity, code, message, item.PatchId, item.MeshId, typeId));
                        bool unknown = feature is UnknownFeatureData;
                        if (unknown || !groups.ContainsKey(typeId))
                        {
                            string code = unknown
                                ? (feature.Required ? "APP_UNSUPPORTED_REQUIRED_FEATURE" : "APP_SKIPPED_OPTIONAL_UNKNOWN_FEATURE")
                                : (feature.Required ? "APP_MISSING_REQUIRED_APPLIER" : "APP_MISSING_OPTIONAL_APPLIER");
                            Report(feature.Required ? ApplyDiagnosticSeverity.Error : ApplyDiagnosticSeverity.Warning,
                                code, $"Unsupported feature {typeId} schema {feature.SchemaVersion}.");
                            continue;
                        }
                        if (item.VertexBinding?.VertexMap?.SourceCount != request.BaseMesh.vertexCount)
                        {
                            Report(ApplyDiagnosticSeverity.Error, "APP_INVALID_BINDING", "Binding vertex count does not match base mesh.");
                            continue;
                        }
                        if (FeatureValidatorRegistry.Default.TryGetValidator(typeId, out var validator))
                        {
                            var validation = new FeatureValidationResult();
                            validator.Validate(feature, new FeatureValidationContext(item.MeshId, item.VertexBinding.VertexMap.TargetCount,
                                payloadSource: item.PayloadSource, validatePayloads: false), validation);
                            foreach (var issue in validation.Issues)
                                Report(issue.Severity == FeatureValidationSeverity.Error ? ApplyDiagnosticSeverity.Error : ApplyDiagnosticSeverity.Warning,
                                    issue.Code, issue.Message);
                        }
                        groups[typeId].Add(new FeatureApplyInput
                        {
                            PatchId = item.PatchId, MeshId = item.MeshId, Feature = feature, MeshHeader = item.MeshHeader,
                            Armatures = item.Armatures, CreateMissingBones = item.CreateMissingBones,
                            VertexBinding = item.VertexBinding, EffectiveSettings = item.EffectiveSettings, PayloadSource = item.PayloadSource
                        });
                    }
                }
                if (context.Diagnostics.Any(d => d.Severity == ApplyDiagnosticSeverity.Error)) return result;
                if (request.CloneBaseMesh) clone = UnityMeshReaderUtility.CloneReadable(request.BaseMesh);
                context.WorkingMesh = request.CloneBaseMesh ? clone : request.BaseMesh;
                if (!context.WorkingMesh || !context.WorkingMesh.isReadable) throw new InvalidOperationException("Working mesh is unreadable.");
                context.WorkingBones = (Transform[])request.IncomingBones?.Clone();
                context.TargetRootTransform = request.TargetRootTransform;
                context.MeshRendererTransform = request.MeshRendererTransform;
                context.BoneTransformOverrides = request.BoneTransformOverrides;
                context.ArmatureRootOverrides = request.ArmatureRootOverrides;
                context.MaxBonesPerVertex = request.MaxBonesPerVertex > 0 ? request.MaxBonesPerVertex : 4;
                context.CreateMissingBones = request.CreateMissingBones;
                context.PreserveExistingBlendShapes = request.PreserveExistingBlendShapes;
                foreach (var applier in appliers)
                {
                    var inputs = groups[applier.FeatureTypeId];
                    if (inputs.Count == 0) continue;
                    var applied = applier.Apply(context, inputs);
                    context.Diagnostics.AddRange(applied.Diagnostics);
                    if (applied.Status == FeatureApplyStatus.Failed) return result;
                    if (applied.Status == FeatureApplyStatus.Applied) result.AppliedFeatureCount += inputs.Count;
                }
                result.OutputBones = context.WorkingBones;
                result.AppliedBoneBindings = context.AppliedBoneBindings;
                result.SkinWeightInfluenceSummary = context.SkinWeightInfluenceSummary;
                result.OutputMesh = context.WorkingMesh;
                result.Succeeded = true;
            }
            catch (Exception ex)
            {
                context.Diagnostics.Add(new ApplyDiagnostic(ApplyDiagnosticSeverity.Error, "APP_FAILED", ex.Message));
            }
            finally
            {
                if (!result.Succeeded && clone) UnityEngine.Object.DestroyImmediate(clone);
            }
            return result;
        }
    }
}
