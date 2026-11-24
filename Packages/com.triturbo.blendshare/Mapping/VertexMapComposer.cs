using System;
using System.Collections.Generic;
using Triturbo.BlendShare.Primitives;

namespace Triturbo.BlendShare.Mapping
{
    /// <summary>
    /// Policy controlling how <see cref="VertexMapComposer"/> handles target vertices mapped from
    /// multiple source vertices (N:1 merges) whose cached FBX control-point groups differ.
    /// </summary>
    public enum VertexMapComposePolicy
    {
        /// <summary>Take the sorted distinct union of source FBX control-point groups (legacy opt-in only).</summary>
        AllowMergedUnion = 0,
        /// <summary>Reject any merged target vertex whose source vertices map to different FBX control-point groups.</summary>
        RejectConflictingGroups = 1
    }

    /// <summary>
    /// Result of composing a source Unity-to-FBX mapping with a target-to-source Unity mesh correspondence.
    /// </summary>
    public sealed class VertexMapComposeResult
    {
        /// <summary>Whether every target vertex received a valid non-empty FBX control-point group.</summary>
        public bool Succeeded { get; }

        /// <summary>Human-readable diagnostic summary.</summary>
        public string Diagnostic { get; }

        /// <summary>Composed sorted distinct FBX control-point index groups per target Unity vertex.</summary>
        public int[][] TargetFbxGroups { get; }

        /// <summary>Number of target vertices that failed to resolve to any FBX control point.</summary>
        public int UnresolvedTargetCount { get; }

        /// <summary>Number of merged target vertices whose source vertices had differing FBX control-point groups.</summary>
        public int ConflictingMergedTargetCount { get; }

        /// <summary>Target vertex indices where N:1 source vertices had conflicting FBX control-point groups.</summary>
        public IReadOnlyList<int> ConflictingTargetVertices { get; }

        internal VertexMapComposeResult(
            bool succeeded,
            string diagnostic,
            int[][] targetFbxGroups,
            int unresolvedTargetCount,
            int conflictingMergedTargetCount,
            int[] conflictingTargetVertices = null)
        {
            Succeeded = succeeded;
            Diagnostic = diagnostic ?? string.Empty;
            TargetFbxGroups = targetFbxGroups ?? Array.Empty<int[]>();
            UnresolvedTargetCount = unresolvedTargetCount;
            ConflictingMergedTargetCount = conflictingMergedTargetCount;
            ConflictingTargetVertices = conflictingTargetVertices ?? Array.Empty<int>();
        }
    }

    /// <summary>
    /// Pure composer that projects a cached <c>Source Unity Vertex -> FBX Control Points</c> map
    /// across a <see cref="MeshVertexCorrespondence"/> (<c>Target Unity Vertex -> Source Unity Vertices</c>).
    /// </summary>
    public static class VertexMapComposer
    {
        /// <summary>
        /// BlendShare 2.0 entry point composing an immutable <see cref="CsrVertexMap"/> with explicit
        /// conflict rejection by default (<see cref="VertexMapComposePolicy.RejectConflictingGroups"/>).
        /// </summary>
        public static VertexMapComposeResult Compose(
            CsrVertexMap sourceCsr,
            MeshVertexCorrespondence correspondence,
            VertexMapComposePolicy policy = VertexMapComposePolicy.RejectConflictingGroups)
        {
            if (sourceCsr == null)
            {
                return new VertexMapComposeResult(
                    succeeded: false,
                    diagnostic: "Composition requires a non-null source CSR vertex map.",
                    targetFbxGroups: Array.Empty<int[]>(),
                    unresolvedTargetCount: correspondence != null ? correspondence.TargetVertexCount : 0,
                    conflictingMergedTargetCount: 0);
            }

            return Compose(CsrVertexMapFactory.ToJaggedGroups(sourceCsr), correspondence, policy);
        }

