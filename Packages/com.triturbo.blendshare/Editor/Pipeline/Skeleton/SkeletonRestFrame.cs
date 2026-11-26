using System;
using System.Collections.Generic;
using Triturbo.BlendShare.Fbx;

namespace Triturbo.BlendShare.Pipeline.Skeleton
{
    /// <summary>
    /// Identifies whether a node's rest transform was directly seeded by a skin cluster bindpose
    /// or propagated top-down from its parent rest transform via its live local transform.
    /// </summary>
    public enum SkeletonRestOrigin : byte
    {
        /// <summary>Directly seeded from at least one skin cluster / bindpose at the winning priority tier.</summary>
        DirectBind = 0,

        /// <summary>Unbound node propagated top-down from its parent's rest frame via its live local transform.</summary>
        PropagatedFromLive = 1
    }

    /// <summary>
    /// Reconstructed rest-frame record for a single scene hierarchy node.
    /// </summary>
    public sealed class SkeletonRestNode
    {
        public string Path { get; }
        public string ParentPath { get; }
        public bool IsBone { get; }
        public SkeletonRestOrigin Origin { get; }
        public FbxMatrix4x4 RestGlobal { get; }
        public FbxMatrix4x4 RestLocal { get; }
        public bool IsRestConsistent { get; }

        internal FbxMatrix4x4 LiveGlobal { get; }
        internal FbxMatrix4x4 PoseDelta { get; }
        internal bool IsConflicting { get; }
        internal string ConflictDetail { get; }

        internal SkeletonRestNode(
            string path,
            string parentPath,
            bool isBone,
            SkeletonRestOrigin origin,
            FbxMatrix4x4 restGlobal,
            FbxMatrix4x4 restLocal,
            FbxMatrix4x4 liveGlobal,
            FbxMatrix4x4 poseDelta,
            bool isRestConsistent,
            bool isConflicting,
            string conflictDetail)
        {
            Path = path ?? string.Empty;
            ParentPath = parentPath ?? string.Empty;
            IsBone = isBone;
            Origin = origin;
            RestGlobal = restGlobal;
            RestLocal = restLocal;
            LiveGlobal = liveGlobal;
            PoseDelta = poseDelta;
            IsRestConsistent = isRestConsistent;
            IsConflicting = isConflicting;
            ConflictDetail = conflictDetail ?? string.Empty;
        }
    }

    /// <summary>
    /// Immutable snapshot of clean rest-pose transforms and live pose deltas across a scene hierarchy.
    /// </summary>
    public sealed class SkeletonRestFrame
    {
        private readonly Dictionary<string, SkeletonRestNode> m_NodesByPath;
        private readonly Dictionary<string, FbxMatrix4x4> m_RestGlobalsByPath;
        private readonly List<string> m_OrderedPaths;

        public IReadOnlyDictionary<string, SkeletonRestNode> NodesByPath => m_NodesByPath;
        public IReadOnlyDictionary<string, FbxMatrix4x4> RestGlobals => m_RestGlobalsByPath;
        public IReadOnlyList<string> OrderedPaths => m_OrderedPaths;
        public double UnitMeters { get; }
        public bool HasAnyConflict { get; }

        internal SkeletonRestFrame(
            Dictionary<string, SkeletonRestNode> nodesByPath,
            List<string> orderedPaths,
            double unitMeters)
        {
            m_NodesByPath = nodesByPath ?? new Dictionary<string, SkeletonRestNode>(StringComparer.Ordinal);
            m_OrderedPaths = orderedPaths ?? new List<string>();
            m_RestGlobalsByPath = new Dictionary<string, FbxMatrix4x4>(m_NodesByPath.Count, StringComparer.Ordinal);
            UnitMeters = unitMeters > 0d ? unitMeters : 1d;

            bool anyConflict = false;
            foreach (var kv in m_NodesByPath)
            {
                if (kv.Value == null)
                {
                    continue;
                }

                m_RestGlobalsByPath[kv.Key] = kv.Value.RestGlobal;
                if (kv.Value.IsConflicting)
                {
                    anyConflict = true;
                }
            }

            HasAnyConflict = anyConflict;
        }

        /// <summary>
        /// Attempts to look up a hierarchy node by normalized FBX/Unity relative path.
        /// </summary>
        public bool TryGetNode(string path, out SkeletonRestNode node)
        {
            string normalized = FbxNameUtility.NormalizePath(path);
            if (string.IsNullOrEmpty(normalized))
            {
                node = null;
                return false;
            }

            return m_NodesByPath.TryGetValue(normalized, out node);
        }

