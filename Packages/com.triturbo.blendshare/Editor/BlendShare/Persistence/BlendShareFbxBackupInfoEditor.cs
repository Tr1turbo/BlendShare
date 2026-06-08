using Triturbo.BlendShapeShare.BlendShapeData;
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
            DrawObjectReference("Source FBX", info.m_SourceFbx);
            DrawDerivedFbxReferences(info);

            EditorGUILayout.Space();
            DrawReadOnlyText("Metadata Version", info.m_MetadataVersion.ToString());
            DrawReadOnlyText("Source FBX Hash", info.m_SourceFbxHash);
            DrawReadOnlyText("Created At UTC", info.m_CreatedAtUtc);
            DrawReadOnlyText("Original File Name", info.m_OriginalFileName);
            DrawReadOnlyText("Backup Path", info.m_BackupPath);
            DrawReadOnlyText("Backup Hash", info.m_BackupHash);
            DrawReadOnlyText("Backup Size Bytes", info.m_BackupSizeBytes.ToString());
        }

        private static void DrawDerivedFbxReferences(BlendShareFbxBackupInfo info)
        {
            var fbxs = info.m_DerivedFbxs ?? new GameObject[0];
            EditorGUILayout.LabelField("Derived FBXs", EditorStyles.boldLabel);
            if (fbxs.Length == 0)
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.TextField("None", string.Empty);
                }

                return;
            }

            for (int i = 0; i < fbxs.Length; i++)
            {
                DrawDerivedFbxReference($"FBX {i + 1}", fbxs[i], info.m_BackupPath);
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
                    if (GUILayout.Button("Select", GUILayout.Width(64)))
                    {
                        Selection.activeObject = value;
                        EditorGUIUtility.PingObject(value);
                    }

                    using (new EditorGUI.DisabledScope(!canRestoreFromBackup))
                    {
                        if (GUILayout.Button("Restore", GUILayout.Width(72)))
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
                    if (GUILayout.Button("Select", GUILayout.Width(64)))
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
                    "Restore FBX to Original",
                    $"This will restore '{targetName}' from this BlendShare backup and clear its active BlendShare patch metadata. The backup is deleted when no derived FBX still references it.",
                    "Restore",
                    "Cancel"))
            {
                return;
            }

            if (BlendShareGenerationService.RestoreToOriginal(targetFbx, out string message))
            {
                Debug.Log($"[BlendShare] {message}");
                return;
            }

            EditorUtility.DisplayDialog("Restore Failed", message, "OK");
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
