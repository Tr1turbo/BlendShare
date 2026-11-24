using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Primitives;
using UnityEngine;

namespace Triturbo.BlendShare.Mapping
{
    /// <summary>
    /// Converts between jagged control-point index groups and validated immutable <see cref="CsrVertexMap"/> instances,
    /// and computes deterministic content-addressed CSR blob identifiers.
    /// </summary>
    public static class CsrVertexMapFactory
    {
        private const string k_BlobHashDomain = "BlendShare.CsrBlob.v1";

        public static CsrVertexMap FromJaggedGroups(
            IReadOnlyList<int[]> groups,
            int controlPointCount,
            IndexDomain sourceDomain = IndexDomain.UnityVertex,
            IndexDomain targetDomain = IndexDomain.ControlPoint)
        {
            if (groups == null)
            {
                throw new ArgumentNullException(nameof(groups));
            }

            int sourceCount = groups.Count;
            var rowOffsets = new int[sourceCount + 1];
            var flatTargets = new List<int>(sourceCount);
            var rowScratch = new List<int>(4);

            for (int row = 0; row < sourceCount; row++)
            {
                rowOffsets[row] = flatTargets.Count;
                var raw = groups[row];
                if (raw == null || raw.Length == 0)
                {
                    continue;
                }

                rowScratch.Clear();
                for (int i = 0; i < raw.Length; i++)
                {
                    int cp = raw[i];
                    if (cp < 0 || cp >= controlPointCount)
                    {
                        throw new ArgumentOutOfRangeException(
                            nameof(groups),
                            $"Row {row} contains out-of-range target index {cp} (targetCount={controlPointCount}).");
                    }

                    rowScratch.Add(cp);
                }

                rowScratch.Sort();
                int prev = -1;
                for (int i = 0; i < rowScratch.Count; i++)
                {
                    int cp = rowScratch[i];
                    if (cp != prev)
                    {
                        flatTargets.Add(cp);
                        prev = cp;
                    }
                }
            }

            rowOffsets[sourceCount] = flatTargets.Count;
            return new CsrVertexMap(
                sourceDomain,
                targetDomain,
                sourceCount,
                controlPointCount,
                rowOffsets,
                flatTargets.ToArray(),
                takeOwnership: true);
        }

        public static int[][] ToJaggedGroups(CsrVertexMap map)
        {
            if (map == null)
            {
                throw new ArgumentNullException(nameof(map));
            }

            var result = new int[map.SourceCount][];
            for (int row = 0; row < map.SourceCount; row++)
            {
                var span = map.GetRow(row);
                result[row] = span.Length == 0 ? Array.Empty<int>() : span.ToArray();
            }

            return result;
        }

        public static string ComputeCsrBlobId(CsrVertexMap map)
        {
            if (map == null)
            {
                throw new ArgumentNullException(nameof(map));
            }

            using (var sha = SHA256.Create())
            using (var cs = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write))
            using (var bw = new BinaryWriter(cs, Encoding.UTF8, leaveOpen: true))
            {
                bw.Write(k_BlobHashDomain);
                bw.Write((byte)map.SourceDomain);
                bw.Write((byte)map.TargetDomain);
                bw.Write(map.SourceCount);
                bw.Write(map.TargetCount);

                var offsets = map.RowOffsets;
                bw.Write(offsets.Length);
                for (int i = 0; i < offsets.Length; i++)
                {
                    bw.Write(offsets[i]);
                }

                var targets = map.TargetIndices;
                bw.Write(targets.Length);
                for (int i = 0; i < targets.Length; i++)
                {
                    bw.Write(targets[i]);
                }

                bw.Flush();
                cs.FlushFinalBlock();
                return ToLowerHex(sha.Hash);
            }
        }

        internal static string ToLowerHex(byte[] bytes)
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

    /// <summary>
    /// Represents a set of Unity vertices sharing the exact same float32 position.
    /// </summary>
    public readonly struct CoincidentVertexClass
    {
        public int[] UnityVertexIndices { get; }
        public string DiscriminatorDigest { get; }
        public string[] PerVertexIncidenceDigests { get; }

        public CoincidentVertexClass(
            int[] unityVertexIndices,
            string discriminatorDigest,
            string[] perVertexIncidenceDigests = null)
        {
            UnityVertexIndices = unityVertexIndices ?? Array.Empty<int>();
            DiscriminatorDigest = discriminatorDigest ?? string.Empty;
            PerVertexIncidenceDigests = perVertexIncidenceDigests ?? Array.Empty<string>();
        }
    }

