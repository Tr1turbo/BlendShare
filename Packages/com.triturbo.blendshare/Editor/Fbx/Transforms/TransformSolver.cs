using System;
using System.Collections.Generic;

namespace Triturbo.BlendShare.Fbx
{
    /// <summary>
    /// Describes a desired transform edit for a hierarchy node in a shared coordinate space.
    /// </summary>
    public readonly struct TransformEdit
    {
        public string Path { get; }
        public FbxMatrix4x4 Global { get; }
        public FbxMatrix4x4 Local { get; }
        public FbxMatrix4x4? ReferenceGlobal { get; }

        public TransformEdit(
            string path,
            FbxMatrix4x4 global,
            FbxMatrix4x4 local,
            FbxMatrix4x4? referenceGlobal = null)
        {
            Path = FbxNameUtility.NormalizePath(path);
            Global = global;
            Local = local;
            ReferenceGlobal = referenceGlobal;
        }
    }

    /// <summary>
    /// Groups a named set of same-space <see cref="TransformEdit"/>s and optional reference frames
    /// for multi-source hierarchy solving.
    /// </summary>
    public sealed class TransformChangeSet
    {
        public string SourceId { get; }
        public IReadOnlyList<TransformEdit> Edits { get; }
        public IReadOnlyDictionary<string, FbxMatrix4x4> ReferenceGlobals { get; }

        public TransformChangeSet(
            string sourceId,
            IReadOnlyList<TransformEdit> edits,
            IReadOnlyDictionary<string, FbxMatrix4x4> referenceGlobals = null)
        {
            SourceId = sourceId ?? string.Empty;
            Edits = edits ?? Array.Empty<TransformEdit>();
            ReferenceGlobals = referenceGlobals;
        }
    }

    /// <summary>
    /// Diagnostic emitted by <see cref="TransformSolver.SolveHierarchy(IReadOnlyDictionary{string, FbxMatrix4x4}, IReadOnlyList{TransformChangeSet}, double)"/>.
    /// </summary>
    public readonly struct TransformSolveDiagnostic
    {
        public string Code { get; }
        public string Message { get; }
        public string SourceId { get; }
        public string Path { get; }

        public TransformSolveDiagnostic(
            string code,
            string message,
            string sourceId = null,
            string path = null)
        {
            Code = code ?? string.Empty;
            Message = message ?? string.Empty;
            SourceId = sourceId ?? string.Empty;
            Path = path ?? string.Empty;
        }
    }

    /// <summary>
    /// Solved global and parent-relative local transform for an edited hierarchy node.
    /// </summary>
    public sealed class SolvedNodeTransform
    {
        public string Path { get; }
        public string ParentPath { get; }
        public string SourceId { get; }
        public bool IsCreated { get; }
        public FbxMatrix4x4 Global { get; }
        public FbxMatrix4x4 Local { get; }

        public SolvedNodeTransform(
            string path,
            string parentPath,
            string sourceId,
            bool isCreated,
            FbxMatrix4x4 global,
            FbxMatrix4x4 local)
        {
            Path = path ?? string.Empty;
            ParentPath = parentPath ?? string.Empty;
            SourceId = sourceId ?? string.Empty;
            IsCreated = isCreated;
            Global = global;
            Local = local;
        }
    }

    /// <summary>
    /// Immutable output of <see cref="TransformSolver.SolveHierarchy(IReadOnlyDictionary{string, FbxMatrix4x4}, IReadOnlyList{TransformChangeSet}, double)"/>,
    /// providing solved node transforms, memoized untouched-child propagation, and same-space basis resolution.
    /// </summary>
    public sealed class TransformHierarchyResult
    {
        private readonly IReadOnlyDictionary<string, FbxMatrix4x4> m_TargetGlobals;
        private readonly Dictionary<string, SolvedNodeTransform> m_NodesByPath;
        private readonly List<string> m_OrderedPaths;
        private readonly List<TransformSolveDiagnostic> m_Diagnostics;
        private readonly Dictionary<string, FbxMatrix4x4> m_GlobalMemo =
            new Dictionary<string, FbxMatrix4x4>(StringComparer.Ordinal);

