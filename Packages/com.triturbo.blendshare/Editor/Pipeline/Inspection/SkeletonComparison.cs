using System;
using System.Collections.Generic;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Model.Documents;
using Triturbo.BlendShare.Pipeline.Skeleton;

namespace Triturbo.BlendShare.Pipeline.Inspection
{
    /// <summary>
    /// Rest-frame comparison status for a scene/skeleton node between Origin and Source FBXs.
    /// </summary>
    public enum SkeletonNodeComparisonStatus : byte
    {
        Same = 0,
        Changed = 1,
        BasisOnly = 2,
        New = 3,
        MissingSource = 4,
        Unavailable = 5
    }

    /// <summary>
    /// Per-node rest-frame comparison record computed top-down in Origin FBX native space.
    /// </summary>
    public sealed class SkeletonNodeComparisonEntry
    {
        public string Path { get; }
        public string ParentPath { get; }
        public SkeletonNodeKind Kind { get; }
        public bool IsBone => Kind == SkeletonNodeKind.Bone;
        public bool SourceExists { get; }
        public bool OriginExists { get; }
        public bool RequiresCreate => SourceExists && !OriginExists;
        public bool IsEndNode { get; }
        public SkeletonNodeComparisonStatus Status { get; }
        public FbxMatrix4x4 SourceRestGlobalInStoredSpace { get; }
        public FbxMatrix4x4 OriginRestGlobal { get; }
        public FbxMatrix4x4 ExpectedRestGlobal { get; }
        public FbxMatrix4x4 BasisRotation { get; }

        public SkeletonNodeComparisonEntry(
            string path,
            string parentPath,
            SkeletonNodeKind kind,
            bool sourceExists,
            bool originExists,
            bool isEndNode,
            SkeletonNodeComparisonStatus status,
            FbxMatrix4x4 sourceRestGlobalInStoredSpace,
            FbxMatrix4x4 originRestGlobal,
            FbxMatrix4x4 expectedRestGlobal,
            FbxMatrix4x4 basisRotation)
        {
            Path = path ?? string.Empty;
            ParentPath = parentPath ?? string.Empty;
            Kind = kind;
            SourceExists = sourceExists;
            OriginExists = originExists;
            IsEndNode = isEndNode;
            Status = status;
            SourceRestGlobalInStoredSpace = sourceRestGlobalInStoredSpace;
            OriginRestGlobal = originRestGlobal;
            ExpectedRestGlobal = expectedRestGlobal;
            BasisRotation = basisRotation;
        }
    }

    /// <summary>
    /// Compares Origin and Source FBX skeleton rest frames (reconstructed from skin bindposes via
    /// <see cref="FbxRestFrameSnapshot"/>) top-down in Origin stored space, classifying pure DCC bone rolls
    /// (<c>BasisOnly</c>) and unskinned <c>&lt;bone&gt;_end</c> leaf helpers (<c>IsEndNode</c>).
    /// </summary>
    public sealed class SkeletonComparison
    {
        private const double k_LinearMatrixTolerance = 1e-4d;
        private const double k_OrthoCheckTolerance = 1e-3d;
        private const double k_TranslationToleranceMeters = 1e-4d;

        private readonly Dictionary<string, SkeletonNodeComparisonEntry> m_EntriesByPath;
        private readonly List<SkeletonNodeComparisonEntry> m_OrderedEntries;

        public SkeletonRestFrame OriginRestFrame { get; }
        public SkeletonRestFrame SourceRestFrame { get; }
        public IReadOnlyList<SkeletonNodeComparisonEntry> Entries => m_OrderedEntries;
        public IReadOnlyDictionary<string, SkeletonNodeComparisonEntry> EntriesByPath => m_EntriesByPath;

