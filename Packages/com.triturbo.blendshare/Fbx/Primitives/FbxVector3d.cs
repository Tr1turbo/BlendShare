using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Triturbo.BlendShare.Fbx
{
    /// <summary>A double-precision 3-component vector.</summary>
    [Serializable, StructLayout(LayoutKind.Sequential)]
    public struct Vector3d : IEquatable<Vector3d>
    {
        /// <summary>Threshold for negligible magnitude.</summary>
        public const double Epsilon = 1e-10;

        /// <summary>The X component.</summary>
        public double x;
        /// <summary>The Y component.</summary>
        public double y;
        /// <summary>The Z component.</summary>
        public double z;

        /// <summary>Vector with every component zero.</summary>
        public static readonly Vector3d zero = new Vector3d(0, 0, 0);
        /// <summary>Vector with every component one.</summary>
        public static readonly Vector3d one = new Vector3d(1, 1, 1);

        /// <summary>Creates a new Vector3d from the supplied values.</summary>
        public Vector3d(double x, double y, double z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        /// <summary>Euclidean length.</summary>
        public readonly double magnitude => Math.Sqrt(sqrMagnitude);
        /// <summary>Squared Euclidean length.</summary>
        public readonly double sqrMagnitude => x * x + y * y + z * z;
        /// <summary>Unit-length value, with the default normalization fallback for negligible length.</summary>
        public readonly Vector3d normalized => magnitude > Epsilon ? this / magnitude : zero;

        /// <summary>Tests whether all components are exactly zero.</summary>
        public readonly bool IsZero()
        {
            return x == 0 && y == 0 && z == 0;
        }

        /// <summary>Tests equality within the supplied tolerance.</summary>
        public readonly bool Approximately(Vector3d other, double epsilon = Epsilon)
        {
            return SqrMagnitude(this - other) <= epsilon * epsilon;
        }

        /// <summary>Scales this vector to unit length, or clears it if its length is negligible.</summary>
        public void Normalize()
        {
            double mag = magnitude;
            this = mag > Epsilon ? this / mag : zero;
        }

        /// <inheritdoc/>
        public readonly bool Equals(Vector3d other)
        {
            return x == other.x && y == other.y && z == other.z;
        }

        /// <inheritdoc/>
        public readonly override bool Equals(object other)
        {
            return other is Vector3d vector && Equals(vector);
        }

        /// <inheritdoc/>
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

        /// <inheritdoc/>
        public readonly override string ToString()
        {
            return $"({x:F1}, {y:F1}, {z:F1})";
        }

        /// <summary>Formats each component using the supplied numeric format.</summary>
        public readonly string ToString(string format)
        {
            return $"({x.ToString(format)}, {y.ToString(format)}, {z.ToString(format)})";
        }

        /// <summary>Tests exact component equality.</summary>
        public static bool operator ==(Vector3d lhs, Vector3d rhs)
        {
            return lhs.Equals(rhs);
        }

        /// <summary>Tests component inequality.</summary>
        public static bool operator !=(Vector3d lhs, Vector3d rhs)
        {
            return !lhs.Equals(rhs);
        }

        /// <summary>Adds corresponding components.</summary>
        public static Vector3d operator +(Vector3d lhs, Vector3d rhs)
        {
            return new Vector3d(lhs.x + rhs.x, lhs.y + rhs.y, lhs.z + rhs.z);
        }

        /// <summary>Subtracts corresponding components.</summary>
        public static Vector3d operator -(Vector3d lhs, Vector3d rhs)
        {
            return new Vector3d(lhs.x - rhs.x, lhs.y - rhs.y, lhs.z - rhs.z);
        }

        /// <summary>Negates each component.</summary>
        public static Vector3d operator -(Vector3d vector)
        {
            return new Vector3d(-vector.x, -vector.y, -vector.z);
        }

        /// <summary>Multiplies each component by a scalar.</summary>
        public static Vector3d operator *(Vector3d vector, double scalar)
        {
            return new Vector3d(vector.x * scalar, vector.y * scalar, vector.z * scalar);
        }

        /// <summary>Multiplies each component by a scalar.</summary>
        public static Vector3d operator *(double scalar, Vector3d vector)
        {
            return vector * scalar;
        }

        /// <summary>Divides each component by a scalar.</summary>
        public static Vector3d operator /(Vector3d vector, double scalar)
        {
            return new Vector3d(vector.x / scalar, vector.y / scalar, vector.z / scalar);
        }

        /// <summary>Converts the value to Vector3d.</summary>
        public static implicit operator Vector3d(Vector3 value)
        {
            return new Vector3d(value.x, value.y, value.z);
        }

        /// <summary>Narrows the components to a Unity vector.</summary>
        public readonly Vector3 ToVector3()
        {
            return new Vector3((float)x, (float)y, (float)z);
        }

        /// <summary>Converts the value to Vector3.</summary>
        public static explicit operator Vector3(Vector3d value)
        {
            return value.ToVector3();
        }

        /// <summary>Dot.</summary>
        public static double Dot(Vector3d lhs, Vector3d rhs)
        {
            return lhs.x * rhs.x + lhs.y * rhs.y + lhs.z * rhs.z;
        }

        /// <summary>Cross.</summary>
        public static Vector3d Cross(Vector3d lhs, Vector3d rhs)
        {
            return new Vector3d(
                lhs.y * rhs.z - lhs.z * rhs.y,
                lhs.z * rhs.x - lhs.x * rhs.z,
                lhs.x * rhs.y - lhs.y * rhs.x);
        }

        /// <summary>Distance.</summary>
        public static double Distance(Vector3d lhs, Vector3d rhs)
        {
            return (lhs - rhs).magnitude;
        }

        /// <summary>Linearly interpolates with the fraction clamped to the unit interval.</summary>
        public static Vector3d Lerp(Vector3d lhs, Vector3d rhs, double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return LerpUnclamped(lhs, rhs, t);
        }

        /// <summary>Linearly interpolates or extrapolates at the supplied fraction.</summary>
        public static Vector3d LerpUnclamped(Vector3d lhs, Vector3d rhs, double t)
        {
            return new Vector3d(
                lhs.x + (rhs.x - lhs.x) * t,
                lhs.y + (rhs.y - lhs.y) * t,
                lhs.z + (rhs.z - lhs.z) * t);
        }

        /// <summary>Multiplies corresponding components.</summary>
        public static Vector3d Scale(Vector3d lhs, Vector3d rhs)
        {
            return new Vector3d(lhs.x * rhs.x, lhs.y * rhs.y, lhs.z * rhs.z);
        }

        /// <summary>Computes Euclidean length.</summary>
        public static double Magnitude(Vector3d vector)
        {
            return vector.magnitude;
        }

        /// <summary>Computes squared Euclidean length.</summary>
        public static double SqrMagnitude(Vector3d vector)
        {
            return vector.sqrMagnitude;
        }
    }
}
