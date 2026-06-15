using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShapeShare;
using Triturbo.BlendShare.Core;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Triturbo.BlendShare.Features.SkinWeights
{
    public sealed class SkinWeightExtractionOptionsProvider
        : MeshFeatureExtractionOptionsProvider<SkinWeightExtractionOptions>,
          IMeshFeatureInspectionProvider,
          IUIToolkitMeshFeatureOptionsProvider
    {
        private const float StatusSlotWidth = 94f;
        private readonly Dictionary<string, bool> boneFoldouts = new();

        public override string FeatureId => SkinWeightFeatureObject.Id;
        public override string TabLabel => "Skin Weights";
        public override int DisplayOrder => -50;

        protected override SkinWeightExtractionOptions CreateDefault()
        {
            return new SkinWeightExtractionOptions();
        }

        public object BuildInspectionData(
            FbxInspectionSession session,
            IReadOnlyList<MeshFeatureExtractionMeshRequest> meshes)
        {
            return SkinWeightFbxComparison.BuildInspectionData(session, meshes);
        }

        public VisualElement CreateOptionsElement(
            MeshFeatureExtractionOptions rawOptions,
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

            var enabledToggle = new Toggle(Localization.S("skin_weights"))
            {
                value = options.Enabled
            };
            root.Add(enabledToggle);

            var listRoot = CreateSkinWeightListElement(options, context);
            listRoot.SetEnabled(options.Enabled);
            root.Add(listRoot);

            enabledToggle.RegisterValueChangedCallback(evt =>
            {
                options.Enabled = evt.newValue;
                listRoot.SetEnabled(options.Enabled);
            });
            return root;
        }

        protected override void DrawOptionsGUI(
            SkinWeightExtractionOptions options,
            MeshFeatureOptionsEditorContext context)
        {
            if (options == null)
            {
                return;
            }

            options.Enabled = EditorGUILayout.ToggleLeft(Localization.S("skin_weights"), options.Enabled);
        }

        private VisualElement CreateSkinWeightListElement(
            SkinWeightExtractionOptions options,
            MeshFeatureOptionsEditorContext context)
        {
            var root = new VisualElement { name = "skin-weight-list-root" };
            root.style.flexGrow = 1f;
            root.style.minHeight = 0f;
            root.style.flexDirection = FlexDirection.Column;
            root.Add(CreateHeaderLabel("Bones"));

            if (context?.SourceFbxGo == null || context.OriginFbxGo == null)
            {
                root.Add(new HelpBox(Localization.S("new_extractor.assign_fbx_hint"), HelpBoxMessageType.Info));
                return root;
            }

            if (!context.TryGetCachedData(SkinWeightFeatureObject.Id, out SkinWeightFbxComparison comparison) ||
                comparison == null ||
                comparison.Bones.Count == 0)
            {
                root.Add(new HelpBox("No skin weight differences were found.", HelpBoxMessageType.Info));
                return root;
            }

            options.EnsureDefaultSelection(comparison);
            options.EnsureRequiredParentSelections(comparison);
            AddDiagnostics(root, comparison);

            var stickyHeader = StickyHeaderElement.CreateCommandBar(
                "Select All",
                "Clear All",
                "Auto",
                "Disable Diff");
            root.Add(stickyHeader);

            var scrollView = new ScrollView(ScrollViewMode.Vertical);
            scrollView.style.flexGrow = 1f;
            scrollView.style.minHeight = 0f;
            root.Add(scrollView);

            Action requestStickyRefresh = null;
            var sections = new List<StickyHeaderSection>();
            foreach (var bone in comparison.Bones)
            {
                var section = CreateBoneSection(
                    options,
                    comparison,
                    bone,
                    () => requestStickyRefresh?.Invoke());
                sections.Add(section);
                scrollView.Add(section.Root);
            }

            var overview = StickyHeaderOverview.Create(sections, () => "Bones");
            stickyHeader.Bind(scrollView, sections, overview);
            requestStickyRefresh = stickyHeader.RequestRefresh;
            return root;
        }

        private StickyHeaderSection CreateBoneSection(
            SkinWeightExtractionOptions options,
            SkinWeightFbxComparison comparison,
            SkinWeightBoneComparison bone,
            Action onSelectionChanged)
        {
            string key = MeshNodePath.Normalize(bone.BonePath);
            if (!boneFoldouts.ContainsKey(key))
            {
                boneFoldouts[key] = GetCombinedStatus(bone) != SkinWeightComparisonStatus.Same;
            }

            var foldout = new Foldout
            {
                value = boneFoldouts[key]
            };
            foldout.style.marginBottom = 6f;

            var weightToggles = new List<(Toggle Toggle, SkinWeightMeshClusterComparison Cluster)>();
            var bindposeToggles = new List<(Toggle Toggle, SkinWeightBindposeComparison Bindpose)>();
            Toggle transformToggle = null;
            Toggle createBoneToggle = null;
            bool contentBuilt = false;

            int SelectedCount()
            {
                int selected = (bone.WeightClusters ?? Array.Empty<SkinWeightMeshClusterComparison>())
                    .Count(cluster => options.IsWeightClusterSelected(cluster.MeshPath, bone.BonePath));
                selected += (bone.Bindposes ?? Array.Empty<SkinWeightBindposeComparison>())
                    .Count(bindPose => options.IsBindposeSelected(bindPose.MeshPath, bone.BonePath));
                if (bone.Transform != null && options.IsBoneTransformSelected(bone.BonePath))
                {
                    selected++;
                }

                if (bone.RequiresCreateBone && options.IsNewBoneSelected(bone.BonePath))
                {
                    selected++;
                }

                return selected;
            }

            int totalCount = (bone.WeightClusters?.Count ?? 0) +
                             (bone.Bindposes?.Count ?? 0) +
                             (bone.Transform != null ? 1 : 0) +
                             (bone.RequiresCreateBone ? 1 : 0);

            void UpdateTitle()
            {
                foldout.text = $"{GetBoneName(bone.BonePath)} ({GetCombinedStatusText(bone)}, {SelectedCount()}/{totalCount})";
            }

            void RefreshToggles()
            {
                foreach (var item in weightToggles)
                {
                    item.Toggle.SetValueWithoutNotify(
                        options.IsWeightClusterSelected(item.Cluster.MeshPath, bone.BonePath));
                }

                foreach (var item in bindposeToggles)
                {
                    item.Toggle.SetValueWithoutNotify(
                        options.IsBindposeSelected(item.Bindpose.MeshPath, bone.BonePath));
                }

                if (transformToggle != null)
                {
                    bool requiredTransform = bone.RequiresCreateBone &&
                                             options.IsNewBoneSelected(bone.BonePath);
                    transformToggle.SetEnabled(!requiredTransform);
                    transformToggle.SetValueWithoutNotify(options.IsBoneTransformSelected(bone.BonePath));
                }

                if (createBoneToggle != null)
                {
                    bool requiredParent = IsRequiredParent(options, comparison, bone.BonePath);
                    createBoneToggle.label = requiredParent ? "Create bone (required parent)" : "Create bone";
                    createBoneToggle.SetEnabled(!requiredParent);
                    createBoneToggle.SetValueWithoutNotify(options.IsNewBoneSelected(bone.BonePath));
                }

                UpdateTitle();
                onSelectionChanged?.Invoke();
            }

            void EnableAll()
            {
                foreach (var cluster in bone.WeightClusters ?? Array.Empty<SkinWeightMeshClusterComparison>())
                {
                    options.SetWeightClusterSelected(cluster.MeshPath, bone.BonePath, true);
                }

                foreach (var bindPose in bone.Bindposes ?? Array.Empty<SkinWeightBindposeComparison>())
                {
                    options.SetBindposeSelected(bindPose.MeshPath, bone.BonePath, true);
                }

                if (bone.Transform != null)
                {
                    options.SetBoneTransformSelected(bone.BonePath, true);
                }

                if (bone.RequiresCreateBone)
                {
                    options.SetNewBoneSelected(bone.BonePath, true);
                    options.SetBoneTransformSelected(bone.BonePath, true);
                    options.EnsureRequiredParentSelections(comparison);
                }

                RefreshToggles();
            }

            void DisableAll()
            {
                foreach (var cluster in bone.WeightClusters ?? Array.Empty<SkinWeightMeshClusterComparison>())
                {
                    options.SetWeightClusterSelected(cluster.MeshPath, bone.BonePath, false);
                }

                foreach (var bindPose in bone.Bindposes ?? Array.Empty<SkinWeightBindposeComparison>())
                {
                    options.SetBindposeSelected(bindPose.MeshPath, bone.BonePath, false);
                }

                options.SetNewBoneSelected(bone.BonePath, false);
                options.SetBoneTransformSelected(bone.BonePath, false);
                options.EnsureRequiredParentSelections(comparison);
                RefreshToggles();
            }

            void Restore()
            {
                foreach (var cluster in bone.WeightClusters ?? Array.Empty<SkinWeightMeshClusterComparison>())
                {
                    bool selected = cluster.Status == SkinWeightComparisonStatus.New ||
                                    cluster.Status == SkinWeightComparisonStatus.Changed;
                    options.SetWeightClusterSelected(cluster.MeshPath, bone.BonePath, selected);
                }

                foreach (var bindPose in bone.Bindposes ?? Array.Empty<SkinWeightBindposeComparison>())
                {
                    bool selected = (bindPose.Status == SkinWeightComparisonStatus.New ||
                                     bindPose.Status == SkinWeightComparisonStatus.Changed) &&
                                    options.IsWeightClusterSelected(bindPose.MeshPath, bone.BonePath);
                    options.SetBindposeSelected(bindPose.MeshPath, bone.BonePath, selected);
                }

                bool anyWeightSelected = (bone.WeightClusters ?? Array.Empty<SkinWeightMeshClusterComparison>())
                    .Any(cluster => options.IsWeightClusterSelected(cluster.MeshPath, bone.BonePath));
                options.SetNewBoneSelected(bone.BonePath, bone.RequiresCreateBone && anyWeightSelected);
                bool selectTransform = bone.Transform?.Status == SkinWeightComparisonStatus.Changed ||
                                       (bone.RequiresCreateBone && anyWeightSelected);
                options.SetBoneTransformSelected(bone.BonePath, selectTransform);
                options.EnsureRequiredParentSelections(comparison);
                RefreshToggles();
            }

            void DisableChanged()
            {
                foreach (var cluster in bone.WeightClusters ?? Array.Empty<SkinWeightMeshClusterComparison>())
                {
                    if (cluster.Status == SkinWeightComparisonStatus.Changed)
                    {
                        options.SetWeightClusterSelected(cluster.MeshPath, bone.BonePath, false);
                    }
                }

                foreach (var bindPose in bone.Bindposes ?? Array.Empty<SkinWeightBindposeComparison>())
                {
                    if (bindPose.Status == SkinWeightComparisonStatus.Changed)
                    {
                        options.SetBindposeSelected(bindPose.MeshPath, bone.BonePath, false);
                    }
                }

                if (bone.Transform?.Status == SkinWeightComparisonStatus.Changed)
                {
                    options.SetBoneTransformSelected(bone.BonePath, false);
                }

                options.EnsureRequiredParentSelections(comparison);
                RefreshToggles();
            }

            void EnsureContentBuilt()
            {
                if (contentBuilt)
                {
                    return;
                }

                contentBuilt = true;
                var pathLabel = new Label($"Path: {bone.BonePath}");
                pathLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
                pathLabel.style.marginBottom = 4f;
                foldout.Add(pathLabel);

                if (bone.RequiresCreateBone)
                {
                    bool requiredParent = IsRequiredParent(options, comparison, bone.BonePath);
                    createBoneToggle = new Toggle(requiredParent ? "Create bone (required parent)" : "Create bone")
                    {
                        value = options.IsNewBoneSelected(bone.BonePath)
                    };
                    createBoneToggle.SetEnabled(!requiredParent);
                    createBoneToggle.RegisterValueChangedCallback(evt =>
                    {
                        options.SetNewBoneSelected(bone.BonePath, evt.newValue);
                        if (evt.newValue)
                        {
                            options.SetBoneTransformSelected(bone.BonePath, true);
                        }
                        else if (bone.RequiresCreateBone)
                        {
                            options.SetBoneTransformSelected(bone.BonePath, false);
                        }

                        options.EnsureRequiredParentSelections(comparison);
                        RefreshToggles();
                    });
                    foldout.Add(createBoneToggle);
                }

                AddTransformTable(foldout, options, bone, toggle => transformToggle = toggle, () =>
                {
                    if (bone.RequiresCreateBone && options.IsBoneTransformSelected(bone.BonePath))
                    {
                        options.SetNewBoneSelected(bone.BonePath, true);
                        options.EnsureRequiredParentSelections(comparison);
                    }

                    RefreshToggles();
                });

                AddWeightTable(foldout, options, bone, weightToggles, () =>
                {
                    if (bone.RequiresCreateBone)
                    {
                        bool anyWeightSelected = (bone.WeightClusters ?? Array.Empty<SkinWeightMeshClusterComparison>())
                            .Any(cluster => options.IsWeightClusterSelected(cluster.MeshPath, bone.BonePath));
                        if (anyWeightSelected)
                        {
                            options.SetNewBoneSelected(bone.BonePath, true);
                            options.SetBoneTransformSelected(bone.BonePath, true);
                            options.EnsureRequiredParentSelections(comparison);
                        }
                    }

                    RefreshToggles();
                });
                AddBindposeTable(foldout, options, bone, bindposeToggles, RefreshToggles);
                RefreshToggles();
            }

            UpdateTitle();
            foldout.RegisterValueChangedCallback(evt =>
            {
                boneFoldouts[key] = evt.newValue;
                if (evt.newValue)
                {
                    EnsureContentBuilt();
                }
                else
                {
                    onSelectionChanged?.Invoke();
                }
            });

            if (foldout.value)
            {
                EnsureContentBuilt();
            }

            return new StickyHeaderSection(
                foldout,
                foldout,
                () => foldout.text,
                SelectedCount,
                totalCount,
                () => SelectedCount() > 0,
                value =>
                {
                    if (value)
                    {
                        EnableAll();
                    }
                    else
                    {
                        DisableAll();
                    }
                },
                EnableAll,
                DisableAll,
                Restore,
                DisableChanged);
        }

        private static void AddTransformTable(
            VisualElement root,
            SkinWeightExtractionOptions options,
            SkinWeightBoneComparison bone,
            Action<Toggle> registerToggle,
            Action onChanged)
        {
            if (bone.Transform == null)
            {
                return;
            }

            root.Add(CreateSectionLabel("Bone Transform"));
            var table = CreateTable();
            table.Add(CreateTableHeader("Pose", "Status", "Use"));

            var toggle = new Toggle
            {
                value = options.IsBoneTransformSelected(bone.BonePath)
            };
            toggle.RegisterValueChangedCallback(evt =>
            {
                options.SetBoneTransformSelected(bone.BonePath, evt.newValue);
                onChanged?.Invoke();
            });
            registerToggle?.Invoke(toggle);

            var row = CreateTableRow(0);
            row.Add(CreateNameLabel("Local translation / rotation / scale"));
            row.Add(CreateStatusSlot(bone.Transform.Status));
            row.Add(CreateToggleSlot(toggle));
            table.Add(row);
            root.Add(table);
        }

        private static void AddWeightTable(
            VisualElement root,
            SkinWeightExtractionOptions options,
            SkinWeightBoneComparison bone,
            List<(Toggle Toggle, SkinWeightMeshClusterComparison Cluster)> toggles,
            Action onChanged)
        {
            if (bone.WeightClusters == null || bone.WeightClusters.Count == 0)
            {
                return;
            }

            root.Add(CreateSectionLabel("Weights"));
            var table = CreateTable();
            table.Add(CreateTableHeader("Mesh", "Status", "Use"));
            for (int i = 0; i < bone.WeightClusters.Count; i++)
            {
                var cluster = bone.WeightClusters[i];
                var toggle = new Toggle
                {
                    value = options.IsWeightClusterSelected(cluster.MeshPath, bone.BonePath)
                };
                toggle.RegisterValueChangedCallback(evt =>
                {
                    options.SetWeightClusterSelected(cluster.MeshPath, bone.BonePath, evt.newValue);
                    onChanged?.Invoke();
                });
                toggles.Add((toggle, cluster));

                var row = CreateTableRow(i);
                row.Add(CreateNameLabel($"{cluster.MeshPath}  ({cluster.AffectedControlPointCount} CP, max {cluster.MaxDelta:0.#####})"));
                row.Add(CreateStatusSlot(cluster.Status));
                row.Add(CreateToggleSlot(toggle));
                table.Add(row);
            }

            root.Add(table);
        }

        private static void AddBindposeTable(
            VisualElement root,
            SkinWeightExtractionOptions options,
            SkinWeightBoneComparison bone,
            List<(Toggle Toggle, SkinWeightBindposeComparison Bindpose)> toggles,
            Action onChanged)
        {
            if (bone.Bindposes == null || bone.Bindposes.Count == 0)
            {
                return;
            }

            root.Add(CreateSectionLabel("Bindposes"));
            var table = CreateTable();
            table.Add(CreateTableHeader("Mesh", "Status", "Use"));
            for (int i = 0; i < bone.Bindposes.Count; i++)
            {
                var bindPose = bone.Bindposes[i];
                var toggle = new Toggle
                {
                    value = options.IsBindposeSelected(bindPose.MeshPath, bone.BonePath)
                };
                toggle.RegisterValueChangedCallback(evt =>
                {
                    options.SetBindposeSelected(bindPose.MeshPath, bone.BonePath, evt.newValue);
                    onChanged?.Invoke();
                });
                toggles.Add((toggle, bindPose));

                var row = CreateTableRow(i);
                row.Add(CreateNameLabel(bindPose.MeshPath));
                row.Add(CreateStatusSlot(bindPose.Status));
                row.Add(CreateToggleSlot(toggle));
                table.Add(row);
            }

            root.Add(table);
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

        private static Label CreateHeaderLabel(string text)
        {
            var label = new Label(text);
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.marginBottom = 4f;
            return label;
        }

        private static Label CreateSectionLabel(string text)
        {
            var label = new Label(text);
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.marginTop = 5f;
            label.style.marginBottom = 2f;
            return label;
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

        private static VisualElement CreateTableHeader(
            string name,
            string status,
            string use)
        {
            var row = CreateTableRow(-1);
            row.style.backgroundColor = new Color(0.23f, 0.23f, 0.23f);

            var nameLabel = CreateNameLabel(name);
            nameLabel.style.unityFontStyleAndWeight = FontStyle.Bold;

            var statusLabel = new Label(status);
            statusLabel.style.width = StatusSlotWidth;
            statusLabel.style.flexShrink = 0f;
            statusLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            statusLabel.style.unityTextAlign = TextAnchor.MiddleRight;

            var toggleLabel = new Label(use);
            toggleLabel.style.width = 22f;
            toggleLabel.style.marginLeft = 14f;
            toggleLabel.style.flexShrink = 0f;
            toggleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            toggleLabel.style.unityTextAlign = TextAnchor.MiddleCenter;

            row.Add(nameLabel);
            row.Add(statusLabel);
            row.Add(toggleLabel);
            return row;
        }

        private static VisualElement CreateTableRow(int rowIndex)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.minHeight = 24f;
            row.style.paddingLeft = 4f;
            row.style.paddingRight = 4f;
            if (rowIndex >= 0)
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
            label.style.flexGrow = 0.8f;
            label.style.flexShrink = 1f;
            label.style.maxWidth = 680f;
            label.style.minWidth = 100f;
            label.style.overflow = Overflow.Hidden;
            label.style.unityTextOverflowPosition = TextOverflowPosition.End;
            return label;
        }

        private static VisualElement CreateStatusSlot(SkinWeightComparisonStatus status)
        {
            var slot = new VisualElement();
            slot.style.width = StatusSlotWidth;
            slot.style.flexShrink = 0f;
            slot.style.flexDirection = FlexDirection.RowReverse;
            slot.style.alignItems = Align.Center;
            if (status != SkinWeightComparisonStatus.Same)
            {
                slot.Add(CreateStatusPill(status));
            }

            return slot;
        }

        private static VisualElement CreateToggleSlot(Toggle toggle)
        {
            toggle.style.marginLeft = 14f;
            toggle.style.width = 22f;
            toggle.style.flexShrink = 0f;
            return toggle;
        }

        private static Label CreateStatusPill(SkinWeightComparisonStatus status)
        {
            var label = new Label(GetStatusText(status));
            label.style.width = GetStatusPillWidth(status);
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
                SkinWeightComparisonStatus.New => new Color(0.20f, 0.36f, 0.22f),
                SkinWeightComparisonStatus.Changed => new Color(0.34f, 0.28f, 0.15f),
                _ => new Color(0.24f, 0.24f, 0.24f)
            };
            var borderColor = status switch
            {
                SkinWeightComparisonStatus.New => new Color(0.27f, 0.48f, 0.30f),
                SkinWeightComparisonStatus.Changed => new Color(0.48f, 0.39f, 0.22f),
                _ => new Color(0.36f, 0.36f, 0.36f)
            };
            label.style.borderBottomColor = borderColor;
            label.style.borderTopColor = borderColor;
            label.style.borderLeftColor = borderColor;
            label.style.borderRightColor = borderColor;
            label.style.color = new Color(0.90f, 0.90f, 0.90f);
            return label;
        }

        private static float GetStatusPillWidth(SkinWeightComparisonStatus status)
        {
            return status switch
            {
                SkinWeightComparisonStatus.New => 52f,
                SkinWeightComparisonStatus.Changed => 52f,
                _ => 90f
            };
        }

        private static SkinWeightComparisonStatus GetCombinedStatus(SkinWeightBoneComparison bone)
        {
            if (bone.RequiresCreateBone)
            {
                return SkinWeightComparisonStatus.New;
            }

            if ((bone.WeightClusters ?? Array.Empty<SkinWeightMeshClusterComparison>())
                    .Any(cluster => cluster.Status == SkinWeightComparisonStatus.Changed ||
                                    cluster.Status == SkinWeightComparisonStatus.New) ||
                (bone.Bindposes ?? Array.Empty<SkinWeightBindposeComparison>())
                    .Any(bindPose => bindPose.Status == SkinWeightComparisonStatus.Changed ||
                                     bindPose.Status == SkinWeightComparisonStatus.New) ||
                bone.Transform?.Status == SkinWeightComparisonStatus.Changed)
            {
                return SkinWeightComparisonStatus.Changed;
            }

            return SkinWeightComparisonStatus.Same;
        }

        private static string GetCombinedStatusText(SkinWeightBoneComparison bone)
        {
            if (bone.RequiresCreateBone)
            {
                return "New Bone";
            }

            bool changedWeights = (bone.WeightClusters ?? Array.Empty<SkinWeightMeshClusterComparison>())
                .Any(cluster => cluster.Status == SkinWeightComparisonStatus.Changed ||
                                cluster.Status == SkinWeightComparisonStatus.New);
            bool changedBindposes = (bone.Bindposes ?? Array.Empty<SkinWeightBindposeComparison>())
                .Any(bindPose => bindPose.Status == SkinWeightComparisonStatus.Changed ||
                                 bindPose.Status == SkinWeightComparisonStatus.New);
            bool changedTransform = bone.Transform?.Status == SkinWeightComparisonStatus.Changed;
            if (changedWeights && changedBindposes && changedTransform)
            {
                return "Changed Weights + Bindpose + Pose";
            }

            if (changedWeights && changedBindposes)
            {
                return "Changed Weights + Bindpose";
            }

            if (changedWeights && changedTransform)
            {
                return "Changed Weights + Pose";
            }

            if (changedBindposes && changedTransform)
            {
                return "Changed Bindpose + Pose";
            }

            if (changedWeights)
            {
                return "Changed Weights";
            }

            if (changedBindposes)
            {
                return "Changed Bindpose";
            }

            if (changedTransform)
            {
                return "Changed Pose";
            }

            return "Same";
        }

        private static string GetStatusText(SkinWeightComparisonStatus status)
        {
            return status switch
            {
                SkinWeightComparisonStatus.New => "New",
                SkinWeightComparisonStatus.Changed => "Diff",
                SkinWeightComparisonStatus.Same => "Same",
                SkinWeightComparisonStatus.MissingSource => "Missing Source",
                SkinWeightComparisonStatus.Unavailable => "N/A",
                _ => "Unknown"
            };
        }

        private static bool IsRequiredParent(
            SkinWeightExtractionOptions options,
            SkinWeightFbxComparison comparison,
            string bonePath)
        {
            string normalized = MeshNodePath.Normalize(bonePath);
            foreach (string selected in options.GetSelectedNewBonePaths())
            {
                if (selected == normalized)
                {
                    continue;
                }

                string parent = MeshNodePath.ParentPath(selected);
                while (parent != MeshNodePath.Root)
                {
                    if (parent == normalized &&
                        comparison.TryGetBone(parent, out var parentBone) &&
                        parentBone.RequiresCreateBone)
                    {
                        return true;
                    }

                    parent = MeshNodePath.ParentPath(parent);
                }
            }

            return false;
        }

        private static string GetBoneName(string path)
        {
            string normalized = MeshNodePath.Normalize(path);
            if (normalized == MeshNodePath.Root)
            {
                return normalized;
            }

            int separator = normalized.LastIndexOf('/');
            return separator >= 0 && separator < normalized.Length - 1
                ? normalized.Substring(separator + 1)
                : normalized;
        }
    }
}
