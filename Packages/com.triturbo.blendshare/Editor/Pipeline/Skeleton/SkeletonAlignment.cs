using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Model.Documents;

namespace Triturbo.BlendShare.Pipeline.Skeleton
{
    /// <summary>
    /// Per-patch skeleton input into <see cref="SkeletonAlignment.Solve"/>.
    /// </summary>
    public readonly struct SkeletonAlignmentPatchInput
    {
        public string PatchId { get; }
        public SkeletonDocument Skeleton { get; }
        public FbxMatrix4x4 StoredToTargetWorld { get; }
        public ISet<string> IgnoredNodePaths { get; }

        public SkeletonAlignmentPatchInput(
            string patchId,
            SkeletonDocument skeleton,
            FbxMatrix4x4 storedToTargetWorld,
            ISet<string> ignoredNodePaths = null)
        {
            PatchId = patchId ?? string.Empty;
            Skeleton = skeleton;
            StoredToTargetWorld = storedToTargetWorld;
            IgnoredNodePaths = ignoredNodePaths;
        }
    }

    /// <summary>
    /// Diagnostic produced by <see cref="SkeletonAlignment"/>.
    /// </summary>
    public readonly struct SkeletonAlignmentDiagnostic
    {
        public bool IsError { get; }
        public string Code { get; }
        public string Message { get; }
        public string PatchId { get; }
        public string NodePath { get; }

        public SkeletonAlignmentDiagnostic(
            bool isError,
            string code,
            string message,
            string patchId = null,
            string nodePath = null)
        {
            IsError = isError;
            Code = code ?? string.Empty;
            Message = message ?? string.Empty;
            PatchId = patchId ?? string.Empty;
            NodePath = nodePath ?? string.Empty;
        }
    }

    /// <summary>
    /// Caller-side skeleton adapter bridging <see cref="SkeletonRestFrame"/>, <see cref="SkeletonDocument"/>,
    /// and the pure same-space <see cref="TransformSolver"/>. Owns per-chain bindpose conflict isolation,
    /// space conversion into target coordinates, ignored/proxy node anchor retention, created-node
    /// <see cref="SkeletonNodeKind"/> agreement, and live <c>PoseDelta</c> writeback composition.
    /// </summary>
    public sealed class SkeletonAlignment
    {
        private const double k_MultiPatchTranslationMetersTolerance = 1e-4d;

        private readonly struct FrameCacheKey : IEquatable<FrameCacheKey>
        {
            private readonly SkeletonDocument m_Skeleton;
            private readonly FbxMatrix4x4 m_Space;

            public FrameCacheKey(SkeletonDocument skeleton, FbxMatrix4x4 space)
            {
                m_Skeleton = skeleton;
                m_Space = space;
            }

            public bool Equals(FrameCacheKey other) =>
                ReferenceEquals(m_Skeleton, other.m_Skeleton) && m_Space.Equals(other.m_Space);

            public override bool Equals(object obj) => obj is FrameCacheKey other && Equals(other);

            public override int GetHashCode() =>
                HashCode.Combine(RuntimeHelpers.GetHashCode(m_Skeleton), m_Space.GetHashCode());
        }

        private readonly List<SkeletonAlignmentDiagnostic> m_Diagnostics;
        private readonly Dictionary<string, SkeletonNodeKind> m_NodeKindsByPath;
        private readonly Dictionary<string, FbxMatrix4x4> m_PostWriteGlobalMemo =
            new Dictionary<string, FbxMatrix4x4>(StringComparer.Ordinal);
        private readonly Dictionary<FrameCacheKey, Dictionary<string, FbxMatrix4x4>> m_ConvertedFrameCache =
            new Dictionary<FrameCacheKey, Dictionary<string, FbxMatrix4x4>>();

        public SkeletonRestFrame PreEditTarget { get; }
        public TransformHierarchyResult HierarchyResult { get; }
        public IReadOnlyList<string> OrderedSolvedPaths => HierarchyResult.OrderedPaths;
        public IReadOnlyDictionary<string, SolvedNodeTransform> SolvedNodesByPath => HierarchyResult.NodesByPath;
        public IReadOnlyList<SkeletonAlignmentDiagnostic> Diagnostics => m_Diagnostics;
        public bool HasErrors { get; }

