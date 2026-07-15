using System.Collections.Generic;
using Triturbo.BlendShare.Fbx.Ufbx;

namespace Triturbo.BlendShare.Hashing
{
    public enum FbxMeshCompatibilityState
    {
        Unavailable,
        Exact,
        CompatibleWithExtraSourceControlPoints,
        CompatibleWithTopologyChange,
        SourceHasFewerControlPoints,
        TopologyMismatch
    }

    public readonly struct FbxMeshCompatibilityResult
    {
        public FbxMeshCompatibilityState State { get; }
        public int OriginalControlPointCount { get; }
        public int SourceControlPointCount { get; }
        public FbxTopologySignature OriginalSignature { get; }
        public bool HasExtraSourceControlPoints { get; }
        public bool HasExtraSourceEdges { get; }

        public bool IsCompatible =>
            State == FbxMeshCompatibilityState.Exact ||
            State == FbxMeshCompatibilityState.CompatibleWithExtraSourceControlPoints ||
            State == FbxMeshCompatibilityState.CompatibleWithTopologyChange;

        public bool HasWarning =>
            IsCompatible && (HasExtraSourceControlPoints || HasExtraSourceEdges);

        internal FbxMeshCompatibilityResult(
            FbxMeshCompatibilityState state,
            int originalControlPointCount,
            int sourceControlPointCount,
            FbxTopologySignature originalSignature,
            bool hasExtraSourceControlPoints,
            bool hasExtraSourceEdges)
        {
            State = state;
            OriginalControlPointCount = originalControlPointCount;
            SourceControlPointCount = sourceControlPointCount;
            OriginalSignature = originalSignature;
            HasExtraSourceControlPoints = hasExtraSourceControlPoints;
            HasExtraSourceEdges = hasExtraSourceEdges;
        }
    }

    public static class FbxMeshCompatibility
    {
        public static FbxMeshCompatibilityResult Evaluate(UfbxMesh original, UfbxMesh source)
        {
            if (original == null || source == null)
            {
                return new FbxMeshCompatibilityResult(
                    FbxMeshCompatibilityState.Unavailable,
                    original?.ControlPointCount ?? 0,
                    source?.ControlPointCount ?? 0,
                    new FbxTopologySignature(),
                    false,
                    false);
            }

            int originalCount = original.ControlPointCount;
            int sourceCount = source.ControlPointCount;
            var originalSignature = FbxTopologyHash.Calculate(original);
            if (sourceCount < originalCount)
            {
                return new FbxMeshCompatibilityResult(
                    FbxMeshCompatibilityState.SourceHasFewerControlPoints,
                    originalCount,
                    sourceCount,
                    originalSignature,
                    false,
                    false);
            }

            return EvaluateTopology(
                originalCount,
                original.GetFaceSizes(),
                original.GetFaceControlPointIndices(),
                originalSignature,
                sourceCount,
                source.GetFaceSizes(),
                source.GetFaceControlPointIndices());
        }

        public static FbxMeshCompatibilityResult EvaluateTopology(
            int originalControlPointCount,
            int[] originalFaceSizes,
            int[] originalFaceIndices,
            FbxTopologySignature originalSignature,
            int sourceControlPointCount,
            int[] sourceFaceSizes,
            int[] sourceFaceIndices)
        {
            if (sourceControlPointCount < originalControlPointCount)
            {
                return new FbxMeshCompatibilityResult(
                    FbxMeshCompatibilityState.SourceHasFewerControlPoints,
                    originalControlPointCount,
                    sourceControlPointCount,
                    originalSignature,
                    false,
                    false);
            }

            if (originalSignature == null || !originalSignature.IsValid ||
                !TryBuildDirectedEdgeCounts(
                    originalFaceSizes,
                    originalFaceIndices,
                    originalControlPointCount,
                    originalControlPointCount,
                    out var originalEdges) ||
                !TryBuildDirectedEdgeCounts(
                    sourceFaceSizes,
                    sourceFaceIndices,
                    sourceControlPointCount,
                    originalControlPointCount,
                    out var sourceEdges))
            {
                return new FbxMeshCompatibilityResult(
                    FbxMeshCompatibilityState.Unavailable,
                    originalControlPointCount,
                    sourceControlPointCount,
                    originalSignature,
                    false,
                    false);
            }

            bool hasExtraControlPoints = sourceControlPointCount > originalControlPointCount;
            bool hasExtraEdges = sourceEdges.Count != originalEdges.Count;
            if (!hasExtraEdges)
            {
                foreach (var edge in sourceEdges)
                {
                    if (!originalEdges.TryGetValue(edge.Key, out int originalCount) || originalCount != edge.Value)
                    {
                        hasExtraEdges = true;
                        break;
                    }
                }
            }

            foreach (var edge in originalEdges)
            {
                if (!sourceEdges.TryGetValue(edge.Key, out int sourceCount) || sourceCount < edge.Value)
                {
                    return new FbxMeshCompatibilityResult(
                        FbxMeshCompatibilityState.TopologyMismatch,
                        originalControlPointCount,
                        sourceControlPointCount,
                        originalSignature,
                        hasExtraControlPoints,
                        hasExtraEdges);
                }
            }

            var state = hasExtraControlPoints
                ? FbxMeshCompatibilityState.CompatibleWithExtraSourceControlPoints
                : hasExtraEdges
                    ? FbxMeshCompatibilityState.CompatibleWithTopologyChange
                    : FbxMeshCompatibilityState.Exact;
            return new FbxMeshCompatibilityResult(
                state,
                originalControlPointCount,
                sourceControlPointCount,
                originalSignature,
                hasExtraControlPoints,
                hasExtraEdges);
        }

        private static bool TryBuildDirectedEdgeCounts(
            int[] faceSizes,
            int[] faceIndices,
            int meshControlPointCount,
            int originalControlPointLimit,
            out Dictionary<long, int> edges)
        {
            edges = new Dictionary<long, int>();
            if (faceSizes == null || faceIndices == null)
            {
                return false;
            }

            int offset = 0;
            foreach (int size in faceSizes)
            {
                if (size < 0 || offset + size > faceIndices.Length)
                {
                    return false;
                }

                for (int i = 0; i < size; i++)
                {
                    int from = faceIndices[offset + i];
                    int to = faceIndices[offset + ((i + 1) % size)];
                    if (from < 0 || to < 0 || from >= meshControlPointCount || to >= meshControlPointCount)
                    {
                        return false;
                    }

                    if (from >= originalControlPointLimit || to >= originalControlPointLimit)
                    {
                        continue;
                    }

                    long key = ((long)(uint)from << 32) | (uint)to;
                    edges.TryGetValue(key, out int count);
                    edges[key] = count + 1;
                }

                offset += size;
            }

            return offset == faceIndices.Length;
        }
    }
}
