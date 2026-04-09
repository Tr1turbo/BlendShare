using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShapeShare.BlendShapeData
{
    [CustomEditor(typeof(MeshBlendShapeSelectionSO))]
    public class MeshBlendShapeSelectionSOEditor : Editor
    {
        private SerializedProperty meshNameProperty;
        private SerializedProperty blendShapeNamesProperty;

        private void OnEnable()
        {
            meshNameProperty = serializedObject.FindProperty(nameof(MeshBlendShapeSelectionSO.m_MeshName));
            blendShapeNamesProperty = serializedObject.FindProperty(nameof(MeshBlendShapeSelectionSO.m_BlendShapeNames));
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var selection = (MeshBlendShapeSelectionSO)target;

            EditorGUI.BeginChangeCheck();
            string updatedName = EditorGUILayout.TextField(new GUIContent("Name"), selection.name);
            if (EditorGUI.EndChangeCheck())
            {
                updatedName = updatedName?.Trim();
                if (!string.IsNullOrWhiteSpace(updatedName) &&
                    !string.Equals(updatedName, selection.name))
                {
                    selection.name = updatedName;
                    EditorUtility.SetDirty(selection);
                    AssetDatabase.SaveAssets();
                    GUIUtility.ExitGUI();
                }
            }


            EditorGUILayout.LabelField("BlendShape Selection", EditorStyles.boldLabel);
            EditorGUILayout.Space(4f);

            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.PropertyField(meshNameProperty, new GUIContent("Mesh Name"));
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.PropertyField(blendShapeNamesProperty, new GUIContent("BlendShape Names"), true);

            bool changed = serializedObject.ApplyModifiedProperties();

            if (changed)
            {
                EditorUtility.SetDirty(target);
                EditorUtility.SetDirty(selection);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }
        }
    }
}
