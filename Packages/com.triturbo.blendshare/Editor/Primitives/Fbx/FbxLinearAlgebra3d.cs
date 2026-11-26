using System;

namespace Triturbo.BlendShare.Fbx
{
    /// <summary>
    /// Exact binary64 3x3 linear-algebra utilities (symmetric Jacobi eigen-solver, 3x3 SVD,
    /// maximum singular value norm, and orthogonal/similarity decomposition) for row-vector matrices.
    /// Zero external dependencies.
    /// </summary>
    public static class FbxLinearAlgebra3d
    {
        private const int k_MaxJacobiSweeps = 32;
        private const double k_JacobiOffDiagEpsilon = 1e-15d;

        /// <summary>
        /// Extracts the linear 3x3 upper-left block of <paramref name="matrix"/> with zero translation and homogeneous 1.
        /// </summary>
        public static FbxMatrix4x4 ExtractLinear(FbxMatrix4x4 matrix)
        {
            return new FbxMatrix4x4(
                matrix[0, 0], matrix[0, 1], matrix[0, 2], 0d,
                matrix[1, 0], matrix[1, 1], matrix[1, 2], 0d,
                matrix[2, 0], matrix[2, 1], matrix[2, 2], 0d,
                0d, 0d, 0d, 1d);
        }

        /// <summary>
        /// Computes the signed 3x3 determinant of the upper-left linear block of <paramref name="matrix"/>.
        /// </summary>
        public static double Determinant3x3(FbxMatrix4x4 matrix)
        {
            return matrix[0, 0] * (matrix[1, 1] * matrix[2, 2] - matrix[1, 2] * matrix[2, 1]) -
                   matrix[0, 1] * (matrix[1, 0] * matrix[2, 2] - matrix[1, 2] * matrix[2, 0]) +
                   matrix[0, 2] * (matrix[1, 0] * matrix[2, 1] - matrix[1, 1] * matrix[2, 0]);
        }

        /// <summary>
        /// Computes the spectral norm (<c>sigma_max</c>, largest singular value) of the 3x3 linear block of <paramref name="matrix"/>.
        /// Returns 0 for non-finite or zero matrices.
        /// </summary>
        public static double ComputeMaxSingularValue(FbxMatrix4x4 matrix)
        {
            if (!TrySvd3x3(matrix, out _, out var sigma, out _))
            {
                return 0d;
            }

            return sigma.x;
        }

