using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Autodesk.Fbx;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Model.Documents;
using Triturbo.BlendShare.Pipeline.Export;
using Triturbo.BlendShare.Pipeline.Skeleton;

namespace Triturbo.BlendShare.FbxExport
{
    /// <summary>
    /// Transaction-scoped FBX skeleton planner, Autodesk FBX SDK writer, and ufbx round-trip verifier.
    /// Bridges Stage-1 <see cref="FbxRestFrameSnapshot"/> rest seeding and Stage-2 <see cref="SkeletonAlignment"/>
    /// with FBX local channel solving (<see cref="FbxNodeTransformEvaluator"/>) so posed Target FBXs,
    /// Maya <c>PreRotation</c> joint orients, <c>RrSs</c>/<c>Rrs</c> scale inheritance, and attribute-less
    /// <c>FbxNull</c> containers round-trip cleanly.
    /// </summary>
    internal sealed class SkeletonFbxWriter
    {
        private const string k_SessionStateKey = "fbx-export.skeleton-writer";
        private const double k_LinearMatrixCompareTolerance = 1e-3d;

        internal sealed class PreparedFbxSkeletonNode
        {
            public string PatchId { get; }
            public string Path { get; }
            public SkeletonNodeKind Kind { get; }
            public bool IsCreated { get; }
            public FbxNodeRawTransform SolvedRaw { get; }
            public SkeletonRawTransform VerbatimRaw { get; }
            public FbxMatrix4x4 ExpectedLocalMatrix { get; }
            public FbxMatrix4x4 ExpectedGlobalMatrix { get; }
            public FbxNodeTransformEvaluator.FbxHierarchyNodeState HierarchyState { get; }
            public FbxTransformInheritMode EffectiveInheritMode =>
                VerbatimRaw != null ? VerbatimRaw.InheritMode : SolvedRaw.OriginalInheritMode;

            public PreparedFbxSkeletonNode(
                string patchId,
                string path,
                SkeletonNodeKind kind,
                bool isCreated,
                in FbxNodeRawTransform solvedRaw,
                SkeletonRawTransform verbatimRaw,
                FbxMatrix4x4 expectedLocalMatrix,
                FbxMatrix4x4 expectedGlobalMatrix,
                in FbxNodeTransformEvaluator.FbxHierarchyNodeState hierarchyState)
            {
                PatchId = patchId ?? string.Empty;
                Path = path ?? string.Empty;
                Kind = kind;
                IsCreated = isCreated;
                SolvedRaw = solvedRaw;
                VerbatimRaw = verbatimRaw;
                ExpectedLocalMatrix = expectedLocalMatrix;
                ExpectedGlobalMatrix = expectedGlobalMatrix;
                HierarchyState = hierarchyState;
            }
        }

        private readonly struct RegisteredSkeletonEntry
        {
            public readonly string PatchId;
            public readonly FbxExportSpace Space;
            public readonly SkeletonDocument Skeleton;

            public RegisteredSkeletonEntry(string patchId, FbxExportSpace space, SkeletonDocument skeleton)
            {
                PatchId = patchId ?? string.Empty;
                Space = space;
                Skeleton = skeleton;
            }
        }

        private readonly List<RegisteredSkeletonEntry> m_Entries = new List<RegisteredSkeletonEntry>();
        private readonly HashSet<SkeletonDocument> m_SeenSkeletons =
            new HashSet<SkeletonDocument>(SkeletonReferenceComparer.Instance);
        private readonly Dictionary<string, PreparedFbxSkeletonNode> m_PreparedByPath =
            new Dictionary<string, PreparedFbxSkeletonNode>(StringComparer.Ordinal);
        private readonly List<string> m_OrderedPreparedPaths = new List<string>();
        private readonly HashSet<string> m_ReportedDiagnosticKeys = new HashSet<string>(StringComparer.Ordinal);

        public SkeletonRestFrame TargetRestFrame { get; private set; }
        public SkeletonAlignment AlignmentResult { get; private set; }
        public IReadOnlyDictionary<string, PreparedFbxSkeletonNode> PreparedNodesByPath => m_PreparedByPath;
        public IReadOnlyList<string> OrderedPreparedPaths => m_OrderedPreparedPaths;
        public bool IsSolved { get; private set; }
        public bool HasErrors { get; private set; }
        public bool AppliedToSdkScene { get; private set; }
        public bool VerifiedRoundTrip { get; private set; }

