using System;
using System.Security.Cryptography;
using System.Text;
using Triturbo.BlendShapeShare.BlendShapeData;
using Triturbo.BlendShapeShare.Ndmf.Runtime;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShapeShare.Ndmf.Editor.Inspector
{
    [CustomEditor(typeof(AppendBlendShapes))]
    public class AppendBlendShapesEditor : UnityEditor.Editor
    {
        private SerializedProperty _propertyBlendShapeData, _propertyBlendShapeWeights;

        private void OnEnable()
        {
            _propertyBlendShapeData = serializedObject.FindProperty("blendShapeData");
            _propertyBlendShapeWeights = serializedObject.FindProperty("blendShapeWeights");
        }

        public override void OnInspectorGUI()
        {
            EditorWidgets.ShowBlendShareBanner();
            GUILayout.BeginHorizontal("box");
            GUILayout.FlexibleSpace();
            GUILayout.Label("Appends blend shapes");
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            serializedObject.Update();
            EditorGUILayout.Space();

            EditorGUILayout.BeginVertical();
            EditorGUILayout.PropertyField(_propertyBlendShapeData);
            EditorGUILayout.EndVertical();

            serializedObject.ApplyModifiedProperties();
        }
    }
}