    /// <summary>
    /// Pure geometry digests computed over a <see cref="UnityMeshSnapshot"/> for OriginBinding verification
    /// and conservative binding cache resolution keys (never disclosing raw 3D vertex coordinates).
    /// </summary>
    public readonly struct UnityMeshGeometryDigest
    {
        public int VertexCount { get; }
        public int TriangleCount { get; }
        public string OrderedPositionsDigest { get; }
        public string CanonicalTriangleIncidenceDigest { get; }
        public string Uv0Digest { get; }
        public string CombinedGeometryKey { get; }

        public UnityMeshGeometryDigest(
            int vertexCount,
            int triangleCount,
            string orderedPositionsDigest,
            string canonicalTriangleIncidenceDigest,
            string uv0Digest,
            string combinedGeometryKey)
        {
            VertexCount = vertexCount;
            TriangleCount = triangleCount;
            OrderedPositionsDigest = orderedPositionsDigest ?? string.Empty;
            CanonicalTriangleIncidenceDigest = canonicalTriangleIncidenceDigest ?? string.Empty;
            Uv0Digest = uv0Digest ?? string.Empty;
            CombinedGeometryKey = combinedGeometryKey ?? string.Empty;
        }
    }

    public static class UnityMeshGeometryDigests
    {
        private const string k_PositionsDomain = "BlendShare.UnityOrderedPositions.v1";
        private const string k_TrianglesDomain = "BlendShare.UnityCanonicalTriangles.v1";
        private const string k_Uv0Domain = "BlendShare.UnityUv0.v1";
        private const string k_CombinedDomain = "BlendShare.UnityGeometryKey.v1";
        private const string k_ClassDiscDomain = "BlendShare.CoincidentClassDisc.v1";

        public static UnityMeshGeometryDigest Compute(UnityMeshSnapshot snapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            string posDigest = ComputeOrderedPositionsDigest(snapshot.Vertices);
            string triDigest = ComputeCanonicalTriangleIncidenceDigest(snapshot.Triangles);
            string uvDigest = ComputeUv0Digest(snapshot.Uv0);

            string combined;
            using (var sha = SHA256.Create())
            using (var cs = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write))
            using (var bw = new BinaryWriter(cs, Encoding.UTF8, leaveOpen: true))
            {
                bw.Write(k_CombinedDomain);
                bw.Write(snapshot.Vertices.Length);
                bw.Write(snapshot.TriangleCount);
                bw.Write(posDigest);
                bw.Write(triDigest);
                bw.Write(uvDigest);
                bw.Flush();
                cs.FlushFinalBlock();
                combined = CsrVertexMapFactory.ToLowerHex(sha.Hash);
            }

            return new UnityMeshGeometryDigest(
                snapshot.Vertices.Length,
                snapshot.TriangleCount,
                posDigest,
                triDigest,
                uvDigest,
                combined);
        }