        /// <summary>
        /// Computes the 3x3 Singular Value Decomposition <c>A = U * diag(sigma.x, sigma.y, sigma.z) * V^T</c>
        /// of the upper-left 3x3 block of <paramref name="matrix"/>, with descending singular values
        /// <c>sigma.x &gt;= sigma.y &gt;= sigma.z &gt;= 0</c> and proper orthogonal <c>U, V</c> (<c>det(U) = +1, det(V) = +1</c> when <c>det(A) &gt;= 0</c>,
        /// or <c>U</c> carrying any reflection when needed so <c>U * Sigma * V^T == A</c> identically).
        /// </summary>
        public static bool TrySvd3x3(
            FbxMatrix4x4 matrix,
            out FbxMatrix4x4 u,
            out Vector3d sigma,
            out FbxMatrix4x4 v)
        {
            u = FbxMatrix4x4.Identity;
            v = FbxMatrix4x4.Identity;
            sigma = Vector3d.zero;

            for (int r = 0; r < 3; r++)
            {
                for (int c = 0; c < 3; c++)
                {
                    double entry = matrix[r, c];
                    if (double.IsNaN(entry) || double.IsInfinity(entry))
                    {
                        return false;
                    }
                }
            }

            // Scale A by max abs entry to prevent overflow/underflow when forming A^T * A.
            double maxAbs = 0d;
            for (int r = 0; r < 3; r++)
            {
                for (int c = 0; c < 3; c++)
                {
                    double abs = Math.Abs(matrix[r, c]);
                    if (abs > maxAbs) maxAbs = abs;
                }
            }

            if (maxAbs <= 0d)
            {
                return true;
            }

            double invScale = 1d / maxAbs;
            var a = new double[3, 3];
            for (int r = 0; r < 3; r++)
            {
                for (int c = 0; c < 3; c++)
                {
                    a[r, c] = matrix[r, c] * invScale;
                }
            }

            // Symmetric positive semi-definite Gram matrix G = A^T * A.
            var gram = new double[3, 3];
            for (int i = 0; i < 3; i++)
            {
                for (int j = i; j < 3; j++)
                {
                    double sum = a[0, i] * a[0, j] + a[1, i] * a[1, j] + a[2, i] * a[2, j];
                    gram[i, j] = sum;
                    gram[j, i] = sum;
                }
            }

            var vMat = new double[,]
            {
                { 1d, 0d, 0d },
                { 0d, 1d, 0d },
                { 0d, 0d, 1d }
            };

            for (int sweep = 0; sweep < k_MaxJacobiSweeps; sweep++)
            {
                double offDiag = Math.Abs(gram[0, 1]) + Math.Abs(gram[0, 2]) + Math.Abs(gram[1, 2]);
                if (offDiag <= k_JacobiOffDiagEpsilon)
                {
                    break;
                }

                JacobiRotate(gram, vMat, 0, 1);
                JacobiRotate(gram, vMat, 0, 2);
                JacobiRotate(gram, vMat, 1, 2);
            }

            double[] eig =
            {
                gram[0, 0] > 0d ? Math.Sqrt(gram[0, 0]) : 0d,
                gram[1, 1] > 0d ? Math.Sqrt(gram[1, 1]) : 0d,
                gram[2, 2] > 0d ? Math.Sqrt(gram[2, 2]) : 0d
            };

            // Sort singular values descending and permute columns of V accordingly.
            int[] order = { 0, 1, 2 };
            if (eig[order[1]] > eig[order[0]]) (order[0], order[1]) = (order[1], order[0]);
            if (eig[order[2]] > eig[order[1]]) (order[1], order[2]) = (order[2], order[1]);
            if (eig[order[1]] > eig[order[0]]) (order[0], order[1]) = (order[1], order[0]);

            var vSorted = new double[3, 3];
            var sSorted = new double[3];
            for (int col = 0; col < 3; col++)
            {
                int srcCol = order[col];
                sSorted[col] = eig[srcCol];
                for (int row = 0; row < 3; row++)
                {
                    vSorted[row, col] = vMat[row, srcCol];
                }
            }

            // Ensure det(V) = +1 so V is always in SO(3).
            if (Det3(vSorted) < 0d)
            {
                vSorted[0, 2] = -vSorted[0, 2];
                vSorted[1, 2] = -vSorted[1, 2];
                vSorted[2, 2] = -vSorted[2, 2];
            }

            // Construct orthonormal columns of U from A * V_col / s_col, completing deficient columns via Gram-Schmidt.
            var bCols = new Vector3d[3];
            for (int col = 0; col < 3; col++)
            {
                bCols[col] = new Vector3d(
                    a[0, 0] * vSorted[0, col] + a[0, 1] * vSorted[1, col] + a[0, 2] * vSorted[2, col],
                    a[1, 0] * vSorted[0, col] + a[1, 1] * vSorted[1, col] + a[1, 2] * vSorted[2, col],
                    a[2, 0] * vSorted[0, col] + a[2, 1] * vSorted[1, col] + a[2, 2] * vSorted[2, col]);
            }

            const double k_RankEps = 1e-12d;
            Vector3d u0 = sSorted[0] > k_RankEps ? (bCols[0] / sSorted[0]).normalized : new Vector3d(1d, 0d, 0d);
            if (u0.sqrMagnitude <= 0.5d)
            {
                u0 = new Vector3d(1d, 0d, 0d);
            }

            Vector3d u1;
            if (sSorted[1] > k_RankEps)
            {
                Vector3d cand = bCols[1] / sSorted[1];
                cand -= u0 * Vector3d.Dot(cand, u0);
                u1 = cand.sqrMagnitude > 1e-20d ? cand.normalized : PickPerpendicularUnit(u0);
            }
            else
            {
                u1 = PickPerpendicularUnit(u0);
            }

            Vector3d cross01 = Vector3d.Cross(u0, u1).normalized;
            Vector3d u2;
            if (sSorted[2] > k_RankEps)
            {
                // Preserve exact sign of B_2 along cross01 so det(U * V^T) matches sign(det(A)) when full-rank!
                u2 = Vector3d.Dot(bCols[2], cross01) >= 0d ? cross01 : cross01 * -1d;
            }
            else
            {
                // Deficient rank: keep det(U) = +1 so U * V^T is in SO(3) before caller applies any parity prior.
                u2 = cross01;
            }

            u = new FbxMatrix4x4(
                u0.x, u1.x, u2.x, 0d,
                u0.y, u1.y, u2.y, 0d,
                u0.z, u1.z, u2.z, 0d,
                0d, 0d, 0d, 1d);
            v = new FbxMatrix4x4(
                vSorted[0, 0], vSorted[0, 1], vSorted[0, 2], 0d,
                vSorted[1, 0], vSorted[1, 1], vSorted[1, 2], 0d,
                vSorted[2, 0], vSorted[2, 1], vSorted[2, 2], 0d,
                0d, 0d, 0d, 1d);
            sigma = new Vector3d(sSorted[0] * maxAbs, sSorted[1] * maxAbs, sSorted[2] * maxAbs);
            return true;
        }

