using System.Collections.Generic;
using System.IO;
using System.Linq;
using Triturbo.BlendShapeShare;
using Triturbo.BlendShapeShare.BlendShapeData;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Features.BlendShapes;
using Triturbo.BlendShare.Features.SkinWeights;
using Triturbo.BlendShare.Persistence;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.Inspector
{
    [CustomEditor(typeof(BlendShareObject))]
    public class BlendShareObjectEditor : UnityEditor.Editor
    {
        private readonly Dictionary<MeshDataObject, bool> meshFoldouts = new();
        private bool forceApplyUnlocked;

        public override void OnInspectorGUI()
        {
            var patch = (BlendShareObject)target;
            serializedObject.Update();

            EditorWidgets.ShowBlendShareBanner();
            Localization.DrawLanguageSelection();
            EditorGUILayout.Space();

            DrawMetadata();
            EditorGUILayout.Space();
            DrawSharedBoneGraphs(patch);
            EditorGUILayout.Space();
            DrawMeshes(patch);
            EditorGUILayout.Space();
            DrawActions(patch);

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawMetadata()
        {
            EditorGUILayout.LabelField("BlendShare Object", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(BlendShareObject.m_Original)), Localization.G("data.original_fbx"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(BlendShareObject.m_DefaultGeneratedAssetName)), Localization.G("data.hidden_settings.default_asset_name"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(BlendShareObject.m_PatchId)), Localization.G("data.hidden_settings.patch_id"));
        }

        private void DrawMeshes(BlendShareObject patch)
        {
            EditorGUILayout.LabelField("Meshes", EditorStyles.boldLabel);
            foreach (var mesh in patch.Meshes)
            {
                if (mesh == null)
                {
                    continue;
                }

                if (!meshFoldouts.ContainsKey(mesh))
                {
                    meshFoldouts[mesh] = false;
                }

                var blendShapeFeature = mesh.GetFeature<BlendShapeFeatureObject>();
                int activeBlendShapeCount = blendShapeFeature?.ActiveBlendShapeIndices.Count ?? 0;
                int blendShapeCount = blendShapeFeature?.BlendShapes.Count ?? 0;

                meshFoldouts[mesh] = EditorGUILayout.Foldout(
                    meshFoldouts[mesh],
                    $"{mesh.m_Path} ({activeBlendShapeCount}/{blendShapeCount})",
                    true);

                if (!meshFoldouts[mesh])
                {
                    continue;
                }

                EditorGUI.indentLevel++;
                EditorGUILayout.LabelField("Path", mesh.m_Path);
                EditorGUILayout.LabelField("FBX Control Points", mesh.FbxControlPointCount.ToString());
                EditorGUILayout.LabelField("FBX Topology Hash", ShortHash(mesh.m_FbxTopologySignature?.Hash));
                DrawMappings(mesh);
                DrawSkinWeightSummary(mesh.GetFeature<SkinWeightFeatureObject>());
                DrawMeshPresets(patch, mesh, blendShapeFeature);
                DrawBlendShapeToggles(blendShapeFeature);
                EditorGUI.indentLevel--;
            }
        }

        private static string ShortHash(string hash)
        {
            if (string.IsNullOrEmpty(hash))
            {
                return "-";
            }

            return hash.Length <= 8 ? hash : hash.Substring(0, 8);
        }

        private void DrawSharedBoneGraphs(BlendShareObject patch)
        {
            var boneGraphs = GetSharedBoneGraphs(patch);
            if (boneGraphs.Count == 0)
            {
                return;
            }

            foreach (var boneGraph in boneGraphs)
            {
                DrawBoneGraphSummary(boneGraph);
            }
        }

        private void DrawBoneGraphSummary(BoneGraphObject boneGraph)
        {
            EditorGUILayout.LabelField(Localization.G("data.bone_graph.title"), EditorStyles.boldLabel);
            var bones = boneGraph?.Bones ?? System.Array.Empty<BoneNodeData>();
            int createdCount = bones.Count(bone => bone != null && bone.m_CreateIfMissing);
            EditorGUILayout.LabelField(Localization.S("data.bone_graph.created_count"), createdCount.ToString());
            EditorGUILayout.LabelField(Localization.S("data.skin_weights.bone_count"), bones.Count.ToString());
            EditorGUI.indentLevel++;
            foreach (var bone in bones)
            {
                if (bone == null)
                {
                    continue;
                }

                EditorGUILayout.LabelField(bone.m_Path);
            }
            EditorGUI.indentLevel--;
        }

        private static List<BoneGraphObject> GetSharedBoneGraphs(BlendShareObject patch)
        {
            return (patch?.Meshes ?? System.Array.Empty<MeshDataObject>())
                .Where(mesh => mesh != null)
                .Select(mesh => mesh.GetFeature<SkinWeightFeatureObject>()?.m_BoneGraph)
                .Where(graph => graph != null)
                .Distinct()
                .ToList();
        }

        private void DrawSkinWeightSummary(SkinWeightFeatureObject skinWeightFeature)
        {
            if (skinWeightFeature == null)
            {
                return;
            }

            EditorGUILayout.LabelField(Localization.G("data.skin_weights.title"), EditorStyles.boldLabel);
            EditorGUILayout.LabelField(Localization.S("data.skin_weights.bone_count"), skinWeightFeature.BoneSlotCount.ToString());
            EditorGUILayout.LabelField(Localization.S("data.skin_weights.weighted_control_points"), skinWeightFeature.WeightedControlPointCount.ToString());
            EditorGUILayout.LabelField(Localization.S("data.skin_weights.root_bone"), skinWeightFeature.m_RootBonePath);
        }

        private void DrawMappings(MeshDataObject mesh)
        {
            var mappings = mesh.m_Mappings ?? System.Array.Empty<UnityVertexMappingObject>();
            EditorGUILayout.LabelField("Unity Mappings", mappings.Length.ToString());
            EditorGUI.indentLevel++;
            foreach (var mapping in mappings)
            {
                if (mapping == null)
                {
                    continue;
                }

                EditorGUILayout.ObjectField("Unity Mesh", mapping.m_UnityMesh, typeof(Mesh), false);
                EditorGUILayout.LabelField("Unity Vertices", mapping.m_UnityVertexCount.ToString());
                EditorGUILayout.LabelField("Mapping Valid", mapping.m_IsValid.ToString());
                if (!mapping.m_IsValid && !string.IsNullOrEmpty(mapping.m_Report))
                {
                    EditorGUILayout.HelpBox(mapping.m_Report, MessageType.Warning);
                }
            }
            EditorGUI.indentLevel--;
        }

        private void DrawMeshPresets(BlendShareObject patch, MeshDataObject mesh, BlendShapeFeatureObject blendShapeFeature)
        {
            var presets = BlendShareAssetService.LoadPresets(patch)
                .Where(preset => preset.m_Path == mesh.m_Path)
                .ToList();

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginDisabledGroup(blendShapeFeature == null);
                if (GUILayout.Button("Save Preset"))
                {
                    string presetName = $"{mesh.m_Path} Preset";
                    var preset = blendShapeFeature.CreatePreset(mesh, presetName);
                    AssetDatabase.AddObjectToAsset(preset, patch);
                    preset.hideFlags = HideFlags.HideInHierarchy;
                    EditorUtility.SetDirty(preset);
                    AssetDatabase.SaveAssets();
                }
                EditorGUI.EndDisabledGroup();

                EditorGUI.BeginDisabledGroup(blendShapeFeature == null || presets.Count == 0);
                if (GUILayout.Button("Apply First Preset"))
                {
                    Undo.RecordObject(blendShapeFeature, "Apply BlendShape Preset");
                    blendShapeFeature.ApplyPreset(mesh, presets[0]);
                    EditorUtility.SetDirty(blendShapeFeature);
                }
                EditorGUI.EndDisabledGroup();
            }
        }

        private void DrawBlendShapeToggles(BlendShapeFeatureObject blendShapeFeature)
        {
            if (blendShapeFeature == null)
            {
                EditorGUILayout.HelpBox("No BlendShape feature found for this mesh.", MessageType.Info);
                return;
            }

            var active = new HashSet<int>(blendShapeFeature.ActiveBlendShapeIndices);
            bool changed = false;

            for (int i = 0; i < blendShapeFeature.BlendShapes.Count; i++)
            {
                var blendShape = blendShapeFeature.BlendShapes[i];
                bool enabled = active.Contains(i);
                bool updated = EditorGUILayout.ToggleLeft(blendShape.m_Name, enabled);
                if (updated == enabled)
                {
                    continue;
                }

                changed = true;
                if (updated)
                {
                    active.Add(i);
                }
                else
                {
                    active.Remove(i);
                }
            }

            if (changed)
            {
                Undo.RecordObject(blendShapeFeature, "Update BlendShape Selection");
                blendShapeFeature.SetActiveBlendShapeIndices(blendShapeFeature.ActiveBlendShapeIndices.Where(active.Contains)
                    .Concat(Enumerable.Range(0, blendShapeFeature.BlendShapes.Count).Where(index => active.Contains(index) && !blendShapeFeature.ActiveBlendShapeIndices.Contains(index))));
                EditorUtility.SetDirty(blendShapeFeature);
            }
        }

        private void DrawActions(BlendShareObject patch)
        {
            bool hasOriginal = patch.m_Original != null;
            var patchState = hasOriginal
                ? BlendShareFbxMetadataService.GetPatchState(patch.m_Original, patch)
                : default;
            var artifactMappingStatus = GetArtifactMappingStatus(patch);

#if !ENABLE_FBX_SDK
            EditorGUILayout.HelpBox(Localization.S("data.fbx_sdk_missing"), MessageType.Warning);
#endif

            using (new EditorGUI.DisabledScope(!hasOriginal))
            {
                DrawPatchMetadataSummary(patchState);
                DrawPatchActions(patch, patchState);
                DrawCreateFbxControl(patch, patchState);
            }

            if (!artifactMappingStatus.CanGenerateArtifact)
            {
                EditorGUILayout.HelpBox(artifactMappingStatus.Message, MessageType.Warning);
            }

            if (artifactMappingStatus.CanCreateMappings)
            {
                if (GUILayout.Button(Localization.G("data.create_mappings")))
                {
                    using var progress = BlendShareEditorProgress.Create(Localization.S("data.create_mappings"));
                    try
                    {
                        CreateMappingsForOriginal(patch, progress);
                    }
                    catch (BlendShareOperationCanceledException)
                    {
                        EditorUtility.DisplayDialog(Localization.S("data.create_mappings"), BlendShareProgressUtility.CanceledMessage, Localization.S("data.dialog.ok"));
                    }
                    artifactMappingStatus = GetArtifactMappingStatus(patch);
                }
            }

            using (new EditorGUI.DisabledScope(!artifactMappingStatus.CanGenerateArtifact))
            {
                if (GUILayout.Button(Localization.G("data.create_artifact")))
                {
                    if (patch.m_Original == null)
                    {
                        Localization.DisplayDialog("data.dialog.fbx_missing", Localization.S("data.dialog.ok"));
                        return;
                    }

                    string folderPath = Path.GetDirectoryName(AssetDatabase.GetAssetPath(patch));
                    string path = EditorUtility.SaveFilePanelInProject(
                        Localization.S("data.save_artifact.title"),
                        $"{patch.DefaultMeshAssetName}_Artifact",
                        "asset",
                        Localization.S("data.save_file.message"),
                        folderPath);
                    if (!string.IsNullOrEmpty(path))
                    {
                        using var progress = BlendShareEditorProgress.Create("Create BlendShare Artifact");
                        try
                        {
                            BlendShareArtifactService.CreateArtifact(patch.m_Original, new[] { patch }, path, progress);
                        }
                        catch (BlendShareOperationCanceledException)
                        {
                            EditorUtility.DisplayDialog("Create BlendShare Artifact", BlendShareProgressUtility.CanceledMessage, "OK");
                        }
                    }
                }
            }

        }

        private void DrawPatchMetadataSummary(BlendShareFbxPatchState patchState)
        {
            if (patchState.ActiveRecordCount <= 0)
            {
                return;
            }

            EditorGUILayout.LabelField("Applied BlendShare Patches", patchState.ActiveRecordCount.ToString());
            if (!string.IsNullOrEmpty(patchState.LatestPatchName))
            {
                EditorGUILayout.LabelField("Latest Patch", patchState.LatestPatchName);
            }
        }

        private void DrawPatchActions(BlendShareObject patch, BlendShareFbxPatchState patchState)
        {
            bool applyLocked = patchState.HasPatch && !forceApplyUnlocked;
            bool showRestore = patchState.ActiveRecordCount > 0;
            Rect row = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
            const float spacing = 4f;
            float halfWidth = (row.width - spacing) * 0.5f;
            var applyRect = new Rect(row.x, row.y, halfWidth, row.height);
            var restoreRect = new Rect(applyRect.xMax + spacing, row.y, halfWidth, row.height);

            DrawApplyPatchControl(applyRect, patch, patchState, applyLocked);
            DrawRestoreControl(restoreRect, patch, patchState, showRestore);

            if (applyLocked)
            {
                string lockMessage = patchState.HasExactPatch
                    ? "This BlendShare patch is already recorded on the original FBX. Unlock to apply it again; this may accumulate changes."
                    : "Another BlendShare patch with the same patch id is already recorded on the original FBX. Unlock to apply this patch anyway; this may accumulate changes or conflict with the recorded patch.";
                EditorGUILayout.HelpBox(lockMessage, MessageType.Info);
            }

            if (patchState.HasExactPatch && patchState.ActiveRecordCount > 1 && !patchState.CanRevertPatch)
            {
                EditorGUILayout.HelpBox("A recorded BlendShare patch needed for replay is missing, so this patch cannot be reverted. Restore to Original is still available.", MessageType.Warning);
            }
        }

        private void DrawApplyPatchControl(
            Rect rect,
            BlendShareObject patch,
            BlendShareFbxPatchState patchState,
            bool applyLocked)
        {
            Rect buttonRect = rect;
            if (patchState.HasPatch)
            {
                const float lockWidth = 14f;
                var lockRect = new Rect(rect.x, rect.y + 1f, lockWidth, rect.height - 2f);
                GUIContent lockIcon = forceApplyUnlocked
                    ? EditorGUIUtility.IconContent("Unlocked")
                    : EditorGUIUtility.IconContent("Locked");
                if (GUI.Button(lockRect, lockIcon, GUIStyle.none))
                {
                    forceApplyUnlocked = !forceApplyUnlocked;
                }

                buttonRect = new Rect(lockRect.xMax + 1f, rect.y, rect.width - lockWidth - 1f, rect.height);
            }

            using (new EditorGUI.DisabledScope(applyLocked))
            {
                if (GUI.Button(buttonRect, "Apply BlendShare Patch"))
                {
                    RunApplyPatch(patch, patchState);
                }
            }
        }

        private void RunApplyPatch(BlendShareObject patch, BlendShareFbxPatchState patchState)
        {
            bool force = patchState.HasPatch && forceApplyUnlocked;
            if (force && !EditorUtility.DisplayDialog(
                    "Apply BlendShare Patch",
                    "This patch id is already recorded on the FBX. Applying again may accumulate changes. Restore to original first if you do not want accumulation.",
                    "Apply",
                    "Cancel"))
            {
                return;
            }

            using var progress = BlendShareEditorProgress.Create("Apply BlendShare Patch");
            if (BlendShareGenerationService.ApplyPatch(patch.m_Original, patch, force, progress, out string message))
            {
                forceApplyUnlocked = false;
            }
            else
            {
                EditorUtility.DisplayDialog("Apply BlendShare Patch", message ?? "Failed to apply BlendShare patch.", "OK");
            }
        }

        private void DrawCreateFbxControl(BlendShareObject patch, BlendShareFbxPatchState patchState)
        {
            if (GUILayout.Button("Apply BlendShare Patch as New FBX"))
            {
                RunCreateFbx(patch, patchState);
            }
        }

        private void RunCreateFbx(BlendShareObject patch, BlendShareFbxPatchState patchState)
        {
            if (patchState.HasPatch &&
                !EditorUtility.DisplayDialog(
                    "Apply BlendShare Patch as New FBX",
                    "This patch id is already recorded on the original FBX. The generated FBX will inherit that history, and applying this patch again may accumulate changes. Restore to original first if you do not want accumulation.",
                    "Generate",
                    "Cancel"))
            {
                return;
            }

            string folderPath = Path.GetDirectoryName(AssetDatabase.GetAssetPath(patch));
            string path = EditorUtility.SaveFilePanelInProject(
                Localization.S("data.save_fbx.title"),
                patch.DefaultFbxName,
                "fbx",
                Localization.S("data.save_file.message"),
                folderPath);
            if (!string.IsNullOrEmpty(path))
            {
                using var progress = BlendShareEditorProgress.Create("Apply BlendShare Patch as New FBX");
                try
                {
                    bool created = BlendShareGenerationService.CreateFbx(patch.m_Original, new[] { patch }, path, progress: progress);
                    if (!created)
                    {
                        EditorUtility.DisplayDialog("Apply BlendShare Patch as New FBX", "Failed to create FBX.", "OK");
                    }
                }
                catch (BlendShareOperationCanceledException)
                {
                    EditorUtility.DisplayDialog("Apply BlendShare Patch as New FBX", BlendShareProgressUtility.CanceledMessage, "OK");
                }
            }
        }

        private void DrawRestoreControl(
            Rect rect,
            BlendShareObject patch,
            BlendShareFbxPatchState patchState,
            bool showRestore)
        {
            bool showDropdown = patchState.ActiveRecordCount > 1 && patchState.HasExactPatch;
            Rect restoreButtonRect = rect;
            Rect dropdownRect = default;
            if (showDropdown)
            {
                const float dropdownWidth = 20f;
                restoreButtonRect = new Rect(rect.x, rect.y, rect.width - dropdownWidth, rect.height);
                dropdownRect = new Rect(restoreButtonRect.xMax, rect.y, dropdownWidth, rect.height);
            }

            using (new EditorGUI.DisabledScope(!showRestore))
            {
                GUIStyle restoreStyle = showDropdown ? EditorStyles.miniButtonLeft : GUI.skin.button;
                if (GUI.Button(restoreButtonRect, "Restore to Original", restoreStyle))
                {
                    RunRestoreToOriginal(patch);
                }
            }

            if (showDropdown && GUI.Button(dropdownRect, EditorGUIUtility.IconContent("icon dropdown"), EditorStyles.miniButtonRight))
            {
                var menu = new GenericMenu();
                if (patchState.CanRevertPatch)
                {
                    menu.AddItem(new GUIContent("Revert This Patch"), false, () => RunRevertPatch(patch));
                }
                else
                {
                    menu.AddDisabledItem(new GUIContent("Revert This Patch"));
                }

                menu.DropDown(dropdownRect);
            }
        }

        private void RunRevertPatch(BlendShareObject patch)
        {
            if (!EditorUtility.DisplayDialog(
                    "Revert BlendShare Patch",
                    "This will restore the FBX baseline backup, then reapply every other recorded BlendShare patch. Continue?",
                    "Revert",
                    "Cancel"))
            {
                return;
            }

            using var progress = BlendShareEditorProgress.Create("Revert BlendShare Patch");
            if (BlendShareGenerationService.RevertPatch(patch.m_Original, patch, progress, out string message))
            {
                forceApplyUnlocked = false;
            }
            else
            {
                EditorUtility.DisplayDialog("Revert BlendShare Patch", message ?? "Failed to revert BlendShare patch.", "OK");
            }
        }

        private void RunRestoreToOriginal(BlendShareObject patch)
        {
            if (!EditorUtility.DisplayDialog(
                    "Restore to Original",
                    "This will restore the FBX baseline backup and remove all BlendShare patch metadata. Continue?",
                    "Restore",
                    "Cancel"))
            {
                return;
            }

            using var progress = BlendShareEditorProgress.Create("Restore to Original");
            if (BlendShareGenerationService.RestoreToOriginal(patch.m_Original, progress, out string message))
            {
                forceApplyUnlocked = false;
            }
            else
            {
                EditorUtility.DisplayDialog("Restore to Original", message ?? "Failed to restore original FBX.", "OK");
            }
        }

        private static ArtifactMappingStatus GetArtifactMappingStatus(BlendShareObject patch)
        {
            if (patch == null || patch.m_Original == null)
            {
                return ArtifactMappingStatus.Blocked(Localization.S("data.artifact_mapping.original_missing"), false);
            }

            var targetLookup = UnityMeshTargetLookup.Create(patch.m_Original);
            if (targetLookup == null)
            {
                return ArtifactMappingStatus.Blocked(Localization.S("data.artifact_mapping.target_unreadable"), false);
            }

            int invalidCount = 0;
            int missingMeshCount = 0;
            foreach (var mesh in patch.Meshes ?? System.Array.Empty<MeshDataObject>())
            {
                if (mesh == null)
                {
                    continue;
                }

                if (!targetLookup.TryGetMesh(mesh, out var targetMesh))
                {
                    missingMeshCount++;
                    continue;
                }

                bool hasValidMapping = (mesh.m_Mappings ?? System.Array.Empty<UnityVertexMappingObject>())
                    .Any(mapping => mapping != null && mapping.IsCompatibleWith(mesh, targetMesh));
                if (!hasValidMapping)
                {
                    invalidCount++;
                }
            }

            if (missingMeshCount > 0)
            {
                return ArtifactMappingStatus.Blocked(
                    string.Format(Localization.S("data.artifact_mapping.mesh_missing"), missingMeshCount),
                    false);
            }

            if (invalidCount > 0)
            {
                return ArtifactMappingStatus.Blocked(
                    string.Format(Localization.S("data.artifact_mapping.invalid"), invalidCount),
                    true);
            }

            return ArtifactMappingStatus.Ready();
        }

        private static void CreateMappingsForOriginal(BlendShareObject patch, IBlendShareProgress progress)
        {
            if (patch == null || patch.m_Original == null)
            {
                Localization.DisplayDialog("data.dialog.fbx_missing", Localization.S("data.dialog.ok"));
                return;
            }

            var targetLookup = UnityMeshTargetLookup.Create(patch.m_Original);
            if (targetLookup == null)
            {
                EditorUtility.DisplayDialog(
                    Localization.S("data.create_mappings.failed.title"),
                    Localization.S("data.artifact_mapping.target_unreadable"),
                    Localization.S("data.dialog.ok"));
                return;
            }

            var createdMappings = new List<(MeshDataObject Mesh, UnityVertexMappingObject Mapping)>();
            var failures = new List<string>();
            var meshes = (patch.Meshes ?? System.Array.Empty<MeshDataObject>())
                .Where(mesh => mesh != null)
                .ToArray();
            try
            {
                for (int meshIndex = 0; meshIndex < meshes.Length; meshIndex++)
                {
                    var mesh = meshes[meshIndex];
                    BlendShareProgressUtility.Report(
                        progress,
                        Localization.S("data.create_mappings"),
                        $"Creating mapping for {mesh.m_Path}...",
                        meshes.Length > 0 ? (float)meshIndex / meshes.Length : 0f,
                        true);

                    if (!targetLookup.TryGetMesh(mesh, out var targetMesh))
                    {
                        failures.Add($"{mesh.m_Path}: {targetLookup.GetResolutionError(mesh)}");
                        continue;
                    }

                    if ((mesh.m_Mappings ?? System.Array.Empty<UnityVertexMappingObject>())
                        .Any(mapping => mapping != null && mapping.IsCompatibleWith(mesh, targetMesh)))
                    {
                        continue;
                    }

                    var mapping = UnityFbxVertexMappingBuilder.BuildFromFbx(mesh.m_Path, targetMesh, patch.m_Original);
                    if (mapping == null || !mapping.m_IsValid)
                    {
                        failures.Add($"{mesh.m_Path}: {mapping?.m_Report ?? "mapping generation failed"}");
                        continue;
                    }

                    createdMappings.Add((mesh, mapping));
                }
            }
            catch (BlendShareOperationCanceledException)
            {
                foreach (var createdMapping in createdMappings)
                {
                    if (createdMapping.Mapping != null && !AssetDatabase.Contains(createdMapping.Mapping))
                    {
                        UnityEngine.Object.DestroyImmediate(createdMapping.Mapping);
                    }
                }

                throw;
            }

            if (createdMappings.Count > 0)
            {
                BlendShareProgressUtility.Report(progress, Localization.S("data.create_mappings"), "Saving mappings...", 0.95f, false);
                foreach (var createdMapping in createdMappings)
                {
                    createdMapping.Mesh.m_Mappings = (createdMapping.Mesh.m_Mappings ?? System.Array.Empty<UnityVertexMappingObject>())
                        .Where(existing => existing != null)
                        .Concat(new[] { createdMapping.Mapping })
                        .ToArray();
                }

                BlendShareAssetService.SaveMappings(patch);
            }

            if (failures.Count > 0)
            {
                EditorUtility.DisplayDialog(
                    Localization.S("data.create_mappings.failed.title"),
                    string.Join("\n", failures),
                    Localization.S("data.dialog.ok"));
                return;
            }

            EditorUtility.DisplayDialog(
                Localization.S("data.create_mappings.success.title"),
                string.Format(Localization.S("data.create_mappings.success.message"), createdMappings.Count),
                Localization.S("data.dialog.ok"));
        }

        private readonly struct ArtifactMappingStatus
        {
            public bool CanGenerateArtifact { get; }
            public bool CanCreateMappings { get; }
            public string Message { get; }

            private ArtifactMappingStatus(bool canGenerateArtifact, bool canCreateMappings, string message)
            {
                CanGenerateArtifact = canGenerateArtifact;
                CanCreateMappings = canCreateMappings;
                Message = message;
            }

            public static ArtifactMappingStatus Ready()
            {
                return new ArtifactMappingStatus(true, false, string.Empty);
            }

            public static ArtifactMappingStatus Blocked(string message, bool canCreateMappings)
            {
                return new ArtifactMappingStatus(false, canCreateMappings, message);
            }
        }
    }
}