        /// <summary>
        /// Returns the <see cref="SkeletonRestNode"/> at <paramref name="path"/>, or <c>null</c> if absent.
        /// </summary>
        public SkeletonRestNode GetNode(string path) =>
            TryGetNode(path, out var node) ? node : null;

        /// <summary>
        /// Validates that neither <paramref name="path"/> nor any of its lexical ancestors has conflicting
        /// pre-edit bindpose seeds. Unrelated branches with bindpose conflicts do not affect clean chains.
        /// </summary>
        public bool TryValidateChain(string path, out string conflictError)
        {
            conflictError = null;
            string curr = FbxNameUtility.NormalizePath(path);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (!string.IsNullOrEmpty(curr) && visited.Add(curr))
            {
                if (m_NodesByPath.TryGetValue(curr, out var node) && node != null && node.IsConflicting)
                {
                    conflictError = !string.IsNullOrEmpty(node.ConflictDetail)
                        ? node.ConflictDetail
                        : $"Conflicting pre-edit target bindpose rest frames for bone '{curr}'.";
                    return false;
                }

                curr = FbxNameUtility.GetParentPath(curr);
            }

            return true;
        }

        /// <summary>
        /// Left-multiplies <paramref name="postEditRestLocal"/> by the captured live <c>PoseDelta</c> for existing
        /// nodes so posed Targets retain their live stance while created nodes use <paramref name="postEditRestLocal"/> directly.
        /// </summary>
        public FbxMatrix4x4 ApplyPoseDelta(string path, FbxMatrix4x4 postEditRestLocal)
        {
            return TryGetNode(path, out var node) && node != null
                ? node.PoseDelta * postEditRestLocal
                : postEditRestLocal;
        }
    }

    /// <summary>
    /// Platform-agnostic Stage-1 rest-frame builder: selects the highest-priority bindpose seed per path,
    /// flags cross-mesh bindpose conflicts, and propagates unbound nodes (<c>FbxNull</c>s, <c>&lt;bone&gt;_end</c>
    /// leaves, unskinned <c>Armature</c> roots) top-down from their parent's rest frame.
    /// </summary>
    public sealed class SkeletonRestFrameBuilder
    {
        private sealed class LiveNodeInput
        {
            public string Path;
            public bool IsBone;
            public FbxMatrix4x4 LiveGlobal;
            public FbxMatrix4x4 LiveLocal;
            public Vector3d LocalScale;
            public FbxTransformInheritMode InheritMode;
            public FbxTransformInheritMode OriginalInheritMode;
        }

        private readonly struct BindSeedInput
        {
            public readonly FbxMatrix4x4 BindGlobal;
            public readonly int PriorityTier;
            public readonly string SourceLabel;

            public BindSeedInput(FbxMatrix4x4 bindGlobal, int priorityTier, string sourceLabel)
            {
                BindGlobal = bindGlobal;
                PriorityTier = priorityTier;
                SourceLabel = sourceLabel ?? string.Empty;
            }
        }

        private readonly Dictionary<string, LiveNodeInput> m_LiveNodes =
            new Dictionary<string, LiveNodeInput>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<BindSeedInput>> m_BindSeeds =
            new Dictionary<string, List<BindSeedInput>>(StringComparer.Ordinal);

        /// <summary>
        /// Registers a live hierarchy node.
        /// </summary>
        public void AddLiveNode(
            string path,
            bool isBone,
            FbxMatrix4x4 liveGlobal,
            FbxMatrix4x4 liveLocal,
            FbxTransformInheritMode inheritMode = FbxTransformInheritMode.RSrs,
            FbxTransformInheritMode originalInheritMode = FbxTransformInheritMode.RSrs,
            Vector3d? localScale = null)
        {
            string normalized = FbxNameUtility.NormalizePath(path);
            if (string.IsNullOrEmpty(normalized))
            {
                return;
            }

            Vector3d resolvedScale = localScale ?? ExtractRowMagnitudes(liveLocal);
            m_LiveNodes[normalized] = new LiveNodeInput
            {
                Path = normalized,
                IsBone = isBone,
                LiveGlobal = liveGlobal,
                LiveLocal = liveLocal,
                LocalScale = resolvedScale,
                InheritMode = inheritMode,
                OriginalInheritMode = originalInheritMode
            };
        }

