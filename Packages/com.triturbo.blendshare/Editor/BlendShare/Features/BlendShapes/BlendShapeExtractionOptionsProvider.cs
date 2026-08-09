using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShapeShare;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Inspector;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Triturbo.BlendShare.Features.BlendShapes
{
    public sealed class BlendShapeExtractionOptionsProvider
        : MeshFeatureExtractionOptionsProvider<BlendShapeExtractionOptions>,
          IMeshFeatureInspectionProvider,
          IUIToolkitMeshFeatureOptionsProvider
    {
        private readonly Dictionary<string, bool> meshFoldouts = new();
        private const float ComparisonPillSlotWidth = 100f;
        private const float BlendShapeTableHorizontalPadding = 4f;
        private const float BlendShapeUseColumnWidth = 60f;
        private const float BlendShapeUseColumnRightPadding = 6f;
        private const float BlendShapeToggleVisualWidth = 14f;
        private Vector2 scrollPosition;
        private float blendShapeScrollHeight = 240f;
        private readonly List<System.Action> baseModeRefreshers = new();

        public override string FeatureId => BlendShapeFeatureObject.Id;
        public override string TabLabel => Localization.FeatureName(FeatureId, "BlendShapes");
        public override int DisplayOrder => -100;

        protected override BlendShapeExtractionOptions CreateDefault()
        {
            return new BlendShapeExtractionOptions();
        }

        public object BuildInspectionData(
            FbxInspectionSession session,
            MeshFeatureExtractionOptionsSet options,
            IReadOnlyList<MeshFeatureExtractionMeshRequest> meshes)
        {
            return BlendShapeFbxComparison.BuildInspectionData(session, options, meshes);
        }

        public VisualElement CreateOptionsElement(
            MeshFeatureExtractionOptions rawOptions,
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

            baseModeRefreshers.Clear();
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
                options.BaseMode);
            baseMode.RegisterValueChangedCallback(evt =>
            {
                options.BaseMode = evt.newValue;
                foreach (var refresh in baseModeRefreshers)
                {
                    refresh?.Invoke();
                }
            });
            root.Add(baseMode);

            var deltaTolerance = new FloatField(
                Localization.S("features.blend-shapes.delta_comparison_tolerance"))
            {
                value = options.DeltaComparisonTolerance,
                isDelayed = true,
                tooltip = Localization.S("features.blend-shapes.delta_comparison_tolerance_tooltip")
            };
            deltaTolerance.RegisterValueChangedCallback(evt =>
            {
                options.DeltaComparisonTolerance = evt.newValue;
                deltaTolerance.SetValueWithoutNotify(options.DeltaComparisonTolerance);
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

            var requests = context.Meshes?
                .Where(mesh => mesh != null)
                .ToArray() ?? System.Array.Empty<MeshFeatureExtractionMeshRequest>();
            if (requests.Length == 0)
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

            var sourceMeshes = new UnityMeshExtractionSource(context.SourceFbxGo);
            var originMeshes = new UnityMeshExtractionSource(context.OriginFbxGo);
            context.TryGetCachedData(
                BlendShapeFeatureObject.Id,
                out BlendShapeFbxComparison comparison);

            System.Action refreshContent = null;
            var meshSections = new List<BlendShapeMeshSection>();
            foreach (var request in requests)
            {
                var meshSection = CreateMeshSection(
                    options,
                    request,
                    sourceMeshes,
                    originMeshes,
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
                context => CreateBlendShapeStickyHeader(context, meshSections, null));
            System.Action refreshRows = () =>
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
            MeshFeatureExtractionMeshRequest request,
            UnityMeshExtractionSource sourceMeshes,
            UnityMeshExtractionSource originMeshes,
            BlendShapeFbxComparison comparison,
            System.Action onSelectionChanged)
        {
            var sourceMesh = sourceMeshes.GetMesh(request.Path);
            BlendShapeMeshComparison meshComparison = null;
            comparison?.TryGetMesh(request.Path, out meshComparison);
            var blendShapeNames = GetBlendShapeNames(sourceMesh, meshComparison);
            if (blendShapeNames.Count == 0)
            {
                return null;
            }

            var originMesh = originMeshes.GetMesh(request.Path);
            EnsureDefaultSelection(options, request, sourceMesh, originMesh, meshComparison, blendShapeNames);

            var selected = new HashSet<string>(options.GetSelectedBlendShapeNames(request.Path));
            string key = MeshFeatureExtractionSession.BuildMeshKey(request.Path);
            if (!meshFoldouts.ContainsKey(key))
            {
                meshFoldouts[key] = selected.Count > 0;
            }

            var foldout = new Foldout
            {
                value = meshFoldouts[key]
            };
            foldout.RegisterValueChangedCallback(evt => meshFoldouts[key] = evt.newValue);

            void UpdateTitle()
            {
                foldout.text = $"{request.Path} ({selected.Count}/{blendShapeNames.Count})";
            }

            UpdateTitle();
            foldout.style.marginBottom = 6f;

            var meshEnabled = new Toggle(Localization.S("common.enabled"))
            {
                value = options.IsMeshEnabled(request.Path)
            };
            foldout.Add(meshEnabled);

            var meshControls = new VisualElement();
            meshControls.SetEnabled(meshEnabled.value);
            foldout.Add(meshControls);

            var pathLabel = new Label($"{Localization.S("common.mesh_path")}: {request.Path}");
            BlendShareInspectorUi.StyleStrong(pathLabel);
            meshControls.Add(pathLabel);

            var baseModeRow = new VisualElement();
            baseModeRow.style.flexDirection = FlexDirection.Row;
            baseModeRow.style.alignItems = Align.Center;
            var overrideBaseMode = new Toggle(Localization.S("features.blend-shapes.override_base_mode"))
            {
                value = options.HasBaseModeOverride(request.Path)
            };
            var meshBaseMode = CreateBaseModeField(string.Empty, options.GetBaseMode(request.Path));
            meshBaseMode.style.flexGrow = 1f;
            meshBaseMode.style.marginLeft = 6f;
            baseModeRow.Add(overrideBaseMode);
            baseModeRow.Add(meshBaseMode);
            meshControls.Add(baseModeRow);

            void RefreshBaseMode()
            {
                bool hasOverride = options.HasBaseModeOverride(request.Path);
                overrideBaseMode.SetValueWithoutNotify(hasOverride);
                meshBaseMode.SetValueWithoutNotify(options.GetBaseMode(request.Path));
                meshBaseMode.SetEnabled(hasOverride);
            }

            overrideBaseMode.RegisterValueChangedCallback(evt =>
            {
                options.SetBaseModeOverride(request.Path, evt.newValue);
                RefreshBaseMode();
            });
            meshBaseMode.RegisterValueChangedCallback(evt =>
            {
                options.SetBaseMode(request.Path, evt.newValue);
            });
            baseModeRefreshers.Add(RefreshBaseMode);
            RefreshBaseMode();

            if (!string.IsNullOrEmpty(meshComparison?.Message))
            {
                meshControls.Add(new HelpBox(meshComparison.Message, HelpBoxMessageType.Warning));
            }

            var buttonRow = new VisualElement();
            buttonRow.style.flexDirection = FlexDirection.Row;
            buttonRow.style.marginTop = 4f;
            buttonRow.style.marginBottom = 4f;

            void EnableAll()
            {
                SetSelectedBlendShapes(blendShapeNames);
            }

            void DisableAll()
            {
                SetSelectedBlendShapes(System.Array.Empty<string>());
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

            void SetSelectedBlendShapes(IEnumerable<string> shapeNames)
            {
                selected = new HashSet<string>(shapeNames ?? System.Array.Empty<string>());
                options.SetSelectedBlendShapeNames(request.Path, selected);
                SetShapeToggleValues(foldout, selected);
                UpdateTitle();
                onSelectionChanged?.Invoke();
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
                options.SetMeshEnabled(request.Path, evt.newValue);
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

                    options.SetSelectedBlendShapeNames(request.Path, selected);
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
                    BlendShapeUseColumnWidth,
                    "blendshape-table-use-cell",
                    BlendShapeUseColumnRightPadding);
                useSlot.Add(shapeToggle);
                row.Add(useSlot);
                table.Add(row);
            }

            meshControls.Add(table);

            void SetMeshEnabled(bool value)
            {
                options.SetMeshEnabled(request.Path, value);
                meshEnabled.SetValueWithoutNotify(value);
                meshControls.SetEnabled(value);
                onSelectionChanged?.Invoke();
            }

            void RefreshSection()
            {
                UpdateTitle();
                meshEnabled.SetValueWithoutNotify(options.IsMeshEnabled(request.Path));
                meshControls.SetEnabled(options.IsMeshEnabled(request.Path));
                SetShapeToggleValues(foldout, selected);
            }

            var result = new BlendShapeMeshSection
            {
                Title = () => foldout.text,
                IsEnabled = () => options.IsMeshEnabled(request.Path),
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
                context => CreateBlendShapeStickyHeader(context, new[] { result }, result));
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
            bar.SetRightContentPadding(BlendShapeTableHorizontalPadding);
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
                ComparisonPillSlotWidth,
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
                BlendShapeUseColumnWidth,
                BlendShapeToggleVisualWidth);
            stickyUseCell.name = "blendshape-sticky-use-cell";
            stickyUseCell.style.paddingRight = BlendShapeUseColumnRightPadding;
            ApplyBlendShapeUseToggleStyle(
                stickyUseCell.Q<Toggle>("blendshape-sticky-toggle:use"));
            bar.Trailing.Add(stickyUseCell);
            return bar;
        }

        private static Toggle CreateOptionToggle(
            string label,
            bool value,
            System.Action<bool> onChanged)
        {
            var toggle = new Toggle(label)
            {
                value = value
            };
            toggle.RegisterValueChangedCallback(evt => onChanged(evt.newValue));
            return toggle;
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
            public System.Func<string> Title { get; set; }
            public System.Func<bool> IsEnabled { get; set; }
            public System.Action<bool> SetEnabled { get; set; }
            public System.Action EnableAll { get; set; }
            public System.Action DisableAll { get; set; }
            public System.Action Restore { get; set; }
            public System.Action DisableChanged { get; set; }
            public System.Action Refresh { get; set; }
            public System.Func<int> SelectedCount { get; set; }
            public System.Func<int> TotalCount { get; set; }
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
                ComparisonPillSlotWidth,
                "blendshape-table-status-header");
            statusHeader.Add(statusLabel);
            var useHeader = CreateBlendShapeFixedColumn(
                BlendShapeUseColumnWidth,
                "blendshape-table-use-header",
                BlendShapeUseColumnRightPadding);
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
            row.style.paddingLeft = BlendShapeTableHorizontalPadding;
            row.style.paddingRight = BlendShapeTableHorizontalPadding;

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
                ComparisonPillSlotWidth,
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
            toggle.style.width = BlendShapeToggleVisualWidth;
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
            options.BaseMode = (BlendShapeBaseMode)EditorGUILayout.EnumPopup(
                Localization.S("features.blend-shapes.base_mode"),
                options.BaseMode);
            float previousTolerance = options.DeltaComparisonTolerance;
            options.DeltaComparisonTolerance = EditorGUILayout.DelayedFloatField(
                Localization.S("features.blend-shapes.delta_comparison_tolerance"),
                options.DeltaComparisonTolerance);
            if (previousTolerance != options.DeltaComparisonTolerance)
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

            var requests = context.Meshes?
                .Where(mesh => mesh != null)
                .ToArray() ?? System.Array.Empty<MeshFeatureExtractionMeshRequest>();
            if (requests.Length == 0)
            {
                EditorGUILayout.HelpBox(Localization.S("patch_creator.no_meshes"), MessageType.Info);
                EditorGUI.EndDisabledGroup();
                return;
            }

            var sourceMeshes = new UnityMeshExtractionSource(context.SourceFbxGo);
            var originMeshes = new UnityMeshExtractionSource(context.OriginFbxGo);
            context.TryGetCachedData(
                BlendShapeFeatureObject.Id,
                out BlendShapeFbxComparison comparison);

            UpdateScrollHeight(context);
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition, GUILayout.Height(blendShapeScrollHeight));
            foreach (var request in requests)
            {
                DrawMeshBlendShapeToggles(options, request, sourceMeshes, originMeshes, comparison);
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
            blendShapeScrollHeight = Mathf.Max(120f, remainingHeight);
        }

        private void DrawMeshBlendShapeToggles(
            BlendShapeExtractionOptions options,
            MeshFeatureExtractionMeshRequest request,
            UnityMeshExtractionSource sourceMeshes,
            UnityMeshExtractionSource originMeshes,
            BlendShapeFbxComparison comparison)
        {
            var sourceMesh = sourceMeshes.GetMesh(request.Path);
            BlendShapeMeshComparison meshComparison = null;
            comparison?.TryGetMesh(request.Path, out meshComparison);
            var blendShapeNames = GetBlendShapeNames(sourceMesh, meshComparison);
            if (blendShapeNames.Count == 0)
            {
                return;
            }

            var originMesh = originMeshes.GetMesh(request.Path);
            EnsureDefaultSelection(options, request, sourceMesh, originMesh, meshComparison, blendShapeNames);

            var selected = new HashSet<string>(options.GetSelectedBlendShapeNames(request.Path));
            string key = MeshFeatureExtractionSession.BuildMeshKey(request.Path);
            if (!meshFoldouts.ContainsKey(key))
            {
                meshFoldouts[key] = selected.Count > 0;
            }

            string displayName = request.Path;
            meshFoldouts[key] = EditorGUILayout.Foldout(
                meshFoldouts[key],
                $"{displayName} ({selected.Count}/{blendShapeNames.Count})",
                true);
            if (!meshFoldouts[key])
            {
                return;
            }

            EditorGUI.indentLevel++;
            EditorGUILayout.BeginVertical(GUI.skin.box);
            EditorGUILayout.LabelField(Localization.G("common.mesh_path"), new GUIContent(request.Path));
            bool hasBaseModeOverride = EditorGUILayout.ToggleLeft(
                Localization.S("features.blend-shapes.override_base_mode"),
                options.HasBaseModeOverride(request.Path));
            options.SetBaseModeOverride(request.Path, hasBaseModeOverride);
            using (new EditorGUI.DisabledScope(!hasBaseModeOverride))
            {
                var mode = (BlendShapeBaseMode)EditorGUILayout.EnumPopup(
                    Localization.S("features.blend-shapes.base_mode"),
                    options.GetBaseMode(request.Path));
                if (hasBaseModeOverride)
                {
                    options.SetBaseMode(request.Path, mode);
                }
            }
            if (!string.IsNullOrEmpty(meshComparison?.Message))
            {
                EditorGUILayout.HelpBox(meshComparison.Message, MessageType.Warning);
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(Localization.S("common.enable_all")))
            {
                selected = new HashSet<string>(blendShapeNames);
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

            options.SetSelectedBlendShapeNames(request.Path, selected);
            EditorGUILayout.EndVertical();
            EditorGUI.indentLevel--;
        }

        private static void EnsureDefaultSelection(
            BlendShapeExtractionOptions options,
            MeshFeatureExtractionMeshRequest request,
            Mesh sourceMesh,
            Mesh originMesh,
            BlendShapeMeshComparison meshComparison,
            IReadOnlyList<string> blendShapeNames)
        {
            if (options.HasSelectedBlendShapeNames(request.Path))
            {
                return;
            }

            var selected = BuildDefaultSelection(originMesh, meshComparison, blendShapeNames);
            options.SetSelectedBlendShapeNames(request.Path, selected);
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
            BlendShapeMeshComparison meshComparison)
        {
            if (meshComparison?.BlendShapes != null && meshComparison.BlendShapes.Count > 0)
            {
                return meshComparison.BlendShapes
                    .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.Name))
                    .Select(entry => entry.Name)
                    .ToArray();
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