        public static SkeletonFbxWriter GetOrCreate(IDictionary<string, object> sessionState)
        {
            if (sessionState != null &&
                sessionState.TryGetValue(k_SessionStateKey, out var existing) &&
                existing is SkeletonFbxWriter writer)
            {
                return writer;
            }

            var created = new SkeletonFbxWriter();
            if (sessionState != null)
            {
                sessionState[k_SessionStateKey] = created;
            }

            return created;
        }

        public void Register(string patchId, FbxExportSpace space, SkeletonDocument skeleton)
        {
            if (skeleton == null || !skeleton.HasContent || !m_SeenSkeletons.Add(skeleton))
            {
                return;
            }

            m_Entries.Add(new RegisteredSkeletonEntry(patchId, space, skeleton));
            IsSolved = false;
        }

        public bool EnsureSolvedAndValidated(
            FbxNode rootNode,
            IFbxScene originalReaderScene,
            IReadOnlyCollection<string> priorityMeshPaths,
            double numericToleranceMeters,
            List<FbxExportDiagnostic> diagnostics)
        {
            if (IsSolved)
            {
                return !HasErrors;
            }

            m_PreparedByPath.Clear();
            m_OrderedPreparedPaths.Clear();
            HasErrors = false;

            double unitMeters = originalReaderScene != null && originalReaderScene.Metadata.UnitMeters > 0d
                ? originalReaderScene.Metadata.UnitMeters
                : 1d;
            double restConflictTolMeters = Math.Max(1e-4d, numericToleranceMeters);
            TargetRestFrame = FbxRestFrameSnapshot.Capture(originalReaderScene, priorityMeshPaths, restConflictTolMeters);

            var patchInputs = new List<SkeletonAlignmentPatchInput>(m_Entries.Count);
            var entriesByPatchAndPath = new Dictionary<string, List<(string PatchId, FbxExportSpace Space, SkeletonNodeRecord Record)>>(StringComparer.Ordinal);
            for (int i = 0; i < m_Entries.Count; i++)
            {
                var entry = m_Entries[i];
                FbxMatrix4x4 h = entry.Space != null ? entry.Space.StoredWorldToTargetWorld : FbxMatrix4x4.Identity;
                patchInputs.Add(new SkeletonAlignmentPatchInput(
                    entry.PatchId,
                    entry.Skeleton,
                    h));

                if (entry.Skeleton?.Nodes == null)
                {
                    continue;
                }

                for (int n = 0; n < entry.Skeleton.Nodes.Count; n++)
                {
                    var rec = entry.Skeleton.Nodes[n];
                    string norm = FbxNameUtility.NormalizePath(rec?.Path ?? string.Empty);
                    if (string.IsNullOrEmpty(norm))
                    {
                        continue;
                    }

                    if (!entriesByPatchAndPath.TryGetValue(norm, out var list))
                    {
                        list = new List<(string, FbxExportSpace, SkeletonNodeRecord)>(1);
                        entriesByPatchAndPath.Add(norm, list);
                    }

                    list.Add((entry.PatchId, entry.Space, rec));
                }
            }

            AlignmentResult = SkeletonAlignment.Solve(TargetRestFrame, patchInputs);
            for (int d = 0; d < AlignmentResult.Diagnostics.Count; d++)
            {
                var diag = AlignmentResult.Diagnostics[d];
                if (!diag.IsError)
                {
                    continue;
                }

                string mappedCode = diag.Code switch
                {
                    "SKEL_ALIGN_MULTI_PATCH_CONFLICT" => "FBX_EXPORT_SKELETON_CONFLICT",
                    "SKEL_ALIGN_CONFLICTING_TARGET_REST" => "FBX_EXPORT_CONFLICTING_TARGET_REST",
                    _ => "FBX_EXPORT_SKELETON_INVALID_MATRIX"
                };

                ReportOnce(
                    diagnostics,
                    $"align|{mappedCode}|{diag.NodePath}|{diag.PatchId}",
                    new FbxExportDiagnostic(
                        FbxExportDiagnosticSeverity.Error,
                        mappedCode,
                        diag.Message,
                        patchId: diag.PatchId,
                        featureTypeId: SkeletonDocument.SectionTypeId));
                HasErrors = true;
            }

            if (HasErrors || AlignmentResult.OrderedSolvedPaths.Count == 0)
            {
                IsSolved = !HasErrors;
                return !HasErrors;
            }

            // Check hierarchy reachability when SDK rootNode is provided (Preflight / Write).
            if (rootNode != null)
            {
                for (int i = 0; i < AlignmentResult.OrderedSolvedPaths.Count; i++)
                {
                    string path = AlignmentResult.OrderedSolvedPaths[i];
                    if (FbxSdkNodeUtility.FindNodeByPath(rootNode, path) != null)
                    {
                        continue;
                    }

                    var solved = AlignmentResult.SolvedNodesByPath[path];
                    if (!CanCreateNodeHierarchy(rootNode, path, out string reachError))
                    {
                        ReportOnce(
                            diagnostics,
                            $"missing|{path}|{solved.SourceId}",
                            new FbxExportDiagnostic(
                                FbxExportDiagnosticSeverity.Error,
                                "FBX_EXPORT_SKIN_MISSING_BONE",
                                reachError,
                                patchId: solved.SourceId,
                                featureTypeId: SkeletonDocument.SectionTypeId));
                        HasErrors = true;
                    }
                }
            }

            var originalNodeMap = BuildNodeMap(originalReaderScene);
            var postLiveStateMemo = new Dictionary<string, FbxNodeTransformEvaluator.FbxHierarchyNodeState>(StringComparer.Ordinal);

            for (int i = 0; i < AlignmentResult.OrderedSolvedPaths.Count; i++)
            {
                string path = AlignmentResult.OrderedSolvedPaths[i];
                var solved = AlignmentResult.SolvedNodesByPath[path];
                string parentPath = solved.ParentPath;
                bool hasParent = !string.IsNullOrEmpty(parentPath);

                var parentLiveState = hasParent
                    ? EvaluatePostEditLiveState(parentPath, originalNodeMap, postLiveStateMemo, new HashSet<string>(StringComparer.Ordinal))
                    : FbxNodeTransformEvaluator.FbxHierarchyNodeState.Identity;

                bool targetIsUnposed = true;
                if (!solved.IsCreated &&
                    TargetRestFrame != null &&
                    TargetRestFrame.TryGetNode(path, out var preRestNode) &&
                    preRestNode != null)
                {
                    targetIsUnposed = preRestNode.IsRestConsistent;
                }

                if (!AlignmentResult.TryGetPostEditWriteGlobal(path, out var desiredWriteGlobal))
                {
                    desiredWriteGlobal = solved.Global;
                }
                SkeletonNodeKind solvedKind = AlignmentResult.GetNodeKind(path);

                // Evaluate every contributing patch candidate against the settled parent FBX hierarchy state
                // so incompatible inherit modes (e.g. RrSs vs RSrs) fail deterministically regardless of patch order.
                if (!entriesByPatchAndPath.TryGetValue(path, out var patchCandidates))
                {
                    continue;
                }

                var evaluatedCandidates = new List<PreparedFbxSkeletonNode>(patchCandidates.Count);
                for (int c = 0; c < patchCandidates.Count; c++)
                {
                    var (candPatchId, candSpace, candRec) = patchCandidates[c];
                    if (TryBuildPreparedFbxNode(
                            candPatchId,
                            candSpace,
                            candRec,
                            solved,
                            solvedKind,
                            desiredWriteGlobal,
                            hasParent,
                            parentLiveState,
                            originalNodeMap,
                            targetIsUnposed,
                            unitMeters,
                            numericToleranceMeters,
                            out var preparedCand,
                            out string channelError))
                    {
                        evaluatedCandidates.Add(preparedCand);
                    }
                    else
                    {
                        ReportOnce(
                            diagnostics,
                            $"chan|{path}|{candPatchId}",
                            new FbxExportDiagnostic(
                                FbxExportDiagnosticSeverity.Error,
                                "FBX_EXPORT_SKELETON_INVALID_MATRIX",
                                channelError,
                                patchId: candPatchId,
                                featureTypeId: SkeletonDocument.SectionTypeId));
                        HasErrors = true;
                    }
                }

                if (evaluatedCandidates.Count == 0)
                {
                    continue;
                }

                (string FirstPatchId, string SecondPatchId)? inheritConflict = null;
                for (int a = 0; a < evaluatedCandidates.Count; a++)
                {
                    for (int b = a + 1; b < evaluatedCandidates.Count; b++)
                    {
                        var left = evaluatedCandidates[a];
                        var right = evaluatedCandidates[b];
                        bool mismatch =
                            left.EffectiveInheritMode != right.EffectiveInheritMode ||
                            !MatricesMatch(left.ExpectedGlobalMatrix, right.ExpectedGlobalMatrix, unitMeters, numericToleranceMeters) ||
                            !MatricesMatch(left.HierarchyState.UnscaledNodeToWorld, right.HierarchyState.UnscaledNodeToWorld, unitMeters, numericToleranceMeters) ||
                            !((left.HierarchyState.InheritScale - right.HierarchyState.InheritScale).magnitude <= k_LinearMatrixCompareTolerance) ||
                            !NullableScaleMatches(left.HierarchyState.InheritedScaleNodeScale, right.HierarchyState.InheritedScaleNodeScale, k_LinearMatrixCompareTolerance);
                        if (!mismatch && left.VerbatimRaw != null && right.VerbatimRaw != null)
                        {
                            double transTolUnits = Math.Max(1e-5d, numericToleranceMeters / unitMeters);
                            mismatch = !left.VerbatimRaw.ApproximatelyEquals(right.VerbatimRaw, k_LinearMatrixCompareTolerance, transTolUnits);
                        }

                        if (mismatch)
                        {
                            string pA = left.PatchId;
                            string pB = right.PatchId;
                            if (StringComparer.Ordinal.Compare(pA, pB) > 0)
                            {
                                (pA, pB) = (pB, pA);
                            }

                            if (!inheritConflict.HasValue ||
                                StringComparer.Ordinal.Compare(pA, inheritConflict.Value.FirstPatchId) < 0 ||
                                (string.Equals(pA, inheritConflict.Value.FirstPatchId, StringComparison.Ordinal) &&
                                 StringComparer.Ordinal.Compare(pB, inheritConflict.Value.SecondPatchId) < 0))
                            {
                                inheritConflict = (pA, pB);
                            }
                        }
                    }
                }

                if (inheritConflict.HasValue)
                {
                    ReportOnce(
                        diagnostics,
                        $"conflict|{path}|{inheritConflict.Value.FirstPatchId}|{inheritConflict.Value.SecondPatchId}",
                        new FbxExportDiagnostic(
                            FbxExportDiagnosticSeverity.Error,
                            "FBX_EXPORT_SKELETON_CONFLICT",
                            $"Bone '{path}' has conflicting FBX hierarchy transforms between patch '{inheritConflict.Value.FirstPatchId}' and patch '{inheritConflict.Value.SecondPatchId}'.",
                            patchId: inheritConflict.Value.SecondPatchId,
                            featureTypeId: SkeletonDocument.SectionTypeId));
                    HasErrors = true;
                }

                var chosen = SelectDeterministicPreparedNode(evaluatedCandidates);
                m_PreparedByPath[path] = chosen;
                m_OrderedPreparedPaths.Add(path);
                postLiveStateMemo[path] = chosen.HierarchyState;
            }

            IsSolved = !HasErrors;
            return !HasErrors;
        }

