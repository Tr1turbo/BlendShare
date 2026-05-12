using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShapeShare.BlendShapeData;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShapeShare.Extractor
{
    public sealed class BlendShapeExtractionOptionsProvider
        : MeshFeatureExtractionOptionsProvider<BlendShapeExtractionOptions>
    {
        private readonly Dictionary<string, bool> meshFoldouts = new();
        private Vector2 scrollPosition;
        private bool showApplyTransform;

        public override string FeatureId => BlendShapeFeatureObject.Id;
        public override int DisplayOrder => -100;

        protected override BlendShapeExtractionOptions CreateDefault()
        {
            return new BlendShapeExtractionOptions();
        }

        protected override void DrawOptionsGUI(
            BlendShapeExtractionOptions options,
            MeshFeatureOptionsEditorContext context)
        {
            if (options == null)
            {
                return;
            }

            DrawGeneralOptions(options);
            DrawBlendShapeToggles(options, context);
        }

        private void DrawGeneralOptions(BlendShapeExtractionOptions options)
        {
            options.Enabled = EditorGUILayout.ToggleLeft(Localization.S("blendshapes"), options.Enabled);
            EditorGUI.BeginDisabledGroup(!options.Enabled);

            options.WeldVertices = EditorGUILayout.Toggle(Localization.G("extractor.weld_vertices"), options.WeldVertices);
            showApplyTransform = EditorGUILayout.Foldout(showApplyTransform, Localization.G("extractor.apply_transform"));
            if (showApplyTransform)
            {
                EditorGUI.indentLevel++;
                options.ApplyTranslate = EditorGUILayout.Toggle(Localization.G("extractor.apply_translate"), options.ApplyTranslate);
                options.ApplyRotation = EditorGUILayout.Toggle(Localization.G("extractor.apply_rotation"), options.ApplyRotation);
                options.ApplyScale = EditorGUILayout.Toggle(Localization.G("extractor.apply_scale"), options.ApplyScale);
                EditorGUI.indentLevel--;
            }

            options.BaseMesh = Localization.LocalizedEnumPopup(
                Localization.G("extractor.base_mesh"),
                options.BaseMesh,
                "extractor.enum.base_mesh");

            EditorGUI.EndDisabledGroup();
        }

        private void DrawBlendShapeToggles(
            BlendShapeExtractionOptions options,
            MeshFeatureOptionsEditorContext context)
        {
            EditorGUI.BeginDisabledGroup(!options.Enabled);
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField(Localization.G("new_extractor.meshes"), EditorStyles.boldLabel);

            if (context?.SourceFbxAsset == null || context.OriginFbxAsset == null)
            {
                EditorGUILayout.HelpBox(Localization.S("new_extractor.assign_fbx_hint"), MessageType.Info);
                EditorGUI.EndDisabledGroup();
                return;
            }

            var requests = context.Meshes?
                .Where(mesh => mesh != null)
                .ToArray() ?? System.Array.Empty<MeshFeatureExtractionMeshRequest>();
            if (requests.Length == 0)
            {
                EditorGUILayout.HelpBox(Localization.S("new_extractor.no_meshes"), MessageType.Info);
                EditorGUI.EndDisabledGroup();
                return;
            }

            var sourceMeshes = new UnityMeshExtractionSource(context.SourceFbxAsset);
            var originMeshes = new UnityMeshExtractionSource(context.OriginFbxAsset);

            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
            foreach (var request in requests)
            {
                DrawMeshBlendShapeToggles(options, request, sourceMeshes, originMeshes);
            }
            EditorGUILayout.EndScrollView();
            EditorGUI.EndDisabledGroup();
        }

        private void DrawMeshBlendShapeToggles(
            BlendShapeExtractionOptions options,
            MeshFeatureExtractionMeshRequest request,
            UnityMeshExtractionSource sourceMeshes,
            UnityMeshExtractionSource originMeshes)
        {
            var sourceMesh = sourceMeshes.GetMesh(request.MeshPath, request.MeshName);
            if (sourceMesh == null || sourceMesh.blendShapeCount == 0)
            {
                return;
            }

            var originMesh = originMeshes.GetMesh(request.MeshPath, request.MeshName);
            EnsureDefaultSelection(options, request, sourceMesh, originMesh);

            var selected = new HashSet<string>(options.GetSelectedBlendShapeNames(request.MeshPath, request.MeshName));
            string key = MeshFeatureExtractionSession.BuildMeshKey(request.MeshPath, request.MeshName);
            if (!meshFoldouts.ContainsKey(key))
            {
                meshFoldouts[key] = true;
            }

            string displayName = string.IsNullOrEmpty(request.MeshPath) ? request.MeshName : request.MeshPath;
            meshFoldouts[key] = EditorGUILayout.Foldout(
                meshFoldouts[key],
                $"{displayName} ({selected.Count}/{sourceMesh.blendShapeCount})",
                true);
            if (!meshFoldouts[key])
            {
                return;
            }

            EditorGUI.indentLevel++;
            EditorGUILayout.BeginVertical(GUI.skin.box);
            EditorGUILayout.LabelField(Localization.G("new_extractor.mesh_path"), new GUIContent(request.MeshPath));

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(Localization.S("data.enable_all_blendshapes")))
            {
                selected = GetAllBlendShapeNames(sourceMesh);
            }

            if (GUILayout.Button(Localization.S("data.mute_all_blendshapes")))
            {
                selected.Clear();
            }
            EditorGUILayout.EndHorizontal();

            for (int i = 0; i < sourceMesh.blendShapeCount; i++)
            {
                string shapeName = sourceMesh.GetBlendShapeName(i);
                bool wasSelected = selected.Contains(shapeName);
                bool isSelected = EditorGUILayout.ToggleLeft(shapeName, wasSelected);
                if (isSelected)
                {
                    selected.Add(shapeName);
                }
                else if (wasSelected)
                {
                    selected.Remove(shapeName);
                }
            }

            options.SetSelectedBlendShapeNames(request.MeshPath, request.MeshName, selected);
            EditorGUILayout.EndVertical();
            EditorGUI.indentLevel--;
        }

        private static void EnsureDefaultSelection(
            BlendShapeExtractionOptions options,
            MeshFeatureExtractionMeshRequest request,
            Mesh sourceMesh,
            Mesh originMesh)
        {
            if (options.HasSelectedBlendShapeNames(request.MeshPath, request.MeshName))
            {
                return;
            }

            var selected = new List<string>();
            for (int i = 0; i < sourceMesh.blendShapeCount; i++)
            {
                string shapeName = sourceMesh.GetBlendShapeName(i);
                if (originMesh == null || originMesh.GetBlendShapeIndex(shapeName) == -1)
                {
                    selected.Add(shapeName);
                }
            }

            options.SetSelectedBlendShapeNames(request.MeshPath, request.MeshName, selected);
        }

        private static HashSet<string> GetAllBlendShapeNames(Mesh mesh)
        {
            var names = new HashSet<string>();
            if (mesh == null)
            {
                return names;
            }

            for (int i = 0; i < mesh.blendShapeCount; i++)
            {
                names.Add(mesh.GetBlendShapeName(i));
            }

            return names;
        }
    }
}