        public IReadOnlyList<string> OrderedPaths => m_OrderedPaths;
        public IReadOnlyDictionary<string, SolvedNodeTransform> NodesByPath => m_NodesByPath;
        public IReadOnlyList<TransformSolveDiagnostic> Diagnostics => m_Diagnostics;
        public bool HasErrors => m_Diagnostics.Count > 0;

        internal TransformHierarchyResult(
            IReadOnlyDictionary<string, FbxMatrix4x4> targetGlobals,
            Dictionary<string, SolvedNodeTransform> nodesByPath,
            List<string> orderedPaths,
            List<TransformSolveDiagnostic> diagnostics)
        {
            m_TargetGlobals = targetGlobals;
            m_NodesByPath = nodesByPath ?? new Dictionary<string, SolvedNodeTransform>(StringComparer.Ordinal);
            m_OrderedPaths = orderedPaths ?? new List<string>();
            m_Diagnostics = diagnostics ?? new List<TransformSolveDiagnostic>();

            foreach (var kv in m_NodesByPath)
            {
                m_GlobalMemo[kv.Key] = kv.Value.Global;
            }
        }

        internal void RegisterSolvedNode(SolvedNodeTransform solved)
        {
            m_NodesByPath[solved.Path] = solved;
            m_OrderedPaths.Add(solved.Path);
            m_GlobalMemo[solved.Path] = solved.Global;
        }

        /// <summary>
        /// Evaluates the post-solve global matrix for <paramref name="path"/>, propagating edited ancestors
        /// through untouched intermediate nodes via <c>(U_b * U_parent^-1) * R'_parent</c>.
        /// </summary>
        public bool TryGetGlobal(string path, out FbxMatrix4x4 global)
        {
            string norm = FbxNameUtility.NormalizePath(path);
            if (string.IsNullOrEmpty(norm))
            {
                global = FbxMatrix4x4.Identity;
                return true;
            }

            return TryResolveGlobalRecursive(norm, new HashSet<string>(StringComparer.Ordinal), out global);
        }

        /// <summary>
        /// Evaluates the post-solve parent-relative local matrix for <paramref name="path"/>.
        /// </summary>
        public bool TryGetLocal(string path, out FbxMatrix4x4 local)
        {
            string norm = FbxNameUtility.NormalizePath(path);
            if (string.IsNullOrEmpty(norm))
            {
                local = FbxMatrix4x4.Identity;
                return true;
            }

            if (m_NodesByPath.TryGetValue(norm, out var solved))
            {
                local = solved.Local;
                return true;
            }

            if (m_TargetGlobals != null && m_TargetGlobals.TryGetValue(norm, out var targetGlobal))
            {
                string parentPath = FbxNameUtility.GetParentPath(norm);
                local = !string.IsNullOrEmpty(parentPath) &&
                        m_TargetGlobals.TryGetValue(parentPath, out var preParentGlobal) &&
                        preParentGlobal.TryInverse(out var preParentInv)
                    ? targetGlobal * preParentInv
                    : targetGlobal;
                return true;
            }

            local = FbxMatrix4x4.Identity;
            return false;
        }

        /// <summary>
        /// Resolves the same-space linear basis calibration <c>Lin(S_b) * Lin(R'_b)^-1</c> at <paramref name="path"/>,
        /// walking ancestors when <paramref name="path"/> is absent from <paramref name="referenceGlobals"/>
        /// and falling back to <see cref="FbxMatrix4x4.Identity"/> at the root.
        /// </summary>
        public FbxMatrix4x4 ResolveBasis(
            IReadOnlyDictionary<string, FbxMatrix4x4> referenceGlobals,
            string path)
        {
            if (referenceGlobals == null || referenceGlobals.Count == 0)
            {
                return FbxMatrix4x4.Identity;
            }

            string curr = FbxNameUtility.NormalizePath(path);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (!string.IsNullOrEmpty(curr) && visited.Add(curr))
            {
                if (referenceGlobals.TryGetValue(curr, out var refGlobal) &&
                    TryGetGlobal(curr, out var postGlobal))
                {
                    return TransformSolver.TrySolveBasis(refGlobal, postGlobal, out var basisDelta)
                        ? basisDelta
                        : FbxMatrix4x4.Identity;
                }

                curr = FbxNameUtility.GetParentPath(curr);
            }

            return FbxMatrix4x4.Identity;
        }

