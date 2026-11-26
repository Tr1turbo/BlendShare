using System;
using System.Collections.Generic;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Model.Serialization;

namespace Triturbo.BlendShare.Model.Documents
{
    /// <summary>
    /// Classifies an authored scene hierarchy node in <see cref="SkeletonDocument"/> so FBX export
    /// creates <c>FbxSkeleton</c> vs attribute-less <c>FbxNull</c> faithfully.
    /// </summary>
    public enum SkeletonNodeKind : byte
    {
        /// <summary>Skeleton joint/limb node (<c>IFbxNode.IsBone == true</c>).</summary>
        Bone = 0,

        /// <summary>Non-bone transform/locator/root container node (<c>IFbxNode.IsBone == false</c>).</summary>
        Null = 1
    }

    /// <summary>
    /// Patch-level shared skeleton document stored once per <see cref="PatchDocument"/>.
    /// Separates authored Upsert <see cref="Nodes"/> (updated if present in Target, created if absent)
    /// from read-only Source rest <see cref="Anchors"/> (used strictly to calibrate post-edit endpoint
    /// coordinate frames without mutating untouched Target nodes).
    /// Each normalized path appears in <see cref="Nodes"/> OR <see cref="Anchors"/>, never both.
    /// </summary>
    [BlendShareRecord(SchemaVersion = 1)]
    public sealed class SkeletonDocument
    {
        public const string SectionFeatureId = "skeleton";
        public const string SectionTypeId = "blendshare.skeleton";
        public const int CurrentSchemaVersion = 1;

        [BlendShareField(1)] public List<SkeletonNodeRecord> Nodes = new List<SkeletonNodeRecord>();
        [BlendShareField(2)] public List<SkeletonAnchorRecord> Anchors = new List<SkeletonAnchorRecord>();

        /// <summary>
        /// Whether this skeleton document contains at least one authored node or calibration anchor.
        /// </summary>
        public bool HasContent =>
            (Nodes != null && Nodes.Count > 0) ||
            (Anchors != null && Anchors.Count > 0);

        /// <summary>
        /// Looks up an authored Upsert node record by normalized scene-root-relative path.
        /// </summary>
        public SkeletonNodeRecord GetNode(string path)
        {
            string normalized = FbxNameUtility.NormalizePath(path);
            if (string.IsNullOrEmpty(normalized) || Nodes == null)
            {
                return null;
            }

            for (int i = 0; i < Nodes.Count; i++)
            {
                var node = Nodes[i];
                if (node != null && string.Equals(FbxNameUtility.NormalizePath(node.Path), normalized, StringComparison.Ordinal))
                {
                    return node;
                }
            }

            return null;
        }

        /// <summary>
        /// Returns true when <paramref name="path"/> is present in <see cref="Nodes"/>.
        /// </summary>
        public bool HasNode(string path) => GetNode(path) != null;

        /// <summary>
        /// Looks up a read-only Source rest calibration anchor by normalized scene-root-relative path.
        /// </summary>
        public SkeletonAnchorRecord GetAnchor(string path)
        {
            string normalized = FbxNameUtility.NormalizePath(path);
            if (string.IsNullOrEmpty(normalized) || Anchors == null)
            {
                return null;
            }

            for (int i = 0; i < Anchors.Count; i++)
            {
                var anchor = Anchors[i];
                if (anchor != null && string.Equals(FbxNameUtility.NormalizePath(anchor.Path), normalized, StringComparison.Ordinal))
                {
                    return anchor;
                }
            }

            return null;
        }

        /// <summary>
        /// Resolves the Source rest global matrix (<c>S_b</c>) for <paramref name="path"/>, checking
        /// authored <see cref="Nodes"/> first and read-only <see cref="Anchors"/> second.
        /// </summary>
        public bool TryGetSourceRestGlobal(string path, out FbxMatrix4x4 restGlobal)
        {
            var node = GetNode(path);
            if (node != null)
            {
                restGlobal = node.RestGlobal;
                return true;
            }

            var anchor = GetAnchor(path);
            if (anchor != null)
            {
                restGlobal = anchor.RestGlobal;
                return true;
            }

            restGlobal = FbxMatrix4x4.Identity;
            return false;
        }