        private SkeletonComparison(
            SkeletonRestFrame originRestFrame,
            SkeletonRestFrame sourceRestFrame,
            Dictionary<string, SkeletonNodeComparisonEntry> entriesByPath,
            List<SkeletonNodeComparisonEntry> orderedEntries)
        {
            OriginRestFrame = originRestFrame;
            SourceRestFrame = sourceRestFrame;
            m_EntriesByPath = entriesByPath ?? new Dictionary<string, SkeletonNodeComparisonEntry>(StringComparer.Ordinal);
            m_OrderedEntries = orderedEntries ?? new List<SkeletonNodeComparisonEntry>();
        }

        public static SkeletonComparison Empty { get; } = new SkeletonComparison(
            null,
            null,
            new Dictionary<string, SkeletonNodeComparisonEntry>(StringComparer.Ordinal),
            new List<SkeletonNodeComparisonEntry>());

        public bool TryGetEntry(string path, out SkeletonNodeComparisonEntry entry)
        {
            string norm = FbxNameUtility.NormalizePath(path);
            if (string.IsNullOrEmpty(norm))
            {
                entry = null;
                return false;
            }

            return m_EntriesByPath.TryGetValue(norm, out entry);
        }

        /// <summary>
        /// Builds a top-down <see cref="SkeletonComparison"/> from <paramref name="sourceScene"/> and <paramref name="originScene"/>.
        /// </summary>
        public static SkeletonComparison Build(
            IFbxScene sourceScene,
            IFbxScene originScene,
            FbxAffineSpaceTransform sourceWorldToOriginRaw,
            double originUnitMeters,
            IReadOnlyCollection<string> priorityMeshPaths = null)
        {
            var originRest = FbxRestFrameSnapshot.Capture(originScene, priorityMeshPaths, k_TranslationToleranceMeters);
            var sourceRest = FbxRestFrameSnapshot.Capture(sourceScene, priorityMeshPaths, k_TranslationToleranceMeters);
            return BuildFromRestFrames(
                sourceScene,
                originScene,
                sourceRest,
                originRest,
                sourceWorldToOriginRaw,
                originUnitMeters);
        }

