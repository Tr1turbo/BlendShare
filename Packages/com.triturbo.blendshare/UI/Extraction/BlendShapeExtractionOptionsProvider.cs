using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShapeShare;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Inspector;
using Triturbo.BlendShare.Model.Features;
using Triturbo.BlendShare.Pipeline.Extraction;
using Triturbo.BlendShare.Pipeline.Inspection;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Triturbo.BlendShare.UI.Extraction
{
    public sealed class BlendShapeExtractionOptionsProvider
        : MeshFeatureExtractionOptionsProvider<BlendShapeExtractionOptions>,
          IMeshFeatureInspectionProvider,
          IUIToolkitMeshFeatureOptionsProvider
    {
        private const float k_ComparisonPillSlotWidth = 100f;
        private const float k_BlendShapeTableHorizontalPadding = 4f;
        private const float k_BlendShapeUseColumnWidth = 60f;
        private const float k_BlendShapeUseColumnRightPadding = 6f;
        private const float k_BlendShapeToggleVisualWidth = 14f;

        private readonly Dictionary<string, bool> m_MeshFoldouts = new Dictionary<string, bool>(StringComparer.Ordinal);
        private readonly List<Action> m_BaseModeRefreshers = new List<Action>();
        private Vector2 m_ScrollPosition;
        private float m_BlendShapeScrollHeight = 240f;
        private float m_DeltaComparisonTolerance = (float)BlendShapeFbxComparison.DefaultDeltaComparisonToleranceMeters;

        public override string FeatureTypeId => BlendShapeFeature.FeatureTypeId;
        public override string TabLabel => Localization.FeatureName(FeatureTypeId, "BlendShapes");
        public override int DisplayOrder => -100;

        public float DeltaComparisonTolerance
        {
            get => m_DeltaComparisonTolerance;
            set => m_DeltaComparisonTolerance = float.IsNaN(value) || float.IsInfinity(value)
                ? (float)BlendShapeFbxComparison.DefaultDeltaComparisonToleranceMeters
                : Mathf.Max(0f, value);
        }

        public override void ResetUiState()
        {
            m_MeshFoldouts.Clear();
            m_BaseModeRefreshers.Clear();
            m_ScrollPosition = Vector2.zero;
        }

        protected override BlendShapeExtractionOptions CreateDefault()
        {
            return new BlendShapeExtractionOptions
            {
                Enabled = true,
                ExtractAllWhenSelectionEmpty = false
            };
        }

        public object BuildInspectionData(
            FbxInspectionSession session,
            ExtractionOptionsSet options,
            SourceOffsetTable sourceOffsets,
            IReadOnlyList<string> meshPaths)
        {
            var comparison = BlendShapeFbxComparison.BuildInspectionData(
                session,
                meshPaths,
                path => sourceOffsets != null && sourceOffsets.TryGet(path, out var offset) ? offset : null,
                m_DeltaComparisonTolerance);

            if (options != null &&
                options.TryGet(FeatureTypeId, out BlendShapeExtractionOptions bsOptions) &&
                bsOptions != null &&
                meshPaths != null)
            {
                var sourceMeshes = CollectUnityMeshesByPath(session?.Source?.FbxObject);
                var originMeshes = CollectUnityMeshesByPath(session?.Origin?.FbxObject);
                for (int i = 0; i < meshPaths.Count; i++)
                {
                    string meshPath = meshPaths[i];
                    if (string.IsNullOrWhiteSpace(meshPath))
                    {
                        continue;
                    }

                    string normalizedPath = FbxNameUtility.NormalizePath(meshPath);
                    sourceMeshes.TryGetValue(normalizedPath, out var sourceMesh);
                    originMeshes.TryGetValue(normalizedPath, out var originMesh);
                    BlendShapeMeshComparison meshComparison = null;
                    comparison.TryGetMesh(normalizedPath, out meshComparison);
                    var blendShapeNames = GetBlendShapeNames(sourceMesh, meshComparison, session, normalizedPath);
                    if (blendShapeNames.Count > 0)
                    {
                        EnsureDefaultSelection(bsOptions, normalizedPath, originMesh, meshComparison, blendShapeNames);
                    }
                }
            }

            return comparison;
        }

        public VisualElement CreateOptionsElement(
            IFeatureExtractionOptions rawOptions,
            MeshFeatureOptionsEditorContext context)
        {
            if (rawOptions is not BlendShapeExtractionOptions options)
            {
                return new VisualElement();
            }

            var root = new VisualElement();
            root.style.flexGrow = 1f;
            root.style.minHeight = 0f;
            root.style.flexDirection = FlexDirection.Column;

            m_BaseModeRefreshers.Clear();
            var controls = CreateGeneralOptionsElement(options, context);
            var listRoot = CreateBlendShapeListElement(options, context);
            listRoot.SetEnabled(options.Enabled);
            root.Add(controls);
            root.Add(listRoot);
            return root;
        }

        private VisualElement CreateGeneralOptionsElement(
            BlendShapeExtractionOptions options,
            MeshFeatureOptionsEditorContext context)
        {
            var root = new VisualElement();
            root.style.marginBottom = 6f;

            var enabledToggle = new Toggle(Localization.S("features.blend-shapes.toggle"))
            {
                value = options.Enabled
            };
            root.Add(enabledToggle);

            var baseMode = CreateBaseModeField(
                Localization.S("features.blend-shapes.base_mode"),
                options.DefaultBaseMode);
            baseMode.RegisterValueChangedCallback(evt =>
            {
                options.DefaultBaseMode = evt.newValue;
                foreach (var refresh in m_BaseModeRefreshers)
                {
                    refresh?.Invoke();
                }
            });
            root.Add(baseMode);

            var deltaTolerance = new FloatField(
                Localization.S("features.blend-shapes.delta_comparison_tolerance"))
            {
                value = DeltaComparisonTolerance,
                isDelayed = true,
                tooltip = Localization.S("features.blend-shapes.delta_comparison_tolerance_tooltip")
            };
            deltaTolerance.RegisterValueChangedCallback(evt =>
            {
                DeltaComparisonTolerance = evt.newValue;
                deltaTolerance.SetValueWithoutNotify(DeltaComparisonTolerance);
                context?.RequestInspectionRefresh?.Invoke();
            });
            root.Add(deltaTolerance);

            enabledToggle.RegisterValueChangedCallback(evt =>
            {
                options.Enabled = evt.newValue;
                root.parent?.Q<VisualElement>("blendshape-list-root")?.SetEnabled(options.Enabled);
            });

            return root;
        }

        private VisualElement CreateBlendShapeListElement(
            BlendShapeExtractionOptions options,
            MeshFeatureOptionsEditorContext context)
        {
            var root = new VisualElement { name = "blendshape-list-root" };
            root.style.flexGrow = 1f;
            root.style.minHeight = 0f;
            root.style.flexDirection = FlexDirection.Column;
            root.Add(CreateHeaderLabel(Localization.S("common.meshes")));

            if (context?.SourceFbxGo == null || context.OriginFbxGo == null)
            {
                root.Add(new HelpBox(Localization.S("patch_creator.assign_fbx_hint"), HelpBoxMessageType.Info));
                return root;
            }

            var meshPaths = context.MeshPaths?
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToArray() ?? Array.Empty<string>();
            if (meshPaths.Length == 0)
            {
                root.Add(new HelpBox(Localization.S("patch_creator.no_meshes"), HelpBoxMessageType.Info));
                return root;
            }

            var stickyHeader = new StickyHeaderElement();
            root.Add(stickyHeader);

            var scrollView = new ScrollView(ScrollViewMode.Vertical);
            scrollView.style.flexGrow = 1f;
            scrollView.style.minHeight = 0f;
            root.Add(scrollView);

            var sourceMeshes = CollectUnityMeshesByPath(context.SourceFbxGo);
            var originMeshes = CollectUnityMeshesByPath(context.OriginFbxGo);
            context.TryGetCachedData(
                BlendShapeFeature.FeatureTypeId,
                out BlendShapeFbxComparison comparison);

            Action refreshContent = null;
            var meshSections = new List<BlendShapeMeshSection>();
            foreach (string meshPath in meshPaths)
            {
                var meshSection = CreateMeshSection(
                    options,
                    meshPath,
                    sourceMeshes,
                    originMeshes,
                    context.InspectionSession,
                    comparison,
                    () => refreshContent?.Invoke());
                if (meshSection == null)
                {
                    continue;
                }

                meshSections.Add(meshSection);
                scrollView.Add(meshSection.Section.Root);
            }

            if (meshSections.Count == 0)
            {
                stickyHeader.style.display = DisplayStyle.None;
                scrollView.Add(new HelpBox(Localization.S("patch_creator.no_meshes"), HelpBoxMessageType.Info));
                return root;
            }

            var sections = meshSections.Select(item => item.Section).ToArray();
            var overview = new StickyHeaderSection(
                null,
                null,
                stickyContext => CreateBlendShapeStickyHeader(stickyContext, meshSections, null));
            Action refreshRows = () =>
            {
                foreach (var meshSection in meshSections)
                {
                    meshSection.Refresh();
                }
            };
            refreshContent = () =>
            {
                refreshRows();
                stickyHeader.RequestRefresh();
            };
            stickyHeader.Bind(scrollView, sections, overview, refreshRows);
            return root;
        }

        private BlendShapeMeshSection CreateMeshSection(
            BlendShapeExtractionOptions options,
            string meshPath,
            IReadOnlyDictionary<string, Mesh> sourceMeshes,
            IReadOnlyDictionary<string, Mesh> originMeshes,
            FbxInspectionSession inspectionSession,
            BlendShapeFbxComparison comparison,
            Action onSelectionChanged)
        {
            string normalizedPath = FbxNameUtility.NormalizePath(meshPath ?? string.Empty);
            sourceMeshes.TryGetValue(normalizedPath, out var sourceMesh);
            BlendShapeMeshComparison meshComparison = null;
            comparison?.TryGetMesh(normalizedPath, out meshComparison);
            var blendShapeNames = GetBlendShapeNames(sourceMesh, meshComparison, inspectionSession, normalizedPath);
            if (blendShapeNames.Count == 0)
            {
                return null;
            }

            originMeshes.TryGetValue(normalizedPath, out var originMesh);
            EnsureDefaultSelection(options, normalizedPath, originMesh, meshComparison, blendShapeNames);

            var selected = new HashSet<string>(options.GetSelectedShapeNames(normalizedPath), StringComparer.Ordinal);
            string key = normalizedPath;
            if (!m_MeshFoldouts.ContainsKey(key))
            {
                m_MeshFoldouts[key] = selected.Count > 0;
            }

            var foldout = new Foldout
            {
                value = m_MeshFoldouts[key]
            };
            foldout.RegisterValueChangedCallback(evt => m_MeshFoldouts[key] = evt.newValue);

            void UpdateTitle()
            {
                foldout.text = $"{normalizedPath} ({selected.Count}/{blendShapeNames.Count})";
            }

            UpdateTitle();
            foldout.style.marginBottom = 6f;

            var meshEnabled = new Toggle(Localization.S("common.enabled"))
            {
                value = options.IsMeshEnabled(normalizedPath)
            };
            foldout.Add(meshEnabled);

            var meshControls = new VisualElement();
            meshControls.SetEnabled(meshEnabled.value);
            foldout.Add(meshControls);

            var pathLabel = new Label($"{Localization.S("common.mesh_path")}: {normalizedPath}");
            BlendShareInspectorUi.StyleStrong(pathLabel);
            meshControls.Add(pathLabel);

            var baseModeRow = new VisualElement();
            baseModeRow.style.flexDirection = FlexDirection.Row;
            baseModeRow.style.alignItems = Align.Center;
            var overrideBaseMode = new Toggle(Localization.S("features.blend-shapes.override_base_mode"))
            {
                value = options.HasBaseModeOverride(normalizedPath)
            };
            var meshBaseMode = CreateBaseModeField(string.Empty, options.GetBaseMode(normalizedPath));
            meshBaseMode.style.flexGrow = 1f;
            meshBaseMode.style.marginLeft = 6f;
            baseModeRow.Add(overrideBaseMode);
            baseModeRow.Add(meshBaseMode);
            meshControls.Add(baseModeRow);

            void RefreshBaseMode()
            {
                bool hasOverride = options.HasBaseModeOverride(normalizedPath);
                overrideBaseMode.SetValueWithoutNotify(hasOverride);
                meshBaseMode.SetValueWithoutNotify(options.GetBaseMode(normalizedPath));
                meshBaseMode.SetEnabled(hasOverride);
            }

            overrideBaseMode.RegisterValueChangedCallback(evt =>
            {
                if (evt.newValue)
                {
                    if (!options.HasBaseModeOverride(normalizedPath))
                    {
                        options.SetBaseModeOverride(normalizedPath, options.DefaultBaseMode);
                    }
                }
                else
                {
                    options.SetBaseModeOverride(normalizedPath, null);
                }

                RefreshBaseMode();
            });
            meshBaseMode.RegisterValueChangedCallback(evt =>
            {
                options.SetBaseModeOverride(normalizedPath, evt.newValue);
            });
            m_BaseModeRefreshers.Add(RefreshBaseMode);
            RefreshBaseMode();

            if (!string.IsNullOrEmpty(meshComparison?.Message))
            {
                meshControls.Add(new HelpBox(meshComparison.Message, HelpBoxMessageType.Warning));
            }

            var buttonRow = new VisualElement();
            buttonRow.style.flexDirection = FlexDirection.Row;
            buttonRow.style.marginTop = 4f;
            buttonRow.style.marginBottom = 4f;

            void SetSelectedBlendShapes(IEnumerable<string> shapeNames)
            {
                selected = new HashSet<string>(shapeNames ?? Array.Empty<string>(), StringComparer.Ordinal);
                options.SetSelectedShapeNames(normalizedPath, selected);
                SetShapeToggleValues(foldout, selected);
                UpdateTitle();
                onSelectionChanged?.Invoke();
            }

            void EnableAll()
            {
                SetSelectedBlendShapes(blendShapeNames);
            }

            void DisableAll()
            {
                SetSelectedBlendShapes(Array.Empty<string>());
            }

            void RestoreDefaults()
            {
                SetSelectedBlendShapes(BuildDefaultSelection(originMesh, meshComparison, blendShapeNames));
            }

            void DisableChanged()
            {
                SetSelectedBlendShapes(selected.Where(shapeName =>
                    GetBlendShapeStatus(shapeName, meshComparison) != BlendShapeComparisonStatus.Changed));
            }

            var enableAll = new Button(EnableAll)
            {
                text = Localization.S("common.enable_all")
            };
            var disableAll = new Button(DisableAll)
            {
                text = Localization.S("common.disable_all")
            };
            var restore = new Button(RestoreDefaults)
            {
                text = Localization.S("common.auto")
            };
            var disableChanged = new Button(DisableChanged)
            {
                text = Localization.S("common.disable_diff")
            };
            enableAll.style.marginRight = 4f;
            disableAll.style.marginRight = 4f;
            restore.style.marginRight = 4f;
            buttonRow.Add(enableAll);
            buttonRow.Add(disableAll);
            buttonRow.Add(restore);
            buttonRow.Add(disableChanged);
            meshControls.Add(buttonRow);

            meshEnabled.RegisterValueChangedCallback(evt =>
            {
                options.SetMeshEnabled(normalizedPath, evt.newValue);
                meshControls.SetEnabled(evt.newValue);
                onSelectionChanged?.Invoke();
            });

            var table = CreateBlendShapeTable();
            table.Add(CreateBlendShapeTableHeader());

            for (int i = 0; i < blendShapeNames.Count; i++)
            {
                string shapeName = blendShapeNames[i];
                var row = CreateBlendShapeTableRow(i);

                var status = GetBlendShapeStatus(shapeName, meshComparison);

                var label = new Label(shapeName);
                ApplyBlendShapeNameColumnStyle(label);

                var shapeToggle = new Toggle
                {
                    value = selected.Contains(shapeName)
                };
                ApplyBlendShapeUseToggleStyle(shapeToggle);
                shapeToggle.userData = shapeName;
                shapeToggle.RegisterValueChangedCallback(evt =>
                {
                    if (evt.newValue)
                    {
                        selected.Add(shapeName);
                    }
                    else
                    {
                        selected.Remove(shapeName);
                    }

                    options.SetSelectedShapeNames(normalizedPath, selected);
                    UpdateTitle();
                    onSelectionChanged?.Invoke();
                });

                row.Add(label);

                var statusSlot = CreateComparisonPillSlot();

                if (ShouldShowComparisonPill(status))
                {
                    statusSlot.Add(CreateComparisonPill(status));
                }

                row.Add(statusSlot);
                var useSlot = CreateBlendShapeFixedColumn(
                    k_BlendShapeUseColumnWidth,
                    "blendshape-table-use-cell",
                    k_BlendShapeUseColumnRightPadding);
                useSlot.Add(shapeToggle);
                row.Add(useSlot);
                table.Add(row);
            }

            meshControls.Add(table);

            void SetMeshEnabled(bool value)
            {
                options.SetMeshEnabled(normalizedPath, value);
                meshEnabled.SetValueWithoutNotify(value);
                meshControls.SetEnabled(value);
                onSelectionChanged?.Invoke();
            }

            void RefreshSection()
            {
                UpdateTitle();
                meshEnabled.SetValueWithoutNotify(options.IsMeshEnabled(normalizedPath));
                meshControls.SetEnabled(options.IsMeshEnabled(normalizedPath));
                SetShapeToggleValues(foldout, selected);
            }

            var result = new BlendShapeMeshSection
            {
                Title = () => foldout.text,
                IsEnabled = () => options.IsMeshEnabled(normalizedPath),
                SetEnabled = SetMeshEnabled,
                EnableAll = EnableAll,
                DisableAll = DisableAll,
                Restore = RestoreDefaults,
                DisableChanged = DisableChanged,
                Refresh = RefreshSection,
                SelectedCount = () => selected.Count,
                TotalCount = () => blendShapeNames.Count
            };
            result.Section = new StickyHeaderSection(
                foldout,
                foldout,
                stickyContext => CreateBlendShapeStickyHeader(stickyContext, new[] { result }, result));
            return result;
        }

        private static VisualElement CreateBlendShapeStickyHeader(
            StickyHeaderContext context,
            IReadOnlyList<BlendShapeMeshSection> meshSections,
            BlendShapeMeshSection activeMesh)
        {
            IReadOnlyList<BlendShapeMeshSection> targets = activeMesh == null
                ? meshSections
                : new[] { activeMesh };
            int enabledCount = targets.Count(item => item.IsEnabled());
            string title = activeMesh?.Title() ??
                           $"{Localization.S("common.meshes")} ({enabledCount}/{targets.Count})";

            var bar = new StickyHeaderBar
            {
                name = activeMesh == null
                    ? "blendshape-sticky:meshes"
                    : "blendshape-sticky:mesh"
            };
            bar.SetRightContentPadding(k_BlendShapeTableHorizontalPadding);
            bar.Leading.Add(StickyHeaderControls.CreatePrimaryToggle(
                StickyHeaderToggleState.FromCounts(enabledCount, targets.Count),
                value => context.ApplyChange(() =>
                {
                    foreach (var target in targets)
                    {
                        target.SetEnabled(value);
                    }
                }),
                "blendshape-sticky-toggle:mesh"));
            bar.Title.Add(StickyHeaderControls.CreateTitleButton(title, context.ScrollToSection));
            bar.Actions.Add(StickyHeaderControls.CreateActionButton(
                Localization.S("common.enable_all"),
                () => context.ApplyChange(() =>
                {
                    foreach (var target in targets)
                    {
                        target.EnableAll();
                    }
                })));
            bar.Actions.Add(StickyHeaderControls.CreateActionButton(
                Localization.S("common.disable_all"),
                () => context.ApplyChange(() =>
                {
                    foreach (var target in targets)
                    {
                        target.DisableAll();
                    }
                })));
            bar.Actions.Add(StickyHeaderControls.CreateActionButton(
                Localization.S("common.auto"),
                () => context.ApplyChange(() =>
                {
                    foreach (var target in targets)
                    {
                        target.Restore();
                    }
                })));
            bar.Actions.Add(StickyHeaderControls.CreateActionButton(
                Localization.S("common.disable_diff"),
                () => context.ApplyChange(() =>
                {
                    foreach (var target in targets)
                    {
                        target.DisableChanged();
                    }
                })));
            int selectedCount = 0;
            int totalCount = 0;
            bool allSectionsEnabled = targets.Count > 0;
            foreach (var target in targets)
            {
                selectedCount += target.SelectedCount();
                totalCount += target.TotalCount();
                allSectionsEnabled &= target.IsEnabled();
            }

            bar.Trailing.Add(CreateBlendShapeFixedColumn(
                k_ComparisonPillSlotWidth,
                "blendshape-sticky-status-spacer"));
            var stickyUseCell = StickyHeaderControls.CreateToggleCell(
                Localization.S("common.use"),
                StickyHeaderToggleState.FromCounts(selectedCount, totalCount, allSectionsEnabled),
                value => context.ApplyChange(() =>
                {
                    foreach (var target in targets)
                    {
                        if (value)
                        {
                            target.EnableAll();
                        }
                        else
                        {
                            target.DisableAll();
                        }
                    }
                }),
                "blendshape-sticky-toggle:use",
                k_BlendShapeUseColumnWidth,
                k_BlendShapeToggleVisualWidth);
            stickyUseCell.name = "blendshape-sticky-use-cell";
            stickyUseCell.style.paddingRight = k_BlendShapeUseColumnRightPadding;
            ApplyBlendShapeUseToggleStyle(
                stickyUseCell.Q<Toggle>("blendshape-sticky-toggle:use"));
            bar.Trailing.Add(stickyUseCell);
            return bar;
        }

        private static PopupField<BlendShapeBaseMode> CreateBaseModeField(
            string label,
            BlendShapeBaseMode value)
        {
            var values = new List<BlendShapeBaseMode>
            {
                BlendShapeBaseMode.PreserveSourceRelative,
                BlendShapeBaseMode.RebaseOntoOriginal
            };
            var field = new PopupField<BlendShapeBaseMode>(label, values, value);
            field.formatSelectedValueCallback = FormatBaseMode;
            field.formatListItemCallback = FormatBaseMode;
            return field;
        }

        private static string FormatBaseMode(BlendShapeBaseMode mode)
        {
            return Localization.S(mode == BlendShapeBaseMode.PreserveSourceRelative
                ? "patch_creator.alignment.basis_source_relative"
                : "patch_creator.alignment.basis_rebase_original");
        }

        private sealed class BlendShapeMeshSection
        {
            public StickyHeaderSection Section { get; set; }
            public Func<string> Title { get; set; }
            public Func<bool> IsEnabled { get; set; }
            public Action<bool> SetEnabled { get; set; }
            public Action EnableAll { get; set; }
            public Action DisableAll { get; set; }
            public Action Restore { get; set; }
            public Action DisableChanged { get; set; }
            public Action Refresh { get; set; }
            public Func<int> SelectedCount { get; set; }
            public Func<int> TotalCount { get; set; }
        }

        private static Label CreateHeaderLabel(string text)
        {
            var label = new Label(text);
            BlendShareInspectorUi.StyleStrong(label);
            label.style.marginBottom = 4f;
            return label;
        }

        private static VisualElement CreateBlendShapeTable()
        {
            var table = new VisualElement();
            table.style.flexDirection = FlexDirection.Column;
            table.style.marginTop = 4f;
            table.style.marginBottom = 4f;
            table.style.borderBottomWidth = 1f;
            table.style.borderTopWidth = 1f;
            table.style.borderLeftWidth = 1f;
            table.style.borderRightWidth = 1f;
            table.style.borderBottomColor = new Color(0.25f, 0.25f, 0.25f);
            table.style.borderTopColor = new Color(0.25f, 0.25f, 0.25f);
            table.style.borderLeftColor = new Color(0.25f, 0.25f, 0.25f);
            table.style.borderRightColor = new Color(0.25f, 0.25f, 0.25f);
            return table;
        }

        private static VisualElement CreateBlendShapeTableHeader()
        {
            var row = CreateBlendShapeTableRow(-1);
            row.style.backgroundColor = new Color(0.23f, 0.23f, 0.23f);

            var nameLabel = new Label(Localization.S("features.blend-shapes.table.name"));
            BlendShareInspectorUi.StyleStrong(nameLabel);
            ApplyBlendShapeNameColumnStyle(nameLabel);

            var statusLabel = new Label(Localization.S("common.status"));
            BlendShareInspectorUi.StyleStrong(statusLabel);
            statusLabel.style.width = Length.Percent(100f);
            statusLabel.style.unityTextAlign = TextAnchor.MiddleRight;

            var toggleLabel = new Label(Localization.S("common.use"));
            BlendShareInspectorUi.StyleStrong(toggleLabel);
            toggleLabel.style.width = Length.Percent(100f);
            toggleLabel.style.unityTextAlign = TextAnchor.MiddleRight;

            var statusHeader = CreateBlendShapeFixedColumn(
                k_ComparisonPillSlotWidth,
                "blendshape-table-status-header");
            statusHeader.Add(statusLabel);
            var useHeader = CreateBlendShapeFixedColumn(
                k_BlendShapeUseColumnWidth,
                "blendshape-table-use-header",
                k_BlendShapeUseColumnRightPadding);
            useHeader.Add(toggleLabel);

            row.Add(nameLabel);
            row.Add(statusHeader);
            row.Add(useHeader);
            return row;
        }

        private static VisualElement CreateBlendShapeTableRow(int rowIndex)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.minHeight = 24f;
            row.style.paddingLeft = k_BlendShapeTableHorizontalPadding;
            row.style.paddingRight = k_BlendShapeTableHorizontalPadding;

            if (rowIndex >= 0)
            {
                row.style.backgroundColor = rowIndex % 2 == 1
                    ? new Color(0.22f, 0.22f, 0.22f)
                    : new Color(0.25f, 0.25f, 0.25f);
            }

            return row;
        }

        private static void ApplyBlendShapeNameColumnStyle(Label label)
        {
            label.style.flexGrow = 1f;
            label.style.flexShrink = 1f;
            label.style.minWidth = 100f;
            label.style.overflow = Overflow.Hidden;
            label.style.unityTextOverflowPosition = TextOverflowPosition.End;
        }

        private static VisualElement CreateComparisonPillSlot()
        {
            var statusSlot = CreateBlendShapeFixedColumn(
                k_ComparisonPillSlotWidth,
                "blendshape-table-status-cell");
            return statusSlot;
        }

        private static VisualElement CreateBlendShapeFixedColumn(
            float width,
            string name,
            float rightPadding = 0f)
        {
            var column = new VisualElement { name = name };
            column.style.width = width;
            column.style.paddingRight = rightPadding;
            column.style.flexShrink = 0f;
            column.style.flexDirection = FlexDirection.Row;
            column.style.alignItems = Align.Center;
            column.style.justifyContent = Justify.FlexEnd;
            return column;
        }

        private static void ApplyBlendShapeUseToggleStyle(Toggle toggle)
        {
            if (toggle == null)
            {
                return;
            }

            // UI Toolkit's default Toggle reserves horizontal margins and
            // left-aligns its 14px checkmark inside a wider control. Remove
            // that extra space so the visible checkmark follows the shared
            // Use-column right padding exactly.
            toggle.style.width = k_BlendShapeToggleVisualWidth;
            toggle.style.flexShrink = 0f;
            toggle.style.marginLeft = 0f;
            toggle.style.marginRight = 0f;
        }

        private static Label CreateComparisonPill(BlendShapeComparisonStatus status)
        {
            string text = GetBlendShapeStatusText(status);
            var label = new Label(text);
            label.style.width = GetComparisonPillWidth(status);
            label.style.flexShrink = 0f;
            label.style.marginLeft = 6f;
            label.style.paddingLeft = 5f;
            label.style.paddingRight = 5f;
            label.style.paddingTop = 1f;
            label.style.paddingBottom = 1f;
            label.style.unityTextAlign = TextAnchor.MiddleCenter;
            label.style.borderBottomWidth = 1f;
            label.style.borderTopWidth = 1f;
            label.style.borderLeftWidth = 1f;
            label.style.borderRightWidth = 1f;
            label.style.borderTopLeftRadius = 6f;
            label.style.borderTopRightRadius = 6f;
            label.style.borderBottomLeftRadius = 6f;
            label.style.borderBottomRightRadius = 6f;
            label.style.backgroundColor = status switch
            {
                BlendShapeComparisonStatus.New => new Color(0.20f, 0.36f, 0.22f),
                BlendShapeComparisonStatus.Changed => new Color(0.34f, 0.28f, 0.15f),
                BlendShapeComparisonStatus.Same => new Color(0.28f, 0.28f, 0.28f),
                _ => new Color(0.24f, 0.24f, 0.24f)
            };
            var borderColor = status switch
            {
                BlendShapeComparisonStatus.New => new Color(0.27f, 0.48f, 0.30f),
                BlendShapeComparisonStatus.Changed => new Color(0.48f, 0.39f, 0.22f),
                BlendShapeComparisonStatus.Same => new Color(0.42f, 0.42f, 0.42f),
                _ => new Color(0.36f, 0.36f, 0.36f)
            };
            label.style.borderBottomColor = borderColor;
            label.style.borderTopColor = borderColor;
            label.style.borderLeftColor = borderColor;
            label.style.borderRightColor = borderColor;
            label.style.color = status == BlendShapeComparisonStatus.Same
                ? new Color(0.78f, 0.78f, 0.78f)
                : new Color(0.90f, 0.90f, 0.90f);
            return label;
        }

        private static bool ShouldShowComparisonPill(BlendShapeComparisonStatus status)
        {
            return status != BlendShapeComparisonStatus.Same;
        }

        private static float GetComparisonPillWidth(BlendShapeComparisonStatus status)
        {
            return status switch
            {
                BlendShapeComparisonStatus.New => 52f,
                BlendShapeComparisonStatus.Changed => 52f,
                _ => 84f
            };
        }

        private static void SetShapeToggleValues(
            VisualElement root,
            ISet<string> selected)
        {
            foreach (var toggle in root.Query<Toggle>().ToList())
            {
                if (toggle.userData is string shapeName)
                {
                    toggle.SetValueWithoutNotify(selected.Contains(shapeName));
                }
            }
        }

        protected override void DrawOptionsGUI(
            BlendShapeExtractionOptions options,
            MeshFeatureOptionsEditorContext context)
        {
            if (options == null)
            {
                return;
            }

            DrawGeneralOptions(options, context);
            DrawBlendShapeToggles(options, context);
        }

        private void DrawGeneralOptions(
            BlendShapeExtractionOptions options,
            MeshFeatureOptionsEditorContext context)
        {
            options.Enabled = EditorGUILayout.ToggleLeft(Localization.S("features.blend-shapes.toggle"), options.Enabled);
            options.DefaultBaseMode = (BlendShapeBaseMode)EditorGUILayout.EnumPopup(
                Localization.S("features.blend-shapes.base_mode"),
                options.DefaultBaseMode);
            float previousTolerance = DeltaComparisonTolerance;
            DeltaComparisonTolerance = EditorGUILayout.DelayedFloatField(
                Localization.S("features.blend-shapes.delta_comparison_tolerance"),
                DeltaComparisonTolerance);
            if (!Mathf.Approximately(previousTolerance, DeltaComparisonTolerance))
            {
                context?.RequestInspectionRefresh?.Invoke();
            }
        }

        private void DrawBlendShapeToggles(
            BlendShapeExtractionOptions options,
            MeshFeatureOptionsEditorContext context)
        {
            EditorGUI.BeginDisabledGroup(!options.Enabled);
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField(Localization.G("common.meshes"), EditorStyles.boldLabel);

            if (context?.SourceFbxGo == null || context.OriginFbxGo == null)
            {
                EditorGUILayout.HelpBox(Localization.S("patch_creator.assign_fbx_hint"), MessageType.Info);
                EditorGUI.EndDisabledGroup();
                return;
            }

            var meshPaths = context.MeshPaths?
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToArray() ?? Array.Empty<string>();
            if (meshPaths.Length == 0)
            {
                EditorGUILayout.HelpBox(Localization.S("patch_creator.no_meshes"), MessageType.Info);
                EditorGUI.EndDisabledGroup();
                return;
            }

            var sourceMeshes = CollectUnityMeshesByPath(context.SourceFbxGo);
            var originMeshes = CollectUnityMeshesByPath(context.OriginFbxGo);
            context.TryGetCachedData(
                BlendShapeFeature.FeatureTypeId,
                out BlendShapeFbxComparison comparison);

            UpdateScrollHeight(context);
            m_ScrollPosition = EditorGUILayout.BeginScrollView(m_ScrollPosition, GUILayout.Height(m_BlendShapeScrollHeight));
            foreach (string meshPath in meshPaths)
            {
                DrawMeshBlendShapeToggles(options, meshPath, sourceMeshes, originMeshes, context.InspectionSession, comparison);
            }

            EditorGUILayout.EndScrollView();
            EditorGUI.EndDisabledGroup();
        }

        private void UpdateScrollHeight(MeshFeatureOptionsEditorContext context)
        {
            if (Event.current.type != EventType.Repaint || context.AvailableHeight <= 0f)
            {
                return;
            }

            Rect lastRect = GUILayoutUtility.GetLastRect();
            float remainingHeight = context.AvailableHeight - lastRect.yMax - 12f;
            m_BlendShapeScrollHeight = Mathf.Max(120f, remainingHeight);
        }

        private void DrawMeshBlendShapeToggles(
            BlendShapeExtractionOptions options,
            string meshPath,
            IReadOnlyDictionary<string, Mesh> sourceMeshes,
            IReadOnlyDictionary<string, Mesh> originMeshes,
            FbxInspectionSession inspectionSession,
            BlendShapeFbxComparison comparison)
        {
            string normalizedPath = FbxNameUtility.NormalizePath(meshPath ?? string.Empty);
            sourceMeshes.TryGetValue(normalizedPath, out var sourceMesh);
            BlendShapeMeshComparison meshComparison = null;
            comparison?.TryGetMesh(normalizedPath, out meshComparison);
            var blendShapeNames = GetBlendShapeNames(sourceMesh, meshComparison, inspectionSession, normalizedPath);
            if (blendShapeNames.Count == 0)
            {
                return;
            }

            originMeshes.TryGetValue(normalizedPath, out var originMesh);
            EnsureDefaultSelection(options, normalizedPath, originMesh, meshComparison, blendShapeNames);

            var selected = new HashSet<string>(options.GetSelectedShapeNames(normalizedPath), StringComparer.Ordinal);
            string key = normalizedPath;
            if (!m_MeshFoldouts.ContainsKey(key))
            {
                m_MeshFoldouts[key] = selected.Count > 0;
            }

            m_MeshFoldouts[key] = EditorGUILayout.Foldout(
                m_MeshFoldouts[key],
                $"{normalizedPath} ({selected.Count}/{blendShapeNames.Count})",
                true);
            if (!m_MeshFoldouts[key])
            {
                return;
            }

            EditorGUI.indentLevel++;
            EditorGUILayout.BeginVertical(GUI.skin.box);
            EditorGUILayout.LabelField(Localization.G("common.mesh_path"), new GUIContent(normalizedPath));
            bool hasBaseModeOverride = EditorGUILayout.ToggleLeft(
                Localization.S("features.blend-shapes.override_base_mode"),
                options.HasBaseModeOverride(normalizedPath));
            if (hasBaseModeOverride)
            {
                if (!options.HasBaseModeOverride(normalizedPath))
                {
                    options.SetBaseModeOverride(normalizedPath, options.DefaultBaseMode);
                }
            }
            else
            {
                options.SetBaseModeOverride(normalizedPath, null);
            }

            using (new EditorGUI.DisabledScope(!hasBaseModeOverride))
            {
                var mode = (BlendShapeBaseMode)EditorGUILayout.EnumPopup(
                    Localization.S("features.blend-shapes.base_mode"),
                    options.GetBaseMode(normalizedPath));
                if (hasBaseModeOverride)
                {
                    options.SetBaseModeOverride(normalizedPath, mode);
                }
            }

            if (!string.IsNullOrEmpty(meshComparison?.Message))
            {
                EditorGUILayout.HelpBox(meshComparison.Message, MessageType.Warning);
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(Localization.S("common.enable_all")))
            {
                selected = new HashSet<string>(blendShapeNames, StringComparer.Ordinal);
            }

            if (GUILayout.Button(Localization.S("common.disable_all")))
            {
                selected.Clear();
            }

            EditorGUILayout.EndHorizontal();

            foreach (string shapeName in blendShapeNames)
            {
                bool wasSelected = selected.Contains(shapeName);
                bool isSelected = EditorGUILayout.ToggleLeft(GetBlendShapeLabel(shapeName, meshComparison), wasSelected);
                if (isSelected)
                {
                    selected.Add(shapeName);
                }
                else if (wasSelected)
                {
                    selected.Remove(shapeName);
                }
            }

            options.SetSelectedShapeNames(normalizedPath, selected);
            EditorGUILayout.EndVertical();
            EditorGUI.indentLevel--;
        }

        private static void EnsureDefaultSelection(
            BlendShapeExtractionOptions options,
            string meshPath,
            Mesh originMesh,
            BlendShapeMeshComparison meshComparison,
            IReadOnlyList<string> blendShapeNames)
        {
            if (options.HasSelectedShapeNamesForMesh(meshPath))
            {
                return;
            }

            var selected = BuildDefaultSelection(originMesh, meshComparison, blendShapeNames);
            options.SetSelectedShapeNames(meshPath, selected);
        }

        private static IReadOnlyList<string> BuildDefaultSelection(
            Mesh originMesh,
            BlendShapeMeshComparison meshComparison,
            IReadOnlyList<string> blendShapeNames)
        {
            var selected = new List<string>();
            foreach (string shapeName in blendShapeNames)
            {
                if (meshComparison != null &&
                    meshComparison.TryGetStatus(shapeName, out var status))
                {
                    if (status == BlendShapeComparisonStatus.New ||
                        status == BlendShapeComparisonStatus.Changed)
                    {
                        selected.Add(shapeName);
                    }

                    continue;
                }

                if (originMesh == null || originMesh.GetBlendShapeIndex(shapeName) == -1)
                {
                    selected.Add(shapeName);
                }
            }

            return selected;
        }

        private static IReadOnlyList<string> GetBlendShapeNames(
            Mesh mesh,
            BlendShapeMeshComparison meshComparison,
            FbxInspectionSession inspectionSession,
            string meshPath)
        {
            if (meshComparison?.BlendShapes != null && meshComparison.BlendShapes.Count > 0)
            {
                return meshComparison.BlendShapes
                    .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.Name))
                    .Select(entry => entry.Name)
                    .ToArray();
            }

            if (inspectionSession != null)
            {
                var channels = inspectionSession.GetSourceBlendShapes(meshPath);
                if (channels != null && channels.Count > 0)
                {
                    var fromFbx = new List<string>(channels.Count);
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    for (int i = 0; i < channels.Count; i++)
                    {
                        string channelName = channels[i]?.Name;
                        if (!string.IsNullOrWhiteSpace(channelName) && seen.Add(channelName))
                        {
                            fromFbx.Add(channelName);
                        }
                    }

                    if (fromFbx.Count > 0)
                    {
                        return fromFbx;
                    }
                }
            }

            var names = new List<string>();
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

        private static Dictionary<string, Mesh> CollectUnityMeshesByPath(GameObject root)
        {
            var map = new Dictionary<string, Mesh>(StringComparer.Ordinal);
            if (root == null)
            {
                return map;
            }

            var rootTransform = root.transform;
            var skinned = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < skinned.Length; i++)
            {
                var smr = skinned[i];
                if (smr == null || smr.sharedMesh == null)
                {
                    continue;
                }

                string path = GetRelativeTransformPath(smr.transform, rootTransform);
                if (!map.ContainsKey(path))
                {
                    map.Add(path, smr.sharedMesh);
                }
            }

            var filters = root.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < filters.Length; i++)
            {
                var mf = filters[i];
                if (mf == null || mf.sharedMesh == null)
                {
                    continue;
                }

                string path = GetRelativeTransformPath(mf.transform, rootTransform);
                if (!map.ContainsKey(path))
                {
                    map.Add(path, mf.sharedMesh);
                }
            }

            return map;
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

        private static string GetBlendShapeLabel(
            string shapeName,
            BlendShapeMeshComparison meshComparison)
        {
            string statusText = GetBlendShapeStatusText(GetBlendShapeStatus(shapeName, meshComparison));
            if (string.IsNullOrEmpty(statusText))
            {
                return shapeName;
            }

            return $"{shapeName} ({statusText})";
        }

        private static BlendShapeComparisonStatus GetBlendShapeStatus(
            string shapeName,
            BlendShapeMeshComparison meshComparison)
        {
            if (meshComparison == null ||
                !meshComparison.TryGetStatus(shapeName, out var status))
            {
                return BlendShapeComparisonStatus.Unavailable;
            }

            return status;
        }

        private static string GetBlendShapeStatusText(BlendShapeComparisonStatus status)
        {
            return status switch
            {
                BlendShapeComparisonStatus.New => Localization.S("common.status.new"),
                BlendShapeComparisonStatus.Changed => Localization.S("common.status.diff"),
                BlendShapeComparisonStatus.Same => Localization.S("common.status.same"),
                BlendShapeComparisonStatus.Unavailable => Localization.S("common.status.unavailable"),
                _ => Localization.S("common.status.unknown")
            };
        }
    }
}