        public bool CanCreateNodeHierarchy(FbxNode rootNode, string nodePath, out string error)
        {
            error = null;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            string current = FbxNameUtility.NormalizePath(nodePath);

            while (!string.IsNullOrEmpty(current))
            {
                if (rootNode != null && FbxSdkNodeUtility.FindNodeByPath(rootNode, current) != null)
                {
                    return true;
                }

                if (!visited.Add(current))
                {
                    error = $"Skeleton node hierarchy contains a cycle at '{current}'.";
                    return false;
                }

                if (AlignmentResult == null || !AlignmentResult.SolvedNodesByPath.ContainsKey(current))
                {
                    error = $"Missing target FBX bone '{current}' and no SkeletonNodeRecord was found to create it.";
                    return false;
                }

                current = FbxNameUtility.GetParentPath(current);
            }

            return true;
        }

        public bool Write(
            FbxManager manager,
            FbxNode rootNode,
            List<FbxExportDiagnostic> diagnostics,
            out IReadOnlyCollection<string> modifiedPaths,
            out IReadOnlyCollection<string> appliedPatchIds)
        {
            var modified = new HashSet<string>(StringComparer.Ordinal);
            var patches = new HashSet<string>(StringComparer.Ordinal);
            modifiedPaths = modified;
            appliedPatchIds = patches;

            if (AppliedToSdkScene || m_OrderedPreparedPaths.Count == 0)
            {
                return true;
            }

            if (manager == null || rootNode == null)
            {
                diagnostics?.Add(new FbxExportDiagnostic(
                    FbxExportDiagnosticSeverity.Error,
                    "FBX_EXPORT_SKIN_ARMATURE_APPLY_FAILED",
                    "Cannot apply FBX skeleton edits without a valid SDK manager and root node.",
                    featureTypeId: SkeletonDocument.SectionTypeId));
                return false;
            }

            for (int i = 0; i < m_OrderedPreparedPaths.Count; i++)
            {
                string path = m_OrderedPreparedPaths[i];
                var entry = m_PreparedByPath[path];
                var node = FbxSdkNodeUtility.FindNodeByPath(rootNode, path);
                if (node == null)
                {
                    string parentPath = FbxNameUtility.GetParentPath(path);
                    FbxNode parentNode = string.IsNullOrEmpty(parentPath)
                        ? rootNode
                        : FbxSdkNodeUtility.FindNodeByPath(rootNode, parentPath);
                    if (parentNode == null)
                    {
                        diagnostics?.Add(new FbxExportDiagnostic(
                            FbxExportDiagnosticSeverity.Error,
                            "FBX_EXPORT_SKIN_BONE_CREATE_FAILED",
                            $"Parent FBX node '{parentPath}' was not found while creating '{path}'.",
                            patchId: entry.PatchId,
                            featureTypeId: SkeletonDocument.SectionTypeId));
                        return false;
                    }

                    string leafName = FbxSdkNodeUtility.GetLeafName(path);
                    var created = FbxNode.Create(manager, leafName);
                    if (entry.Kind == SkeletonNodeKind.Null)
                    {
                        var nullAttr = FbxNull.Create(manager, leafName);
                        created.SetNodeAttribute(nullAttr);
                    }
                    else
                    {
                        var skelAttr = FbxSkeleton.Create(manager, leafName);
                        bool isRootSkeleton = parentNode == rootNode || parentNode.GetSkeleton() == null;
                        skelAttr.SetSkeletonType(isRootSkeleton
                            ? FbxSkeleton.EType.eRoot
                            : FbxSkeleton.EType.eLimbNode);
                        created.SetNodeAttribute(skelAttr);
                    }

                    // Attach to parentNode BEFORE setting transform properties so EvaluateGlobalTransform() sees parent hierarchy.
                    parentNode.AddChild(created);
                    ApplyPreparedNodeTransform(created, entry);
                }
                else
                {
                    ApplyPreparedNodeTransform(node, entry);
                }

                modified.Add(path);
                if (!string.IsNullOrEmpty(entry.PatchId))
                {
                    patches.Add(entry.PatchId);
                }
            }

            AppliedToSdkScene = true;
            return true;
        }

