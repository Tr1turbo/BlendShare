using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autodesk.Fbx;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Model.Documents;
using Triturbo.BlendShare.Model.Features;
using Triturbo.BlendShare.Model.Serialization;
using Triturbo.BlendShare.Pipeline.Export;
using UnityEngine;

namespace Triturbo.BlendShare.FbxExport
{
    public enum FbxFeatureWriteStatus : byte
    {
        Succeeded = 0,
        Skipped = 1,
        Failed = 2
    }

    public sealed class FbxFeatureWriteResult
    {
        public FbxFeatureWriteStatus Status { get; }
        public bool ModifiedMesh { get; }
        public IReadOnlyList<FbxExportDiagnostic> Diagnostics { get; }

        private FbxFeatureWriteResult(
            FbxFeatureWriteStatus status,
            bool modifiedMesh,
            FbxExportDiagnostic[] diagnostics)
        {
            Status = status;
            ModifiedMesh = modifiedMesh;
            Diagnostics = diagnostics ?? Array.Empty<FbxExportDiagnostic>();
        }

        public static FbxFeatureWriteResult Succeeded(bool modifiedMesh = true, params FbxExportDiagnostic[] diagnostics) =>
            new FbxFeatureWriteResult(FbxFeatureWriteStatus.Succeeded, modifiedMesh, diagnostics);

        public static FbxFeatureWriteResult Skipped(params FbxExportDiagnostic[] diagnostics) =>
            new FbxFeatureWriteResult(FbxFeatureWriteStatus.Skipped, false, diagnostics);

        public static FbxFeatureWriteResult Failed(params FbxExportDiagnostic[] diagnostics) =>
            new FbxFeatureWriteResult(FbxFeatureWriteStatus.Failed, false, diagnostics);
    }

    public sealed class FbxExportSpace
    {
        public FbxMatrix4x4 StoredToTargetRaw { get; }
        public FbxMatrix4x4 TargetRawToStored { get; }
        public FbxMatrix4x4 StoredToCanonical { get; }
        public bool IsExactIdentity { get; }
        private readonly FbxMatrix4x4 m_NormalToTargetRaw;

        private FbxExportSpace(FbxMatrix4x4 forward, FbxMatrix4x4 inverse, FbxMatrix4x4 canonical,
            FbxMatrix4x4 normalToTargetRaw, bool identity)
        {
            StoredToTargetRaw = forward;
            TargetRawToStored = inverse;
            StoredToCanonical = canonical;
            m_NormalToTargetRaw = normalToTargetRaw;
            IsExactIdentity = identity;
        }

        public static bool TryCreate(PatchSpaceProvenance stored, FbxSceneMetadata target, out FbxExportSpace space)
        {
            space = null;
            if (stored == null || !stored.IsValid ||
                !CanonicalMeshSpace.TryBuildSpaceToSpace(stored.OriginAxes, stored.OriginRawMetersPerUnit, target.Axes, target.UnitMeters, out var forward, out bool identity) ||
                !CanonicalMeshSpace.TryBuildSpaceToSpace(stored.OriginAxes, stored.OriginRawMetersPerUnit, CanonicalMeshSpace.CanonicalAxes, CanonicalMeshSpace.CanonicalUnitMeters, out var canonical, out _) ||
                !CanonicalMeshSpace.TryBuildSpaceToSpace(stored.OriginAxes, 1d, target.Axes, 1d, out var normal, out _) ||
                !FbxAffineSpaceTransform.TryCreate(forward, out var transform))
                return false;

            space = new FbxExportSpace(forward, transform.InverseOffset, canonical, normal, identity);
            return true;
        }

        public Vector3d PositionDeltaToRaw(Vector3d value) =>
            IsExactIdentity ? value : StoredToTargetRaw.TransformVector(value);

        // Signed axis permutations rotate/reflect a normal delta without changing its length.
        public Vector3d NormalDeltaToRaw(Vector3d value) =>
            IsExactIdentity ? value : m_NormalToTargetRaw.TransformVector(value);

        // Mesh binds map stored mesh-local to stored world. Bone binds map bone-local to world.
        // Both change basis on the input and output sides of the row-vector matrix.
        public FbxMatrix4x4 BindMatrixToRaw(FbxMatrix4x4 value) =>
            IsExactIdentity ? value : TargetRawToStored * value * StoredToTargetRaw;
    }

