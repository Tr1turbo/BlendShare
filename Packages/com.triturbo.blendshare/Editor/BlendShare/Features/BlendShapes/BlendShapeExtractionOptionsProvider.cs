using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShapeShare;
using Triturbo.BlendShare.Core;
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
        private Vector2 scrollPosition;
        private bool showApplyTransform;
        private float blendShapeScrollHeight = 240f;

        public override string FeatureId => BlendShapeFeatureObject.Id;
        public override string TabLabel => "BlendShapes";
        public override int DisplayOrder => -100;

        protected override BlendShapeExtractionOptions CreateDefault()
        {
            return new BlendShapeExtractionOptions();
        }

        public object BuildInspectionData(
            FbxInspectionSession session,
            IReadOnlyList<MeshFeatureExtractionMeshRequest> meshes)
        {
            return BlendShapeFbxComparison.BuildInspectionData(session, meshes);
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

            var controls = CreateGeneralOptionsElement(options);
            var listRoot = CreateBlendShapeListElement(options, context);
            listRoot.SetEnabled(options.Enabled);
            root.Add(controls);
            root.Add(listRoot);
            return root;
        }

        private VisualElement CreateGeneralOptionsElement(BlendShapeExtractionOptions options)
        {
            var root = new VisualElement();
            root.style.marginBottom = 6f;

            var enabledToggle = new Toggle(Localization.S("blendshapes"))
            {
                value = options.Enabled
            };
            root.Add(enabledToggle);

            var controls = new VisualElement();
            controls.style.marginLeft = 14f;
            controls.SetEnabled(options.Enabled);
            root.Add(controls);

            var transformFoldout = new Foldout
            {
                text = Localization.S("extractor.apply_transform"),
                value = showApplyTransform
            };
            transformFoldout.RegisterValueChangedCallback(evt => showApplyTransform = evt.newValue);
            controls.Add(transformFoldout);

            transformFoldout.Add(CreateOptionToggle(
                Localization.S("extractor.apply_translate"),
                options.ApplyTranslate,
                value => options.ApplyTranslate = value));
            transformFoldout.Add(CreateOptionToggle(
                Localization.S("extractor.apply_rotation"),
                options.ApplyRotation,
                value => options.ApplyRotation = value));
            transformFoldout.Add(CreateOptionToggle(
                Localization.S("extractor.apply_scale"),
                options.ApplyScale,
                value => options.ApplyScale = value));

            var baseMeshField = new EnumField(Localization.S("extractor.base_mesh"), options.BaseMesh);
            baseMeshField.RegisterValueChangedCallback(evt =>
            {
                if (evt.newValue is BlendShapeBaseMesh baseMesh)
                {
                    options.BaseMesh = baseMesh;
                }
            });
            controls.Add(baseMeshField);

            enabledToggle.RegisterValueChangedCallback(evt =>
            {
                options.Enabled = evt.newValue;
                controls.SetEnabled(options.Enabled);
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
            root.Add(CreateHeaderLabel(Localization.S("new_extractor.meshes")));

            if (context?.SourceFbxGo == null || context.OriginFbxGo == null)
            {
                root.Add(new HelpBox(Localization.S("new_extractor.assign_fbx_hint"), HelpBoxMessageType.Info));
                return root;
            }

            var requests = context.Meshes?
                .Where(mesh => mesh != null)
                .ToArray() ?? System.Array.Empty<MeshFeatureExtractionMeshRequest>();
            if (requests.Length == 0)
            {
                root.Add(new HelpBox(Localization.S("new_extractor.no_meshes"), HelpBoxMessageType.Info));
                return root;
            }

            var stickyHeader = StickyHeaderElement.CreateCommandBar();
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

            System.Action requestStickyRefresh = null;
            var sections = new List<StickyHeaderSection>();
            foreach (var request in requests)
            {
                var section = CreateMeshSection(
                    options,
                    request,
                    sourceMeshes,
                    originMeshes,
                    comparison,
                    () => requestStickyRefresh?.Invoke());
                if (section == null)
                {
                    continue;
                }

                sections.Add(section);
                scrollView.Add(section.Root);
            }

            if (sections.Count == 0)
            {
                stickyHeader.style.display = DisplayStyle.None;
                scrollView.Add(new HelpBox(Localization.S("new_extractor.no_meshes"), HelpBoxMessageType.Info));
                return root;
            }

            var overview = StickyHeaderOverview.Create(sections, () => Localization.S("new_extractor.meshes"));
            stickyHeader.Bind(scrollView, sections, overview);
            requestStickyRefresh = stickyHeader.Refresh;
            return root;
        }

        private StickyHeaderSection CreateMeshSection(
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

            var meshEnabled = new Toggle("Enable Mesh")
            {
                value = options.IsMeshEnabled(request.Path)
            };
            foldout.Add(meshEnabled);

            var meshControls = new VisualElement();
            meshControls.SetEnabled(meshEnabled.value);
            foldout.Add(meshControls);

            var pathLabel = new Label($"{Localization.S("new_extractor.mesh_path")}: {request.Path}");
            pathLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            meshControls.Add(pathLabel);

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
                text = "Enable All"
            };
            var disableAll = new Button(DisableAll)
            {
                text = "Disable All"
            };
            var restore = new Button(RestoreDefaults)
            {
                text = "Auto"
            };
            var disableChanged = new Button(DisableChanged)
            {
                text = "Disable Diff"
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
                shapeToggle.style.marginLeft = 14f;
                shapeToggle.style.width = 22f;
                shapeToggle.style.flexShrink = 0f;
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
                row.Add(shapeToggle);
                table.Add(row);
            }

            meshControls.Add(table);

            return new StickyHeaderSection(
                foldout,
                foldout,
                () => foldout.text,
                () => selected.Count,
                blendShapeNames.Count,
                () => options.IsMeshEnabled(request.Path),
                value => meshEnabled.value = value,
                EnableAll,
                DisableAll,
                RestoreDefaults,
                DisableChanged);
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

        private static Label CreateHeaderLabel(string text)
        {
            var label = new Label(text);
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
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

            var nameLabel = new Label("BlendShape");
            nameLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            ApplyBlendShapeNameColumnStyle(nameLabel);

            var statusLabel = new Label("Status");
            statusLabel.style.width = ComparisonPillSlotWidth;
            statusLabel.style.flexShrink = 0f;
            statusLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            statusLabel.style.unityTextAlign = TextAnchor.MiddleRight;

            var toggleLabel = new Label("Use");
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

        private static VisualElement CreateBlendShapeTableRow(int rowIndex)
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

        private static void ApplyBlendShapeNameColumnStyle(Label label)
        {
            label.style.flexGrow = 0.8f;
            label.style.flexShrink = 1f;
            label.style.maxWidth = 680f;
            label.style.minWidth = 100f;
            label.style.overflow = Overflow.Hidden;
            label.style.unityTextOverflowPosition = TextOverflowPosition.End;
        }

        private static VisualElement CreateComparisonPillSlot()
        {
            var statusSlot = new VisualElement();
            statusSlot.style.width = ComparisonPillSlotWidth;
            statusSlot.style.flexShrink = 0f;
            statusSlot.style.flexDirection = FlexDirection.RowReverse;
            statusSlot.style.alignItems = Align.Center;
            return statusSlot;
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

            DrawGeneralOptions(options);
            DrawBlendShapeToggles(options, context);
        }

        private void DrawGeneralOptions(BlendShapeExtractionOptions options)
        {
            options.Enabled = EditorGUILayout.ToggleLeft(Localization.S("blendshapes"), options.Enabled);
            EditorGUI.BeginDisabledGroup(!options.Enabled);

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

            if (context?.SourceFbxGo == null || context.OriginFbxGo == null)
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
            EditorGUILayout.LabelField(Localization.G("new_extractor.mesh_path"), new GUIContent(request.Path));
            if (!string.IsNullOrEmpty(meshComparison?.Message))
            {
                EditorGUILayout.HelpBox(meshComparison.Message, MessageType.Warning);
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(Localization.S("data.enable_all_blendshapes")))
            {
                selected = new HashSet<string>(blendShapeNames);
            }

            if (GUILayout.Button(Localization.S("data.mute_all_blendshapes")))
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
                BlendShapeComparisonStatus.New => "New",
                BlendShapeComparisonStatus.Changed => "Diff",
                BlendShapeComparisonStatus.Same => "Same",
                BlendShapeComparisonStatus.Unavailable => "N/A",
                _ => "Unknown"
            };
        }
    }
}
