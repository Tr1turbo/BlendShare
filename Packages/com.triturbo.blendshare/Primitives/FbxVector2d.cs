using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Triturbo.BlendShare.Fbx
{
    /// <summary>A double-precision 2-component vector.</summary>
    [Serializable, StructLayout(LayoutKind.Sequential)]
    public struct Vector2d : IEquatable<Vector2d>
    {
        /// <summary>Threshold for negligible magnitude.</summary>
        public const double Epsilon = Vector3d.Epsilon;

        /// <summary>The X component.</summary>
        public double x;
        /// <summary>The Y component.</summary>
        public double y;

        /// <summary>Vector with every component zero.</summary>
        public static readonly Vector2d zero = new Vector2d(0, 0);
        /// <summary>Vector with every component one.</summary>
        public static readonly Vector2d one = new Vector2d(1, 1);

        /// <summary>Creates a new Vector2d from the supplied values.</summary>
        public Vector2d(double x, double y)
        {
            this.x = x;
            this.y = y;
        }

        /// <summary>Euclidean length.</summary>
        public readonly double magnitude => Math.Sqrt(sqrMagnitude);
        /// <summary>Squared Euclidean length.</summary>
        public readonly double sqrMagnitude => x * x + y * y;
        /// <summary>Unit-length value, with the default normalization fallback for negligible length.</summary>
        public readonly Vector2d normalized => magnitude > Epsilon ? this / magnitude : zero;

        /// <summary>Tests whether all components are exactly zero.</summary>
        public readonly bool IsZero()
        {
            return x == 0 && y == 0;
        }

        /// <summary>Tests equality within the supplied tolerance.</summary>
        public readonly bool Approximately(Vector2d other, double epsilon = Epsilon)
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
        public readonly bool Equals(Vector2d other)
        {
            return x == other.x && y == other.y;
        }

        /// <inheritdoc/>
        public readonly override bool Equals(object other)
        {
            return other is Vector2d vector && Equals(vector);
        }

        /// <inheritdoc/>
        public readonly override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + x.GetHashCode();
                hash = hash * 31 + y.GetHashCode();
                return hash;
            }
        }

        /// <inheritdoc/>
        public readonly override string ToString()
        {
            return $"({x:F1}, {y:F1})";
        }

        /// <summary>Formats each component using the supplied numeric format.</summary>
        public readonly string ToString(string format)
        {
            return $"({x.ToString(format)}, {y.ToString(format)})";
        }

        /// <summary>Tests exact component equality.</summary>
        public static bool operator ==(Vector2d lhs, Vector2d rhs)
        {
            return lhs.Equals(rhs);
        }

        /// <summary>Tests component inequality.</summary>
        public static bool operator !=(Vector2d lhs, Vector2d rhs)
        {
            return !lhs.Equals(rhs);
        }

        /// <summary>Adds corresponding components.</summary>
        public static Vector2d operator +(Vector2d lhs, Vector2d rhs)
        {
            return new Vector2d(lhs.x + rhs.x, lhs.y + rhs.y);
        }

        /// <summary>Subtracts corresponding components.</summary>
        public static Vector2d operator -(Vector2d lhs, Vector2d rhs)
        {
            return new Vector2d(lhs.x - rhs.x, lhs.y - rhs.y);
        }

        /// <summary>Negates each component.</summary>
        public static Vector2d operator -(Vector2d vector)
        {
            return new Vector2d(-vector.x, -vector.y);
        }

        /// <summary>Multiplies each component by a scalar.</summary>
        public static Vector2d operator *(Vector2d vector, double scalar)
        {
            return new Vector2d(vector.x * scalar, vector.y * scalar);
        }

        /// <summary>Multiplies each component by a scalar.</summary>
        public static Vector2d operator *(double scalar, Vector2d vector)
        {
            return vector * scalar;
        }

        /// <summary>Divides each component by a scalar.</summary>
        public static Vector2d operator /(Vector2d vector, double scalar)
        {
            return new Vector2d(vector.x / scalar, vector.y / scalar);
        }

        /// <summary>Converts the value to Vector2d.</summary>
        public static implicit operator Vector2d(Vector2 value)
        {
            return new Vector2d(value.x, value.y);
        }

        /// <summary>Narrows the components to a Unity vector.</summary>
        public readonly Vector2 ToVector2()
        {
            return new Vector2((float)x, (float)y);
        }

        /// <summary>Converts the value to Vector2.</summary>
        public static explicit operator Vector2(Vector2d value)
        {
            return value.ToVector2();
        }

    }
}
