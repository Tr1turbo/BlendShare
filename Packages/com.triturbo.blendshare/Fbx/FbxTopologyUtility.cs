using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Triturbo.BlendShare.Fbx
{
    /// <summary>
    /// Immutable value record representing a canonical FBX polygon topology digest.
    /// </summary>
    public readonly struct FbxTopologyDigest : IEquatable<FbxTopologyDigest>
    {
        public static readonly FbxTopologyDigest Invalid = new FbxTopologyDigest(string.Empty, -1, -1, false);

        public string Hash { get; }
        public int ControlPointCount { get; }
        public int FaceCount { get; }
        public bool IsValid { get; }

        public FbxTopologyDigest(string hash, int controlPointCount, int faceCount, bool isValid)
        {
            Hash = hash ?? string.Empty;
            ControlPointCount = controlPointCount;
            FaceCount = faceCount;
            IsValid = isValid;
        }

        public bool Equals(FbxTopologyDigest other) =>
            string.Equals(Hash, other.Hash, StringComparison.Ordinal) &&
            ControlPointCount == other.ControlPointCount &&
            FaceCount == other.FaceCount &&
            IsValid == other.IsValid;

        public override bool Equals(object obj) => obj is FbxTopologyDigest other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Hash, ControlPointCount, FaceCount, IsValid);
    }

    public enum FbxTopologyCompatibilityState
    {
        Unavailable = 0,
        Exact = 1,
        CompatibleWithExtraSourceControlPoints = 2,
        CompatibleWithTopologyChange = 3,
        SourceHasFewerControlPoints = 4,
        TopologyMismatch = 5
    }

    public readonly struct FbxTopologyCompatibilityResult
    {
        public FbxTopologyCompatibilityState State { get; }
        public int OriginControlPointCount { get; }
        public int SourceControlPointCount { get; }
        public FbxTopologyDigest OriginDigest { get; }
        public bool HasExtraSourceControlPoints { get; }
        public bool HasExtraSourceEdges { get; }

        public bool IsCompatible =>
            State == FbxTopologyCompatibilityState.Exact ||
            State == FbxTopologyCompatibilityState.CompatibleWithExtraSourceControlPoints ||
            State == FbxTopologyCompatibilityState.CompatibleWithTopologyChange;

        public bool HasWarning =>
            IsCompatible && (HasExtraSourceControlPoints || HasExtraSourceEdges);

        public FbxTopologyCompatibilityResult(
            FbxTopologyCompatibilityState state,
            int originControlPointCount,
            int sourceControlPointCount,
            FbxTopologyDigest originDigest,
            bool hasExtraSourceControlPoints,
            bool hasExtraSourceEdges)
        {
            State = state;
            OriginControlPointCount = originControlPointCount;
            SourceControlPointCount = sourceControlPointCount;
            OriginDigest = originDigest;
            HasExtraSourceControlPoints = hasExtraSourceControlPoints;
            HasExtraSourceEdges = hasExtraSourceEdges;
        }
    }

    /// <summary>
    /// Pure Editor-side FBX topology hashing and origin/source compatibility evaluation.
    /// Produces SHA-256 digests identical to BlendShare.FbxTopologyHash.v1.
    /// </summary>
    public static class FbxTopologyUtility
    {
        private const string k_AlgorithmVersion = "BlendShare.FbxTopologyHash.v1";

        public static FbxTopologyDigest ComputeDigest(IFbxMeshTopology mesh, int controlPointLimit = -1)
        {
            if (mesh == null)
            {
                return FbxTopologyDigest.Invalid;
            }

            try
            {
                return ComputeDigest(
                    mesh.ControlPointCount,
                    mesh.GetFaceSizes(),
                    mesh.GetFaceCornerControlPoints(),
                    controlPointLimit);
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                return new FbxTopologyDigest(
                    string.Empty,
                    controlPointLimit >= 0 ? controlPointLimit : mesh.ControlPointCount,
                    mesh.FaceCount,
                    false);
            }
        }

        public static FbxTopologyDigest ComputeDigest(
            int controlPointCount,
            int[] faceSizes,
            int[] faceCornerControlPoints,
            int controlPointLimit = -1)
        {
            if (controlPointCount < 0 || faceSizes == null || faceCornerControlPoints == null)
            {
                return new FbxTopologyDigest(
                    string.Empty,
                    controlPointLimit >= 0 ? controlPointLimit : controlPointCount,
                    faceSizes != null ? faceSizes.Length : -1,
                    false);
            }

            int effectiveLimit = controlPointLimit >= 0 ? controlPointLimit : controlPointCount;
            if (effectiveLimit < 0 || effectiveLimit > controlPointCount)
            {
                return new FbxTopologyDigest(string.Empty, effectiveLimit, faceSizes.Length, false);
            }

            if (!TryBuildCanonicalFaces(faceSizes, faceCornerControlPoints, effectiveLimit, out var faces))
            {
                return new FbxTopologyDigest(string.Empty, effectiveLimit, faceSizes.Length, false);
            }

            faces.Sort(CompareFaces);
            string hash = ComputeSha256Hex(effectiveLimit, faces);
            return new FbxTopologyDigest(hash, effectiveLimit, faces.Count, true);
        }

        public static FbxTopologyCompatibilityResult EvaluateCompatibility(
            IFbxMeshTopology origin,
            IFbxMeshTopology source)
        {
            if (origin == null || source == null)
            {
                return new FbxTopologyCompatibilityResult(
                    FbxTopologyCompatibilityState.Unavailable,
                    origin != null ? origin.ControlPointCount : 0,
                    source != null ? source.ControlPointCount : 0,
                    FbxTopologyDigest.Invalid,
                    false,
                    false);
            }

            int originCount = origin.ControlPointCount;
            int sourceCount = source.ControlPointCount;
            int[] originFaceSizes = origin.GetFaceSizes();
            int[] originFaceIndices = origin.GetFaceCornerControlPoints();
            var originDigest = ComputeDigest(originCount, originFaceSizes, originFaceIndices);

            if (sourceCount < originCount)
            {
                return new FbxTopologyCompatibilityResult(
                    FbxTopologyCompatibilityState.SourceHasFewerControlPoints,
                    originCount,
                    sourceCount,
                    originDigest,
                    false,
                    false);
            }

            return EvaluateCompatibility(
                originCount,
                originFaceSizes,
                originFaceIndices,
                originDigest,
                sourceCount,
                source.GetFaceSizes(),
                source.GetFaceCornerControlPoints());
        }

        public static FbxTopologyCompatibilityResult EvaluateCompatibility(
            int originControlPointCount,
            int[] originFaceSizes,
            int[] originFaceIndices,
            FbxTopologyDigest originDigest,
            int sourceControlPointCount,
            int[] sourceFaceSizes,
            int[] sourceFaceIndices)
        {
            if (sourceControlPointCount < originControlPointCount)
            {
                return new FbxTopologyCompatibilityResult(
                    FbxTopologyCompatibilityState.SourceHasFewerControlPoints,
                    originControlPointCount,
                    sourceControlPointCount,
                    originDigest,
                    false,
                    false);
            }

            if (!originDigest.IsValid ||
                !TryBuildDirectedEdgeCounts(
                    originFaceSizes,
                    originFaceIndices,
                    originControlPointCount,
                    originControlPointCount,
                    out var originEdges) ||
                !TryBuildDirectedEdgeCounts(
                    sourceFaceSizes,
                    sourceFaceIndices,
                    sourceControlPointCount,
                    originControlPointCount,
                    out var sourceEdges))
            {
                return new FbxTopologyCompatibilityResult(
                    FbxTopologyCompatibilityState.Unavailable,
                    originControlPointCount,
                    sourceControlPointCount,
                    originDigest,
                    false,
                    false);
            }

            bool hasExtraControlPoints = sourceControlPointCount > originControlPointCount;
            bool hasExtraEdges = sourceEdges.Count != originEdges.Count;
            if (!hasExtraEdges)
            {
                foreach (var edge in sourceEdges)
                {
                    if (!originEdges.TryGetValue(edge.Key, out int originEdgeCount) || originEdgeCount != edge.Value)
                    {
                        hasExtraEdges = true;
                        break;
                    }
                }
            }

            foreach (var edge in originEdges)
            {
                if (!sourceEdges.TryGetValue(edge.Key, out int sourceEdgeCount) || sourceEdgeCount < edge.Value)
                {
                    return new FbxTopologyCompatibilityResult(
                        FbxTopologyCompatibilityState.TopologyMismatch,
                        originControlPointCount,
                        sourceControlPointCount,
                        originDigest,
                        hasExtraControlPoints,
                        hasExtraEdges);
                }
            }

            FbxTopologyCompatibilityState state = hasExtraControlPoints
                ? FbxTopologyCompatibilityState.CompatibleWithExtraSourceControlPoints
                : hasExtraEdges
                    ? FbxTopologyCompatibilityState.CompatibleWithTopologyChange
                    : FbxTopologyCompatibilityState.Exact;

            return new FbxTopologyCompatibilityResult(
                state,
                originControlPointCount,
                sourceControlPointCount,
                originDigest,
                hasExtraControlPoints,
                hasExtraEdges);
        }

        private static bool TryBuildCanonicalFaces(
            int[] faceSizes,
            int[] faceCornerControlPoints,
            int controlPointLimit,
            out List<int[]> faces)
        {
            faces = new List<int[]>(faceSizes.Length);
            int offset = 0;
            for (int i = 0; i < faceSizes.Length; i++)
            {
                int size = faceSizes[i];
                if (size < 0 || offset + size > faceCornerControlPoints.Length)
                {
                    return false;
                }

                bool include = true;
                for (int j = 0; j < size; j++)
                {
                    int index = faceCornerControlPoints[offset + j];
                    if (index < 0)
                    {
                        return false;
                    }

                    if (index >= controlPointLimit)
                    {
                        include = false;
                    }
                }

                if (include)
                {
                    var face = new int[size];
                    Array.Copy(faceCornerControlPoints, offset, face, 0, size);
                    faces.Add(CanonicalizeCyclic(face));
                }

                offset += size;
            }

            return offset == faceCornerControlPoints.Length;
        }

        private static int[] CanonicalizeCyclic(int[] face)
        {
            if (face == null || face.Length <= 1)
            {
                return face ?? Array.Empty<int>();
            }

            int bestStart = 0;
            for (int candidate = 1; candidate < face.Length; candidate++)
            {
                if (CompareRotation(face, candidate, bestStart) < 0)
                {
                    bestStart = candidate;
                }
            }

            var result = new int[face.Length];
            for (int i = 0; i < face.Length; i++)
            {
                result[i] = face[(bestStart + i) % face.Length];
            }

            return result;
        }

        private static int CompareRotation(int[] face, int leftStart, int rightStart)
        {
            for (int i = 0; i < face.Length; i++)
            {
                int left = face[(leftStart + i) % face.Length];
                int right = face[(rightStart + i) % face.Length];
                int compare = left.CompareTo(right);
                if (compare != 0)
                {
                    return compare;
                }
            }

            return 0;
        }

        private static int CompareFaces(int[] left, int[] right)
        {
            int leftLen = left != null ? left.Length : 0;
            int rightLen = right != null ? right.Length : 0;
            int lengthCompare = leftLen.CompareTo(rightLen);
            if (lengthCompare != 0)
            {
                return lengthCompare;
            }

            for (int i = 0; i < leftLen; i++)
            {
                int compare = left[i].CompareTo(right[i]);
                if (compare != 0)
                {
                    return compare;
                }
            }

            return 0;
        }

        private static bool TryBuildDirectedEdgeCounts(
            int[] faceSizes,
            int[] faceIndices,
            int meshControlPointCount,
            int originControlPointLimit,
            out Dictionary<long, int> edges)
        {
            edges = new Dictionary<long, int>();
            if (faceSizes == null || faceIndices == null)
            {
                return false;
            }

            int offset = 0;
            for (int f = 0; f < faceSizes.Length; f++)
            {
                int size = faceSizes[f];
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

                    if (from >= originControlPointLimit || to >= originControlPointLimit)
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

        private static string ComputeSha256Hex(int controlPointCount, IReadOnlyList<int[]> faces)
        {
            using (var sha256 = SHA256.Create())
            using (var cryptoStream = new CryptoStream(Stream.Null, sha256, CryptoStreamMode.Write))
            using (var writer = new BinaryWriter(cryptoStream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(k_AlgorithmVersion);
                writer.Write(controlPointCount);
                writer.Write(faces != null ? faces.Count : 0);

                if (faces != null)
                {
                    for (int f = 0; f < faces.Count; f++)
                    {
                        var face = faces[f];
                        int len = face != null ? face.Length : 0;
                        writer.Write(len);
                        for (int i = 0; i < len; i++)
                        {
                            writer.Write(face[i]);
                        }
                    }
                }

                writer.Flush();
                cryptoStream.FlushFinalBlock();
                return ToLowerHex(sha256.Hash);
            }
        }

        private static string ToLowerHex(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return string.Empty;
            }

            const string hex = "0123456789abcdef";
            var chars = new char[bytes.Length * 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                byte b = bytes[i];
                chars[i * 2] = hex[b >> 4];
                chars[i * 2 + 1] = hex[b & 0xF];
            }

            return new string(chars);
        }
    }
}