        public bool Verify(
            IFbxScene verifiedScene,
            IFbxScene originalReaderScene,
            bool verifyNumerics,
            double toleranceMeters,
            List<FbxExportDiagnostic> diagnostics)
        {
            if (VerifiedRoundTrip || m_OrderedPreparedPaths.Count == 0)
            {
                return true;
            }

            double unitMeters = originalReaderScene != null && originalReaderScene.Metadata.UnitMeters > 0d
                ? originalReaderScene.Metadata.UnitMeters
                : 1d;
            var originalNodeMap = BuildNodeMap(originalReaderScene);
            var verifiedNodeMap = BuildNodeMap(verifiedScene);

            for (int i = 0; i < m_OrderedPreparedPaths.Count; i++)
            {
                string path = m_OrderedPreparedPaths[i];
                var entry = m_PreparedByPath[path];

                if (!verifiedNodeMap.TryGetValue(path, out var verifiedNode) || verifiedNode == null)
                {
                    diagnostics?.Add(new FbxExportDiagnostic(
                        FbxExportDiagnosticSeverity.Error,
                        "FBX_EXPORT_VERIFY_BONE_NODE_MISSING",
                        $"Verified FBX scene is missing upserted skeleton node '{path}'.",
                        patchId: entry.PatchId,
                        featureTypeId: SkeletonDocument.SectionTypeId));
                    return false;
                }

                if (!originalNodeMap.ContainsKey(path))
                {
                    var expectedAttr = entry.Kind == SkeletonNodeKind.Null
                        ? FbxNodeAttributeKind.Empty
                        : FbxNodeAttributeKind.Bone;
                    if (verifiedNode.AttributeKind != expectedAttr)
                    {
                        diagnostics?.Add(new FbxExportDiagnostic(
                            FbxExportDiagnosticSeverity.Error,
                            "FBX_EXPORT_VERIFY_BONE_ATTRIBUTE_MISMATCH",
                            $"Created FBX node '{path}' expected attribute '{expectedAttr}' but reopened with '{verifiedNode.AttributeKind}'.",
                            patchId: entry.PatchId,
                            featureTypeId: SkeletonDocument.SectionTypeId));
                        return false;
                    }
                }

                if (verifyNumerics)
                {
                    if (!MatricesMatch(verifiedNode.NodeToParentMatrix, entry.ExpectedLocalMatrix, unitMeters, toleranceMeters) ||
                        !MatricesMatch(verifiedNode.NodeToWorldMatrix, entry.ExpectedGlobalMatrix, unitMeters, toleranceMeters))
                    {
                        diagnostics?.Add(new FbxExportDiagnostic(
                            FbxExportDiagnosticSeverity.Error,
                            "FBX_EXPORT_VERIFY_BONE_TRANSFORM_MISMATCH",
                            $"Skeleton node '{path}' evaluated local or global transform does not match expected matrix.",
                            patchId: entry.PatchId,
                            featureTypeId: SkeletonDocument.SectionTypeId));
                        return false;
                    }
                }
            }

            VerifiedRoundTrip = true;
            return true;
        }

