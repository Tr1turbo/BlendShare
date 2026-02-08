using Triturbo.BlendShapeShare.BlendShapeData;
using Triturbo.BlendShapeShare.Ndmf.Runtime;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace Triturbo.BlendShapeShare.Ndmf.Editor.Inspector
{
    [CustomEditor(typeof(AppendBlendShapes))]
    public class AppendBlendShapesEditor : UnityEditor.Editor
    {
        private SerializedProperty _propertyBlendShapeDataEntries, _propertyTarget;
        private ReorderableList _reorderableList;

        private void OnEnable()
        {
            _propertyBlendShapeDataEntries = serializedObject.FindProperty("blendShapeDataEntries");
            _propertyTarget = serializedObject.FindProperty("target");
            
            SetupReorderableList();
            
            Localization.OnLangChange += OnLanguageChange;
        }

        private void OnDisable()
        {
            Localization.OnLangChange -= OnLanguageChange;
        }

        private void OnLanguageChange()
        {
            SetupReorderableList();
            Repaint();
        }

        private float CalculateEntryHeight(int index)
        {
            var element = _propertyBlendShapeDataEntries.GetArrayElementAtIndex(index);
            var isExpandedProp = element.FindPropertyRelative("isExpanded");
            var meshWeightGroupsProp = element.FindPropertyRelative("meshWeightGroups");
            
            var lineHeight = EditorGUIUtility.singleLineHeight;
            var height = lineHeight + 6;
            
            if (!isExpandedProp.boolValue || meshWeightGroupsProp.arraySize == 0)
                return height;
            
            for (var i = 0; i < meshWeightGroupsProp.arraySize; i++)
            {
                var meshGroup = meshWeightGroupsProp.GetArrayElementAtIndex(i);
                var meshIsExpanded = meshGroup.FindPropertyRelative("isExpanded");
                var weightsProp = meshGroup.FindPropertyRelative("weights");
                
                height += lineHeight + 4;
                
                if (meshIsExpanded.boolValue)
                {
                    height += weightsProp.arraySize * (lineHeight + 2);
                }
            }
            
            return height + 4;
        }

        private void SetupReorderableList()
        {
            _reorderableList = new ReorderableList(serializedObject, _propertyBlendShapeDataEntries, true, true, true, true)
            {
                drawHeaderCallback = rect =>
                {
                    EditorGUI.LabelField(rect, Localization.G("append.blendshape_data_entries"));
                },
                elementHeightCallback = CalculateEntryHeight,
                drawElementCallback = DrawEntryElement,
                onAddCallback = list =>
                {
                    var index = list.serializedProperty.arraySize;
                    list.serializedProperty.arraySize++;
                    list.index = index;
            
                    var element = list.serializedProperty.GetArrayElementAtIndex(index);
                    element.FindPropertyRelative("blendShapeData").objectReferenceValue = null;
                    element.FindPropertyRelative("isExpanded").boolValue = true;
                    element.FindPropertyRelative("meshWeightGroups").ClearArray();
                }
            };
        }

        private void DrawEntryElement(Rect rect, int index, bool isActive, bool isFocused)
        {
            var element = _propertyBlendShapeDataEntries.GetArrayElementAtIndex(index);
            var blendShapeDataProp = element.FindPropertyRelative("blendShapeData");
            var isExpandedProp = element.FindPropertyRelative("isExpanded");
            var meshWeightGroupsProp = element.FindPropertyRelative("meshWeightGroups");

            rect.y += 2;
            var lineHeight = EditorGUIUtility.singleLineHeight;

            var dataRect = new Rect(rect.x, rect.y, rect.width, lineHeight);
            EditorGUI.BeginChangeCheck();
            EditorGUI.PropertyField(dataRect, blendShapeDataProp, Localization.G("append.blendshape_data"));
            if (EditorGUI.EndChangeCheck())
            {
                serializedObject.ApplyModifiedProperties();
                SyncWeightsForEntry(index);
                serializedObject.Update();
            }

            rect.y += lineHeight + 4;

            if (meshWeightGroupsProp.arraySize == 0)
                return;

            for (var meshIndex = 0; meshIndex < meshWeightGroupsProp.arraySize; meshIndex++)
            {
                var meshGroup = meshWeightGroupsProp.GetArrayElementAtIndex(meshIndex);
                var meshNameProp = meshGroup.FindPropertyRelative("meshName");
                var meshIsExpandedProp = meshGroup.FindPropertyRelative("isExpanded");
                var weightsProp = meshGroup.FindPropertyRelative("weights");

                var meshFoldoutRect = new Rect(rect.x + 15, rect.y, rect.width - 15, lineHeight);
                var meshLabel = $"{meshNameProp.stringValue} ({weightsProp.arraySize})";
                meshIsExpandedProp.boolValue = EditorGUI.Foldout(meshFoldoutRect, meshIsExpandedProp.boolValue, meshLabel, true);
                rect.y += lineHeight + 4;

                if (!meshIsExpandedProp.boolValue)
                    continue;

                EditorGUI.indentLevel += 2;
                for (var weightIndex = 0; weightIndex < weightsProp.arraySize; weightIndex++)
                {
                    var weightEntry = weightsProp.GetArrayElementAtIndex(weightIndex);
                    var shapeNameProp = weightEntry.FindPropertyRelative("shapeName");
                    var weightValueProp = weightEntry.FindPropertyRelative("weight");

                    var sliderRect = new Rect(rect.x + 30, rect.y, rect.width - 30, lineHeight);

                    var shapeName = shapeNameProp.stringValue;
                    if (string.IsNullOrEmpty(shapeName))
                        shapeName = Localization.S_f("append.shape_default", weightIndex.ToString());

                    weightValueProp.floatValue = EditorGUI.Slider(sliderRect, shapeName, weightValueProp.floatValue, 0f, 100f);
                    rect.y += lineHeight + 2;
                }
                EditorGUI.indentLevel -= 2;
            }
        }

        private void SyncWeightsForEntry(int index)
        {
            var component = (AppendBlendShapes)target;
            if (index < 0 || index >= component.blendShapeDataEntries.Count)
                return;
            
            component.blendShapeDataEntries[index].SyncWeightsWithData();
            EditorUtility.SetDirty(target);
        }

        public override void OnInspectorGUI()
        {
            EditorWidgets.ShowBlendShareBanner();
            GUILayout.BeginHorizontal("box");
            GUILayout.FlexibleSpace();
            GUILayout.Label(Localization.S("append.description"));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            serializedObject.Update();
            EditorGUILayout.Space();

            EditorGUILayout.BeginVertical();
            EditorGUILayout.PropertyField(_propertyTarget, Localization.G("append.target"));
            EditorGUILayout.Space();
            
            _reorderableList.DoLayoutList();
            
            EditorGUILayout.EndVertical();

            serializedObject.ApplyModifiedProperties();
        }
    }
}