using System;
using System.Buffers.Binary;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Model.Serialization;

namespace Triturbo.BlendShare.Model.Documents
{
    /// <summary>
    /// Compact translation-invariant sub-block centroid contrast sketch (at most 801 bytes) stored on <see cref="MeshDocument"/>.
    /// Encodes up to <see cref="MaxBlocks"/> deterministic 32-sample blocks from polygon-referenced Origin control points
    /// (requiring at least <see cref="MinGroupSampleCount"/> samples in each of the 4 interleaved groups <c>j mod 4</c>
    /// so no individual vertex or tiny primitive geometry is recoverable).
    /// </summary>
    [BlendShareRecord(SchemaVersion = 1)]
    public sealed class GeometrySketch
    {
        public const int MaxBlocks = 20;
        public const int SamplesPerBlock = 32;
        public const int MinControlPointsForSketch = 32;
        public const int MinGroupSampleCount = 4;
        public const int BytesPerBlock = 4 + 9 * sizeof(float);
        public const int MaxByteLength = 1 + MaxBlocks * BytesPerBlock;

        /// <summary>
        /// Packed binary payload: <c>byte blockCount</c> followed by <c>blockCount</c> fixed 40-byte little-endian records
        /// (<c>uint32 participationMask</c> + 9 little-endian <c>float32</c> components for <c>mu_1 - mu_0, mu_2 - mu_0, mu_3 - mu_0</c>).
        /// </summary>
        [BlendShareField(1)] public byte[] Data = Array.Empty<byte>();

        public bool HasData => Data != null && Data.Length >= 1 + BytesPerBlock;

        /// <summary>
        /// Computes the deterministic sampled control-point index for block <paramref name="blockIndex"/> and sample slot <paramref name="sampleSlot"/>.
        /// </summary>
        public static int GetSampleControlPointIndex(int controlPointCount, int blockCount, int blockIndex, int sampleSlot)
        {
            long start = ((long)blockIndex * controlPointCount) / blockCount;
            long end = ((long)(blockIndex + 1) * controlPointCount) / blockCount;
            long length = end - start;
            return (int)(start + ((2L * sampleSlot + 1L) * length) / (2L * SamplesPerBlock));
        }

        /// <summary>
        /// Builds a <see cref="GeometrySketch"/> from raw Origin control points and polygon indices.
        /// Returns <c>null</c> for tiny meshes (<c>controlPointCount &lt; 32</c>) or when zero blocks meet participation requirements.
        /// </summary>
        public static GeometrySketch TryBuild(
            ReadOnlySpan<Vector3d> originControlPoints,
            ReadOnlySpan<int> polygonVertexIndices)
        {
            int n = originControlPoints.Length;
            int blockCount = Math.Min(MaxBlocks, n / SamplesPerBlock);
            if (blockCount <= 0)
            {
                return null;
            }

            var referencedByPolygon = new bool[n];
            for (int i = 0; i < polygonVertexIndices.Length; i++)
            {
                int cp = polygonVertexIndices[i];
                if ((uint)cp < (uint)n)
                {
                    referencedByPolygon[cp] = true;
                }
            }

            var bytes = new byte[1 + blockCount * BytesPerBlock];
            bytes[0] = (byte)blockCount;
            int activeBlocks = 0;

            Span<Vector3d> groupSums = stackalloc Vector3d[4];
            Span<int> groupCounts = stackalloc int[4];

            for (int b = 0; b < blockCount; b++)
            {
                uint mask = 0u;
                groupSums.Clear();
                groupCounts.Clear();
                bool hasAnchor = false;
                Vector3d anchor = Vector3d.zero;

                for (int j = 0; j < SamplesPerBlock; j++)
                {
                    int cp = GetSampleControlPointIndex(n, blockCount, b, j);
                    if ((uint)cp >= (uint)n || !referencedByPolygon[cp])
                    {
                        continue;
                    }

                    Vector3d p = originControlPoints[cp];
                    if (!IsFinite(p))
                    {
                        continue;
                    }

                    if (!hasAnchor)
                    {
                        anchor = p;
                        hasAnchor = true;
                    }

                    mask |= 1u << j;
                    int g = j & 3;
                    groupSums[g] += p - anchor;
                    groupCounts[g]++;
                }

                int offset = 1 + b * BytesPerBlock;
                if (groupCounts[0] < MinGroupSampleCount ||
                    groupCounts[1] < MinGroupSampleCount ||
                    groupCounts[2] < MinGroupSampleCount ||
                    groupCounts[3] < MinGroupSampleCount)
                {
                    // Leave inactive block zeroed (mask = 0).
                    continue;
                }

                Vector3d mu0 = groupSums[0] / groupCounts[0];
                Vector3d c1 = (groupSums[1] / groupCounts[1]) - mu0;
                Vector3d c2 = (groupSums[2] / groupCounts[2]) - mu0;
                Vector3d c3 = (groupSums[3] / groupCounts[3]) - mu0;

                if (!CanCastToFiniteFloat32(c1) || !CanCastToFiniteFloat32(c2) || !CanCastToFiniteFloat32(c3))
                {
                    continue;
                }

                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), mask);
                WriteVector3Float32LE(bytes.AsSpan(offset + 4, 12), c1);
                WriteVector3Float32LE(bytes.AsSpan(offset + 16, 12), c2);
                WriteVector3Float32LE(bytes.AsSpan(offset + 28, 12), c3);
                activeBlocks++;
            }