        private SkeletonAlignment(
            SkeletonRestFrame preEditTarget,
            TransformHierarchyResult hierarchyResult,
            List<SkeletonAlignmentDiagnostic> diagnostics,
            Dictionary<string, SkeletonNodeKind> nodeKindsByPath,
            Dictionary<FrameCacheKey, Dictionary<string, FbxMatrix4x4>> convertedFrameCache = null)
        {
            PreEditTarget = preEditTarget;
            HierarchyResult = hierarchyResult ?? TransformSolver.SolveHierarchy(
                preEditTarget?.RestGlobals,
                Array.Empty<TransformChangeSet>());
            m_Diagnostics = diagnostics ?? new List<SkeletonAlignmentDiagnostic>();
            m_NodeKindsByPath = nodeKindsByPath ?? new Dictionary<string, SkeletonNodeKind>(StringComparer.Ordinal);
            if (convertedFrameCache != null)
            {
                foreach (var kv in convertedFrameCache)
                {
                    m_ConvertedFrameCache[kv.Key] = kv.Value;
                }
            }

            bool anyError = false;
            for (int i = 0; i < m_Diagnostics.Count; i++)
            {
                if (m_Diagnostics[i].IsError)
                {
                    anyError = true;
                    break;
                }
            }

            HasErrors = anyError;
        }

