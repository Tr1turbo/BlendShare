using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Components;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.Persistence
{
    [InitializeOnLoad]
    public static class SkinnedMeshRendererMeshChangeObserver
    {
        public delegate void MeshChangedCallback(
            SkinnedMeshRenderer renderer,
            Mesh previousMesh,
            Mesh currentMesh);

        public delegate void ArtifactMeshAssignedCallback(
            SkinnedMeshRenderer renderer,
            Mesh previousMesh,
            Transform previousRootBone,
            Transform[] previousBones,
            BlendShareArtifact artifact,
            BlendShareMeshDescriptor descriptor);

        private static readonly List<ObservedMeshChange> PendingChanges = new();
        private static bool flushScheduled;
        private static bool handlingBlendShareChange;

        private static event MeshChangedCallback MeshChanged;
        private static event ArtifactMeshAssignedCallback ArtifactMeshAssigned;

        static SkinnedMeshRendererMeshChangeObserver()
        {
            Undo.postprocessModifications += OnPostprocessModifications;
        }

        public static void RegisterCallback(MeshChangedCallback callback)
        {
            MeshChanged += callback;
        }

        public static void UnregisterCallback(MeshChangedCallback callback)
        {
            MeshChanged -= callback;
        }

        public static void RegisterArtifactMeshAssignedCallback(ArtifactMeshAssignedCallback callback)
        {
            ArtifactMeshAssigned += callback;
        }

        public static void UnregisterArtifactMeshAssignedCallback(ArtifactMeshAssignedCallback callback)
        {
            ArtifactMeshAssigned -= callback;
        }

        private static UndoPropertyModification[] OnPostprocessModifications(
            UndoPropertyModification[] modifications)
        {
            if (handlingBlendShareChange)
            {
                return modifications;
            }

            int undoGroup = Undo.GetCurrentGroup();
            foreach (var modification in modifications ?? Array.Empty<UndoPropertyModification>())
            {
                var renderer = modification.currentValue.target as SkinnedMeshRenderer;
                if (renderer == null || modification.currentValue.propertyPath != "m_Mesh")
                {
                    continue;
                }

                var currentMesh = modification.currentValue.objectReference as Mesh;
                currentMesh = currentMesh != null ? currentMesh : renderer.sharedMesh;
                var previousMesh = modification.previousValue.objectReference as Mesh;
                if (currentMesh == previousMesh)
                {
                    continue;
                }

                PendingChanges.Add(new ObservedMeshChange(
                    renderer,
                    previousMesh,
                    currentMesh,
                    renderer.rootBone,
                    renderer.bones,
                    undoGroup));
            }

            ScheduleFlush();
            return modifications;
        }

        private static void ScheduleFlush()
        {
            if (flushScheduled || PendingChanges.Count == 0)
            {
                return;
            }

            flushScheduled = true;
            EditorApplication.delayCall += FlushPendingChanges;
        }

        private static void FlushPendingChanges()
        {
            flushScheduled = false;
            var changes = PendingChanges.ToArray();
            PendingChanges.Clear();

            foreach (var change in changes)
            {
                if (change.Renderer == null || change.Renderer.sharedMesh != change.CurrentMesh)
                {
                    continue;
                }

                InvokeMeshChanged(change.Renderer, change.PreviousMesh, change.CurrentMesh);
                var marker = change.Renderer.GetComponent<BlendShareAppliedRenderer>();
                if (marker != null && marker.HasBaseline && change.CurrentMesh == marker.OriginalMesh)
                {
                    handlingBlendShareChange = true;
                    try
                    {
                        BlendShareArtifactService.RevertAppliedRenderer(change.Renderer, change.UndoGroup);
                    }
                    finally
                    {
                        handlingBlendShareChange = false;
                    }

                    continue;
                }

                if (TryResolveArtifactMesh(change.CurrentMesh, out var artifact, out var descriptor))
                {
                    InvokeArtifactMeshAssigned(
                        change.Renderer,
                        change.PreviousMesh,
                        change.PreviousRootBone,
                        change.PreviousBones,
                        artifact,
                        descriptor);
                    OnArtifactMeshAssigned(
                        change.Renderer,
                        change.PreviousMesh,
                        change.PreviousRootBone,
                        change.PreviousBones,
                        artifact,
                        descriptor,
                        change.UndoGroup);
                }
            }
        }

        private static bool TryResolveArtifactMesh(
            Mesh mesh,
            out BlendShareArtifact artifact,
            out BlendShareMeshDescriptor descriptor)
        {
            artifact = null;
            descriptor = null;
            if (mesh == null)
            {
                return false;
            }

            string assetPath = AssetDatabase.GetAssetPath(mesh);
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                return false;
            }

            artifact = AssetDatabase.LoadAssetAtPath<BlendShareArtifact>(assetPath);
            descriptor = (artifact?.m_Meshes ?? Array.Empty<BlendShareMeshDescriptor>())
                .FirstOrDefault(candidate => candidate != null && candidate.m_Mesh == mesh);
            return artifact != null && descriptor != null;
        }

        private static void InvokeMeshChanged(
            SkinnedMeshRenderer renderer,
            Mesh previousMesh,
            Mesh currentMesh)
        {
            if (MeshChanged == null)
            {
                return;
            }

            foreach (MeshChangedCallback callback in MeshChanged.GetInvocationList())
            {
                try
                {
                    callback(renderer, previousMesh, currentMesh);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception, renderer);
                }
            }
        }

        private static void InvokeArtifactMeshAssigned(
            SkinnedMeshRenderer renderer,
            Mesh previousMesh,
            Transform previousRootBone,
            Transform[] previousBones,
            BlendShareArtifact artifact,
            BlendShareMeshDescriptor descriptor)
        {
            if (ArtifactMeshAssigned == null)
            {
                return;
            }

            foreach (ArtifactMeshAssignedCallback callback in ArtifactMeshAssigned.GetInvocationList())
            {
                try
                {
                    callback(renderer, previousMesh, previousRootBone, previousBones, artifact, descriptor);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception, renderer);
                }
            }
        }

        private static void OnArtifactMeshAssigned(
            SkinnedMeshRenderer renderer,
            Mesh previousMesh,
            Transform previousRootBone,
            Transform[] previousBones,
            BlendShareArtifact artifact,
            BlendShareMeshDescriptor descriptor,
            int changeUndoGroup)
        {
            handlingBlendShareChange = true;
            bool applied;
            IReadOnlyList<string> diagnostics;
            try
            {
                applied = BlendShareArtifactService.ApplyArtifactMeshAssignment(
                    artifact,
                    descriptor,
                    renderer,
                    previousMesh,
                    previousRootBone,
                    previousBones,
                    changeUndoGroup,
                    out diagnostics);
            }
            finally
            {
                handlingBlendShareChange = false;
            }

            if (!applied)
            {
                foreach (string diagnostic in diagnostics)
                {
                    Debug.LogError($"[BlendShare Artifact] {diagnostic}", renderer);
                }
            }
        }

        private readonly struct ObservedMeshChange
        {
            public ObservedMeshChange(
                SkinnedMeshRenderer renderer,
                Mesh previousMesh,
                Mesh currentMesh,
                Transform previousRootBone,
                Transform[] previousBones,
                int undoGroup)
            {
                Renderer = renderer;
                PreviousMesh = previousMesh;
                CurrentMesh = currentMesh;
                PreviousRootBone = previousRootBone;
                PreviousBones = previousBones ?? Array.Empty<Transform>();
                UndoGroup = undoGroup;
            }

            public SkinnedMeshRenderer Renderer { get; }
            public Mesh PreviousMesh { get; }
            public Mesh CurrentMesh { get; }
            public Transform PreviousRootBone { get; }
            public Transform[] PreviousBones { get; }
            public int UndoGroup { get; }
        }
    }
}
