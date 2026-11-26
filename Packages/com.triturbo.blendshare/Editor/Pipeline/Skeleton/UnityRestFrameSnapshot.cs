using System;
using System.Collections.Generic;
using Triturbo.BlendShare.Fbx;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.Pipeline.Skeleton
{
    /// <summary>
    /// Skin-renderer bindpose seed passed into <see cref="UnityRestFrameSnapshot"/>.
    /// </summary>
    public readonly struct RendererRestSeed
    {
        public Transform RendererTransform { get; }
        public Transform[] Bones { get; }
        public Matrix4x4[] BindPoses { get; }
        public string[] SlotPaths { get; }

        public RendererRestSeed(
            Transform rendererTransform,
            Transform[] bones,
            Matrix4x4[] bindPoses,
            string[] slotPaths = null)
        {
            RendererTransform = rendererTransform;
            Bones = bones;
            BindPoses = bindPoses;
            SlotPaths = slotPaths;
        }
    }

    /// <summary>
    /// Captures a clean <see cref="SkeletonRestFrame"/> from a Unity <see cref="Transform"/> hierarchy,
    /// seeding bone rest transforms from <c>Mesh.bindposes</c> (priority tier 0 for targeted renderers,
    /// tier 2 for sibling <see cref="SkinnedMeshRenderer"/>s on the avatar) and propagating unskinned
    /// child transforms top-down from their parent's rest frame.
    /// </summary>
    public static class UnityRestFrameSnapshot
    {
        private const double k_RestCompareTolerance = 1e-3d;

        /// <summary>
        /// Captures the pre-edit Unity rest frame relative to <paramref name="targetRoot"/>.
        /// When <paramref name="scanHierarchyRoot"/> is provided (e.g., live avatar root while
        /// <paramref name="targetRoot"/> is an NDMF preview shadow rig), secondary avatar
        /// <see cref="SkinnedMeshRenderer"/>s are discovered on <paramref name="scanHierarchyRoot"/>.
        /// </summary>
        public static SkeletonRestFrame Capture(
            Transform targetRoot,
            IEnumerable<RendererRestSeed> priorityRenderers = null,
            Transform scanHierarchyRoot = null)
        {
            var builder = new SkeletonRestFrameBuilder();
            var livePaths = new HashSet<string>(StringComparer.Ordinal);

            if (targetRoot != null)
            {
                Matrix4x4 rootW2L = targetRoot.worldToLocalMatrix;
                foreach (var t in targetRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (t == null || t == targetRoot)
                    {
                        continue;
                    }

                    string path = FbxNameUtility.NormalizePath(AnimationUtility.CalculateTransformPath(t, targetRoot));
                    if (string.IsNullOrEmpty(path) || !livePaths.Add(path))
                    {
                        continue;
                    }

                    FbxMatrix4x4 liveGlobal = SkeletonTransformMath.FromUnityColumnMatrix(rootW2L * t.localToWorldMatrix);
                    FbxMatrix4x4 liveLocal = SkeletonTransformMath.FromUnityColumnMatrix(
                        Matrix4x4.TRS(t.localPosition, t.localRotation, t.localScale));
                    builder.AddLiveNode(path, isBone: true, liveGlobal, liveLocal);
                }
            }

            var seededRendererTransforms = new HashSet<Transform>();
            if (priorityRenderers != null)
            {
                int seedIdx = 0;
                foreach (var seed in priorityRenderers)
                {
                    if (seed.RendererTransform != null)
                    {
                        seededRendererTransforms.Add(seed.RendererTransform);
                    }

                    AddRendererSeed(
                        builder,
                        livePaths,
                        targetRoot,
                        seed,
                        priorityTier: 0,
                        fallbackLabel: $"RequestRenderer[{seedIdx++}]");
                }
            }

            Transform effectiveScanRoot = scanHierarchyRoot != null ? scanHierarchyRoot : targetRoot;
            if (effectiveScanRoot != null)
            {
                var allSmrs = effectiveScanRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                for (int s = 0; s < allSmrs.Length; s++)
                {
                    var smr = allSmrs[s];
                    if (smr == null ||
                        seededRendererTransforms.Contains(smr.transform) ||
                        smr.sharedMesh == null ||
                        smr.bones == null)
                    {
                        continue;
                    }

                    var bindposes = smr.sharedMesh.bindposes;
                    if (bindposes == null || bindposes.Length == 0)
                    {
                        continue;
                    }

                    Matrix4x4 rendererInRoot = effectiveScanRoot.worldToLocalMatrix * smr.transform.localToWorldMatrix;
                    var bones = smr.bones;
                    int count = Math.Min(bones.Length, bindposes.Length);
                    for (int i = 0; i < count; i++)
                    {
                        var bone = bones[i];
                        if (bone == null || bone == effectiveScanRoot || !bone.IsChildOf(effectiveScanRoot))
                        {
                            continue;
                        }

                        string path = FbxNameUtility.NormalizePath(
                            AnimationUtility.CalculateTransformPath(bone, effectiveScanRoot));
                        if (string.IsNullOrEmpty(path) || (targetRoot != null && !livePaths.Contains(path)))
                        {
                            continue;
                        }

                        Matrix4x4 bp = bindposes[i];
                        if (Mathf.Abs(bp.determinant) <= 1e-12f)
                        {
                            continue;
                        }

                        FbxMatrix4x4 restGlobal = SkeletonTransformMath.FromUnityColumnMatrix(rendererInRoot * bp.inverse);
                        builder.AddBindSeed(path, restGlobal, priorityTier: 2, sourceLabel: smr.name);
                    }
                }
            }

            return builder.Build(
                unitMeters: 1d,
                linearTolerance: k_RestCompareTolerance,
                translationToleranceMeters: k_RestCompareTolerance);
        }

        private static void AddRendererSeed(
            SkeletonRestFrameBuilder builder,
            HashSet<string> livePaths,
            Transform targetRoot,
            in RendererRestSeed seed,
            int priorityTier,
            string fallbackLabel)
        {
            var bones = seed.Bones;
            var poses = seed.BindPoses;
            if (bones == null || poses == null)
            {
                return;
            }

            Matrix4x4 rendererInRoot = (targetRoot != null && seed.RendererTransform != null)
                ? (targetRoot.worldToLocalMatrix * seed.RendererTransform.localToWorldMatrix)
                : Matrix4x4.identity;
            string label = seed.RendererTransform != null ? seed.RendererTransform.name : fallbackLabel;

            int count = Math.Min(bones.Length, poses.Length);
            for (int i = 0; i < count; i++)
            {
                var bone = bones[i];
                if (bone == null)
                {
                    continue;
                }

                string explicitSlotPath = seed.SlotPaths != null && i < seed.SlotPaths.Length
                    ? FbxNameUtility.NormalizePath(seed.SlotPaths[i] ?? string.Empty)
                    : string.Empty;
                string path = !string.IsNullOrEmpty(explicitSlotPath)
                    ? explicitSlotPath
                    : (targetRoot != null
                        ? FbxNameUtility.NormalizePath(AnimationUtility.CalculateTransformPath(bone, targetRoot))
                        : FbxNameUtility.NormalizePath(bone.name));
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }

                Matrix4x4 bp = poses[i];
                if (Mathf.Abs(bp.determinant) <= 1e-12f)
                {
                    continue;
                }

                FbxMatrix4x4 candidateRest = SkeletonTransformMath.FromUnityColumnMatrix(rendererInRoot * bp.inverse);
                if (livePaths.Add(path))
                {
                    FbxMatrix4x4 liveLocal = SkeletonTransformMath.FromUnityColumnMatrix(
                        Matrix4x4.TRS(bone.localPosition, bone.localRotation, bone.localScale));
                    builder.AddLiveNode(path, isBone: true, candidateRest, liveLocal);
                }

                builder.AddBindSeed(path, candidateRest, priorityTier, label);
            }
        }
    }
}