        private bool TryResolveGlobalRecursive(
            string normPath,
            HashSet<string> visited,
            out FbxMatrix4x4 global)
        {
            if (m_GlobalMemo.TryGetValue(normPath, out global))
            {
                return true;
            }

            if (!visited.Add(normPath))
            {
                global = FbxMatrix4x4.Identity;
                return false;
            }

            if (m_TargetGlobals != null && m_TargetGlobals.TryGetValue(normPath, out var targetGlobal))
            {
                string parentPath = FbxNameUtility.GetParentPath(normPath);
                if (string.IsNullOrEmpty(parentPath))
                {
                    global = targetGlobal;
                    m_GlobalMemo[normPath] = global;
                    return true;
                }

                FbxMatrix4x4 targetLocal = m_TargetGlobals.TryGetValue(parentPath, out var preParentGlobal) &&
                                           preParentGlobal.TryInverse(out var preParentInv)
                    ? targetGlobal * preParentInv
                    : targetGlobal;

                if (TryResolveGlobalRecursive(parentPath, visited, out var parentPostGlobal))
                {
                    global = targetLocal * parentPostGlobal;
                    m_GlobalMemo[normPath] = global;
                    return true;
                }

                global = targetGlobal;
                m_GlobalMemo[normPath] = global;
                return true;
            }

            global = FbxMatrix4x4.Identity;
            return false;
        }
    }

    /// <summary>
    /// Pure row-vector affine transform solver providing both atomic single-matrix operations
    /// and same-space hierarchy solving.
    /// </summary>
    public static class TransformSolver
    {
        private const double k_LinearAgreementTolerance = 1e-4d;

        /// <summary>
        /// Solves the linear basis calibration <c>D = Lin(referenceGlobal) * Lin(targetGlobal)^-1</c>
        /// mapping local vectors from <paramref name="referenceGlobal"/>'s frame into <paramref name="targetGlobal"/>'s frame.
        /// </summary>
        public static bool TrySolveBasis(
            FbxMatrix4x4 referenceGlobal,
            FbxMatrix4x4 targetGlobal,
            out FbxMatrix4x4 basisDelta)
        {
            basisDelta = FbxMatrix4x4.Identity;
            FbxMatrix4x4 linRef = FbxLinearAlgebra3d.ExtractLinear(referenceGlobal);
            FbxMatrix4x4 linTarget = FbxLinearAlgebra3d.ExtractLinear(targetGlobal);
            if (!FbxAffineSpaceTransform.TryCreate(linRef, out _) ||
                !FbxAffineSpaceTransform.TryCreate(linTarget, out var targetXform))
            {
                return false;
            }

            FbxMatrix4x4 d = linRef * targetXform.InverseOffset;
            if (!FbxAffineSpaceTransform.TryCreate(d, out _))
            {
                return false;
            }

            basisDelta = d;
            return true;
        }

        /// <summary>
        /// Transfers a same-space global transform change (<paramref name="referenceGlobal"/> to <paramref name="desiredGlobal"/>)
        /// onto <paramref name="currentGlobal"/> while preserving <paramref name="currentGlobal"/>'s local basis orientation:
        /// <c>R' = (Lin(referenceGlobal) * Lin(currentGlobal)^-1)^-1 * desiredGlobal</c>.
        /// </summary>
        public static bool TrySolveGlobal(
            FbxMatrix4x4 currentGlobal,
            FbxMatrix4x4 referenceGlobal,
            FbxMatrix4x4 desiredGlobal,
            out FbxMatrix4x4 solvedGlobal)
        {
            solvedGlobal = FbxMatrix4x4.Identity;
            if (!FbxAffineSpaceTransform.TryCreate(desiredGlobal, out _) ||
                !TrySolveBasis(referenceGlobal, currentGlobal, out var basisDelta) ||
                !FbxAffineSpaceTransform.TryCreate(basisDelta, out var basisXform))
            {
                return false;
            }

            FbxMatrix4x4 candidate = basisXform.InverseOffset * desiredGlobal;
            if (!FbxAffineSpaceTransform.TryCreate(candidate, out _))
            {
                return false;
            }

            solvedGlobal = candidate;
            return true;
        }

