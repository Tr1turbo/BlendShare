using Triturbo.BlendShare.Fbx;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Triturbo.BlendShare.Inspector
{
    [CustomPropertyDrawer(typeof(FbxMatrix4x4))]
    public sealed class FbxMatrix4x4Drawer : PropertyDrawer
    {
        private static readonly string[,] FieldNames =
        {
            { "m00", "m01", "m02", "m03" },
            { "m10", "m11", "m12", "m13" },
            { "m20", "m21", "m22", "m23" },
            { "m30", "m31", "m32", "m33" }
        };

        public override VisualElement CreatePropertyGUI(SerializedProperty property)
        {
            var foldout = new Foldout
            {
                text = property.displayName,
                value = false
            };

            for (int row = 0; row < 4; row++)
            {
                foldout.Add(CreateMatrixRow(property, row));
            }

            return foldout;
        }

        private static VisualElement CreateMatrixRow(SerializedProperty property, int rowIndex)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginLeft = 15;
            row.style.marginTop = 1;
            row.style.marginBottom = 1;
            row.style.flexGrow = 1;
            row.style.flexShrink = 1;
            row.style.minWidth = 0;

            for (int column = 0; column < 4; column++)
            {
                row.Add(CreateCell(property.FindPropertyRelative(FieldNames[rowIndex, column])));
            }

            return row;
        }

        private static FloatField CreateCell(SerializedProperty valueProperty)
        {
            var field = new FloatField
            {
                value = valueProperty != null ? NormalizeFloat((float)valueProperty.doubleValue) : 0f
            };
            field.labelElement.style.display = DisplayStyle.None;
            field.style.flexGrow = 1;
            field.style.flexShrink = 1;
            field.style.minWidth = 0;
            field.style.marginRight = 3;
            field.SetEnabled(false);

            if (valueProperty != null)
            {
                field.RegisterValueChangedCallback(evt =>
                {
                    valueProperty.doubleValue = evt.newValue;
                    valueProperty.serializedObject.ApplyModifiedProperties();
                });
            }

            return field;
        }

        private static float NormalizeFloat(float value)
        {
            return Mathf.Abs(value) < 0.0000005f ? 0f : value;
        }
    }
}