        /// <summary>
        /// Builds a top-down <see cref="SkeletonComparison"/> from pre-captured rest frames.
        /// </summary>
        public static SkeletonComparison BuildFromRestFrames(
            IFbxScene sourceScene,
            IFbxScene originScene,
            SkeletonRestFrame sourceRest,
            SkeletonRestFrame originRest,
            FbxAffineSpaceTransform sourceWorldToOriginRaw,
            double originUnitMeters)
        {
            double safeUnit = CanonicalMeshSpace.IsValidUnitMeters(originUnitMeters) ? originUnitMeters : 1d;
            double transTolUnits = Math.Max(1e-6d, k_TranslationToleranceMeters / safeUnit);

            var referencedClusterPaths = new HashSet<string>(StringComparer.Ordinal);
            CollectReferencedClusterBones(originScene, referencedClusterPaths);
            CollectReferencedClusterBones(sourceScene, referencedClusterPaths);

            var sourceNodesByPath = CollectSceneNodesByPath(sourceScene);
            var originNodesByPath = CollectSceneNodesByPath(originScene);

            var candidatePaths = new HashSet<string>(StringComparer.Ordinal);
            if (sourceRest != null)
            {
                foreach (var kv in sourceRest.NodesByPath)
                {
                    if (kv.Value.IsBone || referencedClusterPaths.Contains(kv.Key))
                    {
                        AddPathAndAncestors(kv.Key, candidatePaths, sourceRest, originRest);
                    }
                }
            }

            if (originRest != null)
            {
                foreach (var kv in originRest.NodesByPath)
                {
                    if (kv.Value.IsBone || referencedClusterPaths.Contains(kv.Key))
                    {
                        AddPathAndAncestors(kv.Key, candidatePaths, sourceRest, originRest);
                    }
                }
            }

            foreach (string clusterBone in referencedClusterPaths)
            {
                AddPathAndAncestors(clusterBone, candidatePaths, sourceRest, originRest);
            }

            var orderedPaths = new List<string>(candidatePaths);
            orderedPaths.Sort(FbxNameUtility.CompareHierarchyOrder);

            var entriesByPath = new Dictionary<string, SkeletonNodeComparisonEntry>(orderedPaths.Count, StringComparer.Ordinal);
            var orderedEntries = new List<SkeletonNodeComparisonEntry>(orderedPaths.Count);
            var postGlobalByPath = new Dictionary<string, FbxMatrix4x4>(orderedPaths.Count, StringComparer.Ordinal);

            for (int i = 0; i < orderedPaths.Count; i++)
            {
                string path = orderedPaths[i];
                string parentPath = FbxNameUtility.GetParentPath(path);

                SkeletonRestNode srcNode = null;
                SkeletonRestNode orgNode = null;
                bool hasSource = sourceRest != null && sourceRest.TryGetNode(path, out srcNode) && srcNode != null;
                bool hasOrigin = originRest != null && originRest.TryGetNode(path, out orgNode) && orgNode != null;

                sourceNodesByPath.TryGetValue(path, out var rawSourceFbxNode);
                bool isBone = (hasSource && srcNode.IsBone) ||
                              (!hasSource && hasOrigin && orgNode.IsBone) ||
                              referencedClusterPaths.Contains(path);
                SkeletonNodeKind kind = isBone ? SkeletonNodeKind.Bone : SkeletonNodeKind.Null;

                bool isEndNode = IsLeafEndBone(path, parentPath, isBone, rawSourceFbxNode, sourceRest, referencedClusterPaths);

                FbxMatrix4x4 sGlobal = FbxMatrix4x4.Identity;
                if (hasSource)
                {
                    sGlobal = !sourceWorldToOriginRaw.IsValid || sourceWorldToOriginRaw.IsIdentity
                        ? srcNode.RestGlobal
                        : sourceWorldToOriginRaw.InverseOffset * srcNode.RestGlobal * sourceWorldToOriginRaw.Offset;
                }

                FbxMatrix4x4 oGlobal = hasOrigin ? orgNode.RestGlobal : FbxMatrix4x4.Identity;
                FbxMatrix4x4 parentPost = !string.IsNullOrEmpty(parentPath) && postGlobalByPath.TryGetValue(parentPath, out var pp)
                    ? pp
                    : FbxMatrix4x4.Identity;

                SkeletonNodeComparisonStatus status;
                FbxMatrix4x4 expectedGlobal = oGlobal;
                FbxMatrix4x4 basisRotation = FbxMatrix4x4.Identity;

                if (hasSource && !hasOrigin)
                {
                    status = SkeletonNodeComparisonStatus.New;
                    expectedGlobal = sGlobal;
                    postGlobalByPath[path] = isEndNode ? parentPost : sGlobal;
                }
                else if (!hasSource && hasOrigin)
                {
                    status = SkeletonNodeComparisonStatus.MissingSource;
                    expectedGlobal = !string.IsNullOrEmpty(parentPath) ? (orgNode.RestLocal * parentPost) : oGlobal;
                    postGlobalByPath[path] = expectedGlobal;
                }
                else if (!hasSource && !hasOrigin)
                {
                    status = SkeletonNodeComparisonStatus.Unavailable;
                    postGlobalByPath[path] = parentPost;
                }
                else
                {
                    expectedGlobal = !string.IsNullOrEmpty(parentPath)
                        ? (orgNode.RestLocal * parentPost)
                        : oGlobal;

                    if (MatricesApproximatelyEqual(sGlobal, expectedGlobal, k_LinearMatrixTolerance, transTolUnits))
                    {
                        status = SkeletonNodeComparisonStatus.Same;
                        postGlobalByPath[path] = expectedGlobal;
                    }
                    else if (kind == SkeletonNodeKind.Bone &&
                             TryCheckPureProperRotationAtSameHead(sGlobal, expectedGlobal, transTolUnits, out basisRotation))
                    {
                        // Pure DCC bone roll (e.g. Maya PreRotation vs Blender axis roll) keeps head coincident:
                        // do NOT auto-select or propagate into Post(n), so child head checks remain relative to Expected(n).
                        status = SkeletonNodeComparisonStatus.BasisOnly;
                        postGlobalByPath[path] = expectedGlobal;
                    }
                    else
                    {
                        status = SkeletonNodeComparisonStatus.Changed;
                        postGlobalByPath[path] = isEndNode ? expectedGlobal : sGlobal;
                    }
                }

                var entry = new SkeletonNodeComparisonEntry(
                    path,
                    parentPath,
                    kind,
                    hasSource,
                    hasOrigin,
                    isEndNode,
                    status,
                    sGlobal,
                    oGlobal,
                    expectedGlobal,
                    basisRotation);
                entriesByPath[path] = entry;
                orderedEntries.Add(entry);
            }

            return new SkeletonComparison(originRest, sourceRest, entriesByPath, orderedEntries);
        }

