using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShapeShare.BlendShapeData
{
    [CustomEditor(typeof(BlendShapeSelectionSO))]
    public class BlendShapeSelectionSOEditor : Editor
    {
        private SerializedProperty meshNameProperty;
        private SerializedProperty blendShapeNamesProperty;

        private void OnEnable()
        {
            meshNameProperty = serializedObject.FindProperty(nameof(BlendShapeSelectionSO.m_MeshName));
            blendShapeNamesProperty = serializedObject.FindProperty(nameof(BlendShapeSelectionSO.m_BlendShapeNames));
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var selection = (BlendShapeSelectionSO)target;
            
            Localization.DrawLanguageSelection();
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(Localization.G("blendshape_selection.title"), EditorStyles.boldLabel);
            EditorGUILayout.Space(4f);

            EditorGUI.BeginChangeCheck();
            string updatedName = EditorGUILayout.TextField(Localization.G("blendshape_selection.name"), selection.name);
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
            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.PropertyField(meshNameProperty, Localization.G("blendshape_selection.mesh_name"));
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.PropertyField(blendShapeNamesProperty, Localization.G("blendshape_selection.blendshape_names"), true);

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
