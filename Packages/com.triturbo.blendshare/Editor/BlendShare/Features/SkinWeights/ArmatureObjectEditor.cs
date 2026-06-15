using System;
using System.Linq;
using Triturbo.BlendShare.Inspector;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Triturbo.BlendShare.Features.SkinWeights.Editor
{
    [CustomEditor(typeof(ArmatureObject))]
    public sealed class ArmatureObjectEditor : UnityEditor.Editor
    {
        private const float TransformLabelWidth = 130f;
        private const float AxisLabelWidth = 14f;

        public override VisualElement CreateInspectorGUI()
        {
            var root = BlendShareInspectorUi.CreateRoot();

            ArmatureObject armature = target as ArmatureObject;
            var titleLabel = new Label("Armature");
            titleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            titleLabel.style.marginBottom = 4;
            root.Add(titleLabel);

            if (armature == null)
            {
                root.Add(new HelpBox("Armature data is missing.", HelpBoxMessageType.Warning));
                return root;
            }

            BlendShareInspectorUi.RegisterDoubleClickAction(root, () =>
            {
                Selection.activeObject = armature;
                EditorGUIUtility.PingObject(armature);
            });

            var bones = armature.Bones ?? Array.Empty<ArmatureBoneData>();
            root.Add(BlendShareInspectorUi.Row("Bones", armature.BoneCount.ToString()));
            root.Add(CreateBonesFoldout(bones));
            return root;
        }

        private static VisualElement CreateBonesFoldout(System.Collections.Generic.IReadOnlyList<ArmatureBoneData> bones)
        {
            var foldout = new Foldout
            {
                text = "Bones",
                value = false
            };

            if (bones.Count == 0)
            {
                foldout.Add(new HelpBox("No bones are stored for this armature.", HelpBoxMessageType.Info));
                return foldout;
            }

            foreach (var bone in bones.Where(bone => bone != null))
            {
                var item = SkinWeightInspectorLayout.CreatePlainItem();
                item.Add(BlendShareInspectorUi.Row("Path", bone.Path));
                item.Add(BlendShareInspectorUi.Row("Parent", bone.ParentPath));
                item.Add(BlendShareInspectorUi.Row("Create If Missing", bone.m_CreateIfMissing ? "Yes" : "No"));
                item.Add(CreateTransformRows(bone));
                foldout.Add(item);
            }

            return foldout;
        }

        private static VisualElement CreateTransformRows(ArmatureBoneData bone)
        {
            var root = new VisualElement();
            root.style.marginTop = 2;
            root.Add(CreateTransformRow("Position", bone.m_FbxLocalTranslation));
            root.Add(CreateTransformRow("Rotation", bone.m_FbxLocalEulerRotation));
            root.Add(CreateTransformRow("Scale", bone.m_FbxLocalScale == Vector3.zero ? Vector3.one : bone.m_FbxLocalScale));
            return root;
        }

        private static VisualElement CreateTransformRow(string label, Vector3 value)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginTop = 1;
            row.style.marginBottom = 1;
            row.style.flexGrow = 1;
            row.style.flexShrink = 1;
            row.style.minWidth = 0;

            var labelElement = new Label(label);
            labelElement.style.width = TransformLabelWidth;
            labelElement.style.minWidth = TransformLabelWidth;
            labelElement.style.opacity = 0.72f;
            row.Add(labelElement);

            row.Add(CreateAxisField("X", value.x));
            row.Add(CreateAxisField("Y", value.y));
            row.Add(CreateAxisField("Z", value.z));
            return row;
        }

        private static VisualElement CreateAxisField(string axis, float value)
        {
            var group = new VisualElement();
            group.style.flexDirection = FlexDirection.Row;
            group.style.alignItems = Align.Center;
            group.style.marginRight = 6;
            group.style.flexGrow = 1;
            group.style.flexShrink = 1;
            group.style.minWidth = 0;

            var axisLabel = new Label(axis);
            axisLabel.style.width = AxisLabelWidth;
            axisLabel.style.minWidth = AxisLabelWidth;
            axisLabel.style.unityTextAlign = TextAnchor.MiddleRight;
            axisLabel.style.marginRight = 3;
            group.Add(axisLabel);

            var field = new FloatField
            {
                value = NormalizeFloat(value)
            };
            field.labelElement.style.display = DisplayStyle.None;
            field.style.flexGrow = 1;
            field.style.flexShrink = 1;
            field.style.minWidth = 0;
            field.SetEnabled(false);
            group.Add(field);
            return group;
        }

        private static float NormalizeFloat(float value)
        {
            return Mathf.Abs(value) < 0.0000005f ? 0f : value;
        }
    }
}