        private static void ApplyPreparedNodeTransform(FbxNode node, PreparedFbxSkeletonNode entry)
        {
            if (entry.VerbatimRaw != null)
            {
                FbxSdkNodeUtility.ApplyRawTransformPropertiesToNode(node, entry.VerbatimRaw);
            }
            else
            {
                FbxSdkNodeUtility.ApplySolvedRawTransformToNode(node, entry.SolvedRaw);
            }
        }

        private static bool TryBuildPreparedFbxNode(
            string patchId,
            FbxExportSpace space,
            SkeletonNodeRecord candidateRecord,
            SolvedNodeTransform solvedNode,
            SkeletonNodeKind solvedKind,
            FbxMatrix4x4 desiredWriteGlobal,
            bool hasParent,
            in FbxNodeTransformEvaluator.FbxHierarchyNodeState parentLiveState,
            IReadOnlyDictionary<string, IFbxNode> originalNodeMap,
            bool targetIsUnposed,
            double unitMeters,
            double toleranceMeters,
            out PreparedFbxSkeletonNode prepared,
            out string error)
        {
            prepared = null;
            error = null;
            string path = solvedNode.Path;

            // Fast-path: exact native space, unposed Target node, and authored SkeletonRawTransform evaluates cleanly to desiredWriteGlobal.
            if (space != null &&
                space.IsSceneExactIdentity &&
                targetIsUnposed &&
                candidateRecord?.Raw != null)
            {
                var rawStack = ToFbxNodeRawTransform(candidateRecord.Raw, hasParent);
                FbxMatrix4x4 rawLocal = FbxNodeTransformEvaluator.EvaluateLocalMatrix(rawStack);
                if (FbxNodeTransformEvaluator.TryEvaluateHierarchyState(
                        rawLocal,
                        rawStack.LclScaling,
                        rawStack.InheritMode,
                        rawStack.OriginalInheritMode,
                        hasParent,
                        parentLiveState,
                        out var rawHierarchyState) &&
                    MatricesMatch(rawHierarchyState.NodeToWorld, desiredWriteGlobal, unitMeters, toleranceMeters))
                {
                    prepared = new PreparedFbxSkeletonNode(
                        patchId,
                        path,
                        solvedKind,
                        solvedNode.IsCreated,
                        rawStack,
                        candidateRecord.Raw,
                        rawLocal,
                        rawHierarchyState.NodeToWorld,
                        rawHierarchyState);
                    return true;
                }
            }

            // Preserve existing Target FBX PreRotation/PostRotation/RotationOrder/Pivots/InheritMode when updating an existing node,
            // or create a clean RSrs XYZ stack for newly created nodes.
            FbxNodeRawTransform preservedStack;
            if (!solvedNode.IsCreated &&
                originalNodeMap != null &&
                originalNodeMap.TryGetValue(path, out var existingNode) &&
                existingNode != null &&
                !existingNode.RawTransform.LclScaling.IsZero())
            {
                preservedStack = existingNode.RawTransform;
            }
            else
            {
                preservedStack = new FbxNodeRawTransform(
                    Vector3d.zero,
                    Vector3d.zero,
                    Vector3d.one,
                    FbxRotationOrder.XYZ,
                    FbxTransformInheritMode.RSrs,
                    FbxTransformInheritMode.RSrs,
                    rotationActive: false,
                    Vector3d.zero,
                    Vector3d.zero,
                    Vector3d.zero,
                    Vector3d.zero,
                    Vector3d.zero,
                    Vector3d.zero);
            }

            if (!FbxNodeTransformEvaluator.TrySolveNodeChannels(
                    desiredWriteGlobal,
                    hasParent,
                    parentLiveState,
                    preservedStack,
                    out var solvedRaw,
                    out var solvedLocalMatrix,
                    out var solvedHierarchyState,
                    out error,
                    path))
            {
                return false;
            }

            prepared = new PreparedFbxSkeletonNode(
                patchId,
                path,
                solvedKind,
                solvedNode.IsCreated,
                solvedRaw,
                verbatimRaw: null,
                solvedLocalMatrix,
                solvedHierarchyState.NodeToWorld,
                solvedHierarchyState);
            return true;
        }