        /// <summary>
        /// Enumerates authored Upsert node paths in deterministic parent-before-child hierarchy order.
        /// </summary>
        public List<string> GetNodePathsInHierarchyOrder()
        {
            var result = new List<string>();
            if (Nodes == null || Nodes.Count == 0)
            {
                return result;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < Nodes.Count; i++)
            {
                var node = Nodes[i];
                if (node == null)
                {
                    continue;
                }

                string normalized = FbxNameUtility.NormalizePath(node.Path);
                if (!string.IsNullOrEmpty(normalized) && seen.Add(normalized))
                {
                    result.Add(normalized);
                }
            }

            result.Sort(FbxNameUtility.CompareHierarchyOrder);
            return result;
        }
    }

    /// <summary>
    /// Authored node Upsert record in <see cref="SkeletonDocument"/>.
    /// If a node at <see cref="Path"/> exists in Target and <see cref="HasBaseRest"/> is true,
    /// its rest transform is updated using <see cref="BaseRestGlobal"/> calibration; if absent in Target,
    /// the node is created under its parent frame using <see cref="RestLocal"/>.
    /// </summary>
    [BlendShareRecord(SchemaVersion = 1)]
    public sealed class SkeletonNodeRecord
    {
        [BlendShareField(1)] public string Path = string.Empty;
        [BlendShareField(2)] public SkeletonNodeKind Kind = SkeletonNodeKind.Bone;
        [BlendShareField(3)] public FbxMatrix4x4 RestGlobal = FbxMatrix4x4.Identity;
        [BlendShareField(4)] public FbxMatrix4x4 RestLocal = FbxMatrix4x4.Identity;
        [BlendShareField(5)] public bool HasBaseRest;
        [BlendShareField(6)] public FbxMatrix4x4 BaseRestGlobal = FbxMatrix4x4.Identity;
        [BlendShareField(7)] public SkeletonRawTransform Raw;

        /// <summary>Normalized parent path derived directly from <see cref="Path"/>.</summary>
        public string ParentPath => FbxNameUtility.GetParentPath(Path);

        /// <summary>
        /// Compares authored transform data against <paramref name="other"/> within specified tolerances.
        /// </summary>
        public bool ApproximatelyTransform(
            SkeletonNodeRecord other,
            double linearTolerance = 1e-6,
            double translationTolerance = 1e-6)
        {
            if (other == null || Kind != other.Kind || HasBaseRest != other.HasBaseRest)
            {
                return false;
            }

            if (!MatricesApproximatelyEqual(RestLocal, other.RestLocal, linearTolerance, translationTolerance) ||
                !MatricesApproximatelyEqual(RestGlobal, other.RestGlobal, linearTolerance, translationTolerance))
            {
                return false;
            }

            if (HasBaseRest &&
                !MatricesApproximatelyEqual(BaseRestGlobal, other.BaseRestGlobal, linearTolerance, translationTolerance))
            {
                return false;
            }

            if (Raw != null && other.Raw != null)
            {
                return Raw.ApproximatelyEquals(other.Raw, linearTolerance, translationTolerance);
            }

            return true;
        }