        /// <summary>
        /// Converts <paramref name="patches"/> into target coordinate space, validates per-chain pre-edit bindpose
        /// consistency, solves transforms via <see cref="TransformSolver.SolveHierarchy(IReadOnlyDictionary{string, FbxMatrix4x4}, IReadOnlyList{TransformChangeSet}, double)"/>,
        /// and verifies created-node <see cref="SkeletonNodeKind"/> agreement.
        /// </summary>
        public static SkeletonAlignment Solve(
            SkeletonRestFrame preEditTarget,
            IReadOnlyList<SkeletonAlignmentPatchInput> patches)
        {
            var diagnostics = new List<SkeletonAlignmentDiagnostic>();
            var nodeKindsByPath = new Dictionary<string, SkeletonNodeKind>(StringComparer.Ordinal);
            var frameCache = new Dictionary<FrameCacheKey, Dictionary<string, FbxMatrix4x4>>();
            var targetGlobals = preEditTarget?.RestGlobals;

            var emptyHierarchy = TransformSolver.SolveHierarchy(
                targetGlobals,
                Array.Empty<TransformChangeSet>());

            if (patches == null || patches.Count == 0)
            {
                return new SkeletonAlignment(preEditTarget, emptyHierarchy, diagnostics, nodeKindsByPath, frameCache);
            }

            var candidatesByPath = new Dictionary<string, List<(SkeletonAlignmentPatchInput Patch, SkeletonNodeRecord Node)>>(StringComparer.Ordinal);
            for (int p = 0; p < patches.Count; p++)
            {
                var patch = patches[p];
                var skeleton = patch.Skeleton;
                if (skeleton?.Nodes == null || skeleton.Nodes.Count == 0)
                {
                    continue;
                }

                for (int n = 0; n < skeleton.Nodes.Count; n++)
                {
                    var node = skeleton.Nodes[n];
                    if (node == null)
                    {
                        continue;
                    }

                    string normPath = FbxNameUtility.NormalizePath(node.Path);
                    if (string.IsNullOrEmpty(normPath))
                    {
                        continue;
                    }

                    if (patch.IgnoredNodePaths != null && patch.IgnoredNodePaths.Contains(normPath))
                    {
                        continue;
                    }

                    if (!candidatesByPath.TryGetValue(normPath, out var list))
                    {
                        list = new List<(SkeletonAlignmentPatchInput, SkeletonNodeRecord)>(1);
                        candidatesByPath.Add(normPath, list);
                    }

                    list.Add((patch, node));
                }
            }

            if (candidatesByPath.Count == 0)
            {
                return new SkeletonAlignment(preEditTarget, emptyHierarchy, diagnostics, nodeKindsByPath, frameCache);
            }

            var orderedCandidatePaths = new List<string>(candidatesByPath.Keys);
            orderedCandidatePaths.Sort(FbxNameUtility.CompareHierarchyOrder);

            // Validate per-chain pre-edit bindpose consistency and space transforms in deterministic hierarchy order.
            for (int i = 0; i < orderedCandidatePaths.Count; i++)
            {
                string path = orderedCandidatePaths[i];
                var cands = candidatesByPath[path];
                if (preEditTarget != null && !preEditTarget.TryValidateChain(path, out string conflictErr))
                {
                    diagnostics.Add(new SkeletonAlignmentDiagnostic(
                        isError: true,
                        code: "SKEL_ALIGN_CONFLICTING_TARGET_REST",
                        message: conflictErr,
                        patchId: cands[0].Patch.PatchId,
                        nodePath: path));
                    return new SkeletonAlignment(preEditTarget, emptyHierarchy, diagnostics, nodeKindsByPath, frameCache);
                }

                for (int c = 0; c < cands.Count; c++)
                {
                    var patchInput = cands[c].Patch;
                    if (!FbxAffineSpaceTransform.TryCreate(patchInput.StoredToTargetWorld, out _))
                    {
                        diagnostics.Add(new SkeletonAlignmentDiagnostic(
                            isError: true,
                            code: "SKEL_ALIGN_INVALID_SPACE_TRANSFORM",
                            message: $"Invalid stored-to-target scene coordinate transform for patch '{patchInput.PatchId}' at '{path}'.",
                            patchId: patchInput.PatchId,
                            nodePath: path));
                        return new SkeletonAlignment(preEditTarget, emptyHierarchy, diagnostics, nodeKindsByPath, frameCache);
                    }
                }
            }

            // Convert active edits and full reference frames (Anchors + ALL Nodes, including ignored proxy parents)
            // into target coordinate space before invoking the pure TransformSolver.
            var changeSets = new List<TransformChangeSet>(patches.Count);
            for (int p = 0; p < patches.Count; p++)
            {
                var patch = patches[p];
                var skeleton = patch.Skeleton;
                if (skeleton?.Nodes == null || skeleton.Nodes.Count == 0)
                {
                    continue;
                }

                FbxMatrix4x4 h = patch.StoredToTargetWorld;
                var edits = new List<TransformEdit>(skeleton.Nodes.Count);
                for (int n = 0; n < skeleton.Nodes.Count; n++)
                {
                    var node = skeleton.Nodes[n];
                    if (node == null)
                    {
                        continue;
                    }

                    string normPath = FbxNameUtility.NormalizePath(node.Path);
                    if (string.IsNullOrEmpty(normPath) ||
                        (patch.IgnoredNodePaths != null && patch.IgnoredNodePaths.Contains(normPath)))
                    {
                        continue;
                    }

                    if (!TransformSolver.TryConvertGlobalSpace(node.RestGlobal, h, out var convGlobal) ||
                        !TransformSolver.TryConvertLocalSpace(node.RestLocal, h, out var convLocal))
                    {
                        diagnostics.Add(new SkeletonAlignmentDiagnostic(
                            isError: true,
                            code: "SKEL_ALIGN_INVALID_MATRIX",
                            message: $"Skeleton node '{normPath}' in patch '{patch.PatchId}' has singular or non-finite rest matrices.",
                            patchId: patch.PatchId,
                            nodePath: normPath));
                        return new SkeletonAlignment(preEditTarget, emptyHierarchy, diagnostics, nodeKindsByPath, frameCache);
                    }

                    FbxMatrix4x4? convBaseGlobal = null;
                    if (node.HasBaseRest)
                    {
                        if (!TransformSolver.TryConvertGlobalSpace(node.BaseRestGlobal, h, out var baseConv))
                        {
                            diagnostics.Add(new SkeletonAlignmentDiagnostic(
                                isError: true,
                                code: "SKEL_ALIGN_INVALID_MATRIX",
                                message: $"Skeleton node '{normPath}' in patch '{patch.PatchId}' has a singular or non-finite BaseRestGlobal matrix.",
                                patchId: patch.PatchId,
                                nodePath: normPath));
                            return new SkeletonAlignment(preEditTarget, emptyHierarchy, diagnostics, nodeKindsByPath, frameCache);
                        }

                        convBaseGlobal = baseConv;
                    }

                    edits.Add(new TransformEdit(normPath, convGlobal, convLocal, convBaseGlobal));
                }

                if (edits.Count == 0)
                {
                    continue;
                }

                var refGlobals = GetOrBuildConvertedFrames(skeleton, h, frameCache);
                changeSets.Add(new TransformChangeSet(patch.PatchId, edits, refGlobals));
            }

            double unitMeters = preEditTarget != null && preEditTarget.UnitMeters > 0d ? preEditTarget.UnitMeters : 1d;
            double transTolUnits = Math.Max(1e-4d, k_MultiPatchTranslationMetersTolerance / unitMeters);

            var hierarchyResult = TransformSolver.SolveHierarchy(targetGlobals, changeSets, transTolUnits);
            if (hierarchyResult.HasErrors)
            {
                for (int i = 0; i < hierarchyResult.Diagnostics.Count; i++)
                {
                    var d = hierarchyResult.Diagnostics[i];
                    string mappedCode = string.Equals(d.Code, "TRANSFORM_SOLVE_CONFLICT", StringComparison.Ordinal)
                        ? "SKEL_ALIGN_MULTI_PATCH_CONFLICT"
                        : "SKEL_ALIGN_INVALID_MATRIX";
                    diagnostics.Add(new SkeletonAlignmentDiagnostic(
                        isError: true,
                        code: mappedCode,
                        message: d.Message,
                        patchId: d.SourceId,
                        nodePath: d.Path));
                }

                return new SkeletonAlignment(preEditTarget, hierarchyResult, diagnostics, nodeKindsByPath, frameCache);
            }

            var alignment = new SkeletonAlignment(preEditTarget, hierarchyResult, diagnostics, nodeKindsByPath, frameCache);

            // Validate created-node SkeletonNodeKind agreement and posed write-parent invertibility in hierarchy order.
            for (int i = 0; i < hierarchyResult.OrderedPaths.Count; i++)
            {
                string path = hierarchyResult.OrderedPaths[i];
                var solved = hierarchyResult.NodesByPath[path];
                var cands = candidatesByPath[path];

                if (!string.IsNullOrEmpty(solved.ParentPath) &&
                    alignment.TryGetPostEditWriteGlobal(solved.ParentPath, out var parentWrite) &&
                    !FbxAffineSpaceTransform.TryCreate(parentWrite, out _))
                {
                    diagnostics.Add(new SkeletonAlignmentDiagnostic(
                        isError: true,
                        code: "SKEL_ALIGN_INVALID_MATRIX",
                        message: $"Singular posed parent transform '{solved.ParentPath}' while solving '{path}'.",
                        patchId: solved.SourceId,
                        nodePath: path));
                    return new SkeletonAlignment(preEditTarget, hierarchyResult, diagnostics, nodeKindsByPath, frameCache);
                }

                if (solved.IsCreated && cands.Count > 1)
                {
                    (string FirstId, string SecondId)? kindConflict = null;
                    for (int a = 0; a < cands.Count; a++)
                    {
                        for (int b = a + 1; b < cands.Count; b++)
                        {
                            if (cands[a].Node.Kind != cands[b].Node.Kind)
                            {
                                string pA = cands[a].Patch.PatchId;
                                string pB = cands[b].Patch.PatchId;
                                if (StringComparer.Ordinal.Compare(pA, pB) > 0)
                                {
                                    (pA, pB) = (pB, pA);
                                }

                                if (!kindConflict.HasValue ||
                                    StringComparer.Ordinal.Compare(pA, kindConflict.Value.FirstId) < 0 ||
                                    (string.Equals(pA, kindConflict.Value.FirstId, StringComparison.Ordinal) &&
                                     StringComparer.Ordinal.Compare(pB, kindConflict.Value.SecondId) < 0))
                                {
                                    kindConflict = (pA, pB);
                                }
                            }
                        }
                    }

                    if (kindConflict.HasValue)
                    {
                        diagnostics.Add(new SkeletonAlignmentDiagnostic(
                            isError: true,
                            code: "SKEL_ALIGN_MULTI_PATCH_CONFLICT",
                            message: $"Conflicting skeleton node kinds for '{path}' between patch '{kindConflict.Value.FirstId}' and patch '{kindConflict.Value.SecondId}'.",
                            patchId: kindConflict.Value.FirstId,
                            nodePath: path));
                        return new SkeletonAlignment(preEditTarget, hierarchyResult, diagnostics, nodeKindsByPath, frameCache);
                    }
                }

                SkeletonNodeKind kind = cands[0].Node.Kind;
                for (int c = 0; c < cands.Count; c++)
                {
                    if (string.Equals(cands[c].Patch.PatchId, solved.SourceId, StringComparison.Ordinal))
                    {
                        kind = cands[c].Node.Kind;
                        break;
                    }
                }

                nodeKindsByPath[path] = kind;
            }

            return alignment;
        }

