using System.Linq;
using Triturbo.BlendShapeShare;
using Triturbo.BlendShapeShare.BlendShapeData;
using Triturbo.BlendShare.Components;
using Triturbo.BlendShare.Persistence;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.Inspector
{
    [CustomEditor(typeof(BlendShareAppliedRenderer))]
    public sealed class BlendShareAppliedRendererEditor : UnityEditor.Editor
    {
        private static readonly GUIContent AppliedMeshLabel = new GUIContent("Applied Mesh");
        private static readonly GUIContent OriginalMeshLabel = new GUIContent("Original Mesh");
        private static readonly GUIContent OriginalRootBoneLabel = new GUIContent("Original Root Bone");

        private bool generatedBonesExpanded;

        public override void OnInspectorGUI()
        {
            EditorWidgets.ShowBlendShareBanner();

            var marker = (BlendShareAppliedRenderer)target;
            var renderer = marker.GetComponent<SkinnedMeshRenderer>();

            EditorGUILayout.HelpBox(Localization.S("applied_renderer.purpose.message"), MessageType.Info);
            EditorGUILayout.Space();

            DrawReadonlyState(marker, renderer);
            EditorGUILayout.Space();

            MarkerState state = EvaluateState(marker, renderer);
            DrawDiagnostics(state);
            EditorGUILayout.Space();

            DrawActions(state, renderer);
        }

        private void DrawReadonlyState(BlendShareAppliedRenderer marker, SkinnedMeshRenderer renderer)
        {
            EditorGUILayout.LabelField(Localization.S("applied_renderer.state"), EditorStyles.boldLabel);

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.ObjectField(AppliedMeshLabel, renderer != null ? renderer.sharedMesh : null, typeof(Mesh), false);
                EditorGUILayout.ObjectField(OriginalMeshLabel, marker.OriginalMesh, typeof(Mesh), false);
                EditorGUILayout.ObjectField(OriginalRootBoneLabel, marker.OriginalRootBone, typeof(Transform), true);

                int originalBoneCount = marker.OriginalBones.Length;
                EditorGUILayout.LabelField(Localization.S("applied_renderer.original_bones"),
                    Localization.SF("applied_renderer.bones_count", originalBoneCount));
            }

            DrawGeneratedBones(marker);
        }

        private void DrawGeneratedBones(BlendShareAppliedRenderer marker)
        {
            var generatedBones = marker.GeneratedBones;
            int missingCount = generatedBones.Count(bone => bone == null);
            string summary = missingCount > 0
                ? Localization.SF("applied_renderer.generated_bones_with_missing", generatedBones.Length, missingCount)
                : Localization.SF("applied_renderer.bones_count", generatedBones.Length);

            generatedBonesExpanded = EditorGUILayout.Foldout(
                generatedBonesExpanded,
                $"{Localization.S("applied_renderer.generated_bones")} ({summary})",
                true);

            if (!generatedBonesExpanded)
            {
                return;
            }

            using (new EditorGUI.DisabledScope(true))
            using (new EditorGUI.IndentLevelScope())
            {
                for (int i = 0; i < generatedBones.Length; i++)
                {
                    EditorGUILayout.ObjectField($"Bone {i}", generatedBones[i], typeof(Transform), true);
                }
            }
        }

        private static MarkerState EvaluateState(BlendShareAppliedRenderer marker, SkinnedMeshRenderer renderer)
        {
            if (renderer == null)
            {
                return MarkerState.RendererMissing;
            }

            if (!marker.HasBaseline)
            {
                return MarkerState.NoBaseline;
            }

            if (renderer.sharedMesh == marker.OriginalMesh)
            {
                return MarkerState.Stale;
            }

            if (marker.GeneratedBones.Any(bone => bone == null))
            {
                return MarkerState.MissingGeneratedBones;
            }

            return MarkerState.Applied;
        }

        private static void DrawDiagnostics(MarkerState state)
        {
            switch (state)
            {
                case MarkerState.RendererMissing:
                    EditorGUILayout.HelpBox(Localization.S("applied_renderer.renderer_missing.message"), MessageType.Error);
                    break;
                case MarkerState.NoBaseline:
                    EditorGUILayout.HelpBox(Localization.S("applied_renderer.no_baseline.message"), MessageType.Warning);
                    break;
                case MarkerState.Stale:
                    EditorGUILayout.HelpBox(Localization.S("applied_renderer.stale.message"), MessageType.Warning);
                    break;
                case MarkerState.IncompleteBaseline:
                    EditorGUILayout.HelpBox(Localization.S("applied_renderer.incomplete.message"), MessageType.Warning);
                    break;
                case MarkerState.MissingGeneratedBones:
                    EditorGUILayout.HelpBox(Localization.S("applied_renderer.missing_bones.message"), MessageType.Info);
                    break;
            }
        }

        private static void DrawActions(MarkerState state, SkinnedMeshRenderer renderer)
        {
            bool canRevert = state == MarkerState.Applied || state == MarkerState.MissingGeneratedBones;
            bool canRemoveMarker = state == MarkerState.NoBaseline || state == MarkerState.Stale;

            using (new EditorGUI.DisabledScope(!canRevert))
            {
                if (GUILayout.Button(Localization.S("common.revert")))
                {
                    if (Localization.DisplayDialog(
                            "applied_renderer.revert_confirm",
                            Localization.S("common.revert"),
                            Localization.S("common.cancel")))
                    {
                        BlendShareArtifactService.RevertAppliedRenderer(renderer);
                    }
                }
            }

            if (canRemoveMarker)
            {
                EditorGUILayout.Space();
                if (GUILayout.Button(Localization.S("applied_renderer.remove_marker")))
                {
                    if (Localization.DisplayDialog(
                            "applied_renderer.remove_confirm",
                            Localization.S("common.delete"),
                            Localization.S("common.cancel")))
                    {
                        Undo.DestroyObjectImmediate(renderer.GetComponent<BlendShareAppliedRenderer>());
                    }
                }
            }
        }

        private enum MarkerState
        {
            Applied,
            RendererMissing,
            NoBaseline,
            Stale,
            IncompleteBaseline,
            MissingGeneratedBones,
        }
    }
}
