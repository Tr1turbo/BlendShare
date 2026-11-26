using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Model.Documents;
using Triturbo.BlendShare.Pipeline.Apply;
using UnityEngine;

namespace Triturbo.BlendShare.Pipeline.Skeleton
{
    /// <summary>
    /// Instantiates missing Unity <see cref="GameObject"/> bone chains and solves per-node Unity local TRS
    /// via <see cref="SkeletonAlignment"/>.
    /// </summary>
    public static class UnitySkeletonWriter
    {
        /// <summary>
        /// Materializes any missing <see cref="Transform"/> segments along <paramref name="sourceBonePath"/>
        /// under <paramref name="targetRoot"/> (or under an overridden ancestor in <paramref name="boneTransformOverrides"/>)
        /// using pre-solved records from <paramref name="scope"/> or solving on-demand from <paramref name="skeleton"/>.
        /// </summary>
        public static bool EnsureBoneHierarchy(
            Transform targetRoot,
            SkeletonDocument skeleton,
            string sourceBonePath,
            FbxMatrix4x4 storedToUnitySceneLocal,
            UnitySkeletonScope scope,
            out Transform createdTransform,
            out Transform topmostCreatedRoot,
            out string diagnostic,
            string patchId = null,
            IReadOnlyDictionary<BoneOverrideKey, Transform> boneTransformOverrides = null)
        {
            createdTransform = null;
            topmostCreatedRoot = null;
            diagnostic = "Missing root, bone path, or skeleton record.";

            string fullPath = FbxNameUtility.NormalizePath(sourceBonePath);
            if (!targetRoot || string.IsNullOrEmpty(fullPath))
            {
                return false;
            }

            if (TryGetOverrideTransform(boneTransformOverrides, patchId, fullPath, out var directOverride))
            {
                createdTransform = directOverride;
                diagnostic = null;
                return true;
            }

            var existingLeaf = targetRoot.Find(fullPath);
            if (existingLeaf != null)
            {
                createdTransform = existingLeaf;
                diagnostic = null;
                return true;
            }

            SkeletonAlignment onDemandSolved = null;
            var pending = new Stack<(string Name, Vector3 Position, Quaternion Rotation, Vector3 Scale)>();
            string current = fullPath;
            Transform parent = null;

            while (!string.IsNullOrEmpty(current))
            {
                if (TryGetOverrideTransform(boneTransformOverrides, patchId, current, out var overrideAncestor))
                {
                    parent = overrideAncestor;
                    break;
                }

                parent = targetRoot.Find(current);
                if (parent != null)
                {
                    break;
                }

                Vector3 pos;
                Quaternion rot;
                Vector3 scale;

                if (scope?.SolvedBonesByPath != null && scope.SolvedBonesByPath.TryGetValue(current, out var preSolved))
                {
                    pos = preSolved.Record.LocalPosition;
                    rot = preSolved.Record.LocalRotation;
                    scale = preSolved.Record.LocalScale;
                }
                else
                {
                    var nodeRecord = skeleton?.GetNode(current);
                    if (nodeRecord == null)
                    {
                        diagnostic = $"Missing skeleton bone record for '{current}'.";
                        return false;
                    }

                    if (onDemandSolved == null)
                    {
                        var restFrame = scope?.PreEditRestFrame ?? UnityRestFrameSnapshot.Capture(targetRoot);
                        HashSet<string> ignored = BuildIgnoredOverridePaths(skeleton, patchId, boneTransformOverrides);
                        onDemandSolved = SkeletonAlignment.Solve(
                            restFrame,
                            new[]
                            {
                                new SkeletonAlignmentPatchInput(
                                    patchId ?? string.Empty,
                                    skeleton,
                                    storedToUnitySceneLocal,
                                    ignoredNodePaths: ignored)
                            });
                        if (onDemandSolved.HasErrors)
                        {
                            diagnostic = onDemandSolved.Diagnostics.First(d => d.IsError).Message;
                            return false;
                        }
                    }

                    if (!onDemandSolved.SolvedNodesByPath.ContainsKey(current) ||
                        !SkeletonTransformMath.TryDecomposeUnityLocalTransform(
                            onDemandSolved.GetWriteLocal(current).ToUnityColumnMatrix(),
                            out pos,
                            out rot,
                            out scale,
                            out diagnostic))
                    {
                        diagnostic ??= $"Failed solving Unity local transform for '{current}'.";
                        return false;
                    }
                }

                string leafName = FbxNameUtility.GetNodeName(current);
                pending.Push((leafName, pos, rot, scale));
                current = FbxNameUtility.GetParentPath(current);
            }

            if (!parent)
            {
                parent = targetRoot;
            }

            while (pending.Count > 0)
            {
                var item = pending.Pop();
                var existingChild = parent.Find(item.Name);
                if (existingChild != null)
                {
                    parent = existingChild;
                    continue;
                }

                var child = new GameObject(item.Name).transform;
                child.SetParent(parent, false);
                child.localPosition = item.Position;
                child.localRotation = item.Rotation;
                child.localScale = item.Scale;
                if (topmostCreatedRoot == null)
                {
                    topmostCreatedRoot = child;
                }

                parent = child;
            }

            createdTransform = parent;
            diagnostic = null;
            return true;
        }

