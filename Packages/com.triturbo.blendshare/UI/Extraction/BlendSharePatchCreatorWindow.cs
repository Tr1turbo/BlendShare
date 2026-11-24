using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Triturbo.BlendShapeShare;
using Triturbo.BlendShapeShare.BlendShapeData;
using Triturbo.BlendShare.EditorAssets.Settings;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Inspector;
using Triturbo.BlendShare.Model.Serialization;
using Triturbo.BlendShare.Pipeline.Extraction;
using Triturbo.BlendShare.Pipeline.Inspection;
using Triturbo.BlendShare.Runtime.Assets;
using Triturbo.BlendShare.UI.Preview;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Triturbo.BlendShare.UI.Extraction
{
    public sealed class BlendSharePatchCreatorWindow : EditorWindow
    {
        private GameObject m_OriginFbx;
        private GameObject m_SourceFbx;
        private GameObject m_InspectedOriginFbx;
        private GameObject m_InspectedSourceFbx;
        private FbxInspectionSession m_InspectionSession;
        private string m_DefaultName = string.Empty;
        private readonly List<string> m_MeshPaths = new List<string>();
        private readonly List<string> m_SkippedMeshes = new List<string>();
        private ExtractionOptionsSet m_FeatureOptionsSet = new ExtractionOptionsSet();
        private readonly SourceOffsetTable m_SourceOffsets = new SourceOffsetTable();
        private readonly Dictionary<string, object> m_FeatureEditorData = new Dictionary<string, object>(StringComparer.Ordinal);
        private IMeshFeatureExtractionOptionsProvider[] m_FeatureProviders;
        private FbxImporterSettingsComparison m_ImporterComparison;
        private Vector2 m_PreviewMeshScrollOffset;
        private ScrollView m_PreviewMeshScrollView;
        private Action<float> m_PreviewMeshScrollCallback;
        private int m_SelectedTab;
        private VisualElement m_TabContent;
        private readonly List<ToolbarButton> m_TabButtons = new List<ToolbarButton>();
        private FbxRawComparePreviewPanel m_PreviewPanel;
        private readonly Dictionary<string, bool> m_PreviewMeshFoldouts = new Dictionary<string, bool>(StringComparer.Ordinal);
        private readonly List<Action> m_PreviewMeshSelectionRefreshers = new List<Action>();
        private StickyHeaderElement m_PreviewMeshStickyHeader;
        private bool m_ComparisonCacheDirty;

        public GameObject OriginFbx => m_OriginFbx;
        public GameObject SourceFbx => m_SourceFbx;
        public string DefaultName
        {
            get => m_DefaultName;
            set => m_DefaultName = value ?? string.Empty;
        }

        public int SelectedTab => m_SelectedTab;
        public int TabCount => m_TabButtons.Count;
        public IReadOnlyList<string> MeshPaths => m_MeshPaths;
        public ExtractionOptionsSet FeatureOptionsSet => m_FeatureOptionsSet;
        public SourceOffsetTable SourceOffsets => m_SourceOffsets;
        public FbxInspectionSession InspectionSession => m_InspectionSession;
        public FbxRawComparePreviewPanel PreviewPanel => m_PreviewPanel;

        [MenuItem("Tools/BlendShare/Patch Creator")]
        public static void ShowWindow()
        {
            GetWindow<BlendSharePatchCreatorWindow>(Localization.S("patch_creator.window_title"));
        }

        public void SetFbxAssets(GameObject originFbx, GameObject sourceFbx, string defaultName = null)
        {
            m_OriginFbx = originFbx;
            m_SourceFbx = sourceFbx;
            if (defaultName != null)
            {
                m_DefaultName = defaultName;
            }
            else if (m_SourceFbx != null && string.IsNullOrWhiteSpace(m_DefaultName))
            {
                m_DefaultName = m_SourceFbx.name;
            }

            OnFbxSelectionChanged();
        }

        public void SelectTab(int tabIndex)
        {
            EnsureFeatureProviders();
            int totalTabs = 2 + (m_FeatureProviders?.Length ?? 0);
            if (tabIndex < 0 || tabIndex >= totalTabs)
            {
                throw new ArgumentOutOfRangeException(nameof(tabIndex));
            }

            if (m_SelectedTab == 1 && tabIndex != 1 && m_ComparisonCacheDirty)
            {
                RefreshComparisonCaches();
            }

            m_SelectedTab = tabIndex;
            RefreshTabContent();
        }

        public PatchExtractionResult ExtractAndSavePatchToPath(
            string outputAssetOrFilePath,
            out BlendShareAsset importedAsset)
        {
            importedAsset = null;
            if (string.IsNullOrWhiteSpace(outputAssetOrFilePath))
            {
                return new PatchExtractionResult(
                    false,
                    null,
                    new[]
                    {
                        new ExtractionDiagnostic(
                            ExtractionDiagnosticSeverity.Error,
                            "UI_PATCH_NO_PATH",
                            "Output path must be non-empty.")
                    });
            }

            if (string.IsNullOrWhiteSpace(m_DefaultName) && m_SourceFbx != null)
            {
                m_DefaultName = m_SourceFbx.name;
            }

            var selectedMeshPaths = GetSelectedFeatureMeshPaths();
            if (selectedMeshPaths.Count == 0)
            {
                return new PatchExtractionResult(
                    false,
                    null,
                    new[]
                    {
                        new ExtractionDiagnostic(
                            ExtractionDiagnosticSeverity.Error,
                            "UI_PATCH_NO_CONTENTS",
                            Localization.S("patch_creator.no_contents.message"))
                    });
            }

            var extractionRequests = new List<MeshExtractionRequest>(selectedMeshPaths.Count);
            for (int i = 0; i < selectedMeshPaths.Count; i++)
            {
                string nodePath = selectedMeshPaths[i];
                extractionRequests.Add(new MeshExtractionRequest(
                    nodePath,
                    meshId: nodePath,
                    displayName: ExtractLeafName(nodePath),
                    sourceToOriginRawOffset: m_SourceOffsets.GetFbxMatrix(nodePath)));
            }

            string fullOutputPath = ResolveFullOutputPath(outputAssetOrFilePath);
            string patchId = "+BlendShare-" + m_DefaultName;
            var pipeline = new PatchExtractionPipeline();
            var result = pipeline.ExtractFromFbxAssetsAndWriteFile(
                m_SourceFbx,
                m_OriginFbx,
                extractionRequests,
                m_FeatureOptionsSet,
                PayloadEncodeOptions.Default,
                fullOutputPath,
                out _,
                patchId: patchId,
                importUnityAsset: true,
                embedOriginBinding: true);

            if (!result.Succeeded)
            {
                return result;
            }

            if (TryResolveUnityAssetPath(fullOutputPath, out string unityAssetPath))
            {
                importedAsset = AssetDatabase.LoadAssetAtPath<BlendShareAsset>(unityAssetPath);
                if (importedAsset != null)
                {
                    string guid = AssetDatabase.AssetPathToGUID(unityAssetPath);
                    var buffer = BlendShareSettingsSession.GetOrCreateBuffer(importedAsset);
                    if (buffer != null && buffer.Settings != null)
                    {
                        buffer.Settings.DefaultTargetFbx = m_OriginFbx;
                        buffer.Settings.DefaultGeneratedAssetName = m_DefaultName;
                        BlendShareSettingsSession.NotifyBufferMutated(buffer);
                        BlendShareSettingsSession.Flush(guid);
                    }

                    importedAsset = AssetDatabase.LoadAssetAtPath<BlendShareAsset>(unityAssetPath);
                }
            }

            return result;
        }

        private void OnEnable()
        {
            EnsureFeatureOptions();
            m_PreviewPanel ??= new FbxRawComparePreviewPanel();
            Localization.LanguageChanged += OnLanguageChanged;
        }

        private void OnDisable()
        {
            Localization.LanguageChanged -= OnLanguageChanged;
            CaptureAndReleasePreviewMeshScrollView();
            m_PreviewPanel?.Dispose();
            m_PreviewPanel = null;
            m_InspectionSession?.Dispose();
            m_InspectionSession = null;
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
            EnsureFeatureOptions();
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

            m_TabContent = new VisualElement();
            m_TabContent.style.flexGrow = 1f;
            m_TabContent.style.minHeight = 0f;
            rootVisualElement.Add(m_TabContent);

            RefreshTabContent();
        }

        private void RefreshTabContent()
        {
            CaptureAndReleasePreviewMeshScrollView();
            m_TabContent?.Clear();
            UpdateTabButtonState();
            if (m_SelectedTab == 0)
            {
                m_TabContent?.Add(CreateGlobalPageElement());
            }
            else if (m_SelectedTab == 1)
            {
                m_TabContent?.Add(CreatePreviewPageElement());
            }
            else
            {
                m_TabContent?.Add(CreateFeaturePageElement(m_FeatureProviders.ElementAtOrDefault(m_SelectedTab - 2)));
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
            BlendShareInspectorUi.StyleHeading(title);
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
                .Concat(m_FeatureProviders.Select(provider => provider.TabLabel))
                .ToArray();
            if (m_SelectedTab >= tabLabels.Length)
            {
                m_SelectedTab = 0;
            }

            m_TabButtons.Clear();
            var toolbar = new Toolbar();
            toolbar.style.marginBottom = 6f;
            for (int i = 0; i < tabLabels.Length; i++)
            {
                int tabIndex = i;
                var button = new ToolbarButton(() => SelectTab(tabIndex))
                {
                    text = tabLabels[i]
                };
                m_TabButtons.Add(button);
                toolbar.Add(button);
            }

            parent.Add(toolbar);
            UpdateTabButtonState();
        }

        private void UpdateTabButtonState()
        {
            for (int i = 0; i < m_TabButtons.Count; i++)
            {
                m_TabButtons[i].SetEnabled(i != m_SelectedTab);
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

                m_PreviewPanel.DrawFixedPreview(availableHeight);
            });
            preview.style.flexShrink = 0f;
            preview.style.marginTop = 6f;
            root.Add(preview);
            return root;
        }

        private VisualElement CreatePreviewMeshListElement()
        {
            m_PreviewMeshSelectionRefreshers.Clear();
            var root = new VisualElement();
            root.style.flexGrow = 1f;
            root.style.minHeight = 0f;
            root.style.flexDirection = FlexDirection.Column;

            var paths = m_PreviewPanel?.GetMeshPaths() ?? Array.Empty<string>();
            if (paths.Length == 0)
            {
                return root;
            }

            root.Add(CreateAlignmentActionsElement());

            var stickyHeader = new StickyHeaderElement();
            m_PreviewMeshStickyHeader = stickyHeader;
            root.Add(stickyHeader);

            Vector2 restoreScrollOffset = m_PreviewMeshScrollOffset;
            var scrollView = new ScrollView(ScrollViewMode.Vertical);
            scrollView.style.flexGrow = 1f;
            scrollView.style.minHeight = 0f;
            scrollView.style.paddingRight = 2f;
            m_PreviewMeshScrollView = scrollView;
            m_PreviewMeshScrollCallback = value => m_PreviewMeshScrollOffset.y = value;
            scrollView.verticalScroller.valueChanged += m_PreviewMeshScrollCallback;
            root.Add(scrollView);

            var sections = new List<StickyHeaderSection>();
            foreach (string path in paths)
            {
                var section = CreatePreviewMeshSection(path);
                sections.Add(section);
                scrollView.Add(section.Root);
            }

            var overview = new StickyHeaderSection(
                null,
                null,
                stickyContext => CreatePreviewStickyHeaderContent(stickyContext, null));
            stickyHeader.Bind(
                scrollView,
                sections,
                overview,
                RefreshPreviewMeshSelectionRows);
            scrollView.schedule.Execute(() => scrollView.scrollOffset = restoreScrollOffset);
            return root;
        }

        private void CaptureAndReleasePreviewMeshScrollView()
        {
            m_PreviewMeshStickyHeader?.Unbind();
            m_PreviewMeshStickyHeader = null;
            if (m_PreviewMeshScrollView == null)
            {
                return;
            }

            m_PreviewMeshScrollOffset = m_PreviewMeshScrollView.scrollOffset;
            if (m_PreviewMeshScrollCallback != null)
            {
                m_PreviewMeshScrollView.verticalScroller.valueChanged -= m_PreviewMeshScrollCallback;
            }

            m_PreviewMeshScrollView = null;
            m_PreviewMeshScrollCallback = null;
        }

        private void SelectPreviewPath(string path)
        {
            m_PreviewPanel?.ShowPath(path, true);
            RefreshPreviewMeshSelectionState();
        }

        private void RefreshPreviewMeshSelectionState()
        {
            RefreshPreviewMeshSelectionRows();
            m_PreviewMeshStickyHeader?.RequestRefresh();
        }

        private void RefreshPreviewMeshSelectionRows()
        {
            foreach (var refresh in m_PreviewMeshSelectionRefreshers)
            {
                refresh?.Invoke();
            }
        }

        private VisualElement CreatePreviewStickyHeaderContent(
            StickyHeaderContext context,
            string path)
        {
            var bar = new StickyHeaderBar();
            string title = path;
            if (title == null)
            {
                var paths = m_PreviewPanel?.GetMeshPaths() ?? Array.Empty<string>();
                int shownCount = paths.Count(candidate => m_PreviewPanel?.IsShownPath(candidate) == true);
                title = $"{Localization.S("common.meshes")} ({shownCount}/{paths.Length})";
            }

            bar.Title.Add(StickyHeaderControls.CreateTitleButton(
                title,
                context.ScrollToSection));

            bool isShown = path != null && m_PreviewPanel?.IsShownPath(path) == true;
            var show = new Button(() => context.ApplyChange(() => m_PreviewPanel?.ShowPath(path, true)))
            {
                text = Localization.S(isShown
                    ? "patch_creator.preview.shown"
                    : "patch_creator.preview.show")
            };
            show.SetEnabled(!isShown);
            show.style.display = context.IsOverview ? DisplayStyle.None : DisplayStyle.Flex;
            show.style.marginLeft = 4f;
            bar.Actions.Add(show);

            bool isShowingAll = m_PreviewPanel?.IsShowingAll() == true;
            var showAll = new Button(() => context.ApplyChange(() => m_PreviewPanel?.ShowAll(true)))
            {
                text = Localization.S(isShowingAll
                    ? "patch_creator.preview.all_shown"
                    : "patch_creator.preview.show_all")
            };
            showAll.SetEnabled(!isShowingAll);
            showAll.style.marginLeft = 4f;
            bar.Actions.Add(showAll);
            return bar;
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

            var resetAllOffsets = new Button(() => m_PreviewPanel?.ResetAllOffsets())
            {
                text = Localization.S("patch_creator.preview.reset_all_offsets")
            };
            row.Add(resetAllOffsets);

            var autoAlignAll = new Button(() => m_PreviewPanel?.AutoAlignAll())
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
            string key = FbxNameUtility.NormalizePath(path ?? string.Empty);
            bool shown = m_PreviewPanel.IsShownPath(path);
            if (!m_PreviewMeshFoldouts.ContainsKey(key))
            {
                m_PreviewMeshFoldouts[key] = shown;
            }

            var sectionRoot = new VisualElement();
            sectionRoot.style.marginBottom = 5f;
            sectionRoot.style.paddingBottom = 4f;
            bool isPathShown = m_PreviewPanel.IsShownPath(path);
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
                value = m_PreviewMeshFoldouts[key]
            };
            foldout.style.flexGrow = 1f;
            headerRow.Add(foldout);

            bool isShown = m_PreviewPanel.IsShownPath(path);
            var showButton = new Button(() => SelectPreviewPath(path))
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
                sourceOffsetEditor = new IMGUIContainer(() => m_PreviewPanel.DrawSourceOffsetEditor());
                sourceOffsetEditor.style.marginTop = 4f;
                sourceOffsetEditor.style.display = m_PreviewPanel.IsShownPath(path)
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
                m_PreviewMeshFoldouts[key] = evt.newValue;
                controls.style.display = evt.newValue ? DisplayStyle.Flex : DisplayStyle.None;
                if (evt.newValue)
                {
                    BuildControls();
                }
            });

            void RefreshSelectionState()
            {
                bool selected = m_PreviewPanel.IsShownPath(path);
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

            m_PreviewMeshSelectionRefreshers.Add(RefreshSelectionState);
            RefreshSelectionState();

            return new StickyHeaderSection(
                sectionRoot,
                headerRow,
                stickyContext => CreatePreviewStickyHeaderContent(stickyContext, path));
        }

        private void AddPreviewMeshInfo(VisualElement root, string path)
        {
            FbxPreviewMeshInfo originalInfo = m_PreviewPanel.GetOriginalMeshInfo(path);
            FbxPreviewMeshInfo sourceInfo = m_PreviewPanel.GetSourceMeshInfo(path);
            var compatibility = m_PreviewPanel.GetTopologyCompatibility(path);
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

            if (compatibility.State == FbxTopologyCompatibilityState.SourceHasFewerControlPoints ||
                compatibility.State == FbxTopologyCompatibilityState.TopologyMismatch)
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
                card.Add(CreatePreviewInfoRow(string.Empty, Localization.S("common.status.unavailable")));
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
                    : fullHash.Substring(0, Math.Min(12, fullHash.Length));
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
            var originField = CreateFbxObjectField(Localization.S("common.original_fbx"), m_OriginFbx, value =>
            {
                m_OriginFbx = value;
                OnFbxSelectionChanged();
            });
            var sourceField = CreateFbxObjectField(Localization.S("common.source_fbx"), m_SourceFbx, value =>
            {
                m_SourceFbx = value;
                if (m_SourceFbx != null && string.IsNullOrWhiteSpace(m_DefaultName))
                {
                    m_DefaultName = m_SourceFbx.name;
                }

                OnFbxSelectionChanged();
            });
            var defaultNameField = new TextField(Localization.S("common.default_asset_name"))
            {
                value = m_DefaultName
            };
            var patchIdField = new TextField(Localization.S("common.patch_id"))
            {
                value = "+BlendShare-" + m_DefaultName
            };
            patchIdField.SetEnabled(false);
            defaultNameField.RegisterValueChangedCallback(evt =>
            {
                m_DefaultName = evt.newValue;
                patchIdField.SetValueWithoutNotify("+BlendShare-" + m_DefaultName);
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
            Action<GameObject> onChanged)
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
            m_ImporterComparison ??= FbxImporterSettingsComparison.Compare(m_OriginFbx, m_SourceFbx);
            container.Add(CreateHelpBox(
                GetImporterComparisonMessage(m_ImporterComparison),
                m_ImporterComparison.HasDifferences ? HelpBoxMessageType.Warning : HelpBoxMessageType.Info));
            container.Add(new Label($"{Localization.S("patch_creator.importer_comparison.global_scale")}    {Localization.SF("patch_creator.importer_comparison.original_value", m_ImporterComparison.OriginGlobalScale)}    {Localization.SF("patch_creator.importer_comparison.source_value", m_ImporterComparison.SourceGlobalScale)}"));
            container.Add(new Label($"{Localization.S("patch_creator.importer_comparison.bake_axis_conversion")}    {Localization.SF("patch_creator.importer_comparison.original_value", m_ImporterComparison.OriginBakeAxisConversion)}    {Localization.SF("patch_creator.importer_comparison.source_value", m_ImporterComparison.SourceBakeAxisConversion)}"));

            var copyButton = new Button(() =>
            {
                if (FbxImporterSettingsComparison.CopyGeometrySettings(m_OriginFbx, m_SourceFbx))
                {
                    ResetFeatureOptions();
                    RefreshExtractionState();
                    RefreshUi();
                }
            })
            {
                text = Localization.S("patch_creator.importer_comparison.copy_to_source")
            };
            copyButton.SetEnabled(m_ImporterComparison.HasDifferences);
            container.Add(copyButton);
            return container;
        }

        private static string GetImporterComparisonMessage(FbxImporterSettingsComparison comparison)
        {
            if (comparison == null)
            {
                return Localization.S("patch_creator.importer_comparison.assign_hint");
            }

            return comparison.Status switch
            {
                FbxImporterComparisonStatus.Match => Localization.S("patch_creator.importer_comparison.match"),
                FbxImporterComparisonStatus.Different => Localization.S("patch_creator.importer_comparison.different"),
                _ => Localization.S("patch_creator.importer_comparison.assign_hint")
            };
        }

        private VisualElement CreateMeshRefreshElement()
        {
            var container = CreateSection();
            if (m_SourceFbx == null || m_OriginFbx == null)
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

            foreach (string skipped in m_SkippedMeshes)
            {
                container.Add(CreateHelpBox(skipped, HelpBoxMessageType.Warning));
            }

            return container;
        }

        private VisualElement CreateSaveElement()
        {
            var container = CreateSection();
            bool enabled = m_SourceFbx != null &&
                           m_OriginFbx != null &&
                           HasSelectedFeatures() &&
                           !HasBlockingTopologyIssues();
            if (HasBlockingTopologyIssues())
            {
                container.Add(CreateHelpBox(Localization.S("patch_creator.alignment.blocking_topology"), HelpBoxMessageType.Error));
            }

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
                !m_FeatureOptionsSet.TryGet(provider.FeatureTypeId, out var options) ||
                options == null)
            {
                return new VisualElement();
            }

            if (m_SourceFbx == null || m_OriginFbx == null)
            {
                return CreateHelpBox(Localization.S("patch_creator.assign_fbx_hint"), HelpBoxMessageType.Info);
            }

            var context = new MeshFeatureOptionsEditorContext(
                m_SourceFbx,
                m_OriginFbx,
                m_InspectionSession,
                m_MeshPaths,
                m_FeatureEditorData,
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
            BlendShareInspectorUi.StyleStrong(label);
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
            if (string.IsNullOrWhiteSpace(m_DefaultName) && m_SourceFbx != null)
            {
                m_DefaultName = m_SourceFbx.name;
            }

            var selectedMeshPaths = GetSelectedFeatureMeshPaths();
            if (selectedMeshPaths.Count == 0)
            {
                EditorUtility.DisplayDialog(
                    Localization.S("patch_creator.no_contents.title"),
                    Localization.S("patch_creator.no_contents.message"),
                    Localization.S("common.ok"));
                return;
            }

            string path = EditorUtility.SaveFilePanelInProject(
                Localization.S("patch_creator.save_title"),
                $"{m_DefaultName}_BlendShare",
                "blendshare",
                Localization.S("data.save_file.message"));
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var result = ExtractAndSavePatchToPath(path, out var patch);
            if (!result.Succeeded)
            {
                string detail = result.Diagnostics
                    .FirstOrDefault(d => d.Severity == ExtractionDiagnosticSeverity.Error)
                    .Message;
                EditorUtility.DisplayDialog(
                    Localization.S("patch_creator.failed.title"),
                    string.IsNullOrWhiteSpace(detail)
                        ? Localization.S("patch_creator.failed.message")
                        : $"{Localization.S("patch_creator.failed.message")}\n\n{detail}",
                    Localization.S("common.ok"));
                return;
            }

            if (patch != null)
            {
                Selection.activeObject = patch;
            }

            ShowInvalidMappingWarning(result);
        }

        private bool HasSelectedFeatures()
        {
            EnsureFeatureProviders();
            var handledIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var provider in m_FeatureProviders)
            {
                if (provider == null)
                {
                    continue;
                }

                handledIds.Add(provider.FeatureTypeId);
                if (m_FeatureOptionsSet.TryGet(provider.FeatureTypeId, out var options) &&
                    provider.HasSelectedWork(options, m_MeshPaths))
                {
                    return true;
                }
            }

            foreach (var options in m_FeatureOptionsSet.All)
            {
                if (options == null || !options.Enabled || handledIds.Contains(options.FeatureTypeId))
                {
                    continue;
                }

                if (m_MeshPaths.Any(path => options.ShouldExtractMesh(FbxNameUtility.NormalizePath(path))))
                {
                    return true;
                }
            }

            return false;
        }

        private List<string> GetSelectedFeatureMeshPaths()
        {
            EnsureFeatureProviders();
            var selected = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (string meshPath in m_MeshPaths.Where(path => !string.IsNullOrWhiteSpace(path)))
            {
                string normalizedPath = FbxNameUtility.NormalizePath(meshPath);
                bool shouldExtract = false;

                foreach (var provider in m_FeatureProviders)
                {
                    if (provider != null &&
                        m_FeatureOptionsSet.TryGet(provider.FeatureTypeId, out var options) &&
                        provider.ShouldExtractMesh(options, normalizedPath))
                    {
                        shouldExtract = true;
                        break;
                    }
                }

                if (!shouldExtract)
                {
                    foreach (var options in m_FeatureOptionsSet.All)
                    {
                        if (options != null && options.ShouldExtractMesh(normalizedPath))
                        {
                            shouldExtract = true;
                            break;
                        }
                    }
                }

                if (shouldExtract && seen.Add(normalizedPath))
                {
                    selected.Add(normalizedPath);
                }
            }

            return selected;
        }

        private static void ShowInvalidMappingWarning(PatchExtractionResult result)
        {
            var meshes = result?.Document?.Meshes;
            if (meshes == null || meshes.Count == 0)
            {
                return;
            }

            bool hasMissingOriginBinding = meshes.Any(mesh =>
                mesh != null && (mesh.OriginBinding == null || !mesh.OriginBinding.HasPayload));
            if (!hasMissingOriginBinding)
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
            m_MeshPaths.Clear();
            m_SkippedMeshes.Clear();

            if (m_SourceFbx == null || m_OriginFbx == null)
            {
                return;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            bool foundAnyUnityRenderer = false;

            foreach (var sourceRenderer in m_SourceFbx.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var sourceMesh = sourceRenderer.sharedMesh;
                if (sourceMesh == null)
                {
                    continue;
                }

                foundAnyUnityRenderer = true;
                string sourcePath = GetRelativeTransformPath(sourceRenderer.transform, m_SourceFbx.transform);
                var originTransform = FindRelativeTransform(m_OriginFbx.transform, sourcePath);
                var originRenderer = originTransform != null ? originTransform.GetComponent<SkinnedMeshRenderer>() : null;
                var originMesh = originRenderer != null ? originRenderer.sharedMesh : null;
                if (originMesh == null)
                {
                    m_SkippedMeshes.Add(Localization.SF("patch_creator.missing_original_mesh", sourcePath));
                    continue;
                }

                string originPath = GetRelativeTransformPath(originRenderer.transform, m_OriginFbx.transform);
                if (!string.Equals(originPath, sourcePath, StringComparison.Ordinal))
                {
                    m_SkippedMeshes.Add(Localization.SF("patch_creator.missing_original_mesh", sourcePath));
                    continue;
                }

                if (seen.Add(originPath))
                {
                    m_MeshPaths.Add(originPath);
                }
            }

            foreach (var sourceFilter in m_SourceFbx.GetComponentsInChildren<MeshFilter>(true))
            {
                var sourceMesh = sourceFilter.sharedMesh;
                if (sourceMesh == null)
                {
                    continue;
                }

                foundAnyUnityRenderer = true;
                string sourcePath = GetRelativeTransformPath(sourceFilter.transform, m_SourceFbx.transform);
                if (seen.Contains(sourcePath))
                {
                    continue;
                }

                var originTransform = FindRelativeTransform(m_OriginFbx.transform, sourcePath);
                var originFilter = originTransform != null ? originTransform.GetComponent<MeshFilter>() : null;
                var originMesh = originFilter != null ? originFilter.sharedMesh : null;
                if (originMesh == null)
                {
                    m_SkippedMeshes.Add(Localization.SF("patch_creator.missing_original_mesh", sourcePath));
                    continue;
                }

                string originPath = GetRelativeTransformPath(originFilter.transform, m_OriginFbx.transform);
                if (!string.Equals(originPath, sourcePath, StringComparison.Ordinal))
                {
                    m_SkippedMeshes.Add(Localization.SF("patch_creator.missing_original_mesh", sourcePath));
                    continue;
                }

                if (seen.Add(originPath))
                {
                    m_MeshPaths.Add(originPath);
                }
            }

            if (m_InspectionSession != null)
            {
                foreach (string sharedPath in m_InspectionSession.GetSharedMeshPaths())
                {
                    string normalized = FbxNameUtility.NormalizePath(sharedPath);
                    if (!string.IsNullOrEmpty(normalized) && seen.Add(normalized))
                    {
                        m_MeshPaths.Add(normalized);
                    }
                }

                if (!foundAnyUnityRenderer && m_InspectionSession.Source?.MeshPaths != null)
                {
                    var originSet = new HashSet<string>(
                        m_InspectionSession.Origin?.MeshPaths ?? Array.Empty<string>(),
                        StringComparer.Ordinal);
                    foreach (string sourcePath in m_InspectionSession.Source.MeshPaths)
                    {
                        if (!originSet.Contains(sourcePath))
                        {
                            m_SkippedMeshes.Add(Localization.SF("patch_creator.missing_original_mesh", sourcePath));
                        }
                    }
                }
            }
        }

        private void RefreshExtractionState(bool autoAlignAll = false)
        {
            EnsureInspectionSession();
            RefreshMeshRequests();
            if (autoAlignAll && m_SourceFbx != null && m_OriginFbx != null && m_MeshPaths.Count > 0)
            {
                BindPreviewPanel();
                m_PreviewPanel.AutoAlignAll();
            }

            RefreshComparisonCaches();
        }

        private void BindPreviewPanel()
        {
            m_PreviewPanel ??= new FbxRawComparePreviewPanel();
            m_PreviewPanel.Bind(
                m_InspectionSession,
                m_SourceOffsets.GetOrCreate,
                MarkComparisonCacheDirty,
                ScheduleInspectionRefresh);
        }

        private void RefreshComparisonCaches()
        {
            EnsureFeatureProviders();
            EnsureFeatureOptions();
            m_ComparisonCacheDirty = false;
            m_FeatureEditorData.Clear();
            EnsureInspectionSession();
            m_ImporterComparison = m_InspectionSession.GetImporterComparison();
            if (m_SourceFbx == null || m_OriginFbx == null || m_MeshPaths.Count == 0)
            {
                return;
            }

            foreach (string meshPath in m_MeshPaths)
            {
                m_InspectionSession.GetTopologyCompatibility(meshPath);
            }

            foreach (var provider in m_FeatureProviders.OfType<IMeshFeatureInspectionProvider>())
            {
                if (provider is IMeshFeatureExtractionOptionsProvider optionsProvider)
                {
                    m_FeatureEditorData[optionsProvider.FeatureTypeId] =
                        provider.BuildInspectionData(m_InspectionSession, m_FeatureOptionsSet, m_SourceOffsets, m_MeshPaths);
                }
            }
        }

        private void MarkComparisonCacheDirty()
        {
            m_ComparisonCacheDirty = true;
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
            if (m_InspectionSession != null &&
                m_InspectedOriginFbx == m_OriginFbx &&
                m_InspectedSourceFbx == m_SourceFbx)
            {
                return;
            }

            ReloadInspectionSession();
        }

        private void ReloadInspectionSession()
        {
            m_InspectionSession?.Dispose();
            m_InspectedOriginFbx = m_OriginFbx;
            m_InspectedSourceFbx = m_SourceFbx;
            m_InspectionSession = FbxInspectionSession.Open(m_SourceFbx, m_OriginFbx);
        }

        private bool HasBlockingTopologyIssues()
        {
            if (m_InspectionSession == null)
            {
                return m_SourceFbx != null && m_OriginFbx != null && m_MeshPaths.Count > 0;
            }

            return GetSelectedFeatureMeshPaths().Any(meshPath =>
                !m_InspectionSession.GetTopologyCompatibility(meshPath).IsCompatible);
        }

        private static string FormatTopologyCompatibilityVerdict(FbxTopologyCompatibilityResult result)
        {
            return result.State switch
            {
                FbxTopologyCompatibilityState.Exact => Localization.S("patch_creator.alignment.compatible"),
                FbxTopologyCompatibilityState.CompatibleWithExtraSourceControlPoints or
                    FbxTopologyCompatibilityState.CompatibleWithTopologyChange =>
                    Localization.S("patch_creator.alignment.compatible"),
                FbxTopologyCompatibilityState.SourceHasFewerControlPoints or
                    FbxTopologyCompatibilityState.TopologyMismatch =>
                    Localization.S("patch_creator.alignment.incompatible"),
                _ => Localization.S("patch_creator.alignment.compatibility_unavailable")
            };
        }

        private static string FormatTopologyCompatibilityDetail(FbxTopologyCompatibilityResult result)
        {
            if (result.HasWarning)
            {
                var details = new List<string>();
                if (result.HasExtraSourceControlPoints)
                {
                    details.Add(Localization.SF(
                        "patch_creator.alignment.source_extra_control_points",
                        result.SourceControlPointCount,
                        result.OriginControlPointCount));
                }

                if (result.HasExtraSourceEdges)
                {
                    details.Add(Localization.S("patch_creator.alignment.topology_extra_edges"));
                }

                return string.Join(" ", details);
            }

            return result.State switch
            {
                FbxTopologyCompatibilityState.SourceHasFewerControlPoints => Localization.SF(
                    "patch_creator.alignment.source_fewer_control_points",
                    result.SourceControlPointCount,
                    result.OriginControlPointCount),
                FbxTopologyCompatibilityState.TopologyMismatch =>
                    Localization.S("patch_creator.alignment.topology_mismatch"),
                _ => string.Empty
            };
        }

        private static string GetRelativeTransformPath(Transform target, Transform root)
        {
            if (target == null || target == root)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            var curr = target;
            while (curr != null && curr != root)
            {
                parts.Add(curr.name);
                curr = curr.parent;
            }

            parts.Reverse();
            return FbxNameUtility.NormalizePath(string.Join("/", parts));
        }

        private static Transform FindRelativeTransform(Transform root, string normalizedPath)
        {
            if (root == null)
            {
                return null;
            }

            string path = FbxNameUtility.NormalizePath(normalizedPath ?? string.Empty);
            if (string.IsNullOrEmpty(path))
            {
                return root;
            }

            return root.Find(path);
        }

        private static string ExtractLeafName(string normalizedPath)
        {
            if (string.IsNullOrEmpty(normalizedPath))
            {
                return string.Empty;
            }

            int slash = normalizedPath.LastIndexOf('/');
            return slash >= 0 && slash + 1 < normalizedPath.Length
                ? normalizedPath.Substring(slash + 1)
                : normalizedPath;
        }

        private static string ResolveFullOutputPath(string outputAssetOrFilePath)
        {
            if (Path.IsPathRooted(outputAssetOrFilePath))
            {
                return Path.GetFullPath(outputAssetOrFilePath);
            }

            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Environment.CurrentDirectory;
            return Path.GetFullPath(Path.Combine(projectRoot, outputAssetOrFilePath));
        }

        private static bool TryResolveUnityAssetPath(string fullFilePath, out string unityAssetPath)
        {
            unityAssetPath = null;
            if (string.IsNullOrWhiteSpace(fullFilePath))
            {
                return false;
            }

            try
            {
                string fullFile = Path.GetFullPath(fullFilePath);
                string fullAssets = Path.GetFullPath(Application.dataPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string prefix = fullAssets + Path.DirectorySeparatorChar;
                StringComparison comparison = Application.platform == RuntimePlatform.LinuxEditor
                    ? StringComparison.Ordinal
                    : StringComparison.OrdinalIgnoreCase;
                if (!fullFile.StartsWith(prefix, comparison))
                {
                    return false;
                }

                string relative = fullFile.Substring(prefix.Length).Replace('\\', '/');
                if (string.IsNullOrEmpty(relative))
                {
                    return false;
                }

                unityAssetPath = "Assets/" + relative;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void ResetFeatureOptions()
        {
            EnsureFeatureProviders();
            m_FeatureOptionsSet = new ExtractionOptionsSet();
            m_SourceOffsets.Clear();
            m_PreviewMeshFoldouts.Clear();
            foreach (var provider in m_FeatureProviders)
            {
                provider?.ResetUiState();
            }

            EnsureFeatureOptions();
        }

        private void EnsureFeatureOptions()
        {
            EnsureFeatureProviders();
            m_FeatureOptionsSet ??= new ExtractionOptionsSet();
            foreach (var provider in m_FeatureProviders)
            {
                if (provider == null || m_FeatureOptionsSet.TryGet(provider.FeatureTypeId, out _))
                {
                    continue;
                }

                m_FeatureOptionsSet.Set(provider.CreateDefaultOptions());
            }
        }

        private void EnsureFeatureProviders()
        {
            m_FeatureProviders ??= FeatureExtractionOptionsProviderRegistry.CreateProviders();
        }
    }
}
