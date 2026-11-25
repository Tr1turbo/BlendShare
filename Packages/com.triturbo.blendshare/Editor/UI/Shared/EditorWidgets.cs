using System;
using System.Linq;
using Triturbo.BlendShare.Assets;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Triturbo.BlendShare.UI.Shared
{
    public static class EditorWidgets
    {
        private const string k_BannerIconGuid = "6ee731e02404e154694005c2442f2bf4";
        private const string k_FallbackBannerPath = "Packages/com.triturbo.blendshare/Editor/UI/Icons/BlendShapeShare.png";

        private static Texture s_BannerIcon;

        public static Texture BannerIcon
        {
            get
            {
                if (s_BannerIcon == null)
                {
                    string path = AssetDatabase.GUIDToAssetPath(k_BannerIconGuid);
                    if (string.IsNullOrEmpty(path))
                    {
                        path = k_FallbackBannerPath;
                    }

                    s_BannerIcon = AssetDatabase.LoadAssetAtPath<Texture>(path);
                }

                return s_BannerIcon;
            }
        }

        public static void ShowBlendShareBanner()
        {
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            GUILayout.Label(BannerIcon, GUILayout.Height(42), GUILayout.Width(168));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(8);
        }

        public static bool IsFBXGameObject(Object obj, out GameObject gameObject)
        {
            if (IsFBXGameObject(obj))
            {
                gameObject = obj as GameObject;
                return true;
            }

            gameObject = null;
            return false;
        }

        public static bool IsFBXGameObject(Object obj)
        {
            if (obj is GameObject go)
            {
                string path = AssetDatabase.GetAssetPath(go);
                return !string.IsNullOrEmpty(path) && path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        public static GameObject FBXGameObjectField(GUIContent label, GameObject obj)
        {
            Rect fieldRect = EditorGUILayout.GetControlRect();
            bool valid = false;
            Object firstDragObject = null;

            Event evt = Event.current;
            if (evt.type == EventType.DragUpdated || evt.type == EventType.DragPerform)
            {
                if (fieldRect.Contains(evt.mousePosition))
                {
                    firstDragObject = DragAndDrop.objectReferences.FirstOrDefault();
                    valid = IsFBXGameObject(firstDragObject);
                    if (!valid)
                    {
                        DragAndDrop.visualMode = DragAndDropVisualMode.Rejected;
                        evt.Use();
                    }
                }
            }

            GameObject updated = EditorGUI.ObjectField(fieldRect, label, obj, typeof(GameObject), false) as GameObject;
            if (updated == null)
            {
                return null;
            }

            if (valid && (firstDragObject == updated || IsFBXGameObject(updated)))
            {
                return updated;
            }

            return IsFBXGameObject(updated) ? updated : obj;
        }

        public static bool FBXGameObjectField(GUIContent label, SerializedProperty property)
        {
            Rect fieldRect = EditorGUILayout.GetControlRect();
            bool valid = false;
            Object firstDragObject = null;

            Event evt = Event.current;
            if (evt.type == EventType.DragUpdated || evt.type == EventType.DragPerform)
            {
                if (fieldRect.Contains(evt.mousePosition))
                {
                    firstDragObject = DragAndDrop.objectReferences.FirstOrDefault();
                    valid = IsFBXGameObject(firstDragObject);
                    if (!valid)
                    {
                        DragAndDrop.visualMode = DragAndDropVisualMode.Rejected;
                        evt.Use();
                    }
                }
            }

            EditorGUI.PropertyField(fieldRect, property, label);
            var updated = property.objectReferenceValue as GameObject;
            if (valid && (firstDragObject == updated || IsFBXGameObject(updated)))
            {
                property.objectReferenceValue = updated;
                return true;
            }

            return false;
        }

        public static Object MeshAssetObjectField(GUIContent label, Object current)
        {
            bool IsValidTarget(Object obj)
            {
                if (obj == null) return false;
                if (obj is Mesh || obj is BlendShareGeneratedArtifact) return true;
                if (obj is GameObject go)
                {
                    string path = AssetDatabase.GetAssetPath(go);
                    if (!string.IsNullOrEmpty(path) &&
                        (path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) ||
                         path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
                    }
                }

                return false;
            }

            Object firstDragObject = null;
            bool valid = false;

            Rect fieldRect = EditorGUILayout.GetControlRect();
            Event evt = Event.current;
            if ((evt.type == EventType.DragPerform || evt.type == EventType.DragUpdated) && fieldRect.Contains(evt.mousePosition))
            {
                firstDragObject = DragAndDrop.objectReferences.FirstOrDefault();
                valid = IsValidTarget(firstDragObject);
                if (!valid)
                {
                    DragAndDrop.visualMode = DragAndDropVisualMode.Rejected;
                    evt.Use();
                }
            }

            var updated = EditorGUI.ObjectField(fieldRect, label, current, typeof(Object), false);
            if (updated == null)
            {
                return null;
            }

            if (valid && (firstDragObject == updated || IsValidTarget(updated)))
            {
                return updated;
            }

            return IsValidTarget(updated) ? updated : current;
        }
    }
}
