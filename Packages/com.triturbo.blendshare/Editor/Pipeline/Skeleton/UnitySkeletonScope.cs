using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Assets;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Model.Documents;
using Triturbo.BlendShare.Model.Features;
using Triturbo.BlendShare.Pipeline.Apply;
using UnityEngine;

namespace Triturbo.BlendShare.Pipeline.Skeleton
{
    /// <summary>
    /// Candidate patch skeleton input for <see cref="UnitySkeletonScope"/>.
    /// </summary>
    public readonly struct SkeletonScopeCandidate
    {
        public string PatchId { get; }
        public SkeletonDocument Skeleton { get; }
        public FbxMatrix4x4 StoredToUnitySceneLocal { get; }

        public SkeletonScopeCandidate(
            string patchId,
            SkeletonDocument skeleton,
            FbxMatrix4x4 storedToUnitySceneLocal)
        {
            PatchId = patchId ?? string.Empty;
            Skeleton = skeleton;
            StoredToUnitySceneLocal = storedToUnitySceneLocal;
        }
    }

    /// <summary>
    /// Avatar- or artifact-wide Unity skeleton scope wrapping <see cref="UnityRestFrameSnapshot"/> (Stage 1)
    /// and <see cref="SkeletonAlignment"/> (Stage 2) across multiple renderers and patches.
    /// </summary>
    public sealed class UnitySkeletonScope
    {
        public SkeletonRestFrame PreEditRestFrame { get; }
        public SkeletonAlignment AlignmentResult { get; }
        public IReadOnlyDictionary<string, (string PatchId, GeneratedSkeletonBoneRecord Record)> SolvedBonesByPath { get; }
        public bool HasAnySkeletonEdits { get; }

        private UnitySkeletonScope(
            SkeletonRestFrame preEditRestFrame,
            SkeletonAlignment alignmentResult,
            IReadOnlyDictionary<string, (string PatchId, GeneratedSkeletonBoneRecord Record)> solvedBonesByPath,
            bool hasAnySkeletonEdits)
        {
            PreEditRestFrame = preEditRestFrame;
            AlignmentResult = alignmentResult;
            SolvedBonesByPath = solvedBonesByPath;
            HasAnySkeletonEdits = hasAnySkeletonEdits;
        }

        /// <summary>
        /// Resolves the post-edit endpoint calibration basis <c>D_frame(b)</c> for <paramref name="bonePath"/>,
        /// failing closed if <paramref name="bonePath"/> or an ancestor has conflicting pre-edit bindpose seeds.
        /// </summary>
        public FbxMatrix4x4 ResolveEndpointBasis(
            SkeletonDocument patchSkeleton,
            string bonePath,
            FbxMatrix4x4 storedToUnitySceneLocal)
        {
            if (AlignmentResult == null)
            {
                return FbxLinearAlgebra3d.ExtractLinear(storedToUnitySceneLocal);
            }

            if (!AlignmentResult.TryResolveEndpointBasis(
                    patchSkeleton,
                    storedToUnitySceneLocal,
                    bonePath,
                    out var endpointD,
                    out string error))
            {
                throw new InvalidOperationException(error);
            }

            return endpointD;
        }

        /// <summary>
        /// Looks up the post-edit Target rest local matrix (<c>L'_b</c>) for <paramref name="path"/>,
        /// throwing if <paramref name="path"/> has conflicting pre-edit bindposes.
        /// </summary>
        public bool TryGetPostEditRestLocal(string path, out FbxMatrix4x4 postRestLocal)
        {
            postRestLocal = FbxMatrix4x4.Identity;
            if (AlignmentResult == null)
            {
                return false;
            }

            if (!AlignmentResult.TryGetPostEditRestLocal(path, out postRestLocal, out string conflictErr))
            {
                if (!string.IsNullOrEmpty(conflictErr))
                {
                    throw new InvalidOperationException(conflictErr);
                }

                return false;
            }

            return true;
        }