    public sealed class FbxFeatureWriteInput
    {
        public FbxPatchExportItem PatchItem { get; }
        public MeshDocument MeshHeader { get; }
        public SkeletonDocument Skeleton { get; }
        public BlendShareFeatureData Feature { get; }
        public ILazyPayloadSource PayloadSource { get; }
        public string TargetNodePath { get; }
        public FbxExportSpace PatchSpace { get; }

        public FbxFeatureWriteInput(
            FbxPatchExportItem patchItem,
            MeshDocument meshHeader,
            SkeletonDocument skeleton,
            BlendShareFeatureData feature,
            ILazyPayloadSource payloadSource,
            string targetNodePath,
            FbxExportSpace patchSpace)
        {
            PatchSpace = patchSpace ?? throw new ArgumentNullException(nameof(patchSpace));
            PatchItem = patchItem ?? throw new ArgumentNullException(nameof(patchItem));
            MeshHeader = meshHeader ?? throw new ArgumentNullException(nameof(meshHeader));
            Skeleton = skeleton;
            Feature = feature ?? throw new ArgumentNullException(nameof(feature));
            PayloadSource = payloadSource;
            TargetNodePath = FbxNameUtility.NormalizePath(targetNodePath ?? string.Empty);
        }
    }

    public sealed class FbxExportMeshContext
    {
        public FbxManager Manager { get; }
        public FbxScene SdkScene { get; }
        public FbxNode RootNode { get; }
        public FbxNode TargetNode { get; }
        public FbxMesh TargetMesh { get; }
        public IFbxScene ReaderScene { get; }
        public IFbxMesh ReaderMesh { get; }
        public string TargetNodePath { get; }
        public FbxAffineSpaceTransform CanonicalToTargetRaw { get; }
        public FbxAffineSpaceTransform TargetRawToCanonical { get; }
        public double NumericVerificationToleranceMeters { get; }
        public IDictionary<string, object> SessionState { get; }
        public List<FbxExportDiagnostic> Diagnostics { get; }

        public FbxExportMeshContext(
            FbxManager manager,
            FbxScene sdkScene,
            FbxNode rootNode,
            FbxNode targetNode,
            FbxMesh targetMesh,
            IFbxScene readerScene,
            IFbxMesh readerMesh,
            string targetNodePath,
            FbxAffineSpaceTransform canonicalToTargetRaw,
            FbxAffineSpaceTransform targetRawToCanonical,
            double numericVerificationToleranceMeters,
            IDictionary<string, object> sessionState,
            List<FbxExportDiagnostic> diagnostics)
        {
            Manager = manager;
            SdkScene = sdkScene;
            RootNode = rootNode;
            TargetNode = targetNode;
            TargetMesh = targetMesh;
            ReaderScene = readerScene;
            ReaderMesh = readerMesh;
            TargetNodePath = targetNodePath ?? string.Empty;
            CanonicalToTargetRaw = canonicalToTargetRaw;
            TargetRawToCanonical = targetRawToCanonical;
            NumericVerificationToleranceMeters = numericVerificationToleranceMeters;
            SessionState = sessionState ?? new Dictionary<string, object>(StringComparer.Ordinal);
            Diagnostics = diagnostics ?? new List<FbxExportDiagnostic>();
        }
    }

    public sealed class FbxExportVerifyMeshContext
    {
        public IFbxScene VerifiedScene { get; }
        public IFbxMesh VerifiedMesh { get; }
        public IFbxScene OriginalReaderScene { get; }
        public IFbxMesh OriginalReaderMesh { get; }
        public string TargetNodePath { get; }
        public FbxAffineSpaceTransform CanonicalToTargetRaw { get; }
        public FbxAffineSpaceTransform TargetRawToCanonical { get; }
        public double NumericVerificationToleranceMeters { get; }
        public bool VerifyNumerics { get; }
        public IDictionary<string, object> SessionState { get; }
        public List<FbxExportDiagnostic> Diagnostics { get; }