        internal static bool TryGetOverrideTransform(
            IReadOnlyDictionary<BoneOverrideKey, Transform> overrides,
            string patchId,
            string normalizedPath,
            out Transform target)
        {
            target = null;
            if (overrides == null || overrides.Count == 0 || string.IsNullOrEmpty(normalizedPath))
            {
                return false;
            }

            return overrides.TryGetValue(new BoneOverrideKey(patchId ?? string.Empty, normalizedPath), out target) &&
                   target != null;
        }

        internal static HashSet<string> BuildIgnoredOverridePaths(
            SkeletonDocument skeleton,
            string patchId,
            IReadOnlyDictionary<BoneOverrideKey, Transform> overrides)
        {
            if (skeleton?.Nodes == null || overrides == null || overrides.Count == 0)
            {
                return null;
            }

            HashSet<string> ignored = null;
            for (int i = 0; i < skeleton.Nodes.Count; i++)
            {
                string norm = FbxNameUtility.NormalizePath(skeleton.Nodes[i]?.Path ?? string.Empty);
                if (!string.IsNullOrEmpty(norm) && TryGetOverrideTransform(overrides, patchId, norm, out _))
                {
                    (ignored ??= new HashSet<string>(StringComparer.Ordinal)).Add(norm);
                }
            }

            return ignored;
        }

        /// <summary>
        /// Solves the calibrated Unity local TRS for a single <paramref name="node"/> in <paramref name="skeleton"/>
        /// against <paramref name="targetRoot"/> (used by authoring bone proxy setup and revert workflows).
        /// </summary>
        public static bool TrySolveUnityLocalTransform(
            SkeletonDocument skeleton,
            SkeletonNodeRecord node,
            FbxMatrix4x4 storedToUnitySceneLocal,
            Transform targetRoot,
            out Vector3 position,
            out Quaternion rotation,
            out Vector3 scale,
            out string diagnostic)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            scale = Vector3.one;
            diagnostic = null;

            if (node == null)
            {
                diagnostic = "Missing skeleton node record.";
                return false;
            }

            string path = FbxNameUtility.NormalizePath(node.Path);
            if (string.IsNullOrEmpty(path))
            {
                diagnostic = "Skeleton node record has an empty path.";
                return false;
            }

            var tempSkeleton = skeleton ?? new SkeletonDocument { Nodes = new List<SkeletonNodeRecord> { node } };
            var restFrame = UnityRestFrameSnapshot.Capture(targetRoot);
            var alignment = SkeletonAlignment.Solve(
                restFrame,
                new[]
                {
                    new SkeletonAlignmentPatchInput(
                        string.Empty,
                        tempSkeleton,
                        storedToUnitySceneLocal)
                });
            if (alignment.HasErrors)
            {
                diagnostic = alignment.Diagnostics.First(d => d.IsError).Message;
                return false;
            }

            if (!alignment.SolvedNodesByPath.ContainsKey(path))
            {
                diagnostic = $"Skeleton solver did not produce an entry for '{path}'.";
                return false;
            }

            return SkeletonTransformMath.TryDecomposeUnityLocalTransform(
                alignment.GetWriteLocal(path).ToUnityColumnMatrix(),
                out position,
                out rotation,
                out scale,
                out diagnostic);
        }
    }
}
