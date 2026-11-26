using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.EditorAssets.Mapping;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Fbx.Unity;
using Triturbo.BlendShare.Mapping;
using Triturbo.BlendShare.Model.Documents;
using Triturbo.BlendShare.Primitives;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.Pipeline.Apply
{
    /// <summary>
    /// Establishes exact correspondence between an imported Unity <see cref="Mesh"/> subasset on a target FBX prefab,
    /// its owning <see cref="IFbxMesh"/> node in the raw FBX scene (never relying on the patch's <c>NodePath</c>),
    /// its full-target <c>UnityVertex -&gt; ControlPoint</c> mapping (preserving prefix-extended control points), and
    /// its imported <see cref="SkinnedMeshRenderer"/> bone-path-to-slot mapping.
    /// </summary>
    public static class VerifiedRawTargetSkinResolver
    {
        public static VerifiedRawTargetSkinContext TryResolve(
            Mesh incomingMesh,
            GameObject targetFbx,
            IFbxScene rawScene,
            MeshDocument patchMeshHeader,
            ResolvedVertexMapping resolvedMapping)
        {
            if (incomingMesh == null || targetFbx == null || rawScene == null ||
                !EditorUtility.IsPersistent(incomingMesh) || !EditorUtility.IsPersistent(targetFbx))
            {
                return null;
            }

            string meshAssetPath = AssetDatabase.GetAssetPath(incomingMesh);
            string fbxAssetPath = AssetDatabase.GetAssetPath(targetFbx);
            if (string.IsNullOrEmpty(meshAssetPath) || !string.Equals(meshAssetPath, fbxAssetPath, StringComparison.Ordinal))
            {
                return null;
            }

            Transform matchedOwnerTransform = null;
            string matchedOwnerNodePath = null;
            IFbxMesh matchedRawMesh = null;
            Dictionary<string, IReadOnlyList<int>> matchedSlotsByBonePath = null;

            bool RegisterCandidateOwner(Transform ownerTransform, SkinnedMeshRenderer ownerSmr)
            {
                if (ownerTransform == null)
                {
                    return false;
                }

                string ownerPath = FbxNameUtility.NormalizePath(
                    AnimationUtility.CalculateTransformPath(ownerTransform, targetFbx.transform));
                var candidateRawMesh = rawScene.FindMeshByNodePath(ownerPath);
                if (candidateRawMesh == null)
                {
                    return false;
                }

                Dictionary<string, IReadOnlyList<int>> slotsByPath = null;
                if (ownerSmr != null && ownerSmr.bones != null)
                {
                    var builder = new Dictionary<string, List<int>>(ownerSmr.bones.Length, StringComparer.Ordinal);
                    var ownerBones = ownerSmr.bones;
                    for (int s = 0; s < ownerBones.Length; s++)
                    {
                        var b = ownerBones[s];
                        if (b == null) continue;
                        string bPath = FbxNameUtility.NormalizePath(
                            AnimationUtility.CalculateTransformPath(b, targetFbx.transform));
                        if (!string.IsNullOrEmpty(bPath))
                        {
                            if (!builder.TryGetValue(bPath, out var list))
                            {
                                list = new List<int>(1);
                                builder.Add(bPath, list);
                            }

                            list.Add(s);
                        }
                    }

                    slotsByPath = new Dictionary<string, IReadOnlyList<int>>(builder.Count, StringComparer.Ordinal);
                    foreach (var kv in builder)
                    {
                        slotsByPath[kv.Key] = kv.Value.ToArray();
                    }
                }

                if (matchedRawMesh == null)
                {
                    matchedOwnerTransform = ownerTransform;
                    matchedOwnerNodePath = ownerPath;
                    matchedRawMesh = candidateRawMesh;
                    matchedSlotsByBonePath = slotsByPath;
                    return true;
                }

                if (!ReferenceEquals(matchedRawMesh, candidateRawMesh))
                {
                    return false;
                }

                if ((matchedSlotsByBonePath == null) != (slotsByPath == null))
                {
                    return false;
                }

                if (matchedSlotsByBonePath != null)
                {
                    if (matchedSlotsByBonePath.Count != slotsByPath.Count)
                    {
                        return false;
                    }

                    foreach (var kv in matchedSlotsByBonePath)
                    {
                        if (!slotsByPath.TryGetValue(kv.Key, out var otherSlots) || !kv.Value.SequenceEqual(otherSlots))
                        {
                            return false;
                        }
                    }
                }

                return true;
            }

            foreach (var smr in targetFbx.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr != null && ReferenceEquals(smr.sharedMesh, incomingMesh))
                {
                    if (!RegisterCandidateOwner(smr.transform, smr))
                    {
                        return null;
                    }
                }
            }

            foreach (var mf in targetFbx.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf != null && ReferenceEquals(mf.sharedMesh, incomingMesh))
                {
                    if (!RegisterCandidateOwner(mf.transform, null))
                    {
                        return null;
                    }
                }
            }

            if (matchedRawMesh == null || matchedOwnerTransform == null)
            {
                return null;
            }

            string patchNodePath = FbxNameUtility.NormalizePath(patchMeshHeader?.NodePath ?? string.Empty);
            if (resolvedMapping?.FullTargetVertexMap != null &&
                (resolvedMapping.SourceKind == VertexMappingSourceKind.FbxInverseMapComputed ||
                 resolvedMapping.SourceKind == VertexMappingSourceKind.FbxInverseMapCached) &&
                string.Equals(matchedOwnerNodePath, patchNodePath, StringComparison.Ordinal) &&
                resolvedMapping.FullTargetVertexMap.SourceCount == incomingMesh.vertexCount &&
                resolvedMapping.FullTargetVertexMap.TargetCount == matchedRawMesh.ControlPointCount)
            {
                return VerifiedRawTargetSkinContext.WithSlotLists(
                    matchedRawMesh,
                    resolvedMapping.FullTargetVertexMap,
                    matchedSlotsByBonePath);
            }

            try
            {
                if (!UnityMeshReaderUtility.TryReadMeshSnapshot(incomingMesh, out var snapshot))
                {
                    return null;
                }

                var importSettings = FbxUnityAssetReader.GetImportSettings(targetFbx);
                var hints = FbxInverseMapHints.FromImportSettings(importSettings, rawScene.Metadata.Axes, matchedOwnerNodePath);
                var mapped = FbxUnityInverseMapper.Map(matchedRawMesh, snapshot, hints);
                if (!mapped.Succeeded)
                {
                    return null;
                }

                var fullTargetCsr = CsrVertexMapFactory.FromJaggedGroups(
                    mapped.VertexControlPointGroups,
                    matchedRawMesh.ControlPointCount,
                    IndexDomain.UnityVertex,
                    IndexDomain.ControlPoint);
                return VerifiedRawTargetSkinContext.WithSlotLists(matchedRawMesh, fullTargetCsr, matchedSlotsByBonePath);
            }
            catch
            {
                return null;
            }
        }
    }
}