        /// <summary>
        /// Expands an explicit upsert set with <c>&lt;bone&gt;_end</c> leaf helpers whose parents are in the upsert set.
        /// </summary>
        public void ExpandEndNodesFollowingParents(ISet<string> upsertPaths)
        {
            if (upsertPaths == null || upsertPaths.Count == 0)
            {
                return;
            }

            for (int i = 0; i < m_OrderedEntries.Count; i++)
            {
                var entry = m_OrderedEntries[i];
                if (!entry.IsEndNode || !entry.SourceExists)
                {
                    continue;
                }

                if ((entry.Status == SkeletonNodeComparisonStatus.Changed ||
                     entry.Status == SkeletonNodeComparisonStatus.New ||
                     entry.Status == SkeletonNodeComparisonStatus.BasisOnly) &&
                    !string.IsNullOrEmpty(entry.ParentPath) &&
                    upsertPaths.Contains(entry.ParentPath))
                {
                    upsertPaths.Add(entry.Path);
                }
            }
        }

        private static bool TryCheckPureProperRotationAtSameHead(
            in FbxMatrix4x4 sourceGlobal,
            in FbxMatrix4x4 expectedGlobal,
            double translationTolUnits,
            out FbxMatrix4x4 rotationQ)
        {
            rotationQ = FbxMatrix4x4.Identity;
            if ((sourceGlobal.Translation - expectedGlobal.Translation).magnitude > translationTolUnits)
            {
                return false;
            }

            if (!FbxAffineSpaceTransform.TryCreate(expectedGlobal, out var expectedXform))
            {
                return false;
            }

            FbxMatrix4x4 q = FbxLinearAlgebra3d.ExtractLinear(sourceGlobal) *
                             FbxLinearAlgebra3d.ExtractLinear(expectedXform.InverseOffset);
            double det = FbxLinearAlgebra3d.Determinant3x3(q);
            if (double.IsNaN(det) || double.IsInfinity(det) || det <= 0d || Math.Abs(det - 1d) > k_OrthoCheckTolerance)
            {
                return false;
            }

            FbxMatrix4x4 qqt = q * q.Transpose();
            for (int r = 0; r < 3; r++)
            {
                for (int c = 0; c < 3; c++)
                {
                    double expected = r == c ? 1d : 0d;
                    if (Math.Abs(qqt[r, c] - expected) > k_OrthoCheckTolerance)
                    {
                        return false;
                    }
                }
            }

            rotationQ = q;
            return true;
        }

