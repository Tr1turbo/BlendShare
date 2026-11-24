using System;
using UnityEngine;

namespace Triturbo.BlendShare.Primitives
{
    /// <summary>
    /// Represents a validated byte range within a BlendShare container stream.
    /// Uses private [SerializeField] backing fields so Unity serializes ByteSlice cleanly inside ScriptableObjects
    /// while remaining immutable to callers.
    /// </summary>
    [Serializable]
    public struct ByteSlice : IEquatable<ByteSlice>
    {
        public static readonly ByteSlice Empty = new ByteSlice(0L, 0L);

        [SerializeField] private long m_Offset;
        [SerializeField] private long m_Length;

        public long Offset => m_Offset;
        public long Length => m_Length;
        public bool IsEmpty => m_Length == 0L;

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

            if (offset > long.MaxValue - length)
            {
                throw new OverflowException("ByteSlice end offset overflows Int64.");
            }

            m_Offset = offset;
            m_Length = length;
        }

        public long End => checked(m_Offset + m_Length);

        public bool IsWithin(long totalLength)
        {
            return totalLength >= 0L && m_Offset <= totalLength && m_Length <= totalLength - m_Offset;
        }

        public bool IsWithin(ByteSlice container)
        {
            return m_Offset >= container.m_Offset && End <= container.End;
        }

        public bool Equals(ByteSlice other) => m_Offset == other.m_Offset && m_Length == other.m_Length;
        public override bool Equals(object obj) => obj is ByteSlice other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(m_Offset, m_Length);
        public static bool operator ==(ByteSlice left, ByteSlice right) => left.Equals(right);
        public static bool operator !=(ByteSlice left, ByteSlice right) => !left.Equals(right);
        public override string ToString() => $"ByteSlice(Offset={m_Offset}, Length={m_Length})";
    }
}
