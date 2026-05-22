using System;

namespace Triturbo.Fbx
{
    [Serializable]
    public readonly struct FbxMatrix4x4 : IEquatable<FbxMatrix4x4>
    {
        public static readonly FbxMatrix4x4 Identity = new FbxMatrix4x4(
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            0, 0, 0, 1);

        private readonly double m00;
        private readonly double m01;
        private readonly double m02;
        private readonly double m03;
        private readonly double m10;
        private readonly double m11;
        private readonly double m12;
        private readonly double m13;
        private readonly double m20;
        private readonly double m21;
        private readonly double m22;
        private readonly double m23;
        private readonly double m30;
        private readonly double m31;
        private readonly double m32;
        private readonly double m33;

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

        public bool IsIdentity => Approximately(Identity);
        public Vector3d Translation => new Vector3d(m30, m31, m32);

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

        public static FbxMatrix4x4 Translate(Vector3d translation)
        {
            return new FbxMatrix4x4(
                1, 0, 0, 0,
                0, 1, 0, 0,
                0, 0, 1, 0,
                translation.x, translation.y, translation.z, 1);
        }

        public static FbxMatrix4x4 Scale(Vector3d scale)
        {
            return new FbxMatrix4x4(
                scale.x, 0, 0, 0,
                0, scale.y, 0, 0,
                0, 0, scale.z, 0,
                0, 0, 0, 1);
        }

        public static FbxMatrix4x4 RotateEulerDegrees(Vector3d rotation)
        {
            double x = DegreesToRadians(rotation.x);
            double y = DegreesToRadians(rotation.y);
            double z = DegreesToRadians(rotation.z);

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

            return rx * ry * rz;
        }

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

        public static FbxMatrix4x4 FromTranslationRotationScale(
            Vector3d translation,
            Vector3d rotation,
            Vector3d scale)
        {
            return Scale(scale) * RotateEulerDegrees(rotation) * Translate(translation);
        }

        public static FbxMatrix4x4 FromTranslationRotationScale(
            Vector3d translation,
            Quaterniond rotation,
            Vector3d scale)
        {
            return Scale(scale) * RotateQuaternion(rotation) * Translate(translation);
        }

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

        public override bool Equals(object obj)
        {
            return obj is FbxMatrix4x4 matrix && Equals(matrix);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                foreach (double value in ToRowMajorArray())
                {
                    hash = hash * 31 + value.GetHashCode();
                }

                return hash;
            }
        }

        public static FbxMatrix4x4 operator *(FbxMatrix4x4 lhs, FbxMatrix4x4 rhs)
        {
            var values = new double[16];
            for (int row = 0; row < 4; row++)
            {
                for (int column = 0; column < 4; column++)
                {
                    double sum = 0d;
                    for (int i = 0; i < 4; i++)
                    {
                        sum += lhs[row, i] * rhs[i, column];
                    }

                    values[row * 4 + column] = sum;
                }
            }

            return FromRowMajor(values);
        }

        public static bool operator ==(FbxMatrix4x4 lhs, FbxMatrix4x4 rhs)
        {
            return lhs.Equals(rhs);
        }

        public static bool operator !=(FbxMatrix4x4 lhs, FbxMatrix4x4 rhs)
        {
            return !lhs.Equals(rhs);
        }

        private static double DegreesToRadians(double degrees)
        {
            return degrees * Math.PI / 180d;
        }
    }

    [Serializable]
    public readonly struct FbxTransform
    {
        public static readonly FbxTransform Identity = new FbxTransform(Vector3d.zero, Vector3d.zero, Vector3d.one);

        public Vector3d Translation { get; }
        public Vector3d Rotation { get; }
        public Vector3d Scale { get; }
        public FbxMatrix4x4 LocalMatrix => FbxMatrix4x4.FromTranslationRotationScale(Translation, Rotation, Scale);

        public FbxTransform(Vector3d translation, Vector3d rotation, Vector3d scale)
        {
            Translation = translation;
            Rotation = rotation;
            Scale = scale;
        }
    }

    [Serializable]
    public readonly struct Quaterniond : IEquatable<Quaterniond>
    {
        public static readonly Quaterniond Identity = new Quaterniond(0d, 0d, 0d, 1d);

        public double x { get; }
        public double y { get; }
        public double z { get; }
        public double w { get; }

        public Quaterniond(double x, double y, double z, double w)
        {
            this.x = x;
            this.y = y;
            this.z = z;
            this.w = w;
        }

        public double magnitude => Math.Sqrt(x * x + y * y + z * z + w * w);
        public Quaterniond normalized => magnitude > Vector3d.Epsilon ? this / magnitude : Identity;
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

        public bool Equals(Quaterniond other)
        {
            return x == other.x && y == other.y && z == other.z && w == other.w;
        }

        public override bool Equals(object obj)
        {
            return obj is Quaterniond other && Equals(other);
        }

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

        public static Quaterniond operator *(Quaterniond lhs, Quaterniond rhs)
        {
            return new Quaterniond(
                lhs.w * rhs.x + lhs.x * rhs.w + lhs.y * rhs.z - lhs.z * rhs.y,
                lhs.w * rhs.y - lhs.x * rhs.z + lhs.y * rhs.w + lhs.z * rhs.x,
                lhs.w * rhs.z + lhs.x * rhs.y - lhs.y * rhs.x + lhs.z * rhs.w,
                lhs.w * rhs.w - lhs.x * rhs.x - lhs.y * rhs.y - lhs.z * rhs.z);
        }

        public static Quaterniond operator /(Quaterniond quaternion, double scalar)
        {
            return new Quaterniond(quaternion.x / scalar, quaternion.y / scalar, quaternion.z / scalar, quaternion.w / scalar);
        }
    }

    [Serializable]
    public readonly struct UfbxTransform
    {
        public static readonly UfbxTransform Identity = new UfbxTransform(Vector3d.zero, Quaterniond.Identity, Vector3d.one);

        public Vector3d Translation { get; }
        public Quaterniond Rotation { get; }
        public Vector3d Scale { get; }
        public FbxMatrix4x4 LocalMatrix => FbxMatrix4x4.FromTranslationRotationScale(Translation, Rotation, Scale);

        public UfbxTransform(Vector3d translation, Quaterniond rotation, Vector3d scale)
        {
            Translation = translation;
            Rotation = rotation;
            Scale = scale;
        }
    }
}