        private FbxNodeTransformEvaluator.FbxHierarchyNodeState EvaluatePostEditLiveState(
            string path,
            IReadOnlyDictionary<string, IFbxNode> originalNodeMap,
            Dictionary<string, FbxNodeTransformEvaluator.FbxHierarchyNodeState> memo,
            HashSet<string> visited)
        {
            if (string.IsNullOrEmpty(path))
            {
                return FbxNodeTransformEvaluator.FbxHierarchyNodeState.Identity;
            }

            if (memo.TryGetValue(path, out var cached))
            {
                return cached;
            }

            if (m_PreparedByPath.TryGetValue(path, out var prepared))
            {
                memo[path] = prepared.HierarchyState;
                return prepared.HierarchyState;
            }

            if (!visited.Add(path))
            {
                return FbxNodeTransformEvaluator.FbxHierarchyNodeState.Identity;
            }

            string parentPath = FbxNameUtility.GetParentPath(path);
            bool hasParent = !string.IsNullOrEmpty(parentPath);
            var parentState = hasParent
                ? EvaluatePostEditLiveState(parentPath, originalNodeMap, memo, visited)
                : FbxNodeTransformEvaluator.FbxHierarchyNodeState.Identity;

            if (originalNodeMap != null && originalNodeMap.TryGetValue(path, out var origNode) && origNode != null)
            {
                var origRaw = origNode.RawTransform;
                Vector3d localScale = !origRaw.LclScaling.IsZero() ? origRaw.LclScaling : origNode.LocalTransform.Scale;
                if (localScale.IsZero())
                {
                    localScale = Vector3d.one;
                }

                bool anyAncestorEdited = HasEditedAncestor(parentPath);
                if (!anyAncestorEdited &&
                    FbxNodeTransformEvaluator.TryEvaluateHierarchyState(
                        origNode.NodeToParentMatrix,
                        localScale,
                        origNode.RawTransform.InheritMode,
                        origNode.RawTransform.OriginalInheritMode,
                        hasParent,
                        parentState,
                        out var exactState))
                {
                    // Keep ufbx's exact NodeToWorldMatrix when no ancestor was edited.
                    var uneditedState = new FbxNodeTransformEvaluator.FbxHierarchyNodeState(
                        origNode.NodeToWorldMatrix,
                        exactState.UnscaledNodeToWorld,
                        exactState.InheritScale,
                        exactState.InheritedScaleNodeScale);
                    memo[path] = uneditedState;
                    return uneditedState;
                }

                if (FbxNodeTransformEvaluator.TryEvaluateHierarchyState(
                        origNode.NodeToParentMatrix,
                        localScale,
                        origNode.RawTransform.InheritMode,
                        origNode.RawTransform.OriginalInheritMode,
                        hasParent,
                        parentState,
                        out var propagatedState))
                {
                    memo[path] = propagatedState;
                    return propagatedState;
                }
            }

            memo[path] = FbxNodeTransformEvaluator.FbxHierarchyNodeState.Identity;
            return FbxNodeTransformEvaluator.FbxHierarchyNodeState.Identity;
        }

