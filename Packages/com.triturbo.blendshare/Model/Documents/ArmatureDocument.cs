using System;
using System.Collections.Generic;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Model.Serialization;

namespace Triturbo.BlendShare.Model.Documents
{
    /// <summary>
    /// Shared armature bone table referenced by ArmatureId from SkinWeightFeature.
    /// Stored once per PatchDocument instead of as hidden ScriptableObject subassets.
    /// </summary>
    [BlendShareRecord(SchemaVersion = 1)]
    public sealed class ArmatureDocument
    {
        [BlendShareField(1)] public string ArmatureId = string.Empty;
        [BlendShareField(2)] public string Name = string.Empty;
        [BlendShareField(3)] public string RootBonePath = string.Empty;
        [BlendShareField(4)] public List<ArmatureBoneRecord> Bones = new List<ArmatureBoneRecord>();

        /// <summary>
        /// Coordinate space provenance (Origin FBX axes and meters-per-unit scale) for this armature's bone bind matrices.
        /// </summary>
        [BlendShareField(5)] public MeshSpaceProvenance SpaceProvenance = new MeshSpaceProvenance();

        public ArmatureBoneRecord GetBone(string path)
        {
            string normalized = NormalizeBonePath(path);
            if (string.IsNullOrEmpty(normalized) || Bones == null)
            {
                return null;
            }

            for (int i = 0; i < Bones.Count; i++)
            {
                var bone = Bones[i];
                if (bone != null && string.Equals(NormalizeBonePath(bone.Path), normalized, StringComparison.Ordinal))
                {
                    return bone;
                }
            }

            return null;
        }

        public bool HasBone(string path) => GetBone(path) != null;

        /// <summary>
        /// Enumerates stored bone paths in deterministic parent-before-child hierarchy order.
        /// </summary>
        public List<string> GetBonePathsInHierarchyOrder()
        {
            var result = new List<string>();
            if (Bones == null || Bones.Count == 0)
            {
                return result;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < Bones.Count; i++)
            {
                var bone = Bones[i];
                if (bone == null)
                {
                    continue;
                }

                string normalized = NormalizeBonePath(bone.Path);
                if (!string.IsNullOrEmpty(normalized) && seen.Add(normalized))
                {
                    result.Add(normalized);
                }
            }

            result.Sort((left, right) =>
            {
                int depthDiff = CountPathDepth(left).CompareTo(CountPathDepth(right));
                return depthDiff != 0 ? depthDiff : StringComparer.Ordinal.Compare(left, right);
            });
            return result;
        }

        internal static string NormalizeBonePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            string normalized = path.Replace('\\', '/').Trim().Trim('/');
            return normalized == "." ? string.Empty : normalized;
        }

