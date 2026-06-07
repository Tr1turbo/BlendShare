using Triturbo.BlendShapeShare.BlendShapeData;
using Triturbo.BlendShare.Core;
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
            DrawObjectReference("Source FBX", ResolveSourceFbx(info));
            DrawPatchReferences(info);

            EditorGUILayout.Space();
            DrawRestoreButton(info);

            EditorGUILayout.Space();
            DrawReadOnlyText("Metadata Version", info.m_MetadataVersion.ToString());
            DrawReadOnlyText("Source FBX GUID", info.m_SourceFbxGuid);
            DrawReadOnlyText("Source FBX Path", info.m_SourceFbxPath);
            DrawReadOnlyText("Source FBX Hash", info.m_SourceFbxHash);
            DrawReadOnlyList("BlendShare GUIDs", info.m_BlendShareGuids);
            DrawReadOnlyText("Created At UTC", info.m_CreatedAtUtc);
            DrawReadOnlyText("Original File Name", info.m_OriginalFileName);
            DrawReadOnlyText("Backup Path", info.m_BackupPath);
            DrawReadOnlyText("Backup Hash", info.m_BackupHash);
            DrawReadOnlyText("Backup Size Bytes", info.m_BackupSizeBytes.ToString());
            DrawReadOnlyText("Import Message", info.m_ImportMessage);
        }

        private static void DrawPatchReferences(BlendShareFbxBackupInfo info)
        {
            var patches = info.m_BlendSharePatches ?? new BlendShareObject[0];
            EditorGUILayout.LabelField("BlendShare Patches", EditorStyles.boldLabel);
            if (patches.Length == 0)
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.TextField("None", string.Empty);
                }

                return;
            }

            for (int i = 0; i < patches.Length; i++)
            {
                DrawObjectReference($"Patch {i + 1}", patches[i]);
            }
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

        private static void DrawRestoreButton(BlendShareFbxBackupInfo info)
        {
            GameObject sourceFbx = ResolveSourceFbx(info);
            bool wasEnabled = GUI.enabled;
            GUI.enabled = sourceFbx != null;
            bool clicked;
            try
            {
                clicked = GUILayout.Button("Restore Source FBX to Original");
            }
            finally
            {
                GUI.enabled = wasEnabled;
            }

            if (!clicked)
            {
                return;
            }

            if (!EditorUtility.DisplayDialog(
                    "Restore Source FBX to Original",
                    "This will restore the source FBX from this BlendShare backup, clear active BlendShare patch metadata, and prune the backup on success.",
                    "Restore",
                    "Cancel"))
            {
                return;
            }

            if (BlendShareGenerationService.RestoreToOriginal(sourceFbx, out string message))
            {
                Debug.Log($"[BlendShare] {message}");
                return;
            }

            EditorUtility.DisplayDialog("Restore Failed", message, "OK");
        }

        private static GameObject ResolveSourceFbx(BlendShareFbxBackupInfo info)
        {
            if (info.m_SourceFbx != null)
            {
                return info.m_SourceFbx;
            }

            string sourcePath = string.IsNullOrEmpty(info.m_SourceFbxGuid)
                ? string.Empty
                : AssetDatabase.GUIDToAssetPath(info.m_SourceFbxGuid);
            if (string.IsNullOrEmpty(sourcePath))
            {
                sourcePath = info.m_SourceFbxPath;
            }

            return string.IsNullOrEmpty(sourcePath)
                ? null
                : AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath);
        }

        private static void DrawReadOnlyText(string label, string value)
        {
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.TextField(label, value ?? string.Empty);
            }
        }

        private static void DrawReadOnlyList(string label, string[] values)
        {
            EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
            values ??= new string[0];
            if (values.Length == 0)
            {
                DrawReadOnlyText("None", string.Empty);
                return;
            }

            for (int i = 0; i < values.Length; i++)
            {
                DrawReadOnlyText($"Element {i}", values[i]);
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
