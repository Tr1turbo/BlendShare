using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Triturbo.BlendShare.UI;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.UI.Inspectors;
using Triturbo.BlendShare.Model.Features;
using Triturbo.BlendShare.Pipeline.Extraction;
using Triturbo.BlendShare.Pipeline.Inspection;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Triturbo.BlendShare.UI.Extraction
{
    internal enum SkinWeightSelectionMode
    {
        Simple,
        Advanced
    }

    public sealed class SkinWeightExtractionOptionsProvider
        : MeshFeatureExtractionOptionsProvider<SkinWeightExtractionOptions>,
          IMeshFeatureInspectionProvider,
          IUIToolkitMeshFeatureOptionsProvider
    {
        private const float k_StatusSlotWidth = 94f;
        private const float k_SimpleRowHeight = 22f;
        private const float k_SimpleListPanelMinHeight = 124f;
        private const float k_AdvancedFeatureColumnWidth = 170f;
        private const float k_AdvancedTableHorizontalPadding = 4f;

        private readonly ConditionalWeakTable<SkinWeightExtractionOptions, SkinWeightSelectionState> m_SelectionStates =
            new ConditionalWeakTable<SkinWeightExtractionOptions, SkinWeightSelectionState>();
        private readonly Dictionary<string, bool> m_MeshFoldouts = new Dictionary<string, bool>(StringComparer.Ordinal);
        private bool m_ShowAllBones;
        private string m_MeshSearch = string.Empty;
        private string m_BoneSearch = string.Empty;
        private string m_AdvancedBoneSearch = string.Empty;
        private Vector2 m_AdvancedScrollOffset;

        public override string FeatureTypeId => SkinWeightFeature.FeatureTypeId;
        public override string TabLabel => Localization.FeatureName(FeatureTypeId, "Skin Weights");
        public override int DisplayOrder => -50;

        public override void ResetUiState()
        {
            m_MeshFoldouts.Clear();
            m_ShowAllBones = false;
            m_MeshSearch = string.Empty;
            m_BoneSearch = string.Empty;
            m_AdvancedBoneSearch = string.Empty;
            m_AdvancedScrollOffset = Vector2.zero;
        }

        protected override SkinWeightExtractionOptions CreateDefault()
        {
            return new SkinWeightExtractionOptions
            {
                Enabled = false,
                ExtractAllClustersWhenSelectionEmpty = false
            };
        }

        public object BuildInspectionData(
            FbxInspectionSession session,
            ExtractionOptionsSet options,
            SourceOffsetTable sourceOffsets,
            IReadOnlyList<string> meshPaths)
        {
            var comparison = SkinWeightFbxComparison.BuildInspectionData(
                session,
                meshPaths,
                path => sourceOffsets != null && sourceOffsets.TryGet(path, out var offset) ? offset : null);

            if (options != null &&
                options.TryGet(FeatureTypeId, out SkinWeightExtractionOptions swOptions) &&
                swOptions != null &&
                comparison.Bones.Count > 0)
            {
                var controller = new SkinWeightSelectionController(
                    swOptions,
                    GetOrCreateSelectionState(swOptions),
                    comparison);
                controller.EnsureDefaultSelection();
            }

            return comparison;
        }

        public VisualElement CreateOptionsElement(
            IFeatureExtractionOptions rawOptions,
            MeshFeatureOptionsEditorContext context)
        {
            if (rawOptions is not SkinWeightExtractionOptions options)
            {
                return new VisualElement();
            }

            var root = new VisualElement();
            root.style.flexGrow = 1f;
            root.style.minHeight = 0f;
            root.style.flexDirection = FlexDirection.Column;

            var enabledToggle = new Toggle(Localization.S("features.skin-weights.toggle"))
            {
                value = options.Enabled
            };
            root.Add(enabledToggle);

            var content = new VisualElement();
            content.style.flexGrow = 1f;
            content.style.minHeight = 0f;
            content.style.flexDirection = FlexDirection.Column;
            content.SetEnabled(options.Enabled);
            root.Add(content);

            enabledToggle.RegisterValueChangedCallback(evt =>
            {
                options.Enabled = evt.newValue;
                content.SetEnabled(evt.newValue);
            });

            if (context?.SourceFbxGo == null || context.OriginFbxGo == null)
            {
                content.Add(new HelpBox(
                    Localization.S("patch_creator.assign_fbx_hint"),
                    HelpBoxMessageType.Info));
                return root;
            }

            if (!context.TryGetCachedData(SkinWeightFeature.FeatureTypeId, out SkinWeightFbxComparison comparison) ||
                comparison == null ||
                comparison.Bones.Count == 0)
            {
                content.Add(new HelpBox(
                    Localization.S("features.skin-weights.no_differences"),
                    HelpBoxMessageType.Info));
                return root;
            }

            var selectionState = GetOrCreateSelectionState(options);
            var controller = new SkinWeightSelectionController(options, selectionState, comparison);
            controller.EnsureDefaultSelection();
            AddDiagnostics(content, comparison);

            var modeChoices = new List<string>
            {
                Localization.S("features.skin-weights.mode.simple"),
                Localization.S("features.skin-weights.mode.advanced")
            };
            var modeField = new PopupField<string>(
                Localization.S("features.skin-weights.mode"),
                modeChoices,
                selectionState.SelectionMode == SkinWeightSelectionMode.Simple ? 0 : 1);
            content.Add(modeField);

            var modeContent = new VisualElement();
            modeContent.style.flexGrow = 1f;
            modeContent.style.minHeight = 0f;
            modeContent.style.flexDirection = FlexDirection.Column;
            content.Add(modeContent);

            void RebuildModeContent()
            {
                modeContent.Clear();
                modeContent.Add(selectionState.SelectionMode == SkinWeightSelectionMode.Simple
                    ? CreateSimpleMode(controller, comparison)
                    : CreateAdvancedMode(controller, comparison));
            }

            modeField.RegisterValueChangedCallback(evt =>
            {
                int requestedIndex = modeChoices.IndexOf(evt.newValue);
                if (requestedIndex == 1)
                {
                    controller.SwitchToAdvanced();
                    RebuildModeContent();
                    return;
                }

                int expansionCount = controller.GetSimpleExpansionCount();
                if (expansionCount > 0 && !EditorUtility.DisplayDialog(
                        Localization.S("features.skin-weights.mode.expand.title"),
                        Localization.SF("features.skin-weights.mode.expand.message", expansionCount),
                        Localization.S("features.skin-weights.mode.expand.confirm"),
                        Localization.S("common.cancel")))
                {
                    modeField.SetValueWithoutNotify(modeChoices[1]);
                    return;
                }

                controller.SwitchToSimple();
                RebuildModeContent();
            });

            RebuildModeContent();
            return root;
        }

        protected override void DrawOptionsGUI(
            SkinWeightExtractionOptions options,
            MeshFeatureOptionsEditorContext context)
        {
            if (options != null)
            {
                options.Enabled = EditorGUILayout.ToggleLeft(
                    Localization.S("features.skin-weights.toggle"),
                    options.Enabled);
            }
        }

        private SkinWeightSelectionState GetOrCreateSelectionState(SkinWeightExtractionOptions options)
        {
            return m_SelectionStates.GetOrCreateValue(options);
        }

        private VisualElement CreateSimpleMode(
            SkinWeightSelectionController controller,
            SkinWeightFbxComparison comparison)
        {
            var root = CreateModeRoot();
            Action refresh = null;
            root.Add(CreateCommandBar(controller, () => refresh?.Invoke()));

            var split = new TwoPaneSplitView(0, 300f, TwoPaneSplitViewOrientation.Vertical);
            split.style.flexGrow = 1f;
            split.style.minHeight = 0f;
            split.style.overflow = Overflow.Hidden;
            var meshList = CreateSimpleMeshList(controller, comparison, () => refresh?.Invoke());
            var boneList = CreateSimpleBoneList(controller, comparison, () => refresh?.Invoke());
            refresh = () =>
            {
                meshList.Refresh();
                boneList.Refresh();
            };
            split.Add(meshList.Root);
            split.Add(boneList.Root);
            root.Add(split);
            return root;
        }

        private RefreshableElement CreateSimpleMeshList(
            SkinWeightSelectionController controller,
            SkinWeightFbxComparison comparison,
            Action refresh)
        {
            var panel = CreateListPanel(Localization.S("common.meshes"));
            var search = new ToolbarSearchField { value = m_MeshSearch };
            search.style.flexGrow = 0f;
            search.style.flexShrink = 0f;
            search.style.width = Length.Percent(100f);
            search.tooltip = Localization.S("features.skin-weights.search_meshes.tooltip");
            panel.Add(search);

            var list = new ListView
            {
                fixedItemHeight = k_SimpleRowHeight,
                virtualizationMethod = CollectionVirtualizationMethod.FixedHeight,
                selectionType = SelectionType.None,
                makeItem = CreateSimpleListItem
            };
            list.style.flexGrow = 1f;
            list.style.minHeight = 80f;
            panel.Add(list);

            void RefreshItems()
            {
                var visible = comparison.Meshes
                    .Where(mesh => MatchesSearch(mesh.MeshPath, m_MeshSearch))
                    .ToArray();
                list.itemsSource = visible;
                list.bindItem = (element, index) =>
                {
                    element.Clear();
                    var mesh = visible[index];
                    var row = CreateSelectionRow(
                        mesh.MeshPath,
                        FormatMeshSummary(mesh),
                        controller.IsSimpleMeshSelected(mesh.MeshPath),
                        value =>
                        {
                            controller.SetSimpleMeshSelected(mesh.MeshPath, value);
                            refresh?.Invoke();
                        });
                    element.Add(row);
                };
                list.Rebuild();
            }

            void RefreshSelection()
            {
                list.RefreshItems();
            }

            search.RegisterValueChangedCallback(evt =>
            {
                m_MeshSearch = evt.newValue ?? string.Empty;
                RefreshItems();
            });
            RefreshItems();
            return new RefreshableElement(panel, RefreshSelection);
        }

        private RefreshableElement CreateSimpleBoneList(
            SkinWeightSelectionController controller,
            SkinWeightFbxComparison comparison,
            Action refresh)
        {
            var showAll = new Toggle(Localization.S("features.skin-weights.show_all_bones"))
            {
                value = m_ShowAllBones
            };
            var panel = CreateListPanel(Localization.S("features.skin-weights.bones"), showAll);
            var search = new ToolbarSearchField { value = m_BoneSearch };
            search.style.flexGrow = 0f;
            search.style.flexShrink = 0f;
            search.style.width = Length.Percent(100f);
            search.tooltip = Localization.S("features.skin-weights.search_bones.tooltip");
            panel.Add(search);

            var list = new ListView
            {
                fixedItemHeight = k_SimpleRowHeight,
                virtualizationMethod = CollectionVirtualizationMethod.FixedHeight,
                selectionType = SelectionType.None,
                makeItem = CreateSimpleListItem
            };
            list.style.flexGrow = 1f;
            list.style.minHeight = 80f;
            panel.Add(list);

            void RefreshItems()
            {
                var source = m_ShowAllBones ? comparison.Bones : comparison.RelevantBones;
                var visible = source
                    .Where(bone => MatchesSearch(bone.BonePath, m_BoneSearch))
                    .ToArray();
                list.itemsSource = visible;
                list.bindItem = (element, index) =>
                {
                    element.Clear();
                    var bone = visible[index];
                    bool requiredParent = controller.IsRequiredParent(bone.BonePath);
                    var row = CreateSelectionRow(
                        GetBoneName(bone.BonePath),
                        FormatBoneSummary(bone),
                        controller.IsSimpleBoneSelected(bone.BonePath) || requiredParent,
                        value =>
                        {
                            controller.SetSimpleBoneSelected(bone.BonePath, value);
                            refresh?.Invoke();
                        },
                        !requiredParent,
                        bone.BonePath,
                        bone.RequiresCreateBone
                            ? Localization.S("features.skin-weights.status.new_bone")
                            : null);
                    element.Add(row);
                };
                list.Rebuild();
            }

            void RefreshSelection()
            {
                list.RefreshItems();
            }

            search.RegisterValueChangedCallback(evt =>
            {
                m_BoneSearch = evt.newValue ?? string.Empty;
                RefreshItems();
            });
            showAll.RegisterValueChangedCallback(evt =>
            {
                m_ShowAllBones = evt.newValue;
                RefreshItems();
            });
            RefreshItems();
            return new RefreshableElement(panel, RefreshSelection);
        }

        private VisualElement CreateAdvancedMode(
            SkinWeightSelectionController controller,
            SkinWeightFbxComparison comparison)
        {
            var root = CreateModeRoot();
            Action refresh = null;
            var stickyHeader = new StickyHeaderElement();
            root.Add(stickyHeader);

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1f;
            scroll.style.minHeight = 0f;
            scroll.verticalScroller.valueChanged += value => m_AdvancedScrollOffset.y = value;
            root.Add(scroll);

            var sectionRefreshers = new List<Action>();
            var sections = new List<StickyHeaderSection>();
            foreach (var mesh in comparison.Meshes)
            {
                var section = CreateAdvancedMeshSection(
                    controller,
                    comparison,
                    mesh,
                    sectionRefreshers,
                    () => refresh?.Invoke());
                sections.Add(section);
                scroll.Add(section.Root);
            }

            var transformSection = CreateAdvancedTransformSection(
                controller,
                comparison,
                sectionRefreshers,
                () => refresh?.Invoke());
            sections.Add(transformSection);
            scroll.Add(transformSection.Root);

            var meshOverview = new StickyHeaderSection(
                null,
                null,
                stickyContext => CreateAdvancedMeshStickyHeader(
                    stickyContext,
                    controller,
                    comparison,
                    null,
                    Localization.S("common.meshes")));
            Action refreshContent = () =>
            {
                foreach (var update in sectionRefreshers)
                {
                    update();
                }
            };
            refresh = () =>
            {
                refreshContent();
                stickyHeader.RequestRefresh();
            };
            stickyHeader.Bind(scroll, sections, meshOverview, refreshContent);
            scroll.schedule.Execute(() => scroll.scrollOffset = m_AdvancedScrollOffset);
            return root;
        }

        private StickyHeaderSection CreateAdvancedMeshSection(
            SkinWeightSelectionController controller,
            SkinWeightFbxComparison comparison,
            SkinWeightMeshComparison mesh,
            List<Action> sectionRefreshers,
            Action refresh)
        {
            string key = NormalizePathKey(mesh.MeshPath);
            if (!m_MeshFoldouts.ContainsKey(key))
            {
                m_MeshFoldouts[key] = mesh.Bones.Any(pair => IsPairSelected(controller, pair));
            }

            var foldout = new Foldout
            {
                value = m_MeshFoldouts[key]
            };
            foldout.style.marginBottom = 6f;
            bool built = false;
            var toggleRefreshers = new List<Action>();
            var meshControls = new VisualElement
            {
                name = $"skin-weight-mesh-controls:{key}"
            };
            var meshEnabled = new Toggle(Localization.S("common.enabled"))
            {
                name = $"skin-weight-mesh-enabled:{key}",
                value = controller.IsAdvancedMeshEnabled(mesh)
            };
            meshEnabled.RegisterValueChangedCallback(evt =>
            {
                controller.SetAdvancedMeshEnabled(mesh, evt.newValue);
                refresh?.Invoke();
            });
            foldout.Add(meshEnabled);
            foldout.Add(meshControls);

            int SelectedCount() => CountSelectedPairs(controller, mesh);

            void UpdateTitle()
            {
                foldout.text = $"{mesh.MeshPath} ({SelectedCount()}/{mesh.Bones.Count})";
            }

            void RefreshSection()
            {
                UpdateTitle();
                meshEnabled.SetValueWithoutNotify(controller.IsAdvancedMeshEnabled(mesh));
                meshControls.SetEnabled(controller.IsAdvancedMeshEnabled(mesh));
                foreach (var update in toggleRefreshers)
                {
                    update();
                }
            }

            sectionRefreshers.Add(RefreshSection);

            void BuildContent()
            {
                if (built)
                {
                    return;
                }

                built = true;
                meshControls.Add(CreateMeshSectionButtonRow(
                    () =>
                    {
                        controller.SetAdvancedMeshDataEnabled(mesh, true);
                        refresh?.Invoke();
                    },
                    () =>
                    {
                        controller.SetAdvancedMeshDataEnabled(mesh, false);
                        refresh?.Invoke();
                    },
                    () =>
                    {
                        controller.RestoreAdvancedMesh(mesh);
                        refresh?.Invoke();
                    },
                    () =>
                    {
                        controller.DisableChangedAdvancedMesh(mesh);
                        refresh?.Invoke();
                    }));
                meshControls.Add(CreateAdvancedPairTable(
                    controller,
                    comparison,
                    mesh,
                    toggleRefreshers,
                    refresh));
            }

            foldout.RegisterValueChangedCallback(evt =>
            {
                m_MeshFoldouts[key] = evt.newValue;
                if (evt.newValue)
                {
                    BuildContent();
                }
            });
            if (foldout.value)
            {
                BuildContent();
            }

            RefreshSection();
            return new StickyHeaderSection(
                foldout,
                foldout,
                stickyContext => CreateAdvancedMeshStickyHeader(
                    stickyContext,
                    controller,
                    comparison,
                    mesh,
                    foldout.text));
        }

        private static VisualElement CreateAdvancedMeshStickyHeader(
            StickyHeaderContext context,
            SkinWeightSelectionController controller,
            SkinWeightFbxComparison comparison,
            SkinWeightMeshComparison mesh,
            string title)
        {
            var pairs = mesh?.Bones ?? comparison.Meshes.SelectMany(item => item.Bones).ToArray();
            int enabledMeshCount = mesh == null
                ? comparison.Meshes.Count(controller.IsAdvancedMeshEnabled)
                : controller.IsAdvancedMeshEnabled(mesh) ? 1 : 0;
            int meshCount = mesh == null ? comparison.Meshes.Count : 1;
            var bar = new StickyHeaderBar();
            bar.SetRightContentPadding(k_AdvancedTableHorizontalPadding);
            bar.name = mesh == null
                ? "skin-weight-sticky:meshes"
                : "skin-weight-sticky:mesh";
            bar.Leading.Add(StickyHeaderControls.CreatePrimaryToggle(
                StickyHeaderToggleState.FromCounts(enabledMeshCount, meshCount),
                value => context.ApplyChange(() =>
                {
                    if (mesh == null)
                    {
                        controller.SetAdvancedMeshesEnabled(value);
                    }
                    else
                    {
                        controller.SetAdvancedMeshEnabled(mesh, value);
                    }
                }),
                "skin-weight-sticky-toggle:section"));
            var titleButton = StickyHeaderControls.CreateTitleButton(
                mesh == null
                    ? $"{title} ({enabledMeshCount}/{meshCount})"
                    : title,
                context.ScrollToSection);
            titleButton.name = "skin-weight-sticky-title";
            bar.Title.Add(titleButton);
            AddSkinWeightStickyActions(
                bar,
                context,
                () =>
                {
                    if (mesh == null)
                    {
                        controller.RestoreAdvancedMeshes();
                    }
                    else
                    {
                        controller.RestoreAdvancedMesh(mesh);
                    }
                },
                () =>
                {
                    if (mesh == null)
                    {
                        controller.DisableChangedAdvancedMeshes();
                    }
                    else
                    {
                        controller.DisableChangedAdvancedMesh(mesh);
                    }
                });
            bar.Trailing.Add(StickyHeaderControls.CreateToggleCell(
                Localization.S("features.skin-weights.weights"),
                StickyHeaderToggleState.FromCounts(
                    pairs.Count(pair => pair.Weight != null && controller.IsWeightSelected(pair)),
                    pairs.Count(pair => pair.Weight != null),
                    mesh == null || controller.IsAdvancedMeshEnabled(mesh)),
                value => context.ApplyChange(() =>
                {
                    if (mesh == null)
                    {
                        controller.SetAdvancedWeightsEnabled(value);
                    }
                    else
                    {
                        controller.SetAdvancedMeshWeightsEnabled(mesh, value);
                    }
                }),
                "skin-weight-sticky-toggle:weights",
                k_AdvancedFeatureColumnWidth));
            bar.Trailing.Add(StickyHeaderControls.CreateToggleCell(
                Localization.S("features.skin-weights.bindposes"),
                StickyHeaderToggleState.FromCounts(
                    pairs.Count(pair => pair.Bindpose?.SourceExists == true && controller.IsBindposeSelected(pair)),
                    pairs.Count(pair => pair.Bindpose?.SourceExists == true),
                    mesh == null || controller.IsAdvancedMeshEnabled(mesh)),
                value => context.ApplyChange(() =>
                {
                    if (mesh == null)
                    {
                        controller.SetAdvancedBindposesEnabled(value);
                    }
                    else
                    {
                        controller.SetAdvancedMeshBindposesEnabled(mesh, value);
                    }
                }),
                "skin-weight-sticky-toggle:bindposes",
                k_AdvancedFeatureColumnWidth));
            return bar;
        }

        private static VisualElement CreateArmatureStickyHeader(
            StickyHeaderContext context,
            SkinWeightSelectionController controller,
            SkinWeightFbxComparison comparison)
        {
            var bones = comparison.Bones.Where(bone => bone.SourceBoneExists).ToArray();
            var bar = new StickyHeaderBar
            {
                name = "skin-weight-sticky:armature"
            };
            bar.SetRightContentPadding(k_AdvancedTableHorizontalPadding);
            var titleButton = StickyHeaderControls.CreateTitleButton(
                $"{Localization.S("features.skin-weights.armature")} " +
                $"({bones.Count(bone => controller.IsTransformSelected(bone.BonePath))}/{bones.Length})",
                context.ScrollToSection);
            titleButton.name = "skin-weight-sticky-title";
            bar.Title.Add(titleButton);
            AddSkinWeightStickyActions(
                bar,
                context,
                controller.RestoreAdvancedTransforms,
                controller.DisableChangedAdvancedTransforms);

            var statusSpacer = new VisualElement();
            statusSpacer.style.width = k_AdvancedFeatureColumnWidth;
            statusSpacer.style.flexShrink = 0f;
            bar.Trailing.Add(statusSpacer);
            bar.Trailing.Add(StickyHeaderControls.CreateToggleCell(
                Localization.S("features.skin-weights.bone_transform"),
                StickyHeaderToggleState.FromCounts(
                    bones.Count(bone => controller.IsTransformSelected(bone.BonePath)),
                    bones.Length),
                value => context.ApplyChange(() => controller.SetAdvancedTransformsEnabled(value)),
                "skin-weight-sticky-toggle:transforms",
                k_AdvancedFeatureColumnWidth));
            return bar;
        }

        private static void AddSkinWeightStickyActions(
            StickyHeaderBar bar,
            StickyHeaderContext context,
            Action restore,
            Action disableChanged)
        {
            var auto = StickyHeaderControls.CreateActionButton(
                Localization.S("common.auto"),
                () => context.ApplyChange(restore));
            auto.style.marginRight = 0f;
            bar.Actions.Add(auto);
            var disable = StickyHeaderControls.CreateActionButton(
                Localization.S("common.disable_diff"),
                () => context.ApplyChange(disableChanged));
            disable.style.marginRight = 4f;
            bar.Actions.Add(disable);
        }

        private VisualElement CreateAdvancedPairTable(
            SkinWeightSelectionController controller,
            SkinWeightFbxComparison comparison,
            SkinWeightMeshComparison mesh,
            List<Action> toggleRefreshers,
            Action refresh)
        {
            var table = CreateTable();
            table.Add(CreateAdvancedHeader());
            for (int i = 0; i < mesh.Bones.Count; i++)
            {
                var pair = mesh.Bones[i];
                var row = CreateTableRow(i);
                row.Add(CreateAdvancedBoneCell(comparison, pair));
                row.Add(CreateFeatureToggle(
                    pair.Weight?.Status,
                    pair.Weight != null,
                    controller,
                    pair,
                    true,
                    toggleRefreshers,
                    refresh));
                row.Add(CreateFeatureToggle(
                    pair.Bindpose?.Status,
                    pair.Bindpose?.SourceExists == true,
                    controller,
                    pair,
                    false,
                    toggleRefreshers,
                    refresh));
                table.Add(row);
            }

            return table;
        }

        private StickyHeaderSection CreateAdvancedTransformSection(
            SkinWeightSelectionController controller,
            SkinWeightFbxComparison comparison,
            List<Action> sectionRefreshers,
            Action refresh)
        {
            var bones = comparison.Bones
                .Where(bone => bone.SourceBoneExists)
                .OrderBy(bone => bone.BonePath, StringComparer.Ordinal)
                .ToArray();
            var root = new VisualElement();
            root.style.marginTop = 8f;
            root.style.marginBottom = 6f;

            int SelectedCount() => bones.Count(bone => controller.IsTransformSelected(bone.BonePath));
            var header = CreateAdvancedGroupHeader(
                "armature",
                Localization.S("features.skin-weights.armature"),
                SelectedCount,
                bones.Length,
                value =>
                {
                    controller.SetAdvancedTransformsEnabled(value);
                    refresh?.Invoke();
                },
                Localization.S("features.skin-weights.bone_transform"));
            root.Add(header.Root);

            var search = new ToolbarSearchField { value = m_AdvancedBoneSearch };
            search.style.flexGrow = 0f;
            search.style.flexShrink = 0f;
            search.style.width = Length.Percent(100f);
            search.tooltip = Localization.S("features.skin-weights.search_bones.tooltip");
            root.Add(search);

            var table = CreateTable();
            root.Add(table);
            var toggleRefreshers = new List<Action>();

            void RebuildRows()
            {
                table.Clear();
                table.Add(CreateArmatureHeader());
                toggleRefreshers.Clear();
                var visible = bones
                    .Where(bone => MatchesSearch(bone.BonePath, m_AdvancedBoneSearch))
                    .ToArray();
                for (int index = 0; index < visible.Length; index++)
                {
                    var bone = visible[index];
                    var row = CreateTableRow(index);
                    var name = CreateNameLabel(GetBoneName(bone.BonePath));
                    name.tooltip = bone.BonePath;
                    row.Add(name);
                    row.Add(CreateArmatureStatusCell(bone));
                    row.Add(CreateAdvancedTransformToggle(
                        controller,
                        bone,
                        toggleRefreshers,
                        refresh));
                    table.Add(row);
                }
            }

            search.RegisterValueChangedCallback(evt =>
            {
                m_AdvancedBoneSearch = evt.newValue ?? string.Empty;
                RebuildRows();
            });
            void RefreshSection()
            {
                header.Refresh();
                foreach (var update in toggleRefreshers)
                {
                    update();
                }
            }

            sectionRefreshers.Add(RefreshSection);
            RebuildRows();
            header.Refresh();

            return new StickyHeaderSection(
                root,
                header.Root,
                stickyContext => CreateArmatureStickyHeader(stickyContext, controller, comparison));
        }

        private static RefreshableElement CreateAdvancedGroupHeader(
            string groupName,
            string title,
            Func<int> selectedCount,
            int totalCount,
            Action<bool> changed,
            string toggleLabel = null)
        {
            var header = new StickyHeaderBar
            {
                name = $"skin-weight-group-header:{groupName}"
            };
            header.style.marginBottom = 4f;

            var label = new Label();
            BlendShareInspectorUi.StyleStrong(label);
            label.style.flexGrow = 1f;
            header.Title.Add(label);

            if (!string.IsNullOrEmpty(toggleLabel))
            {
                var actionLabel = new Label(toggleLabel);
                actionLabel.style.marginRight = 6f;
                header.Trailing.Add(actionLabel);
            }

            var toggle = new Toggle
            {
                name = $"skin-weight-group-toggle:{groupName}"
            };
            toggle.style.width = 22f;
            toggle.style.marginLeft = 6f;
            toggle.style.marginRight = 0f;
            toggle.tooltip = Localization.S("features.skin-weights.group_toggle.tooltip");
            header.Trailing.Add(toggle);

            void Refresh()
            {
                int selected = selectedCount?.Invoke() ?? 0;
                label.text = $"{title} ({selected}/{totalCount})";
                toggle.showMixedValue = selected > 0 && selected < totalCount;
                toggle.SetValueWithoutNotify(totalCount > 0 && selected == totalCount);
            }

            toggle.RegisterValueChangedCallback(evt => changed?.Invoke(evt.newValue));
            Refresh();
            return new RefreshableElement(header, Refresh);
        }

        private static VisualElement CreateCommandBar(
            SkinWeightSelectionController controller,
            Action refresh)
        {
            var bar = new StickyHeaderBar();
            bar.Actions.Add(CreateCommandButton(Localization.S("common.select_all"), () =>
            {
                controller.SelectAll();
                refresh?.Invoke();
            }));
            bar.Actions.Add(CreateCommandButton(Localization.S("common.clear_all"), () =>
            {
                controller.ClearAll();
                refresh?.Invoke();
            }));
            bar.Actions.Add(CreateCommandButton(Localization.S("common.auto"), () =>
            {
                controller.RestoreAutomaticSelection();
                refresh?.Invoke();
            }));
            bar.Actions.Add(CreateCommandButton(Localization.S("common.disable_diff"), () =>
            {
                controller.DisableChanged();
                refresh?.Invoke();
            }));
            return bar;
        }

        private static VisualElement CreateMeshSectionButtonRow(
            Action enableAll,
            Action disableAll,
            Action restore,
            Action disableChanged)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.marginTop = 4f;
            row.style.marginBottom = 4f;
            row.Add(CreateCommandButton(Localization.S("common.enable_all"), enableAll));
            row.Add(CreateCommandButton(Localization.S("common.disable_all"), disableAll));
            row.Add(CreateCommandButton(Localization.S("common.auto"), restore));
            row.Add(CreateCommandButton(Localization.S("common.disable_diff"), disableChanged));
            return row;
        }

        private static Button CreateCommandButton(string text, Action clicked)
        {
            var button = new Button(clicked) { text = text };
            button.style.marginRight = 4f;
            return button;
        }

        private static VisualElement CreateSelectionRow(
            string title,
            string summary,
            bool selected,
            Action<bool> changed,
            bool enabled = true,
            string tooltip = null,
            string badge = null)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.height = k_SimpleRowHeight;
            row.style.minHeight = k_SimpleRowHeight;
            row.style.maxHeight = k_SimpleRowHeight;
            row.style.flexGrow = 1f;
            row.style.paddingLeft = k_AdvancedTableHorizontalPadding;
            row.style.paddingRight = k_AdvancedTableHorizontalPadding;

            var toggle = new Toggle { value = selected };
            toggle.style.width = 22f;
            toggle.style.alignSelf = Align.Center;
            toggle.style.marginTop = 0f;
            toggle.style.marginBottom = 0f;
            toggle.SetEnabled(enabled);
            toggle.RegisterValueChangedCallback(evt => changed?.Invoke(evt.newValue));
            row.Add(toggle);

            var label = new Label(title);
            label.style.flexGrow = 1f;
            label.style.overflow = Overflow.Hidden;
            label.style.unityTextOverflowPosition = TextOverflowPosition.End;
            label.style.alignSelf = Align.Center;
            label.tooltip = tooltip ?? title;
            row.Add(label);

            if (!string.IsNullOrEmpty(badge))
            {
                row.Add(CreateTextPill(badge));
            }

            var status = new Label(summary);
            status.style.flexShrink = 0f;
            status.style.alignSelf = Align.Center;
            status.style.unityTextAlign = TextAnchor.MiddleRight;
            row.Add(status);
            return row;
        }

        private static VisualElement CreateSimpleListItem()
        {
            var item = new VisualElement();
            item.style.height = k_SimpleRowHeight;
            item.style.minHeight = k_SimpleRowHeight;
            item.style.maxHeight = k_SimpleRowHeight;
            item.style.flexDirection = FlexDirection.Row;
            item.style.alignItems = Align.Center;
            item.style.justifyContent = Justify.Center;
            item.style.paddingTop = 0f;
            item.style.paddingBottom = 0f;
            item.style.marginTop = 0f;
            item.style.marginBottom = 0f;
            return item;
        }

        private static VisualElement CreateFeatureToggle(
            SkinWeightComparisonStatus? status,
            bool available,
            SkinWeightSelectionController controller,
            SkinWeightMeshBoneComparison pair,
            bool weight,
            List<Action> toggleRefreshers,
            Action refresh)
        {
            var cell = CreateFeatureCell(status);
            var toggle = new Toggle
            {
                value = weight
                    ? controller.IsWeightSelected(pair)
                    : controller.IsBindposeSelected(pair)
            };
            toggle.SetEnabled(available);
            toggleRefreshers.Add(() =>
                toggle.SetValueWithoutNotify(weight
                    ? controller.IsWeightSelected(pair)
                    : controller.IsBindposeSelected(pair)));
            toggle.RegisterValueChangedCallback(evt =>
            {
                if (weight)
                {
                    controller.SetAdvancedWeightSelected(pair, evt.newValue);
                }
                else
                {
                    controller.SetAdvancedBindposeSelected(pair, evt.newValue);
                }

                refresh?.Invoke();
            });
            cell.Add(toggle);
            return cell;
        }

        private static VisualElement CreateAdvancedHeader()
        {
            var row = CreateTableRow(-1);
            row.Add(CreateHeaderCell(Localization.S("features.skin-weights.bones"), true));
            row.Add(CreateHeaderCell(Localization.S("features.skin-weights.weights"), false));
            row.Add(CreateHeaderCell(Localization.S("features.skin-weights.bindposes"), false));
            return row;
        }

        private static VisualElement CreateArmatureHeader()
        {
            var row = CreateTableRow(-1);
            row.Add(CreateHeaderCell(Localization.S("features.skin-weights.bones"), true));
            row.Add(CreateHeaderCell(Localization.S("common.status"), false));
            row.Add(CreateHeaderCell(Localization.S("features.skin-weights.transforms"), false));
            return row;
        }

        private static VisualElement CreateArmatureStatusCell(SkinWeightBoneComparison bone)
        {
            var cell = CreateFeatureCell(null);
            if (bone.RequiresCreateBone)
            {
                cell.Add(CreateTextPill(Localization.S("features.skin-weights.status.new_bone")));
            }
            else if (bone.Transform != null)
            {
                cell.Add(CreateStatusPill(bone.Transform.Status));
            }

            return cell;
        }

        private static VisualElement CreateAdvancedTransformToggle(
            SkinWeightSelectionController controller,
            SkinWeightBoneComparison bone,
            List<Action> toggleRefreshers,
            Action refresh)
        {
            var cell = CreateFeatureCell(null);
            var toggle = new Toggle
            {
                name = $"skin-weight-transform-toggle:{bone.BonePath}"
            };
            void RefreshToggle()
            {
                bool forced = controller.IsTransformForced(bone.BonePath);
                toggle.SetValueWithoutNotify(controller.IsTransformSelected(bone.BonePath));
                toggle.SetEnabled(!forced);
                toggle.tooltip = forced
                    ? Localization.S("features.skin-weights.transform_forced.tooltip")
                    : string.Empty;
            }

            toggle.RegisterValueChangedCallback(evt =>
            {
                controller.SetAdvancedTransformSelected(bone.BonePath, evt.newValue);
                refresh?.Invoke();
            });
            toggleRefreshers.Add(RefreshToggle);
            RefreshToggle();
            cell.Add(toggle);
            return cell;
        }

        private static Label CreateHeaderCell(string text, bool grow)
        {
            var label = new Label(text);
            BlendShareInspectorUi.StyleStrong(label);
            if (grow)
            {
                label.style.flexGrow = 1f;
                label.style.minWidth = 140f;
            }
            else
            {
                label.style.width = k_AdvancedFeatureColumnWidth;
                label.style.unityTextAlign = TextAnchor.MiddleRight;
            }

            return label;
        }

        private static VisualElement CreateFeatureCell(SkinWeightComparisonStatus? status)
        {
            var cell = new VisualElement();
            cell.style.width = k_AdvancedFeatureColumnWidth;
            cell.style.flexShrink = 0f;
            cell.style.flexDirection = FlexDirection.Row;
            cell.style.alignItems = Align.Center;
            cell.style.justifyContent = Justify.FlexEnd;
            if (status.HasValue && status != SkinWeightComparisonStatus.Same)
            {
                cell.Add(CreateStatusPill(status.Value));
            }

            return cell;
        }

        private static VisualElement CreateModeRoot()
        {
            var root = new VisualElement();
            root.style.flexGrow = 1f;
            root.style.minHeight = 0f;
            root.style.flexDirection = FlexDirection.Column;
            return root;
        }

        private static VisualElement CreateListPanel(string title, VisualElement titleAction = null)
        {
            var panel = CreateModeRoot();
            panel.style.minHeight = k_SimpleListPanelMinHeight;
            panel.style.overflow = Overflow.Hidden;
            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            var label = new Label(title);
            BlendShareInspectorUi.StyleStrong(label);
            label.style.flexGrow = 1f;
            header.Add(label);
            if (titleAction != null)
            {
                header.Add(titleAction);
            }

            panel.Add(header);
            return panel;
        }

        private static VisualElement CreateTable()
        {
            var table = new VisualElement();
            table.style.flexDirection = FlexDirection.Column;
            table.style.marginTop = 2f;
            table.style.marginBottom = 5f;
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

        private static VisualElement CreateTableRow(int rowIndex)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.minHeight = 24f;
            row.style.paddingLeft = 4f;
            row.style.paddingRight = 4f;
            if (rowIndex < 0)
            {
                row.style.backgroundColor = new Color(0.23f, 0.23f, 0.23f);
            }
            else
            {
                row.style.backgroundColor = rowIndex % 2 == 1
                    ? new Color(0.22f, 0.22f, 0.22f)
                    : new Color(0.25f, 0.25f, 0.25f);
            }

            return row;
        }

        private static Label CreateNameLabel(string text)
        {
            var label = new Label(text);
            label.style.flexGrow = 1f;
            label.style.minWidth = 140f;
            label.style.overflow = Overflow.Hidden;
            label.style.unityTextOverflowPosition = TextOverflowPosition.End;
            return label;
        }

        private static VisualElement CreateAdvancedBoneCell(
            SkinWeightFbxComparison comparison,
            SkinWeightMeshBoneComparison pair)
        {
            var cell = new VisualElement();
            cell.style.flexGrow = 1f;
            cell.style.minWidth = 140f;
            cell.style.flexDirection = FlexDirection.Row;
            cell.style.alignItems = Align.Center;

            var name = CreateNameLabel(GetBoneName(pair.BonePath));
            name.tooltip = pair.BonePath;
            cell.Add(name);
            if (comparison.TryGetBone(pair.BonePath, out var bone) && bone.RequiresCreateBone)
            {
                cell.Add(CreateTextPill(Localization.S("features.skin-weights.status.new_bone")));
            }

            return cell;
        }

        private static Label CreateStatusPill(SkinWeightComparisonStatus status)
        {
            var label = new Label(GetStatusText(status));
            label.style.width = k_StatusSlotWidth;
            label.style.flexShrink = 0f;
            label.style.alignSelf = Align.Center;
            label.style.marginRight = 6f;
            label.style.unityTextAlign = TextAnchor.MiddleRight;
            return label;
        }

        private static Label CreateTextPill(string text)
        {
            var label = new Label(text);
            label.style.flexShrink = 0f;
            label.style.marginLeft = 6f;
            label.style.marginRight = 8f;
            label.style.paddingLeft = 6f;
            label.style.paddingRight = 6f;
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
            label.style.backgroundColor = new Color(0.20f, 0.36f, 0.22f);
            label.style.borderBottomColor = new Color(0.27f, 0.48f, 0.30f);
            label.style.borderTopColor = new Color(0.27f, 0.48f, 0.30f);
            label.style.borderLeftColor = new Color(0.27f, 0.48f, 0.30f);
            label.style.borderRightColor = new Color(0.27f, 0.48f, 0.30f);
            label.style.color = new Color(0.90f, 0.90f, 0.90f);
            return label;
        }

        private static void AddDiagnostics(VisualElement root, SkinWeightFbxComparison comparison)
        {
            foreach (string diagnostic in comparison.Diagnostics ?? Array.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(diagnostic))
                {
                    root.Add(new HelpBox(diagnostic, HelpBoxMessageType.Warning));
                }
            }
        }

        private static bool MatchesSearch(string value, string search)
        {
            return string.IsNullOrWhiteSpace(search) ||
                   (value ?? string.Empty).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string FormatMeshSummary(SkinWeightMeshComparison mesh)
        {
            int changed = mesh.Bones.Count(pair =>
                IsNewOrChanged(pair.Weight?.Status) || IsNewOrChanged(pair.Bindpose?.Status));
            return Localization.SF("features.skin-weights.summary.changed_bones", changed, mesh.Bones.Count);
        }

        private static string FormatBoneSummary(SkinWeightBoneComparison bone)
        {
            int changed = bone.Meshes.Count(pair =>
                IsNewOrChanged(pair.Weight?.Status) || IsNewOrChanged(pair.Bindpose?.Status));
            return Localization.SF("features.skin-weights.summary.changed_meshes", changed, bone.Meshes.Count);
        }

        private static bool IsPairSelected(
            SkinWeightSelectionController controller,
            SkinWeightMeshBoneComparison pair)
        {
            return controller.IsWeightSelected(pair) || controller.IsBindposeSelected(pair);
        }

        private static int CountSelectedPairs(
            SkinWeightSelectionController controller,
            SkinWeightMeshComparison mesh)
        {
            return mesh.Bones.Count(pair => IsPairSelected(controller, pair));
        }

        private static bool IsNewOrChanged(SkinWeightComparisonStatus? status)
        {
            return status == SkinWeightComparisonStatus.New ||
                   status == SkinWeightComparisonStatus.Changed;
        }

        private static string GetStatusText(SkinWeightComparisonStatus status)
        {
            return status switch
            {
                SkinWeightComparisonStatus.New => Localization.S("common.status.new"),
                SkinWeightComparisonStatus.Changed => Localization.S("common.status.diff"),
                SkinWeightComparisonStatus.Same => Localization.S("common.status.same"),
                SkinWeightComparisonStatus.MissingSource => Localization.S("common.status.missing_source"),
                SkinWeightComparisonStatus.Unavailable => Localization.S("common.status.unavailable"),
                _ => Localization.S("common.status.unknown")
            };
        }

        private static string NormalizePathKey(string path)
        {
            return FbxNameUtility.NormalizePath(path ?? string.Empty);
        }

        private static string GetParentPath(string path)
        {
            string normalized = NormalizePathKey(path);
            if (string.IsNullOrEmpty(normalized))
            {
                return string.Empty;
            }

            int slash = normalized.LastIndexOf('/');
            return slash > 0 ? normalized.Substring(0, slash) : string.Empty;
        }

        private static string GetBoneName(string path)
        {
            string normalized = NormalizePathKey(path);
            if (string.IsNullOrEmpty(normalized))
            {
                return string.Empty;
            }

            int slash = normalized.LastIndexOf('/');
            return slash >= 0 && slash + 1 < normalized.Length
                ? normalized.Substring(slash + 1)
                : normalized;
        }

        private sealed class RefreshableElement
        {
            private readonly Action m_Refresh;

            public VisualElement Root { get; }

            public RefreshableElement(VisualElement root, Action refresh)
            {
                Root = root;
                m_Refresh = refresh;
            }

            public void Refresh()
            {
                m_Refresh?.Invoke();
            }
        }

        private sealed class SkinWeightSelectionState
        {
            private readonly Dictionary<string, HashSet<string>> m_SelectedWeightBonePathsByMesh =
                new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            private readonly Dictionary<string, HashSet<string>> m_SelectedBindposeBonePathsByMesh =
                new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            private readonly HashSet<string> m_SelectedTransformBonePaths = new HashSet<string>(StringComparer.Ordinal);
            private readonly HashSet<string> m_ForcedTransformBonePaths = new HashSet<string>(StringComparer.Ordinal);
            private readonly HashSet<string> m_SelectedNewBonePaths = new HashSet<string>(StringComparer.Ordinal);
            private readonly HashSet<string> m_SelectedSimpleMeshKeys = new HashSet<string>(StringComparer.Ordinal);
            private readonly HashSet<string> m_SelectedSimpleBonePaths = new HashSet<string>(StringComparer.Ordinal);
            private readonly HashSet<string> m_DisabledMeshKeys = new HashSet<string>(StringComparer.Ordinal);
            private string m_ArmatureOnlyHostMeshKey = string.Empty;

            public SkinWeightSelectionMode SelectionMode { get; set; } = SkinWeightSelectionMode.Simple;
            public bool DefaultsInitialized { get; set; }

            public bool IsMeshEnabled(string meshPath)
            {
                return !m_DisabledMeshKeys.Contains(NormalizePathKey(meshPath));
            }

            public void SetMeshEnabled(string meshPath, bool enabled)
            {
                SetSelection(m_DisabledMeshKeys, NormalizePathKey(meshPath), !enabled);
            }

            public void ClearMeshEnabledOverrides()
            {
                m_DisabledMeshKeys.Clear();
            }

            public bool IsWeightClusterSelected(string meshPath, string bonePath)
            {
                return TryGetSelectedSet(m_SelectedWeightBonePathsByMesh, meshPath, out var selected) &&
                       selected.Contains(NormalizePathKey(bonePath));
            }

            public void SetWeightClusterSelected(string meshPath, string bonePath, bool selected)
            {
                SetMeshBoneSelection(m_SelectedWeightBonePathsByMesh, meshPath, bonePath, selected);
            }

            public bool IsBindposeSelected(string meshPath, string bonePath)
            {
                return TryGetSelectedSet(m_SelectedBindposeBonePathsByMesh, meshPath, out var selected) &&
                       selected.Contains(NormalizePathKey(bonePath));
            }

            public void SetBindposeSelected(string meshPath, string bonePath, bool selected)
            {
                SetMeshBoneSelection(m_SelectedBindposeBonePathsByMesh, meshPath, bonePath, selected);
            }

            public bool IsBoneTransformSelected(string bonePath)
            {
                string normalized = NormalizePathKey(bonePath);
                return m_SelectedTransformBonePaths.Contains(normalized) ||
                       m_ForcedTransformBonePaths.Contains(normalized);
            }

            public void SetBoneTransformSelected(string bonePath, bool selected)
            {
                SetSelection(m_SelectedTransformBonePaths, NormalizePathKey(bonePath), selected);
            }

            public IReadOnlyCollection<string> GetSelectedNewBonePaths()
            {
                return m_SelectedNewBonePaths.ToArray();
            }

            public void SetNewBoneSelected(string bonePath, bool selected)
            {
                SetSelection(m_SelectedNewBonePaths, NormalizePathKey(bonePath), selected);
            }

            public bool IsSimpleMeshSelected(string meshPath)
            {
                return m_SelectedSimpleMeshKeys.Contains(NormalizePathKey(meshPath));
            }

            public void SetSimpleMeshSelected(string meshPath, bool selected)
            {
                SetSelection(m_SelectedSimpleMeshKeys, NormalizePathKey(meshPath), selected);
            }

            public IReadOnlyCollection<string> GetSelectedSimpleMeshKeys()
            {
                return m_SelectedSimpleMeshKeys.ToArray();
            }

            public bool IsSimpleBoneSelected(string bonePath)
            {
                return m_SelectedSimpleBonePaths.Contains(NormalizePathKey(bonePath));
            }

            public void SetSimpleBoneSelected(string bonePath, bool selected)
            {
                SetSelection(m_SelectedSimpleBonePaths, NormalizePathKey(bonePath), selected);
            }

            public IReadOnlyCollection<string> GetSelectedSimpleBonePaths()
            {
                return m_SelectedSimpleBonePaths.ToArray();
            }

            public void ClearSimpleAxes()
            {
                m_SelectedSimpleMeshKeys.Clear();
                m_SelectedSimpleBonePaths.Clear();
            }

            public void ClearMeshSelections()
            {
                m_SelectedWeightBonePathsByMesh.Clear();
                m_SelectedBindposeBonePathsByMesh.Clear();
            }

            public void ClearArmatureSelections()
            {
                m_SelectedTransformBonePaths.Clear();
                m_ForcedTransformBonePaths.Clear();
                m_SelectedNewBonePaths.Clear();
                m_ArmatureOnlyHostMeshKey = string.Empty;
            }

            public bool IsBoneTransformForced(string bonePath)
            {
                return m_ForcedTransformBonePaths.Contains(NormalizePathKey(bonePath));
            }

            public void SetBoneTransformForced(string bonePath, bool selected)
            {
                SetSelection(m_ForcedTransformBonePaths, NormalizePathKey(bonePath), selected);
            }

            public IReadOnlyCollection<string> GetExplicitBoneTransformPaths()
            {
                return m_SelectedTransformBonePaths.ToArray();
            }

            public void ClearExplicitBoneTransformSelections()
            {
                m_SelectedTransformBonePaths.Clear();
            }

            public void ClearAutomaticArmatureSelections()
            {
                m_ForcedTransformBonePaths.Clear();
                m_SelectedNewBonePaths.Clear();
                m_ArmatureOnlyHostMeshKey = string.Empty;
            }

            public bool HasMeshScopedSelections()
            {
                return m_SelectedWeightBonePathsByMesh
                           .Any(pair => !m_DisabledMeshKeys.Contains(pair.Key) && pair.Value.Count > 0) ||
                       m_SelectedBindposeBonePathsByMesh
                           .Any(pair => !m_DisabledMeshKeys.Contains(pair.Key) && pair.Value.Count > 0);
            }

            public IReadOnlyCollection<string> GetSelectedBoneTransformPaths()
            {
                var combined = new HashSet<string>(m_SelectedTransformBonePaths, StringComparer.Ordinal);
                combined.UnionWith(m_ForcedTransformBonePaths);
                return combined.OrderBy(path => path, StringComparer.Ordinal).ToArray();
            }

            public void SetArmatureOnlyHostMesh(string meshPath)
            {
                m_ArmatureOnlyHostMeshKey = NormalizePathKey(meshPath);
            }

            public IEnumerable<(string MeshKey, string BonePath)> GetSelectedMeshBonePairs()
            {
                return m_SelectedWeightBonePathsByMesh
                    .SelectMany(pair => pair.Value.Select(path => (pair.Key, path)))
                    .Concat(m_SelectedBindposeBonePathsByMesh
                        .SelectMany(pair => pair.Value.Select(path => (pair.Key, path))))
                    .Distinct();
            }

            public void EnsureRequiredParentSelections(SkinWeightFbxComparison comparison)
            {
                if (comparison == null)
                {
                    return;
                }

                foreach (string path in m_SelectedNewBonePaths.ToArray())
                {
                    string parent = GetParentPath(path);
                    while (!string.IsNullOrEmpty(parent))
                    {
                        if (comparison.TryGetBone(parent, out var parentBone) &&
                            parentBone.RequiresCreateBone)
                        {
                            m_SelectedNewBonePaths.Add(parent);
                            m_ForcedTransformBonePaths.Add(parent);
                        }

                        parent = GetParentPath(parent);
                    }
                }
            }

            public void SyncToExtractionOptions(
                SkinWeightExtractionOptions target,
                SkinWeightFbxComparison comparison)
            {
                if (target == null)
                {
                    return;
                }

                target.ExtractAllClustersWhenSelectionEmpty = false;

                var allMeshKeys = new HashSet<string>(StringComparer.Ordinal);
                if (comparison?.Meshes != null)
                {
                    for (int i = 0; i < comparison.Meshes.Count; i++)
                    {
                        string key = NormalizePathKey(comparison.Meshes[i]?.MeshPath);
                        if (!string.IsNullOrEmpty(key))
                        {
                            allMeshKeys.Add(key);
                        }
                    }
                }

                foreach (string key in m_SelectedWeightBonePathsByMesh.Keys)
                {
                    allMeshKeys.Add(key);
                }

                foreach (string key in m_SelectedBindposeBonePathsByMesh.Keys)
                {
                    allMeshKeys.Add(key);
                }

                foreach (string key in m_DisabledMeshKeys)
                {
                    allMeshKeys.Add(key);
                }

                foreach (string meshKey in allMeshKeys)
                {
                    bool meshEnabled = !m_DisabledMeshKeys.Contains(meshKey);
                    target.SetMeshEnabled(meshKey, meshEnabled);

                    IEnumerable<string> weights = m_SelectedWeightBonePathsByMesh.TryGetValue(meshKey, out var weightSet)
                        ? weightSet.OrderBy(p => p, StringComparer.Ordinal)
                        : Enumerable.Empty<string>();
                    IEnumerable<string> bindposes = m_SelectedBindposeBonePathsByMesh.TryGetValue(meshKey, out var bindposeSet)
                        ? bindposeSet.OrderBy(p => p, StringComparer.Ordinal)
                        : Enumerable.Empty<string>();

                    target.SetSelectedWeightBonePaths(meshKey, weights);
                    target.SetSelectedBindposeBonePaths(meshKey, bindposes);
                }

                target.SetSelectedTransformBonePaths(GetSelectedBoneTransformPaths());
                target.SetSelectedNewBonePaths(m_SelectedNewBonePaths.OrderBy(p => p, StringComparer.Ordinal));
                target.ArmatureOnlyHostMeshPath = m_ArmatureOnlyHostMeshKey ?? string.Empty;
            }

            private static void SetSelection(HashSet<string> selections, string key, bool selected)
            {
                if (string.IsNullOrEmpty(key))
                {
                    return;
                }

                if (selected)
                {
                    selections.Add(key);
                }
                else
                {
                    selections.Remove(key);
                }
            }

            private static bool TryGetSelectedSet(
                Dictionary<string, HashSet<string>> selectionsByMesh,
                string meshPath,
                out HashSet<string> selected)
            {
                return selectionsByMesh.TryGetValue(NormalizePathKey(meshPath), out selected);
            }

            private static void SetMeshBoneSelection(
                Dictionary<string, HashSet<string>> selectionsByMesh,
                string meshPath,
                string bonePath,
                bool selected)
            {
                string meshKey = NormalizePathKey(meshPath);
                string normalizedBonePath = NormalizePathKey(bonePath);
                if (string.IsNullOrEmpty(meshKey) || string.IsNullOrEmpty(normalizedBonePath))
                {
                    return;
                }

                if (!selectionsByMesh.TryGetValue(meshKey, out var selectedBones))
                {
                    selectedBones = new HashSet<string>(StringComparer.Ordinal);
                    selectionsByMesh[meshKey] = selectedBones;
                }

                if (selected)
                {
                    selectedBones.Add(normalizedBonePath);
                }
                else
                {
                    selectedBones.Remove(normalizedBonePath);
                    if (selectedBones.Count == 0)
                    {
                        selectionsByMesh.Remove(meshKey);
                    }
                }
            }
        }

        private sealed class SkinWeightSelectionController
        {
            private readonly SkinWeightExtractionOptions m_ExtractionOptions;
            private readonly SkinWeightSelectionState m_State;
            private readonly SkinWeightFbxComparison m_Comparison;

            public SkinWeightSelectionMode Mode => m_State.SelectionMode;

            public SkinWeightSelectionController(
                SkinWeightExtractionOptions extractionOptions,
                SkinWeightSelectionState state,
                SkinWeightFbxComparison comparison)
            {
                m_ExtractionOptions = extractionOptions ?? throw new ArgumentNullException(nameof(extractionOptions));
                m_State = state ?? throw new ArgumentNullException(nameof(state));
                m_Comparison = comparison ?? SkinWeightFbxComparison.Empty;
            }

            public void EnsureDefaultSelection()
            {
                if (m_State.DefaultsInitialized)
                {
                    m_State.SyncToExtractionOptions(m_ExtractionOptions, m_Comparison);
                    return;
                }

                m_State.DefaultsInitialized = true;
                RestoreAutomaticSelection();
            }

            public void SwitchToAdvanced()
            {
                m_State.SelectionMode = SkinWeightSelectionMode.Advanced;
                foreach (var mesh in m_Comparison.Meshes)
                {
                    m_State.SetMeshEnabled(mesh.MeshPath, HasSelectedMeshData(mesh));
                }

                RebuildArmatureDependencies();
            }

            public int GetSimpleExpansionCount()
            {
                var selectedPairs = m_State.GetSelectedMeshBonePairs().ToArray();
                var meshKeys = new HashSet<string>(
                    selectedPairs
                        .Select(pair => pair.MeshKey)
                        .Where(meshKey => m_Comparison.Meshes.Any(mesh =>
                            NormalizePathKey(mesh.MeshPath) == meshKey &&
                            m_State.IsMeshEnabled(mesh.MeshPath))),
                    StringComparer.Ordinal);
                var bonePaths = new HashSet<string>(
                    selectedPairs.Select(pair => pair.BonePath)
                        .Concat(m_State.GetExplicitBoneTransformPaths()),
                    StringComparer.Ordinal);
                int additional = 0;
                foreach (var mesh in m_Comparison.Meshes)
                {
                    string meshKey = NormalizePathKey(mesh.MeshPath);
                    if (!meshKeys.Contains(meshKey))
                    {
                        continue;
                    }

                    foreach (var pair in mesh.Bones.Where(pair => bonePaths.Contains(pair.BonePath)))
                    {
                        if (!m_State.IsWeightClusterSelected(pair.MeshPath, pair.BonePath) &&
                            !m_State.IsBindposeSelected(pair.MeshPath, pair.BonePath))
                        {
                            additional++;
                        }
                    }
                }

                return additional;
            }

            public void SwitchToSimple()
            {
                var selectedPairs = m_State.GetSelectedMeshBonePairs().ToArray();
                m_State.ClearSimpleAxes();
                foreach (string meshKey in selectedPairs.Select(pair => pair.MeshKey).Distinct(StringComparer.Ordinal))
                {
                    var mesh = m_Comparison.Meshes.FirstOrDefault(candidate =>
                        NormalizePathKey(candidate.MeshPath) == meshKey);
                    if (mesh != null && m_State.IsMeshEnabled(mesh.MeshPath))
                    {
                        m_State.SetSimpleMeshSelected(mesh.MeshPath, true);
                    }
                }

                foreach (string bonePath in selectedPairs.Select(pair => pair.BonePath)
                             .Concat(m_State.GetExplicitBoneTransformPaths())
                             .Distinct(StringComparer.Ordinal))
                {
                    m_State.SetSimpleBoneSelected(bonePath, true);
                }

                m_State.SelectionMode = SkinWeightSelectionMode.Simple;
                ApplySimpleCartesianSelection();
            }

            public bool IsSimpleMeshSelected(string meshPath)
            {
                return m_State.IsSimpleMeshSelected(meshPath) && m_State.IsMeshEnabled(meshPath);
            }

            public void SetSimpleMeshSelected(string meshPath, bool selected)
            {
                m_State.SetSimpleMeshSelected(meshPath, selected);
                ApplySimpleCartesianSelection();
            }

            public bool IsSimpleBoneSelected(string bonePath)
            {
                return m_State.IsSimpleBoneSelected(bonePath);
            }

            public bool IsWeightSelected(SkinWeightMeshBoneComparison pair)
            {
                return pair != null && m_State.IsWeightClusterSelected(pair.MeshPath, pair.BonePath);
            }

            public bool IsBindposeSelected(SkinWeightMeshBoneComparison pair)
            {
                return pair != null && m_State.IsBindposeSelected(pair.MeshPath, pair.BonePath);
            }

            public bool IsAdvancedMeshEnabled(SkinWeightMeshComparison mesh)
            {
                return mesh != null && m_State.IsMeshEnabled(mesh.MeshPath);
            }

            public bool IsTransformSelected(string bonePath)
            {
                return m_State.IsBoneTransformSelected(bonePath);
            }

            public bool IsTransformForced(string bonePath)
            {
                return m_State.IsBoneTransformForced(bonePath);
            }

            public void SetAdvancedTransformSelected(string bonePath, bool selected)
            {
                if (IsTransformForced(bonePath))
                {
                    return;
                }

                m_State.SetBoneTransformSelected(bonePath, selected);
                RebuildArmatureDependencies();
            }

            public void SetAdvancedTransformsEnabled(bool selected)
            {
                m_State.ClearExplicitBoneTransformSelections();
                if (selected)
                {
                    foreach (var bone in m_Comparison.Bones.Where(bone => bone.SourceBoneExists))
                    {
                        m_State.SetBoneTransformSelected(bone.BonePath, true);
                    }
                }

                RebuildArmatureDependencies();
            }

            public void RestoreAdvancedTransforms()
            {
                m_State.ClearExplicitBoneTransformSelections();
                foreach (var bone in m_Comparison.Bones.Where(bone =>
                             bone.SourceBoneExists &&
                             bone.Transform?.Status == SkinWeightComparisonStatus.Changed))
                {
                    m_State.SetBoneTransformSelected(bone.BonePath, true);
                }

                RebuildArmatureDependencies();
            }

            public void DisableChangedAdvancedTransforms()
            {
                foreach (var bone in m_Comparison.Bones.Where(bone =>
                             bone.Transform?.Status == SkinWeightComparisonStatus.Changed))
                {
                    m_State.SetBoneTransformSelected(bone.BonePath, false);
                }

                RebuildArmatureDependencies();
            }

            public void SetSimpleBoneSelected(string bonePath, bool selected)
            {
                m_State.SetSimpleBoneSelected(bonePath, selected);
                ApplySimpleCartesianSelection();
            }

            public void SetAdvancedWeightSelected(SkinWeightMeshBoneComparison pair, bool selected)
            {
                if (pair?.Weight == null)
                {
                    return;
                }

                m_State.SetWeightClusterSelected(pair.MeshPath, pair.BonePath, selected);
                RebuildArmatureDependencies();
            }

            public void SetAdvancedBindposeSelected(SkinWeightMeshBoneComparison pair, bool selected)
            {
                if (pair?.Bindpose == null || !pair.Bindpose.SourceExists)
                {
                    return;
                }

                m_State.SetBindposeSelected(pair.MeshPath, pair.BonePath, selected);
                RebuildArmatureDependencies();
            }

            public void SetAdvancedMeshEnabled(SkinWeightMeshComparison mesh, bool selected)
            {
                if (mesh == null)
                {
                    return;
                }

                m_State.SetMeshEnabled(mesh.MeshPath, selected);
                RebuildArmatureDependencies();
            }

            public void SetAdvancedMeshDataEnabled(SkinWeightMeshComparison mesh, bool selected)
            {
                if (mesh == null)
                {
                    return;
                }

                foreach (var pair in mesh.Bones)
                {
                    m_State.SetWeightClusterSelected(
                        pair.MeshPath,
                        pair.BonePath,
                        selected && pair.Weight != null);
                    m_State.SetBindposeSelected(
                        pair.MeshPath,
                        pair.BonePath,
                        selected && pair.Bindpose?.SourceExists == true);
                }

                RebuildArmatureDependencies();
            }

            public void SetAdvancedMeshesEnabled(bool selected)
            {
                foreach (var mesh in m_Comparison.Meshes)
                {
                    m_State.SetMeshEnabled(mesh.MeshPath, selected);
                }

                RebuildArmatureDependencies();
            }

            public void SetAdvancedWeightsEnabled(bool selected)
            {
                foreach (var pair in m_Comparison.Meshes.SelectMany(mesh => mesh.Bones))
                {
                    m_State.SetWeightClusterSelected(
                        pair.MeshPath,
                        pair.BonePath,
                        selected && pair.Weight != null);
                }

                RebuildArmatureDependencies();
            }

            public void SetAdvancedBindposesEnabled(bool selected)
            {
                foreach (var pair in m_Comparison.Meshes.SelectMany(mesh => mesh.Bones))
                {
                    m_State.SetBindposeSelected(
                        pair.MeshPath,
                        pair.BonePath,
                        selected && pair.Bindpose?.SourceExists == true);
                }

                RebuildArmatureDependencies();
            }

            public void SetAdvancedMeshWeightsEnabled(SkinWeightMeshComparison mesh, bool selected)
            {
                if (mesh == null)
                {
                    return;
                }

                foreach (var pair in mesh.Bones)
                {
                    m_State.SetWeightClusterSelected(
                        pair.MeshPath,
                        pair.BonePath,
                        selected && pair.Weight != null);
                }

                RebuildArmatureDependencies();
            }

            public void SetAdvancedMeshBindposesEnabled(SkinWeightMeshComparison mesh, bool selected)
            {
                if (mesh == null)
                {
                    return;
                }

                foreach (var pair in mesh.Bones)
                {
                    m_State.SetBindposeSelected(
                        pair.MeshPath,
                        pair.BonePath,
                        selected && pair.Bindpose?.SourceExists == true);
                }

                RebuildArmatureDependencies();
            }

            public void RestoreAdvancedMeshes()
            {
                foreach (var pair in m_Comparison.Meshes.SelectMany(mesh => mesh.Bones))
                {
                    m_State.SetWeightClusterSelected(
                        pair.MeshPath,
                        pair.BonePath,
                        ShouldAutomaticallySelectWeight(pair));
                    m_State.SetBindposeSelected(
                        pair.MeshPath,
                        pair.BonePath,
                        ShouldAutomaticallySelectBindpose(pair));
                }

                foreach (var mesh in m_Comparison.Meshes)
                {
                    m_State.SetMeshEnabled(mesh.MeshPath, HasSelectedMeshData(mesh));
                }

                RebuildArmatureDependencies();
            }

            public void DisableChangedAdvancedMeshes()
            {
                foreach (var pair in m_Comparison.Meshes.SelectMany(mesh => mesh.Bones))
                {
                    if (pair.Weight?.Status == SkinWeightComparisonStatus.Changed)
                    {
                        m_State.SetWeightClusterSelected(pair.MeshPath, pair.BonePath, false);
                    }

                    if (pair.Bindpose?.Status == SkinWeightComparisonStatus.Changed)
                    {
                        m_State.SetBindposeSelected(pair.MeshPath, pair.BonePath, false);
                    }
                }

                RebuildArmatureDependencies();
            }

            public void RestoreAdvancedMesh(SkinWeightMeshComparison mesh)
            {
                if (mesh == null)
                {
                    return;
                }

                foreach (var pair in mesh.Bones)
                {
                    bool selectWeight = ShouldAutomaticallySelectWeight(pair);
                    bool selectBindpose = ShouldAutomaticallySelectBindpose(pair);
                    m_State.SetWeightClusterSelected(pair.MeshPath, pair.BonePath, selectWeight);
                    m_State.SetBindposeSelected(pair.MeshPath, pair.BonePath, selectBindpose);
                }

                m_State.SetMeshEnabled(mesh.MeshPath, HasSelectedMeshData(mesh));
                RebuildArmatureDependencies();
            }

            public void DisableChangedAdvancedMesh(SkinWeightMeshComparison mesh)
            {
                if (mesh == null)
                {
                    return;
                }

                foreach (var pair in mesh.Bones)
                {
                    if (pair.Weight?.Status == SkinWeightComparisonStatus.Changed)
                    {
                        SetAdvancedWeightSelected(pair, false);
                    }

                    if (pair.Bindpose?.Status == SkinWeightComparisonStatus.Changed)
                    {
                        SetAdvancedBindposeSelected(pair, false);
                    }
                }

                RebuildArmatureDependencies();
            }

            public void SelectAll()
            {
                ClearAllInternal();
                if (Mode == SkinWeightSelectionMode.Simple)
                {
                    foreach (var mesh in m_Comparison.Meshes)
                    {
                        m_State.SetSimpleMeshSelected(mesh.MeshPath, true);
                    }

                    foreach (var bone in m_Comparison.RelevantBones)
                    {
                        m_State.SetSimpleBoneSelected(bone.BonePath, true);
                    }

                    ApplySimpleCartesianSelection();
                    return;
                }

                foreach (var pair in m_Comparison.Meshes.SelectMany(mesh => mesh.Bones))
                {
                    m_State.SetWeightClusterSelected(pair.MeshPath, pair.BonePath, pair.Weight != null);
                    m_State.SetBindposeSelected(
                        pair.MeshPath,
                        pair.BonePath,
                        pair.Bindpose?.SourceExists == true);
                }

                foreach (var bone in m_Comparison.Bones.Where(bone => bone.SourceBoneExists))
                {
                    m_State.SetBoneTransformSelected(bone.BonePath, true);
                }

                RebuildArmatureDependencies();
            }

            public void ClearAll()
            {
                ClearAllInternal();
                m_State.SyncToExtractionOptions(m_ExtractionOptions, m_Comparison);
            }

            public void RestoreAutomaticSelection()
            {
                ClearAllInternal();
                var automaticPairs = m_Comparison.Meshes
                    .SelectMany(mesh => mesh.Bones)
                    .Where(ShouldAutomaticallySelectPair)
                    .ToArray();
                var automaticMeshKeys = new HashSet<string>(
                    automaticPairs.Select(pair => NormalizePathKey(pair.MeshPath)),
                    StringComparer.Ordinal);
                var automaticBonePaths = new HashSet<string>(
                    automaticPairs.Select(pair => pair.BonePath),
                    StringComparer.Ordinal);

                foreach (var mesh in m_Comparison.Meshes.Where(mesh =>
                             automaticMeshKeys.Contains(NormalizePathKey(mesh.MeshPath))))
                {
                    m_State.SetSimpleMeshSelected(mesh.MeshPath, true);
                }

                foreach (string bonePath in automaticBonePaths)
                {
                    m_State.SetSimpleBoneSelected(bonePath, true);
                }

                foreach (var pair in automaticPairs)
                {
                    m_State.SetWeightClusterSelected(
                        pair.MeshPath,
                        pair.BonePath,
                        ShouldAutomaticallySelectWeight(pair));
                    m_State.SetBindposeSelected(
                        pair.MeshPath,
                        pair.BonePath,
                        ShouldAutomaticallySelectBindpose(pair));
                }

                foreach (var bone in m_Comparison.Bones.Where(bone =>
                             bone.SourceBoneExists &&
                             bone.Transform?.Status == SkinWeightComparisonStatus.Changed))
                {
                    m_State.SetSimpleBoneSelected(bone.BonePath, true);
                    m_State.SetBoneTransformSelected(bone.BonePath, true);
                }

                if (Mode == SkinWeightSelectionMode.Advanced)
                {
                    foreach (var mesh in m_Comparison.Meshes)
                    {
                        m_State.SetMeshEnabled(mesh.MeshPath, HasSelectedMeshData(mesh));
                    }
                }

                RebuildArmatureDependencies();
            }

            public void DisableChanged()
            {
                if (Mode == SkinWeightSelectionMode.Simple)
                {
                    foreach (var bone in m_Comparison.RelevantBones.Where(bone => bone.Meshes.Any(pair =>
                                 pair.Weight?.Status == SkinWeightComparisonStatus.Changed ||
                                 pair.Bindpose?.Status == SkinWeightComparisonStatus.Changed)))
                    {
                        m_State.SetSimpleBoneSelected(bone.BonePath, false);
                    }

                    ApplySimpleCartesianSelection();
                    return;
                }

                foreach (var pair in m_Comparison.Meshes.SelectMany(mesh => mesh.Bones))
                {
                    if (pair.Weight?.Status == SkinWeightComparisonStatus.Changed)
                    {
                        m_State.SetWeightClusterSelected(pair.MeshPath, pair.BonePath, false);
                    }

                    if (pair.Bindpose?.Status == SkinWeightComparisonStatus.Changed)
                    {
                        m_State.SetBindposeSelected(pair.MeshPath, pair.BonePath, false);
                    }
                }

                foreach (var bone in m_Comparison.Bones.Where(bone =>
                             bone.Transform?.Status == SkinWeightComparisonStatus.Changed))
                {
                    m_State.SetBoneTransformSelected(bone.BonePath, false);
                }

                RebuildArmatureDependencies();
            }

            public bool IsRequiredParent(string bonePath)
            {
                string normalized = NormalizePathKey(bonePath);
                foreach (string selected in m_State.GetSelectedNewBonePaths())
                {
                    if (selected == normalized)
                    {
                        continue;
                    }

                    string parent = GetParentPath(selected);
                    while (!string.IsNullOrEmpty(parent))
                    {
                        if (parent == normalized &&
                            m_Comparison.TryGetBone(parent, out var parentBone) &&
                            parentBone.RequiresCreateBone)
                        {
                            return true;
                        }

                        parent = GetParentPath(parent);
                    }
                }

                return false;
            }

            private void ClearAllInternal()
            {
                m_State.ClearSimpleAxes();
                m_State.ClearMeshSelections();
                m_State.ClearArmatureSelections();
                m_State.ClearMeshEnabledOverrides();
            }

            private void ApplySimpleCartesianSelection()
            {
                m_State.ClearMeshSelections();
                m_State.ClearExplicitBoneTransformSelections();
                m_State.ClearAutomaticArmatureSelections();
                m_State.ClearMeshEnabledOverrides();
                var selectedMeshKeys = new HashSet<string>(
                    m_State.GetSelectedSimpleMeshKeys(),
                    StringComparer.Ordinal);
                var selectedBones = new HashSet<string>(
                    m_State.GetSelectedSimpleBonePaths(),
                    StringComparer.Ordinal);
                foreach (string bonePath in selectedBones)
                {
                    if (m_Comparison.TryGetBone(bonePath, out var bone) && bone.SourceBoneExists)
                    {
                        m_State.SetBoneTransformSelected(bonePath, true);
                    }
                }

                foreach (var mesh in m_Comparison.Meshes)
                {
                    if (!selectedMeshKeys.Contains(NormalizePathKey(mesh.MeshPath)))
                    {
                        continue;
                    }

                    foreach (var pair in mesh.Bones.Where(pair => selectedBones.Contains(pair.BonePath)))
                    {
                        if (pair.Weight != null)
                        {
                            m_State.SetWeightClusterSelected(pair.MeshPath, pair.BonePath, true);
                        }

                        if (ShouldAutomaticallySelectBindpose(pair))
                        {
                            m_State.SetBindposeSelected(pair.MeshPath, pair.BonePath, true);
                        }
                    }
                }

                RebuildArmatureDependencies();
            }

            private void AddAutomaticArmatureDependencies(string bonePath, bool forceTransform)
            {
                if (!m_Comparison.TryGetBone(bonePath, out var bone))
                {
                    return;
                }

                if (forceTransform && bone.SourceBoneExists)
                {
                    m_State.SetBoneTransformForced(bone.BonePath, true);
                }

                if (bone.RequiresCreateBone)
                {
                    m_State.SetNewBoneSelected(bone.BonePath, true);
                    m_State.EnsureRequiredParentSelections(m_Comparison);
                }
            }

            private void RebuildArmatureDependencies()
            {
                m_State.ClearAutomaticArmatureSelections();
                foreach (string bonePath in m_State.GetExplicitBoneTransformPaths())
                {
                    AddAutomaticArmatureDependencies(bonePath, false);
                }

                foreach (var pair in m_Comparison.Meshes.SelectMany(mesh => mesh.Bones))
                {
                    if (!m_State.IsMeshEnabled(pair.MeshPath))
                    {
                        continue;
                    }

                    if (m_State.IsWeightClusterSelected(pair.MeshPath, pair.BonePath) ||
                        m_State.IsBindposeSelected(pair.MeshPath, pair.BonePath))
                    {
                        AddAutomaticArmatureDependencies(pair.BonePath, true);
                    }
                }

                if (!m_State.HasMeshScopedSelections() &&
                    m_State.GetSelectedBoneTransformPaths().Count > 0 &&
                    (m_Comparison.Meshes.FirstOrDefault(mesh => m_State.IsMeshEnabled(mesh.MeshPath)) ??
                     m_Comparison.Meshes.FirstOrDefault()) is { } hostMesh)
                {
                    m_State.SetArmatureOnlyHostMesh(hostMesh.MeshPath);
                }

                m_State.SyncToExtractionOptions(m_ExtractionOptions, m_Comparison);
            }

            private static bool ShouldAutomaticallySelectWeight(SkinWeightMeshBoneComparison pair)
            {
                return pair?.Weight != null && IsNewOrChanged(pair.Weight.Status);
            }

            private static bool ShouldAutomaticallySelectBindpose(SkinWeightMeshBoneComparison pair)
            {
                return pair?.Bindpose?.SourceExists == true && IsNewOrChanged(pair.Bindpose.Status);
            }

            private static bool ShouldAutomaticallySelectPair(SkinWeightMeshBoneComparison pair)
            {
                return ShouldAutomaticallySelectWeight(pair) || ShouldAutomaticallySelectBindpose(pair);
            }

            private bool HasSelectedMeshData(SkinWeightMeshComparison mesh)
            {
                return mesh != null && mesh.Bones.Any(pair =>
                    m_State.IsWeightClusterSelected(pair.MeshPath, pair.BonePath) ||
                    m_State.IsBindposeSelected(pair.MeshPath, pair.BonePath));
            }
        }
    }
}
