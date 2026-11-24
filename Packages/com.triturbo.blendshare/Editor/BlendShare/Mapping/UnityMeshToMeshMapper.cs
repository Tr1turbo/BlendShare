using System;
using System.Collections.Generic;
using UnityEngine;

namespace Triturbo.BlendShare.Mapping
{
    /// <summary>
    /// Evidence explaining how a target Unity vertex was matched back to source Unity vertex/vertices.
    /// </summary>
    public enum CorrespondenceEvidence
    {
        /// <summary>Target vertex could not be matched cleanly.</summary>
        Unresolved = 0,
        /// <summary>Matched via one or more unique oriented triangle position anchors.</summary>
        CommonTriangle = 1,
        /// <summary>Matched via a globally unique vertex position cluster (1 source, 1 target).</summary>
        UniquePosition = 2,
        /// <summary>Matched as the sole remaining unanchored 1:1 pair within a bijective position cluster.</summary>
        ConditionalResidual = 3,
        /// <summary>Disambiguated among coincident source vertices using UV0.</summary>
        UvDisambiguated = 4,
        /// <summary>Disambiguated among coincident source vertices using single-frame weight-100 blendshapes.</summary>
        BlendShapeDisambiguated = 5
    }

    /// <summary>
    /// Configuration for <see cref="UnityMeshToMeshMapper"/>.
    /// </summary>
    public readonly struct MeshToMeshOptions
    {
        /// <summary>Default exact float32 matching with UV0/blendshape disambiguation enabled.</summary>
        public static readonly MeshToMeshOptions Default = new(
            positionEpsilon: 0f,
            allowAttributeDisambiguation: true,
            uv0SqrDistanceTolerance: 1.001e-6f,
            blendShapeDeltaTolerance: 1.2e-5f);

        /// <summary>
        /// Maximum Euclidean distance between matching source and target vertex positions.
        /// Zero enforces exact float32 coordinate equality (recommended for NDMF / same-exporter meshes);
        /// a small positive value (e.g. <c>1e-5f</c>) accommodates Blender round-trip requantization.
        /// </summary>
        public float PositionEpsilon { get; }

        /// <summary>Whether UV0 and single-frame blendshape deltas may disambiguate remaining coincident vertices.</summary>
        public bool AllowAttributeDisambiguation { get; }

        /// <summary>Maximum squared UV0 distance for UV disambiguation.</summary>
        public float Uv0SqrDistanceTolerance { get; }

        /// <summary>Maximum per-axis absolute blendshape delta error for blendshape disambiguation.</summary>
        public float BlendShapeDeltaTolerance { get; }

        /// <summary>Creates mesh-to-mesh mapping options.</summary>
        public MeshToMeshOptions(
            float positionEpsilon = 0f,
            bool allowAttributeDisambiguation = true,
            float uv0SqrDistanceTolerance = 1.001e-6f,
            float blendShapeDeltaTolerance = 1.2e-5f)
        {
            PositionEpsilon = positionEpsilon > 0f && !float.IsNaN(positionEpsilon) && !float.IsInfinity(positionEpsilon)
                ? positionEpsilon
                : 0f;
            AllowAttributeDisambiguation = allowAttributeDisambiguation;
            Uv0SqrDistanceTolerance = uv0SqrDistanceTolerance > 0f ? uv0SqrDistanceTolerance : 1.001e-6f;
            BlendShapeDeltaTolerance = blendShapeDeltaTolerance > 0f ? blendShapeDeltaTolerance : 1.2e-5f;
        }
    }

    /// <summary>
    /// Pure target-to-source vertex correspondence between two Unity meshes in the same local space.
    /// Supports 1:1 reorders, N:1 vertex merges, and 1:N vertex splits without assuming a bijection.
    /// </summary>
    public sealed class MeshVertexCorrespondence
    {
        /// <summary>Whether every target vertex resolved to at least one source vertex.</summary>
        public bool Succeeded { get; }

        /// <summary>Human-readable diagnostic summary.</summary>
        public string Diagnostic { get; }

        /// <summary>Number of source mesh vertices.</summary>
        public int SourceVertexCount { get; }

        /// <summary>Number of target mesh vertices.</summary>
        public int TargetVertexCount { get; }

        /// <summary>
        /// For each target vertex index <c>t</c>, sorted distinct source vertex indices mapped to <c>t</c>.
        /// Empty when <see cref="Evidence"/>[t] is <see cref="CorrespondenceEvidence.Unresolved"/>.
        /// </summary>
        public int[][] TargetToSourceVertices { get; }