        /// <summary>
        /// Conjugates a parent-relative <paramref name="desiredLocal"/> matrix from <paramref name="referenceParentGlobal"/>'s
        /// linear basis into <paramref name="targetParentGlobal"/>'s linear basis:
        /// <c>L' = D_parent^-1 * desiredLocal * D_parent</c>.
        /// </summary>
        public static bool TrySolveLocal(
            FbxMatrix4x4 referenceParentGlobal,
            FbxMatrix4x4 targetParentGlobal,
            FbxMatrix4x4 desiredLocal,
            out FbxMatrix4x4 solvedLocal)
        {
            solvedLocal = FbxMatrix4x4.Identity;
            if (!FbxAffineSpaceTransform.TryCreate(desiredLocal, out _) ||
                !TrySolveBasis(referenceParentGlobal, targetParentGlobal, out var basisDelta) ||
                !FbxAffineSpaceTransform.TryCreate(basisDelta, out var basisXform))
            {
                return false;
            }

            FbxMatrix4x4 candidate = basisXform.InverseOffset * desiredLocal * basisXform.Offset;
            if (!FbxAffineSpaceTransform.TryCreate(candidate, out _))
            {
                return false;
            }

            solvedLocal = candidate;
            return true;
        }

        /// <summary>
        /// Propagates an untouched node's global transform under a modified parent transform:
        /// <c>L = currentGlobal * currentParentGlobal^-1</c>, <c>R' = L * solvedParentGlobal</c>.
        /// </summary>
        public static bool TryPropagate(
            FbxMatrix4x4 currentGlobal,
            FbxMatrix4x4 currentParentGlobal,
            FbxMatrix4x4 solvedParentGlobal,
            out FbxMatrix4x4 currentLocal,
            out FbxMatrix4x4 solvedGlobal)
        {
            currentLocal = FbxMatrix4x4.Identity;
            solvedGlobal = FbxMatrix4x4.Identity;
            if (!FbxAffineSpaceTransform.TryCreate(currentGlobal, out _) ||
                !FbxAffineSpaceTransform.TryCreate(solvedParentGlobal, out _))
            {
                return false;
            }

            currentLocal = currentParentGlobal.TryInverse(out var parentInv)
                ? currentGlobal * parentInv
                : currentGlobal;
            FbxMatrix4x4 candidate = currentLocal * solvedParentGlobal;
            if (!FbxAffineSpaceTransform.TryCreate(candidate, out _))
            {
                return false;
            }

            solvedGlobal = candidate;
            return true;
        }

        /// <summary>
        /// Converts a global row-vector transform from a source coordinate space into a target coordinate space
        /// via <c>Lin(spaceTransform)^-1 * global * spaceTransform</c>.
        /// </summary>
        public static bool TryConvertGlobalSpace(
            FbxMatrix4x4 global,
            FbxMatrix4x4 spaceTransform,
            out FbxMatrix4x4 convertedGlobal)
        {
            convertedGlobal = FbxMatrix4x4.Identity;
            if (!FbxAffineSpaceTransform.TryCreate(global, out _) ||
                !FbxAffineSpaceTransform.TryCreate(spaceTransform, out _))
            {
                return false;
            }

            if (FbxAffineSpaceTransform.IsExactIdentity(spaceTransform))
            {
                convertedGlobal = global;
                return true;
            }

            FbxMatrix4x4 linSpace = FbxLinearAlgebra3d.ExtractLinear(spaceTransform);
            if (!FbxAffineSpaceTransform.TryCreate(linSpace, out var linXform))
            {
                return false;
            }

            FbxMatrix4x4 converted = linXform.InverseOffset * global * spaceTransform;
            if (!FbxAffineSpaceTransform.TryCreate(converted, out _))
            {
                return false;
            }

            convertedGlobal = converted;
            return true;
        }

        /// <summary>
        /// Converts a parent-relative local row-vector transform from a source coordinate space into a target coordinate space
        /// via <c>Lin(spaceTransform)^-1 * local * Lin(spaceTransform)</c>.
        /// </summary>
        public static bool TryConvertLocalSpace(
            FbxMatrix4x4 local,
            FbxMatrix4x4 spaceTransform,
            out FbxMatrix4x4 convertedLocal)
        {
            convertedLocal = FbxMatrix4x4.Identity;
            if (!FbxAffineSpaceTransform.TryCreate(local, out _) ||
                !FbxAffineSpaceTransform.TryCreate(spaceTransform, out _))
            {
                return false;
            }

            if (FbxAffineSpaceTransform.IsExactIdentity(spaceTransform))
            {
                convertedLocal = local;
                return true;
            }

            FbxMatrix4x4 linSpace = FbxLinearAlgebra3d.ExtractLinear(spaceTransform);
            if (!FbxAffineSpaceTransform.TryCreate(linSpace, out var linXform))
            {
                return false;
            }

            FbxMatrix4x4 converted = linXform.InverseOffset * local * linXform.Offset;
            if (!FbxAffineSpaceTransform.TryCreate(converted, out _))
            {
                return false;
            }

            convertedLocal = converted;
            return true;
        }

