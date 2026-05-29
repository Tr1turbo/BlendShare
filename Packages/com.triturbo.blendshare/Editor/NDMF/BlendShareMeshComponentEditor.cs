using Triturbo.BlendShare.Components;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.NDMF
{
    [CustomEditor(typeof(BlendShareMeshComponent))]
    public sealed class BlendShareMeshComponentEditor : UnityEditor.Editor
    {
        static BlendShareMeshComponentEditor()
        {
            EditorApplication.contextualPropertyMenu -= AddBlendShapeAnimationMenuItems;
            EditorApplication.contextualPropertyMenu += AddBlendShapeAnimationMenuItems;
        }

        public override void OnInspectorGUI()
        {
            var applier = (BlendShareMeshComponent)target;
            if (applier.SyncActiveBlendShapeWeights())
            {
                EditorUtility.SetDirty(applier);
            }

            serializedObject.Update();

            EditorGUILayout.LabelField("BlendShare Mesh Applier", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Owner"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_TargetRendererReference"), new GUIContent("Target Renderer"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_MeshData"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_EnabledForBuild"));

            DrawBlendShapeWeights(applier, serializedObject.FindProperty(BlendShareMeshComponent.BlendShapeWeightsFieldName));

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_DiagnosticMessage"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_BoneProxyBindings"), true);

            if (!BlendShareComponentSetupService.ValidateMeshApplierMapping(applier, out string mappingDiagnostic))
            {
                bool showMappingError = true;
                if (BlendShareComponentSetupService.TryGetCachedInvalidMappingDiagnostic(applier, out string cachedDiagnostic))
                {
                    mappingDiagnostic = cachedDiagnostic;
                }
                else if (BlendShareComponentSetupService.EnsureMeshApplierMappingCache(applier, out string createdDiagnostic))
                {
                    showMappingError = false;
                }
                else if (BlendShareComponentSetupService.TryGetCachedInvalidMappingDiagnostic(applier, out cachedDiagnostic))
                {
                    mappingDiagnostic = cachedDiagnostic;
                }
                else
                {
                    mappingDiagnostic = createdDiagnostic;
                }

                if (showMappingError)
                {
                    EditorGUILayout.HelpBox(mappingDiagnostic, MessageType.Error);
                }
            }

            serializedObject.ApplyModifiedProperties();
        }

        private static void DrawBlendShapeWeights(BlendShareMeshComponent applier, SerializedProperty weightsProperty)
        {
            if (weightsProperty == null || !weightsProperty.isArray || weightsProperty.arraySize == 0)
            {
                return;
            }

            EditorGUILayout.Space();
            GUIContent label = new GUIContent("BlendShapes");
            Rect rect = EditorGUILayout.GetControlRect();
            label = EditorGUI.BeginProperty(rect, label, weightsProperty);
            weightsProperty.isExpanded = EditorGUI.Foldout(rect, weightsProperty.isExpanded, label, true);
            EditorGUI.EndProperty();

            if(weightsProperty.isExpanded)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    for (int i = 0; i < weightsProperty.arraySize; i++)
                    {
                        var item = weightsProperty.GetArrayElementAtIndex(i);
                        var shapeName = item.FindPropertyRelative(BlendShareProxyBlendShapeWeight.ShapeNameFieldName);
                        var weight = item.FindPropertyRelative(BlendShareProxyBlendShapeWeight.WeightFieldName);
                        if (shapeName == null || weight == null)
                        {
                            continue;
                        }
                        Rect shapeRect = EditorGUILayout.GetControlRect(true, EditorGUIUtility.singleLineHeight);
                        GUIContent shapeLabel = new GUIContent(shapeName.stringValue);
                        shapeLabel = EditorGUI.BeginProperty(shapeRect, shapeLabel, weight);
                        bool isAnimated = BlendShareAnimationHelper.TryGetAnimatedBlendShapeValue(
                            applier,
                            shapeName.stringValue,
                            out float animatedValue,
                            out bool isRecording);
                        float displayValue = isAnimated ? animatedValue : weight.floatValue;

                        var contentRect = EditorGUI.PrefixLabel(shapeRect, shapeLabel);

                        Color previousColor = GUI.color;
                        if (isAnimated)
                        {
                            GUI.color = isRecording
                                ? AnimationMode.recordedPropertyColor
                                : AnimationMode.animatedPropertyColor;
                        }

                        EditorGUI.BeginChangeCheck();

                        float newValue = EditorGUI.Slider(
                            contentRect,
                            displayValue,
                            0f,
                            100f);
                        GUI.color = previousColor;
                        if (EditorGUI.EndChangeCheck())
                        {
                            weight.floatValue = newValue;
                            RecordProxyBlendShapeAnimation(applier, shapeName.stringValue, newValue);
                        }
                        EditorGUI.EndProperty();

                    }
                }
            }



        }

        private static void RecordProxyBlendShapeAnimation(BlendShareMeshComponent applier, string shapeName, float value)
        {
            if (applier == null || string.IsNullOrWhiteSpace(shapeName))
            {
                return;
            }

            BlendShareAnimationHelper.RecordBlendShapeValue(applier, shapeName, value);
        }

        private static void AddBlendShapeAnimationMenuItems(
            GenericMenu menu,
            SerializedProperty property)
        {
            if (!BlendShareAnimationHelper.IsAnimationPreviewOrRecordingActive() ||
                !TryGetBlendShapeMenuContext(property, out var applier, out string shapeName, out float value))
            {
                return;
            }

            menu.AddSeparator(string.Empty);
            AddBlendShapeAnimationMenuItems(menu, applier, shapeName, value);
        }

        private static bool TryGetBlendShapeMenuContext(
            SerializedProperty property,
            out BlendShareMeshComponent applier,
            out string shapeName,
            out float value)
        {
            applier = null;
            shapeName = null;
            value = 0f;
            if (property == null ||
                property.serializedObject?.targetObject is not BlendShareMeshComponent blendShareMesh ||
                property.propertyType != SerializedPropertyType.Float ||
                !property.propertyPath.EndsWith($".{BlendShareProxyBlendShapeWeight.WeightFieldName}") ||
                !property.propertyPath.Contains($"{BlendShareMeshComponent.BlendShapeWeightsFieldName}.Array.data["))
            {
                return false;
            }

            string shapeNamePath = property.propertyPath.Substring(
                0,
                property.propertyPath.Length - BlendShareProxyBlendShapeWeight.WeightFieldName.Length) +
                BlendShareProxyBlendShapeWeight.ShapeNameFieldName;
            var shapeNameProperty = property.serializedObject.FindProperty(shapeNamePath);
            if (shapeNameProperty == null ||
                string.IsNullOrWhiteSpace(shapeNameProperty.stringValue))
            {
                return false;
            }

            applier = blendShareMesh;
            shapeName = shapeNameProperty.stringValue;
            value = BlendShareAnimationHelper.TryGetAnimatedBlendShapeValue(applier, shapeName, out float animatedValue, out _)
                ? animatedValue
                : property.floatValue;
            return true;
        }

        private static void AddBlendShapeAnimationMenuItems(
            GenericMenu menu,
            BlendShareMeshComponent applier,
            string shapeName,
            float value)
        {
            if (BlendShareAnimationHelper.CanEditActiveBlendShapeCurve(applier, shapeName))
            {
                menu.AddItem(
                    new GUIContent("Add Key"),
                    false,
                    () => BlendShareAnimationHelper.AddBlendShapeKey(applier, shapeName, value));
            }
            else
            {
                menu.AddDisabledItem(new GUIContent("Add Key"));
            }

            if (BlendShareAnimationHelper.HasBlendShapeKeyAtCurrentTime(applier, shapeName))
            {
                menu.AddItem(
                    new GUIContent("Remove Key"),
                    false,
                    () => BlendShareAnimationHelper.RemoveBlendShapeKey(applier, shapeName));
            }
            else
            {
                menu.AddDisabledItem(new GUIContent("Remove Key"));
            }

            if (BlendShareAnimationHelper.HasBlendShapeCurve(applier, shapeName))
            {
                menu.AddItem(
                    new GUIContent("Remove All Keys"),
                    false,
                    () => BlendShareAnimationHelper.RemoveAllBlendShapeKeys(applier, shapeName));
            }
            else
            {
                menu.AddDisabledItem(new GUIContent("Remove All Keys"));
            }
        }
    }
}