        /// <summary>
        /// Registers a skin-cluster / bindpose global matrix candidate at <paramref name="priorityTier"/>
        /// (lower tier value = higher priority).
        /// </summary>
        public void AddBindSeed(string path, FbxMatrix4x4 bindGlobal, int priorityTier, string sourceLabel = null)
        {
            string normalized = FbxNameUtility.NormalizePath(path);
            if (string.IsNullOrEmpty(normalized) || !FbxAffineSpaceTransform.IsInvertibleAffine(bindGlobal))
            {
                return;
            }

            if (!m_BindSeeds.TryGetValue(normalized, out var list))
            {
                list = new List<BindSeedInput>(2);
                m_BindSeeds.Add(normalized, list);
            }

            list.Add(new BindSeedInput(bindGlobal, priorityTier, sourceLabel));
        }

        /// <summary>
        /// Builds the <see cref="SkeletonRestFrame"/> in parent-before-child hierarchy order.
        /// </summary>
        public SkeletonRestFrame Build(
            double unitMeters = 1d,
            double linearTolerance = 1e-3d,
            double translationToleranceMeters = 1e-4d)
        {
            double safeUnit = (!double.IsNaN(unitMeters) && !double.IsInfinity(unitMeters) && unitMeters > 0d)
                ? unitMeters
                : 1d;
            double translationTolUnits = Math.Max(1e-6d, translationToleranceMeters / safeUnit);

            var orderedPaths = new List<string>(m_LiveNodes.Keys);
            orderedPaths.Sort(FbxNameUtility.CompareHierarchyOrder);

            var nodesByPath = new Dictionary<string, SkeletonRestNode>(orderedPaths.Count, StringComparer.Ordinal);
            var restHierarchyStates = new Dictionary<string, FbxNodeTransformEvaluator.FbxHierarchyNodeState>(
                orderedPaths.Count,
                StringComparer.Ordinal);

            for (int i = 0; i < orderedPaths.Count; i++)
            {
                string path = orderedPaths[i];
                var live = m_LiveNodes[path];
                string parentPath = FbxNameUtility.GetParentPath(path);

                SkeletonRestNode parentNode = null;
                bool hasParentNode = !string.IsNullOrEmpty(parentPath) && nodesByPath.TryGetValue(parentPath, out parentNode);
                FbxMatrix4x4 parentRestGlobal = hasParentNode
                    ? parentNode.RestGlobal
                    : FbxMatrix4x4.Identity;
                FbxMatrix4x4 parentLiveGlobal = hasParentNode
                    ? parentNode.LiveGlobal
                    : FbxMatrix4x4.Identity;
                var parentRestState = hasParentNode && restHierarchyStates.TryGetValue(parentPath, out var ps)
                    ? ps
                    : FbxNodeTransformEvaluator.FbxHierarchyNodeState.Identity;

                SkeletonRestOrigin origin;
                FbxMatrix4x4 restGlobal;
                FbxNodeTransformEvaluator.FbxHierarchyNodeState currentRestState;
                bool isConflicting = false;
                string conflictDetail = string.Empty;

                if (TryResolveWinningBindSeed(
                        path,
                        linearTolerance,
                        translationTolUnits,
                        safeUnit,
                        out var bindGlobal,
                        out isConflicting,
                        out conflictDetail))
                {
                    origin = SkeletonRestOrigin.DirectBind;
                    restGlobal = bindGlobal;
                    if (!FbxNodeTransformEvaluator.TryCreateStateFromGlobalMatrix(restGlobal, out currentRestState))
                    {
                        currentRestState = new FbxNodeTransformEvaluator.FbxHierarchyNodeState(
                            restGlobal, restGlobal, Vector3d.one, null);
                    }
                }
                else
                {
                    origin = SkeletonRestOrigin.PropagatedFromLive;
                    if (FbxNodeTransformEvaluator.TryEvaluateHierarchyState(
                            live.LiveLocal,
                            live.LocalScale,
                            live.InheritMode,
                            live.OriginalInheritMode,
                            hasParentNode,
                            parentRestState,
                            out currentRestState))
                    {
                        restGlobal = currentRestState.NodeToWorld;
                    }
                    else
                    {
                        restGlobal = hasParentNode ? (live.LiveLocal * parentRestGlobal) : live.LiveGlobal;
                        if (!FbxNodeTransformEvaluator.TryCreateStateFromGlobalMatrix(restGlobal, out currentRestState))
                        {
                            currentRestState = new FbxNodeTransformEvaluator.FbxHierarchyNodeState(
                                restGlobal, restGlobal, Vector3d.one, null);
                        }
                    }
                }

                restHierarchyStates[path] = currentRestState;

                FbxMatrix4x4 restLocal = hasParentNode && parentRestGlobal.TryInverse(out var parentRestInv)
                    ? restGlobal * parentRestInv
                    : restGlobal;

                // EffectiveLiveLocal = LiveGlobal * LiveParentGlobal^-1 equals LiveLocal identically for RSrs/Unity,
                // while guaranteeing PoseDelta == Identity for unposed RrSs/Rrs nodes under scaled parents.
                FbxMatrix4x4 effectiveLiveLocal = hasParentNode && parentLiveGlobal.TryInverse(out var parentLiveInv)
                    ? live.LiveGlobal * parentLiveInv
                    : live.LiveGlobal;
                FbxMatrix4x4 poseDelta = restLocal.TryInverse(out var restLocalInv)
                    ? effectiveLiveLocal * restLocalInv
                    : FbxMatrix4x4.Identity;

                bool isRestConsistent = MatricesApproximatelyEqual(
                    restGlobal,
                    live.LiveGlobal,
                    linearTolerance,
                    translationTolUnits);

                nodesByPath[path] = new SkeletonRestNode(
                    path,
                    parentPath,
                    live.IsBone,
                    origin,
                    restGlobal,
                    restLocal,
                    live.LiveGlobal,
                    poseDelta,
                    isRestConsistent,
                    isConflicting,
                    conflictDetail);
            }

            return new SkeletonRestFrame(nodesByPath, orderedPaths, safeUnit);
        }

