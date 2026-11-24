using System;
using System.Collections.Generic;
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
    /// Inspector for a single selectable BlendShareMeshAsset sub-asset handle.
    /// Reads only this mesh's header and features via O(1) TOC offsets and edits the same shared UndoBuffer as the root patch.
    /// </summary>
    [CustomEditor(typeof(BlendShareMeshAsset))]
    public sealed class BlendShareMeshAssetEditor : UnityEditor.Editor
    {
        private string m_CachedGuid = string.Empty;
        private string m_CachedRevision = string.Empty;
        private string m_CachedMeshId = string.Empty;
        private MeshDocument m_CachedMeshHeader;
        private List<BlendShareFeatureData> m_CachedFeatures;
        private string m_LastReadError = string.Empty;

        private void OnEnable()
        {
            BlendShareSettingsSession.SettingsChanged += OnSettingsChanged;
        }

        private void OnDisable()
        {
            BlendShareSettingsSession.SettingsChanged -= OnSettingsChanged;
            if (!string.IsNullOrEmpty(m_CachedGuid))
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
            var meshHandle = target as BlendShareMeshAsset;
            if (meshHandle == null || meshHandle.Root == null)
            {
                EditorGUILayout.HelpBox("BlendShareMeshAsset is missing its root BlendShareAsset.", MessageType.Warning);
                return;
            }

            var root = meshHandle.Root;
            string assetPath = AssetDatabase.GetAssetPath(root);
            string guid = string.IsNullOrEmpty(assetPath) ? string.Empty : AssetDatabase.AssetPathToGUID(assetPath);
            m_CachedGuid = guid;

            bool isReadOnly = BlendShareSettingsSession.IsAssetReadOnly(assetPath, out string readOnlyReason);
            var buffer = !string.IsNullOrEmpty(guid) ? BlendShareSettingsSession.GetOrCreateBuffer(root) : null;

            bool prevEnabled = GUI.enabled;
            GUI.enabled = true;
            try
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField(meshHandle.name, EditorStyles.boldLabel);
                EditorGUILayout.LabelField("Mesh ID", meshHandle.MeshId);
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.ObjectField("Parent Patch", root, typeof(BlendShareAsset), allowSceneObjects: false);
                }
                EditorGUILayout.EndVertical();

                if (isReadOnly)
                {
                    EditorGUILayout.HelpBox($"Read-only patch ({readOnlyReason}).", MessageType.Info);
                }

                if (buffer != null && buffer.HasExternalConflict)
                {
                    BlendShareAssetEditor.DrawConflictBanner(guid);
                }

                if (buffer != null && !string.IsNullOrEmpty(buffer.LastCommitError))
                {
                    EditorGUILayout.HelpBox($"Settings save failed: {buffer.LastCommitError}", MessageType.Warning);
                }

                EnsureMeshLoaded(root, meshHandle.MeshId);
                if (!string.IsNullOrEmpty(m_LastReadError))
                {
                    EditorGUILayout.HelpBox(m_LastReadError, MessageType.Error);
                    return;
                }

                if (m_CachedMeshHeader != null && m_CachedMeshHeader.Topology != null)
                {
                    EditorGUILayout.LabelField(
                        "Topology",
                        $"CPs={m_CachedMeshHeader.Topology.ControlPointCount}, Faces={m_CachedMeshHeader.Topology.FaceCount}, Valid={m_CachedMeshHeader.Topology.IsValid}");
                }

                EditorGUILayout.Space(4);
                var ctx = new FeatureInspectorContext(root, guid, meshHandle.MeshId, buffer, isReadOnly);
                BlendShareAssetEditor.DrawFeaturesList(m_CachedFeatures, ctx);
            }
            finally
            {
                GUI.enabled = prevEnabled;
            }
        }

        private void EnsureMeshLoaded(BlendShareAsset root, string meshId)
        {
            if (m_CachedFeatures != null &&
                string.Equals(m_CachedRevision, root.SourceRevision, StringComparison.Ordinal) &&
                string.Equals(m_CachedMeshId, meshId, StringComparison.Ordinal))
            {
                return;
            }

            m_CachedRevision = root.SourceRevision;
            m_CachedMeshId = meshId;
            m_LastReadError = string.Empty;

            var headerRes = BlendShareSourceRepository.Shared.ReadMeshHeader(root, meshId);
            if (!headerRes.Success)
            {
                m_CachedMeshHeader = null;
                m_CachedFeatures = null;
                m_LastReadError = $"Failed to read mesh header: {headerRes.Status} - {headerRes.Message}";
                return;
            }

            m_CachedMeshHeader = headerRes.Value;
            var features = new List<BlendShareFeatureData>();
            if (root.TryGetMesh(meshId, out var meshToc))
            {
                for (int i = 0; i < meshToc.FeatureEntryIndices.Count; i++)
                {
                    int entryIdx = meshToc.FeatureEntryIndices[i];
                    if (entryIdx < 0 || entryIdx >= root.Entries.Count) continue;
                    string featureId = root.Entries[entryIdx].FeatureId;
                    var featRes = BlendShareSourceRepository.Shared.ReadFeature(root, meshId, featureId);
                    if (!featRes.Success)
                    {
                        m_CachedFeatures = null;
                        m_LastReadError = $"Failed to read feature '{featureId}': {featRes.Status} - {featRes.Message}";
                        return;
                    }
                    features.Add(featRes.Value);
                }
            }

            m_CachedFeatures = features;
        }
    }
}
