using System;
using System.Runtime.InteropServices;

namespace Triturbo.BlendShare.Fbx
{
    /// <summary>Signed coordinate axes in ufbx_coordinate_axis order.</summary>
    public enum FbxCoordinateAxis
    {
        /// <summary>Positive x.</summary>
        PositiveX = 0,
        /// <summary>Negative x.</summary>
        NegativeX = 1,
        /// <summary>Positive y.</summary>
        PositiveY = 2,
        /// <summary>Negative y.</summary>
        NegativeY = 3,
        /// <summary>Positive z.</summary>
        PositiveZ = 4,
        /// <summary>Negative z.</summary>
        NegativeZ = 5,
        /// <summary>Unknown.</summary>
        Unknown = 6
    }

    /// <summary>Signed axes describing a coordinate system.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct FbxCoordinateAxes : IEquatable<FbxCoordinateAxes>
    {
        /// <summary>Right.</summary>
        public readonly FbxCoordinateAxis Right;
        /// <summary>Up.</summary>
        public readonly FbxCoordinateAxis Up;
        /// <summary>Signed front axis, pointing opposite the forward direction in ufbx.</summary>
        public readonly FbxCoordinateAxis Front;

        /// <summary>Creates a new FbxCoordinateAxes from the supplied values.</summary>
        public FbxCoordinateAxes(FbxCoordinateAxis right, FbxCoordinateAxis up, FbxCoordinateAxis front)
        {
            Right = right;
            Up = up;
            Front = front;
        }

        /// <inheritdoc/>
        public bool Equals(FbxCoordinateAxes other) => Right == other.Right && Up == other.Up && Front == other.Front;
        /// <inheritdoc/>
        public override bool Equals(object obj) => obj is FbxCoordinateAxes other && Equals(other);
        /// <inheritdoc/>
        public override int GetHashCode() => HashCode.Combine(Right, Up, Front);
    }

    /// <summary>File version, timing and coordinate settings reported by the source scene.</summary>
    public readonly struct FbxSceneMetadata
    {
        /// <summary>File version.</summary>
        public readonly uint FileVersion;
        /// <summary>Original up axis recorded in the scene settings.</summary>
        public readonly FbxCoordinateAxis OriginalUpAxis;
        /// <summary>Length of one scene unit in meters.</summary>
        public readonly double UnitMeters;
        /// <summary>Length of one original file unit in meters.</summary>
        public readonly double OriginalUnitMeters;
        /// <summary>Frames per second.</summary>
        public readonly double FramesPerSecond;
        /// <summary>Coordinate axes of the source file, including when conversion is requested.</summary>
        public readonly FbxCoordinateAxes Axes;

        /// <summary>Creates a new FbxSceneMetadata from the supplied values.</summary>
        public FbxSceneMetadata(
            uint fileVersion,
            FbxCoordinateAxis originalUpAxis,
            double unitMeters,
            double originalUnitMeters,
            double framesPerSecond,
            FbxCoordinateAxes axes)
        {
            FileVersion = fileVersion;
            UnitMeters = unitMeters;
            OriginalUnitMeters = originalUnitMeters;
            FramesPerSecond = framesPerSecond;
            Axes = axes;
            OriginalUpAxis = originalUpAxis;
        }
    }
}