        private static bool IsLeafEndBone(
            string path,
            string parentPath,
            bool isBone,
            IFbxNode rawSourceNode,
            SkeletonRestFrame sourceRest,
            HashSet<string> referencedClusterPaths)
        {
            if (!isBone || string.IsNullOrEmpty(parentPath) || referencedClusterPaths.Contains(path))
            {
                return false;
            }

            string nodeName = FbxNameUtility.GetNodeName(path);
            string parentName = FbxNameUtility.GetNodeName(parentPath);
            if (string.IsNullOrEmpty(nodeName) ||
                string.IsNullOrEmpty(parentName) ||
                !string.Equals(nodeName, parentName + "_end", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (rawSourceNode != null)
            {
                return rawSourceNode.Children == null || rawSourceNode.Children.Count == 0;
            }

            // Fallback when IFbxNode.Children is unavailable in mock scenes: check no path has this node as ParentPath.
            if (sourceRest != null)
            {
                string prefix = path + "/";
                for (int i = 0; i < sourceRest.OrderedPaths.Count; i++)
                {
                    if (sourceRest.OrderedPaths[i].StartsWith(prefix, StringComparison.Ordinal))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static void AddPathAndAncestors(
            string path,
            HashSet<string> destination,
            SkeletonRestFrame sourceRest,
            SkeletonRestFrame originRest)
        {
            string curr = FbxNameUtility.NormalizePath(path);
            while (!string.IsNullOrEmpty(curr))
            {
                bool existsInEither = (sourceRest != null && sourceRest.TryGetNode(curr, out _)) ||
                                      (originRest != null && originRest.TryGetNode(curr, out _));
                if (existsInEither || curr == FbxNameUtility.NormalizePath(path))
                {
                    destination.Add(curr);
                }

                curr = FbxNameUtility.GetParentPath(curr);
            }
        }

        private static void CollectReferencedClusterBones(IFbxScene scene, HashSet<string> destination)
        {
            if (scene?.Meshes == null)
            {
                return;
            }

            for (int m = 0; m < scene.Meshes.Count; m++)
            {
                var skins = scene.Meshes[m]?.SkinDeformers;
                if (skins == null) continue;
                for (int s = 0; s < skins.Count; s++)
                {
                    var clusters = skins[s]?.Clusters;
                    if (clusters == null) continue;
                    for (int c = 0; c < clusters.Count; c++)
                    {
                        var cl = clusters[c];
                        if (cl == null) continue;
                        string bp = FbxNameUtility.NormalizePath(cl.BoneNode != null ? cl.BoneNode.Path : cl.BonePath);
                        if (!string.IsNullOrEmpty(bp))
                        {
                            destination.Add(bp);
                        }
                    }
                }
            }
        }

        private static Dictionary<string, IFbxNode> CollectSceneNodesByPath(IFbxScene scene)
        {
            var map = new Dictionary<string, IFbxNode>(StringComparer.Ordinal);
            if (scene == null)
            {
                return map;
            }

            if (scene.Nodes != null)
            {
                for (int i = 0; i < scene.Nodes.Count; i++)
                {
                    RegisterChain(map, scene.Nodes[i]);
                }
            }

            if (scene.Meshes != null)
            {
                for (int m = 0; m < scene.Meshes.Count; m++)
                {
                    var skins = scene.Meshes[m]?.SkinDeformers;
                    if (skins == null) continue;
                    for (int s = 0; s < skins.Count; s++)
                    {
                        var clusters = skins[s]?.Clusters;
                        if (clusters == null) continue;
                        for (int c = 0; c < clusters.Count; c++)
                        {
                            RegisterChain(map, clusters[c]?.BoneNode);
                        }
                    }
                }
            }

            return map;
        }

        private static void RegisterChain(Dictionary<string, IFbxNode> map, IFbxNode node)
        {
            var curr = node;
            while (curr != null && !curr.IsRoot)
            {
                string norm = FbxNameUtility.NormalizePath(curr.Path);
                if (!string.IsNullOrEmpty(norm) && !map.ContainsKey(norm))
                {
                    map.Add(norm, curr);
                }

                curr = curr.Parent;
            }
        }

        private static bool MatricesApproximatelyEqual(
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