        /// <summary>Resolution method for each target vertex.</summary>
        public CorrespondenceEvidence[] Evidence { get; }

        /// <summary>Count of unique oriented triangle anchors shared by both meshes.</summary>
        public int CommonTriangleAnchorCount { get; }

        /// <summary>Count of target vertices mapped to two or more merged source vertices (N:1).</summary>
        public int MergedTargetVertexCount { get; }

        /// <summary>Count of source vertices mapped to two or more split target vertices (1:N).</summary>
        public int SplitSourceVertexCount { get; }

        /// <summary>Count of 1:1 target vertices whose index differs from their source vertex index.</summary>
        public int MovedDirectCount { get; }

        /// <summary>Count of target vertices that could not be resolved.</summary>
        public int UnresolvedTargetCount { get; }

        internal MeshVertexCorrespondence(
            bool succeeded,
            string diagnostic,
            int sourceVertexCount,
            int targetVertexCount,
            int[][] targetToSourceVertices,
            CorrespondenceEvidence[] evidence,
            int commonTriangleAnchorCount,
            int mergedTargetVertexCount,
            int splitSourceVertexCount,
            int movedDirectCount,
            int unresolvedTargetCount)
        {
            Succeeded = succeeded;
            Diagnostic = diagnostic ?? string.Empty;
            SourceVertexCount = sourceVertexCount;
            TargetVertexCount = targetVertexCount;
            TargetToSourceVertices = targetToSourceVertices ?? Array.Empty<int[]>();
            Evidence = evidence ?? Array.Empty<CorrespondenceEvidence>();
            CommonTriangleAnchorCount = commonTriangleAnchorCount;
            MergedTargetVertexCount = mergedTargetVertexCount;
            SplitSourceVertexCount = splitSourceVertexCount;
            MovedDirectCount = movedDirectCount;
            UnresolvedTargetCount = unresolvedTargetCount;
        }

        /// <summary>Creates a failed correspondence result.</summary>
        public static MeshVertexCorrespondence Failed(string diagnostic, int sourceVertexCount = 0, int targetVertexCount = 0)
        {
            int tCount = Math.Max(0, targetVertexCount);
            var empty = new int[tCount][];
            for (int i = 0; i < tCount; i++) empty[i] = Array.Empty<int>();
            return new MeshVertexCorrespondence(
                succeeded: false,
                diagnostic: diagnostic,
                sourceVertexCount: Math.Max(0, sourceVertexCount),
                targetVertexCount: tCount,
                targetToSourceVertices: empty,
                evidence: new CorrespondenceEvidence[tCount],
                commonTriangleAnchorCount: 0,
                mergedTargetVertexCount: 0,
                splitSourceVertexCount: 0,
                movedDirectCount: 0,
                unresolvedTargetCount: tCount);
        }
    }

    /// <summary>
    /// Computes target-to-source vertex correspondences between two Unity meshes in the same local space
    /// using unique oriented triangle anchors, globally unique positions, UV0/blendshape disambiguation,
    /// and guarded local-bijection conditional residual matching.
    /// </summary>
    public static class UnityMeshToMeshMapper
    {
        private const double MaxGridCoordinateMagnitude = 1e15d;

