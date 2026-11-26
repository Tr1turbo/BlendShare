using System;

namespace Triturbo.BlendShare.Fbx
{
    /// <summary>
    /// Precomputed binary64 affine transform encapsulating point, linear vector,
    /// inverse-transpose normal / normal-delta, and skin bind-matrix conversion rules.
    /// Rejects singular, non-finite, or projective (non-affine) matrices.
    /// </summary>
    public readonly struct FbxAffineSpaceTransform
    {
        private const double k_AffineColumnEpsilon = 1e-10d;

        public static readonly FbxAffineSpaceTransform Identity =
            new FbxAffineSpaceTransform(FbxMatrix4x4.Identity, FbxMatrix4x4.Identity, true);

        public FbxMatrix4x4 Offset { get; }
        public FbxMatrix4x4 InverseOffset { get; }
        public bool IsIdentity { get; }
        public bool IsValid { get; }

        private FbxAffineSpaceTransform(FbxMatrix4x4 offset, FbxMatrix4x4 inverseOffset, bool isIdentity)
        {
            Offset = offset;
            InverseOffset = inverseOffset;
            IsIdentity = isIdentity;
            IsValid = true;
        }

        /// <summary>
        /// Creates an invertible affine <see cref="FbxAffineSpaceTransform"/> from a forward row-vector matrix.
        /// Returns false when the matrix is singular, non-finite, or has projective terms in column 3.
        /// </summary>
        public static bool TryCreate(FbxMatrix4x4 offset, out FbxAffineSpaceTransform transform)
        {
            if (!IsFinite(offset) || !IsAffineRowVectorMatrix(offset))
            {
                transform = default;
                return false;
            }

            if (IsExactIdentity(offset))
            {
                transform = Identity;
                return true;
            }

            if (!offset.TryInverse(out var inverse) || !IsFinite(inverse))
            {
                transform = default;
                return false;
            }

            transform = new FbxAffineSpaceTransform(offset, inverse, false);
            return true;
        }

        /// <summary>Transforms a control-point position including translation.</summary>
        public Vector3d TransformPoint(Vector3d point)
        {
            return IsIdentity ? point : Offset.TransformPoint(point);
        }

        /// <summary>Transforms a displacement vector using only the linear part (no translation).</summary>
        public Vector3d TransformVector(Vector3d vector)
        {
            return IsIdentity ? vector : Offset.TransformVector(vector);
        }

        /// <summary>Transforms a base normal using the inverse-transpose linear transform.</summary>
        public Vector3d TransformNormal(Vector3d normal)
        {
            if (!IsFiniteVector(normal))
            {
                return new Vector3d(double.NaN, double.NaN, double.NaN);
            }

            return IsIdentity ? normal : InverseOffset.TransformNormalWithInverse(normal);
        }

        /// <summary>
        /// Transforms a blendshape normal delta relative to its untransformed base normal so
        /// non-uniform scaling preserves the deformed unit normal direction.
        /// Non-finite <paramref name="normalDelta"/> inputs are preserved as NaN rather than masked into finite vectors.
        /// </summary>
        public Vector3d TransformNormalDelta(Vector3d baseNormal, Vector3d normalDelta)
        {
            if (!IsFiniteVector(normalDelta))
            {
                return new Vector3d(double.NaN, double.NaN, double.NaN);
            }

            if (IsIdentity)
            {
                return normalDelta;
            }

            bool hasValidBase = IsFiniteVector(baseNormal) && baseNormal.sqrMagnitude > Vector3d.Epsilon;
            if (!hasValidBase)
            {
                double magnitude = normalDelta.magnitude;
                if (magnitude <= Vector3d.Epsilon)
                {
                    return Vector3d.zero;
                }

                return InverseOffset.TransformNormalWithInverse(normalDelta) * magnitude;
            }

            Vector3d deformed = baseNormal + normalDelta;
            if (!IsFiniteVector(deformed))
            {
                return new Vector3d(double.NaN, double.NaN, double.NaN);
            }

            return InverseOffset.TransformNormalWithInverse(deformed) -
                   InverseOffset.TransformNormalWithInverse(baseNormal);
        }

        /// <summary>Corrects a source skin cluster's mesh-bind-world matrix for the forward transform.</summary>
        public FbxMatrix4x4 TransformMeshBindWorld(FbxMatrix4x4 meshBindWorld)
        {
            return IsIdentity ? meshBindWorld : InverseOffset * meshBindWorld;
        }

        /// <summary>
        /// Exact IEEE-754 identity test (zero tolerance; returns false for any non-zero perturbation or NaN).
        /// </summary>
        public static bool IsExactIdentity(FbxMatrix4x4 matrix)
        {
            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++)
                {
                    double expected = r == c ? 1d : 0d;
                    if (!(matrix[r, c] == expected))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Checks whether <paramref name="matrix"/> is a finite, non-projective, invertible 3D affine row-vector matrix.
        /// </summary>
        public static bool IsInvertibleAffine(in FbxMatrix4x4 matrix)
        {
            if (!IsFinite(matrix) ||
                Math.Abs(matrix[0, 3]) > 1e-9d ||
                Math.Abs(matrix[1, 3]) > 1e-9d ||
                Math.Abs(matrix[2, 3]) > 1e-9d ||
                Math.Abs(matrix[3, 3] - 1d) > 1e-9d)
            {
                return false;
            }

            double det = FbxLinearAlgebra3d.Determinant3x3(matrix);
            return !double.IsNaN(det) && !double.IsInfinity(det) && Math.Abs(det) > 1e-18d;
        }

        private static bool IsAffineRowVectorMatrix(FbxMatrix4x4 matrix)
        {
            return Math.Abs(matrix[0, 3]) <= k_AffineColumnEpsilon &&
                   Math.Abs(matrix[1, 3]) <= k_AffineColumnEpsilon &&
                   Math.Abs(matrix[2, 3]) <= k_AffineColumnEpsilon &&
                   Math.Abs(matrix[3, 3] - 1d) <= k_AffineColumnEpsilon;
        }

        private static bool IsFinite(FbxMatrix4x4 matrix)
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

            return true;
        }

        private static bool IsFiniteVector(Vector3d v)
        {
            return !double.IsNaN(v.x) && !double.IsInfinity(v.x) &&
                   !double.IsNaN(v.y) && !double.IsInfinity(v.y) &&
                   !double.IsNaN(v.z) && !double.IsInfinity(v.z);
        }
    }
}