        private bool HasEditedAncestor(string ancestorPath)
        {
            string curr = FbxNameUtility.NormalizePath(ancestorPath);
            while (!string.IsNullOrEmpty(curr))
            {
                if (m_PreparedByPath.ContainsKey(curr))
                {
                    return true;
                }

                curr = FbxNameUtility.GetParentPath(curr);
            }

            return false;
        }

        private static FbxNodeRawTransform ToFbxNodeRawTransform(SkeletonRawTransform raw, bool hasParent)
        {
            FbxTransformInheritMode effectiveInherit = hasParent
                ? raw.InheritMode
                : FbxTransformInheritMode.RSrs;
            return new FbxNodeRawTransform(
                raw.LclTranslation,
                raw.LclRotation,
                raw.LclScaling,
                raw.RotationOrder,
                effectiveInherit,
                raw.InheritMode,
                raw.RotationActive,
                raw.PreRotation,
                raw.PostRotation,
                raw.RotationPivot,
                raw.ScalingPivot,
                raw.RotationOffset,
                raw.ScalingOffset);
        }

        private static PreparedFbxSkeletonNode SelectDeterministicPreparedNode(List<PreparedFbxSkeletonNode> candidates)
        {
            if (candidates.Count == 1)
            {
                return candidates[0];
            }

            bool anyVerbatim = candidates.Any(c => c.VerbatimRaw != null);
            var pool = anyVerbatim
                ? candidates.Where(c => c.VerbatimRaw != null).ToList()
                : candidates;
            if (pool.Count == 1)
            {
                return pool[0];
            }

            pool.Sort((a, b) =>
            {
                int cmp = CompareMatrixLexicographic(a.ExpectedGlobalMatrix, b.ExpectedGlobalMatrix);
                if (cmp != 0) return cmp;
                cmp = CompareMatrixLexicographic(a.ExpectedLocalMatrix, b.ExpectedLocalMatrix);
                return cmp != 0 ? cmp : StringComparer.Ordinal.Compare(a.PatchId, b.PatchId);
            });
            return pool[0];
        }

