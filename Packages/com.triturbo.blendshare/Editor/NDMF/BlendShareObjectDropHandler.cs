using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Components;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Inspector;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.NDMF
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
            var patches = GetDraggedPatches();
            var meshes = GetDraggedMeshes();
            if ((patches.Count == 0 && meshes.Count == 0) || targets == null || targets.Count == 0)
            {
                return DragAndDropVisualMode.None;
            }

            if (perform)
            {
                foreach (var target in targets)
                {
                    AddPatches(target, patches);
                    AddMeshes(target, meshes);
                }
            }

            return DragAndDropVisualMode.Copy;
        }

        private static IReadOnlyList<BlendShareObject> GetDraggedPatches()
        {
            return DragAndDrop.objectReferences
                .OfType<BlendShareObject>()
                .Where(patch => patch != null)
                .Distinct()
                .ToList();
        }

        private static IReadOnlyList<MeshDataObject> GetDraggedMeshes()
        {
            return DragAndDrop.objectReferences
                .OfType<MeshDataObject>()
                .Where(mesh => mesh != null && BlendShareInspectorUtility.FindOwnerPatch(mesh) != null)
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

        private static void AddPatches(GameObject target, IReadOnlyList<BlendShareObject> patches)
        {
            if (target == null || patches == null || patches.Count == 0)
            {
                return;
            }

            BlendShareMesh lastCreatedMesh = null;
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Create BlendShare Setup");
            foreach (var patch in patches)
            {
                var result = BlendShareComponentSetupService.CreateSetup(target.transform, patch);
                lastCreatedMesh = result.MeshAppliers.LastOrDefault() ?? lastCreatedMesh;
                foreach (string diagnostic in result.Diagnostics)
                {
                    Debug.LogWarning($"[BlendShare] {diagnostic}", target);
                }
            }

            Undo.CollapseUndoOperations(undoGroup);
            Selection.activeObject = lastCreatedMesh != null ? lastCreatedMesh.gameObject : target;
        }

        private static void AddMeshes(GameObject target, IReadOnlyList<MeshDataObject> meshes)
        {
            if (target == null || meshes == null || meshes.Count == 0)
            {
                return;
            }

            BlendShareMesh lastCreatedMesh = null;
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Create BlendShare Mesh Setup");
            var targetRenderer = target.GetComponent<SkinnedMeshRenderer>();
            foreach (var mesh in meshes)
            {
                var patch = BlendShareInspectorUtility.FindOwnerPatch(mesh);
                var targetRoot = targetRenderer != null
                    ? BlendShareComponentSetupService.ResolveTargetRoot(targetRenderer, mesh)
                    : target.transform;
                if (targetRoot == null)
                {
                    Debug.LogWarning(
                        $"[BlendShare] Could not infer the target root for renderer '{target.name}' from mesh path '{MeshNodePath.Normalize(mesh.m_Path)}'.",
                        target);
                    continue;
                }

                var result = BlendShareComponentSetupService.CreateSetup(
                    targetRoot,
                    patch,
                    mesh,
                    targetRenderer);
                lastCreatedMesh = result.MeshAppliers.LastOrDefault() ?? lastCreatedMesh;
                foreach (string diagnostic in result.Diagnostics)
                {
                    Debug.LogWarning($"[BlendShare] {diagnostic}", target);
                }
            }

            Undo.CollapseUndoOperations(undoGroup);
            if (lastCreatedMesh != null)
            {
                Selection.activeObject = lastCreatedMesh.gameObject;
            }
        }
    }
}
