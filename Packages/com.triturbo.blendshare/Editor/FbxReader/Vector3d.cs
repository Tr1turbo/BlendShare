using System;
using UnityEngine;

namespace Triturbo.BlendShapeShare.FbxReader
{
    [System.Serializable]
    public struct Vector3d : IEquatable<Vector3d>
    {
        public const double Epsilon = 1e-10;

        public double x;
        public double y;
        public double z;

        public static readonly Vector3d zero = new Vector3d(0, 0, 0);
        public static readonly Vector3d one = new Vector3d(1, 1, 1);

        public Vector3d(double x, double y, double z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        public readonly double magnitude
        {
            get { return Math.Sqrt(sqrMagnitude); }
        }

        public readonly double sqrMagnitude
        {
            get { return x * x + y * y + z * z; }
        }

        public readonly Vector3d normalized
        {
            get
            {
                double mag = magnitude;
                return mag > Epsilon ? this / mag : zero;
            }
        }

        public readonly bool IsZero()
        {
            return x == 0 && y == 0 && z == 0;
        }

        public readonly bool Approximately(Vector3d other, double epsilon = Epsilon)
        {
            return SqrMagnitude(this - other) <= epsilon * epsilon;
        }

        public void Normalize()
        {
            double mag = magnitude;
            this = mag > Epsilon ? this / mag : zero;
        }



        public readonly override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + x.GetHashCode();
                hash = hash * 31 + y.GetHashCode();
                hash = hash * 31 + z.GetHashCode();
                return hash;
            }
        }

        public readonly bool Equals(Vector3d other)
        {
            return x == other.x && y == other.y && z == other.z;
        }

        public readonly override bool Equals(object other)
        {
            return other is Vector3d vector && Equals(vector);
        }

        public readonly override string ToString()
        {
            return $"({x:F1}, {y:F1}, {z:F1})";
        }

        public readonly string ToString(string format)
        {
            return $"({x.ToString(format)}, {y.ToString(format)}, {z.ToString(format)})";
        }

        public static bool operator ==(Vector3d lhs, Vector3d rhs)
        {
            return lhs.x == rhs.x && lhs.y == rhs.y && lhs.z == rhs.z;
        }

        public static bool operator !=(Vector3d lhs, Vector3d rhs)
        {
            return lhs.x != rhs.x || lhs.y != rhs.y || lhs.z != rhs.z;
        }

        public static Vector3d operator +(Vector3d lhs, Vector3d rhs)
        {
            return new Vector3d(lhs.x + rhs.x, lhs.y + rhs.y, lhs.z + rhs.z);
        }

        public static Vector3d operator -(Vector3d lhs, Vector3d rhs)
        {
            return new Vector3d(lhs.x - rhs.x, lhs.y - rhs.y, lhs.z - rhs.z);
        }

        public static Vector3d operator -(Vector3d vector)
        {
            return new Vector3d(-vector.x, -vector.y, -vector.z);
        }

        public static Vector3d operator *(Vector3d vector, double scalar)
        {
            return new Vector3d(vector.x * scalar, vector.y * scalar, vector.z * scalar);
        }

        public static Vector3d operator *(double scalar, Vector3d vector)
        {
            return vector * scalar;
        }

        public static Vector3d operator /(Vector3d vector, double scalar)
        {
            return new Vector3d(vector.x / scalar, vector.y / scalar, vector.z / scalar);
        }

        public static implicit operator Vector3d(Vector3 value)
        {
            return new Vector3d(value.x, value.y, value.z);
        }


        public readonly Vector3 ToVector3()
        {
            return new Vector3((float)x, (float)y, (float)z);
        }

        public static explicit operator Vector3(Vector3d value)
        {
            return value.ToVector3();
        }

        public static double Dot(Vector3d lhs, Vector3d rhs)
        {
            return lhs.x * rhs.x + lhs.y * rhs.y + lhs.z * rhs.z;
        }

        public static Vector3d Cross(Vector3d lhs, Vector3d rhs)
        {
            return new Vector3d(
                lhs.y * rhs.z - lhs.z * rhs.y,
                lhs.z * rhs.x - lhs.x * rhs.z,
                lhs.x * rhs.y - lhs.y * rhs.x);
        }

        public static double Distance(Vector3d lhs, Vector3d rhs)
        {
            return (lhs - rhs).magnitude;
        }

        public static Vector3d Lerp(Vector3d lhs, Vector3d rhs, double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return LerpUnclamped(lhs, rhs, t);
        }

        public static Vector3d LerpUnclamped(Vector3d lhs, Vector3d rhs, double t)
        {
            return new Vector3d(
                lhs.x + (rhs.x - lhs.x) * t,
                lhs.y + (rhs.y - lhs.y) * t,
                lhs.z + (rhs.z - lhs.z) * t);
        }

        public static Vector3d Scale(Vector3d lhs, Vector3d rhs)
        {
            return new Vector3d(lhs.x * rhs.x, lhs.y * rhs.y, lhs.z * rhs.z);
        }

        public static double Magnitude(Vector3d vector)
        {
            return vector.magnitude;
        }

        public static double SqrMagnitude(Vector3d vector)
        {
            return vector.sqrMagnitude;
        }
    }
}
