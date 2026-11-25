using System;

namespace Triturbo.BlendShare.Primitives
{
    /// <summary>
    /// Immutable Compressed Sparse Row (CSR) vertex correspondence map from one IndexDomain to another.
    /// Validates monotonic offsets, terminal offset, sorted unique entries per row, and target bounds on construction.
    /// </summary>
    public sealed class CsrVertexMap
    {
        private readonly int[] m_RowOffsets;
        private readonly int[] m_TargetIndices;

        public IndexDomain SourceDomain { get; }
        public IndexDomain TargetDomain { get; }
        public int SourceCount { get; }
        public int TargetCount { get; }

        public ReadOnlySpan<int> RowOffsets => m_RowOffsets;
        public ReadOnlySpan<int> TargetIndices => m_TargetIndices;

        public CsrVertexMap(
            IndexDomain sourceDomain,
            IndexDomain targetDomain,
            int sourceCount,
            int targetCount,
            int[] rowOffsets,
            int[] targetIndices,
            bool takeOwnership = false)
        {
            IndexDomainUtility.ThrowIfInvalid(sourceDomain, nameof(sourceDomain));
            IndexDomainUtility.ThrowIfInvalid(targetDomain, nameof(targetDomain));

            if (sourceCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sourceCount));
            }

            if (targetCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(targetCount));
            }

            if (rowOffsets == null)
            {
                throw new ArgumentNullException(nameof(rowOffsets));
            }

            if (targetIndices == null)
            {
                throw new ArgumentNullException(nameof(targetIndices));
            }

            if (rowOffsets.Length != sourceCount + 1)
            {
                throw new ArgumentException($"RowOffsets length ({rowOffsets.Length}) must equal SourceCount + 1 ({sourceCount + 1}).");
            }

            if (rowOffsets[0] != 0)
            {
                throw new ArgumentException("RowOffsets[0] must be 0.", nameof(rowOffsets));
            }

            if (rowOffsets[sourceCount] != targetIndices.Length)
            {
                throw new ArgumentException(
                    $"Terminal RowOffset ({rowOffsets[sourceCount]}) must equal TargetIndices length ({targetIndices.Length}).",
                    nameof(rowOffsets));
            }

            for (int row = 0; row < sourceCount; row++)
            {
                int start = rowOffsets[row];
                int end = rowOffsets[row + 1];
                if (end < start)
                {
                    throw new ArgumentException($"RowOffsets must be monotonically non-decreasing (row {row}: {start} -> {end}).", nameof(rowOffsets));
                }

                int previousTarget = -1;
                for (int slot = start; slot < end; slot++)
                {
                    int target = targetIndices[slot];
                    if (target <= previousTarget)
                    {
                        throw new ArgumentException(
                            $"Target indices within row {row} must be non-negative and strictly increasing.",
                            nameof(targetIndices));
                    }

                    if (target >= targetCount)
                    {
                        throw new ArgumentOutOfRangeException(
                            nameof(targetIndices),
                            $"Target index {target} in row {row} exceeds TargetCount ({targetCount}).");
                    }

                    previousTarget = target;
                }
            }

            SourceDomain = sourceDomain;
            TargetDomain = targetDomain;
            SourceCount = sourceCount;
            TargetCount = targetCount;
            m_RowOffsets = takeOwnership ? rowOffsets : (int[])rowOffsets.Clone();
            m_TargetIndices = takeOwnership ? targetIndices : (targetIndices.Length == 0 ? Array.Empty<int>() : (int[])targetIndices.Clone());
        }

        public ReadOnlySpan<int> GetRow(int sourceIndex)
        {
            if (sourceIndex < 0 || sourceIndex >= SourceCount)
            {
                throw new ArgumentOutOfRangeException(nameof(sourceIndex));
            }

            int start = m_RowOffsets[sourceIndex];
            int count = m_RowOffsets[sourceIndex + 1] - start;
            return new ReadOnlySpan<int>(m_TargetIndices, start, count);
        }
    }
}