        /// <summary>
        /// Checks whether the linear 3x3 block of <paramref name="matrix"/> is an orthogonal similarity
        /// (<c>s * Q</c> with <c>s &gt; 0</c> and <c>Q^T * Q = I</c>, <c>det(Q) = +-1</c>) within <paramref name="tolerance"/>.
        /// </summary>
        public static bool TryExtractOrthogonalSimilarity(
            FbxMatrix4x4 matrix,
            out double scale,
            out FbxMatrix4x4 orthogonal,
            double tolerance = 1e-5d)
        {
            scale = 0d;
            orthogonal = FbxMatrix4x4.Identity;

            var r0 = new Vector3d(matrix[0, 0], matrix[0, 1], matrix[0, 2]);
            var r1 = new Vector3d(matrix[1, 0], matrix[1, 1], matrix[1, 2]);
            var r2 = new Vector3d(matrix[2, 0], matrix[2, 1], matrix[2, 2]);
            double s0 = r0.magnitude;
            double s1 = r1.magnitude;
            double s2 = r2.magnitude;
            if (s0 <= Vector3d.Epsilon || s1 <= Vector3d.Epsilon || s2 <= Vector3d.Epsilon ||
                double.IsNaN(s0) || double.IsNaN(s1) || double.IsNaN(s2) ||
                double.IsInfinity(s0) || double.IsInfinity(s1) || double.IsInfinity(s2))
            {
                return false;
            }

            double avg = (s0 + s1 + s2) / 3d;
            double relDiff = Math.Max(Math.Abs(s0 - avg), Math.Max(Math.Abs(s1 - avg), Math.Abs(s2 - avg))) / avg;
            if (relDiff > tolerance)
            {
                return false;
            }

            double invS = 1d / avg;
            var u0 = r0 * invS;
            var u1 = r1 * invS;
            var u2 = r2 * invS;
            if (Math.Abs(Vector3d.Dot(u0, u1)) > tolerance ||
                Math.Abs(Vector3d.Dot(u0, u2)) > tolerance ||
                Math.Abs(Vector3d.Dot(u1, u2)) > tolerance)
            {
                return false;
            }

            scale = avg;
            orthogonal = new FbxMatrix4x4(
                u0.x, u0.y, u0.z, 0d,
                u1.x, u1.y, u1.z, 0d,
                u2.x, u2.y, u2.z, 0d,
                0d, 0d, 0d, 1d);
            return true;
        }

        private static void JacobiRotate(double[,] a, double[,] v, int p, int q)
        {
            double apq = a[p, q];
            if (Math.Abs(apq) <= k_JacobiOffDiagEpsilon)
            {
                return;
            }

            double app = a[p, p];
            double aqq = a[q, q];
            double tau = (aqq - app) / (2d * apq);
            double t = tau >= 0d
                ? 1d / (tau + Math.Sqrt(1d + tau * tau))
                : -1d / (-tau + Math.Sqrt(1d + tau * tau));
            double c = 1d / Math.Sqrt(1d + t * t);
            double s = t * c;

            a[p, p] = app - t * apq;
            a[q, q] = aqq + t * apq;
            a[p, q] = 0d;
            a[q, p] = 0d;

            for (int k = 0; k < 3; k++)
            {
                if (k != p && k != q)
                {
                    double akp = a[k, p];
                    double akq = a[k, q];
                    a[k, p] = c * akp - s * akq;
                    a[p, k] = a[k, p];
                    a[k, q] = s * akp + c * akq;
                    a[q, k] = a[k, q];
                }

                double vkp = v[k, p];
                double vkq = v[k, q];
                v[k, p] = c * vkp - s * vkq;
                v[k, q] = s * vkp + c * vkq;
            }
        }

        private static double Det3(double[,] m)
        {
            return m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1]) -
                   m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0]) +
                   m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);
        }

        private static Vector3d PickPerpendicularUnit(Vector3d axis)
        {
            Vector3d seed = Math.Abs(axis.x) < 0.9d ? new Vector3d(1d, 0d, 0d) : new Vector3d(0d, 1d, 0d);
            return (seed - axis * Vector3d.Dot(seed, axis)).normalized;
        }
    }
}