        /// <summary>
        /// Maps every target vertex in <paramref name="target"/> back to one or more source vertices in <paramref name="source"/>.
        /// </summary>
        public static MeshVertexCorrespondence Map(
            UnityMeshSnapshot source,
            UnityMeshSnapshot target,
            MeshToMeshOptions options = default)
        {
            if (options.Uv0SqrDistanceTolerance <= 0f && options.BlendShapeDeltaTolerance <= 0f)
            {
                options = MeshToMeshOptions.Default;
            }

            if (source == null || source.Vertices.Length == 0)
            {
                return MeshVertexCorrespondence.Failed(
                    "Mesh-to-mesh mapping requires a non-empty source Unity mesh.",
                    0,
                    target?.Vertices.Length ?? 0);
            }

            if (target == null || target.Vertices.Length == 0)
            {
                return MeshVertexCorrespondence.Failed(
                    "Mesh-to-mesh mapping requires a non-empty target Unity mesh.",
                    source.Vertices.Length,
                    0);
            }

            var srcVerts = source.Vertices;
            var dstVerts = target.Vertices;
            int srcCount = srcVerts.Length;
            int dstCount = dstVerts.Length;

            if (!ValidateFiniteVertices(srcVerts) || !ValidateFiniteVertices(dstVerts))
            {
                return MeshVertexCorrespondence.Failed(
                    "Mesh-to-mesh mapping cannot process meshes with non-finite vertex coordinates.",
                    srcCount,
                    dstCount);
            }

            var srcTris = source.Triangles;
            var dstTris = target.Triangles;
            int srcTriCount = srcTris.Length / 3;
            int dstTriCount = dstTris.Length / 3;

            if (!ValidateTriangleIndices(srcTris, srcTriCount * 3, srcCount) ||
                !ValidateTriangleIndices(dstTris, dstTriCount * 3, dstCount))
            {
                return MeshVertexCorrespondence.Failed(
                    "Mesh-to-mesh mapping received out-of-range triangle indices.",
                    srcCount,
                    dstCount);
            }

            double epsilon = options.PositionEpsilon;

            // Step 1: Partition source and target vertices into position clusters.
            if (!TryBuildPositionClusters(
                    srcVerts,
                    dstVerts,
                    epsilon,
                    out int clusterCount,
                    out int[] srcCluster,
                    out int[] dstCluster,
                    out List<int>[] srcByCluster,
                    out List<int>[] dstByCluster))
            {
                return MeshVertexCorrespondence.Failed(
                    "Mesh-to-mesh spatial grid exceeded numeric bounds for the requested position epsilon.",
                    srcCount,
                    dstCount);
            }

            // Step 2: Unique oriented triangle anchors (enforcing pairwise Euclidean distance <= epsilon
            // on every corner so transitive Union-Find chaining can never accept links farther than epsilon).
            var srcTriTable = BuildTriangleIndex(srcCluster, srcTris, srcTriCount);
            var dstTriTable = BuildTriangleIndex(dstCluster, dstTris, dstTriCount);

            var targetToSources = new HashSet<int>[dstCount];
            var sourceToTargets = new HashSet<int>[srcCount];
            int anchorCount = 0;

            foreach (var kv in srcTriTable)
            {
                if (kv.Value.Count != 1) continue;
                if (!dstTriTable.TryGetValue(kv.Key, out var dstList) || dstList.Count != 1) continue;

                var st = kv.Value[0];
                var dt = dstList[0];
                if (!WithinPositionEpsilon(srcVerts[st.A], dstVerts[dt.A], epsilon) ||
                    !WithinPositionEpsilon(srcVerts[st.B], dstVerts[dt.B], epsilon) ||
                    !WithinPositionEpsilon(srcVerts[st.C], dstVerts[dt.C], epsilon))
                {
                    continue;
                }

                anchorCount++;
                AddLink(dt.A, st.A, targetToSources, sourceToTargets);
                AddLink(dt.B, st.B, targetToSources, sourceToTargets);
                AddLink(dt.C, st.C, targetToSources, sourceToTargets);
            }

            var evidence = new CorrespondenceEvidence[dstCount];
            for (int t = 0; t < dstCount; t++)
            {
                if (targetToSources[t] != null && targetToSources[t].Count > 0)
                {
                    evidence[t] = CorrespondenceEvidence.CommonTriangle;
                }
            }

            // Step 3: Globally unique positions (1 source and 1 target in the position cluster within epsilon).
            for (int c = 0; c < clusterCount; c++)
            {
                var sList = srcByCluster[c];
                var dList = dstByCluster[c];
                if (sList == null || dList == null || sList.Count != 1 || dList.Count != 1) continue;

                int s = sList[0];
                int t = dList[0];
                if (evidence[t] == CorrespondenceEvidence.Unresolved &&
                    WithinPositionEpsilon(srcVerts[s], dstVerts[t], epsilon))
                {
                    AddLink(t, s, targetToSources, sourceToTargets);
                    evidence[t] = CorrespondenceEvidence.UniquePosition;
                }
            }

            // Step 4: Run UV0 and single-frame BlendShape disambiguation BEFORE conditional residual so
            // splits/merges with attribute evidence are never forced into a false 1:1 residual pair.
            bool[] rejectedByAttributes = null;
            if (options.AllowAttributeDisambiguation && HasUnresolved(evidence))
            {
                rejectedByAttributes = DisambiguateByAttributes(
                    source,
                    target,
                    dstCluster,
                    srcByCluster,
                    targetToSources,
                    sourceToTargets,
                    evidence,
                    options,
                    epsilon);
            }

            // Step 5: Guarded conditional residual 1:1 — only when the position cluster has equal source/target
            // cardinality, every anchored member in the cluster is 1:1, the sole remaining pair lies within
            // epsilon, and attribute disambiguation did not already contradict all candidates for that target.
            for (int c = 0; c < clusterCount; c++)
            {
                var sList = srcByCluster[c];
                var dList = dstByCluster[c];
                if (sList == null || dList == null || sList.Count == 0 || sList.Count != dList.Count) continue;

                int unlinkedSource = -1;
                int unlinkedSourceCount = 0;
                bool bijectiveCluster = true;
                for (int i = 0; i < sList.Count; i++)
                {
                    int s = sList[i];
                    var tSet = sourceToTargets[s];
                    if (tSet == null || tSet.Count == 0)
                    {
                        unlinkedSource = s;
                        unlinkedSourceCount++;
                    }
                    else if (tSet.Count != 1)
                    {
                        bijectiveCluster = false;
                        break;
                    }
                }

                if (!bijectiveCluster || unlinkedSourceCount != 1) continue;

                int unlinkedTarget = -1;
                int unlinkedTargetCount = 0;
                for (int i = 0; i < dList.Count; i++)
                {
                    int t = dList[i];
                    var sSet = targetToSources[t];
                    if (sSet == null || sSet.Count == 0)
                    {
                        unlinkedTarget = t;
                        unlinkedTargetCount++;
                    }
                    else if (sSet.Count != 1)
                    {
                        bijectiveCluster = false;
                        break;
                    }
                }

                if (!bijectiveCluster || unlinkedTargetCount != 1) continue;
                if (rejectedByAttributes != null && rejectedByAttributes[unlinkedTarget]) continue;
                if (!WithinPositionEpsilon(srcVerts[unlinkedSource], dstVerts[unlinkedTarget], epsilon)) continue;

                AddLink(unlinkedTarget, unlinkedSource, targetToSources, sourceToTargets);
                evidence[unlinkedTarget] = CorrespondenceEvidence.ConditionalResidual;
            }

            // Build final sorted arrays and diagnostics.
            var resultGroups = new int[dstCount][];
            int unresolvedCount = 0;
            int mergedCount = 0;
            int movedDirectCount = 0;

            for (int t = 0; t < dstCount; t++)
            {
                var set = targetToSources[t];
                if (set == null || set.Count == 0)
                {
                    resultGroups[t] = Array.Empty<int>();
                    evidence[t] = CorrespondenceEvidence.Unresolved;
                    unresolvedCount++;
                    continue;
                }

                var arr = new int[set.Count];
                set.CopyTo(arr);
                Array.Sort(arr);
                resultGroups[t] = arr;

                if (arr.Length > 1)
                {
                    mergedCount++;
                }
                else if (arr[0] != t)
                {
                    movedDirectCount++;
                }
            }

            int splitSourceCount = 0;
            for (int s = 0; s < srcCount; s++)
            {
                if (sourceToTargets[s] != null && sourceToTargets[s].Count > 1)
                {
                    splitSourceCount++;
                }
            }

            bool succeeded = unresolvedCount == 0;
            string diagnostic = succeeded
                ? string.Empty
                : $"Mesh-to-mesh mapping left {unresolvedCount} of {dstCount} target vertices unresolved " +
                  $"(anchors={anchorCount}, merged={mergedCount}, split={splitSourceCount}).";

            return new MeshVertexCorrespondence(
                succeeded: succeeded,
                diagnostic: diagnostic,
                sourceVertexCount: srcCount,
                targetVertexCount: dstCount,
                targetToSourceVertices: resultGroups,
                evidence: evidence,
                commonTriangleAnchorCount: anchorCount,
                mergedTargetVertexCount: mergedCount,
                splitSourceVertexCount: splitSourceCount,
                movedDirectCount: movedDirectCount,
                unresolvedTargetCount: unresolvedCount);
        }

