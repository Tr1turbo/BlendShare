using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;


namespace Triturbo.BlendShapeShare.BlendShapeData
{
    public static class EditorWidgets
    {
        public static readonly Texture BannerIcon = AssetDatabase.LoadAssetAtPath(AssetDatabase.GUIDToAssetPath("6ee731e02404e154694005c2442f2bf4"), typeof(Texture)) as Texture;

        public static void ShowBlendShareBanner()
        {        
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace(); // Pushes the label to the center
            GUILayout.Label(BannerIcon, GUILayout.Height(42), GUILayout.Width(168));
            GUILayout.FlexibleSpace(); // Pushes the label to the center
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
            
            // Handle drag & drop
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
                        evt.Use(); // consume event
                    }
                }
            }
            GameObject updated = EditorGUI.ObjectField(fieldRect, label, obj, typeof(GameObject), false) as GameObject;
            
            if (updated == null)
            {
                return null;
            }
            if (valid)
            {
                if (firstDragObject == updated || IsFBXGameObject(updated))
                {
                    return updated;
                }
            }
            
            return obj;
        }
        
        public static bool FBXGameObjectField(GUIContent label, SerializedProperty property)
        {
            Rect fieldRect = EditorGUILayout.GetControlRect();
            bool valid = false;
            Object firstDragObject = null;
            
            // Handle drag & drop
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
                        evt.Use(); // consume event
                    }
                }
            }
            var updated = EditorGUI.ObjectField(fieldRect, label, property.objectReferenceValue, typeof(GameObject), false);
            
            if (updated == null)
            {
                property.objectReferenceValue = null;
                return true;
            }
            if (valid)
            {
                if (firstDragObject == updated || IsFBXGameObject(updated))
                {
                    property.objectReferenceValue = updated;
                    return true;
                }
            }
            
            return false;
        }

        
        
        public static Object MeshAssetObjectField(GUIContent label, Object current)
        {
            bool IsValidTarget(Object obj)
            {
                if (obj == null) return false;
                if (obj is Mesh) return true;
                // Your GeneratedMeshAsset type (replace with actual class name)
                if (obj is GeneratedMeshAssetSO) return true;
                // FBX check: GameObject that comes from an FBX file (not scene instance)
                if (obj is GameObject go)
                {
                    string path = AssetDatabase.GetAssetPath(go);
                    if (!string.IsNullOrEmpty(path) && path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                        return true;
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
            if (valid)
            {
                if (firstDragObject == updated || IsValidTarget(updated))
                {
                    return updated;
                }
            }
            
            
            return current;
        }
        

    }
}

