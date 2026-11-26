using System;
using System.Collections.Generic;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Model.Documents;

namespace Triturbo.BlendShare.Model.Validation
{
    /// <summary>
    /// Semantic validator for patch-level <see cref="SkeletonDocument"/> records on both container write and read paths.
    /// </summary>
    public static class SkeletonDocumentValidator
    {
        /// <summary>
        /// Validates <paramref name="skeleton"/> and appends any errors to <paramref name="result"/>.
        /// A <c>null</c> <paramref name="skeleton"/> is valid (patches without skeleton data omit the section).
        /// </summary>
        public static FeatureValidationResult Validate(SkeletonDocument skeleton, FeatureValidationResult result = null)
        {
            result ??= new FeatureValidationResult();
            if (skeleton == null)
            {
                return result;
            }

            var seenNodes = new HashSet<string>(StringComparer.Ordinal);
            if (skeleton.Nodes != null)
            {
                for (int i = 0; i < skeleton.Nodes.Count; i++)
                {
                    var node = skeleton.Nodes[i];
                    if (node == null)
                    {
                        result.AddError("SKEL_VAL_NULL_NODE", $"Skeleton node at index {i} is null.");
                        continue;
                    }

                    string normalized = FbxNameUtility.NormalizePath(node.Path);
                    if (string.IsNullOrEmpty(normalized))
                    {
                        result.AddError("SKEL_VAL_EMPTY_NODE_PATH", $"Skeleton node at index {i} has an empty path.");
                        continue;
                    }

                    if (!seenNodes.Add(normalized))
                    {
                        result.AddError("SKEL_VAL_DUP_NODE_PATH", $"Duplicate skeleton node path '{node.Path}'.");
                        continue;
                    }

                    if (!Enum.IsDefined(typeof(SkeletonNodeKind), node.Kind))
                    {
                        result.AddError("SKEL_VAL_INVALID_NODE_KIND", $"Skeleton node '{normalized}' has undefined kind '{(int)node.Kind}'.");
                    }

                    if (!IsValidAffineMatrix(node.RestGlobal) || !IsValidAffineMatrix(node.RestLocal))
                    {
                        result.AddError("SKEL_VAL_INVALID_NODE_MATRIX", $"Skeleton node '{normalized}' has non-finite, projective, or singular RestGlobal/RestLocal matrices.");
                    }

                    if (node.HasBaseRest && !IsValidAffineMatrix(node.BaseRestGlobal))
                    {
                        result.AddError("SKEL_VAL_INVALID_BASE_MATRIX", $"Skeleton node '{normalized}' has HasBaseRest=true with an invalid BaseRestGlobal matrix.");
                    }

                    if (node.Raw != null && !IsValidRawTransform(node.Raw))
                    {
                        result.AddError("SKEL_VAL_INVALID_RAW_TRANSFORM", $"Skeleton node '{normalized}' has non-finite, zero-scale, or invalid Raw FBX transform properties.");
                    }
                }
            }

            if (skeleton.Anchors != null)
            {
                var seenAnchors = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < skeleton.Anchors.Count; i++)
                {
                    var anchor = skeleton.Anchors[i];
                    if (anchor == null)
                    {
                        result.AddError("SKEL_VAL_NULL_ANCHOR", $"Skeleton anchor at index {i} is null.");
                        continue;
                    }

                    string normalized = FbxNameUtility.NormalizePath(anchor.Path);
                    if (string.IsNullOrEmpty(normalized))
                    {
                        result.AddError("SKEL_VAL_EMPTY_ANCHOR_PATH", $"Skeleton anchor at index {i} has an empty path.");
                        continue;
                    }

                    if (seenNodes.Contains(normalized))
                    {
                        result.AddError("SKEL_VAL_OVERLAPPING_PATH", $"Skeleton path '{normalized}' cannot appear in both Nodes and Anchors.");
                        continue;
                    }

                    if (!seenAnchors.Add(normalized))
                    {
                        result.AddError("SKEL_VAL_DUP_ANCHOR_PATH", $"Duplicate skeleton anchor path '{anchor.Path}'.");
                        continue;
                    }

                    if (!IsValidAffineMatrix(anchor.RestGlobal))
                    {
                        result.AddError("SKEL_VAL_INVALID_ANCHOR_MATRIX", $"Skeleton anchor '{normalized}' has non-finite, projective, or singular RestGlobal matrix.");
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Checks whether <paramref name="matrix"/> is a finite, non-projective, invertible 3D affine transform.
        /// </summary>
        public static bool IsValidAffineMatrix(in FbxMatrix4x4 matrix)
        {
            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++)
                {
                    double v = matrix[r, c];
                    if (double.IsNaN(v) || double.IsInfinity(v))
                    {
                        return false;
                    }
                }
            }

            if (Math.Abs(matrix[0, 3]) > 1e-9d ||
                Math.Abs(matrix[1, 3]) > 1e-9d ||
                Math.Abs(matrix[2, 3]) > 1e-9d ||
                Math.Abs(matrix[3, 3] - 1d) > 1e-9d)
            {
                return false;
            }

            double det = FbxLinearAlgebra3d.Determinant3x3(matrix);
            return !double.IsNaN(det) && !double.IsInfinity(det) && Math.Abs(det) > 1e-18d;
        }

        private static bool IsValidRawTransform(SkeletonRawTransform raw)
        {
            if (!Enum.IsDefined(typeof(FbxRotationOrder), raw.RotationOrder) ||
                !Enum.IsDefined(typeof(FbxTransformInheritMode), raw.InheritMode))
            {
                return false;
            }

            if (!IsFinite(raw.LclTranslation) ||
                !IsFinite(raw.LclRotation) ||
                !IsFinite(raw.LclScaling) ||
                !IsFinite(raw.PreRotation) ||
                !IsFinite(raw.PostRotation) ||
                !IsFinite(raw.RotationPivot) ||
                !IsFinite(raw.ScalingPivot) ||
                !IsFinite(raw.RotationOffset) ||
                !IsFinite(raw.ScalingOffset))
            {
                return false;
            }

            if (Math.Abs(raw.LclScaling.x) <= 1e-10d ||
                Math.Abs(raw.LclScaling.y) <= 1e-10d ||
                Math.Abs(raw.LclScaling.z) <= 1e-10d)
            {
                return false;
            }

            return IsValidAffineMatrix(raw.RawNodeToParentMatrix);
        }

        private static bool IsFinite(Vector3d v)
        {
            return !double.IsNaN(v.x) && !double.IsInfinity(v.x) &&
                   !double.IsNaN(v.y) && !double.IsInfinity(v.y) &&
                   !double.IsNaN(v.z) && !double.IsInfinity(v.z);
        }
    }
}