        private bool TryResolveWinningBindSeed(
            string path,
            double linearTolerance,
            double translationTolUnits,
            double safeUnitMeters,
            out FbxMatrix4x4 winningMatrix,
            out bool isConflicting,
            out string conflictDetail)
        {
            winningMatrix = FbxMatrix4x4.Identity;
            isConflicting = false;
            conflictDetail = string.Empty;

            if (!m_BindSeeds.TryGetValue(path, out var seeds) || seeds == null || seeds.Count == 0)
            {
                return false;
            }

            int minTier = int.MaxValue;
            for (int i = 0; i < seeds.Count; i++)
            {
                if (seeds[i].PriorityTier < minTier)
                {
                    minTier = seeds[i].PriorityTier;
                }
            }

            var tierSeeds = new List<BindSeedInput>(seeds.Count);
            for (int i = 0; i < seeds.Count; i++)
            {
                if (seeds[i].PriorityTier == minTier)
                {
                    var s = seeds[i];
                    tierSeeds.Add(new BindSeedInput(CanonicalizeMatrixZero(s.BindGlobal), s.PriorityTier, s.SourceLabel));
                }
            }

            if (tierSeeds.Count == 1)
            {
                winningMatrix = tierSeeds[0].BindGlobal;
                return true;
            }

            // Compute per-cell bounding range across all winning-tier seeds so both conflict detection
            // (full pairwise spread) and representative selection (closest to midpoint, tie-broken purely on
            // canonicalized matrix cells) are 100% invariant to renderer/mesh iteration order and fallback labels.
            var minCell = new double[16];
            var maxCell = new double[16];
            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++)
                {
                    int k = (r * 4) + c;
                    double v = tierSeeds[0].BindGlobal[r, c];
                    minCell[k] = v;
                    maxCell[k] = v;
                }
            }

            for (int i = 1; i < tierSeeds.Count; i++)
            {
                var mat = tierSeeds[i].BindGlobal;
                for (int r = 0; r < 4; r++)
                {
                    for (int c = 0; c < 4; c++)
                    {
                        int k = (r * 4) + c;
                        double v = mat[r, c];
                        if (v < minCell[k]) minCell[k] = v;
                        if (v > maxCell[k]) maxCell[k] = v;
                    }
                }
            }

            int bestIdx = 0;
            double bestMidDist = ComputeMidpointChebyshevDistance(tierSeeds[0].BindGlobal, minCell, maxCell);
            for (int i = 1; i < tierSeeds.Count; i++)
            {
                double dist = ComputeMidpointChebyshevDistance(tierSeeds[i].BindGlobal, minCell, maxCell);
                int cmp = dist.CompareTo(bestMidDist);
                if (cmp < 0 || (cmp == 0 && CompareMatrixCells(tierSeeds[i].BindGlobal, tierSeeds[bestIdx].BindGlobal) < 0))
                {
                    bestIdx = i;
                    bestMidDist = dist;
                }
            }

            winningMatrix = tierSeeds[bestIdx].BindGlobal;

            double maxLinDiff = 0d;
            for (int r = 0; r < 3; r++)
            {
                for (int c = 0; c < 3; c++)
                {
                    double spread = maxCell[(r * 4) + c] - minCell[(r * 4) + c];
                    if (spread > maxLinDiff)
                    {
                        maxLinDiff = spread;
                    }
                }
            }

