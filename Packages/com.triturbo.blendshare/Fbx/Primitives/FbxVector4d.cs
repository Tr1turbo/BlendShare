using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Triturbo.BlendShare.Fbx
{
    /// <summary>A double-precision 4-component vector.</summary>
    [Serializable, StructLayout(LayoutKind.Sequential)]
    public struct Vector4d : IEquatable<Vector4d>
    {
        /// <summary>Threshold for negligible magnitude.</summary>
        public const double Epsilon = Vector3d.Epsilon;

        /// <summary>The X component.</summary>
        public double x;
        /// <summary>The Y component.</summary>
        public double y;
        /// <summary>The Z component.</summary>
        public double z;
        /// <summary>The W component.</summary>
        public double w;

        /// <summary>Vector with every component zero.</summary>
        public static readonly Vector4d zero = new Vector4d(0, 0, 0, 0);
        /// <summary>Vector with every component one.</summary>
        public static readonly Vector4d one = new Vector4d(1, 1, 1, 1);

        /// <summary>Creates a new Vector4d from the supplied values.</summary>
        public Vector4d(double x, double y, double z, double w)
        {
            this.x = x;
            this.y = y;
            this.z = z;
            this.w = w;
        }

        /// <summary>Euclidean length.</summary>
        public readonly double magnitude => Math.Sqrt(sqrMagnitude);
        /// <summary>Squared Euclidean length.</summary>
        public readonly double sqrMagnitude => x * x + y * y + z * z + w * w;
        /// <summary>Unit-length value, with the default normalization fallback for negligible length.</summary>
        public readonly Vector4d normalized => magnitude > Epsilon ? this / magnitude : zero;

        /// <summary>Tests whether all components are exactly zero.</summary>
        public readonly bool IsZero()
        {
            return x == 0 && y == 0 && z == 0 && w == 0;
        }

        /// <summary>Tests equality within the supplied tolerance.</summary>
        public readonly bool Approximately(Vector4d other, double epsilon = Epsilon)
        {
            return (this - other).sqrMagnitude <= epsilon * epsilon;
        }

        /// <summary>Scales this vector to unit length, or clears it if its length is negligible.</summary>
        public void Normalize()
        {
            double mag = magnitude;
            this = mag > Epsilon ? this / mag : zero;
        }

        /// <inheritdoc/>
        public readonly bool Equals(Vector4d other)
        {
            return x == other.x && y == other.y && z == other.z && w == other.w;
        }

        /// <inheritdoc/>
        public readonly override bool Equals(object other)
        {
            return other is Vector4d vector && Equals(vector);
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
                hash = hash * 31 + w.GetHashCode();
                return hash;
            }
        }

        /// <inheritdoc/>
        public readonly override string ToString()
        {
            return $"({x:F1}, {y:F1}, {z:F1}, {w:F1})";
        }

        /// <summary>Formats each component using the supplied numeric format.</summary>
        public readonly string ToString(string format)
        {
            return $"({x.ToString(format)}, {y.ToString(format)}, {z.ToString(format)}, {w.ToString(format)})";
        }

        /// <summary>Tests exact component equality.</summary>
        public static bool operator ==(Vector4d lhs, Vector4d rhs)
        {
            return lhs.Equals(rhs);
        }

        /// <summary>Tests component inequality.</summary>
        public static bool operator !=(Vector4d lhs, Vector4d rhs)
        {
            return !lhs.Equals(rhs);
        }

        /// <summary>Adds corresponding components.</summary>
        public static Vector4d operator +(Vector4d lhs, Vector4d rhs)
        {
            return new Vector4d(lhs.x + rhs.x, lhs.y + rhs.y, lhs.z + rhs.z, lhs.w + rhs.w);
        }

        /// <summary>Subtracts corresponding components.</summary>
        public static Vector4d operator -(Vector4d lhs, Vector4d rhs)
        {
            return new Vector4d(lhs.x - rhs.x, lhs.y - rhs.y, lhs.z - rhs.z, lhs.w - rhs.w);
        }

        /// <summary>Negates each component.</summary>
        public static Vector4d operator -(Vector4d vector)
        {
            return new Vector4d(-vector.x, -vector.y, -vector.z, -vector.w);
        }

        /// <summary>Multiplies each component by a scalar.</summary>
        public static Vector4d operator *(Vector4d vector, double scalar)
        {
            return new Vector4d(vector.x * scalar, vector.y * scalar, vector.z * scalar, vector.w * scalar);
        }

        /// <summary>Multiplies each component by a scalar.</summary>
        public static Vector4d operator *(double scalar, Vector4d vector)
        {
            return vector * scalar;
        }

        /// <summary>Divides each component by a scalar.</summary>
        public static Vector4d operator /(Vector4d vector, double scalar)
        {
            return new Vector4d(vector.x / scalar, vector.y / scalar, vector.z / scalar, vector.w / scalar);
        }

        /// <summary>Converts the value to Vector4d.</summary>
        public static implicit operator Vector4d(Vector4 value)
        {
            return new Vector4d(value.x, value.y, value.z, value.w);
        }

        /// <summary>Narrows the components to a Unity vector.</summary>
        public readonly Vector4 ToVector4()
        {
            return new Vector4((float)x, (float)y, (float)z, (float)w);
        }

        /// <summary>Converts the value to Vector4.</summary>
        public static implicit operator Vector4(Vector4d value)
        {
            return value.ToVector4();
        }

        /// <summary>Converts XYZW to Unity RGBA components.</summary>
        public readonly Color ToColor() => new Color((float)x, (float)y, (float)z, (float)w);

        /// <summary>Converts the value to Color.</summary>
        public static implicit operator Color(Vector4d value) => value.ToColor();
        /// <summary>Converts the value to Vector4d.</summary>
        public static implicit operator Vector4d(Color value) => new Vector4d(value.r, value.g, value.b, value.a);
    }
}
