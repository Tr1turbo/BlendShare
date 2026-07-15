using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShapeShare;
using Triturbo.BlendShapeShare.BlendShapeData;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Hashing;
using Triturbo.BlendShare.Preview;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Triturbo.BlendShare.Editor
{
    public sealed class BlendSharePatchCreatorWindow : EditorWindow
    {
        private GameObject originFBX;
        private GameObject sourceFBX;
        private GameObject inspectedOriginFBX;
        private GameObject inspectedSourceFBX;
        private FbxInspectionSession inspectionSession;
        private string defaultName = "";
        private readonly List<MeshFeatureExtractionMeshRequest> meshRequests = new();
        private readonly List<string> skippedMeshes = new();
        private MeshFeatureExtractionOptionsSet featureOptionsSet = new();
        private readonly Dictionary<string, object> featureEditorData = new();
        private IMeshFeatureExtractionOptionsProvider[] featureProviders;
        private FbxImporterSettingsComparison importerComparison;
        private Vector2 scrollPosition;
        private Vector2 previewMeshScrollOffset;
        private ScrollView previewMeshScrollView;
        private System.Action<float> previewMeshScrollCallback;
        private int selectedTab;
        private VisualElement tabContent;
        private readonly List<ToolbarButton> tabButtons = new();
        private FbxRawComparePreviewPanel previewPanel;
        private readonly Dictionary<string, bool> previewMeshFoldouts = new();
        private readonly List<System.Action> previewMeshSelectionRefreshers = new();
        private StickyHeaderElement previewMeshStickyHeader;
        private bool comparisonCacheDirty;

        [MenuItem("Tools/BlendShare/Patch Creator")]
        public static void ShowWindow()
        {
            GetWindow<BlendSharePatchCreatorWindow>(Localization.S("patch_creator.window_title"));
        }

        private void OnEnable()
        {
            EnsureFeatureOptions();
            previewPanel ??= new FbxRawComparePreviewPanel();
            Localization.LanguageChanged += OnLanguageChanged;
        }

        private void OnDisable()
        {
            Localization.LanguageChanged -= OnLanguageChanged;
            CaptureAndReleasePreviewMeshScrollView();
            previewPanel?.Dispose();
            previewPanel = null;
            inspectionSession?.Dispose();
            inspectionSession = null;
        }

        private void OnLanguageChanged()
        {
            titleContent = new GUIContent(Localization.S("patch_creator.window_title"));
            rootVisualElement.schedule.Execute(RefreshUi);
        }

        public void CreateGUI()
        {
            RefreshUi();
        }

        private void RefreshUi()
        {
            EnsureFeatureProviders();
            titleContent = new GUIContent(Localization.S("patch_creator.window_title"));
            CaptureAndReleasePreviewMeshScrollView();
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
            CaptureAndReleasePreviewMeshScrollView();
            tabContent?.Clear();
            UpdateTabButtonState();
            if (selectedTab == 0)
            {
                tabContent?.Add(CreateGlobalPageElement());
            }
            else if (selectedTab == 1)
            {
                tabContent?.Add(CreatePreviewPageElement());
            }
            else
            {
                tabContent?.Add(CreateFeaturePageElement(featureProviders.ElementAtOrDefault(selectedTab - 2)));
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

            var title = new Label(Localization.S("patch_creator.title"));
            Inspector.BlendShareInspectorUi.StyleHeading(title);
            title.style.marginTop = 6f;
            title.style.marginBottom = 6f;
            parent.Add(title);

            var language = new IMGUIContainer(Localization.DrawLanguageSelection);
            language.style.marginBottom = 6f;
            parent.Add(language);
        }

        private void BuildTabs(VisualElement parent)
        {
            string[] tabLabels = new[] { Localization.S("patch_creator.global_tab"), Localization.S("patch_creator.alignment_tab") }
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
                    if (selectedTab == 1 && tabIndex != 1 && comparisonCacheDirty)
                    {
                        RefreshComparisonCaches();
                    }

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
            scrollView.Add(CreateHelpBox(Localization.S("patch_creator.global_settings"), HelpBoxMessageType.None));
            scrollView.Add(CreateHelpBox(
                Localization.S("patch_creator.global_help"),
                HelpBoxMessageType.Info));
            scrollView.Add(CreateFbxFieldsElement());
            scrollView.Add(CreateImporterComparisonElement());
            scrollView.Add(CreateMeshRefreshElement());
            scrollView.Add(CreateSaveElement());
            return scrollView;
        }

        private VisualElement CreatePreviewPageElement()
        {
            BindPreviewPanel();

            var root = new VisualElement();
            root.style.flexGrow = 1f;
            root.style.minHeight = 0f;
            root.style.flexDirection = FlexDirection.Column;

            root.Add(CreatePreviewMeshListElement());

            var preview = new IMGUIContainer(() =>
            {
                float availableHeight = root.layout.height;
                if (float.IsNaN(availableHeight) || availableHeight <= 0f)
                {
                    availableHeight = position.height;
                }

                previewPanel.DrawFixedPreview(availableHeight);
            });
            preview.style.flexShrink = 0f;
            preview.style.marginTop = 6f;
            root.Add(preview);
            return root;
        }

        private VisualElement CreatePreviewMeshListElement()
        {
            previewMeshSelectionRefreshers.Clear();
            var root = new VisualElement();
            root.style.flexGrow = 1f;
            root.style.minHeight = 0f;
            root.style.flexDirection = FlexDirection.Column;

            var paths = previewPanel?.GetMeshPaths() ?? System.Array.Empty<string>();
            if (paths.Length == 0)
            {
                return root;
            }

            root.Add(CreateAlignmentActionsElement());

            var stickyHeader = new StickyHeaderElement(CreatePreviewStickyHeaderContent);
            previewMeshStickyHeader = stickyHeader;
            root.Add(stickyHeader);

            Vector2 restoreScrollOffset = previewMeshScrollOffset;
            var scrollView = new ScrollView(ScrollViewMode.Vertical);
            scrollView.style.flexGrow = 1f;
            scrollView.style.minHeight = 0f;
            scrollView.style.paddingRight = 2f;
            previewMeshScrollView = scrollView;
            previewMeshScrollCallback = value => previewMeshScrollOffset.y = value;
            scrollView.verticalScroller.valueChanged += previewMeshScrollCallback;
            root.Add(scrollView);

            var sections = new List<StickyHeaderSection>();
            foreach (string path in paths)
            {
                var section = CreatePreviewMeshSection(path);
                sections.Add(section);
                scrollView.Add(section.Root);
            }

            var overview = StickyHeaderOverview.Create(sections, () => Localization.S("common.meshes"));
            stickyHeader.Bind(scrollView, sections, overview);
            scrollView.schedule.Execute(() => scrollView.scrollOffset = restoreScrollOffset);
            return root;
        }

        private void CaptureAndReleasePreviewMeshScrollView()
        {
            if (previewMeshScrollView == null)
            {
                return;
            }

            previewMeshScrollOffset = previewMeshScrollView.scrollOffset;
            if (previewMeshScrollCallback != null)
            {
                previewMeshScrollView.verticalScroller.valueChanged -= previewMeshScrollCallback;
            }

            previewMeshScrollView = null;
            previewMeshScrollCallback = null;
        }

        private void SelectPreviewPath(string path)
        {
            previewPanel?.ShowPath(path, true);
            RefreshPreviewMeshSelectionState();
        }

        private void SelectPreviewAll()
        {
            previewPanel?.ShowAll(true);
            RefreshPreviewMeshSelectionState();
        }

        private void RefreshPreviewMeshSelectionState()
        {
            foreach (var refresh in previewMeshSelectionRefreshers)
            {
                refresh?.Invoke();
            }

            previewMeshStickyHeader?.RequestRefresh();
        }

        private VisualElement CreatePreviewStickyHeaderContent(StickyHeaderContext context)
        {
            var root = new VisualElement();
            root.style.flexGrow = 1f;
            root.style.flexDirection = FlexDirection.Row;
            root.style.alignItems = Align.Center;

            var titleButton = new Button(context.ScrollToSection)
            {
                text = context.Section.Title
            };
            titleButton.style.flexGrow = 1f;
            titleButton.style.unityTextAlign = TextAnchor.MiddleLeft;
            titleButton.style.backgroundColor = Color.clear;
            titleButton.style.borderBottomWidth = 0f;
            titleButton.style.borderTopWidth = 0f;
            titleButton.style.borderLeftWidth = 0f;
            titleButton.style.borderRightWidth = 0f;
            root.Add(titleButton);

            bool isShown = !context.IsOverview && context.Section.IsEnabled();
            var show = new Button(() => context.Section.SetEnabled(true))
            {
                text = Localization.S(isShown
                    ? "patch_creator.preview.shown"
                    : "patch_creator.preview.show")
            };
            show.SetEnabled(!isShown);
            show.style.display = context.IsOverview ? DisplayStyle.None : DisplayStyle.Flex;
            show.style.marginLeft = 4f;
            root.Add(show);

            bool isShowingAll = previewPanel?.IsShowingAll() == true;
            var showAll = new Button(SelectPreviewAll)
            {
                text = Localization.S(isShowingAll
                    ? "patch_creator.preview.all_shown"
                    : "patch_creator.preview.show_all")
            };
            showAll.SetEnabled(!isShowingAll);
            showAll.style.marginLeft = 4f;
            root.Add(showAll);
            return root;
        }

        private VisualElement CreateAlignmentActionsElement()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginBottom = 6f;
            row.style.paddingLeft = 6f;
            row.style.paddingRight = 6f;

            var spacer = new VisualElement();
            spacer.style.flexGrow = 1f;
            row.Add(spacer);

            var resetAllOffsets = new Button(() => previewPanel?.ResetAllOffsets())
            {
                text = Localization.S("patch_creator.preview.reset_all_offsets")
            };
            row.Add(resetAllOffsets);

            var autoAlignAll = new Button(() => previewPanel?.AutoAlignAll())
            {
                text = Localization.S("patch_creator.preview.auto_align_all"),
                tooltip = Localization.S("patch_creator.preview.auto_align_all_tooltip")
            };
            autoAlignAll.style.marginLeft = 4f;
            row.Add(autoAlignAll);
            return row;
        }

        private StickyHeaderSection CreatePreviewMeshSection(string path)
        {
            string key = MeshFeatureExtractionSession.BuildMeshKey(path);
            bool shown = previewPanel.IsShownPath(path);
            if (!previewMeshFoldouts.ContainsKey(key))
            {
                previewMeshFoldouts[key] = shown;
            }

            var sectionRoot = new VisualElement();
            sectionRoot.style.marginBottom = 5f;
            sectionRoot.style.paddingBottom = 4f;
            bool isPathShown = previewPanel.IsShownPath(path);
            Color dividerColor = EditorGUIUtility.isProSkin
                ? new Color(0.12f, 0.12f, 0.12f)
                : new Color(0.72f, 0.72f, 0.72f);
            sectionRoot.style.borderTopWidth = 0f;
            sectionRoot.style.borderRightWidth = 0f;
            sectionRoot.style.borderBottomWidth = 1f;
            sectionRoot.style.borderLeftWidth = isPathShown ? 2f : 0f;
            sectionRoot.style.borderBottomColor = dividerColor;
            sectionRoot.style.borderLeftColor = EditorGUIUtility.isProSkin
                ? new Color(0.32f, 0.50f, 0.66f, 0.75f)
                : new Color(0.20f, 0.40f, 0.62f, 0.75f);
            sectionRoot.style.backgroundColor = isPathShown
                ? (EditorGUIUtility.isProSkin
                    ? new Color(0.22f, 0.22f, 0.22f)
                    : new Color(0.86f, 0.86f, 0.86f))
                : Color.clear;

            var headerRow = new VisualElement();
            headerRow.style.flexDirection = FlexDirection.Row;
            headerRow.style.alignItems = Align.Center;
            headerRow.style.paddingTop = 4f;
            headerRow.style.paddingLeft = 6f;
            headerRow.style.paddingRight = 2f;
            sectionRoot.Add(headerRow);

            var foldout = new Foldout
            {
                text = path,
                value = previewMeshFoldouts[key]
            };
            foldout.style.flexGrow = 1f;
            headerRow.Add(foldout);

            bool isShown = previewPanel.IsShownPath(path);
            var showButton = new Button(() =>
            {
                SelectPreviewPath(path);
            })
            {
                text = Localization.S(isShown
                    ? "patch_creator.preview.shown"
                    : "patch_creator.preview.show")
            };
            showButton.SetEnabled(!isShown);
            showButton.style.minWidth = 72f;
            showButton.style.marginLeft = 4f;
            headerRow.Add(showButton);

            var controls = new VisualElement();
            controls.style.marginLeft = 24f;
            controls.style.marginRight = 6f;
            controls.style.paddingTop = 4f;
            controls.style.display = foldout.value ? DisplayStyle.Flex : DisplayStyle.None;
            sectionRoot.Add(controls);

            bool controlsBuilt = false;
            IMGUIContainer sourceOffsetEditor = null;
            void BuildControls()
            {
                if (controlsBuilt)
                {
                    return;
                }

                controlsBuilt = true;
                AddPreviewMeshInfo(controls, path);
                sourceOffsetEditor = new IMGUIContainer(() => previewPanel.DrawSourceOffsetEditor());
                sourceOffsetEditor.style.marginTop = 4f;
                sourceOffsetEditor.style.display = previewPanel.IsShownPath(path)
                    ? DisplayStyle.Flex
                    : DisplayStyle.None;
                controls.Add(sourceOffsetEditor);
            }

            if (foldout.value)
            {
                BuildControls();
            }

            foldout.RegisterValueChangedCallback(evt =>
            {
                previewMeshFoldouts[key] = evt.newValue;
                controls.style.display = evt.newValue ? DisplayStyle.Flex : DisplayStyle.None;
                if (evt.newValue)
                {
                    BuildControls();
                }
            });

            void RefreshSelectionState()
            {
                bool selected = previewPanel.IsShownPath(path);
                showButton.text = Localization.S(selected
                    ? "patch_creator.preview.shown"
                    : "patch_creator.preview.show");
                showButton.SetEnabled(!selected);
                sectionRoot.style.borderLeftWidth = selected ? 2f : 0f;
                sectionRoot.style.backgroundColor = selected
                    ? (EditorGUIUtility.isProSkin
                        ? new Color(0.22f, 0.22f, 0.22f)
                        : new Color(0.86f, 0.86f, 0.86f))
                    : Color.clear;
                if (sourceOffsetEditor != null)
                {
                    sourceOffsetEditor.style.display = selected
                        ? DisplayStyle.Flex
                        : DisplayStyle.None;
                }
            }

            previewMeshSelectionRefreshers.Add(RefreshSelectionState);
            RefreshSelectionState();

            return new StickyHeaderSection(
                sectionRoot,
                headerRow,
                () => foldout.text,
                () => previewPanel.IsShownPath(path) ? 1 : 0,
                1,
                () => previewPanel.IsShownPath(path),
                value =>
                {
                    if (value)
                    {
                        SelectPreviewPath(path);
                    }
                },
                () => { },
                () => { },
                () => { },
                () => { });
        }

        private void AddPreviewMeshInfo(VisualElement root, string path)
        {
            FbxPreviewMeshInfo originalInfo = previewPanel.GetOriginalMeshInfo(path);
            FbxPreviewMeshInfo sourceInfo = previewPanel.GetSourceMeshInfo(path);
            var compatibility = previewPanel.GetTopologyCompatibility(path);
            var comparison = new VisualElement();
            comparison.style.flexDirection = FlexDirection.Row;
            comparison.style.flexWrap = Wrap.Wrap;
            comparison.style.marginBottom = 4f;
            comparison.Add(CreatePreviewMeshInfoCard(
                Localization.S("patch_creator.alignment.original"),
                originalInfo,
                new Color(0.25f, 0.8f, 1f),
                true));
            comparison.Add(CreatePreviewMeshInfoCard(
                Localization.S("patch_creator.alignment.source"),
                sourceInfo,
                new Color(1f, 0.72f, 0.2f),
                false,
                FormatTopologyCompatibilityVerdict(compatibility)));
            root.Add(comparison);

            if (compatibility.State == FbxMeshCompatibilityState.SourceHasFewerControlPoints ||
                compatibility.State == FbxMeshCompatibilityState.TopologyMismatch)
            {
                root.Add(new HelpBox(
                    FormatTopologyCompatibilityDetail(compatibility),
                    HelpBoxMessageType.Error));
            }
            else if (compatibility.HasWarning)
            {
                root.Add(new HelpBox(
                    Localization.SF(
                        "patch_creator.alignment.compatibility_warning",
                        FormatTopologyCompatibilityDetail(compatibility)),
                    HelpBoxMessageType.Warning));
            }
        }

        private static VisualElement CreatePreviewMeshInfoCard(
            string title,
            FbxPreviewMeshInfo info,
            Color accent,
            bool showTopologyHash,
            string topologyCompatibility = null)
        {
            var card = new VisualElement();
            card.style.flexGrow = 1f;
            card.style.flexBasis = 240f;
            card.style.minWidth = 220f;
            card.style.marginRight = 4f;
            card.style.marginBottom = 4f;
            card.style.paddingTop = 5f;
            card.style.paddingBottom = 5f;
            card.style.paddingLeft = 7f;
            card.style.paddingRight = 7f;
            card.style.borderTopLeftRadius = 3f;
            card.style.borderTopRightRadius = 3f;
            card.style.borderBottomLeftRadius = 3f;
            card.style.borderBottomRightRadius = 3f;
            card.style.backgroundColor = EditorGUIUtility.isProSkin
                ? new Color(0.17f, 0.17f, 0.17f, 0.8f)
                : new Color(0.88f, 0.88f, 0.88f, 0.8f);

            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            header.style.marginBottom = 3f;
            var swatch = new VisualElement();
            swatch.style.width = 8f;
            swatch.style.height = 8f;
            swatch.style.marginRight = 5f;
            swatch.style.backgroundColor = accent;
            header.Add(swatch);
            var heading = new Label(title);
            heading.style.unityFontStyleAndWeight = FontStyle.Bold;
            header.Add(heading);
            card.Add(header);

            if (!info.Exists)
            {
                card.Add(CreatePreviewInfoRow("", Localization.S("common.status.unavailable")));
                return card;
            }

            card.Add(CreatePreviewInfoRow(
                Localization.S("patch.mesh_data.fbx_control_points"),
                info.ControlPointCount.ToString()));
            card.Add(CreatePreviewInfoRow(
                Localization.S("patch.mesh_data.faces"),
                info.FaceCount.ToString()));

            if (showTopologyHash)
            {
                string fullHash = info.HasValidTopology ? info.TopologyHash : string.Empty;
                string shortHash = string.IsNullOrEmpty(fullHash)
                    ? Localization.S("common.status.unavailable")
                    : fullHash.Substring(0, System.Math.Min(12, fullHash.Length));
                card.Add(CreatePreviewInfoRow(
                    Localization.S("patch.mesh_data.fbx_topology_hash"),
                    shortHash,
                    fullHash));
            }
            else if (!string.IsNullOrEmpty(topologyCompatibility))
            {
                card.Add(CreatePreviewInfoRow(
                    Localization.S("patch_creator.preview.topology_compatibility"),
                    topologyCompatibility));
            }
            return card;
        }

        private static VisualElement CreatePreviewInfoRow(string label, string value, string tooltip = null)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.FlexStart;
            row.style.marginBottom = 2f;

            var name = new Label(label);
            name.style.width = 118f;
            name.style.opacity = 0.72f;
            row.Add(name);

            var description = new Label(value);
            description.style.flexGrow = 1f;
            description.style.whiteSpace = WhiteSpace.Normal;
            description.tooltip = tooltip;
            row.Add(description);
            return row;
        }

        private VisualElement CreateFbxFieldsElement()
        {
            var container = CreateSection();
            var originField = CreateFbxObjectField(Localization.S("common.original_fbx"), originFBX, value =>
            {
                originFBX = value;
                OnFbxSelectionChanged();
            });
            var sourceField = CreateFbxObjectField(Localization.S("common.source_fbx"), sourceFBX, value =>
            {
                sourceFBX = value;
                if (sourceFBX != null && string.IsNullOrWhiteSpace(defaultName))
                {
                    defaultName = sourceFBX.name;
                }

                OnFbxSelectionChanged();
            });
            var defaultNameField = new TextField(Localization.S("common.default_asset_name"))
            {
                value = defaultName
            };
            var patchIdField = new TextField(Localization.S("common.patch_id"))
            {
                value = "+BlendShare-" + defaultName
            };
            patchIdField.SetEnabled(false);
            defaultNameField.RegisterValueChangedCallback(evt =>
            {
                defaultName = evt.newValue;
                patchIdField.SetValueWithoutNotify("+BlendShare-" + defaultName);
            });

            container.Add(originField);
            container.Add(sourceField);
            container.Add(defaultNameField);
            container.Add(patchIdField);
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
            RefreshExtractionState(true);
            RefreshUi();
        }

        private VisualElement CreateImporterComparisonElement()
        {
            var container = CreateSection();
            container.Add(CreateHeaderLabel(Localization.S("patch_creator.importer_comparison.title")));
            importerComparison ??= FbxImporterSettingsComparison.Compare(originFBX, sourceFBX);
            container.Add(CreateHelpBox(
                importerComparison.Message,
                importerComparison.HasDifferences ? HelpBoxMessageType.Warning : HelpBoxMessageType.Info));
            container.Add(new Label($"{Localization.S("patch_creator.importer_comparison.global_scale")}    {Localization.SF("patch_creator.importer_comparison.original_value", importerComparison.OriginGlobalScale)}    {Localization.SF("patch_creator.importer_comparison.source_value", importerComparison.SourceGlobalScale)}"));
            container.Add(new Label($"{Localization.S("patch_creator.importer_comparison.bake_axis_conversion")}    {Localization.SF("patch_creator.importer_comparison.original_value", importerComparison.OriginBakeAxisConversion)}    {Localization.SF("patch_creator.importer_comparison.source_value", importerComparison.SourceBakeAxisConversion)}"));

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
                text = Localization.S("patch_creator.importer_comparison.copy_to_source")
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
                container.Add(CreateHelpBox(Localization.S("patch_creator.assign_fbx_hint"), HelpBoxMessageType.Info));
                return container;
            }

            container.Add(new Button(() =>
            {
                ResetFeatureOptions();
                RefreshExtractionState(true);
                RefreshUi();
            })
            {
                text = Localization.S("patch_creator.refresh_meshes")
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
                           HasSelectedFeatures() &&
                           !HasBlockingTopologyIssues();
            if (HasBlockingTopologyIssues())
            {
                container.Add(CreateHelpBox(Localization.S("patch_creator.alignment.blocking_topology"), HelpBoxMessageType.Error));
            }
#if !ENABLE_FBX_SDK
            enabled = false;
            container.Add(CreateHelpBox(Localization.S("common.fbx_sdk_missing"), HelpBoxMessageType.Warning));
#endif
            var saveButton = new Button(SaveBlendShareAsset)
            {
                text = Localization.S("patch_creator.save_patch")
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
                return CreateHelpBox(Localization.S("patch_creator.assign_fbx_hint"), HelpBoxMessageType.Info);
            }

            var context = new MeshFeatureOptionsEditorContext(
                sourceFBX,
                originFBX,
                meshRequests,
                featureEditorData,
                position.height,
                ScheduleComparisonRefresh);

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
            Inspector.BlendShareInspectorUi.StyleStrong(label);
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
                    Localization.S("patch_creator.no_contents.title"),
                    Localization.S("patch_creator.no_contents.message"),
                    Localization.S("common.ok"));
                return;
            }

            string path = EditorUtility.SaveFilePanelInProject(
                Localization.S("patch_creator.save_title"),
                $"{defaultName}_BlendShare",
                "asset",
                Localization.S("data.save_file.message"));
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var patch = BlendShareExtractionService.ExtractAndSave(
                sourceFBX,
                originFBX,
                selectedRequests,
                featureOptionsSet,
                path,
                defaultName);

            if (patch == null)
            {
                EditorUtility.DisplayDialog(
                    Localization.S("patch_creator.failed.title"),
                    Localization.S("patch_creator.failed.message"),
                    Localization.S("common.ok"));
                return;
            }

            Selection.activeObject = patch;
            ShowInvalidMappingWarning(patch);
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

        private void ShowInvalidMappingWarning(BlendShareObject patch)
        {
            bool hasInvalidMapping = patch.Meshes.Any(mesh =>
                (mesh.m_Mappings ?? System.Array.Empty<UnityVertexMappingObject>())
                .Any(mapping => mapping != null && !mapping.m_IsValid));
            if (!hasInvalidMapping)
            {
                return;
            }

            EditorUtility.DisplayDialog(
                Localization.S("patch_creator.invalid_mapping.title"),
                Localization.S("patch_creator.invalid_mapping.message"),
                Localization.S("common.ok"));
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
                    skippedMeshes.Add(Localization.SF("patch_creator.missing_original_mesh", sourcePath));
                    continue;
                }

                string originPath = MeshNodePath.GetRelativePath(originRenderer.transform, originFBX.transform);
                if (originPath != sourcePath)
                {
                    skippedMeshes.Add(Localization.SF("patch_creator.missing_original_mesh", sourcePath));
                    continue;
                }

                var request = new MeshFeatureExtractionMeshRequest(originPath);
                if (seen.Add(MeshFeatureExtractionSession.BuildMeshKey(request.Path)))
                {
                    meshRequests.Add(request);
                }
            }
        }

        private void RefreshExtractionState(bool autoAlignAll = false)
        {
            RefreshMeshRequests();
            EnsureInspectionSession();
            if (autoAlignAll && sourceFBX != null && originFBX != null && meshRequests.Count > 0)
            {
                BindPreviewPanel();
                previewPanel.AutoAlignAll();
            }

            RefreshComparisonCaches();
        }

        private void BindPreviewPanel()
        {
            previewPanel ??= new FbxRawComparePreviewPanel();
            previewPanel.Bind(
                inspectionSession,
                featureOptionsSet.GetSourceOffset,
                MarkComparisonCacheDirty,
                ScheduleInspectionRefresh);
        }

        private void RefreshComparisonCaches()
        {
            comparisonCacheDirty = false;
            featureEditorData.Clear();
            EnsureInspectionSession();
            importerComparison = inspectionSession.GetImporterComparison();
            if (sourceFBX == null || originFBX == null || meshRequests.Count == 0)
            {
                return;
            }

            foreach (var request in meshRequests)
            {
                inspectionSession.GetTopologyCompatibility(request.Path);
            }

            foreach (var provider in featureProviders.OfType<IMeshFeatureInspectionProvider>())
            {
                if (provider is IMeshFeatureExtractionOptionsProvider optionsProvider)
                {
                    featureEditorData[optionsProvider.FeatureId] =
                        provider.BuildInspectionData(inspectionSession, featureOptionsSet, meshRequests);
                }
            }
        }

        private void MarkComparisonCacheDirty()
        {
            comparisonCacheDirty = true;
        }

        private void ScheduleComparisonRefresh()
        {
            rootVisualElement.schedule.Execute(() =>
            {
                RefreshComparisonCaches();
                RefreshTabContent();
            });
        }

        private void ScheduleInspectionRefresh()
        {
            rootVisualElement.schedule.Execute(() =>
            {
                ReloadInspectionSession();
                RefreshExtractionState();
                RefreshTabContent();
            });
        }

        private void EnsureInspectionSession()
        {
            if (inspectionSession != null &&
                inspectedOriginFBX == originFBX &&
                inspectedSourceFBX == sourceFBX)
            {
                return;
            }

            ReloadInspectionSession();
        }

        private void ReloadInspectionSession()
        {
            inspectionSession?.Dispose();
            inspectedOriginFBX = originFBX;
            inspectedSourceFBX = sourceFBX;
            inspectionSession = FbxInspectionSession.Open(sourceFBX, originFBX);
        }

        private bool HasBlockingTopologyIssues()
        {
            if (inspectionSession == null)
            {
                return sourceFBX != null && originFBX != null && meshRequests.Count > 0;
            }

            return GetSelectedFeatureRequests().Any(request =>
                !inspectionSession.GetTopologyCompatibility(request.Path).IsCompatible);
        }

        private static string FormatTopologyCompatibilityVerdict(FbxMeshCompatibilityResult result)
        {
            return result.State switch
            {
                FbxMeshCompatibilityState.Exact => Localization.S("patch_creator.alignment.compatible"),
                FbxMeshCompatibilityState.CompatibleWithExtraSourceControlPoints or
                    FbxMeshCompatibilityState.CompatibleWithTopologyChange =>
                    Localization.S("patch_creator.alignment.compatible"),
                FbxMeshCompatibilityState.SourceHasFewerControlPoints or
                    FbxMeshCompatibilityState.TopologyMismatch =>
                    Localization.S("patch_creator.alignment.incompatible"),
                _ => Localization.S("patch_creator.alignment.compatibility_unavailable")
            };
        }

        private static string FormatTopologyCompatibilityDetail(FbxMeshCompatibilityResult result)
        {
            if (result.HasWarning)
            {
                var details = new List<string>();
                if (result.HasExtraSourceControlPoints)
                {
                    details.Add(Localization.SF(
                        "patch_creator.alignment.source_extra_control_points",
                        result.SourceControlPointCount,
                        result.OriginalControlPointCount));
                }

                if (result.HasExtraSourceEdges)
                {
                    details.Add(Localization.S("patch_creator.alignment.topology_extra_edges"));
                }

                return string.Join(" ", details);
            }

            return result.State switch
            {
                FbxMeshCompatibilityState.SourceHasFewerControlPoints => Localization.SF(
                    "patch_creator.alignment.source_fewer_control_points",
                    result.SourceControlPointCount,
                    result.OriginalControlPointCount),
                FbxMeshCompatibilityState.TopologyMismatch =>
                    Localization.S("patch_creator.alignment.topology_mismatch"),
                _ => string.Empty
            };
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