            if (activeBlocks == 0)
            {
                return null;
            }

            return new GeometrySketch { Data = bytes };
        }

        /// <summary>
        /// Validates binary framing, size bounds, group sample counts, and float32 finiteness of <see cref="Data"/>.
        /// </summary>
        public bool TryValidate(int expectedControlPointCount, out string error)
        {
            error = null;
            if (Data == null || Data.Length == 0)
            {
                return true;
            }

            if (Data.Length > MaxByteLength)
            {
                error = $"GeometrySketch exceeds maximum byte length ({Data.Length} > {MaxByteLength}).";
                return false;
            }

            int blockCount = Data[0];
            if (blockCount <= 0 || blockCount > MaxBlocks || Data.Length != 1 + blockCount * BytesPerBlock)
            {
                error = $"GeometrySketch has invalid block count ({blockCount}) or byte length ({Data.Length}).";
                return false;
            }

            if (expectedControlPointCount >= 0)
            {
                int expectedBlocks = Math.Min(MaxBlocks, expectedControlPointCount / SamplesPerBlock);
                if (expectedBlocks != blockCount)
                {
                    error = $"GeometrySketch block count ({blockCount}) does not match topology control point count ({expectedControlPointCount}).";
                    return false;
                }
            }

            Span<int> counts = stackalloc int[4];
            for (int b = 0; b < blockCount; b++)
            {
                int offset = 1 + b * BytesPerBlock;
                uint mask = BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(offset, 4));
                if (mask == 0u)
                {
                    continue;
                }

                counts.Clear();
                for (int j = 0; j < SamplesPerBlock; j++)
                {
                    if (((mask >> j) & 1u) != 0u)
                    {
                        counts[j & 3]++;
                    }
                }

                if (counts[0] < MinGroupSampleCount ||
                    counts[1] < MinGroupSampleCount ||
                    counts[2] < MinGroupSampleCount ||
                    counts[3] < MinGroupSampleCount)
                {
                    error = $"GeometrySketch block {b} has fewer than {MinGroupSampleCount} participating samples in a group.";
                    return false;
                }

                if (!TryReadBlockContrasts(b, out _, out _, out _, out _))
                {
                    error = $"GeometrySketch block {b} contains non-finite contrast components.";
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Reads the participation mask and three stored contrast vectors for block <paramref name="blockIndex"/>.
        /// </summary>
        public bool TryReadBlockContrasts(
            int blockIndex,
            out uint mask,
            out Vector3d c1,
            out Vector3d c2,
            out Vector3d c3)
        {
            mask = 0u;
            c1 = Vector3d.zero;
            c2 = Vector3d.zero;
            c3 = Vector3d.zero;

            if (Data == null || Data.Length < 1)
            {
                return false;
            }

            int blockCount = Data[0];
            if ((uint)blockIndex >= (uint)blockCount || Data.Length != 1 + blockCount * BytesPerBlock)
            {
                return false;
            }

            int offset = 1 + blockIndex * BytesPerBlock;
            mask = BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(offset, 4));
            c1 = ReadVector3Float32LE(Data.AsSpan(offset + 4, 12));
            c2 = ReadVector3Float32LE(Data.AsSpan(offset + 16, 12));
            c3 = ReadVector3Float32LE(Data.AsSpan(offset + 28, 12));
            return IsFinite(c1) && IsFinite(c2) && IsFinite(c3);
        }

        public int BlockCount => Data != null && Data.Length >= 1 ? Data[0] : 0;

        private static void WriteVector3Float32LE(Span<byte> dst, Vector3d value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(dst.Slice(0, 4), BitConverter.SingleToInt32Bits((float)value.x));
            BinaryPrimitives.WriteInt32LittleEndian(dst.Slice(4, 4), BitConverter.SingleToInt32Bits((float)value.y));
            BinaryPrimitives.WriteInt32LittleEndian(dst.Slice(8, 4), BitConverter.SingleToInt32Bits((float)value.z));
        }

        private static Vector3d ReadVector3Float32LE(ReadOnlySpan<byte> src)
        {
            float x = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(src.Slice(0, 4)));
            float y = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(src.Slice(4, 4)));
            float z = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(src.Slice(8, 4)));
            return new Vector3d(x, y, z);
        }

        private static bool IsFinite(Vector3d v)
        {
            return !double.IsNaN(v.x) && !double.IsInfinity(v.x) &&
                   !double.IsNaN(v.y) && !double.IsInfinity(v.y) &&
                   !double.IsNaN(v.z) && !double.IsInfinity(v.z);
        }

        private static bool CanCastToFiniteFloat32(Vector3d v)
        {
            float fx = (float)v.x;
            float fy = (float)v.y;
            float fz = (float)v.z;
            return !float.IsNaN(fx) && !float.IsInfinity(fx) &&
                   !float.IsNaN(fy) && !float.IsInfinity(fy) &&
                   !float.IsNaN(fz) && !float.IsInfinity(fz);
        }
    }
}
