using System;

namespace Triturbo.BlendShare.Fbx
{
    /// <summary>
    /// Defines BlendShare's canonical mesh local coordinate frame (<c>CanonicalMeshLocalV1</c>):
    /// right-handed Y-up in meters (<c>Right = +X</c>, <c>Up = +Y</c>, <c>Front = +Z</c>, <c>1 unit = 1 meter</c>),
    /// using binary64 row-vector right multiplication (<c>v' = v * M</c>).
    /// </summary>
    public static class CanonicalMeshSpace
    {
        public const int CanonicalMeshLocalV1 = 1;
        public const double CanonicalUnitMeters = 1d;

        public static readonly FbxCoordinateAxes CanonicalAxes = new FbxCoordinateAxes(
            FbxCoordinateAxis.PositiveX,
            FbxCoordinateAxis.PositiveY,
            FbxCoordinateAxis.PositiveZ);

        /// <summary>
        /// Returns true when the three axes are known and span three distinct orthogonal dimensions (X, Y, Z).
        /// </summary>
        public static bool IsValidAxes(FbxCoordinateAxes axes)
        {
            int right = (int)axes.Right;
            int up = (int)axes.Up;
            int front = (int)axes.Front;
            if (right < 0 || right >= (int)FbxCoordinateAxis.Unknown ||
                up < 0 || up >= (int)FbxCoordinateAxis.Unknown ||
                front < 0 || front >= (int)FbxCoordinateAxis.Unknown)
            {
                return false;
            }

            int rightDim = right / 2;
            int upDim = up / 2;
            int frontDim = front / 2;
            return rightDim != upDim && rightDim != frontDim && upDim != frontDim;
        }

        /// <summary>
        /// Returns true when <paramref name="unitMeters"/> is a finite strictly positive scale factor.
        /// </summary>
        public static bool IsValidUnitMeters(double unitMeters)
        {
            return !double.IsNaN(unitMeters) && !double.IsInfinity(unitMeters) && unitMeters > 0d;
        }

        /// <summary>
        /// Builds the row-vector change-of-basis and unit-scale matrix transforming points/vectors
        /// from an FBX scene's raw local space into <c>CanonicalMeshLocalV1</c> (meters, +X right, +Y up, +Z front).
        /// </summary>
        public static FbxMatrix4x4 BuildRawToCanonical(
            FbxCoordinateAxes rawAxes,
            double rawUnitMeters,
            out bool usedFallback)
        {
            usedFallback = false;

            FbxCoordinateAxes effectiveAxes = rawAxes;
            if (!IsValidAxes(effectiveAxes))
            {
                effectiveAxes = CanonicalAxes;
                usedFallback = true;
            }

            double effectiveUnitMeters = rawUnitMeters;
            if (!IsValidUnitMeters(effectiveUnitMeters))
            {
                effectiveUnitMeters = CanonicalUnitMeters;
                usedFallback = true;
            }

            if (effectiveAxes.Equals(CanonicalAxes) && effectiveUnitMeters == CanonicalUnitMeters)
            {
                return FbxMatrix4x4.Identity;
            }

            Vector3d rightVec = AxisToUnitVector(effectiveAxes.Right) * effectiveUnitMeters;
            Vector3d upVec = AxisToUnitVector(effectiveAxes.Up) * effectiveUnitMeters;
            Vector3d frontVec = AxisToUnitVector(effectiveAxes.Front) * effectiveUnitMeters;

            // Row-vector right multiplication:
            //   x' = p.x * m00 + p.y * m10 + p.z * m20 = dot(p, rightVec)
            //   y' = p.x * m01 + p.y * m11 + p.z * m21 = dot(p, upVec)
            //   z' = p.x * m02 + p.y * m12 + p.z * m22 = dot(p, frontVec)
            return new FbxMatrix4x4(
                rightVec.x, upVec.x, frontVec.x, 0d,
                rightVec.y, upVec.y, frontVec.y, 0d,
                rightVec.z, upVec.z, frontVec.z, 0d,
                0d, 0d, 0d, 1d);
        }

