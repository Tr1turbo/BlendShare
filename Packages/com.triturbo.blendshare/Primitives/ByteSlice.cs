using System;

namespace Triturbo.BlendShare.Primitives
{
    /// <summary>
    /// Represents a validated byte range within a BlendShare container stream.
    /// </summary>
    [Serializable]
    public readonly struct ByteSlice : IEquatable<ByteSlice>
    {
        public static readonly ByteSlice Empty = new ByteSlice(0L, 0L);

        public readonly long Offset;
        public readonly long Length;

        public bool IsEmpty => Length == 0L;

        public ByteSlice(long offset, long length)
        {
            if (offset < 0L)
            {
                throw new ArgumentOutOfRangeException(nameof(offset), "ByteSlice offset must be non-negative.");
            }

            if (length < 0L)
            {
                throw new ArgumentOutOfRangeException(nameof(length), "ByteSlice length must be non-negative.");
            }

            // Verify offset + length does not overflow Int64.
            if (offset > long.MaxValue - length)
            {
                throw new OverflowException("ByteSlice end offset overflows Int64.");
            }

            Offset = offset;
            Length = length;
        }

        public long End => checked(Offset + Length);

        public bool IsWithin(long totalLength)
        {
            return totalLength >= 0L && Offset <= totalLength && Length <= totalLength - Offset;
        }

        public bool IsWithin(ByteSlice container)
        {
            return Offset >= container.Offset && End <= container.End;
        }

        public bool Equals(ByteSlice other) => Offset == other.Offset && Length == other.Length;
        public override bool Equals(object obj) => obj is ByteSlice other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Offset, Length);
        public static bool operator ==(ByteSlice left, ByteSlice right) => left.Equals(right);
        public static bool operator !=(ByteSlice left, ByteSlice right) => !left.Equals(right);
        public override string ToString() => $"ByteSlice(Offset={Offset}, Length={Length})";
    }
}