        /// <summary>
        /// Composes <paramref name="sourceFbxGroups"/> with <paramref name="correspondence"/> to produce
        /// FBX control-point groups indexed by target Unity vertices.
        /// </summary>
        public static VertexMapComposeResult Compose(
            IReadOnlyList<int[]> sourceFbxGroups,
            MeshVertexCorrespondence correspondence,
            VertexMapComposePolicy policy = VertexMapComposePolicy.AllowMergedUnion)
        {
            if (sourceFbxGroups == null || correspondence == null || !correspondence.Succeeded)
            {
                return new VertexMapComposeResult(
                    succeeded: false,
                    diagnostic: correspondence?.Diagnostic ?? "Composition requires a valid source map and correspondence.",
                    targetFbxGroups: Array.Empty<int[]>(),
                    unresolvedTargetCount: correspondence?.TargetVertexCount ?? 0,
                    conflictingMergedTargetCount: 0);
            }

            int targetCount = correspondence.TargetVertexCount;
            var composed = new int[targetCount][];
            var unionSet = new HashSet<int>();
            var firstCanonical = new HashSet<int>();
            var currentCanonical = new HashSet<int>();
            var conflictingIndices = new List<int>();
            int unresolved = 0;
            int conflicts = 0;

            for (int t = 0; t < targetCount; t++)
            {
                var srcVertices = correspondence.TargetToSourceVertices[t];
                if (srcVertices == null || srcVertices.Length == 0)
                {
                    composed[t] = Array.Empty<int>();
                    unresolved++;
                    continue;
                }

                unionSet.Clear();
                firstCanonical.Clear();
                bool hasFirst = false;
                bool missingContributor = false;
                bool differs = false;

                for (int i = 0; i < srcVertices.Length; i++)
                {
                    int s = srcVertices[i];
                    if ((uint)s >= (uint)sourceFbxGroups.Count)
                    {
                        missingContributor = true;
                        break;
                    }

                    var group = sourceFbxGroups[s];
                    if (group == null || group.Length == 0)
                    {
                        missingContributor = true;
                        break;
                    }

                    currentCanonical.Clear();
                    for (int j = 0; j < group.Length; j++)
                    {
                        if (group[j] >= 0)
                        {
                            currentCanonical.Add(group[j]);
                        }
                    }

                    if (currentCanonical.Count == 0)
                    {
                        missingContributor = true;
                        break;
                    }

                    // Compare as canonical sets so unsorted/duplicated arrays representing the same CPs do not false-conflict.
                    if (!hasFirst)
                    {
                        foreach (int cp in currentCanonical) firstCanonical.Add(cp);
                        hasFirst = true;
                    }
                    else if (!firstCanonical.SetEquals(currentCanonical))
                    {
                        differs = true;
                    }

                    foreach (int cp in currentCanonical)
                    {
                        unionSet.Add(cp);
                    }
                }

                if (missingContributor)
                {
                    if (differs)
                    {
                        conflicts++;
                        conflictingIndices.Add(t);
                    }
                    composed[t] = Array.Empty<int>();
                    unresolved++;
                    continue;
                }

                if (differs)
                {
                    conflicts++;
                    conflictingIndices.Add(t);
                    if (policy == VertexMapComposePolicy.RejectConflictingGroups)
                    {
                        composed[t] = Array.Empty<int>();
                        unresolved++;
                        continue;
                    }
                }

                if (unionSet.Count == 0)
                {
                    composed[t] = Array.Empty<int>();
                    unresolved++;
                    continue;
                }

                var sorted = new int[unionSet.Count];
                unionSet.CopyTo(sorted);
                Array.Sort(sorted);
                composed[t] = sorted;
            }

            bool succeeded = unresolved == 0;
            string diagnostic = succeeded
                ? string.Empty
                : $"Vertex map composition left {unresolved} target vertex(es) unresolved (conflicts={conflicts}).";

            return new VertexMapComposeResult(
                succeeded: succeeded,
                diagnostic: diagnostic,
                targetFbxGroups: composed,
                unresolvedTargetCount: unresolved,
                conflictingMergedTargetCount: conflicts,
                conflictingTargetVertices: conflictingIndices.Count > 0 ? conflictingIndices.ToArray() : Array.Empty<int>());
        }
    }
}