        // ─── Position Clustering (Exact Float32 or Spatial-Grid Connected Components) ──

        private static bool TryBuildPositionClusters(
            Vector3[] srcVerts,
            Vector3[] dstVerts,
            double epsilon,
            out int clusterCount,
            out int[] srcCluster,
            out int[] dstCluster,
            out List<int>[] srcByCluster,
            out List<int>[] dstByCluster)
        {
            int srcCount = srcVerts.Length;
            int dstCount = dstVerts.Length;

            if (epsilon <= 0d)
            {
                var clusterByKey = new Dictionary<PosKey, int>(srcCount);
                srcCluster = new int[srcCount];
                int next = 0;
                for (int s = 0; s < srcCount; s++)
                {
                    var key = new PosKey(srcVerts[s]);
                    if (!clusterByKey.TryGetValue(key, out int cid))
                    {
                        cid = next++;
                        clusterByKey.Add(key, cid);
                    }

                    srcCluster[s] = cid;
                }

                dstCluster = new int[dstCount];
                for (int t = 0; t < dstCount; t++)
                {
                    var key = new PosKey(dstVerts[t]);
                    if (!clusterByKey.TryGetValue(key, out int cid))
                    {
                        cid = next++;
                        clusterByKey.Add(key, cid);
                    }

                    dstCluster[t] = cid;
                }

                clusterCount = next;
                GroupByCluster(clusterCount, srcCluster, dstCluster, out srcByCluster, out dstByCluster);
                return true;
            }

            // Spatial-grid 27-neighbor clustering with double-precision distance and overflow guard.
            double invCell = 1d / epsilon;
            if (double.IsNaN(invCell) || double.IsInfinity(invCell))
            {
                clusterCount = 0;
                srcCluster = dstCluster = null;
                srcByCluster = dstByCluster = null;
                return false;
            }

            int total = srcCount + dstCount;
            var parent = new int[total];
            for (int i = 0; i < total; i++) parent[i] = i;

            int Find(int x)
            {
                while (parent[x] != x)
                {
                    parent[x] = parent[parent[x]];
                    x = parent[x];
                }

                return x;
            }

            void Union(int a, int b)
            {
                int ra = Find(a);
                int rb = Find(b);
                if (ra != rb) parent[rb] = ra;
            }

            var grid = new Dictionary<CellKey, List<int>>(srcCount);
            for (int s = 0; s < srcCount; s++)
            {
                var p = srcVerts[s];
                if (!TryToCell(p, invCell, out var cell))
                {
                    clusterCount = 0;
                    srcCluster = dstCluster = null;
                    srcByCluster = dstByCluster = null;
                    return false;
                }

                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (!grid.TryGetValue(new CellKey(cell.X + dx, cell.Y + dy, cell.Z + dz), out var peers)) continue;
                    for (int i = 0; i < peers.Count; i++)
                    {
                        int peer = peers[i];
                        if (WithinPositionEpsilon(p, srcVerts[peer], epsilon))
                        {
                            Union(s, peer);
                        }
                    }
                }

                if (!grid.TryGetValue(cell, out var bucket))
                {
                    bucket = new List<int>(2);
                    grid.Add(cell, bucket);
                }

                bucket.Add(s);
            }