        /// <summary>
        /// Looks up the post-edit Target rest global matrix (<c>R'_b</c>, relative to <c>TargetRootTransform</c>)
        /// for <paramref name="path"/>, throwing if <paramref name="path"/> has conflicting pre-edit bindposes.
        /// </summary>
        public bool TryGetPostEditRestGlobal(string path, out FbxMatrix4x4 postRestGlobal)
        {
            postRestGlobal = FbxMatrix4x4.Identity;
            if (AlignmentResult == null)
            {
                return false;
            }

            if (!AlignmentResult.TryGetPostEditRestGlobal(path, out postRestGlobal, out string conflictErr))
            {
                if (!string.IsNullOrEmpty(conflictErr))
                {
                    throw new InvalidOperationException(conflictErr);
                }

                return false;
            }

            return true;
        }

        /// <summary>
        /// Builds a <see cref="UnitySkeletonScope"/> from renderer rest seeds and patch skeleton candidates.
        /// </summary>
        public static UnitySkeletonScope Build(
            Transform targetRoot,
            IEnumerable<RendererRestSeed> renderers,
            IEnumerable<SkeletonScopeCandidate> candidates,
            IReadOnlyDictionary<BoneOverrideKey, Transform> boneTransformOverrides,
            Transform scanHierarchyRoot = null)
        {
            var restFrame = UnityRestFrameSnapshot.Capture(targetRoot, renderers, scanHierarchyRoot);

            var patchInputs = new List<SkeletonAlignmentPatchInput>();
            bool hasAnySkeletonEdits = false;
            if (candidates != null)
            {
                foreach (var cand in candidates)
                {
                    if (cand.Skeleton?.Nodes == null || cand.Skeleton.Nodes.Count == 0)
                    {
                        continue;
                    }

                    hasAnySkeletonEdits = true;
                    HashSet<string> ignored = UnitySkeletonWriter.BuildIgnoredOverridePaths(
                        cand.Skeleton,
                        cand.PatchId,
                        boneTransformOverrides);

                    patchInputs.Add(new SkeletonAlignmentPatchInput(
                        cand.PatchId,
                        cand.Skeleton,
                        cand.StoredToUnitySceneLocal,
                        ignored));
                }
            }

            var alignment = SkeletonAlignment.Solve(restFrame, patchInputs);
            if (alignment.HasErrors)
            {
                string firstError = alignment.Diagnostics.First(d => d.IsError).Message;
                throw new InvalidOperationException(firstError);
            }

            var solvedBonesByPath = new Dictionary<string, (string PatchId, GeneratedSkeletonBoneRecord Record)>(
                alignment.OrderedSolvedPaths.Count,
                StringComparer.Ordinal);

            for (int i = 0; i < alignment.OrderedSolvedPaths.Count; i++)
            {
                string path = alignment.OrderedSolvedPaths[i];
                var solved = alignment.SolvedNodesByPath[path];
                FbxMatrix4x4 writeLocal = alignment.GetWriteLocal(path);
                if (!SkeletonTransformMath.TryDecomposeUnityLocalTransform(
                        writeLocal.ToUnityColumnMatrix(),
                        out var pos,
                        out var rot,
                        out var scale,
                        out string decomposeError))
                {
                    throw new InvalidOperationException(
                        $"Cannot convert skeleton bone '{path}': {decomposeError ?? "invalid local matrix"}");
                }

                solvedBonesByPath[path] = (solved.SourceId, new GeneratedSkeletonBoneRecord(path, pos, rot, scale));
            }

            return new UnitySkeletonScope(
                restFrame,
                alignment,
                solvedBonesByPath,
                hasAnySkeletonEdits);
        }

