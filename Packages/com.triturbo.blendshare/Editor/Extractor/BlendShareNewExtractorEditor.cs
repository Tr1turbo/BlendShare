using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShapeShare.BlendShapeData;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShapeShare.Extractor
{
    public sealed class BlendShareNewExtractorEditor : EditorWindow
    {
        private GameObject originFBX;
        private GameObject sourceFBX;
        private string defaultName = "";
        private readonly List<MeshFeatureExtractionMeshRequest> meshRequests = new();
        private readonly List<string> skippedMeshes = new();
        private MeshFeatureExtractionOptionsSet featureOptionsSet = new();

        [MenuItem("Tools/BlendShare/BlendShapes Extractor (New System)")]
        public static void ShowWindow()
        {
            GetWindow<BlendShareNewExtractorEditor>("BlendShare Extractor");
        }

        private void OnEnable()
        {
            EnsureFeatureOptions();
        }

        private void OnGUI()
        {
            EditorWidgets.ShowBlendShareBanner();

            GUILayout.BeginHorizontal(GUI.skin.box);
            GUILayout.Label(Localization.S("new_extractor.title"), EditorStyles.boldLabel);
            GUILayout.EndHorizontal();
            EditorGUILayout.Separator();

            Localization.DrawLanguageSelection();
            EditorGUILayout.Separator();

            DrawFbxFields();
            DrawFeatureOptions();
            DrawSaveButton();
        }

        private void DrawFbxFields()
        {
            EditorGUI.BeginChangeCheck();
            originFBX = EditorWidgets.FBXGameObjectField(Localization.G("origin_fbx"), originFBX);
            sourceFBX = EditorWidgets.FBXGameObjectField(Localization.G("source_fbx"), sourceFBX);
            if (EditorGUI.EndChangeCheck())
            {
                if (sourceFBX != null && string.IsNullOrWhiteSpace(defaultName))
                {
                    defaultName = sourceFBX.name;
                }

                ResetFeatureOptions();
                RefreshMeshRequests();
            }

            defaultName = EditorGUILayout.TextField(Localization.G("extractor.default_asset_name"), defaultName);

            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.TextField(Localization.G("extractor.deformer_id"), "+BlendShare-" + defaultName);
            EditorGUI.EndDisabledGroup();
        }

        private void DrawFeatureOptions()
        {
            EnsureFeatureOptions();
            EditorGUILayout.Space(4);

            if (sourceFBX == null || originFBX == null)
            {
                EditorGUILayout.HelpBox(Localization.S("new_extractor.assign_fbx_hint"), MessageType.Info);
                return;
            }

            if (GUILayout.Button(Localization.S("new_extractor.refresh")))
            {
                RefreshMeshRequests();
            }

            foreach (string skipped in skippedMeshes)
            {
                EditorGUILayout.HelpBox(skipped, MessageType.Warning);
            }

            var context = new MeshFeatureOptionsEditorContext(sourceFBX, originFBX, meshRequests);
            foreach (var provider in MeshFeatureExtractionOptionsProviderRegistry.Providers)
            {
                if (provider == null ||
                    !featureOptionsSet.TryGet(provider.OptionsType, out var options) ||
                    options == null)
                {
                    continue;
                }

                provider.DrawOptionsGUI(options, context);
            }
        }

        private void DrawSaveButton()
        {
            EditorGUILayout.Separator();

            bool enabled = sourceFBX != null &&
                           originFBX != null &&
                           HasSelectedBlendShapes();
#if !ENABLE_FBX_SDK
            enabled = false;
            EditorGUILayout.HelpBox(Localization.S("data.fbx_sdk_missing"), MessageType.Warning);
#endif

            EditorGUI.BeginDisabledGroup(!enabled);
            if (GUILayout.Button(Localization.S("new_extractor.save_asset")))
            {
                SaveBlendShareAsset();
            }
            EditorGUI.EndDisabledGroup();
        }

        private void SaveBlendShareAsset()
        {
            if (string.IsNullOrWhiteSpace(defaultName) && sourceFBX != null)
            {
                defaultName = sourceFBX.name;
            }

            var selectedRequests = GetSelectedBlendShapeRequests();
            if (selectedRequests.Count == 0)
            {
                EditorUtility.DisplayDialog(
                    Localization.S("new_extractor.no_selection.title"),
                    Localization.S("new_extractor.no_selection.message"),
                    Localization.S("data.dialog.ok"));
                return;
            }

            string path = EditorUtility.SaveFilePanelInProject(
                Localization.S("new_extractor.save_title"),
                $"{defaultName}_BlendShare",
                "asset",
                Localization.S("data.save_file.message"));
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var blendShare = BlendShareExtractionService.ExtractAndSave(
                sourceFBX,
                originFBX,
                selectedRequests,
                featureOptionsSet,
                path,
                defaultName);

            if (blendShare == null)
            {
                EditorUtility.DisplayDialog(
                    Localization.S("new_extractor.failed.title"),
                    Localization.S("new_extractor.failed.message"),
                    Localization.S("data.dialog.ok"));
                return;
            }

            Selection.activeObject = blendShare;
            ShowInvalidMappingWarning(blendShare);
        }

        private bool HasSelectedBlendShapes()
        {
            return featureOptionsSet.TryGet<BlendShapeExtractionOptions>(out var options) &&
                   options != null &&
                   options.Enabled &&
                   options.HasAnySelectedBlendShapeNames(meshRequests);
        }

        private List<MeshFeatureExtractionMeshRequest> GetSelectedBlendShapeRequests()
        {
            if (!featureOptionsSet.TryGet<BlendShapeExtractionOptions>(out var options) || options == null)
            {
                return new List<MeshFeatureExtractionMeshRequest>();
            }

            return meshRequests
                .Where(request => request != null &&
                                  options.GetSelectedBlendShapeNames(request.MeshPath, request.MeshName).Count > 0)
                .ToList();
        }

        private void ShowInvalidMappingWarning(BlendShareObject blendShare)
        {
            bool hasInvalidMapping = blendShare.Meshes.Any(mesh =>
                (mesh.m_Mappings ?? System.Array.Empty<UnityVertexMappingObject>())
                .Any(mapping => mapping != null && !mapping.m_IsValid));
            if (!hasInvalidMapping)
            {
                return;
            }

            string message = Localization.S("new_extractor.invalid_mapping.message");
            if (featureOptionsSet.TryGet<BlendShapeExtractionOptions>(out var options) &&
                options != null &&
                !options.WeldVertices)
            {
                message += " " + Localization.S("new_extractor.invalid_mapping.weld_hint");
            }

            EditorUtility.DisplayDialog(
                Localization.S("new_extractor.invalid_mapping.title"),
                message,
                Localization.S("data.dialog.ok"));
        }

        private void RefreshMeshRequests()
        {
            meshRequests.Clear();
            skippedMeshes.Clear();

            if (sourceFBX == null || originFBX == null)
            {
                return;
            }

            var seen = new HashSet<string>();
            foreach (var sourceRenderer in sourceFBX.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var sourceMesh = sourceRenderer.sharedMesh;
                if (sourceMesh == null || sourceMesh.blendShapeCount == 0)
                {
                    continue;
                }

                string sourcePath = GetRelativePath(sourceRenderer.transform, sourceFBX.transform);
                var originRenderer = FindOriginRenderer(sourcePath, sourceRenderer.name);
                var originMesh = originRenderer != null ? originRenderer.sharedMesh : null;
                if (originMesh == null)
                {
                    skippedMeshes.Add(Localization.SF("new_extractor.missing_origin_mesh", sourcePath));
                    continue;
                }

                string originPath = GetRelativePath(originRenderer.transform, originFBX.transform);
                var request = new MeshFeatureExtractionMeshRequest(originPath, originMesh.name);
                if (seen.Add(MeshFeatureExtractionSession.BuildMeshKey(request.MeshPath, request.MeshName)))
                {
                    meshRequests.Add(request);
                }
            }
        }

        private SkinnedMeshRenderer FindOriginRenderer(string sourcePath, string sourceRendererName)
        {
            Transform originTransform = string.IsNullOrEmpty(sourcePath)
                ? originFBX.transform
                : originFBX.transform.Find(sourcePath);
            var byPath = originTransform != null
                ? originTransform.GetComponent<SkinnedMeshRenderer>()
                : null;
            if (byPath != null)
            {
                return byPath;
            }

            return originFBX.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .FirstOrDefault(renderer => renderer.name == sourceRendererName);
        }

        private void ResetFeatureOptions()
        {
            featureOptionsSet = new MeshFeatureExtractionOptionsSet();
            EnsureFeatureOptions();
        }

        private void EnsureFeatureOptions()
        {
            featureOptionsSet ??= new MeshFeatureExtractionOptionsSet();
            foreach (var provider in MeshFeatureExtractionOptionsProviderRegistry.Providers)
            {
                if (provider == null || featureOptionsSet.TryGet(provider.OptionsType, out _))
                {
                    continue;
                }

                featureOptionsSet.Set(provider.OptionsType, provider.CreateDefaultOptions());
            }
        }

        private static string GetRelativePath(Transform target, Transform root)
        {
            if (target == null || root == null || target == root)
            {
                return string.Empty;
            }

            var parts = new Stack<string>();
            var current = target;
            while (current != null && current != root)
            {
                parts.Push(current.name);
                current = current.parent;
            }

            return string.Join("/", parts);
        }
    }
}
