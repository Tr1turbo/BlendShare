using System;

namespace Triturbo.BlendShare.Primitives
{
    /// <summary>
    /// Compact, domain-tagged sparse buffer backed by parallel strictly-increasing index and value arrays.
    /// Replaces legacy Unity-serialized SparseArray&lt;T&gt; with zero Dictionary/List overhead.
    /// </summary>
    public sealed class IndexedBuffer<T>
    {
        private readonly int[] m_Indices;
        private readonly T[] m_Values;

        public IndexDomain Domain { get; }
        public int DomainCount { get; }
        public int Count => m_Indices.Length;
        public bool IsEmpty => m_Indices.Length == 0;

        public ReadOnlySpan<int> Indices => m_Indices;
        public ReadOnlySpan<T> Values => m_Values;

        public IndexedBuffer(IndexDomain domain, int domainCount, int[] indices, T[] values, bool takeOwnership = false)
        {
            IndexDomainUtility.ThrowIfInvalid(domain, nameof(domain));

            if (domainCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(domainCount), "DomainCount must be non-negative.");
            }

            if (indices == null)
            {
                throw new ArgumentNullException(nameof(indices));
            }

            if (values == null)
            {
                throw new ArgumentNullException(nameof(values));
            }

            if (indices.Length != values.Length)
            {
                throw new ArgumentException($"Indices length ({indices.Length}) must equal values length ({values.Length}).");
            }

            int previous = -1;
            for (int i = 0; i < indices.Length; i++)
            {
                int index = indices[i];
                if (index <= previous)
                {
                    throw new ArgumentException(
                        $"IndexedBuffer indices must be non-negative and strictly increasing (failed at slot {i}: {index} after {previous}).",
                        nameof(indices));
                }

                if (index >= domainCount)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(indices),
                        $"IndexedBuffer index {index} at slot {i} exceeds DomainCount ({domainCount}).");
                }

                previous = index;
            }

            Domain = domain;
            DomainCount = domainCount;
            if (takeOwnership)
            {
                m_Indices = indices;
                m_Values = values;
            }
            else
            {
                m_Indices = indices.Length == 0 ? Array.Empty<int>() : (int[])indices.Clone();
                m_Values = values.Length == 0 ? Array.Empty<T>() : (T[])values.Clone();
            }
        }

        public static IndexedBuffer<T> Empty(IndexDomain domain, int domainCount)
        {
            return new IndexedBuffer<T>(domain, domainCount, Array.Empty<int>(), Array.Empty<T>(), takeOwnership: true);
        }

        public bool TryGetValue(int index, out T value)
        {
            if (index < 0 || index >= DomainCount || m_Indices.Length == 0)
            {
                value = default;
                return false;
            }

            int slot = Array.BinarySearch(m_Indices, index);
            if (slot >= 0)
            {
                value = m_Values[slot];
                return true;
            }

            value = default;
            return false;
        }

        public T GetValueOrDefault(int index, T defaultValue = default)
        {
            return TryGetValue(index, out var value) ? value : defaultValue;
        }

        public T[] ToDenseArray(T defaultValue = default)
        {
            var dense = new T[DomainCount];
            if (!Equals(defaultValue, default(T)))
            {
                for (int i = 0; i < dense.Length; i++)
                {
                    dense[i] = defaultValue;
                }
            }

            for (int i = 0; i < m_Indices.Length; i++)
            {
                dense[m_Indices[i]] = m_Values[i];
            }

            return dense;
        }

        public int[] CopyIndices() => m_Indices.Length == 0 ? Array.Empty<int>() : (int[])m_Indices.Clone();
        public T[] CopyValues() => m_Values.Length == 0 ? Array.Empty<T>() : (T[])m_Values.Clone();
    }
}