        public static string ComputeOrderedPositionsDigest(Vector3[] vertices)
        {
            using (var sha = SHA256.Create())
            using (var cs = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write))
            using (var bw = new BinaryWriter(cs, Encoding.UTF8, leaveOpen: true))
            {
                bw.Write(k_PositionsDomain);
                int count = vertices != null ? vertices.Length : 0;
                bw.Write(count);
                for (int i = 0; i < count; i++)
                {
                    var v = vertices[i];
                    bw.Write(CanonicalFloatBits(v.x));
                    bw.Write(CanonicalFloatBits(v.y));
                    bw.Write(CanonicalFloatBits(v.z));
                }

                bw.Flush();
                cs.FlushFinalBlock();
                return CsrVertexMapFactory.ToLowerHex(sha.Hash);
            }
        }

        /// <summary>
        /// Computes a canonical triangle incidence digest ignoring submesh grouping while preserving
        /// triangle winding and exact triangle multiplicity.
        /// </summary>
        public static string ComputeCanonicalTriangleIncidenceDigest(int[] triangles)
        {
            int triCount = triangles != null ? triangles.Length / 3 : 0;
            var canonical = new (int A, int B, int C)[triCount];
            for (int t = 0; t < triCount; t++)
            {
                int a = triangles[t * 3];
                int b = triangles[t * 3 + 1];
                int c = triangles[t * 3 + 2];

                // Cyclic rotation preserving winding so smallest vertex index is first.
                if (b <= a && b <= c)
                {
                    canonical[t] = (b, c, a);
                }
                else if (c <= a && c <= b)
                {
                    canonical[t] = (c, a, b);
                }
                else
                {
                    canonical[t] = (a, b, c);
                }
            }

            Array.Sort(canonical, (x, y) =>
            {
                int cmp = x.A.CompareTo(y.A);
                if (cmp != 0) return cmp;
                cmp = x.B.CompareTo(y.B);
                return cmp != 0 ? cmp : x.C.CompareTo(y.C);
            });

            using (var sha = SHA256.Create())
            using (var cs = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write))
            using (var bw = new BinaryWriter(cs, Encoding.UTF8, leaveOpen: true))
            {
                bw.Write(k_TrianglesDomain);
                bw.Write(triCount);
                for (int t = 0; t < triCount; t++)
                {
                    bw.Write(canonical[t].A);
                    bw.Write(canonical[t].B);
                    bw.Write(canonical[t].C);
                }

                bw.Flush();
                cs.FlushFinalBlock();
                return CsrVertexMapFactory.ToLowerHex(sha.Hash);
            }
        }

        public static string ComputeUv0Digest(Vector2[] uv0)
        {
            if (uv0 == null || uv0.Length == 0)
            {
                return string.Empty;
            }

            using (var sha = SHA256.Create())
            using (var cs = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write))
            using (var bw = new BinaryWriter(cs, Encoding.UTF8, leaveOpen: true))
            {
                bw.Write(k_Uv0Domain);
                bw.Write(uv0.Length);
                for (int i = 0; i < uv0.Length; i++)
                {
                    var uv = uv0[i];
                    bw.Write(CanonicalFloatBits(uv.x));
                    bw.Write(CanonicalFloatBits(uv.y));
                }

                bw.Flush();
                cs.FlushFinalBlock();
                return CsrVertexMapFactory.ToLowerHex(sha.Hash);
            }
        }

        /// <summary>
        /// Discovers all multi-vertex coincident-position classes and computes a per-class local triangle-incidence
        /// discriminator digest that depends only on vertex indices & incident oriented edges (independent of UV0/SubMesh).
        /// </summary>
        public static List<CoincidentVertexClass> FindCoincidentPositionClasses(UnityMeshSnapshot snapshot)
        {
            var result = new List<CoincidentVertexClass>();
            if (snapshot == null || snapshot.Vertices == null || snapshot.Vertices.Length < 2)
            {
                return result;
            }

            var verts = snapshot.Vertices;
            var groups = new Dictionary<(int, int, int), List<int>>(verts.Length);
            var order = new List<(int, int, int)>();

            for (int i = 0; i < verts.Length; i++)
            {
                var v = verts[i];
                if (float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z))
                {
                    continue;
                }

                var key = (CanonicalFloatBits(v.x), CanonicalFloatBits(v.y), CanonicalFloatBits(v.z));
                if (!groups.TryGetValue(key, out var list))
                {
                    list = new List<int>(2);
                    groups.Add(key, list);
                    order.Add(key);
                }

                list.Add(i);
            }

            // Build per-vertex incident oriented triangle neighbor lists only if any coincident class exists.
            List<(int Prev, int Next)>[] incidentByVertex = null;
            for (int k = 0; k < order.Count; k++)
            {
                var list = groups[order[k]];
                if (list.Count < 2)
                {
                    continue;
                }

                if (incidentByVertex == null)
                {
                    incidentByVertex = BuildIncidentOrientedNeighbors(verts.Length, snapshot.Triangles);
                }

                var indices = list.ToArray();
                var perVertexDigests = ComputePerVertexIncidenceDigests(indices, incidentByVertex);
                string disc = ComputeCoincidentClassDiscriminator(indices, perVertexDigests);
                result.Add(new CoincidentVertexClass(indices, disc, perVertexDigests));
            }

            return result;
        }

        private static List<(int Prev, int Next)>[] BuildIncidentOrientedNeighbors(int vertexCount, int[] triangles)
        {
            var incident = new List<(int Prev, int Next)>[vertexCount];
            if (triangles == null)
            {
                return incident;
            }

            int triCount = triangles.Length / 3;
            for (int t = 0; t < triCount; t++)
            {
                int a = triangles[t * 3];
                int b = triangles[t * 3 + 1];
                int c = triangles[t * 3 + 2];
                if ((uint)a >= (uint)vertexCount || (uint)b >= (uint)vertexCount || (uint)c >= (uint)vertexCount)
                {
                    continue;
                }

                (incident[a] ?? (incident[a] = new List<(int, int)>(4))).Add((c, b));
                (incident[b] ?? (incident[b] = new List<(int, int)>(4))).Add((a, c));
                (incident[c] ?? (incident[c] = new List<(int, int)>(4))).Add((b, a));
            }

            return incident;
        }

        private static string[] ComputePerVertexIncidenceDigests(
            int[] classVertexIndices,
            List<(int Prev, int Next)>[] incidentByVertex)
        {
            var perVertex = new string[classVertexIndices.Length];
            for (int i = 0; i < classVertexIndices.Length; i++)
            {
                int v = classVertexIndices[i];
                var neighbors = (uint)v < (uint)incidentByVertex.Length ? incidentByVertex[v] : null;
                using (var sha = SHA256.Create())
                using (var cs = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write))
                using (var bw = new BinaryWriter(cs, Encoding.UTF8, leaveOpen: true))
                {
                    bw.Write(k_ClassDiscDomain);
                    if (neighbors == null || neighbors.Count == 0)
                    {
                        bw.Write(0);
                    }
                    else
                    {
                        var sorted = neighbors.ToArray();
                        Array.Sort(sorted, (x, y) =>
                        {
                            int cmp = x.Prev.CompareTo(y.Prev);
                            return cmp != 0 ? cmp : x.Next.CompareTo(y.Next);
                        });

                        bw.Write(sorted.Length);
                        for (int n = 0; n < sorted.Length; n++)
                        {
                            bw.Write(sorted[n].Prev);
                            bw.Write(sorted[n].Next);
                        }
                    }

                    bw.Flush();
                    cs.FlushFinalBlock();
                    perVertex[i] = CsrVertexMapFactory.ToLowerHex(sha.Hash);
                }
            }

            return perVertex;
        }

        private static string ComputeCoincidentClassDiscriminator(
            int[] classVertexIndices,
            string[] perVertexIncidenceDigests)
        {
            using (var sha = SHA256.Create())
            using (var cs = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write))
            using (var bw = new BinaryWriter(cs, Encoding.UTF8, leaveOpen: true))
            {
                bw.Write(k_ClassDiscDomain);
                bw.Write(classVertexIndices.Length);
                for (int i = 0; i < classVertexIndices.Length; i++)
                {
                    bw.Write(classVertexIndices[i]);
                    bw.Write(perVertexIncidenceDigests[i] ?? string.Empty);
                }

                bw.Flush();
                cs.FlushFinalBlock();
                return CsrVertexMapFactory.ToLowerHex(sha.Hash);
            }
        }

        private static int CanonicalFloatBits(float value)
        {
            if (value == 0f)
            {
                return 0;
            }

            if (float.IsNaN(value))
            {
                return 0x7FC00000;
            }

            return BitConverter.SingleToInt32Bits(value);
        }
    }

    /// <summary>
    /// Binary64 coordinate contract math between <c>CanonicalMeshLocalV1</c> and Unity <c>Mesh</c> local space.
    /// </summary>
    public static class UnitySpaceContractMath
    {
        /// <summary>
        /// Builds the row-vector matrix from raw FBX local coordinates to Unity mesh local coordinates
        /// matching Unity's ModelImporter scale and axis conversion.
        /// </summary>
        public static FbxMatrix4x4 BuildFbxRawToUnityMatrix(float matchedScale, bool bakeAxisConversion)
        {
            double s = matchedScale;
            if (double.IsNaN(s) || double.IsInfinity(s) || s <= 0d)
            {
                throw new ArgumentOutOfRangeException(nameof(matchedScale), "Matched scale must be finite and strictly positive.");
            }

            return bakeAxisConversion
                ? FbxMatrix4x4.Scale(new Vector3d(s, s, -s))
                : FbxMatrix4x4.Scale(new Vector3d(-s, s, s));
        }

        /// <summary>
        /// Builds the exact binary64 row-vector matrix transforming <c>CanonicalMeshLocalV1</c> coordinates
        /// into Unity mesh local coordinates for a target FBX scene with the given matched import scale & axis mode.
        /// </summary>
        public static FbxMatrix4x4 BuildCanonicalToUnityMatrix(
            FbxSceneMetadata targetSceneMetadata,
            float matchedScale,
            bool bakeAxisConversion,
            out bool usedFallback)
        {
            FbxMatrix4x4 canonicalToRaw = CanonicalMeshSpace.BuildCanonicalToRaw(
                targetSceneMetadata.Axes,
                targetSceneMetadata.UnitMeters,
                out usedFallback);
            FbxMatrix4x4 rawToUnity = BuildFbxRawToUnityMatrix(matchedScale, bakeAxisConversion);
            return canonicalToRaw * rawToUnity;
        }

        /// <summary>
        /// Transforms a position delta from <c>CanonicalMeshLocalV1</c> to Unity mesh local space.
        /// </summary>
        public static Vector3 TransformPositionDelta(FbxMatrix4x4 canonicalToUnity, Vector3d canonicalDelta)
        {
            var transformed = canonicalToUnity.TransformVector(canonicalDelta);
            return new Vector3((float)transformed.x, (float)transformed.y, (float)transformed.z);
        }

        /// <summary>
        /// Builds a scale-free normal-delta transform matrix from <paramref name="canonicalToUnity"/>.
        /// For similarity transforms (uniform scale + orthogonal rotation/reflection, which covers all FBX/Unity
        /// axis and unit conversions), this extracts the exact orthogonal factor without scaling delta magnitudes
        /// and WITHOUT renormalizing delta vectors.
        /// </summary>
        public static bool TryBuildNormalDeltaMatrix(FbxMatrix4x4 canonicalToUnity, out FbxMatrix4x4 normalDeltaMatrix)
        {
            normalDeltaMatrix = FbxMatrix4x4.Identity;
            if (!FbxAffineSpaceTransform.TryCreate(canonicalToUnity, out var affine))
            {
                return false;
            }

            // Compute linear row norms of the upper-left 3x3.
            var r0 = new Vector3d(canonicalToUnity[0, 0], canonicalToUnity[0, 1], canonicalToUnity[0, 2]);
            var r1 = new Vector3d(canonicalToUnity[1, 0], canonicalToUnity[1, 1], canonicalToUnity[1, 2]);
            var r2 = new Vector3d(canonicalToUnity[2, 0], canonicalToUnity[2, 1], canonicalToUnity[2, 2]);
            double s0 = r0.magnitude;
            double s1 = r1.magnitude;
            double s2 = r2.magnitude;
            if (s0 <= Vector3d.Epsilon || s1 <= Vector3d.Epsilon || s2 <= Vector3d.Epsilon)
            {
                return false;
            }

            // For uniform similarity transform, dividing by uniform scale preserves exact orientation/reflection.
            double avg = (s0 + s1 + s2) / 3d;
            double relDiff = Math.Max(Math.Abs(s0 - avg), Math.Max(Math.Abs(s1 - avg), Math.Abs(s2 - avg))) / avg;
            if (relDiff <= 1e-6d)
            {
                double invS = 1d / avg;
                normalDeltaMatrix = new FbxMatrix4x4(
                    r0.x * invS, r0.y * invS, r0.z * invS, 0d,
                    r1.x * invS, r1.y * invS, r1.z * invS, 0d,
                    r2.x * invS, r2.y * invS, r2.z * invS, 0d,
                    0d, 0d, 0d, 1d);
                return true;
            }

            // Non-uniform affine fallback: normalized inverse-transpose rows preserve delta scale order-of-magnitude
            // without renormalizing the delta vector itself.
            var inv = affine.InverseOffset;
            var it0 = new Vector3d(inv[0, 0], inv[1, 0], inv[2, 0]);
            var it1 = new Vector3d(inv[0, 1], inv[1, 1], inv[2, 1]);
            var it2 = new Vector3d(inv[0, 2], inv[1, 2], inv[2, 2]);
            double itAvg = (it0.magnitude + it1.magnitude + it2.magnitude) / 3d;
            if (itAvg <= Vector3d.Epsilon)
            {
                return false;
            }

            double invIt = 1d / itAvg;
            normalDeltaMatrix = new FbxMatrix4x4(
                it0.x * invIt, it0.y * invIt, it0.z * invIt, 0d,
                it1.x * invIt, it1.y * invIt, it1.z * invIt, 0d,
                it2.x * invIt, it2.y * invIt, it2.z * invIt, 0d,
                0d, 0d, 0d, 1d);
            return true;
        }

        /// <summary>
        /// Transforms a normal delta using a precomputed scale-free <paramref name="normalDeltaMatrix"/>
        /// without renormalizing the delta vector.
        /// </summary>
        public static Vector3 TransformNormalDelta(FbxMatrix4x4 normalDeltaMatrix, Vector3d canonicalNormalDelta)
        {
            var transformed = normalDeltaMatrix.TransformVector(canonicalNormalDelta);
            return new Vector3((float)transformed.x, (float)transformed.y, (float)transformed.z);
        }
    }
}
