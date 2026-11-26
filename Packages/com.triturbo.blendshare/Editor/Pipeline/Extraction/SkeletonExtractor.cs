using System;
using System.Collections.Generic;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Model.Documents;
using Triturbo.BlendShare.Model.Features;
using Triturbo.BlendShare.Pipeline.Inspection;
using Triturbo.BlendShare.Pipeline.Skeleton;

namespace Triturbo.BlendShare.Pipeline.Extraction
{
    /// <summary>
    /// Optional patch-level skeleton extraction options for <see cref="PatchExtractionRequest.SkeletonOptions"/>.
    /// </summary>
    public sealed class SkeletonExtractionOptions
    {
        public bool Enabled { get; set; }
        public List<string> SelectedNodePaths { get; } = new List<string>();
    }

    /// <summary>
    /// Extracts the patch-level <see cref="SkeletonDocument"/> once per <see cref="PatchExtractionPipeline"/> run
    /// from bindpose-reconstructed Origin and Source <see cref="SkeletonRestFrame"/>s:
    /// <list type="bullet">
    ///   <item>Emits <see cref="SkeletonNodeRecord"/> (<c>RestGlobal = S_n</c>, <c>BaseRestGlobal = O_n</c>) for selected Upsert nodes, missing-in-Origin cluster bones, missing-in-Origin ancestors, and parent-following <c>&lt;bone&gt;_end</c> leaves.</item>
    ///   <item>Emits read-only <see cref="SkeletonAnchorRecord"/> (<c>RestGlobal = S_a</c>, always Source rest global) for untouched cluster bones and ancestor chains without duplicating paths in <see cref="SkeletonDocument.Nodes"/>.</item>
    /// </list>
    /// </summary>
    public static class SkeletonExtractor
    {
        public static bool TryExtract(
            IFbxScene sourceScene,
            IFbxScene originScene,
            FbxAffineSpaceTransform sourceWorldToStored,
            double originUnitMeters,
            IReadOnlyCollection<string> priorityMeshPaths,
            IReadOnlyList<MeshDocument> extractedMeshes,
            SkinWeightExtractionOptions skinWeightOptions,
            SkeletonExtractionOptions skeletonOptions,
            List<ExtractionDiagnostic> diagnostics,
            out SkeletonDocument skeletonDocument)
        {
            skeletonDocument = null;

            bool anySkinFeature = false;
            var newClusterBoneCandidates = new HashSet<string>(StringComparer.Ordinal);
            var explicitMatrixClusterBones = new HashSet<string>(StringComparer.Ordinal);
            var allExtractedClusterBones = new HashSet<string>(StringComparer.Ordinal);

            if (extractedMeshes != null)
            {
                for (int m = 0; m < extractedMeshes.Count; m++)
                {
                    var features = extractedMeshes[m]?.Features;
                    if (features == null) continue;
                    for (int f = 0; f < features.Count; f++)
                    {
                        if (features[f] is not SkinWeightFeature swFeature || swFeature.Clusters == null)
                        {
                            continue;
                        }

                        anySkinFeature = true;
                        for (int c = 0; c < swFeature.Clusters.Count; c++)
                        {
                            var cl = swFeature.Clusters[c];
                            string bp = FbxNameUtility.NormalizePath(cl?.BonePath ?? string.Empty);
                            if (string.IsNullOrEmpty(bp)) continue;

                            allExtractedClusterBones.Add(bp);
                            if (cl.HasClusterMatrices)
                            {
                                explicitMatrixClusterBones.Add(bp);
                            }
                        }
                    }
                }
            }

            bool swEnabledWithArmature = skinWeightOptions != null &&
                                         skinWeightOptions.Enabled &&
                                         (skinWeightOptions.SelectedTransformBonePaths.Count > 0 ||
                                          skinWeightOptions.SelectedNewBonePaths.Count > 0);
            bool skelOptsEnabled = skeletonOptions != null && skeletonOptions.Enabled;

            if (!anySkinFeature && !swEnabledWithArmature && !skelOptsEnabled)
            {
                return true;
            }

            var comparison = SkeletonComparison.Build(
                sourceScene,
                originScene,
                sourceWorldToStored,
                originUnitMeters,
                priorityMeshPaths);
            var sourceRest = comparison.SourceRestFrame;
            var originRest = comparison.OriginRestFrame;
            var sourceNodesByPath = CollectSceneNodesByPath(sourceScene);

            var upsertPaths = new HashSet<string>(StringComparer.Ordinal);

            if (skinWeightOptions != null && skinWeightOptions.Enabled)
            {
                for (int i = 0; i < skinWeightOptions.SelectedNewBonePaths.Count; i++)
                {
                    string norm = FbxNameUtility.NormalizePath(skinWeightOptions.SelectedNewBonePaths[i]);
                    if (!string.IsNullOrEmpty(norm)) upsertPaths.Add(norm);
                }

                for (int i = 0; i < skinWeightOptions.SelectedTransformBonePaths.Count; i++)
                {
                    string norm = FbxNameUtility.NormalizePath(skinWeightOptions.SelectedTransformBonePaths[i]);
                    if (!string.IsNullOrEmpty(norm)) upsertPaths.Add(norm);
                }
            }

            if (skelOptsEnabled)
            {
                if (skeletonOptions.SelectedNodePaths.Count > 0)
                {
                    for (int i = 0; i < skeletonOptions.SelectedNodePaths.Count; i++)
                    {
                        string norm = FbxNameUtility.NormalizePath(skeletonOptions.SelectedNodePaths[i]);
                        if (!string.IsNullOrEmpty(norm)) upsertPaths.Add(norm);
                    }
                }
                else
                {
                    // Default automatic selection when SkeletonOptions.Enabled is true without explicit list:
                    // include Changed and New non-end nodes.
                    for (int i = 0; i < comparison.Entries.Count; i++)
                    {
                        var entry = comparison.Entries[i];
                        if (!entry.IsEndNode &&
                            entry.SourceExists &&
                            (entry.Status == SkeletonNodeComparisonStatus.Changed ||
                             entry.Status == SkeletonNodeComparisonStatus.New))
                        {
                            upsertPaths.Add(entry.Path);
                        }
                    }
                }
            }

            // Automatically include genuinely new cluster bones absent from OriginScene.
            foreach (string bp in allExtractedClusterBones)
            {
                bool existsInOrigin = originRest != null && originRest.TryGetNode(bp, out _);
                bool existsInSource = sourceRest != null && sourceRest.TryGetNode(bp, out _);
                if (!existsInOrigin && existsInSource)
                {
                    upsertPaths.Add(bp);
                    newClusterBoneCandidates.Add(bp);
                }
            }

            // Expand <bone>_end leaf helpers whose parent is in upsertPaths.
            comparison.ExpandEndNodesFollowingParents(upsertPaths);

            if (upsertPaths.Count == 0 && explicitMatrixClusterBones.Count == 0)
            {
                return true;
            }

            // Validate all requested upsert paths exist in Source and pull in any missing-in-Origin ancestors that exist in Source.
            var initialUpserts = new List<string>(upsertPaths);
            initialUpserts.Sort(StringComparer.Ordinal);
            for (int i = 0; i < initialUpserts.Count; i++)
            {
                string path = initialUpserts[i];
                if (sourceRest == null || !sourceRest.TryGetNode(path, out _))
                {
                    diagnostics?.Add(new ExtractionDiagnostic(
                        ExtractionDiagnosticSeverity.Error,
                        "SW_EXT_MISSING_SOURCE_BONE",
                        $"Bone '{path}' was not found in the source FBX.",
                        featureTypeId: SkinWeightFeature.FeatureTypeId));
                    return false;
                }

                bool existsInOrigin = originRest != null && originRest.TryGetNode(path, out _);
                if (!existsInOrigin)
                {
                    string anc = FbxNameUtility.GetParentPath(path);
                    while (!string.IsNullOrEmpty(anc))
                    {
                        bool ancInOrigin = originRest != null && originRest.TryGetNode(anc, out _);
                        if (ancInOrigin)
                        {
                            break;
                        }

                        if (sourceRest.TryGetNode(anc, out _))
                        {
                            upsertPaths.Add(anc);
                        }

                        anc = FbxNameUtility.GetParentPath(anc);
                    }
                }
            }

            // Expand <bone>_end again in case missing-ancestor expansion added a parent.
            comparison.ExpandEndNodesFollowingParents(upsertPaths);

            var orderedUpsertPaths = new List<string>(upsertPaths);
            orderedUpsertPaths.Sort(FbxNameUtility.CompareHierarchyOrder);

            var doc = new SkeletonDocument();
            for (int i = 0; i < orderedUpsertPaths.Count; i++)
            {
                string path = orderedUpsertPaths[i];
                if (!sourceRest.TryGetNode(path, out var srcRestNode) || srcRestNode == null)
                {
                    continue;
                }

                string parentPath = FbxNameUtility.GetParentPath(path);
                FbxMatrix4x4 sGlobal = ToStoredSpace(srcRestNode.RestGlobal, sourceWorldToStored);
                FbxMatrix4x4 sParentGlobal = FbxMatrix4x4.Identity;
                if (!string.IsNullOrEmpty(parentPath) &&
                    sourceRest.TryGetNode(parentPath, out var parentSrcRestNode) &&
                    parentSrcRestNode != null)
                {
                    sParentGlobal = ToStoredSpace(parentSrcRestNode.RestGlobal, sourceWorldToStored);
                }

                FbxMatrix4x4 sLocal = FbxAffineSpaceTransform.TryCreate(sParentGlobal, out var sParentXform)
                    ? sGlobal * sParentXform.InverseOffset
                    : sGlobal;

                SkeletonRestNode origRestNode = null;
                bool hasBaseRest = originRest != null &&
                                   originRest.TryGetNode(path, out origRestNode) &&
                                   origRestNode != null;
                FbxMatrix4x4 baseRestGlobal = hasBaseRest
                    ? origRestNode.RestGlobal
                    : FbxMatrix4x4.Identity;

                SkeletonNodeKind kind = srcRestNode.IsBone
                    ? SkeletonNodeKind.Bone
                    : SkeletonNodeKind.Null;

                SkeletonRawTransform raw = null;
                if (sourceWorldToStored.IsIdentity &&
                    srcRestNode.IsRestConsistent &&
                    sourceNodesByPath.TryGetValue(path, out var rawFbxNode) &&
                    rawFbxNode != null &&
                    !rawFbxNode.RawTransform.LclScaling.IsZero())
                {
                    raw = SkeletonRawTransform.FromFbxRawTransform(
                        rawFbxNode.RawTransform,
                        rawFbxNode.NodeToParentMatrix);
                }

                doc.Nodes.Add(new SkeletonNodeRecord
                {
                    Path = path,
                    Kind = kind,
                    RestGlobal = sGlobal,
                    RestLocal = sLocal,
                    HasBaseRest = hasBaseRest,
                    BaseRestGlobal = baseRestGlobal,
                    Raw = raw
                });
            }

            // Collect read-only Source rest calibration anchors (strictly disjoint from Nodes).
            var anchorPaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (string bp in explicitMatrixClusterBones)
            {
                AddSourceAncestorsForAnchor(bp, includeSelf: true, upsertPaths, sourceRest, anchorPaths);
            }

            if (doc.Nodes.Count > 0)
            {
                foreach (string bp in allExtractedClusterBones)
                {
                    AddSourceAncestorsForAnchor(bp, includeSelf: true, upsertPaths, sourceRest, anchorPaths);
                }

                for (int i = 0; i < doc.Nodes.Count; i++)
                {
                    AddSourceAncestorsForAnchor(doc.Nodes[i].Path, includeSelf: false, upsertPaths, sourceRest, anchorPaths);
                }
            }

            var orderedAnchorPaths = new List<string>(anchorPaths);
            orderedAnchorPaths.Sort(FbxNameUtility.CompareHierarchyOrder);
            for (int i = 0; i < orderedAnchorPaths.Count; i++)
            {
                string ancPath = orderedAnchorPaths[i];
                if (sourceRest != null && sourceRest.TryGetNode(ancPath, out var ancRestNode) && ancRestNode != null)
                {
                    doc.Anchors.Add(new SkeletonAnchorRecord
                    {
                        Path = ancPath,
                        RestGlobal = ToStoredSpace(ancRestNode.RestGlobal, sourceWorldToStored)
                    });
                }
            }

            skeletonDocument = doc.HasContent ? doc : null;
            return true;
        }

        private static void AddSourceAncestorsForAnchor(
            string startPath,
            bool includeSelf,
            HashSet<string> upsertPaths,
            SkeletonRestFrame sourceRest,
            HashSet<string> anchorPaths)
        {
            if (sourceRest == null)
            {
                return;
            }

            string curr = includeSelf
                ? FbxNameUtility.NormalizePath(startPath)
                : FbxNameUtility.GetParentPath(startPath);

            while (!string.IsNullOrEmpty(curr))
            {
                if (!upsertPaths.Contains(curr) && sourceRest.TryGetNode(curr, out _))
                {
                    anchorPaths.Add(curr);
                }

                curr = FbxNameUtility.GetParentPath(curr);
            }
        }

        private static FbxMatrix4x4 ToStoredSpace(in FbxMatrix4x4 sourceGlobal, in FbxAffineSpaceTransform sourceWorldToStored)
        {
            if (!sourceWorldToStored.IsValid || sourceWorldToStored.IsIdentity)
            {
                return sourceGlobal;
            }

            return sourceWorldToStored.InverseOffset * sourceGlobal * sourceWorldToStored.Offset;
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
    }
}
