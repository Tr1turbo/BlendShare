using Triturbo.BlendShapeShare.BlendShapeData;
using Triturbo.BlendShapeShare;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.Persistence
{
    [CustomEditor(typeof(BlendShareFbxBackupInfo))]
    public sealed class BlendShareFbxBackupInfoEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var info = (BlendShareFbxBackupInfo)target;
    
            bool wasEnabled = GUI.enabled;
            GUI.enabled = true;
            EditorWidgets.ShowBlendShareBanner();
            GUI.enabled = wasEnabled;


            DrawInfo(info);
        }

        internal static void DrawInfo(BlendShareFbxBackupInfo info)
        {
            DrawObjectReference(Localization.S("backup.source_fbx"), info.m_SourceFbx);
            DrawDerivedFbxReferences(info);

            EditorGUILayout.Space();
            DrawReadOnlyText(Localization.S("backup.metadata_version"), info.m_MetadataVersion.ToString());
            DrawReadOnlyText(Localization.S("backup.source_fbx_hash"), info.m_SourceFbxHash);
            DrawReadOnlyText(Localization.S("backup.created_at_utc"), info.m_CreatedAtUtc);
            DrawReadOnlyText(Localization.S("backup.original_file_name"), info.m_OriginalFileName);
            DrawReadOnlyText(Localization.S("backup.path"), info.m_BackupPath);
            DrawReadOnlyText(Localization.S("backup.hash"), info.m_BackupHash);
            DrawReadOnlyText(Localization.S("backup.size_bytes"), info.m_BackupSizeBytes.ToString());
        }

        private static void DrawDerivedFbxReferences(BlendShareFbxBackupInfo info)
        {
            var fbxs = info.m_DerivedFbxs ?? new GameObject[0];
            EditorGUILayout.LabelField(Localization.S("backup.derived_fbxs"), EditorStyles.boldLabel);
            if (fbxs.Length == 0)
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.TextField(Localization.S("common.none"), string.Empty);
                }

                return;
            }

            for (int i = 0; i < fbxs.Length; i++)
            {
                DrawDerivedFbxReference(Localization.SF("backup.derived_fbx", i + 1), fbxs[i], info.m_BackupPath);
            }
        }

        private static void DrawDerivedFbxReference(string label, GameObject value, string backupPath)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.ObjectField(label, value, typeof(GameObject), false);
                }

                bool wasEnabled = GUI.enabled;
                GUI.enabled = value != null;
                try
                {
                    bool canRestoreFromBackup = CanRestoreFromBackup(value, backupPath);
                    if (GUILayout.Button(Localization.S("common.select"), GUILayout.Width(64)))
                    {
                        Selection.activeObject = value;
                        EditorGUIUtility.PingObject(value);
                    }

                    using (new EditorGUI.DisabledScope(!canRestoreFromBackup))
                    {
                        if (GUILayout.Button(Localization.S("common.restore"), GUILayout.Width(72)))
                        {
                            RunRestoreToOriginal(value);
                        }
                    }
                }
                finally
                {
                    GUI.enabled = wasEnabled;
                }
            }
        }

        private static bool CanRestoreFromBackup(GameObject value, string backupPath)
        {
            return value != null && BlendShareFbxMetadataService.UsesBaselineBackup(value, backupPath);
        }

        private static void DrawObjectReference<T>(string label, T value)
            where T : UnityEngine.Object
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.ObjectField(label, value, typeof(T), false);
                }

                bool wasEnabled = GUI.enabled;
                GUI.enabled = value != null;
                try
                {
                    if (GUILayout.Button(Localization.S("common.select"), GUILayout.Width(64)))
                    {
                        Selection.activeObject = value;
                        EditorGUIUtility.PingObject(value);
                    }
                }
                finally
                {
                    GUI.enabled = wasEnabled;
                }
            }
        }

        private static void RunRestoreToOriginal(GameObject targetFbx)
        {
            if (targetFbx == null)
            {
                return;
            }

            string targetName = targetFbx.name;
            if (!EditorUtility.DisplayDialog(
                    Localization.S("backup.restore.title"),
                    Localization.SF("backup.restore.message", targetName),
                    Localization.S("common.restore"),
                    Localization.S("common.cancel")))
            {
                return;
            }

            if (BlendShareGenerationService.RestoreToOriginal(targetFbx, out string message))
            {
                Debug.Log($"[BlendShare] {message}");
                return;
            }

            EditorUtility.DisplayDialog(Localization.S("backup.restore.failed"), message, Localization.S("common.ok"));
        }

        private static void DrawReadOnlyText(string label, string value)
        {
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.TextField(label, value ?? string.Empty);
            }
        }

    }

    [CustomEditor(typeof(BlendShareFbxBackupImporter))]
    public sealed class BlendShareFbxBackupImporterEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            //Empty
        }
    }
}
