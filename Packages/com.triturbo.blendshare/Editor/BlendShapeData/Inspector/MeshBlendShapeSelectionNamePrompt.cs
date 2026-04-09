using System;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShapeShare.BlendShapeData
{
    internal class MeshBlendShapeSelectionNamePrompt : EditorWindow
    {
        private string value;
        private Action<string> onSubmit;

        public static void Show(string title, string initialValue, Action<string> onSubmit)
        {
            var window = CreateInstance<MeshBlendShapeSelectionNamePrompt>();
            window.titleContent = new GUIContent(title);
            window.value = initialValue ?? string.Empty;
            window.onSubmit = onSubmit;
            window.minSize = new Vector2(320f, 86f);
            window.maxSize = window.minSize;
            window.ShowUtility();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Selection Name", EditorStyles.boldLabel);
            GUI.SetNextControlName("SelectionNameField");
            value = EditorGUILayout.TextField(value);

            GUILayout.Space(8f);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Cancel"))
            {
                Close();
                GUIUtility.ExitGUI();
            }

            EditorGUI.BeginDisabledGroup(string.IsNullOrWhiteSpace(value));
            if (GUILayout.Button("Save"))
            {
                string trimmedValue = value.Trim();
                Close();
                onSubmit?.Invoke(trimmedValue);
                GUIUtility.ExitGUI();
            }
            EditorGUI.EndDisabledGroup();
            GUILayout.EndHorizontal();

            if (Event.current.type == EventType.Repaint)
            {
                EditorGUI.FocusTextInControl("SelectionNameField");
            }
        }
    }
}
