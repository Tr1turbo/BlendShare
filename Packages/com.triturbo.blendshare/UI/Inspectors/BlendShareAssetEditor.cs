using System;
using System.Collections.Generic;
using Triturbo.BlendShare.Container.Format;
using Triturbo.BlendShare.EditorAssets;
using Triturbo.BlendShare.EditorAssets.Settings;
using Triturbo.BlendShare.Model.Documents;
using Triturbo.BlendShare.Model.Features;
using Triturbo.BlendShare.Runtime.Assets;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.UI.Inspectors
{
    /// <summary>
    /// Inspector for the ultra-thin BlendShareAsset root shell.
    /// Explicitly re-enables GUI over Unity's NotEditable imported object state and binds all edits strictly to BlendShareSettingsUndoBuffer.
    /// </summary>
    [CustomEditor(typeof(BlendShareAsset))]
    public sealed class BlendShareAssetEditor : UnityEditor.Editor
    {
        private string m_CachedGuid = string.Empty;
        private string m_CachedRevision = string.Empty;
        private PatchDocument m_CachedDocument;
        private string m_LastReadError = string.Empty;
        private readonly Dictionary<string, bool> m_MeshFoldouts = new Dictionary<string, bool>(StringComparer.Ordinal);

        internal bool SuppressFlushOnDisable;

        private void OnEnable()
        {
            BlendShareSettingsSession.SettingsChanged += OnSettingsChanged;
        }

        private void OnDisable()
        {
            BlendShareSettingsSession.SettingsChanged -= OnSettingsChanged;
            if (!SuppressFlushOnDisable && !string.IsNullOrEmpty(m_CachedGuid))
            {
                BlendShareSettingsSession.Flush(m_CachedGuid);
            }
        }

        private void OnSettingsChanged(string guid, long revision)
        {
            if (string.Equals(guid, m_CachedGuid, StringComparison.Ordinal))
            {
                Repaint();
            }
        }

        public override void OnInspectorGUI()
        {
            var patch = target as BlendShareAsset;
            if (patch == null) return;

            string assetPath = AssetDatabase.GetAssetPath(patch);
            string guid = string.IsNullOrEmpty(assetPath) ? string.Empty : AssetDatabase.AssetPathToGUID(assetPath);
            m_CachedGuid = guid;

            bool isReadOnly = BlendShareSettingsSession.IsAssetReadOnly(assetPath, out string readOnlyReason);
            var buffer = !string.IsNullOrEmpty(guid) ? BlendShareSettingsSession.GetOrCreateBuffer(patch) : null;

            bool prevEnabled = GUI.enabled;
            GUI.enabled = true; // Imported ScriptableObjects are NotEditable by default; edits target UndoBuffer.
            try
            {
                DrawHeaderSummary(patch, assetPath);

                if (isReadOnly)
                {
                    EditorGUILayout.HelpBox($"Read-only patch ({readOnlyReason}). Settings edits cannot be written to .blendshare.meta.", MessageType.Info);
                }

                if (buffer != null && buffer.HasExternalConflict)
                {
                    DrawConflictBanner(guid);
                }

                if (buffer != null && !string.IsNullOrEmpty(buffer.LastCommitError))
                {
                    EditorGUILayout.HelpBox($"Settings save failed: {buffer.LastCommitError}", MessageType.Warning);
                }

                if (buffer != null)
                {
                    DrawPatchSettings(buffer, guid, isReadOnly);
                }

                EditorGUILayout.Space(6);
                EnsureDocumentLoaded(patch);

                if (!string.IsNullOrEmpty(m_LastReadError))
                {
                    EditorGUILayout.HelpBox(m_LastReadError, MessageType.Error);
                    return;
                }

                if (m_CachedDocument == null) return;

                EditorGUILayout.LabelField($"Meshes ({m_CachedDocument.Meshes.Count})", EditorStyles.boldLabel);
                for (int i = 0; i < m_CachedDocument.Meshes.Count; i++)
                {
                    var mesh = m_CachedDocument.Meshes[i];
                    if (mesh == null) continue;
                    DrawMeshSection(patch, guid, buffer, isReadOnly, mesh);
                }
            }
            finally
            {
                GUI.enabled = prevEnabled;
            }
        }

        private static void DrawHeaderSummary(BlendShareAsset patch, string assetPath)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(string.IsNullOrEmpty(patch.DisplayName) ? patch.name : patch.DisplayName, EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Patch ID", patch.PatchId);
            string shortRev = patch.SourceRevision.Length > 16 ? patch.SourceRevision.Substring(0, 16) + "..." : patch.SourceRevision;
            EditorGUILayout.LabelField("Format / Revision", $"BLSHARE1 v{patch.FormatVersion} ({shortRev})");
            EditorGUILayout.LabelField("Container Size", $"{patch.TotalFileLength:N0} bytes ({patch.Entries.Count} TOC entries)");
            EditorGUILayout.EndVertical();
        }

        internal static void DrawConflictBanner(string guid)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.HelpBox(
                "External changes to .blendshare.meta were detected while local edits were pending. Choose how to resolve:",
                MessageType.Warning);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Reload From Disk"))
            {
                BlendShareSettingsSession.ReloadFromDiskAndDiscardConflict(guid);
            }
            if (GUILayout.Button("Keep Local & Overwrite Disk"))
            {
                BlendShareSettingsSession.ForceOverwriteDiskFromBuffer(guid);
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        private static void DrawPatchSettings(BlendShareSettingsUndoBuffer buffer, string guid, bool isReadOnly)
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Project Settings (.blendshare.meta)", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(isReadOnly))
            {
                EditorGUI.BeginChangeCheck();
                var nextFbx = (GameObject)EditorGUILayout.ObjectField(
                    "Default Target FBX",
                    buffer.Settings.DefaultTargetFbx,
                    typeof(GameObject),
                    allowSceneObjects: false);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(buffer, "Set Default Target FBX");
                    buffer.Settings.DefaultTargetFbx = nextFbx;
                    EditorUtility.SetDirty(buffer);
                    BlendShareSettingsSession.NotifyBufferMutated(buffer);
                    FeatureInspectorContext.ScheduleDeferredFlush(guid);
                }
            }
        }

        private void DrawMeshSection(
            BlendShareAsset patch,
            string guid,
            BlendShareSettingsUndoBuffer buffer,
            bool isReadOnly,
            MeshDocument mesh)
        {
            if (!m_MeshFoldouts.TryGetValue(mesh.MeshId, out bool expanded))
            {
                expanded = true;
                m_MeshFoldouts[mesh.MeshId] = expanded;
            }

            string title = string.IsNullOrEmpty(mesh.Name) ? mesh.MeshId : $"{mesh.Name} ({mesh.MeshId})";
            expanded = EditorGUILayout.Foldout(expanded, title, toggleOnLabelClick: true);
            m_MeshFoldouts[mesh.MeshId] = expanded;
            if (!expanded) return;

            EditorGUI.indentLevel++;
            var ctx = new FeatureInspectorContext(patch, guid, mesh.MeshId, buffer, isReadOnly);
            DrawFeaturesList(mesh.Features, ctx);
            EditorGUI.indentLevel--;
        }

        internal static void DrawFeaturesList(List<BlendShareFeatureData> features, FeatureInspectorContext ctx)
        {
            if (features == null || features.Count == 0)
            {
                EditorGUILayout.HelpBox("No features in this mesh.", MessageType.Info);
                return;
            }

            var ordered = new List<(BlendShareFeatureData Feature, IFeatureInspectorDrawer Drawer)>(features.Count);
            for (int i = 0; i < features.Count; i++)
            {
                var feat = features[i];
                if (feat == null) continue;
                ordered.Add((feat, FeatureInspectorRegistry.Resolve(feat)));
            }
            ordered.Sort((a, b) => a.Drawer.DisplayOrder.CompareTo(b.Drawer.DisplayOrder));

            for (int i = 0; i < ordered.Count; i++)
            {
                ordered[i].Drawer.DrawInspector(ordered[i].Feature, ctx);
            }
        }

        private void EnsureDocumentLoaded(BlendShareAsset patch)
        {
            if (m_CachedDocument != null && string.Equals(m_CachedRevision, patch.SourceRevision, StringComparison.Ordinal))
            {
                return;
            }

            var result = BlendShareSourceRepository.Shared.ReadPatchDocument(patch);
            m_CachedRevision = patch.SourceRevision;
            if (result.Success)
            {
                m_CachedDocument = result.Value;
                m_LastReadError = string.Empty;
            }
            else
            {
                m_CachedDocument = null;
                m_LastReadError = $"Failed to read patch container: {result.Status} - {result.Message}";
            }
        }
    }
}
