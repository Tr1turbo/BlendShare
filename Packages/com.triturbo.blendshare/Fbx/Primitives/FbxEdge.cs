using System;
using System.Runtime.InteropServices;

namespace Triturbo.BlendShare.Fbx
{
    /// <summary>An ufbx_edge. Both endpoints index the face-corner control point array.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct FbxEdge : IEquatable<FbxEdge>
    {
        /// <summary>Corner a.</summary>
        public readonly int CornerA;
        /// <summary>Corner b.</summary>
        public readonly int CornerB;

        /// <summary>Creates a new FbxEdge from the supplied values.</summary>
        public FbxEdge(int cornerA, int cornerB)
        {
            CornerA = cornerA;
            CornerB = cornerB;
        }

        /// <inheritdoc/>
        public bool Equals(FbxEdge other) => CornerA == other.CornerA && CornerB == other.CornerB;
        /// <inheritdoc/>
        public override bool Equals(object obj) => obj is FbxEdge other && Equals(other);
        /// <inheritdoc/>
        public override int GetHashCode() => HashCode.Combine(CornerA, CornerB);
    }
}
