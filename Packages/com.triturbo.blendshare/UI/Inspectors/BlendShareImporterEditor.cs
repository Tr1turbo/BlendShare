using System;
using System.Collections.Generic;
using Triturbo.BlendShare.EditorAssets;
using Triturbo.BlendShare.EditorAssets.Settings;
using Triturbo.BlendShare.Runtime.Assets;
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

        public override void OnInspectorGUI()
        {
            if (targets != null && targets.Length > 1)
            {
                DrawMultiSelectPatchInspector();
                return;
            }

            var importer = target as BlendShareImporter;
            if (importer == null) return;

            var patchAsset = AssetDatabase.LoadAssetAtPath<BlendShareAsset>(importer.assetPath);
            if (patchAsset == null)
            {
                EditorGUILayout.HelpBox("BlendShareAsset could not be loaded for this .blendshare file.", MessageType.Warning);
                return;
            }

            if (m_RootEditor == null || m_RootEditor.target != patchAsset)
            {
                if (m_RootEditor != null) DestroyImmediate(m_RootEditor);
                m_RootEditor = CreateEditor(patchAsset);
            }

            if (m_RootEditor != null)
            {
                m_RootEditor.OnInspectorGUI();
            }
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
                        "Default Target FBX",
                        mixed ? null : commonTarget,
                        typeof(GameObject),
                        allowSceneObjects: false);
                    EditorGUI.showMixedValue = false;

                    if (EditorGUI.EndChangeCheck())
                    {
                        // Increment BEFORE capturing group so Undo never collapses into the preceding unrelated edit.
                        Undo.IncrementCurrentGroup();
                        int group = Undo.GetCurrentGroup();
                        Undo.SetCurrentGroupName("Set BlendShare Default Target FBX");

                        var undoTargets = buffers.ToArray();
                        Undo.RecordObjects(undoTargets, "Set BlendShare Default Target FBX");
                        for (int i = 0; i < buffers.Count; i++)
                        {
                            buffers[i].Settings.DefaultTargetFbx = nextTarget;
                            EditorUtility.SetDirty(buffers[i]);
                            BlendShareSettingsSession.NotifyBufferMutated(buffers[i]);
                        }
                        Undo.CollapseUndoOperations(group);

                        // Defer reimports until after the current IMGUI event completes.
                        for (int i = 0; i < buffers.Count; i++)
                        {
                            FeatureInspectorContext.ScheduleDeferredFlush(buffers[i].AssetGuid);
                        }
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