        /// <summary>
        /// Solves a single set of same-space <paramref name="edits"/> against <paramref name="targetGlobals"/>
        /// in parent-before-child hierarchy order.
        /// </summary>
        public static TransformHierarchyResult SolveHierarchy(
            IReadOnlyDictionary<string, FbxMatrix4x4> targetGlobals,
            IReadOnlyList<TransformEdit> edits,
            IReadOnlyDictionary<string, FbxMatrix4x4> referenceGlobals = null,
            double translationTolerance = 1e-4d)
        {
            if (edits == null || edits.Count == 0)
            {
                return new TransformHierarchyResult(
                    targetGlobals,
                    new Dictionary<string, SolvedNodeTransform>(StringComparer.Ordinal),
                    new List<string>(),
                    new List<TransformSolveDiagnostic>());
            }

            Dictionary<string, FbxMatrix4x4> combinedRefs = BuildCombinedReferenceMap(edits, referenceGlobals);
            var singleSet = new TransformChangeSet(string.Empty, edits, combinedRefs);
            return SolveHierarchy(targetGlobals, new[] { singleSet }, translationTolerance);
        }

        /// <summary>
        /// Solves one or more same-space <paramref name="changeSets"/> against <paramref name="targetGlobals"/>
        /// in parent-before-child hierarchy order, verifying pairwise local-transform agreement across overlapping sources.
        /// </summary>
        public static TransformHierarchyResult SolveHierarchy(
            IReadOnlyDictionary<string, FbxMatrix4x4> targetGlobals,
            IReadOnlyList<TransformChangeSet> changeSets,
            double translationTolerance = 1e-4d)
        {
            var solvedByPath = new Dictionary<string, SolvedNodeTransform>(StringComparer.Ordinal);
            var orderedPaths = new List<string>();
            var diagnostics = new List<TransformSolveDiagnostic>();
            var result = new TransformHierarchyResult(targetGlobals, solvedByPath, orderedPaths, diagnostics);

            if (changeSets == null || changeSets.Count == 0)
            {
                return result;
            }

            var candidatesByPath = new Dictionary<string, List<(TransformChangeSet Set, TransformEdit Edit, IReadOnlyDictionary<string, FbxMatrix4x4> Refs)>>(StringComparer.Ordinal);
            for (int s = 0; s < changeSets.Count; s++)
            {
                var set = changeSets[s];
                if (set?.Edits == null || set.Edits.Count == 0)
                {
                    continue;
                }

                var combinedRefs = BuildCombinedReferenceMap(set.Edits, set.ReferenceGlobals);
                for (int e = 0; e < set.Edits.Count; e++)
                {
                    var edit = set.Edits[e];
                    string normPath = FbxNameUtility.NormalizePath(edit.Path);
                    if (string.IsNullOrEmpty(normPath))
                    {
                        continue;
                    }

                    if (!candidatesByPath.TryGetValue(normPath, out var list))
                    {
                        list = new List<(TransformChangeSet, TransformEdit, IReadOnlyDictionary<string, FbxMatrix4x4>)>(1);
                        candidatesByPath.Add(normPath, list);
                    }

                    list.Add((set, edit, combinedRefs));
                }
            }

            if (candidatesByPath.Count == 0)
            {
                return result;
            }

            var allCandidatePaths = new List<string>(candidatesByPath.Keys);
            allCandidatePaths.Sort(FbxNameUtility.CompareHierarchyOrder);

            double safeTransTol = (!double.IsNaN(translationTolerance) && !double.IsInfinity(translationTolerance) && translationTolerance > 0d)
                ? translationTolerance
                : 1e-4d;

            for (int idx = 0; idx < allCandidatePaths.Count; idx++)
            {
                string path = allCandidatePaths[idx];
                var candidates = candidatesByPath[path];
                string parentPath = FbxNameUtility.GetParentPath(path);

                FbxMatrix4x4 preTargetGlobal = FbxMatrix4x4.Identity;
                bool hasPreTarget = targetGlobals != null && targetGlobals.TryGetValue(path, out preTargetGlobal);
                var evaluated = new List<SolvedNodeTransform>(candidates.Count);

                for (int c = 0; c < candidates.Count; c++)
                {
                    var (set, edit, refs) = candidates[c];

                    FbxMatrix4x4 parentPostGlobal = FbxMatrix4x4.Identity;
                    if (!string.IsNullOrEmpty(parentPath) && !result.TryGetGlobal(parentPath, out parentPostGlobal))
                    {
                        parentPostGlobal = FbxMatrix4x4.Identity;
                    }

                    if (!FbxAffineSpaceTransform.TryCreate(parentPostGlobal, out var parentXform))
                    {
                        diagnostics.Add(new TransformSolveDiagnostic(
                            "TRANSFORM_SOLVE_SINGULAR_PARENT",
                            $"Singular parent transform '{parentPath}' while solving '{path}'.",
                            set.SourceId,
                            path));
                        return result;
                    }

                    FbxMatrix4x4 postGlobal;
                    FbxMatrix4x4 postLocal;
                    if (hasPreTarget && edit.ReferenceGlobal.HasValue)
                    {
                        if (!TrySolveGlobal(preTargetGlobal, edit.ReferenceGlobal.Value, edit.Global, out postGlobal))
                        {
                            diagnostics.Add(new TransformSolveDiagnostic(
                                "TRANSFORM_SOLVE_SINGULAR_BASIS",
                                $"Singular or invalid global calibration basis for '{path}'.",
                                set.SourceId,
                                path));
                            return result;
                        }

                        postLocal = postGlobal * parentXform.InverseOffset;
                    }
                    else
                    {
                        FbxMatrix4x4 dParent = result.ResolveBasis(refs, parentPath);
                        if (!FbxAffineSpaceTransform.TryCreate(dParent, out var dParentXform))
                        {
                            diagnostics.Add(new TransformSolveDiagnostic(
                                "TRANSFORM_SOLVE_SINGULAR_BASIS",
                                $"Singular parent calibration basis for '{path}'.",
                                set.SourceId,
                                path));
                            return result;
                        }

                        postLocal = dParentXform.InverseOffset * edit.Local * dParentXform.Offset;
                        postGlobal = postLocal * parentPostGlobal;
                    }

                    if (!FbxAffineSpaceTransform.TryCreate(postGlobal, out _) ||
                        !FbxAffineSpaceTransform.TryCreate(postLocal, out _))
                    {
                        diagnostics.Add(new TransformSolveDiagnostic(
                            "TRANSFORM_SOLVE_INVALID_OUTPUT",
                            $"Solved transform matrix for '{path}' is singular or non-finite.",
                            set.SourceId,
                            path));
                        return result;
                    }

                    evaluated.Add(new SolvedNodeTransform(
                        path,
                        parentPath,
                        set.SourceId,
                        isCreated: !hasPreTarget,
                        postGlobal,
                        postLocal));
                }

                // Order-independent pairwise agreement check on pure solved Local transforms.
                (string FirstId, string SecondId)? conflictPair = null;
                for (int i = 0; i < evaluated.Count; i++)
                {
                    for (int j = i + 1; j < evaluated.Count; j++)
                    {
                        if (!MatricesMatch(evaluated[i].Local, evaluated[j].Local, k_LinearAgreementTolerance, safeTransTol))
                        {
                            string sA = evaluated[i].SourceId;
                            string sB = evaluated[j].SourceId;
                            if (StringComparer.Ordinal.Compare(sA, sB) > 0)
                            {
                                (sA, sB) = (sB, sA);
                            }

                            if (!conflictPair.HasValue ||
                                StringComparer.Ordinal.Compare(sA, conflictPair.Value.FirstId) < 0 ||
                                (string.Equals(sA, conflictPair.Value.FirstId, StringComparison.Ordinal) &&
                                 StringComparer.Ordinal.Compare(sB, conflictPair.Value.SecondId) < 0))
                            {
                                conflictPair = (sA, sB);
                            }
                        }
                    }
                }

                if (conflictPair.HasValue)
                {
                    diagnostics.Add(new TransformSolveDiagnostic(
                        "TRANSFORM_SOLVE_CONFLICT",
                        $"Conflicting transforms for '{path}' between '{conflictPair.Value.FirstId}' and '{conflictPair.Value.SecondId}'.",
                        conflictPair.Value.FirstId,
                        path));
                    return result;
                }

                var winner = SelectDeterministicWinner(evaluated);
                result.RegisterSolvedNode(winner);
            }

            return result;
        }

