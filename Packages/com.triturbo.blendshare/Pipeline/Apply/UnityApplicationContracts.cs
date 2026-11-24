using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Triturbo.BlendShare.EditorAssets.Binding;
using Triturbo.BlendShare.Model.Documents;
using Triturbo.BlendShare.Model.Features;
using Triturbo.BlendShare.Model.Serialization;
using Triturbo.BlendShare.Model.Settings;
using Triturbo.BlendShare.Runtime.Assets;
using UnityEngine;

namespace Triturbo.BlendShare.Pipeline.Apply
{
    public enum ApplyDiagnosticSeverity { Info, Warning, Error }

    public readonly struct ApplyDiagnostic
    {
        public ApplyDiagnosticSeverity Severity { get; }
        public string Code { get; }
        public string Message { get; }
        public string PatchId { get; }
        public string MeshId { get; }
        public string FeatureTypeId { get; }

        public ApplyDiagnostic(ApplyDiagnosticSeverity severity, string code, string message,
            string patchId = null, string meshId = null, string featureTypeId = null)
        {
            Severity = severity;
            Code = code;
            Message = message;
            PatchId = patchId;
            MeshId = meshId;
            FeatureTypeId = featureTypeId;
        }
    }

    public enum FeatureApplyStatus { Applied, Skipped, Failed }

    public sealed class FeatureApplyResult
    {
        public FeatureApplyStatus Status { get; }
        public IReadOnlyList<ApplyDiagnostic> Diagnostics { get; }
        public bool ModifiedMesh { get; }

        private FeatureApplyResult(FeatureApplyStatus status, bool modifiedMesh, ApplyDiagnostic[] diagnostics)
        {
            Status = status;
            ModifiedMesh = modifiedMesh;
            Diagnostics = diagnostics ?? Array.Empty<ApplyDiagnostic>();
        }

        public static FeatureApplyResult Applied(params ApplyDiagnostic[] diagnostics) =>
            new FeatureApplyResult(FeatureApplyStatus.Applied, true, diagnostics);
        public static FeatureApplyResult Skipped(params ApplyDiagnostic[] diagnostics) =>
            new FeatureApplyResult(FeatureApplyStatus.Skipped, false, diagnostics);
        public static FeatureApplyResult Failed(params ApplyDiagnostic[] diagnostics) =>
            new FeatureApplyResult(FeatureApplyStatus.Failed, false, diagnostics);
    }

    /// <summary>
    /// Concrete bone binding resolved and applied during Unity mesh generation.
    /// Passed directly into Artifact persistence and NDMF renderer updates without re-parsing.
    /// </summary>
    public sealed class AppliedBoneBinding
    {
        public string PatchId { get; }
        public string SourceBonePath { get; }
        public string TargetBonePath { get; }
        public Transform TargetTransform { get; }
        public Matrix4x4 BindPose { get; }
        public int BoneIndex { get; }
        public bool IsNewBone { get; }

        public AppliedBoneBinding(
            string patchId,
            string sourceBonePath,
            string targetBonePath,
            Transform targetTransform,
            Matrix4x4 bindPose,
            int boneIndex,
            bool isNewBone)
        {
            PatchId = patchId ?? string.Empty;
            SourceBonePath = sourceBonePath ?? string.Empty;
            TargetBonePath = targetBonePath ?? string.Empty;
            TargetTransform = targetTransform;
            BindPose = bindPose;
            BoneIndex = boneIndex;
            IsNewBone = isNewBone;
        }
    }

    /// <summary>
    /// Optional per-bone transform override (e.g. from BlendShareBoneProxy or NDMF preview) keyed by (PatchId, SourceBonePath).
    /// </summary>
    public readonly struct BoneOverrideKey : IEquatable<BoneOverrideKey>
    {
        public string PatchId { get; }
        public string SourceBonePath { get; }

        public BoneOverrideKey(string patchId, string sourceBonePath)
        {
            PatchId = patchId ?? string.Empty;
            SourceBonePath = sourceBonePath ?? string.Empty;
        }

        public bool Equals(BoneOverrideKey other) =>
            string.Equals(PatchId, other.PatchId, StringComparison.Ordinal) &&
            string.Equals(SourceBonePath, other.SourceBonePath, StringComparison.Ordinal);

        public override bool Equals(object obj) => obj is BoneOverrideKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(PatchId, SourceBonePath);
    }

    public abstract class PatchMeshContext
    {
        public string PatchId { get; set; }
        public string MeshId { get; set; }
        public PatchSpaceProvenance SpaceProvenance { get; set; }
        public MeshDocument MeshHeader { get; set; }
        public SkeletonDocument Skeleton { get; set; }
        public BlendShareImportSettings EffectiveSettings { get; set; }
        public ILazyPayloadSource PayloadSource { get; set; }
        public ResolvedVertexBinding VertexBinding { get; set; }
        public bool CreateMissingBones { get; set; } = true;
        public IReadOnlyDictionary<string, Matrix4x4> BindPoseOverrides { get; set; }
    }