        /// <summary>
        /// Returns the authored <see cref="SkeletonNodeKind"/> for a solved node at <paramref name="path"/>.
        /// </summary>
        public SkeletonNodeKind GetNodeKind(string path)
        {
            string norm = FbxNameUtility.NormalizePath(path);
            return !string.IsNullOrEmpty(norm) && m_NodeKindsByPath.TryGetValue(norm, out var kind)
                ? kind
                : SkeletonNodeKind.Bone;
        }

        /// <summary>
        /// Computes the pose-preserving local write matrix (<c>PoseDelta * RestLocal_post</c>) for <paramref name="path"/>.
        /// </summary>
        public FbxMatrix4x4 GetWriteLocal(string path)
        {
            if (!HierarchyResult.TryGetLocal(path, out var postLocal))
            {
                return FbxMatrix4x4.Identity;
            }

            return PreEditTarget != null
                ? PreEditTarget.ApplyPoseDelta(path, postLocal)
                : postLocal;
        }

        /// <summary>
        /// Evaluates the post-edit Target live global matrix (<c>W'_b</c>) for <paramref name="path"/>
        /// by composing <c>PoseDelta * L'_x</c> top-down along the hierarchy.
        /// </summary>
        public bool TryGetPostEditWriteGlobal(string path, out FbxMatrix4x4 postWriteGlobal)
        {
            string norm = FbxNameUtility.NormalizePath(path);
            if (string.IsNullOrEmpty(norm))
            {
                postWriteGlobal = FbxMatrix4x4.Identity;
                return true;
            }

            return TryResolvePostEditWriteGlobalRecursive(norm, new HashSet<string>(StringComparer.Ordinal), out postWriteGlobal);
        }