        private static Dictionary<string, FbxMatrix4x4> BuildCombinedReferenceMap(
            IReadOnlyList<TransformEdit> edits,
            IReadOnlyDictionary<string, FbxMatrix4x4> referenceGlobals)
        {
            int capacity = (edits?.Count ?? 0) + (referenceGlobals?.Count ?? 0);
            var map = new Dictionary<string, FbxMatrix4x4>(capacity, StringComparer.Ordinal);
            if (referenceGlobals != null)
            {
                foreach (var kv in referenceGlobals)
                {
                    string norm = FbxNameUtility.NormalizePath(kv.Key);
                    if (!string.IsNullOrEmpty(norm))
                    {
                        map[norm] = kv.Value;
                    }
                }
            }

            if (edits != null)
            {
                for (int i = 0; i < edits.Count; i++)
                {
                    string norm = FbxNameUtility.NormalizePath(edits[i].Path);
                    if (!string.IsNullOrEmpty(norm))
                    {
                        map[norm] = edits[i].Global;
                    }
                }
            }

            return map;
        }

        private static SolvedNodeTransform SelectDeterministicWinner(List<SolvedNodeTransform> candidates)
        {
            if (candidates.Count == 1)
            {
                return candidates[0];
            }

            var min = new double[16];
            var max = new double[16];
            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++)
                {
                    double v = CanonicalZero(candidates[0].Local[r, c]);
                    int k = (r * 4) + c;
                    min[k] = v;
                    max[k] = v;
                }
            }