        /// <summary>
        /// Builds the row-vector matrix transforming points/vectors from <c>CanonicalMeshLocalV1</c>
        /// back into the specified FBX raw local space.
        /// </summary>
        public static FbxMatrix4x4 BuildCanonicalToRaw(
            FbxCoordinateAxes rawAxes,
            double rawUnitMeters,
            out bool usedFallback)
        {
            usedFallback = false;

            FbxCoordinateAxes effectiveAxes = rawAxes;
            if (!IsValidAxes(effectiveAxes))
            {
                effectiveAxes = CanonicalAxes;
                usedFallback = true;
            }

            double effectiveUnitMeters = rawUnitMeters;
            if (!IsValidUnitMeters(effectiveUnitMeters))
            {
                effectiveUnitMeters = CanonicalUnitMeters;
                usedFallback = true;
            }

            if (effectiveAxes.Equals(CanonicalAxes) && effectiveUnitMeters == CanonicalUnitMeters)
            {
                return FbxMatrix4x4.Identity;
            }

            double invScale = 1d / effectiveUnitMeters;
            Vector3d rightVec = AxisToUnitVector(effectiveAxes.Right) * invScale;
            Vector3d upVec = AxisToUnitVector(effectiveAxes.Up) * invScale;
            Vector3d frontVec = AxisToUnitVector(effectiveAxes.Front) * invScale;

            // Orthogonal basis inverse is transpose divided by scale:
            //   p_raw = x_c * rightVec + y_c * upVec + z_c * frontVec
            return new FbxMatrix4x4(
                rightVec.x, rightVec.y, rightVec.z, 0d,
                upVec.x, upVec.y, upVec.z, 0d,
                frontVec.x, frontVec.y, frontVec.z, 0d,
                0d, 0d, 0d, 1d);
        }

        /// <summary>
        /// Builds the raw-to-canonical matrix from <see cref="FbxSceneMetadata"/>.
        /// </summary>
        public static FbxMatrix4x4 BuildRawToCanonical(FbxSceneMetadata metadata, out bool usedFallback)
        {
            return BuildRawToCanonical(metadata.Axes, metadata.UnitMeters, out usedFallback);
        }

        /// <summary>
        /// Sanitizes scene metadata axes and unit scale for provenance recording.
        /// </summary>
        public static void SanitizeMetadata(
            FbxSceneMetadata metadata,
            out FbxCoordinateAxes effectiveAxes,
            out double effectiveUnitMeters,
            out bool usedFallback)
        {
            usedFallback = false;
            effectiveAxes = metadata.Axes;
            if (!IsValidAxes(effectiveAxes))
            {
                effectiveAxes = CanonicalAxes;
                usedFallback = true;
            }

            effectiveUnitMeters = metadata.UnitMeters;
            if (!IsValidUnitMeters(effectiveUnitMeters))
            {
                effectiveUnitMeters = CanonicalUnitMeters;
                usedFallback = true;
            }
        }

        private static Vector3d AxisToUnitVector(FbxCoordinateAxis axis)
        {
            switch (axis)
            {
                case FbxCoordinateAxis.PositiveX: return new Vector3d(1d, 0d, 0d);
                case FbxCoordinateAxis.NegativeX: return new Vector3d(-1d, 0d, 0d);
                case FbxCoordinateAxis.PositiveY: return new Vector3d(0d, 1d, 0d);
                case FbxCoordinateAxis.NegativeY: return new Vector3d(0d, -1d, 0d);
                case FbxCoordinateAxis.PositiveZ: return new Vector3d(0d, 0d, 1d);
                case FbxCoordinateAxis.NegativeZ: return new Vector3d(0d, 0d, -1d);
                default:
                    throw new ArgumentOutOfRangeException(nameof(axis), axis, "Axis must be a known signed coordinate axis.");
            }
        }
    }
}