        private static int CountPathDepth(string normalizedPath)
        {
            int depth = 0;
            for (int i = 0; i < normalizedPath.Length; i++)
            {
                if (normalizedPath[i] == '/')
                {
                    depth++;
                }
            }

            return depth;
        }
    }

    [BlendShareRecord(SchemaVersion = 1)]
    public sealed class ArmatureBoneRecord
    {
        [BlendShareField(1)] public string Path = string.Empty;
        [BlendShareField(2)] public string ParentPath = string.Empty;
        [BlendShareField(3)] public FbxMatrix4x4 LocalBindMatrix = FbxMatrix4x4.Identity;
        [BlendShareField(4)] public FbxMatrix4x4 GlobalBindMatrix = FbxMatrix4x4.Identity;

        /// <summary>
        /// When true, the bone is created if absent in the target hierarchy or FBX scene.
        /// When false, the record represents a transform modification on an existing bone.
        /// </summary>
        [BlendShareField(5)] public bool CreateIfMissing = true;

        /// <summary>
        /// Whether this record carries valid local bind transform data.
        /// </summary>
        [BlendShareField(6)] public bool HasTransformData = true;

        /// <summary>
        /// Whether the lossless FBX raw transform properties below were captured directly in the stored FBX space.
        /// </summary>
        [BlendShareField(7)] public bool HasRawTransformProperties;

        [BlendShareField(8)] public Vector3d LclTranslation = Vector3d.zero;
        [BlendShareField(9)] public Vector3d LclRotation = Vector3d.zero;
        [BlendShareField(10)] public Vector3d LclScaling = Vector3d.one;
        [BlendShareField(11)] public FbxRotationOrder RotationOrder = FbxRotationOrder.XYZ;
        [BlendShareField(12)] public FbxTransformInheritMode InheritMode = FbxTransformInheritMode.RrSs;
        [BlendShareField(13)] public bool RotationActive = true;
        [BlendShareField(14)] public Vector3d PreRotation = Vector3d.zero;
        [BlendShareField(15)] public Vector3d PostRotation = Vector3d.zero;
        [BlendShareField(16)] public Vector3d RotationPivot = Vector3d.zero;
        [BlendShareField(17)] public Vector3d ScalingPivot = Vector3d.zero;
        [BlendShareField(18)] public Vector3d RotationOffset = Vector3d.zero;
        [BlendShareField(19)] public Vector3d ScalingOffset = Vector3d.zero;

        public void CopyRawTransformFrom(FbxNodeRawTransform rawTransform)
        {
            HasRawTransformProperties = true;
            LclTranslation = rawTransform.LclTranslation;
            LclRotation = rawTransform.LclRotation;
            LclScaling = rawTransform.LclScaling;
            RotationOrder = rawTransform.RotationOrder;
            InheritMode = rawTransform.OriginalInheritMode;
            RotationActive = rawTransform.RotationActive;
            PreRotation = rawTransform.PreRotation;
            PostRotation = rawTransform.PostRotation;
            RotationPivot = rawTransform.RotationPivot;
            ScalingPivot = rawTransform.ScalingPivot;
            RotationOffset = rawTransform.RotationOffset;
            ScalingOffset = rawTransform.ScalingOffset;
        }

        public bool ApproximatelyTransform(
            ArmatureBoneRecord other,
            double linearTolerance = 1e-6,
            double translationTolerance = 1e-6)
        {
            if (other == null || !HasTransformData || !other.HasTransformData)
            {
                return false;
            }

            if (!MatricesApproximatelyEqual(LocalBindMatrix, other.LocalBindMatrix, linearTolerance, translationTolerance))
            {
                return false;
            }

            if (HasRawTransformProperties && other.HasRawTransformProperties)
            {
                return RotationOrder == other.RotationOrder &&
                       InheritMode == other.InheritMode &&
                       RotationActive == other.RotationActive &&
                       VectorsApproximatelyEqual(LclTranslation, other.LclTranslation, translationTolerance) &&
                       VectorsApproximatelyEqual(LclRotation, other.LclRotation, linearTolerance) &&
                       VectorsApproximatelyEqual(LclScaling, other.LclScaling, linearTolerance) &&
                       VectorsApproximatelyEqual(PreRotation, other.PreRotation, linearTolerance) &&
                       VectorsApproximatelyEqual(PostRotation, other.PostRotation, linearTolerance) &&
                       VectorsApproximatelyEqual(RotationPivot, other.RotationPivot, translationTolerance) &&
                       VectorsApproximatelyEqual(ScalingPivot, other.ScalingPivot, translationTolerance) &&
                       VectorsApproximatelyEqual(RotationOffset, other.RotationOffset, translationTolerance) &&
                       VectorsApproximatelyEqual(ScalingOffset, other.ScalingOffset, translationTolerance);
            }

            return true;
        }

        private static bool VectorsApproximatelyEqual(Vector3d a, Vector3d b, double tolerance)
        {
            return Math.Abs(a.x - b.x) <= tolerance &&
                   Math.Abs(a.y - b.y) <= tolerance &&
                   Math.Abs(a.z - b.z) <= tolerance;
        }

        private static bool MatricesApproximatelyEqual(
            FbxMatrix4x4 a,
            FbxMatrix4x4 b,
            double linearTolerance,
            double translationTolerance)
        {
            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++)
                {
                    double tol = (r == 3 && c < 3) ? translationTolerance : linearTolerance;
                    if (Math.Abs(a[r, c] - b[r, c]) > tol)
                    {
                        return false;
                    }
                }
            }

            return true;
        }
    }
}