            for (int t = 0; t < dstCount; t++)
            {
                var p = dstVerts[t];
                if (!TryToCell(p, invCell, out var cell))
                {
                    clusterCount = 0;
                    srcCluster = dstCluster = null;
                    srcByCluster = dstByCluster = null;
                    return false;
                }

                int dstNode = srcCount + t;
                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (!grid.TryGetValue(new CellKey(cell.X + dx, cell.Y + dy, cell.Z + dz), out var peers)) continue;
                    for (int i = 0; i < peers.Count; i++)
                    {
                        int s = peers[i];
                        if (WithinPositionEpsilon(p, srcVerts[s], epsilon))
                        {
                            Union(dstNode, s);
                        }
                    }
                }
            }

            var rootToCluster = new Dictionary<int, int>(srcCount);
            srcCluster = new int[srcCount];
            dstCluster = new int[dstCount];
            int nextCluster = 0;

            for (int s = 0; s < srcCount; s++)
            {
                int r = Find(s);
                if (!rootToCluster.TryGetValue(r, out int cid))
                {
                    cid = nextCluster++;
                    rootToCluster.Add(r, cid);
                }

                srcCluster[s] = cid;
            }

            for (int t = 0; t < dstCount; t++)
            {
                int r = Find(srcCount + t);
                if (!rootToCluster.TryGetValue(r, out int cid))
                {
                    cid = nextCluster++;
                    rootToCluster.Add(r, cid);
                }

                dstCluster[t] = cid;
            }

