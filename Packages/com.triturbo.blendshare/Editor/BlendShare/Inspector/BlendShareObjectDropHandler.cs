using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Components;
using Triturbo.BlendShare.Core;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.Inspector
{
    [InitializeOnLoad]
    internal static class BlendShareObjectDropHandler
    {
        static BlendShareObjectDropHandler()
        {
            DragAndDrop.AddDropHandler(OnHierarchyDrop);
            //DragAndDrop.AddDropHandler(OnSceneDrop);
            DragAndDrop.AddDropHandler(OnInspectorDrop);
        }

        private static DragAndDropVisualMode OnHierarchyDrop(
            int dropTargetInstanceID,
            HierarchyDropFlags dropMode,
            Transform parentForDraggedObjects,
            bool perform)
        {
            // Only claim direct drops onto a GameObject row. Drop-between/parenting gestures should
            // continue through Unity's normal Hierarchy handling.
            if ((dropMode & HierarchyDropFlags.DropUpon) == 0)
            {
                return DragAndDropVisualMode.None;
            }

            return HandleDrop(GetGameObject(EditorUtility.InstanceIDToObject(dropTargetInstanceID)), perform);
        }

        private static DragAndDropVisualMode OnSceneDrop(
            Object dropUpon,
            Vector3 worldPosition,
            Vector2 viewportPosition,
            Transform parentForDraggedObjects,
            bool perform)
        {
            return HandleDrop(GetGameObject(dropUpon), perform);
        }

        private static DragAndDropVisualMode OnInspectorDrop(Object[] targets, bool perform)
        {
            var gameObjects = targets?
                .Select(GetGameObject)
                .Where(gameObject => gameObject != null)
                .Distinct()
                .ToArray();

            return HandleDrop(gameObjects, perform);
        }

        private static DragAndDropVisualMode HandleDrop(GameObject target, bool perform)
        {
            return target != null
                ? HandleDrop(new[] { target }, perform)
                : DragAndDropVisualMode.None;
        }

        private static DragAndDropVisualMode HandleDrop(IReadOnlyCollection<GameObject> targets, bool perform)
        {
            var blendShares = GetDraggedBlendShareObjects();
            if (blendShares.Count == 0 || targets == null || targets.Count == 0)
            {
                return DragAndDropVisualMode.None;
            }

            if (perform)
            {
                foreach (var target in targets)
                {
                    AddBlendShares(target, blendShares);
                }
            }

            return DragAndDropVisualMode.Copy;
        }

        private static IReadOnlyList<BlendShareObject> GetDraggedBlendShareObjects()
        {
            return DragAndDrop.objectReferences
                .OfType<BlendShareObject>()
                .Where(blendShare => blendShare != null)
                .Distinct()
                .ToList();
        }

        private static GameObject GetGameObject(Object target)
        {
            return target switch
            {
                GameObject gameObject => gameObject,
                Component component => component.gameObject,
                _ => null
            };
        }

        private static void AddBlendShares(GameObject target, IReadOnlyList<BlendShareObject> blendShares)
        {
            if (target == null)
            {
                return;
            }

            var component = target.GetComponent<BlendShareComponent>();
            if (component == null)
            {
                component = Undo.AddComponent<BlendShareComponent>(target);
            }

            Undo.RecordObject(component, "Assign BlendShare Object");
            component.SetBlendShares(component.BlendShares.Concat(blendShares).Distinct());
            EditorUtility.SetDirty(component);
            Selection.activeObject = target;
        }
    }
}