        /// <summary>
        /// Builds a shared <see cref="UnitySkeletonScope"/> across all <see cref="UnityMeshApplyRequest"/>s on an avatar or artifact.
        /// </summary>
        public static UnitySkeletonScope BuildForRequests(
            Transform targetRoot,
            IReadOnlyList<UnityMeshApplyRequest> requests,
            Transform scanHierarchyRoot = null)
        {
            var seeds = new List<RendererRestSeed>();
            var candidates = new List<SkeletonScopeCandidate>();
            var mergedOverrides = new Dictionary<BoneOverrideKey, Transform>();

            if (requests != null)
            {
                for (int r = 0; r < requests.Count; r++)
                {
                    var req = requests[r];
                    if (req == null) continue;
                    if (req.BaseMesh != null)
                    {
                        Dictionary<string, IReadOnlyList<int>> reqImportedSlots = null;
                        bool reqSlotsConflict = false;
                        if (req.IncomingBoneSlotPaths == null && req.Items != null)
                        {
                            for (int i = 0; i < req.Items.Count; i++)
                            {
                                var impMap = req.Items[i]?.VerifiedRawTargetSkin?.ImportedSlotsByFbxBonePath;
                                if (impMap == null)
                                {
                                    continue;
                                }

                                reqImportedSlots ??= new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal);
                                foreach (var kv in impMap)
                                {
                                    string normKey = FbxNameUtility.NormalizePath(kv.Key ?? string.Empty);
                                    if (string.IsNullOrEmpty(normKey) || kv.Value == null || kv.Value.Count == 0)
                                    {
                                        continue;
                                    }

                                    int[] canonicalSlots = kv.Value.Distinct().OrderBy(s => s).ToArray();
                                    if (reqImportedSlots.TryGetValue(normKey, out var prevSlots))
                                    {
                                        if (!prevSlots.SequenceEqual(canonicalSlots))
                                        {
                                            reqSlotsConflict = true;
                                            break;
                                        }
                                    }
                                    else
                                    {
                                        reqImportedSlots.Add(normKey, canonicalSlots);
                                    }
                                }

                                if (reqSlotsConflict)
                                {
                                    break;
                                }
                            }
                        }

                        string[] effectiveSlotPaths = ResolveEffectiveSeedSlotPaths(
                            req.IncomingBones?.Length ?? 0,
                            req.IncomingBoneSlotPaths,
                            reqSlotsConflict ? null : reqImportedSlots);
                        seeds.Add(new RendererRestSeed(
                            req.MeshRendererTransform,
                            req.IncomingBones,
                            req.BaseMesh.bindposes,
                            effectiveSlotPaths));
                    }

                    if (req.BoneTransformOverrides != null)
                    {
                        foreach (var kv in req.BoneTransformOverrides)
                        {
                            mergedOverrides[kv.Key] = kv.Value;
                        }
                    }

                    if (req.Items == null) continue;
                    for (int i = 0; i < req.Items.Count; i++)
                    {
                        var item = req.Items[i];
                        if (item?.VertexMapping == null) continue;
                        bool hasSkinFeature = (item.Features ?? item.MeshHeader?.Features)?
                            .OfType<SkinWeightFeature>()
                            .Any() == true;
                        if (!hasSkinFeature && item.Skeleton?.HasContent != true)
                        {
                            continue;
                        }

                        if (item.Skeleton != null && item.Skeleton.HasContent)
                        {
                            candidates.Add(new SkeletonScopeCandidate(
                                item.PatchId,
                                item.Skeleton,
                                item.VertexMapping.SpaceContract.EffectiveStoredToUnitySceneLocal));
                        }
                    }
                }
            }

            return Build(targetRoot, seeds, candidates, mergedOverrides, scanHierarchyRoot);
        }

        /// <summary>
        /// Synthesizes per-slot FBX bone paths from <paramref name="importedSlotsByFbxBonePath"/> when
        /// <paramref name="explicitSlotPaths"/> is not supplied, preventing sibling slots that share a remapped
        /// Unity <see cref="Transform"/> from mis-seeding each other's rest bindpose.
        /// </summary>
        internal static string[] ResolveEffectiveSeedSlotPaths(
            int slotCount,
            string[] explicitSlotPaths,
            IReadOnlyDictionary<string, IReadOnlyList<int>> importedSlotsByFbxBonePath)
        {
            if (explicitSlotPaths != null || importedSlotsByFbxBonePath == null || importedSlotsByFbxBonePath.Count == 0 || slotCount <= 0)
            {
                return explicitSlotPaths;
            }

            var synthesized = new string[slotCount];
            foreach (var kv in importedSlotsByFbxBonePath)
            {
                string normPath = FbxNameUtility.NormalizePath(kv.Key ?? string.Empty);
                if (string.IsNullOrEmpty(normPath) || kv.Value == null)
                {
                    continue;
                }

                for (int i = 0; i < kv.Value.Count; i++)
                {
                    int slot = kv.Value[i];
                    if ((uint)slot < (uint)slotCount && string.IsNullOrEmpty(synthesized[slot]))
                    {
                        synthesized[slot] = normPath;
                    }
                }
            }

            return synthesized;
        }
    }
}