            for (int i = 1; i < candidates.Count; i++)
            {
                var m = candidates[i].Local;
                for (int r = 0; r < 4; r++)
                {
                    for (int c = 0; c < 4; c++)
                    {
                        double v = CanonicalZero(m[r, c]);
                        int k = (r * 4) + c;
                        if (v < min[k]) min[k] = v;
                        if (v > max[k]) max[k] = v;
                    }
                }
            }

            int bestIdx = 0;
            double bestDist = ComputeMidpointDistance(candidates[0].Local, min, max);
            for (int i = 1; i < candidates.Count; i++)
            {
                double dist = ComputeMidpointDistance(candidates[i].Local, min, max);
                int cmp = dist.CompareTo(bestDist);
                if (cmp < 0 || (cmp == 0 && CompareSolvedNodesCanonical(candidates[i], candidates[bestIdx]) < 0))
                {
                    bestIdx = i;
                    bestDist = dist;
                }
            }

            return candidates[bestIdx];
        }

        private static double ComputeMidpointDistance(in FbxMatrix4x4 m, double[] min, double[] max)
        {
            double maxDist = 0d;
            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++)
                {
                    int k = (r * 4) + c;
                    double mid = 0.5d * (min[k] + max[k]);
                    double d = Math.Abs(CanonicalZero(m[r, c]) - mid);
                    if (d > maxDist) maxDist = d;
                }
            }

            return maxDist;
        }

        private static int CompareSolvedNodesCanonical(SolvedNodeTransform left, SolvedNodeTransform right)
        {
            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++)
                {
                    int cmp = CanonicalZero(left.Local[r, c]).CompareTo(CanonicalZero(right.Local[r, c]));
                    if (cmp != 0) return cmp;
                }
            }

            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++)
                {
                    int cmp = CanonicalZero(left.Global[r, c]).CompareTo(CanonicalZero(right.Global[r, c]));
                    if (cmp != 0) return cmp;
                }
            }

            return StringComparer.Ordinal.Compare(left.SourceId, right.SourceId);
        }

        private static double CanonicalZero(double value) => value == 0d ? 0d : value;

        private static bool MatricesMatch(
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