        internal static bool MatricesApproximatelyEqual(
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

    /// <summary>
    /// Optional lossless FBX node transform channels captured when Origin and Source share exact native space
    /// and the Source node's live scene transform equals its skin-reconstructed rest transform.
    /// </summary>
    [BlendShareRecord(SchemaVersion = 1)]
    public sealed class SkeletonRawTransform
    {
        [BlendShareField(1)] public Vector3d LclTranslation = Vector3d.zero;
        [BlendShareField(2)] public Vector3d LclRotation = Vector3d.zero;
        [BlendShareField(3)] public Vector3d LclScaling = Vector3d.one;
        [BlendShareField(4)] public FbxRotationOrder RotationOrder = FbxRotationOrder.XYZ;
        [BlendShareField(5)] public FbxTransformInheritMode InheritMode = FbxTransformInheritMode.RSrs;
        [BlendShareField(6)] public bool RotationActive = true;
        [BlendShareField(7)] public Vector3d PreRotation = Vector3d.zero;
        [BlendShareField(8)] public Vector3d PostRotation = Vector3d.zero;
        [BlendShareField(9)] public Vector3d RotationPivot = Vector3d.zero;
        [BlendShareField(10)] public Vector3d ScalingPivot = Vector3d.zero;
        [BlendShareField(11)] public Vector3d RotationOffset = Vector3d.zero;
        [BlendShareField(12)] public Vector3d ScalingOffset = Vector3d.zero;
        [BlendShareField(13)] public FbxMatrix4x4 RawNodeToParentMatrix = FbxMatrix4x4.Identity;

        /// <summary>
        /// Captures raw authored FBX node channels from <paramref name="rawTransform"/> and <paramref name="rawNodeToParentMatrix"/>.
        /// </summary>
        public static SkeletonRawTransform FromFbxRawTransform(
            FbxNodeRawTransform rawTransform,
            FbxMatrix4x4 rawNodeToParentMatrix)
        {
            return new SkeletonRawTransform
            {
                LclTranslation = rawTransform.LclTranslation,
                LclRotation = rawTransform.LclRotation,
                LclScaling = rawTransform.LclScaling,
                RotationOrder = rawTransform.RotationOrder,
                InheritMode = rawTransform.OriginalInheritMode,
                RotationActive = rawTransform.RotationActive,
                PreRotation = rawTransform.PreRotation,
                PostRotation = rawTransform.PostRotation,
                RotationPivot = rawTransform.RotationPivot,
                ScalingPivot = rawTransform.ScalingPivot,
                RotationOffset = rawTransform.RotationOffset,
                ScalingOffset = rawTransform.ScalingOffset,
                RawNodeToParentMatrix = rawNodeToParentMatrix
            };
        }

        /// <summary>
        /// Compares authored raw FBX channels within specified tolerances.
        /// </summary>
        public bool ApproximatelyEquals(
            SkeletonRawTransform other,
            double linearTolerance = 1e-6,
            double translationTolerance = 1e-6)
        {
            if (other == null)
            {
                return false;
            }

            return RotationOrder == other.RotationOrder &&
                   InheritMode == other.InheritMode &&
                   RotationActive == other.RotationActive &&
                   SkeletonNodeRecord.MatricesApproximatelyEqual(RawNodeToParentMatrix, other.RawNodeToParentMatrix, linearTolerance, translationTolerance) &&
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

        private static bool VectorsApproximatelyEqual(Vector3d a, Vector3d b, double tolerance)
        {
            return Math.Abs(a.x - b.x) <= tolerance &&
                   Math.Abs(a.y - b.y) <= tolerance &&
                   Math.Abs(a.z - b.z) <= tolerance;
        }
    }

    /// <summary>
    /// Read-only Source rest frame (<c>S_a</c>) captured for untouched bones and parent chains so
    /// post-edit endpoint frames (<c>D_frame(b) = Lin(S_b) * Lin(H) * Lin(R'_b)^-1</c>) calibrate
    /// cluster bind poses and newly created child bones without mutating untouched Target nodes.
    /// </summary>
    [BlendShareRecord(SchemaVersion = 1)]
    public sealed class SkeletonAnchorRecord
    {
        [BlendShareField(1)] public string Path = string.Empty;
        [BlendShareField(2)] public FbxMatrix4x4 RestGlobal = FbxMatrix4x4.Identity;
    }
}