        private static int CompareMatrixLexicographic(in FbxMatrix4x4 a, in FbxMatrix4x4 b)
        {
            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++)
                {
                    double va = a[r, c] == 0d ? 0d : a[r, c];
                    double vb = b[r, c] == 0d ? 0d : b[r, c];
                    int cmp = va.CompareTo(vb);
                    if (cmp != 0) return cmp;
                }
            }

            return 0;
        }

        internal static Dictionary<string, IFbxNode> BuildNodeMap(IFbxScene scene)
        {
            var map = new Dictionary<string, IFbxNode>(StringComparer.Ordinal);
            if (scene?.Nodes == null)
            {
                return map;
            }

            for (int i = 0; i < scene.Nodes.Count; i++)
            {
                var n = scene.Nodes[i];
                if (n == null || n.IsRoot)
                {
                    continue;
                }

                string norm = FbxNameUtility.NormalizePath(n.Path);
                if (!string.IsNullOrEmpty(norm) && !map.ContainsKey(norm))
                {
                    map.Add(norm, n);
                }
            }

            return map;
        }

        internal static bool MatricesMatch(in FbxMatrix4x4 actual, in FbxMatrix4x4 expected, double unitMeters, double toleranceMeters)
        {
            double safeUnit = unitMeters > 0d ? unitMeters : 1d;
            double transTol = Math.Max(1e-5d, toleranceMeters / safeUnit);
            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++)
                {
                    double tol = (r == 3 && c < 3) ? transTol : k_LinearMatrixCompareTolerance;
                    if (!(Math.Abs(actual[r, c] - expected[r, c]) <= tol))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool NullableScaleMatches(Vector3d? a, Vector3d? b, double tolerance)
        {
            if (!a.HasValue && !b.HasValue)
            {
                return true;
            }

            if (a.HasValue != b.HasValue)
            {
                return false;
            }

            return (a.Value - b.Value).magnitude <= tolerance;
        }

        private void ReportOnce(List<FbxExportDiagnostic> diagnostics, string key, FbxExportDiagnostic diagnostic)
        {
            if (m_ReportedDiagnosticKeys.Add(key))
            {
                diagnostics?.Add(diagnostic);
            }
        }

        private sealed class SkeletonReferenceComparer : IEqualityComparer<SkeletonDocument>
        {
            public static readonly SkeletonReferenceComparer Instance = new SkeletonReferenceComparer();
            public bool Equals(SkeletonDocument x, SkeletonDocument y) => ReferenceEquals(x, y);
            public int GetHashCode(SkeletonDocument obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }
}
