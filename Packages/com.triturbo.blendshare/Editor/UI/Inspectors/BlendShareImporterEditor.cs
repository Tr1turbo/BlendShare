using System;
using Triturbo.BlendShare.UI;
using Triturbo.BlendShare.UI.Shared;
using UnityEngine.UIElements;
using System.Collections.Generic;
using Triturbo.BlendShare.EditorAssets;
using Triturbo.BlendShare.EditorAssets.Settings;
using Triturbo.BlendShare.Assets;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace Triturbo.BlendShare.UI.Inspectors
{
    /// <summary>
    /// Root Inspector adapter for .blendshare files.
    /// Suppresses Unity's default ScriptedImporter Apply/Revert second write path and delegates directly
    /// to BlendShareSettingsSession + BlendShareAssetEditor.
    /// Supports multi-object editing of patch-level fields (DefaultTargetFbx) while keeping per-ID lists read-only.
    /// </summary>
    [CustomEditor(typeof(BlendShareImporter))]
    [CanEditMultipleObjects]
    public sealed class BlendShareImporterEditor : ScriptedImporterEditor
    {
        private UnityEditor.Editor m_RootEditor;
        private readonly HashSet<string> m_TrackedGuids = new HashSet<string>(StringComparer.Ordinal);

        public override bool showImportedObject => false;
        protected override bool needsApplyRevert => false;

        public override void OnEnable()
        {
            base.OnEnable();
            RebuildTrackedGuids();
        }

        public override VisualElement CreateInspectorGUI()
        {
            if (targets.Length > 1) return new IMGUIContainer(DrawMultiSelectPatchInspector);
            var importer = (BlendShareImporter)target;
            var patch = AssetDatabase.LoadAssetAtPath<BlendShareAsset>(importer.assetPath);
            if (!patch) return new HelpBox(Localization.S("patch.mesh_data.missing"), HelpBoxMessageType.Warning);
            CreateCachedEditor(patch, typeof(BlendShareAssetEditor), ref m_RootEditor);
            ((BlendShareAssetEditor)m_RootEditor).SuppressFlushOnDisable = true;
            return m_RootEditor.CreateInspectorGUI();
        }

        private void DrawMultiSelectPatchInspector()
        {
            bool prevEnabled = GUI.enabled;
            GUI.enabled = true;
            try
            {
                var buffers = new List<BlendShareSettingsUndoBuffer>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                bool anyReadOnly = false;

                for (int i = 0; i < targets.Length; i++)
                {
                    if (!(targets[i] is BlendShareImporter imp)) continue;
                    string guid = AssetDatabase.AssetPathToGUID(imp.assetPath);
                    if (string.IsNullOrEmpty(guid) || !seen.Add(guid)) continue;
                    if (BlendShareSettingsSession.IsAssetReadOnly(imp.assetPath, out _)) anyReadOnly = true;
                    var buf = BlendShareSettingsSession.GetOrCreateBufferByGuid(guid);
                    if (buf != null) buffers.Add(buf);
                }

                EditorGUILayout.LabelField($"BlendShare Patches ({buffers.Count} selected)", EditorStyles.boldLabel);
                if (anyReadOnly)
                {
                    EditorGUILayout.HelpBox("One or more selected patches are read-only; multi-edit is disabled.", MessageType.Info);
                }

                if (buffers.Count == 0) return;

                GameObject commonTarget = buffers[0].Settings.DefaultTargetFbx;
                bool mixed = false;
                for (int i = 1; i < buffers.Count; i++)
                {
                    if (buffers[i].Settings.DefaultTargetFbx != commonTarget)
                    {
                        mixed = true;
                        break;
                    }
                }

                using (new EditorGUI.DisabledScope(anyReadOnly))
                {
                    EditorGUI.showMixedValue = mixed;
                    EditorGUI.BeginChangeCheck();
                    var nextTarget = (GameObject)EditorGUILayout.ObjectField(
                        Localization.S("common.target_fbx"),
                        mixed ? null : commonTarget,
                        typeof(GameObject),
                        allowSceneObjects: false);
                    EditorGUI.showMixedValue = false;

                    if (EditorGUI.EndChangeCheck() && (!nextTarget || EditorWidgets.IsFBXGameObject(nextTarget)))
                    {
                        // Increment BEFORE capturing group so Undo never collapses into the preceding unrelated edit.
                        Undo.IncrementCurrentGroup();
                        int group = Undo.GetCurrentGroup();
                        Undo.SetCurrentGroupName("Set BlendShare Default Target FBX");

                        foreach (var buffer in buffers)
                        {
                            var context = new FeatureInspectorContext(null, buffer.AssetGuid, null, buffer, anyReadOnly);
                            context.RecordEditAndCommit("Set BlendShare Default Target FBX", settings => settings.DefaultTargetFbx = nextTarget);
                        }
                        Undo.CollapseUndoOperations(group);
                    }
                }

                EditorGUILayout.HelpBox("Per-mesh feature lists are read-only during multi-patch selection.", MessageType.Info);
            }
            finally
            {
                GUI.enabled = prevEnabled;
            }
        }

        public override bool HasModified()
        {
            foreach (string guid in m_TrackedGuids)
            {
                var buf = BlendShareSettingsSession.GetOrCreateBufferByGuid(guid);
                if (buf != null && buf.IsDirty) return true;
            }
            return false;
        }

        public override void SaveChanges()
        {
            FlushTrackedGuids();
            hasUnsavedChanges = HasModified();
        }

        public override void OnDisable()
        {
            // Destroy nested root editor first (without double-flushing); ImporterEditor is single teardown commit owner.
            if (m_RootEditor != null)
            {
                if (m_RootEditor is BlendShareAssetEditor rootEd) rootEd.SuppressFlushOnDisable = true;
                DestroyImmediate(m_RootEditor);
                m_RootEditor = null;
            }
            FlushTrackedGuids();
            hasUnsavedChanges = false;
            base.OnDisable();
        }

        private void RebuildTrackedGuids()
        {
            m_TrackedGuids.Clear();
            if (targets == null) return;
            for (int i = 0; i < targets.Length; i++)
            {
                if (targets[i] is BlendShareImporter imp && !string.IsNullOrEmpty(imp.assetPath))
                {
                    string guid = AssetDatabase.AssetPathToGUID(imp.assetPath);
                    if (!string.IsNullOrEmpty(guid)) m_TrackedGuids.Add(guid);
                }
            }
        }

        private void FlushTrackedGuids()
        {
            foreach (string guid in m_TrackedGuids)
            {
                BlendShareSettingsSession.Flush(guid);
            }
        }
    }
}
