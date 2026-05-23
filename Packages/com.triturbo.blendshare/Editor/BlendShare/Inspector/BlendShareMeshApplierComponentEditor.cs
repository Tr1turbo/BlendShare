using Triturbo.BlendShare.Components;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.Inspector
{
    [CustomEditor(typeof(BlendShareMeshApplierComponent))]
    public sealed class BlendShareMeshApplierComponentEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.LabelField("BlendShare Mesh Applier", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Owner"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_TargetRendererReference"), new GUIContent("Target Renderer"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_MeshData"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_RendererNodePath"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_EnabledForBuild"));

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_IsStale"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_DiagnosticMessage"));

            var applier = (BlendShareMeshApplierComponent)target;
            if (applier.IsStale && !string.IsNullOrWhiteSpace(applier.DiagnosticMessage))
            {
                EditorGUILayout.HelpBox(applier.DiagnosticMessage, MessageType.Warning);
            }

            serializedObject.ApplyModifiedProperties();
        }
    }
}