        public FbxExportVerifyMeshContext(
            IFbxScene verifiedScene,
            IFbxMesh verifiedMesh,
            IFbxScene originalReaderScene,
            IFbxMesh originalReaderMesh,
            string targetNodePath,
            FbxAffineSpaceTransform canonicalToTargetRaw,
            FbxAffineSpaceTransform targetRawToCanonical,
            double numericVerificationToleranceMeters,
            IDictionary<string, object> sessionState,
            List<FbxExportDiagnostic> diagnostics,
            bool verifyNumerics = true)
        {
            VerifiedScene = verifiedScene;
            VerifiedMesh = verifiedMesh;
            OriginalReaderScene = originalReaderScene;
            OriginalReaderMesh = originalReaderMesh;
            TargetNodePath = targetNodePath ?? string.Empty;
            CanonicalToTargetRaw = canonicalToTargetRaw;
            TargetRawToCanonical = targetRawToCanonical;
            NumericVerificationToleranceMeters = numericVerificationToleranceMeters;
            VerifyNumerics = verifyNumerics;
            SessionState = sessionState ?? new Dictionary<string, object>(StringComparer.Ordinal);
            Diagnostics = diagnostics ?? new List<FbxExportDiagnostic>();
        }
    }

    /// <summary>
    /// Capability Registry #6: Feature-specific writer for transactional Autodesk FBX SDK export
    /// and post-export ufbx numeric/channel round-trip verification.
    /// </summary>
    public interface IFeatureFbxWriter
    {
        string FeatureTypeId { get; }
        int ExecutionOrder { get; }

        FbxFeatureWriteResult Preflight(FbxExportMeshContext context, IReadOnlyList<FbxFeatureWriteInput> inputs);
        FbxFeatureWriteResult Write(FbxExportMeshContext context, IReadOnlyList<FbxFeatureWriteInput> inputs);
        FbxFeatureWriteResult Verify(FbxExportVerifyMeshContext context, IReadOnlyList<FbxFeatureWriteInput> inputs);
    }

    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class RegisterBlendShareFeatureFbxWriterAttribute : Attribute
    {
        public Type WriterType { get; }
        public RegisterBlendShareFeatureFbxWriterAttribute(Type writerType) => WriterType = writerType;
    }

    public sealed class FeatureFbxWriterRegistry
    {
        private static readonly Lazy<FeatureFbxWriterRegistry> s_Default =
            new Lazy<FeatureFbxWriterRegistry>(() => new FeatureFbxWriterRegistry());

        public static FeatureFbxWriterRegistry Default => s_Default.Value;

        private readonly Dictionary<string, IFeatureFbxWriter> m_Entries =
            new Dictionary<string, IFeatureFbxWriter>(StringComparer.Ordinal);

