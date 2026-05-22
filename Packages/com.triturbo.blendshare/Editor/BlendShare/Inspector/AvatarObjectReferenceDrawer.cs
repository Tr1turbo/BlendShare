// Derived from Modular Avatar (https://github.com/bdunderscore/modular-avatar)
// Original work Copyright (c) 2022 bd_
// Distributed under the MIT License; see THIRD_PARTY_NOTICES.md

using System;
using System.Collections.Generic;
using Triturbo.BlendShare.Components;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Triturbo.BlendShare.Inspector
{
    [CustomPropertyDrawer(typeof(AvatarObjectReference), true)]
    internal class AvatarObjectReferenceDrawer : PropertyDrawer
    {
        private static readonly Dictionary<Type, Type> s_AcceptedTypeCache = new();

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            label = EditorGUI.BeginProperty(position, label, property);
            try
            {
                SerializedProperty referencePathProperty = property.FindPropertyRelative("referencePath");
                SerializedProperty targetObjectProperty = property.FindPropertyRelative("targetObject");
                Type acceptedType = GetAcceptedObjectType();

                if (referencePathProperty == null || targetObjectProperty == null)
                {
                    EditorGUI.LabelField(position, label, GUIContent.none);
                    return;
                }

                Transform avatarRoot = AvatarObjectReference.FindContainingAvatarTransform(property);
                bool isPrefabAsset = AvatarObjectReference.IsInPrefabAsset(property);
                bool isInPrefabMode = AvatarObjectReference.IsInPrefabMode(property);

                if (isPrefabAsset)
                {
                    EditorGUI.BeginDisabledGroup(true);
                    string displayText = GetReadOnlyDisplayText(referencePathProperty, targetObjectProperty) + $" ({GetTypeDisplayName(acceptedType)})";
                    DrawFakeField(acceptedType, position, label, Color.white, displayText, out _);
                    EditorGUI.EndDisabledGroup();
                    return;
                }

                AvatarObjectReference.InspectorState inspectorState = AvatarObjectReference.GetInspectorState(
                    property,
                    acceptedType,
                    allowPathRepair: !isInPrefabMode
                );
                Object currentObject = inspectorState.CurrentObject;
                bool showMissingPath = inspectorState.ShowMissingPath;

                Color contentColor = GUI.contentColor;
                try
                {
                    if (showMissingPath)
                    {
                        string field = referencePathProperty.stringValue + $" ({GetTypeDisplayName(acceptedType)})";
                        if (DrawFakeField(acceptedType, position, label, isInPrefabMode ? Color.white : Color.red, field, out Object newObject))
                        {
                            ApplySelection(avatarRoot, referencePathProperty, targetObjectProperty, newObject);
                        }
                    }
                    else
                    {
                        EditorGUI.BeginChangeCheck();
                        Object newObject = EditorGUI.ObjectField(position, label, currentObject, acceptedType, true);
                        if (EditorGUI.EndChangeCheck())
                        {
                            ApplySelection(avatarRoot, referencePathProperty, targetObjectProperty, newObject);
                        }
                    }
                }
                finally
                {
                    GUI.contentColor = contentColor;
                }
            }
            finally
            {
                EditorGUI.EndProperty();
            }
        }

        private static bool DrawFakeField(
            Type acceptedType,
            Rect position,
            GUIContent label,
            Color textColor,
            string text,
            out Object newObject)
        {
            EditorGUI.LabelField(position, label);

            int oldIndentLevel = EditorGUI.indentLevel;
            EditorGUI.indentLevel = 0;

            Rect fieldRect = position;
            float labelWidth = EditorGUIUtility.labelWidth + EditorGUIUtility.standardVerticalSpacing;
            fieldRect.x = position.x + labelWidth;
            fieldRect.width = position.width - labelWidth;

            Color oldColor = GUI.contentColor;

            GUI.contentColor = new Color(0, 0, 0, 0);
            EditorGUI.BeginChangeCheck();
            newObject = EditorGUI.ObjectField(fieldRect, GUIContent.none, null, acceptedType, true);
            bool updated = EditorGUI.EndChangeCheck();

            GUI.contentColor = textColor;

            if (position.width > 152)
            {
                GUIContent icon = GetTypeIconContent(acceptedType);
                Rect iconRect = fieldRect;
                iconRect.width = 18;
                iconRect.height = 18;
                iconRect.y += (fieldRect.height - 18) * 0.5f;

                Rect textRect = fieldRect;
                textRect.x += 16;
                textRect.width = position.width - labelWidth - 36;

                if (icon?.image != null)
                {
                    GUI.Label(iconRect, icon);
                }

                EditorGUI.LabelField(textRect, text);
            }

            EditorGUI.indentLevel = oldIndentLevel;
            GUI.contentColor = oldColor;
            return updated;
        }

        private static string GetTypeDisplayName(Type acceptedType)
        {
            if (acceptedType == typeof(SkinnedMeshRenderer))
            {
                return "Skinned Mesh Renderer";
            }

            if (acceptedType == typeof(GameObject))
            {
                return "Game Object";
            }

            return ObjectNames.NicifyVariableName(acceptedType.Name);
        }

        private static GUIContent GetTypeIconContent(Type acceptedType)
        {
            if (acceptedType == typeof(SkinnedMeshRenderer))
            {
                return GetBuiltInIconContent("SkinnedMeshRenderer Icon");
            }

            if (acceptedType == typeof(GameObject))
            {
                return GetBuiltInIconContent("GameObject Icon");
            }

            if (acceptedType == typeof(Transform))
            {
                return GetBuiltInIconContent("Transform Icon");
            }

            return EditorGUIUtility.ObjectContent(null, acceptedType ?? typeof(Object));
        }

        private static GUIContent GetBuiltInIconContent(string iconName)
        {
            string themedIconName = EditorGUIUtility.isProSkin ? $"d_{iconName}" : iconName;
            GUIContent content = EditorGUIUtility.IconContent(themedIconName);

            if (content?.image != null)
            {
                return content;
            }

            return EditorGUIUtility.IconContent(iconName);
        }

        private static string GetReadOnlyDisplayText(
            SerializedProperty referencePathProperty,
            SerializedProperty targetObjectProperty)
        {
            if (referencePathProperty != null && !string.IsNullOrEmpty(referencePathProperty.stringValue))
            {
                return referencePathProperty.stringValue;
            }

            Object directTarget = targetObjectProperty?.objectReferenceValue;
            if (directTarget != null)
            {
                return directTarget.name;
            }

            return "None";
        }

        private Type GetAcceptedObjectType()
        {
            Type fieldType = fieldInfo?.FieldType;
            if (fieldType == null)
            {
                return typeof(GameObject);
            }

            if (s_AcceptedTypeCache.TryGetValue(fieldType, out Type acceptedType))
            {
                return acceptedType;
            }

            Type type = fieldType;
            while (type != null)
            {
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(AvatarObjectReference<>))
                {
                    acceptedType = type.GetGenericArguments()[0];
                    s_AcceptedTypeCache[fieldType] = acceptedType;
                    return acceptedType;
                }

                type = type.BaseType;
            }

            s_AcceptedTypeCache[fieldType] = typeof(GameObject);
            return typeof(GameObject);
        }

        private static void ApplySelection(
            Transform avatarRoot,
            SerializedProperty referencePathProperty,
            SerializedProperty targetObjectProperty,
            Object selectedObject)
        {
            GameObject selectedGameObject = ExtractGameObject(selectedObject);

            if (selectedGameObject == null)
            {
                referencePathProperty.stringValue = string.Empty;
                targetObjectProperty.objectReferenceValue = null;
                return;
            }

            if (avatarRoot == null)
            {
                referencePathProperty.stringValue = string.Empty;
                targetObjectProperty.objectReferenceValue = selectedObject;
                return;
            }

            if (selectedGameObject.transform == avatarRoot)
            {
                referencePathProperty.stringValue = AvatarObjectReference.AvatarRoot;
                targetObjectProperty.objectReferenceValue = selectedObject;
                return;
            }

            string relativePath = AvatarHierarchyUtil.RelativePath(avatarRoot, selectedGameObject.transform);
            if (relativePath == null)
            {
                return;
            }

            referencePathProperty.stringValue = relativePath;
            targetObjectProperty.objectReferenceValue = selectedObject;
        }

        private static GameObject ExtractGameObject(Object selectedObject)
        {
            if (selectedObject is GameObject gameObject)
            {
                return gameObject;
            }

            if (selectedObject is Component component)
            {
                return component.gameObject;
            }

            return null;
        }
    }
}