        /// <summary>
        /// Evaluates the post-edit Target rest global matrix (<c>R'_b</c>) for <paramref name="path"/>,
        /// failing closed if <paramref name="path"/> or an ancestor has conflicting pre-edit bindposes.
        /// </summary>
        public bool TryGetPostEditRestGlobal(string path, out FbxMatrix4x4 postRestGlobal, out string error)
        {
            postRestGlobal = FbxMatrix4x4.Identity;
            error = null;
            if (PreEditTarget != null && !PreEditTarget.TryValidateChain(path, out error))
            {
                return false;
            }

            return HierarchyResult.TryGetGlobal(path, out postRestGlobal);
        }

        /// <summary>
        /// Evaluates the post-edit Target rest local matrix (<c>L'_b</c>) for <paramref name="path"/>,
        /// failing closed if <paramref name="path"/> or an ancestor has conflicting pre-edit bindposes.
        /// </summary>
        public bool TryGetPostEditRestLocal(string path, out FbxMatrix4x4 postRestLocal, out string error)
        {
            postRestLocal = FbxMatrix4x4.Identity;
            error = null;
            if (PreEditTarget != null && !PreEditTarget.TryValidateChain(path, out error))
            {
                return false;
            }

            return HierarchyResult.TryGetLocal(path, out postRestLocal);
        }

        /// <summary>
        /// Resolves the post-edit endpoint calibration basis <c>D_frame(b) = Lin(S_b) * Lin(H) * Lin(R'_b)^-1</c>
        /// via <see cref="TransformHierarchyResult.ResolveBasis"/>, validating pre-edit chain consistency first.
        /// </summary>
        public bool TryResolveEndpointBasis(
            SkeletonDocument patchSkeleton,
            FbxMatrix4x4 storedToTargetWorld,
            string bonePath,
            out FbxMatrix4x4 endpointD,
            out string error)
        {
            endpointD = FbxLinearAlgebra3d.ExtractLinear(storedToTargetWorld);
            error = null;
            if (PreEditTarget != null && !PreEditTarget.TryValidateChain(bonePath, out error))
            {
                return false;
            }

            if (patchSkeleton == null || !FbxAffineSpaceTransform.TryCreate(storedToTargetWorld, out _))
            {
                return true;
            }

            var convertedFrames = GetOrBuildConvertedFrames(patchSkeleton, storedToTargetWorld, m_ConvertedFrameCache);
            FbxMatrix4x4 sameSpaceBasis = HierarchyResult.ResolveBasis(convertedFrames, bonePath);
            FbxMatrix4x4 candidate = endpointD * sameSpaceBasis;
            if (FbxAffineSpaceTransform.TryCreate(candidate, out _))
            {
                endpointD = candidate;
            }

            return true;
        }

