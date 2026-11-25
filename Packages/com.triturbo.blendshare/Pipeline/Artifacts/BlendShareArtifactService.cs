using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Triturbo.BlendShare.Components;
using Triturbo.BlendShare.EditorAssets;
using Triturbo.BlendShare.EditorAssets.Mapping;
using Triturbo.BlendShare.EditorAssets.Settings;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Pipeline.Apply;
using Triturbo.BlendShare.Runtime.Assets;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.Pipeline.Artifacts
{
    /// <summary>
    /// High-level Editor service for creating <see cref="BlendShareGeneratedArtifact"/> assets from
    /// BlendShare 2.0 patches, applying them to scene GameObjects/prefabs with <see cref="BlendShareAppliedRenderer"/>
    /// baseline capture, and reverting applied renderers cleanly with Undo.
    /// </summary>
    public static class BlendShareArtifactService
    {
        public static BlendShareGeneratedArtifact CreateArtifact(
            GameObject targetFbx,
            IReadOnlyList<BlendShareAsset> patches,
            string saveAssetPath,
            out string error)
        {
            error = null;
            if (targetFbx == null || patches == null || patches.Count == 0 || string.IsNullOrWhiteSpace(saveAssetPath))
            {
                error = "Target FBX, at least one patch, and an output asset path are required.";
                return null;
            }

            var meshByPath = CollectUnityMeshesByPath(targetFbx);
            var inputsByNodePath = new Dictionary<string, List<PatchMeshApplicationItem>>(StringComparer.Ordinal);
            var orderedNodePaths = new List<string>();
            var appliedPatchIds = new List<string>();
            var seenPatchIds = new HashSet<string>(StringComparer.Ordinal);

            for (int p = 0; p < patches.Count; p++)
            {
                var patch = patches[p];
                if (patch == null) continue;

                var docRes = BlendShareSourceRepository.Shared.ReadPatchDocument(patch);
                if (!docRes.Success || docRes.Value == null)
                {
                    error = $"Failed reading patch '{patch.name}': {docRes.Message}";
                    return null;
                }

                var doc = docRes.Value;
                var settings = EffectiveSettingsResolver.Resolve(patch).Settings;
                if (seenPatchIds.Add(doc.PatchId))
                {
                    appliedPatchIds.Add(doc.PatchId);
                }

                for (int m = 0; m < doc.Meshes.Count; m++)
                {
                    var meshDoc = doc.Meshes[m];
                    if (meshDoc == null) continue;
                    string nodePath = FbxNameUtility.NormalizePath(meshDoc.NodePath);
                    if (!meshByPath.TryGetValue(nodePath, out var entry) || entry.Mesh == null)
                    {
                        error = $"Target FBX '{targetFbx.name}' does not contain Unity mesh at '{nodePath}'.";
                        return null;
                    }

                    var mapRes = VertexMappingService.Shared.Resolve(new VertexMappingResolveRequest
                    {
                        MeshHeader = meshDoc,
                        SpaceProvenance = doc.SpaceProvenance,
                        TargetUnityMesh = entry.Mesh,
                        TargetFbxAsset = targetFbx,
                        PayloadSource = BlendShareSourceRepository.Shared.PayloadStore,
                        CacheMode = VertexMappingCacheMode.PersistAssetsOnly
                    });
                    if (!mapRes.Succeeded || mapRes.Mapping == null)
                    {
                        error = $"Failed mapping mesh '{nodePath}': {mapRes.FailureCode} ({mapRes.Diagnostic})";
                        return null;
                    }

                    if (!inputsByNodePath.TryGetValue(nodePath, out var list))
                    {
                        list = new List<PatchMeshApplicationItem>();
                        inputsByNodePath.Add(nodePath, list);
                        orderedNodePaths.Add(nodePath);
                    }

                    list.Add(new PatchMeshApplicationItem
                    {
                        PatchId = doc.PatchId,
                        MeshId = meshDoc.MeshId,
                        SpaceProvenance = doc.SpaceProvenance,
                        MeshHeader = meshDoc,
                        Skeleton = doc.Skeleton,
                        EffectiveSettings = settings,
                        PayloadSource = BlendShareSourceRepository.Shared.PayloadStore,
                        VertexMapping = mapRes.Mapping,
                        CreateMissingBones = true
                    });
                }
            }

            if (orderedNodePaths.Count == 0)
            {
                error = "No meshes found in the supplied patches.";
                return null;
            }

            // Instantiate a temporary detached clone of the target FBX so skin bone creation never touches the FBX asset.
            var scratchRoot = UnityEngine.Object.Instantiate(targetFbx);
            scratchRoot.hideFlags = HideFlags.HideAndDontSave;
            var buildInputs = new List<ArtifactMeshBuildInput>(orderedNodePaths.Count);
            var allocatedMeshes = new List<Mesh>();
            try
            {
                var scratchByPath = CollectUnityMeshesByPath(scratchRoot);
                for (int i = 0; i < orderedNodePaths.Count; i++)
                {
                    string nodePath = orderedNodePaths[i];
                    var items = inputsByNodePath[nodePath];
                    var scratchEntry = scratchByPath[nodePath];
                    var applyRes = UnityMeshApplicationPipeline.Apply(new UnityMeshApplyRequest
                    {
                        BaseMesh = scratchEntry.Mesh,
                        IncomingBones = scratchEntry.Renderer != null ? scratchEntry.Renderer.bones : Array.Empty<Transform>(),
                        IncomingRootBone = scratchEntry.Renderer != null ? scratchEntry.Renderer.rootBone : null,
                        TargetRootTransform = scratchRoot.transform,
                        MeshRendererTransform = scratchEntry.Transform,
                        MaxBonesPerVertex = BlendShareSkinWeightSettings.EffectiveMaxBonesPerVertex,
                        CreateMissingBones = true,
                        PreserveExistingBlendShapes = true,
                        Items = items
                    });

                    if (!applyRes.Succeeded || applyRes.OutputMesh == null)
                    {
                        string diag = string.Join("; ", applyRes.Diagnostics.Select(d => d.Message));
                        error = $"Failed applying features to mesh '{nodePath}': {diag}";
                        return null;
                    }

                    allocatedMeshes.Add(applyRes.OutputMesh);
                    buildInputs.Add(new ArtifactMeshBuildInput
                    {
                        MeshId = items[0].MeshId,
                        NodePath = nodePath,
                        ApplyResult = applyRes,
                        TargetRootTransform = scratchRoot.transform
                    });
                }

                var inMemory = BlendShareArtifactBuilder.CreateInMemory(targetFbx, appliedPatchIds, buildInputs);
                try
                {
                    if (File.Exists(saveAssetPath))
                    {
                        AssetDatabase.DeleteAsset(saveAssetPath);
                    }

                    return BlendShareArtifactBuilder.SaveToAsset(inMemory, saveAssetPath);
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(inMemory);
                }
            }
            catch (Exception ex)
            {
                error = $"Failed creating artifact: {ex.Message}";
                return null;
            }
            finally
            {
                for (int i = 0; i < allocatedMeshes.Count; i++)
                {
                    if (allocatedMeshes[i] != null) UnityEngine.Object.DestroyImmediate(allocatedMeshes[i]);
                }

                if (scratchRoot != null) UnityEngine.Object.DestroyImmediate(scratchRoot);
            }
        }

        public static bool ApplyArtifact(BlendShareGeneratedArtifact artifact, Transform targetRoot)
        {
            if (artifact == null || targetRoot == null)
            {
                return false;
            }

            bool isPrefabAsset = EditorUtility.IsPersistent(targetRoot.gameObject);
            if (isPrefabAsset)
            {
                string prefabPath = AssetDatabase.GetAssetPath(targetRoot.gameObject);
                if (string.IsNullOrEmpty(prefabPath)) return false;

                var contents = PrefabUtility.LoadPrefabContents(prefabPath);
                try
                {
                    bool changed = ApplyArtifactToHierarchy(artifact, contents.transform, recordUndo: false);
                    if (changed)
                    {
                        PrefabUtility.SaveAsPrefabAsset(contents, prefabPath);
                    }

                    return changed;
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(contents);
                }
            }

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Apply BlendShare Artifact");
            return ApplyArtifactToHierarchy(artifact, targetRoot, recordUndo: true);
        }

        public static bool RevertAppliedRenderer(SkinnedMeshRenderer renderer)
        {
            if (renderer == null)
            {
                return false;
            }

            var marker = renderer.GetComponent<BlendShareAppliedRenderer>();
            if (marker == null || !marker.HasBaseline)
            {
                return false;
            }

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Revert BlendShare Applied Renderer");
            Undo.RecordObject(renderer, "Revert BlendShare Applied Renderer");

            renderer.sharedMesh = marker.OriginalMesh;
            renderer.rootBone = marker.OriginalRootBone;
            renderer.bones = marker.OriginalBones;
            EditorUtility.SetDirty(renderer);

            var generatedBones = marker.GeneratedBones;
            for (int i = generatedBones.Length - 1; i >= 0; i--)
            {
                var bone = generatedBones[i];
                if (bone != null && bone.childCount == 0)
                {
                    Undo.DestroyObjectImmediate(bone.gameObject);
                }
            }

            Undo.DestroyObjectImmediate(marker);
            return true;
        }

        private static bool ApplyArtifactToHierarchy(BlendShareGeneratedArtifact artifact, Transform root, bool recordUndo)
        {
            bool anyApplied = false;
            var skeletonBonesByPath = new Dictionary<string, GeneratedSkeletonBoneRecord>(StringComparer.Ordinal);
            var createdSkeletonBones = new List<Transform>();
            var createdSkeletonSet = new HashSet<Transform>();

            if (artifact.SkeletonBones != null && artifact.SkeletonBones.Count > 0)
            {
                var orderedBones = artifact.SkeletonBones
                    .Where(b => b != null && !string.IsNullOrWhiteSpace(b.Path))
                    .OrderBy(b => CountPathDepth(FbxNameUtility.NormalizePath(b.Path)))
                    .ThenBy(b => FbxNameUtility.NormalizePath(b.Path), StringComparer.Ordinal)
                    .ToArray();

                for (int i = 0; i < orderedBones.Length; i++)
                {
                    var boneRecord = orderedBones[i];
                    string normPath = FbxNameUtility.NormalizePath(boneRecord.Path);
                    if (string.IsNullOrEmpty(normPath)) continue;
                    skeletonBonesByPath[normPath] = boneRecord;

                    var existing = root.Find(normPath);
                    if (existing != null)
                    {
                        if (recordUndo) Undo.RecordObject(existing, "Apply BlendShare Skeleton Bone Transform");
                        boneRecord.ApplyLocalTransform(existing);
                        EditorUtility.SetDirty(existing);
                        anyApplied = true;
                        continue;
                    }

                    if (!boneRecord.CreateIfMissing)
                    {
                        continue;
                    }

                    var created = CreateMissingBonePath(root, normPath, recordUndo, createdSkeletonBones, createdSkeletonSet);
                    if (created != null)
                    {
                        if (recordUndo) Undo.RecordObject(created, "Apply BlendShare Skeleton Bone Transform");
                        boneRecord.ApplyLocalTransform(created);
                        EditorUtility.SetDirty(created);
                        anyApplied = true;
                    }
                }
            }

            var entries = artifact.Meshes;
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry == null || entry.Mesh == null) continue;

                var targetTransform = FindTransformByPath(root, entry.NodePath);
                if (targetTransform == null) continue;

                var smr = targetTransform.GetComponent<SkinnedMeshRenderer>();
                if (smr != null)
                {
                    var marker = smr.GetComponent<BlendShareAppliedRenderer>();
                    if (marker == null)
                    {
                        marker = recordUndo
                            ? Undo.AddComponent<BlendShareAppliedRenderer>(smr.gameObject)
                            : smr.gameObject.AddComponent<BlendShareAppliedRenderer>();
                    }

                    if (!marker.HasBaseline)
                    {
                        if (recordUndo) Undo.RecordObject(marker, "Capture BlendShare Baseline");
                        marker.CaptureBaseline(smr);
                    }

                    if (recordUndo) Undo.RecordObject(smr, "Apply BlendShare Artifact Mesh");
                    smr.sharedMesh = entry.Mesh;

                    if (entry.BonePaths != null && entry.BonePaths.Count > 0)
                    {
                        var resolvedBones = new Transform[entry.BonePaths.Count];
                        var createdBones = new List<Transform>();
                        var createdSet = new HashSet<Transform>();
                        var bindPosesByPath = BuildBindPoseLookup(entry);

                        for (int b = 0; b < entry.BonePaths.Count; b++)
                        {
                            string bonePath = entry.BonePaths[b];
                            if (string.IsNullOrEmpty(bonePath)) continue;
                            string normBonePath = FbxNameUtility.NormalizePath(bonePath);
                            var found = FindTransformByPath(root, normBonePath);
                            if (found == null)
                            {
                                bool canCreate = !skeletonBonesByPath.TryGetValue(normBonePath, out var skelBone) || skelBone.CreateIfMissing;
                                if (canCreate)
                                {
                                    found = CreateMissingBonePath(root, normBonePath, recordUndo, createdBones, createdSet);
                                    if (found != null && skelBone != null)
                                    {
                                        skelBone.ApplyLocalTransform(found);
                                    }
                                }
                            }

                            resolvedBones[b] = found;
                        }

                        var fallbackCreated = createdBones
                            .Where(t => t != null && !skeletonBonesByPath.ContainsKey(GetRelativeTransformPath(t, root)))
                            .ToList();
                        if (fallbackCreated.Count > 0)
                        {
                            ApplyBindPosesToCreatedBones(root, smr.transform, fallbackCreated, bindPosesByPath);
                        }

                        smr.bones = resolvedBones;
                        if (!string.IsNullOrEmpty(entry.RootBonePath))
                        {
                            var rootBone = FindTransformByPath(root, entry.RootBonePath);
                            if (rootBone != null)
                            {
                                smr.rootBone = rootBone;
                            }
                        }

                        var allGenerated = new List<Transform>(createdBones);
                        for (int k = 0; k < createdSkeletonBones.Count; k++)
                        {
                            if (createdSkeletonBones[k] != null && resolvedBones.Contains(createdSkeletonBones[k]) && !allGenerated.Contains(createdSkeletonBones[k]))
                            {
                                allGenerated.Add(createdSkeletonBones[k]);
                            }
                        }

                        if (allGenerated.Count > 0)
                        {
                            if (recordUndo) Undo.RecordObject(marker, "Record Generated Bones");
                            marker.AddGeneratedBones(allGenerated.ToArray());
                        }
                    }

                    EditorUtility.SetDirty(smr);
                    EditorUtility.SetDirty(marker);
                    anyApplied = true;
                    continue;
                }

                var mf = targetTransform.GetComponent<MeshFilter>();
                if (mf != null)
                {
                    if (recordUndo) Undo.RecordObject(mf, "Apply BlendShare Artifact Mesh");
                    mf.sharedMesh = entry.Mesh;
                    EditorUtility.SetDirty(mf);
                    anyApplied = true;
                }
            }

            return anyApplied;
        }

        private static Transform FindTransformByPath(Transform root, string nodePath)
        {
            string norm = FbxNameUtility.NormalizePath(nodePath ?? string.Empty);
            if (string.IsNullOrEmpty(norm)) return root;
            var direct = root.Find(norm);
            if (direct != null) return direct;

            // Fallback: match by leaf name if hierarchy root differs slightly.
            int slash = norm.LastIndexOf('/');
            string leaf = slash >= 0 ? norm.Substring(slash + 1) : norm;
            var all = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != root && string.Equals(all[i].name, leaf, StringComparison.Ordinal))
                {
                    return all[i];
                }
            }

            return null;
        }

        private static Transform CreateMissingBonePath(
            Transform root,
            string bonePath,
            bool recordUndo,
            List<Transform> createdBones,
            HashSet<Transform> createdSet)
        {
            string norm = FbxNameUtility.NormalizePath(bonePath);
            string[] parts = norm.Split('/');
            var current = root;
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                if (string.IsNullOrEmpty(part)) continue;
                var next = current.Find(part);
                if (next == null)
                {
                    var go = new GameObject(part);
                    if (recordUndo) Undo.RegisterCreatedObjectUndo(go, "Create BlendShare Bone");
                    next = go.transform;
                    next.SetParent(current, worldPositionStays: false);
                    if (createdSet.Add(next))
                    {
                        createdBones.Add(next);
                    }
                }

                current = next;
            }

            return current;
        }

        private static Dictionary<string, Matrix4x4> BuildBindPoseLookup(GeneratedMeshEntry entry)
        {
            var map = new Dictionary<string, Matrix4x4>(StringComparer.Ordinal);
            if (entry == null) return map;

            var meshBindPoses = entry.Mesh != null ? entry.Mesh.bindposes : null;
            if (meshBindPoses != null && entry.BonePaths != null)
            {
                int count = Math.Min(meshBindPoses.Length, entry.BonePaths.Count);
                for (int i = 0; i < count; i++)
                {
                    string norm = FbxNameUtility.NormalizePath(entry.BonePaths[i] ?? string.Empty);
                    if (!string.IsNullOrEmpty(norm) && !map.ContainsKey(norm))
                    {
                        map[norm] = meshBindPoses[i];
                    }
                }
            }

            if (entry.BoneBindings != null)
            {
                for (int i = 0; i < entry.BoneBindings.Count; i++)
                {
                    var binding = entry.BoneBindings[i];
                    if (binding == null) continue;
                    string norm = FbxNameUtility.NormalizePath(binding.TargetBonePath ?? string.Empty);
                    if (!string.IsNullOrEmpty(norm))
                    {
                        map[norm] = binding.BindPose;
                    }
                }
            }

            return map;
        }

        private static void ApplyBindPosesToCreatedBones(
            Transform root,
            Transform rendererTransform,
            List<Transform> createdBones,
            Dictionary<string, Matrix4x4> bindPosesByPath)
        {
            if (createdBones == null || createdBones.Count == 0 || bindPosesByPath == null || bindPosesByPath.Count == 0)
            {
                return;
            }

            var ordered = createdBones
                .Where(t => t != null)
                .Select(t => (Transform: t, Path: GetRelativeTransformPath(t, root)))
                .OrderBy(item => CountPathDepth(item.Path))
                .ToList();

            Matrix4x4 rendererLocalToWorld = rendererTransform != null ? rendererTransform.localToWorldMatrix : Matrix4x4.identity;
            for (int i = 0; i < ordered.Count; i++)
            {
                var bone = ordered[i].Transform;
                if (!bindPosesByPath.TryGetValue(ordered[i].Path, out var bindPose)) continue;
                if (Mathf.Abs(bindPose.determinant) < 1e-12f) continue;

                Matrix4x4 boneWorld = rendererLocalToWorld * bindPose.inverse;
                Matrix4x4 parentWorldToLocal = bone.parent != null ? bone.parent.worldToLocalMatrix : Matrix4x4.identity;
                Matrix4x4 local = parentWorldToLocal * boneWorld;

                bone.localPosition = new Vector3(local.m03, local.m13, local.m23);
                Vector3 colX = new Vector3(local.m00, local.m10, local.m20);
                Vector3 colY = new Vector3(local.m01, local.m11, local.m21);
                Vector3 colZ = new Vector3(local.m02, local.m12, local.m22);
                float sx = colX.magnitude;
                float sy = colY.magnitude;
                float sz = colZ.magnitude;
                if (Vector3.Dot(Vector3.Cross(colX, colY), colZ) < 0f)
                {
                    sz = -sz;
                }

                bone.localScale = new Vector3(sx, sy, sz);
                if (sx > 1e-6f && sy > 1e-6f && Mathf.Abs(sz) > 1e-6f)
                {
                    Vector3 forward = colZ / sz;
                    Vector3 up = colY / sy;
                    if (forward.sqrMagnitude > 1e-8f && up.sqrMagnitude > 1e-8f)
                    {
                        bone.localRotation = Quaternion.LookRotation(forward, up);
                    }
                }
            }
        }

        private static int CountPathDepth(string path)
        {
            if (string.IsNullOrEmpty(path)) return 0;
            int depth = 1;
            for (int i = 0; i < path.Length; i++)
            {
                if (path[i] == '/') depth++;
            }
            return depth;
        }

        private readonly struct UnityMeshNodeEntry
        {
            public readonly Mesh Mesh;
            public readonly Transform Transform;
            public readonly SkinnedMeshRenderer Renderer;

            public UnityMeshNodeEntry(Mesh mesh, Transform transform, SkinnedMeshRenderer renderer)
            {
                Mesh = mesh;
                Transform = transform;
                Renderer = renderer;
            }
        }

        private static Dictionary<string, UnityMeshNodeEntry> CollectUnityMeshesByPath(GameObject root)
        {
            var map = new Dictionary<string, UnityMeshNodeEntry>(StringComparer.Ordinal);
            if (root == null) return map;

            var rootTransform = root.transform;
            var skinned = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < skinned.Length; i++)
            {
                var smr = skinned[i];
                if (smr == null || smr.sharedMesh == null) continue;
                string path = GetRelativeTransformPath(smr.transform, rootTransform);
                if (!map.ContainsKey(path)) map.Add(path, new UnityMeshNodeEntry(smr.sharedMesh, smr.transform, smr));
            }

            var filters = root.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < filters.Length; i++)
            {
                var mf = filters[i];
                if (mf == null || mf.sharedMesh == null) continue;
                string path = GetRelativeTransformPath(mf.transform, rootTransform);
                if (!map.ContainsKey(path)) map.Add(path, new UnityMeshNodeEntry(mf.sharedMesh, mf.transform, null));
            }

            return map;
        }

        private static string GetRelativeTransformPath(Transform target, Transform root)
        {
            if (target == null || target == root) return string.Empty;
            var parts = new List<string>();
            var curr = target;
            while (curr != null && curr != root)
            {
                parts.Add(curr.name);
                curr = curr.parent;
            }

            parts.Reverse();
            return FbxNameUtility.NormalizePath(string.Join("/", parts));
        }
    }
}