    public sealed class FeatureApplyInput : PatchMeshContext
    {
        public BlendShareFeatureData Feature { get; set; }
    }

    public sealed class PatchMeshApplicationItem : PatchMeshContext
    {
        public IReadOnlyList<BlendShareFeatureData> Features { get; set; }
    }

    public sealed class UnityMeshApplyContext
    {
        public Mesh WorkingMesh { get; internal set; }
        public Transform[] WorkingBones { get; set; }
        public Transform IncomingRootBone { get; internal set; }
        public Transform OutputRootBone { get; set; }
        public string OutputRootBonePath { get; set; } = string.Empty;
        public Transform TargetRootTransform { get; internal set; }
        public Transform MeshRendererTransform { get; internal set; }
        public IReadOnlyDictionary<BoneOverrideKey, Transform> BoneTransformOverrides { get; internal set; }
        public int MaxBonesPerVertex { get; internal set; } = 4;
        public bool CreateMissingBones { get; internal set; }
        public IReadOnlyList<PatchMeshApplicationItem> Items { get; internal set; }
        public bool PreserveExistingBlendShapes { get; internal set; } = true;
        public List<AppliedBoneBinding> AppliedBoneBindings { get; } = new List<AppliedBoneBinding>();
        public List<GeneratedSkeletonBoneRecord> AppliedSkeletonBones { get; } = new List<GeneratedSkeletonBoneRecord>();
        public BlendShareSkinWeightInfluenceSummary SkinWeightInfluenceSummary { get; set; }
        public List<ApplyDiagnostic> Diagnostics { get; } = new List<ApplyDiagnostic>();
    }

    public interface IFeatureUnityApplier
    {
        string FeatureTypeId { get; }
        int ExecutionOrder { get; }
        FeatureApplyResult Apply(UnityMeshApplyContext context, IReadOnlyList<FeatureApplyInput> inputs);
    }

    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class RegisterBlendShareFeatureUnityApplierAttribute : Attribute
    {
        public Type ApplierType { get; }
        public RegisterBlendShareFeatureUnityApplierAttribute(Type applierType) => ApplierType = applierType;
    }

    public sealed class FeatureUnityApplierRegistry
    {
        private static readonly Lazy<FeatureUnityApplierRegistry> s_Default =
            new Lazy<FeatureUnityApplierRegistry>(() => new FeatureUnityApplierRegistry());
        public static FeatureUnityApplierRegistry Default => s_Default.Value;
        private readonly Dictionary<string, IFeatureUnityApplier> m_Entries =
            new Dictionary<string, IFeatureUnityApplier>(StringComparer.Ordinal);

        public FeatureUnityApplierRegistry()
        {
            Register(new SkinWeightUnityApplier());
            Register(new BlendShapeUnityApplier());
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic).OrderBy(a => a.FullName, StringComparer.Ordinal))
            {
                RegisterBlendShareFeatureUnityApplierAttribute[] attributes;
                try { attributes = assembly.GetCustomAttributes<RegisterBlendShareFeatureUnityApplierAttribute>().ToArray(); }
                catch (Exception ex) { Debug.LogWarning(ex.Message); continue; }
                foreach (var attribute in attributes.OrderBy(a => a.ApplierType?.FullName, StringComparer.Ordinal))
                {
                    var type = attribute.ApplierType;
                    if (type == null || type.IsAbstract || !typeof(IFeatureUnityApplier).IsAssignableFrom(type)) continue;
                    try { Register((IFeatureUnityApplier)Activator.CreateInstance(type)); }
                    catch (Exception ex) { Debug.LogWarning(ex.Message); }
                }
            }
        }

        public void Register(IFeatureUnityApplier applier)
        {
            if (string.IsNullOrWhiteSpace(applier?.FeatureTypeId)) throw new ArgumentException("Missing feature type ID.");
            lock (m_Entries) m_Entries.Add(applier.FeatureTypeId, applier);
        }

        public bool Unregister(string featureTypeId) { lock (m_Entries) return m_Entries.Remove(featureTypeId); }
        public bool TryGet(string featureTypeId, out IFeatureUnityApplier applier)
        {
            lock (m_Entries) return m_Entries.TryGetValue(featureTypeId, out applier);
        }

        public IReadOnlyList<IFeatureUnityApplier> GetAllOrdered()
        {
            lock (m_Entries) return m_Entries.Values.OrderBy(a => a.ExecutionOrder)
                .ThenBy(a => a.FeatureTypeId, StringComparer.Ordinal).ToArray();
        }
    }
}
