using Triturbo.BlendShapeShare.BlendShapeData;
using Triturbo.BlendShare.Core;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Triturbo.BlendShare.Inspector
{
    [CustomPropertyDrawer(typeof(FbxAssetAttribute))]
    internal sealed class FbxAssetDrawer : PropertyDrawer
    {
        public override VisualElement CreatePropertyGUI(SerializedProperty property)
        {
            if (property.propertyType != SerializedPropertyType.ObjectReference)
            {
                return new Label("Use FbxAsset with object references.");
            }

            var field = new ObjectField
            {
                objectType = typeof(GameObject),
                allowSceneObjects = false,
                value = property.objectReferenceValue
            };
            field.BindProperty(property);
            field.RegisterValueChangedCallback(evt =>
            {
                var previous = evt.previousValue;
                var next = evt.newValue;
                if (next == null || EditorWidgets.IsFBXGameObject(next))
                {
                    return;
                }

                field.SetValueWithoutNotify(previous);
                property.objectReferenceValue = previous;
                property.serializedObject.ApplyModifiedProperties();
            });
            return field;
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            label = EditorGUI.BeginProperty(position, label, property);
            try
            {
                if (property.propertyType != SerializedPropertyType.ObjectReference)
                {
                    EditorGUI.LabelField(position, label.text, "Use FbxAsset with object references.");
                    return;
                }

                var current = property.objectReferenceValue;
                EditorGUI.BeginChangeCheck();
                var updated = EditorGUI.ObjectField(position, label, current, typeof(GameObject), false);
                if (!EditorGUI.EndChangeCheck())
                {
                    return;
                }

                property.objectReferenceValue = updated == null || EditorWidgets.IsFBXGameObject(updated)
                    ? updated
                    : current;
            }
            finally
            {
                EditorGUI.EndProperty();
            }
        }
    }
}
