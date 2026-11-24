using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Triturbo.BlendShare.Fbx
{
    /// <summary>Defines the axis order used to evaluate FBX Euler rotation properties.</summary>
    public enum FbxRotationOrder
    {
        /// <summary>Xyz.</summary>
        XYZ,
        /// <summary>Xzy.</summary>
        XZY,
        /// <summary>Yzx.</summary>
        YZX,
        /// <summary>Yxz.</summary>
        YXZ,
        /// <summary>Zxy.</summary>
        ZXY,
        /// <summary>Zyx.</summary>
        ZYX,
        /// <summary>Spheric xyz.</summary>
        SphericXYZ
    }

    /// <summary>Defines how an FBX node inherits scaling from its parent.</summary>
    public enum FbxTransformInheritMode
    {
        /// <summary>Inherits scale after applying local rotation.</summary>
        RrSs,
        /// <summary>Applies local scale independently of parent scale.</summary>
        Rrs,
        /// <summary>Applies parent scale before local rotation.</summary>
        RSrs
    }

    /// <summary>A double-precision row-vector transform stored in row-major order.</summary>
    [Serializable, StructLayout(LayoutKind.Sequential)]
    public struct FbxMatrix4x4 : IEquatable<FbxMatrix4x4>
    {
        /// <summary>Identity.</summary>
        public static readonly FbxMatrix4x4 Identity = new FbxMatrix4x4(
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            0, 0, 0, 1);

        [SerializeField] private double m00;
        [SerializeField] private double m01;
        [SerializeField] private double m02;
        [SerializeField] private double m03;
        [SerializeField] private double m10;
        [SerializeField] private double m11;
        [SerializeField] private double m12;
        [SerializeField] private double m13;
        [SerializeField] private double m20;
        [SerializeField] private double m21;
        [SerializeField] private double m22;
        [SerializeField] private double m23;
        [SerializeField] private double m30;
        [SerializeField] private double m31;
        [SerializeField] private double m32;
        [SerializeField] private double m33;

        /// <summary>Creates a new FbxMatrix4x4 from the supplied values.</summary>
        public FbxMatrix4x4(
            double m00, double m01, double m02, double m03,
            double m10, double m11, double m12, double m13,
            double m20, double m21, double m22, double m23,
            double m30, double m31, double m32, double m33)
        {
            this.m00 = m00;
            this.m01 = m01;
            this.m02 = m02;
            this.m03 = m03;
            this.m10 = m10;
            this.m11 = m11;
            this.m12 = m12;
            this.m13 = m13;
            this.m20 = m20;
            this.m21 = m21;
            this.m22 = m22;
            this.m23 = m23;
            this.m30 = m30;
            this.m31 = m31;
            this.m32 = m32;
            this.m33 = m33;
        }

        /// <summary>Gets the matrix component at the zero-based row and column.</summary>
        public double this[int row, int column]
        {
            get
            {
                return (row, column) switch
                {
                    (0, 0) => m00,
                    (0, 1) => m01,
                    (0, 2) => m02,
                    (0, 3) => m03,
                    (1, 0) => m10,
                    (1, 1) => m11,
                    (1, 2) => m12,
                    (1, 3) => m13,
                    (2, 0) => m20,
                    (2, 1) => m21,
                    (2, 2) => m22,
                    (2, 3) => m23,
                    (3, 0) => m30,
                    (3, 1) => m31,
                    (3, 2) => m32,
                    (3, 3) => m33,
                    _ => throw new ArgumentOutOfRangeException()
                };
            }
        }

        /// <summary>Whether this matrix approximately equals the identity.</summary>
        public bool IsIdentity => Approximately(Identity);
        /// <summary>Translation.</summary>
        public Vector3d Translation => new Vector3d(m30, m31, m32);

        /// <summary>Inverse.</summary>
        public FbxMatrix4x4 Inverse
        {
            get
            {
                if (!TryInverse(out var inverse))
                {
                    throw new InvalidOperationException("Matrix is singular and cannot be inverted.");
                }

                return inverse;
            }
        }

        /// <summary>Reads sixteen row-major doubles, returning identity for missing or incomplete input.</summary>
        public static FbxMatrix4x4 FromRowMajor(double[] values)
        {
            if (values == null || values.Length < 16)
            {
                return Identity;
            }

            return new FbxMatrix4x4(
                values[0], values[1], values[2], values[3],
                values[4], values[5], values[6], values[7],
                values[8], values[9], values[10], values[11],
                values[12], values[13], values[14], values[15]);
        }

        /// <summary>Builds a translation matrix.</summary>
        public static FbxMatrix4x4 Translate(Vector3d translation)
        {
            return new FbxMatrix4x4(
                1, 0, 0, 0,
                0, 1, 0, 0,
                0, 0, 1, 0,
                translation.x, translation.y, translation.z, 1);
        }

        /// <summary>Builds a nonuniform scaling matrix.</summary>
        public static FbxMatrix4x4 Scale(Vector3d scale)
        {
            return new FbxMatrix4x4(
                scale.x, 0, 0, 0,
                0, scale.y, 0, 0,
                0, 0, scale.z, 0,
                0, 0, 0, 1);
        }

        /// <summary>Creates a rotation matrix from FBX Euler angles in the specified axis order.</summary>
        public static FbxMatrix4x4 RotateEulerDegrees(
            Vector3d eulerRotation,
            FbxRotationOrder rotationOrder)
        {
            double x = DegreesToRadians(eulerRotation.x);
            double y = DegreesToRadians(eulerRotation.y);
            double z = DegreesToRadians(eulerRotation.z);

            var rx = new FbxMatrix4x4(
                1, 0, 0, 0,
                0, Math.Cos(x), Math.Sin(x), 0,
                0, -Math.Sin(x), Math.Cos(x), 0,
                0, 0, 0, 1);
            var ry = new FbxMatrix4x4(
                Math.Cos(y), 0, -Math.Sin(y), 0,
                0, 1, 0, 0,
                Math.Sin(y), 0, Math.Cos(y), 0,
                0, 0, 0, 1);
            var rz = new FbxMatrix4x4(
                Math.Cos(z), Math.Sin(z), 0, 0,
                -Math.Sin(z), Math.Cos(z), 0, 0,
                0, 0, 1, 0,
                0, 0, 0, 1);

            return rotationOrder switch
            {
                FbxRotationOrder.XYZ => rx * ry * rz,
                FbxRotationOrder.XZY => rx * rz * ry,
                FbxRotationOrder.YZX => ry * rz * rx,
                FbxRotationOrder.YXZ => ry * rx * rz,
                FbxRotationOrder.ZXY => rz * rx * ry,
                FbxRotationOrder.ZYX => rz * ry * rx,
                FbxRotationOrder.SphericXYZ => throw new NotSupportedException(
                    "FBX SphericXYZ rotation cannot be evaluated as an ordinary Euler axis order."),
                _ => throw new ArgumentOutOfRangeException(nameof(rotationOrder), rotationOrder, null)
            };
        }

        /// <summary>Builds a row-vector rotation matrix from a quaternion.</summary>
        public static FbxMatrix4x4 RotateQuaternion(Quaterniond rotation)
        {
            var q = rotation.normalized;
            double x = q.x;
            double y = q.y;
            double z = q.z;
            double w = q.w;
            double xx = x * x;
            double yy = y * y;
            double zz = z * z;
            double xy = x * y;
            double xz = x * z;
            double yz = y * z;
            double wx = w * x;
            double wy = w * y;
            double wz = w * z;

            return new FbxMatrix4x4(
                1d - 2d * (yy + zz), 2d * (xy + wz), 2d * (xz - wy), 0d,
                2d * (xy - wz), 1d - 2d * (xx + zz), 2d * (yz + wx), 0d,
                2d * (xz + wy), 2d * (yz - wx), 1d - 2d * (xx + yy), 0d,
                0d, 0d, 0d, 1d);
        }

        /// <summary>Creates a TRS matrix from FBX Euler angles in the specified axis order.</summary>
        public static FbxMatrix4x4 FromTranslationRotationScale(
            Vector3d translation,
            Vector3d eulerRotation,
            Vector3d scale,
            FbxRotationOrder rotationOrder)
        {
            return Scale(scale) * RotateEulerDegrees(eulerRotation, rotationOrder) * Translate(translation);
        }

        /// <summary>Composes scale, rotation and translation into a row-vector matrix.</summary>
        public static FbxMatrix4x4 FromTranslationRotationScale(
            Vector3d translation,
            Quaterniond rotation,
            Vector3d scale)
        {
            return Scale(scale) * RotateQuaternion(rotation) * Translate(translation);
        }

        /// <summary>Transforms and normalizes a normal using this matrix as the already-computed inverse of the forward transform.</summary>
        public Vector3d TransformNormalWithInverse(Vector3d normal)
        {
            var transformed = new Vector3d(
                normal.x * m00 + normal.y * m01 + normal.z * m02,
                normal.x * m10 + normal.y * m11 + normal.z * m12,
                normal.x * m20 + normal.y * m21 + normal.z * m22);
            return transformed.normalized;
        }

        /// <summary>Applies the affine transform to a row-vector position, including translation.</summary>
        public Vector3d TransformPoint(Vector3d point)
        {
            return new Vector3d(
                point.x * m00 + point.y * m10 + point.z * m20 + m30,
                point.x * m01 + point.y * m11 + point.z * m21 + m31,
                point.x * m02 + point.y * m12 + point.z * m22 + m32);
        }

        /// <summary>Applies the linear part of the transform to a row vector.</summary>
        public Vector3d TransformVector(Vector3d vector)
        {
            return new Vector3d(
                vector.x * m00 + vector.y * m10 + vector.z * m20,
                vector.x * m01 + vector.y * m11 + vector.z * m21,
                vector.x * m02 + vector.y * m12 + vector.z * m22);
        }

        /// <summary>Transforms a direction and restores its input length when both lengths exceed the normalization tolerance.</summary>
        public Vector3d TransformDirectionPreservingMagnitude(Vector3d vector)
        {
            var transformed = TransformVector(vector);
            double sourceMagnitude = vector.magnitude;
            double transformedMagnitude = transformed.magnitude;
            return sourceMagnitude > Vector3d.Epsilon && transformedMagnitude > Vector3d.Epsilon
                ? transformed * (sourceMagnitude / transformedMagnitude)
                : transformed;
        }

        /// <summary>Double.</summary>
        public double[] ToRowMajorArray()
        {
            return new[]
            {
                m00, m01, m02, m03,
                m10, m11, m12, m13,
                m20, m21, m22, m23,
                m30, m31, m32, m33
            };
        }

        /// <summary>Returns the matrix with rows and columns exchanged.</summary>
        public FbxMatrix4x4 Transpose()
        {
            return new FbxMatrix4x4(
                m00, m10, m20, m30,
                m01, m11, m21, m31,
                m02, m12, m22, m32,
                m03, m13, m23, m33);
        }

        /// <summary>Transposes and narrows the FBX matrix for Unity column-vector multiplication.</summary>
        public Matrix4x4 ToUnityColumnMatrix()
        {
            var transposed = Transpose();
            var result = new Matrix4x4();
            for (int row = 0; row < 4; row++)
            {
                for (int column = 0; column < 4; column++)
                {
                    result[row, column] = (float)transposed[row, column];
                }
            }

            return result;
        }

        /// <summary>Converts the value to Matrix4x4.</summary>
        public static implicit operator Matrix4x4(FbxMatrix4x4 m) => m.ToUnityColumnMatrix();


        /// <summary>Attempts to invert the matrix, returning false for a singular pivot.</summary>
        public bool TryInverse(out FbxMatrix4x4 inverse)
        {
            var a = new double[4, 8];
            for (int row = 0; row < 4; row++)
            {
                for (int column = 0; column < 4; column++)
                {
                    a[row, column] = this[row, column];
                    a[row, column + 4] = row == column ? 1d : 0d;
                }
            }

            for (int pivot = 0; pivot < 4; pivot++)
            {
                int bestRow = pivot;
                double bestValue = Math.Abs(a[pivot, pivot]);
                for (int row = pivot + 1; row < 4; row++)
                {
                    double value = Math.Abs(a[row, pivot]);
                    if (value > bestValue)
                    {
                        bestValue = value;
                        bestRow = row;
                    }
                }

                if (bestValue <= 1e-12)
                {
                    inverse = Identity;
                    return false;
                }

                if (bestRow != pivot)
                {
                    for (int column = 0; column < 8; column++)
                    {
                        (a[pivot, column], a[bestRow, column]) = (a[bestRow, column], a[pivot, column]);
                    }
                }

                double divisor = a[pivot, pivot];
                for (int column = 0; column < 8; column++)
                {
                    a[pivot, column] /= divisor;
                }

                for (int row = 0; row < 4; row++)
                {
                    if (row == pivot)
                    {
                        continue;
                    }

                    double factor = a[row, pivot];
                    if (factor == 0d)
                    {
                        continue;
                    }

                    for (int column = 0; column < 8; column++)
                    {
                        a[row, column] -= factor * a[pivot, column];
                    }
                }
            }

            inverse = new FbxMatrix4x4(
                a[0, 4], a[0, 5], a[0, 6], a[0, 7],
                a[1, 4], a[1, 5], a[1, 6], a[1, 7],
                a[2, 4], a[2, 5], a[2, 6], a[2, 7],
                a[3, 4], a[3, 5], a[3, 6], a[3, 7]);
            return true;
        }

        /// <summary>Tests equality within the supplied tolerance.</summary>
        public bool Approximately(FbxMatrix4x4 other, double epsilon = 1e-10)
        {
            for (int row = 0; row < 4; row++)
            {
                for (int column = 0; column < 4; column++)
                {
                    if (Math.Abs(this[row, column] - other[row, column]) > epsilon)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        /// <inheritdoc/>
        public bool Equals(FbxMatrix4x4 other)
        {
            return m00.Equals(other.m00) &&
                   m01.Equals(other.m01) &&
                   m02.Equals(other.m02) &&
                   m03.Equals(other.m03) &&
                   m10.Equals(other.m10) &&
                   m11.Equals(other.m11) &&
                   m12.Equals(other.m12) &&
                   m13.Equals(other.m13) &&
                   m20.Equals(other.m20) &&
                   m21.Equals(other.m21) &&
                   m22.Equals(other.m22) &&
                   m23.Equals(other.m23) &&
                   m30.Equals(other.m30) &&
                   m31.Equals(other.m31) &&
                   m32.Equals(other.m32) &&
                   m33.Equals(other.m33);
        }

        /// <inheritdoc/>
        public override bool Equals(object obj)
        {
            return obj is FbxMatrix4x4 matrix && Equals(matrix);
        }

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + m00.GetHashCode();
                hash = hash * 31 + m01.GetHashCode();
                hash = hash * 31 + m02.GetHashCode();
                hash = hash * 31 + m03.GetHashCode();
                hash = hash * 31 + m10.GetHashCode();
                hash = hash * 31 + m11.GetHashCode();
                hash = hash * 31 + m12.GetHashCode();
                hash = hash * 31 + m13.GetHashCode();
                hash = hash * 31 + m20.GetHashCode();
                hash = hash * 31 + m21.GetHashCode();
                hash = hash * 31 + m22.GetHashCode();
                hash = hash * 31 + m23.GetHashCode();
                hash = hash * 31 + m30.GetHashCode();
                hash = hash * 31 + m31.GetHashCode();
                hash = hash * 31 + m32.GetHashCode();
                hash = hash * 31 + m33.GetHashCode();

                return hash;
            }
        }

        /// <summary>Composes the two transforms.</summary>
        public static FbxMatrix4x4 operator *(FbxMatrix4x4 lhs, FbxMatrix4x4 rhs)
        {
            double m00 = lhs.m00 * rhs.m00 + lhs.m01 * rhs.m10 + lhs.m02 * rhs.m20 + lhs.m03 * rhs.m30;
            double m01 = lhs.m00 * rhs.m01 + lhs.m01 * rhs.m11 + lhs.m02 * rhs.m21 + lhs.m03 * rhs.m31;
            double m02 = lhs.m00 * rhs.m02 + lhs.m01 * rhs.m12 + lhs.m02 * rhs.m22 + lhs.m03 * rhs.m32;
            double m03 = lhs.m00 * rhs.m03 + lhs.m01 * rhs.m13 + lhs.m02 * rhs.m23 + lhs.m03 * rhs.m33;
            double m10 = lhs.m10 * rhs.m00 + lhs.m11 * rhs.m10 + lhs.m12 * rhs.m20 + lhs.m13 * rhs.m30;
            double m11 = lhs.m10 * rhs.m01 + lhs.m11 * rhs.m11 + lhs.m12 * rhs.m21 + lhs.m13 * rhs.m31;
            double m12 = lhs.m10 * rhs.m02 + lhs.m11 * rhs.m12 + lhs.m12 * rhs.m22 + lhs.m13 * rhs.m32;
            double m13 = lhs.m10 * rhs.m03 + lhs.m11 * rhs.m13 + lhs.m12 * rhs.m23 + lhs.m13 * rhs.m33;
            double m20 = lhs.m20 * rhs.m00 + lhs.m21 * rhs.m10 + lhs.m22 * rhs.m20 + lhs.m23 * rhs.m30;
            double m21 = lhs.m20 * rhs.m01 + lhs.m21 * rhs.m11 + lhs.m22 * rhs.m21 + lhs.m23 * rhs.m31;
            double m22 = lhs.m20 * rhs.m02 + lhs.m21 * rhs.m12 + lhs.m22 * rhs.m22 + lhs.m23 * rhs.m32;
            double m23 = lhs.m20 * rhs.m03 + lhs.m21 * rhs.m13 + lhs.m22 * rhs.m23 + lhs.m23 * rhs.m33;
            double m30 = lhs.m30 * rhs.m00 + lhs.m31 * rhs.m10 + lhs.m32 * rhs.m20 + lhs.m33 * rhs.m30;
            double m31 = lhs.m30 * rhs.m01 + lhs.m31 * rhs.m11 + lhs.m32 * rhs.m21 + lhs.m33 * rhs.m31;
            double m32 = lhs.m30 * rhs.m02 + lhs.m31 * rhs.m12 + lhs.m32 * rhs.m22 + lhs.m33 * rhs.m32;
            double m33 = lhs.m30 * rhs.m03 + lhs.m31 * rhs.m13 + lhs.m32 * rhs.m23 + lhs.m33 * rhs.m33;

            return new FbxMatrix4x4(
                m00, m01, m02, m03,
                m10, m11, m12, m13,
                m20, m21, m22, m23,
                m30, m31, m32, m33);
        }

        /// <summary>Tests exact component equality.</summary>
        public static bool operator ==(FbxMatrix4x4 lhs, FbxMatrix4x4 rhs)
        {
            return lhs.Equals(rhs);
        }

        /// <summary>Tests component inequality.</summary>
        public static bool operator !=(FbxMatrix4x4 lhs, FbxMatrix4x4 rhs)
        {
            return !lhs.Equals(rhs);
        }

        private static double DegreesToRadians(double degrees)
        {
            return degrees * Math.PI / 180d;
        }
    }

    /// <summary>A double-precision rotation quaternion.</summary>
    [Serializable, StructLayout(LayoutKind.Sequential)]
    public struct Quaterniond : IEquatable<Quaterniond>
    {
        /// <summary>Identity.</summary>
        public static readonly Quaterniond Identity = new Quaterniond(0d, 0d, 0d, 1d);

        [SerializeField] private double m_X;
        [SerializeField] private double m_Y;
        [SerializeField] private double m_Z;
        [SerializeField] private double m_W;

        /// <summary>The X component.</summary>
        public double x => m_X;
        /// <summary>The Y component.</summary>
        public double y => m_Y;
        /// <summary>The Z component.</summary>
        public double z => m_Z;
        /// <summary>The W component.</summary>
        public double w => m_W;

        /// <summary>Creates a new Quaterniond from the supplied values.</summary>
        public Quaterniond(double x, double y, double z, double w)
        {
            m_X = x;
            m_Y = y;
            m_Z = z;
            m_W = w;
        }

        /// <summary>Euclidean length.</summary>
        public double magnitude => Math.Sqrt(x * x + y * y + z * z + w * w);
        /// <summary>Unit-length value, with the default normalization fallback for negligible length.</summary>
        public Quaterniond normalized => magnitude > Vector3d.Epsilon ? this / magnitude : Identity;
        /// <summary>Inverse.</summary>
        public Quaterniond Inverse
        {
            get
            {
                double sqrMagnitude = x * x + y * y + z * z + w * w;
                return sqrMagnitude > Vector3d.Epsilon
                    ? new Quaterniond(-x / sqrMagnitude, -y / sqrMagnitude, -z / sqrMagnitude, w / sqrMagnitude)
                    : Identity;
            }
        }

        /// <inheritdoc/>
        public bool Equals(Quaterniond other)
        {
            return x == other.x && y == other.y && z == other.z && w == other.w;
        }

        /// <inheritdoc/>
        public override bool Equals(object obj)
        {
            return obj is Quaterniond other && Equals(other);
        }

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + x.GetHashCode();
                hash = hash * 31 + y.GetHashCode();
                hash = hash * 31 + z.GetHashCode();
                hash = hash * 31 + w.GetHashCode();
                return hash;
            }
        }

        /// <summary>Composes the two transforms.</summary>
        public static Quaterniond operator *(Quaterniond lhs, Quaterniond rhs)
        {
            return new Quaterniond(
                lhs.w * rhs.x + lhs.x * rhs.w + lhs.y * rhs.z - lhs.z * rhs.y,
                lhs.w * rhs.y - lhs.x * rhs.z + lhs.y * rhs.w + lhs.z * rhs.x,
                lhs.w * rhs.z + lhs.x * rhs.y - lhs.y * rhs.x + lhs.z * rhs.w,
                lhs.w * rhs.w - lhs.x * rhs.x - lhs.y * rhs.y - lhs.z * rhs.z);
        }

        /// <summary>Divides each component by a scalar.</summary>
        public static Quaterniond operator /(Quaterniond quaternion, double scalar)
        {
            return new Quaterniond(quaternion.x / scalar, quaternion.y / scalar, quaternion.z / scalar, quaternion.w / scalar);
        }
    }
}