            clusterCount = nextCluster;
            GroupByCluster(clusterCount, srcCluster, dstCluster, out srcByCluster, out dstByCluster);
            return true;
        }

        private static void GroupByCluster(
            int clusterCount,
            int[] srcCluster,
            int[] dstCluster,
            out List<int>[] srcByCluster,
            out List<int>[] dstByCluster)
        {
            srcByCluster = new List<int>[clusterCount];
            dstByCluster = new List<int>[clusterCount];

            for (int s = 0; s < srcCluster.Length; s++)
            {
                int c = srcCluster[s];
                var list = srcByCluster[c];
                if (list == null)
                {
                    list = new List<int>(1);
                    srcByCluster[c] = list;
                }

                list.Add(s);
            }

            for (int t = 0; t < dstCluster.Length; t++)
            {
                int c = dstCluster[t];
                var list = dstByCluster[c];
                if (list == null)
                {
                    list = new List<int>(1);
                    dstByCluster[c] = list;
                }

                list.Add(t);
            }
        }

        private static Dictionary<TriKey, List<TriTriple>> BuildTriangleIndex(
            int[] vertexCluster,
            int[] triangles,
            int triangleCount)
        {
            var table = new Dictionary<TriKey, List<TriTriple>>(triangleCount);
            for (int i = 0; i < triangleCount; i++)
            {
                int v0 = triangles[i * 3];
                int v1 = triangles[i * 3 + 1];
                int v2 = triangles[i * 3 + 2];
                int c0 = vertexCluster[v0];
                int c1 = vertexCluster[v1];
                int c2 = vertexCluster[v2];
                if (c0 == c1 || c1 == c2 || c2 == c0) continue;

                TriKey key;
                TriTriple triple;
                if (c0 < c1 && c0 < c2)
                {
                    key = new TriKey(c0, c1, c2);
                    triple = new TriTriple(v0, v1, v2);
                }
                else if (c1 < c2)
                {
                    key = new TriKey(c1, c2, c0);
                    triple = new TriTriple(v1, v2, v0);
                }
                else
                {
                    key = new TriKey(c2, c0, c1);
                    triple = new TriTriple(v2, v0, v1);
                }

                if (!table.TryGetValue(key, out var list))
                {
                    list = new List<TriTriple>(1);
                    table.Add(key, list);
                }

                list.Add(triple);
            }

            return table;
        }

        // ─── Step 4: Order-Independent UV0 & BlendShape Disambiguation ─────────────

        private static bool[] DisambiguateByAttributes(
            UnityMeshSnapshot source,
            UnityMeshSnapshot target,
            int[] dstCluster,
            List<int>[] srcByCluster,
            HashSet<int>[] targetToSources,
            HashSet<int>[] sourceToTargets,
            CorrespondenceEvidence[] evidence,
            MeshToMeshOptions options,
            double epsilon)
        {
            int dstCount = evidence.Length;
            var rejectedByAttributes = new bool[dstCount];
            var srcVerts = source.Vertices;
            var dstVerts = target.Vertices;

            bool hasUv0 = source.Uv0 != null && target.Uv0 != null;
            float uvTol = options.Uv0SqrDistanceTolerance;

            // Collect unresolved target vertices and their pairwise-within-epsilon source candidates.
            var unresolvedTargets = new List<int>();
            var remainingByTarget = new Dictionary<int, List<int>>();
            for (int t = 0; t < dstCount; t++)
            {
                if (evidence[t] != CorrespondenceEvidence.Unresolved) continue;
                var clusterCands = srcByCluster[dstCluster[t]];
                if (clusterCands == null || clusterCands.Count == 0) continue;

                var withinEps = new List<int>(clusterCands.Count);
                for (int i = 0; i < clusterCands.Count; i++)
                {
                    int s = clusterCands[i];
                    if (WithinPositionEpsilon(srcVerts[s], dstVerts[t], epsilon))
                    {
                        withinEps.Add(s);
                    }
                }

                if (withinEps.Count == 0) continue;

                if (hasUv0)
                {
                    var uvFiltered = new List<int>(withinEps.Count);
                    var tv = target.Uv0[t];
                    for (int i = 0; i < withinEps.Count; i++)
                    {
                        int s = withinEps[i];
                        var sv = source.Uv0[s];
                        float dx = sv.x - tv.x;
                        float dy = sv.y - tv.y;
                        if (dx * dx + dy * dy <= uvTol)
                        {
                            uvFiltered.Add(s);
                        }
                    }

                    // Preserve empty candidate sets (contradiction): never restore UV-rejected candidates.
                    if (uvFiltered.Count == 0)
                    {
                        rejectedByAttributes[t] = true;
                        continue;
                    }

                    withinEps = uvFiltered;
                }

                unresolvedTargets.Add(t);
                remainingByTarget[t] = withinEps;
            }

            if (unresolvedTargets.Count == 0) return rejectedByAttributes;

            // Evaluate ALL shared single-frame weight-100 blendshapes against ALL remaining candidates
            // (including singletons!) so conflicting shapes reject cleanly regardless of enumeration order.
            var sharedNames = CollectSharedShapeNames(source.BlendShapes, target.BlendShapes);
            bool evaluatedAnyShape = false;
            if (sharedNames.Count > 0)
            {
                float bsTol = options.BlendShapeDeltaTolerance;
                for (int si = 0; si < sharedNames.Count; si++)
                {
                    string name = sharedNames[si];
                    if (!source.BlendShapes.TryGetSingleFrameDeltaVertices(name, out var srcDeltas) ||
                        !target.BlendShapes.TryGetSingleFrameDeltaVertices(name, out var dstDeltas) ||
                        srcDeltas == null || dstDeltas == null ||
                        srcDeltas.Length != srcVerts.Length ||
                        dstDeltas.Length != dstVerts.Length)
                    {
                        continue;
                    }

                    evaluatedAnyShape = true;
                    for (int i = 0; i < unresolvedTargets.Count; i++)
                    {
                        int t = unresolvedTargets[i];
                        var cands = remainingByTarget[t];
                        if (cands.Count == 0) continue;

                        var td = dstDeltas[t];
                        for (int j = cands.Count - 1; j >= 0; j--)
                        {
                            var sd = srcDeltas[cands[j]];
                            if (Math.Abs(sd.x - td.x) > bsTol ||
                                Math.Abs(sd.y - td.y) > bsTol ||
                                Math.Abs(sd.z - td.z) > bsTol)
                            {
                                cands.RemoveAt(j);
                            }
                        }
                    }
                }
            }

            for (int i = 0; i < unresolvedTargets.Count; i++)
            {
                int t = unresolvedTargets[i];
                var cands = remainingByTarget[t];
                if (cands.Count == 0)
                {
                    rejectedByAttributes[t] = true;
                    continue;
                }

                if (cands.Count == 1 && (hasUv0 || evaluatedAnyShape))
                {
                    AddLink(t, cands[0], targetToSources, sourceToTargets);
                    evidence[t] = evaluatedAnyShape && (!hasUv0 || srcByCluster[dstCluster[t]].Count > 1)
                        ? (hasUv0 && CountUvMatches(source.Uv0, target.Uv0[t], srcByCluster[dstCluster[t]], srcVerts, dstVerts[t], epsilon, uvTol) == 1
                            ? CorrespondenceEvidence.UvDisambiguated
                            : CorrespondenceEvidence.BlendShapeDisambiguated)
                        : CorrespondenceEvidence.UvDisambiguated;
                }
            }

            return rejectedByAttributes;
        }

        private static int CountUvMatches(
            Vector2[] srcUv,
            Vector2 tv,
            List<int> clusterCands,
            Vector3[] srcVerts,
            Vector3 tp,
            double epsilon,
            float uvTol)
        {
            int count = 0;
            for (int i = 0; i < clusterCands.Count; i++)
            {
                int s = clusterCands[i];
                if (!WithinPositionEpsilon(srcVerts[s], tp, epsilon)) continue;
                float dx = srcUv[s].x - tv.x;
                float dy = srcUv[s].y - tv.y;
                if (dx * dx + dy * dy <= uvTol) count++;
            }

            return count;
        }

        private static List<string> CollectSharedShapeNames(
            IUnityBlendShapeSnapshot source,
            IUnityBlendShapeSnapshot target)
        {
            var shared = new List<string>();
            if (source == null || target == null) return shared;

            var srcNames = source.SingleFrameShapeNames;
            var dstNames = target.SingleFrameShapeNames;
            if (srcNames == null || srcNames.Count == 0 || dstNames == null || dstNames.Count == 0)
            {
                return shared;
            }

            var dstSet = new HashSet<string>(dstNames, StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < srcNames.Count; i++)
            {
                string name = srcNames[i];
                if (!string.IsNullOrEmpty(name) && dstSet.Contains(name) && seen.Add(name))
                {
                    shared.Add(name);
                }
            }

            return shared;
        }

        // ─── Helpers ───────────────────────────────────────────────────────────────

        private static bool WithinPositionEpsilon(Vector3 a, Vector3 b, double epsilon)
        {
            if (epsilon <= 0d)
            {
                return new PosKey(a).Equals(new PosKey(b));
            }

            // Use double precision to prevent float32 underflow/overflow on extreme scales.
            double dx = (double)a.x - (double)b.x;
            double dy = (double)a.y - (double)b.y;
            double dz = (double)a.z - (double)b.z;
            double sqrDist = dx * dx + dy * dy + dz * dz;
            double epsSqr = epsilon * epsilon;
            if (double.IsInfinity(sqrDist) || double.IsInfinity(epsSqr))
            {
                double maxComponent = Math.Max(Math.Abs(dx), Math.Max(Math.Abs(dy), Math.Abs(dz)));
                if (maxComponent > epsilon) return false;
                double nx = dx / epsilon;
                double ny = dy / epsilon;
                double nz = dz / epsilon;
                return nx * nx + ny * ny + nz * nz <= 1d;
            }

            return sqrDist <= epsSqr;
        }

        private static bool HasUnresolved(CorrespondenceEvidence[] evidence)
        {
            for (int i = 0; i < evidence.Length; i++)
            {
                if (evidence[i] == CorrespondenceEvidence.Unresolved) return true;
            }

            return false;
        }

        private static void AddLink(
            int targetVertex,
            int sourceVertex,
            HashSet<int>[] targetToSources,
            HashSet<int>[] sourceToTargets)
        {
            var tSet = targetToSources[targetVertex];
            if (tSet == null)
            {
                tSet = new HashSet<int>();
                targetToSources[targetVertex] = tSet;
            }

            tSet.Add(sourceVertex);

            var sSet = sourceToTargets[sourceVertex];
            if (sSet == null)
            {
                sSet = new HashSet<int>();
                sourceToTargets[sourceVertex] = sSet;
            }

            sSet.Add(targetVertex);
        }

        private static bool ValidateFiniteVertices(Vector3[] verts)
        {
            for (int i = 0; i < verts.Length; i++)
            {
                var v = verts[i];
                if (float.IsNaN(v.x) || float.IsInfinity(v.x) ||
                    float.IsNaN(v.y) || float.IsInfinity(v.y) ||
                    float.IsNaN(v.z) || float.IsInfinity(v.z))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool ValidateTriangleIndices(int[] tris, int length, int vertexCount)
        {
            for (int i = 0; i < length; i++)
            {
                if ((uint)tris[i] >= (uint)vertexCount)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryToCell(Vector3 p, double invCell, out CellKey cell)
        {
            double fx = Math.Floor((double)p.x * invCell);
            double fy = Math.Floor((double)p.y * invCell);
            double fz = Math.Floor((double)p.z * invCell);
            if (double.IsNaN(fx) || double.IsNaN(fy) || double.IsNaN(fz) ||
                Math.Abs(fx) > MaxGridCoordinateMagnitude ||
                Math.Abs(fy) > MaxGridCoordinateMagnitude ||
                Math.Abs(fz) > MaxGridCoordinateMagnitude)
            {
                cell = default;
                return false;
            }

            cell = new CellKey((long)fx, (long)fy, (long)fz);
            return true;
        }

        private readonly struct PosKey : IEquatable<PosKey>
        {
            private readonly int x, y, z;

            public PosKey(Vector3 v)
            {
                x = BitConverter.SingleToInt32Bits(v.x == 0f ? 0f : v.x);
                y = BitConverter.SingleToInt32Bits(v.y == 0f ? 0f : v.y);
                z = BitConverter.SingleToInt32Bits(v.z == 0f ? 0f : v.z);
            }

            public bool Equals(PosKey other) => x == other.x && y == other.y && z == other.z;
            public override bool Equals(object obj) => obj is PosKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = 17;
                    h = h * 31 + x;
                    h = h * 31 + y;
                    h = h * 31 + z;
                    return h;
                }
            }
        }

        private readonly struct CellKey : IEquatable<CellKey>
        {
            public readonly long X, Y, Z;

            public CellKey(long x, long y, long z)
            {
                X = x;
                Y = y;
                Z = z;
            }

            public bool Equals(CellKey other) => X == other.X && Y == other.Y && Z == other.Z;
            public override bool Equals(object obj) => obj is CellKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = 17;
                    h = h * 31 + X.GetHashCode();
                    h = h * 31 + Y.GetHashCode();
                    h = h * 31 + Z.GetHashCode();
                    return h;
                }
            }
        }

        private readonly struct TriKey : IEquatable<TriKey>
        {
            private readonly int a, b, c;

            public TriKey(int a, int b, int c)
            {
                this.a = a;
                this.b = b;
                this.c = c;
            }

            public bool Equals(TriKey other) => a == other.a && b == other.b && c == other.c;
            public override bool Equals(object obj) => obj is TriKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = a;
                    h = h * 397 ^ b;
                    h = h * 397 ^ c;
                    return h;
                }
            }
        }

        private readonly struct TriTriple
        {
            public readonly int A, B, C;

            public TriTriple(int a, int b, int c)
            {
                A = a;
                B = b;
                C = c;
            }
        }
    }
}