            double maxTransDiffUnits = 0d;
            (string First, string Second)? conflictLabels = null;
            for (int i = 0; i < tierSeeds.Count; i++)
            {
                for (int j = i + 1; j < tierSeeds.Count; j++)
                {
                    var left = tierSeeds[i].BindGlobal;
                    var right = tierSeeds[j].BindGlobal;
                    double pairLin = 0d;
                    for (int r = 0; r < 3; r++)
                    {
                        for (int c = 0; c < 3; c++)
                        {
                            double diff = Math.Abs(left[r, c] - right[r, c]);
                            if (diff > pairLin) pairLin = diff;
                        }
                    }

                    double transDiff = (left.Translation - right.Translation).magnitude;
                    if (transDiff > maxTransDiffUnits)
                    {
                        maxTransDiffUnits = transDiff;
                    }

                    if (pairLin > linearTolerance || transDiff > translationTolUnits)
                    {
                        string lA = tierSeeds[i].SourceLabel;
                        string lB = tierSeeds[j].SourceLabel;
                        if (StringComparer.Ordinal.Compare(lA, lB) > 0)
                        {
                            (lA, lB) = (lB, lA);
                        }

                        if (!conflictLabels.HasValue ||
                            StringComparer.Ordinal.Compare(lA, conflictLabels.Value.First) < 0 ||
                            (string.Equals(lA, conflictLabels.Value.First, StringComparison.Ordinal) &&
                             StringComparer.Ordinal.Compare(lB, conflictLabels.Value.Second) < 0))
                        {
                            conflictLabels = (lA, lB);
                        }
                    }
                }
            }

            if (maxLinDiff > linearTolerance || maxTransDiffUnits > translationTolUnits)
            {
                isConflicting = true;
                double transMeters = maxTransDiffUnits * safeUnitMeters;
                string labelA = conflictLabels?.First ?? tierSeeds[0].SourceLabel;
                string labelB = conflictLabels?.Second ?? tierSeeds[1].SourceLabel;
                conflictDetail =
                    $"Conflicting bind poses on '{path}' between '{labelA}' and '{labelB}' " +
                    $"(max linear diff {maxLinDiff:E3}, translation diff {transMeters:E3} m).";
            }

            return true;
        }

        private static FbxMatrix4x4 CanonicalizeMatrixZero(in FbxMatrix4x4 m)
        {
            static double Z(double v) => v == 0d ? 0d : v;
            return new FbxMatrix4x4(
                Z(m[0, 0]), Z(m[0, 1]), Z(m[0, 2]), Z(m[0, 3]),
                Z(m[1, 0]), Z(m[1, 1]), Z(m[1, 2]), Z(m[1, 3]),
                Z(m[2, 0]), Z(m[2, 1]), Z(m[2, 2]), Z(m[2, 3]),
                Z(m[3, 0]), Z(m[3, 1]), Z(m[3, 2]), Z(m[3, 3]));
        }

        private static double ComputeMidpointChebyshevDistance(in FbxMatrix4x4 m, double[] minCell, double[] maxCell)
        {
            double maxDist = 0d;
            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++)
                {
                    int k = (r * 4) + c;
                    double mid = 0.5d * (minCell[k] + maxCell[k]);
                    double d = Math.Abs(m[r, c] - mid);
                    if (d > maxDist) maxDist = d;
                }
            }

            return maxDist;
        }

        private static int CompareMatrixCells(in FbxMatrix4x4 left, in FbxMatrix4x4 right)
        {
            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++)
                {
                    int entryCmp = left[r, c].CompareTo(right[r, c]);
                    if (entryCmp != 0)
                    {
                        return entryCmp;
                    }
                }
            }

            return 0;
        }

        private static Vector3d ExtractRowMagnitudes(in FbxMatrix4x4 m)
        {
            double sx = new Vector3d(m[0, 0], m[0, 1], m[0, 2]).magnitude;
            double sy = new Vector3d(m[1, 0], m[1, 1], m[1, 2]).magnitude;
            double sz = new Vector3d(m[2, 0], m[2, 1], m[2, 2]).magnitude;
            return new Vector3d(
                sx > 1e-12d ? sx : 1d,
                sy > 1e-12d ? sy : 1d,
                sz > 1e-12d ? sz : 1d);
        }

        internal static bool MatricesApproximatelyEqual(
            in FbxMatrix4x4 a,
            in FbxMatrix4x4 b,
            double linearTolerance,
            double translationTolerance)
        {
            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++)
                {
                    double tol = (r == 3 && c < 3) ? translationTolerance : linearTolerance;
                    if (Math.Abs(a[r, c] - b[r, c]) > tol)
                    {
                        return false;
                    }
                }
            }

            return true;
        }
    }
}