        private bool TryResolvePostEditWriteGlobalRecursive(
            string normPath,
            HashSet<string> visited,
            out FbxMatrix4x4 postWriteGlobal)
        {
            if (m_PostWriteGlobalMemo.TryGetValue(normPath, out postWriteGlobal))
            {
                return true;
            }

            if (!visited.Add(normPath))
            {
                postWriteGlobal = FbxMatrix4x4.Identity;
                return false;
            }

            if (HierarchyResult.NodesByPath.TryGetValue(normPath, out var solved))
            {
                FbxMatrix4x4 writeLocal = PreEditTarget != null
                    ? PreEditTarget.ApplyPoseDelta(normPath, solved.Local)
                    : solved.Local;
                FbxMatrix4x4 parentWrite = FbxMatrix4x4.Identity;
                if (!string.IsNullOrEmpty(solved.ParentPath))
                {
                    if (!TryResolvePostEditWriteGlobalRecursive(solved.ParentPath, visited, out parentWrite) &&
                        !HierarchyResult.TryGetGlobal(solved.ParentPath, out parentWrite))
                    {
                        parentWrite = FbxMatrix4x4.Identity;
                    }
                }

                postWriteGlobal = writeLocal * parentWrite;
                m_PostWriteGlobalMemo[normPath] = postWriteGlobal;
                return true;
            }

            if (PreEditTarget != null && PreEditTarget.TryGetNode(normPath, out var targetNode) && targetNode != null)
            {
                if (string.IsNullOrEmpty(targetNode.ParentPath))
                {
                    postWriteGlobal = targetNode.LiveGlobal;
                    m_PostWriteGlobalMemo[normPath] = postWriteGlobal;
                    return true;
                }

                if (TryResolvePostEditWriteGlobalRecursive(targetNode.ParentPath, visited, out var parentPostWrite))
                {
                    FbxMatrix4x4 effectiveLiveLocal = targetNode.PoseDelta * targetNode.RestLocal;
                    postWriteGlobal = effectiveLiveLocal * parentPostWrite;
                    m_PostWriteGlobalMemo[normPath] = postWriteGlobal;
                    return true;
                }

                postWriteGlobal = targetNode.LiveGlobal;
                m_PostWriteGlobalMemo[normPath] = postWriteGlobal;
                return true;
            }

            postWriteGlobal = FbxMatrix4x4.Identity;
            return false;
        }

        private static Dictionary<string, FbxMatrix4x4> GetOrBuildConvertedFrames(
            SkeletonDocument skeleton,
            FbxMatrix4x4 storedToTargetWorld,
            Dictionary<FrameCacheKey, Dictionary<string, FbxMatrix4x4>> cache)
        {
            var key = new FrameCacheKey(skeleton, storedToTargetWorld);
            if (cache != null && cache.TryGetValue(key, out var existing))
            {
                return existing;
            }

            int capacity = (skeleton.Nodes?.Count ?? 0) + (skeleton.Anchors?.Count ?? 0);
            var map = new Dictionary<string, FbxMatrix4x4>(capacity, StringComparer.Ordinal);

            if (skeleton.Anchors != null)
            {
                for (int i = 0; i < skeleton.Anchors.Count; i++)
                {
                    var anchor = skeleton.Anchors[i];
                    if (anchor == null) continue;
                    string norm = FbxNameUtility.NormalizePath(anchor.Path);
                    if (!string.IsNullOrEmpty(norm) &&
                        TransformSolver.TryConvertGlobalSpace(anchor.RestGlobal, storedToTargetWorld, out var conv))
                    {
                        map[norm] = conv;
                    }
                }
            }

            if (skeleton.Nodes != null)
            {
                for (int i = 0; i < skeleton.Nodes.Count; i++)
                {
                    var node = skeleton.Nodes[i];
                    if (node == null) continue;
                    string norm = FbxNameUtility.NormalizePath(node.Path);
                    if (!string.IsNullOrEmpty(norm) &&
                        TransformSolver.TryConvertGlobalSpace(node.RestGlobal, storedToTargetWorld, out var conv))
                    {
                        map[norm] = conv;
                    }
                }
            }

            if (cache != null)
            {
                cache[key] = map;
            }

            return map;
        }
    }
}
