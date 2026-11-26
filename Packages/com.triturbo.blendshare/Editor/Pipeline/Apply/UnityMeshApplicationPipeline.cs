using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Container.Serialization;
using Triturbo.BlendShare.EditorAssets.Mapping;
using Triturbo.BlendShare.Model.Documents;
using Triturbo.BlendShare.Model.Features;
using Triturbo.BlendShare.Model.Validation;
using Triturbo.BlendShare.Pipeline.Skeleton;
using Triturbo.BlendShare.Assets;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.Pipeline.Apply
{
    public sealed class UnityMeshApplyRequest
    {
        public Mesh BaseMesh { get; set; }
        public Transform[] IncomingBones { get; set; }
        public string[] IncomingBoneSlotPaths { get; set; }
        public Transform IncomingRootBone { get; set; }
        public Transform TargetRootTransform { get; set; }
        public Transform MeshRendererTransform { get; set; }
        public UnitySkeletonScope SharedSkeletonScope { get; set; }
        public IReadOnlyDictionary<BoneOverrideKey, Transform> BoneTransformOverrides { get; set; }
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
        public Transform OutputRootBone { get; internal set; }
        public string OutputRootBonePath { get; internal set; } = string.Empty;
        public IReadOnlyList<AppliedBoneBinding> AppliedBoneBindings { get; internal set; } = Array.Empty<AppliedBoneBinding>();
        public IReadOnlyList<GeneratedSkeletonBoneRecord> AppliedSkeletonBones { get; internal set; } = Array.Empty<GeneratedSkeletonBoneRecord>();
        public IReadOnlyList<Transform> CreatedBoneRoots { get; internal set; } = Array.Empty<Transform>();
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
            Mesh inPlaceBackup = null;
            string origBaseMeshName = null;
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
                    if (item.Skeleton != null)
                    {
                        var skelVal = SkeletonDocumentValidator.Validate(item.Skeleton);
                        foreach (var issue in skelVal.Issues)
                        {
                            context.Diagnostics.Add(new ApplyDiagnostic(
                                issue.Severity == FeatureValidationSeverity.Error ? ApplyDiagnosticSeverity.Error : ApplyDiagnosticSeverity.Warning,
                                issue.Code,
                                issue.Message,
                                item.PatchId,
                                item.MeshId,
                                SkeletonDocument.SectionTypeId));
                        }
                    }

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
                        if (item.VertexMapping?.VertexMap?.SourceCount != request.BaseMesh.vertexCount)
                        {
                            Report(ApplyDiagnosticSeverity.Error, "APP_INVALID_MAPPING", "Vertex mapping count does not match base mesh.");
                            continue;
                        }
                        if (FeatureValidatorRegistry.Default.TryGetValidator(typeId, out var validator))
                        {
                            var validation = new FeatureValidationResult();
                            validator.Validate(feature, new FeatureValidationContext(item.MeshId, item.VertexMapping.VertexMap.TargetCount,
                                payloadSource: item.PayloadSource, validatePayloads: false), validation);
                            foreach (var issue in validation.Issues)
                                Report(issue.Severity == FeatureValidationSeverity.Error ? ApplyDiagnosticSeverity.Error : ApplyDiagnosticSeverity.Warning,
                                    issue.Code, issue.Message);
                        }
                        groups[typeId].Add(new FeatureApplyInput
                        {
                            PatchId = item.PatchId, MeshId = item.MeshId, Feature = feature,
                            SpaceProvenance = item.SpaceProvenance, MeshHeader = item.MeshHeader,
                            Skeleton = item.Skeleton, CreateMissingBones = item.CreateMissingBones,
                            BindPoseOverrides = item.BindPoseOverrides,
                            VertexMapping = item.VertexMapping, VerifiedRawTargetSkin = item.VerifiedRawTargetSkin,
                            EffectiveSettings = item.EffectiveSettings, PayloadSource = item.PayloadSource
                        });
                    }
                }
                if (context.Diagnostics.Any(d => d.Severity == ApplyDiagnosticSeverity.Error)) return result;
                if (request.CloneBaseMesh)
                {
                    clone = UnityMeshReaderUtility.CloneReadable(request.BaseMesh);
                }
                else
                {
                    if (!request.BaseMesh.isReadable) throw new InvalidOperationException("Working mesh is unreadable.");
                    origBaseMeshName = request.BaseMesh.name;
                    inPlaceBackup = UnityMeshReaderUtility.CloneReadable(request.BaseMesh);
                }

                context.WorkingMesh = request.CloneBaseMesh ? clone : request.BaseMesh;
                if (!context.WorkingMesh || !context.WorkingMesh.isReadable) throw new InvalidOperationException("Working mesh is unreadable.");
                context.WorkingBones = (Transform[])request.IncomingBones?.Clone();
                context.IncomingBoneSlotPaths = (string[])request.IncomingBoneSlotPaths?.Clone();
                context.IncomingRootBone = request.IncomingRootBone;
                context.OutputRootBone = request.IncomingRootBone;
                context.TargetRootTransform = request.TargetRootTransform;
                context.MeshRendererTransform = request.MeshRendererTransform;
                context.SharedSkeletonScope = request.SharedSkeletonScope;
                context.BoneTransformOverrides = request.BoneTransformOverrides;
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
                result.OutputRootBone = context.OutputRootBone;
                result.OutputRootBonePath = context.OutputRootBonePath;
                result.AppliedBoneBindings = context.AppliedBoneBindings;
                result.AppliedSkeletonBones = context.AppliedSkeletonBones;
                result.CreatedBoneRoots = context.CreatedBoneRoots.ToArray();
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
                if (!result.Succeeded)
                {
                    if (clone) UnityEngine.Object.DestroyImmediate(clone);
                    if (inPlaceBackup && request?.BaseMesh)
                    {
                        EditorUtility.CopySerialized(inPlaceBackup, request.BaseMesh);
                        request.BaseMesh.name = origBaseMeshName;
                    }

                    for (int i = 0; i < context.CreatedBoneRoots.Count; i++)
                    {
                        if (context.CreatedBoneRoots[i])
                        {
                            UnityEngine.Object.DestroyImmediate(context.CreatedBoneRoots[i].gameObject);
                        }
                    }
                }

                if (inPlaceBackup) UnityEngine.Object.DestroyImmediate(inPlaceBackup);
            }
            return result;
        }
    }
}
