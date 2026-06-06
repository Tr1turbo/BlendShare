using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShapeShare;
using Triturbo.BlendShapeShare.BlendShapeData;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Features.BlendShapes;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Triturbo.BlendShare.Editor
{
    public sealed class BlendShareNewExtractorEditor : EditorWindow
    {
        private GameObject originFBX;
        private GameObject sourceFBX;
        private string defaultName = "";
        private readonly List<MeshFeatureExtractionMeshRequest> meshRequests = new();
        private readonly List<string> skippedMeshes = new();
        private MeshFeatureExtractionOptionsSet featureOptionsSet = new();
        private readonly Dictionary<string, object> featureEditorData = new();
        private IMeshFeatureExtractionOptionsProvider[] featureProviders;
        private FbxImporterSettingsComparison importerComparison;
        private Vector2 scrollPosition;
        private int selectedTab;
        private VisualElement tabContent;
        private readonly List<ToolbarButton> tabButtons = new();

        [MenuItem("Tools/BlendShare/Feature Extractor")]
        public static void ShowWindow()
        {
            GetWindow<BlendShareNewExtractorEditor>("BlendShare Extractor");
        }

        private void OnEnable()
        {
            EnsureFeatureOptions();
        }

        public void CreateGUI()
        {
            RefreshUi();
        }

        private void RefreshUi()
        {
            EnsureFeatureProviders();
            rootVisualElement.Clear();
            rootVisualElement.style.flexDirection = FlexDirection.Column;
            rootVisualElement.style.flexGrow = 1f;
            rootVisualElement.style.paddingLeft = 8f;
            rootVisualElement.style.paddingRight = 8f;
            rootVisualElement.style.paddingTop = 8f;
            rootVisualElement.style.paddingBottom = 8f;

            BuildHeader(rootVisualElement);
            BuildTabs(rootVisualElement);

            tabContent = new VisualElement();
            tabContent.style.flexGrow = 1f;
            tabContent.style.minHeight = 0f;
            rootVisualElement.Add(tabContent);

            RefreshTabContent();
        }

        private void RefreshTabContent()
        {
            tabContent?.Clear();
            UpdateTabButtonState();
            if (selectedTab == 0)
            {
                tabContent?.Add(CreateGlobalPageElement());
            }
            else
            {
                tabContent?.Add(CreateFeaturePageElement(featureProviders.ElementAtOrDefault(selectedTab - 1)));
            }
        }

        private void BuildHeader(VisualElement parent)
        {
            if (EditorWidgets.BannerIcon != null)
            {
                var banner = new Image
                {
                    image = EditorWidgets.BannerIcon,
                    scaleMode = ScaleMode.ScaleToFit
                };
                banner.style.height = 42f;
                banner.style.alignSelf = Align.Center;
                banner.style.width = 168f;
                parent.Add(banner);
            }

            var title = new Label(Localization.S("new_extractor.title"));
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.marginTop = 6f;
            title.style.marginBottom = 6f;
            parent.Add(title);

            var language = new IMGUIContainer(Localization.DrawLanguageSelection);
            language.style.marginBottom = 6f;
            parent.Add(language);
        }

        private void BuildTabs(VisualElement parent)
        {
            string[] tabLabels = new[] { "Global" }
                .Concat(featureProviders.Select(provider => provider.TabLabel))
                .ToArray();
            if (selectedTab >= tabLabels.Length)
            {
                selectedTab = 0;
            }

            tabButtons.Clear();
            var toolbar = new Toolbar();
            toolbar.style.marginBottom = 6f;
            for (int i = 0; i < tabLabels.Length; i++)
            {
                int tabIndex = i;
                var button = new ToolbarButton(() =>
                {
                    selectedTab = tabIndex;
                    RefreshTabContent();
                })
                {
                    text = tabLabels[i]
                };
                tabButtons.Add(button);
                toolbar.Add(button);
            }

            parent.Add(toolbar);
            UpdateTabButtonState();
        }

        private void UpdateTabButtonState()
        {
            for (int i = 0; i < tabButtons.Count; i++)
            {
                tabButtons[i].SetEnabled(i != selectedTab);
            }
        }

        private VisualElement CreateGlobalPageElement()
        {
            var scrollView = new ScrollView();
            scrollView.style.flexGrow = 1f;
            scrollView.Add(CreateHelpBox("Global Settings", HelpBoxMessageType.None));
            scrollView.Add(CreateHelpBox(
                "Select the original FBX and source FBX, compare importer settings, refresh meshes, then configure each feature tab.",
                HelpBoxMessageType.Info));
            scrollView.Add(CreateFbxFieldsElement());
            scrollView.Add(CreateImporterComparisonElement());
            scrollView.Add(CreateMeshRefreshElement());
            scrollView.Add(CreateSaveElement());
            return scrollView;
        }

        private VisualElement CreateFbxFieldsElement()
        {
            var container = CreateSection();
            var originField = CreateFbxObjectField(Localization.S("origin_fbx"), originFBX, value =>
            {
                originFBX = value;
                OnFbxSelectionChanged();
            });
            var sourceField = CreateFbxObjectField(Localization.S("source_fbx"), sourceFBX, value =>
            {
                sourceFBX = value;
                if (sourceFBX != null && string.IsNullOrWhiteSpace(defaultName))
                {
                    defaultName = sourceFBX.name;
                }

                OnFbxSelectionChanged();
            });
            var defaultNameField = new TextField(Localization.S("extractor.default_asset_name"))
            {
                value = defaultName
            };
            var deformerField = new TextField(Localization.S("extractor.deformer_id"))
            {
                value = "+BlendShare-" + defaultName
            };
            deformerField.SetEnabled(false);
            defaultNameField.RegisterValueChangedCallback(evt =>
            {
                defaultName = evt.newValue;
                deformerField.SetValueWithoutNotify("+BlendShare-" + defaultName);
            });

            container.Add(originField);
            container.Add(sourceField);
            container.Add(defaultNameField);
            container.Add(deformerField);
            return container;
        }

        private ObjectField CreateFbxObjectField(
            string label,
            GameObject current,
            System.Action<GameObject> onChanged)
        {
            var field = new ObjectField(label)
            {
                objectType = typeof(GameObject),
                allowSceneObjects = false,
                value = current
            };
            field.RegisterValueChangedCallback(evt =>
            {
                if (evt.newValue == null)
                {
                    onChanged(null);
                    return;
                }

                if (EditorWidgets.IsFBXGameObject(evt.newValue, out var fbx))
                {
                    onChanged(fbx);
                    return;
                }

                field.SetValueWithoutNotify(evt.previousValue);
            });
            return field;
        }

        private void OnFbxSelectionChanged()
        {
            ResetFeatureOptions();
            RefreshExtractionState();
            RefreshUi();
        }

        private VisualElement CreateImporterComparisonElement()
        {
            var container = CreateSection();
            container.Add(CreateHeaderLabel("Importer Settings Comparison"));
            importerComparison ??= FbxImporterSettingsComparison.Compare(originFBX, sourceFBX);
            container.Add(CreateHelpBox(
                importerComparison.Message,
                importerComparison.HasDifferences ? HelpBoxMessageType.Warning : HelpBoxMessageType.Info));
            container.Add(new Label($"Global Scale    Original: {importerComparison.OriginGlobalScale}    Source: {importerComparison.SourceGlobalScale}"));
            container.Add(new Label($"Bake Axis Conversion    Original: {importerComparison.OriginBakeAxisConversion}    Source: {importerComparison.SourceBakeAxisConversion}"));

            var copyButton = new Button(() =>
            {
                if (FbxImporterSettingsComparison.CopyGeometrySettings(originFBX, sourceFBX))
                {
                    ResetFeatureOptions();
                    RefreshExtractionState();
                    RefreshUi();
                }
            })
            {
                text = "Make source importer settings same as original"
            };
            copyButton.SetEnabled(importerComparison.HasDifferences);
            container.Add(copyButton);
            return container;
        }

        private VisualElement CreateMeshRefreshElement()
        {
            var container = CreateSection();
            if (sourceFBX == null || originFBX == null)
            {
                container.Add(CreateHelpBox(Localization.S("new_extractor.assign_fbx_hint"), HelpBoxMessageType.Info));
                return container;
            }

            container.Add(new Button(() =>
            {
                ResetFeatureOptions();
                RefreshExtractionState();
                RefreshUi();
            })
            {
                text = Localization.S("new_extractor.refresh")
            });

            foreach (string skipped in skippedMeshes)
            {
                container.Add(CreateHelpBox(skipped, HelpBoxMessageType.Warning));
            }

            return container;
        }

        private VisualElement CreateSaveElement()
        {
            var container = CreateSection();
            bool enabled = sourceFBX != null &&
                           originFBX != null &&
                           HasSelectedFeatures();
#if !ENABLE_FBX_SDK
            enabled = false;
            container.Add(CreateHelpBox(Localization.S("data.fbx_sdk_missing"), HelpBoxMessageType.Warning));
#endif
            var saveButton = new Button(SaveBlendShareAsset)
            {
                text = Localization.S("new_extractor.save_asset")
            };
            saveButton.SetEnabled(enabled);
            container.Add(saveButton);
            return container;
        }

        private VisualElement CreateFeaturePageElement(IMeshFeatureExtractionOptionsProvider provider)
        {
            if (provider == null ||
                !featureOptionsSet.TryGet(provider.OptionsType, out var options) ||
                options == null)
            {
                return new VisualElement();
            }

            if (sourceFBX == null || originFBX == null)
            {
                return CreateHelpBox(Localization.S("new_extractor.assign_fbx_hint"), HelpBoxMessageType.Info);
            }

            var context = new MeshFeatureOptionsEditorContext(
                sourceFBX,
                originFBX,
                meshRequests,
                featureEditorData,
                position.height);

            if (provider is IUIToolkitMeshFeatureOptionsProvider uiToolkitProvider)
            {
                var element = uiToolkitProvider.CreateOptionsElement(options, context);
                element.style.flexGrow = 1f;
                element.style.minHeight = 0f;
                return element;
            }

            var fallback = new IMGUIContainer(() => provider.DrawOptionsGUI(options, context));
            fallback.style.flexGrow = 1f;
            return fallback;
        }

        private static VisualElement CreateSection()
        {
            var section = new VisualElement();
            section.style.marginBottom = 8f;
            section.style.paddingTop = 6f;
            section.style.paddingBottom = 6f;
            section.style.paddingLeft = 6f;
            section.style.paddingRight = 6f;
            section.style.borderBottomWidth = 1f;
            section.style.borderTopWidth = 1f;
            section.style.borderLeftWidth = 1f;
            section.style.borderRightWidth = 1f;
            section.style.borderBottomColor = new Color(0.22f, 0.22f, 0.22f);
            section.style.borderTopColor = new Color(0.22f, 0.22f, 0.22f);
            section.style.borderLeftColor = new Color(0.22f, 0.22f, 0.22f);
            section.style.borderRightColor = new Color(0.22f, 0.22f, 0.22f);
            return section;
        }

        private static Label CreateHeaderLabel(string text)
        {
            var label = new Label(text);
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.marginBottom = 4f;
            return label;
        }

        private static VisualElement CreateHelpBox(string message, HelpBoxMessageType type)
        {
            if (type == HelpBoxMessageType.None)
            {
                return CreateHeaderLabel(message);
            }

            return new HelpBox(message, type);
        }

        private void DrawGlobalPage()
        {
            EditorGUILayout.LabelField("Global Settings", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Select the original FBX and source FBX, compare importer settings, refresh meshes, then configure each feature tab.",
                MessageType.Info);
            DrawFbxFields();
            DrawImporterComparison();
            DrawMeshRefresh();
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
                RefreshExtractionState();
            }

            defaultName = EditorGUILayout.TextField(Localization.G("extractor.default_asset_name"), defaultName);

            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.TextField(Localization.G("extractor.deformer_id"), "+BlendShare-" + defaultName);
            EditorGUI.EndDisabledGroup();
        }

        private void DrawImporterComparison()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Importer Settings Comparison", EditorStyles.boldLabel);
            importerComparison ??= FbxImporterSettingsComparison.Compare(originFBX, sourceFBX);
            EditorGUILayout.HelpBox(importerComparison.Message, importerComparison.HasDifferences ? MessageType.Warning : MessageType.Info);

            EditorGUI.BeginDisabledGroup(!importerComparison.CanCompare);
            EditorGUILayout.LabelField("Global Scale", $"Original: {importerComparison.OriginGlobalScale}  Source: {importerComparison.SourceGlobalScale}");
            EditorGUILayout.LabelField("Bake Axis Conversion", $"Original: {importerComparison.OriginBakeAxisConversion}  Source: {importerComparison.SourceBakeAxisConversion}");
            EditorGUI.EndDisabledGroup();

            EditorGUI.BeginDisabledGroup(!importerComparison.HasDifferences);
            if (GUILayout.Button("Make source importer settings same as original"))
            {
                if (FbxImporterSettingsComparison.CopyGeometrySettings(originFBX, sourceFBX))
                {
                    ResetFeatureOptions();
                    RefreshExtractionState();
                }
            }
            EditorGUI.EndDisabledGroup();
        }

        private void DrawMeshRefresh()
        {
            EditorGUILayout.Space(4);

            if (sourceFBX == null || originFBX == null)
            {
                EditorGUILayout.HelpBox(Localization.S("new_extractor.assign_fbx_hint"), MessageType.Info);
                return;
            }

            if (GUILayout.Button(Localization.S("new_extractor.refresh")))
            {
                ResetFeatureOptions();
                RefreshExtractionState();
            }

            foreach (string skipped in skippedMeshes)
            {
                EditorGUILayout.HelpBox(skipped, MessageType.Warning);
            }
        }

        private void DrawFeaturePage(IMeshFeatureExtractionOptionsProvider provider)
        {
            EnsureFeatureOptions();
            if (provider == null ||
                !featureOptionsSet.TryGet(provider.OptionsType, out var options) ||
                options == null)
            {
                return;
            }

            if (sourceFBX == null || originFBX == null)
            {
                EditorGUILayout.HelpBox(Localization.S("new_extractor.assign_fbx_hint"), MessageType.Info);
                return;
            }

            var context = new MeshFeatureOptionsEditorContext(
                sourceFBX,
                originFBX,
                meshRequests,
                featureEditorData,
                GetFeaturePageAvailableHeight());
            provider.DrawOptionsGUI(options, context);
        }

        private float GetFeaturePageAvailableHeight()
        {
            return position.height;
        }

        private void DrawSaveButton()
        {
            EditorGUILayout.Separator();

            bool enabled = sourceFBX != null &&
                           originFBX != null &&
                           HasSelectedFeatures();
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

            var selectedRequests = GetSelectedFeatureRequests();
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

        private bool HasSelectedFeatures()
        {
            return (featureOptionsSet?.All ?? Enumerable.Empty<MeshFeatureExtractionOptions>())
                .Any(options => options != null && options.HasSelectedWork(meshRequests));
        }

        private List<MeshFeatureExtractionMeshRequest> GetSelectedFeatureRequests()
        {
            var options = (featureOptionsSet?.All ?? Enumerable.Empty<MeshFeatureExtractionOptions>())
                .Where(option => option != null)
                .ToArray();
            var selected = new List<MeshFeatureExtractionMeshRequest>();
            var seen = new HashSet<string>();

            foreach (var request in meshRequests.Where(request => request != null))
            {
                if (!options.Any(option => option.ShouldExtractMesh(request)))
                {
                    continue;
                }

                if (seen.Add(MeshFeatureExtractionSession.BuildMeshKey(request.Path)))
                {
                    selected.Add(request);
                }
            }

            return selected;
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

            EditorUtility.DisplayDialog(
                Localization.S("new_extractor.invalid_mapping.title"),
                Localization.S("new_extractor.invalid_mapping.message"),
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
                if (sourceMesh == null)
                {
                    continue;
                }

                string sourcePath = MeshNodePath.GetRelativePath(sourceRenderer.transform, sourceFBX.transform);
                var originRenderer = FindOriginRenderer(sourcePath);
                var originMesh = originRenderer != null ? originRenderer.sharedMesh : null;
                if (originMesh == null)
                {
                    skippedMeshes.Add(Localization.SF("new_extractor.missing_origin_mesh", sourcePath));
                    continue;
                }

                string originPath = MeshNodePath.GetRelativePath(originRenderer.transform, originFBX.transform);
                if (originPath != sourcePath)
                {
                    skippedMeshes.Add(Localization.SF("new_extractor.missing_origin_mesh", sourcePath));
                    continue;
                }

                var request = new MeshFeatureExtractionMeshRequest(originPath);
                if (seen.Add(MeshFeatureExtractionSession.BuildMeshKey(request.Path)))
                {
                    meshRequests.Add(request);
                }
            }
        }

        private void RefreshExtractionState()
        {
            RefreshMeshRequests();
            RefreshComparisonCaches();
        }

        private void RefreshComparisonCaches()
        {
            featureEditorData.Clear();
            importerComparison = FbxImporterSettingsComparison.Compare(originFBX, sourceFBX);
            if (sourceFBX == null || originFBX == null || meshRequests.Count == 0)
            {
                return;
            }

            using var inspectionSession = FbxInspectionSession.Open(sourceFBX, originFBX);
            importerComparison = inspectionSession.GetImporterComparison();

            foreach (var provider in featureProviders.OfType<IMeshFeatureInspectionProvider>())
            {
                if (provider is IMeshFeatureExtractionOptionsProvider optionsProvider)
                {
                    featureEditorData[optionsProvider.FeatureId] =
                        provider.BuildInspectionData(inspectionSession, meshRequests);
                }
            }
        }

        private SkinnedMeshRenderer FindOriginRenderer(string sourcePath)
        {
            Transform originTransform = MeshNodePath.FindRelativeTransform(originFBX.transform, sourcePath);
            var byPath = originTransform != null
                ? originTransform.GetComponent<SkinnedMeshRenderer>()
                : null;
            if (byPath != null)
            {
                return byPath;
            }

            return null;
        }

        private void ResetFeatureOptions()
        {
            featureOptionsSet = new MeshFeatureExtractionOptionsSet();
            EnsureFeatureOptions();
        }

        private void EnsureFeatureOptions()
        {
            EnsureFeatureProviders();
            featureOptionsSet ??= new MeshFeatureExtractionOptionsSet();
            foreach (var provider in featureProviders)
            {
                if (provider == null || featureOptionsSet.TryGet(provider.OptionsType, out _))
                {
                    continue;
                }

                featureOptionsSet.Set(provider.OptionsType, provider.CreateDefaultOptions());
            }
        }

        private void EnsureFeatureProviders()
        {
            featureProviders ??= GetFeatureOptionsProviders().ToArray();
        }

        private static IEnumerable<IMeshFeatureExtractionOptionsProvider> GetFeatureOptionsProviders()
        {
            return BlendShareFeatureModules.All
                .Select(module => module?.OptionsProvider)
                .Where(provider => provider != null)
                .OrderBy(provider => provider.DisplayOrder)
                .ThenBy(provider => provider.GetType().FullName, System.StringComparer.Ordinal);
        }

    }
}