        public FeatureFbxWriterRegistry()
        {
            Register(new SkinWeightFbxWriter());
            Register(new BlendShapeFbxWriter());

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic)
                .OrderBy(a => a.FullName, StringComparer.Ordinal))
            {
                RegisterBlendShareFeatureFbxWriterAttribute[] attributes;
                try
                {
                    attributes = assembly.GetCustomAttributes<RegisterBlendShareFeatureFbxWriterAttribute>().ToArray();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[BlendShare.FbxExport] Failed reading FbxWriter attributes from '{assembly.FullName}': {ex.Message}");
                    continue;
                }

                foreach (var attribute in attributes.OrderBy(a => a.WriterType?.FullName, StringComparer.Ordinal))
                {
                    var type = attribute.WriterType;
                    if (type == null || type.IsAbstract || !typeof(IFeatureFbxWriter).IsAssignableFrom(type))
                    {
                        continue;
                    }

                    try
                    {
                        Register((IFeatureFbxWriter)Activator.CreateInstance(type));
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[BlendShare.FbxExport] Failed instantiating FbxWriter '{type.FullName}': {ex.Message}");
                    }
                }
            }
        }

        public void Register(IFeatureFbxWriter writer)
        {
            if (writer == null || string.IsNullOrWhiteSpace(writer.FeatureTypeId))
            {
                throw new ArgumentException("Feature FBX writer must have a non-empty FeatureTypeId.", nameof(writer));
            }

            lock (m_Entries)
            {
                if (m_Entries.ContainsKey(writer.FeatureTypeId))
                {
                    throw new InvalidOperationException($"Duplicate IFeatureFbxWriter registration for '{writer.FeatureTypeId}'.");
                }

                m_Entries.Add(writer.FeatureTypeId, writer);
            }
        }

        public bool Unregister(string featureTypeId)
        {
            if (string.IsNullOrWhiteSpace(featureTypeId))
            {
                return false;
            }

            lock (m_Entries)
            {
                return m_Entries.Remove(featureTypeId);
            }
        }

        public bool TryGet(string featureTypeId, out IFeatureFbxWriter writer)
        {
            if (string.IsNullOrWhiteSpace(featureTypeId))
            {
                writer = null;
                return false;
            }

            lock (m_Entries)
            {
                return m_Entries.TryGetValue(featureTypeId, out writer);
            }
        }

        public IReadOnlyList<IFeatureFbxWriter> GetAllOrdered()
        {
            lock (m_Entries)
            {
                return m_Entries.Values
                    .OrderBy(w => w.ExecutionOrder)
                    .ThenBy(w => w.FeatureTypeId, StringComparer.Ordinal)
                    .ToArray();
            }
        }
    }

    public static class FbxSdkNodeUtility
    {
        public static FbxNode FindNodeByPath(FbxNode rootNode, string path)
        {
            if (rootNode == null)
            {
                return null;
            }

            string normalized = FbxNameUtility.NormalizePath(path ?? string.Empty);
            if (string.IsNullOrEmpty(normalized))
            {
                return rootNode;
            }

            FbxNode current = rootNode;
            string[] parts = normalized.Split('/');
            for (int p = 0; p < parts.Length; p++)
            {
                string part = parts[p];
                if (string.IsNullOrEmpty(part))
                {
                    continue;
                }

                FbxNode found = null;
                int childCount = current.GetChildCount();
                for (int i = 0; i < childCount; i++)
                {
                    var child = current.GetChild(i);
                    if (child != null && string.Equals(child.GetName(), part, StringComparison.Ordinal))
                    {
                        found = child;
                        break;
                    }
                }

                if (found == null)
                {
                    return null;
                }

                current = found;
            }

            return current;
        }

        public static string GetNormalizedNodePath(FbxNode node)
        {
            if (node == null)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            FbxNode current = node;
            while (current != null)
            {
                FbxNode parent = current.GetParent();
                if (parent == null)
                {
                    break;
                }

                parts.Add(current.GetName());
                current = parent;
            }

            parts.Reverse();
            return FbxNameUtility.NormalizePath(string.Join("/", parts));
        }

        public static string GetLeafName(string normalizedPath)
        {
            string norm = FbxNameUtility.NormalizePath(normalizedPath ?? string.Empty);
            int slash = norm.LastIndexOf('/');
            return slash >= 0 && slash + 1 < norm.Length ? norm.Substring(slash + 1) : norm;
        }

        public static string GetParentPath(string normalizedPath)
        {
            string norm = FbxNameUtility.NormalizePath(normalizedPath ?? string.Empty);
            int slash = norm.LastIndexOf('/');
            return slash > 0 ? norm.Substring(0, slash) : string.Empty;
        }

        public static FbxAMatrix ToFbxAMatrix(FbxMatrix4x4 rowMajor)
        {
            var result = new FbxAMatrix();
            for (int r = 0; r < 4; r++)
            {
                result[r] = new FbxDouble4(rowMajor[r, 0], rowMajor[r, 1], rowMajor[r, 2], rowMajor[r, 3]);
            }

            return result;
        }

        public static FbxMatrix4x4 FromFbxAMatrix(FbxAMatrix matrix)
        {
            if (matrix == null)
            {
                return FbxMatrix4x4.Identity;
            }

            return new FbxMatrix4x4(
                matrix.Get(0, 0), matrix.Get(0, 1), matrix.Get(0, 2), matrix.Get(0, 3),
                matrix.Get(1, 0), matrix.Get(1, 1), matrix.Get(1, 2), matrix.Get(1, 3),
                matrix.Get(2, 0), matrix.Get(2, 1), matrix.Get(2, 2), matrix.Get(2, 3),
                matrix.Get(3, 0), matrix.Get(3, 1), matrix.Get(3, 2), matrix.Get(3, 3));
        }

        /// <summary>
        /// Conjugates a canonical row-vector transform matrix into Target FBX raw space:
        /// <c>M_raw = C^-1 * M_canon * C</c>, where <c>C = CanonicalToTargetRaw.Offset</c> maps canonical -> raw.
        /// </summary>
        public static FbxMatrix4x4 ConvertCanonicalMatrixToTargetRaw(
            FbxMatrix4x4 canonicalMatrix,
            FbxAffineSpaceTransform canonicalToTargetRaw)
        {
            if (canonicalToTargetRaw.IsIdentity)
            {
                return canonicalMatrix;
            }

            return canonicalToTargetRaw.InverseOffset * canonicalMatrix * canonicalToTargetRaw.Offset;
        }

        /// <summary>
        /// Applies a raw local affine matrix to an FBX SDK node via TRS decomposition (clearing pivots/pre/post rotations).
        /// Returns false if the matrix contains shear/perspective or non-finite entries.
        /// </summary>
        public static bool TryApplyRawLocalMatrixToNode(FbxNode node, FbxMatrix4x4 rawLocalMatrix, out string error)
        {
            error = null;
            if (node == null)
            {
                error = "Target FBX node is null.";
                return false;
            }

            if (!FbxAffineSpaceTransform.TryCreate(rawLocalMatrix, out _))
            {
                error = $"Local matrix for FBX node '{node.GetName()}' is singular, projective, or non-finite.";
                return false;
            }

            // Check orthogonality of linear rows (no shear).
            var r0 = new Vector3d(rawLocalMatrix[0, 0], rawLocalMatrix[0, 1], rawLocalMatrix[0, 2]);
            var r1 = new Vector3d(rawLocalMatrix[1, 0], rawLocalMatrix[1, 1], rawLocalMatrix[1, 2]);
            var r2 = new Vector3d(rawLocalMatrix[2, 0], rawLocalMatrix[2, 1], rawLocalMatrix[2, 2]);
            double s0 = r0.magnitude;
            double s1 = r1.magnitude;
            double s2 = r2.magnitude;
            if (s0 <= 1e-10d || s1 <= 1e-10d || s2 <= 1e-10d)
            {
                error = $"Local matrix for FBX node '{node.GetName()}' has near-zero scale.";
                return false;
            }

            var u0 = r0 / s0;
            var u1 = r1 / s1;
            var u2 = r2 / s2;
            const double k_OrthoTol = 1e-4d;
            if (Math.Abs(Vector3d.Dot(u0, u1)) > k_OrthoTol ||
                Math.Abs(Vector3d.Dot(u0, u2)) > k_OrthoTol ||
                Math.Abs(Vector3d.Dot(u1, u2)) > k_OrthoTol)
            {
                error = $"Local matrix for FBX node '{node.GetName()}' contains shear and cannot be decomposed into FBX TRS.";
                return false;
            }

            var sdkMatrix = ToFbxAMatrix(rawLocalMatrix);
            FbxVector4 t = sdkMatrix.GetT();
            FbxVector4 r = sdkMatrix.GetR();
            FbxVector4 s = sdkMatrix.GetS();
            if (!IsFinite(t) || !IsFinite(r) || !IsFinite(s))
            {
                error = $"Decomposed TRS for FBX node '{node.GetName()}' is non-finite.";
                return false;
            }

            node.SetRotationOrder(FbxNode.EPivotSet.eSourcePivot, FbxEuler.EOrder.eOrderXYZ);
            node.SetRotationActive(false);
            node.SetPreRotation(FbxNode.EPivotSet.eSourcePivot, new FbxVector4(0d, 0d, 0d, 0d));
            node.SetPostRotation(FbxNode.EPivotSet.eSourcePivot, new FbxVector4(0d, 0d, 0d, 0d));
            node.SetRotationPivot(FbxNode.EPivotSet.eSourcePivot, new FbxVector4(0d, 0d, 0d, 0d));
            node.SetScalingPivot(FbxNode.EPivotSet.eSourcePivot, new FbxVector4(0d, 0d, 0d, 0d));
            node.SetRotationOffset(FbxNode.EPivotSet.eSourcePivot, new FbxVector4(0d, 0d, 0d, 0d));
            node.SetScalingOffset(FbxNode.EPivotSet.eSourcePivot, new FbxVector4(0d, 0d, 0d, 0d));
            node.LclTranslation.Set(new FbxDouble3(t.X, t.Y, t.Z));
            node.LclRotation.Set(new FbxDouble3(r.X, r.Y, r.Z));
            node.LclScaling.Set(new FbxDouble3(s.X, s.Y, s.Z));
            return true;
        }

        /// <summary>
        /// Writes exact lossless authored FBX node transform properties (Euler, rotation order, inherit mode,
        /// pre/post rotations, rotation/scaling pivots and offsets) verbatim onto an FBX SDK node.
        /// </summary>
        public static void ApplyRawTransformPropertiesToNode(FbxNode node, SkeletonBoneRecord bone)
        {
            if (node == null)
            {
                throw new ArgumentNullException(nameof(node));
            }

            if (bone == null || !bone.HasRawTransformProperties)
            {
                throw new InvalidOperationException("SkeletonBoneRecord does not contain raw FBX transform properties.");
            }

            node.LclTranslation.Set(new FbxDouble3(bone.LclTranslation.x, bone.LclTranslation.y, bone.LclTranslation.z));
            node.LclRotation.Set(new FbxDouble3(bone.LclRotation.x, bone.LclRotation.y, bone.LclRotation.z));
            node.LclScaling.Set(new FbxDouble3(bone.LclScaling.x, bone.LclScaling.y, bone.LclScaling.z));
            node.SetRotationOrder(FbxNode.EPivotSet.eSourcePivot, ToSdkRotationOrder(bone.RotationOrder));
            node.SetTransformationInheritType(ToSdkInheritType(bone.InheritMode));
            node.SetRotationActive(bone.RotationActive);
            node.SetPreRotation(FbxNode.EPivotSet.eSourcePivot, ToFbxVector4(bone.PreRotation));
            node.SetPostRotation(FbxNode.EPivotSet.eSourcePivot, ToFbxVector4(bone.PostRotation));
            node.SetRotationPivot(FbxNode.EPivotSet.eSourcePivot, ToFbxVector4(bone.RotationPivot));
            node.SetScalingPivot(FbxNode.EPivotSet.eSourcePivot, ToFbxVector4(bone.ScalingPivot));
            node.SetRotationOffset(FbxNode.EPivotSet.eSourcePivot, ToFbxVector4(bone.RotationOffset));
            node.SetScalingOffset(FbxNode.EPivotSet.eSourcePivot, ToFbxVector4(bone.ScalingOffset));
        }

        private static FbxVector4 ToFbxVector4(Vector3d value) => new FbxVector4(value.x, value.y, value.z);

        private static FbxEuler.EOrder ToSdkRotationOrder(FbxRotationOrder order)
        {
            return order switch
            {
                FbxRotationOrder.XZY => FbxEuler.EOrder.eOrderXZY,
                FbxRotationOrder.YZX => FbxEuler.EOrder.eOrderYZX,
                FbxRotationOrder.YXZ => FbxEuler.EOrder.eOrderYXZ,
                FbxRotationOrder.ZXY => FbxEuler.EOrder.eOrderZXY,
                FbxRotationOrder.ZYX => FbxEuler.EOrder.eOrderZYX,
                FbxRotationOrder.SphericXYZ => FbxEuler.EOrder.eOrderSphericXYZ,
                _ => FbxEuler.EOrder.eOrderXYZ
            };
        }

        private static Autodesk.Fbx.FbxTransform.EInheritType ToSdkInheritType(FbxTransformInheritMode mode)
        {
            return mode switch
            {
                FbxTransformInheritMode.RrSs => Autodesk.Fbx.FbxTransform.EInheritType.eInheritRrSs,
                FbxTransformInheritMode.Rrs => Autodesk.Fbx.FbxTransform.EInheritType.eInheritRrs,
                _ => Autodesk.Fbx.FbxTransform.EInheritType.eInheritRSrs
            };
        }

        private static bool IsFinite(FbxVector4 v)
        {
            return !double.IsNaN(v.X) && !double.IsInfinity(v.X) &&
                   !double.IsNaN(v.Y) && !double.IsInfinity(v.Y) &&
                   !double.IsNaN(v.Z) && !double.IsInfinity(v.Z);
        }
    }
}
