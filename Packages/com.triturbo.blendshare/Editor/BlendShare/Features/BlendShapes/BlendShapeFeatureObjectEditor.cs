using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShapeShare;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Features.BlendShapes;
using Triturbo.BlendShare.Inspector;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.UIElements;

namespace Triturbo.BlendShare.Features.BlendShapes.Editor
{
    [CustomEditor(typeof(BlendShapeFeatureObject))]
    public sealed class BlendShapeFeatureObjectEditor : MeshFeatureObjectEditor<BlendShapeFeatureObject>
    {
        public override string FeatureId => BlendShapeFeatureObject.Id;
        public override string DisplayName => Localization.FeatureName(FeatureId);

        public override VisualElement CreateElement(MeshFeatureEditorContext context)
        {
            return BlendShapeFeatureEditorElement.Create(context);
        }
    }

    public sealed class BlendShapeFeatureEditorFactory : IMeshFeatureObjectEditor
    {
        private const string LockedPrefKey = "blendshapes.locked";
        private const bool DefaultLocked = true;

        public string FeatureId => BlendShapeFeatureObject.Id;
        public string DisplayName => Localization.FeatureName(FeatureId);
        public Type TargetType => typeof(BlendShapeFeatureObject);

        public VisualElement CreateElement(MeshFeatureEditorContext context)
        {
            return BlendShapeFeatureEditorElement.Create(context);
        }

        public VisualElement CreateEmbeddedInspector(BlendShareEmbeddedEditorContext context)
        {
            var ownerPatch = context.OwnerPatch ?? BlendShareInspectorUtility.FindOwnerPatch(context.EmbeddedObject);
            bool locked = BlendShareInspectorUtility.GetAssetEditorPrefBool(ownerPatch, LockedPrefKey, DefaultLocked);
            return CreateBoxedLockableElement(
                new MeshFeatureEditorContext(
                    context.EmbeddedObject as MeshFeatureObject,
                    context.OwnerMeshData,
                    ownerPatch,
                    context.Refresh),
                locked,
                value => BlendShareInspectorUtility.SetAssetEditorPrefBool(ownerPatch, LockedPrefKey, value, DefaultLocked),
                true);
        }

        public VisualElement CreateCompactElement(MeshFeatureEditorContext context)
        {
            var feature = context.Feature as BlendShapeFeatureObject;
            string label = feature != null
                ? Localization.SF("features.blend-shapes.status.compact", feature.ActiveBlendShapeIndices.Count, feature.BlendShapes.Count)
                : DisplayName;
            return new BlendShareFeatureBadge(label, BlendShapeFeatureEditorElement.Create(context, true, false));
        }

        public long EstimateVideoMemoryBytes(MeshFeatureEditorContext context, int unityVertexCount)
        {
            var feature = context.Feature as BlendShapeFeatureObject;
            if (feature == null || unityVertexCount <= 0)
            {
                return 0;
            }

            int frameCount = 0;
            foreach (int index in feature.ActiveBlendShapeIndices)
            {
                if (index < 0 || index >= feature.BlendShapes.Count)
                {
                    continue;
                }

                frameCount += feature.BlendShapes[index]?.m_Frames?.Length ?? 0;
            }

            const int vector3Bytes = 12;
            const int blendShapeAttributes = 1;
            return (long)frameCount * unityVertexCount * vector3Bytes * blendShapeAttributes;
        }

        private VisualElement CreateBoxedLockableElement(MeshFeatureEditorContext context, bool initialLocked, Action<bool> lockChanged, bool showLockButton)
        {
            var box = BlendShareInspectorUi.Box();
            BlendShareInspectorUi.RegisterDoubleClickAction(box, () => Selection.activeObject = context.Feature);
            var content = new VisualElement();
            bool locked = initialLocked;

            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            header.style.marginBottom = 4;

            var title = new Label(DisplayName);
            BlendShareInspectorUi.StyleStrong(title);
            title.style.flexGrow = 1;
            header.Add(title);

            Button lockButton = null;
            lockButton = CreateLockButton(() =>
            {
                locked = !locked;
                lockChanged?.Invoke(locked);
                Rebuild();
            });
            if (showLockButton)
            {
                header.Add(lockButton);
            }

            box.Add(header);
            box.Add(content);

            void Rebuild()
            {
                UpdateLockButton(lockButton, locked);
                lockButton.tooltip = locked ? Localization.S("features.blend-shapes.unlock_tooltip") : Localization.S("features.blend-shapes.lock_tooltip");
                content.Clear();
                content.Add(BlendShapeFeatureEditorElement.Create(context, locked, true));
            }

            Rebuild();
            return box;
        }

        private static Button CreateLockButton(Action clicked)
        {
            var button = new Button(clicked);
            button.style.width = 24;
            button.style.height = 22;
            button.style.paddingLeft = 2;
            button.style.paddingRight = 2;
            button.style.paddingTop = 1;
            button.style.paddingBottom = 1;
            return button;
        }

        private static void UpdateLockButton(Button button, bool locked)
        {
            var icon = EditorGUIUtility.IconContent(locked ? "Locked" : "Unlocked");
            button.Clear();
            if (icon?.image != null)
            {
                var image = new Image { image = icon.image };
                image.style.width = 16;
                image.style.height = 16;
                image.style.alignSelf = Align.Center;
                button.Add(image);
                button.text = string.Empty;
                return;
            }

            button.text = locked ? Localization.S("common.lock") : Localization.S("common.unlock");
        }
    }

    internal sealed class BlendShapeFeatureEditorElement : VisualElement
    {
        private readonly BlendShapeFeatureObject feature;
        private readonly MeshDataObject ownerMesh;
        private readonly BlendShareObject ownerPatch;
        private readonly Action refreshOwner;
        private readonly TextField filterField;
        private readonly PopupField<string> setPopup;
        private readonly Label statusLabel;
        private Button saveButton;
        private Button saveAsButton;
        private Button revertButton;
        private Button deleteButton;
        private Button muteVisibleButton;
        private Button enableVisibleButton;
        private Button enableAllButton;
        private Button activeSelectedActionButton;
        private Button disabledSelectedActionButton;
        private readonly Foldout disabledFoldout;
        private readonly List<int> activeIndices = new();
        private readonly List<int> visibleActiveIndices = new();
        private readonly List<int> visibleAvailableIndices = new();
        private readonly BlendShapeListView activeBlendShapeList;
        private readonly BlendShapeListView disabledBlendShapeList;
        private readonly bool lockedMode;

        private static string DefaultChoice => Localization.S("features.blend-shapes.selection.default");
        private static string UnsavedChoice => Localization.S("features.blend-shapes.selection.unsaved");

        private BlendShapeFeatureEditorElement(MeshFeatureEditorContext context, bool lockedMode, bool showActiveListHeader)
        {
            feature = context.Feature as BlendShapeFeatureObject;
            ownerMesh = context.OwnerMesh ?? BlendShareInspectorUtility.FindOwnerMesh(feature, context.OwnerPatch);
            ownerPatch = context.OwnerPatch ?? BlendShareInspectorUtility.FindOwnerPatch(feature);
            refreshOwner = context.Refresh;
            this.lockedMode = lockedMode;

            if (feature == null)
            {
                Add(new HelpBox(Localization.S("features.blend-shapes.missing_data"), HelpBoxMessageType.Warning));
                return;
            }

            feature.SanitizeShapeNames();
            activeIndices.Clear();
            activeIndices.AddRange(feature.ActiveBlendShapeIndices);
            RefreshVisibleActiveIndices();

            if (!lockedMode)
            {
                setPopup = new PopupField<string>(Localization.S("features.blend-shapes.selection_set"), BuildPopupChoices(), GetCurrentPopupChoice());
                setPopup.RegisterValueChangedCallback(evt => ApplySelectionChoice(evt.newValue));
                Add(setPopup);

                Add(CreateSelectionSetButtons());

                filterField = new TextField(Localization.S("common.filter"));
                AddFilterIcon(filterField);
                filterField.RegisterValueChangedCallback(_ => RefreshAfterSelectionChange());
                Add(filterField);
                Add(CreateMuteButtons());
            }

            statusLabel = new Label();
            statusLabel.style.opacity = 0.75f;
            Add(statusLabel);

            activeBlendShapeList = new BlendShapeListView(
                visibleActiveIndices,
                GetBlendShapeName,
                lockedMode ? null : Localization.S("features.blend-shapes.mute"),
                lockedMode ? null : indices => MuteActive(indices),
                showActiveListHeader,
                GetActiveListHeader,
                lockedMode ? null : RefreshSelectedActionButtons);
            activeBlendShapeList.Element.style.marginTop = 6;
            Add(activeBlendShapeList.Element);

            if (!lockedMode)
            {
                disabledBlendShapeList = new BlendShapeListView(
                    visibleAvailableIndices,
                    GetBlendShapeName,
                    Localization.S("features.blend-shapes.enable_action"),
                    indices => AddAvailable(indices),
                    false,
                    null,
                    RefreshSelectedActionButtons);
                disabledFoldout = new Foldout { text = Localization.S("features.blend-shapes.selection.disabled_header"), value = false };
                disabledFoldout.style.marginLeft = 0;
                disabledFoldout.style.paddingLeft = 10;
                disabledFoldout.contentContainer.style.marginLeft = 0;
                disabledFoldout.contentContainer.style.paddingLeft = 0;
                disabledFoldout.Add(CreateEnableButtons());
                disabledFoldout.Add(disabledBlendShapeList.Element);
                Add(disabledFoldout);
            }

            activeBlendShapeList.Rebuild(!lockedMode && !IsFilterActive(), lockedMode ? null : indices => ApplyEditedActiveIndices(indices, "Reorder BlendShapes"));
            RefreshVisibleAvailableIndices();
            disabledBlendShapeList?.Rebuild(false, null);
            if (!lockedMode)
            {
                RefreshSelectionSetUi();
            }

            RefreshStatus();
            RefreshSelectedActionButtons();
        }

        public static VisualElement Create(MeshFeatureEditorContext context)
        {
            return new BlendShapeFeatureEditorElement(context, false, true);
        }

        public static VisualElement Create(MeshFeatureEditorContext context, bool lockedMode)
        {
            return new BlendShapeFeatureEditorElement(context, lockedMode, true);
        }

        public static VisualElement Create(MeshFeatureEditorContext context, bool lockedMode, bool showActiveListHeader)
        {
            return new BlendShapeFeatureEditorElement(context, lockedMode, showActiveListHeader);
        }

        private VisualElement CreateSelectionSetButtons()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.flexWrap = Wrap.Wrap;
            saveButton = BlendShareInspectorUi.SmallButton(Localization.S("common.save"), SaveCurrentSet);
            saveAsButton = BlendShareInspectorUi.SmallButton(Localization.S("common.save_as"), SaveAsNewSet);
            revertButton = BlendShareInspectorUi.SmallButton(Localization.S("common.revert"), RevertCurrentSet);
            deleteButton = BlendShareInspectorUi.SmallButton(Localization.S("common.delete"), DeleteCurrentSet);
            row.Add(saveButton);
            row.Add(saveAsButton);
            row.Add(revertButton);
            row.Add(deleteButton);
            return row;
        }

        private static void AddFilterIcon(TextField field)
        {
            BlendShareInspectorUi.AddIconToPrefixLabel(
                field,
                EditorGUIUtility.IconContent(EditorGUIUtility.isProSkin ? "d_Filter Icon" : "Filter Icon"));
        }

        private VisualElement CreateMuteButtons()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.flexWrap = Wrap.Wrap;
            row.style.marginTop = 4;
            activeSelectedActionButton = CreateSelectedActionButton(
                Localization.S("features.blend-shapes.mute"),
                () => activeBlendShapeList.SelectedVisibleIndices(),
                indices => MuteActive(indices));
            muteVisibleButton = BlendShareInspectorUi.SmallButton(Localization.S("features.blend-shapes.mute_visible"), () => MuteActive(visibleActiveIndices));
            row.Add(muteVisibleButton);
            row.Add(activeSelectedActionButton);
            return row;
        }

        private VisualElement CreateEnableButtons()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.flexWrap = Wrap.Wrap;
            //row.style.marginTop = 4;
            disabledSelectedActionButton = CreateSelectedActionButton(
                Localization.S("features.blend-shapes.enable_action"),
                () => disabledBlendShapeList.SelectedVisibleIndices(),
                indices => AddAvailable(indices));
            enableVisibleButton = BlendShareInspectorUi.SmallButton(Localization.S("features.blend-shapes.enable_visible"), () => AddAvailable(visibleAvailableIndices));
            enableAllButton = BlendShareInspectorUi.SmallButton(Localization.S("features.blend-shapes.enable_all"), () => ApplyEditedActiveIndices(Enumerable.Range(0, feature.BlendShapes.Count), "Enable BlendShapes"));
            row.Add(enableVisibleButton);
            row.Add(disabledSelectedActionButton);
            row.Add(enableAllButton);
            return row;
        }

        private static Button CreateSelectedActionButton(
            string actionLabel,
            Func<IEnumerable<int>> getSelection,
            Action<IEnumerable<int>> applyAction)
        {
            var button = BlendShareInspectorUi.SmallButton(string.Empty, () => applyAction?.Invoke(getSelection?.Invoke() ?? Array.Empty<int>()));
            button.userData = actionLabel;
            button.SetEnabled(false);
            return button;
        }

        private void RefreshSelectedActionButtons()
        {
            muteVisibleButton?.SetEnabled(IsFilterActive() && visibleActiveIndices.Count > 0);
            enableVisibleButton?.SetEnabled(visibleAvailableIndices.Count > 0);
            enableAllButton?.SetEnabled(activeIndices.Count < feature.BlendShapes.Count);
            RefreshSelectedActionButton(activeSelectedActionButton, activeBlendShapeList?.SelectedVisibleCount() ?? 0);
            RefreshSelectedActionButton(disabledSelectedActionButton, disabledBlendShapeList?.SelectedVisibleCount() ?? 0);
        }

        private static void RefreshSelectedActionButton(Button button, int selectedCount)
        {
            if (button == null)
            {
                return;
            }

            string actionLabel = button.userData as string ?? Localization.S("common.apply");
            button.text = Localization.SF("features.blend-shapes.selection.selected_action", actionLabel, selectedCount);
            button.SetEnabled(selectedCount > 0);
        }

        private string GetActiveListHeader()
        {
            return IsFilterActive()
                ? Localization.S("features.blend-shapes.selection.active_header_filtered")
                : Localization.S("features.blend-shapes.selection.active_header");
        }

        private void RefreshVisibleAvailableIndices()
        {
            visibleAvailableIndices.Clear();

            var active = new HashSet<int>(activeIndices);
            string search = GetFilterText();
            for (int i = 0; i < feature.BlendShapes.Count; i++)
            {
                if (active.Contains(i))
                {
                    continue;
                }

                string name = GetBlendShapeName(i);
                if (!string.IsNullOrWhiteSpace(search) && name.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                visibleAvailableIndices.Add(i);
            }

            RefreshStatus();
        }

        private void RefreshVisibleActiveIndices()
        {
            visibleActiveIndices.Clear();
            string search = GetFilterText();
            foreach (int index in activeIndices)
            {
                string name = GetBlendShapeName(index);
                if (string.IsNullOrWhiteSpace(search) || name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    visibleActiveIndices.Add(index);
                }
            }
        }

        private void AddAvailable(IEnumerable<int> indices)
        {
            var next = new List<int>(activeIndices);
            foreach (int index in indices ?? Enumerable.Empty<int>())
            {
                if (index >= 0 && index < feature.BlendShapes.Count && !next.Contains(index))
                {
                    next.Add(index);
                }
            }

            ApplyEditedActiveIndices(next, "Enable BlendShapes");
        }

        private void MuteActive(IEnumerable<int> indices)
        {
            var muted = new HashSet<int>(indices ?? Enumerable.Empty<int>());
            if (muted.Count == 0)
            {
                return;
            }

            var next = activeIndices
                .Where(index => !muted.Contains(index))
                .ToList();
            activeBlendShapeList.RemoveSelection(muted);

            ApplyEditedActiveIndices(next, muted.Count == 1 ? "Mute BlendShape" : "Mute BlendShapes");
        }

        private void ApplySelectionChoice(string choice)
        {
            if (choice == DefaultChoice)
            {
                ApplyActiveIndices(Enumerable.Range(0, feature.BlendShapes.Count), string.Empty, "Apply BlendShape Selection");
                return;
            }

            if (choice == UnsavedChoice)
            {
                RefreshSelectionSetUi();
                return;
            }

            var selection = FindSelectionByPopupChoice(choice);
            if (selection != null)
            {
                ApplyActiveIndices(selection.m_OrderedBlendShapeIndices, selection.m_Id, "Apply BlendShape Selection Set");
            }
        }

        private void SaveCurrentSet()
        {
            string activeId = feature.ActiveSelectionSetId;
            if (string.IsNullOrWhiteSpace(activeId))
            {
                SaveAsNewSet();
                return;
            }

            SaveSet(activeId);
        }

        private void SaveAsNewSet()
        {
            SaveSet(null);
        }

        private void SaveSet(string existingId)
        {
            var existing = feature.GetSelectionSet(existingId);
            string defaultName = existing != null ? existing.DisplayName : GetNextSetName();
            string setName = SelectionSetNamePrompt.Show(Localization.S("features.blend-shapes.selection.name_prompt"), defaultName);
            if (string.IsNullOrWhiteSpace(setName))
            {
                return;
            }

            setName = setName.Trim();
            if (string.IsNullOrWhiteSpace(setName))
            {
                return;
            }

            Undo.RecordObject(feature, string.IsNullOrWhiteSpace(existingId) ? "Save BlendShape Selection Set" : "Update BlendShape Selection Set");
            var selection = feature.SaveSelectionSet(setName, existingId);
            EditorUtility.SetDirty(feature);
            RefreshAfterSelectionChange(selection?.DisplayName);
            refreshOwner?.Invoke();
        }

        private void RevertCurrentSet()
        {
            var selection = feature.GetSelectionSet(feature.ActiveSelectionSetId);
            if (selection != null)
            {
                ApplyActiveIndices(selection.m_OrderedBlendShapeIndices, selection.m_Id, "Revert BlendShape Selection Set");
                return;
            }

            ApplyActiveIndices(Enumerable.Range(0, feature.BlendShapes.Count), string.Empty, "Revert to Default BlendShape Selection");
        }

        private void DeleteCurrentSet()
        {
            var selection = feature.GetSelectionSet(feature.ActiveSelectionSetId);
            if (selection == null)
            {
                return;
            }

            if (!EditorUtility.DisplayDialog(
                    Localization.S("features.blend-shapes.selection.delete_title"),
                    Localization.SF("features.blend-shapes.selection.delete_message", selection.DisplayName),
                    Localization.S("common.delete"),
                    Localization.S("common.cancel")))
            {
                return;
            }

            Undo.RecordObject(feature, "Delete BlendShape Selection Set");
            feature.DeleteSelectionSet(selection.m_Id);
            EditorUtility.SetDirty(feature);
            RefreshAfterSelectionChange(UnsavedChoice);
            refreshOwner?.Invoke();
        }

        private void ApplyActiveIndices(IEnumerable<int> indices, string selectionSetId, string undoName)
        {
            Undo.RecordObject(feature, undoName);
            feature.SetWorkingSelection(indices, selectionSetId);
            EditorUtility.SetDirty(feature);
            RefreshAfterSelectionChange();
        }

        private void ApplyEditedActiveIndices(IEnumerable<int> indices, string undoName)
        {
            var activeSet = feature.GetSelectionSet(feature.ActiveSelectionSetId);
            ApplyActiveIndices(indices, activeSet != null ? activeSet.m_Id : string.Empty, undoName);
        }

        private void RefreshAfterSelectionChange(string selectedChoice = null)
        {
            feature.SanitizeShapeNames();
            RefreshActiveCache();
            RefreshVisibleActiveIndices();
            activeBlendShapeList.Rebuild(!lockedMode && !IsFilterActive(), lockedMode ? null : indices => ApplyEditedActiveIndices(indices, "Reorder BlendShapes"));
            if (!lockedMode)
            {
                RefreshSelectionSetUi(selectedChoice);
            }

            RefreshVisibleAvailableIndices();
            disabledBlendShapeList?.Rebuild(false, null);
            RefreshStatus();
            RefreshSelectedActionButtons();
        }

        private void RefreshActiveCache()
        {
            activeIndices.Clear();
            activeIndices.AddRange(feature.ActiveBlendShapeIndices);
            activeBlendShapeList?.PruneSelection(activeIndices);
            disabledBlendShapeList?.PruneSelection(Enumerable.Range(0, feature.BlendShapes.Count).Except(activeIndices));
        }

        private void RefreshSelectionSetUi(string selectedChoice = null)
        {
            var choices = BuildPopupChoices();
            setPopup.choices = choices;
            string choice = selectedChoice != null && choices.Contains(selectedChoice) ? selectedChoice : GetCurrentPopupChoice();
            setPopup.SetValueWithoutNotify(choice);
            RefreshSelectionSetButtons(choice);
        }

        private void RefreshSelectionSetButtons(string choice)
        {
            bool hasSavedSet = feature.GetSelectionSet(feature.ActiveSelectionSetId) != null;
            saveButton?.SetEnabled(hasSavedSet);
            saveAsButton?.SetEnabled(true);
            revertButton?.SetEnabled(choice != DefaultChoice);
            deleteButton?.SetEnabled(hasSavedSet);
        }

        private void RefreshStatus()
        {
            if (lockedMode)
            {
                statusLabel.text = Localization.SF("features.blend-shapes.status.locked", activeIndices.Count, feature.BlendShapes.Count);
                return;
            }

            string choice = GetCurrentPopupChoice();
            statusLabel.text = Localization.SF("features.blend-shapes.status.editing", activeIndices.Count, feature.BlendShapes.Count, visibleActiveIndices.Count, visibleAvailableIndices.Count, choice);
        }

        private List<string> BuildPopupChoices()
        {
            var choices = new List<string> { DefaultChoice };
            choices.AddRange(feature.SelectionSets.Select(GetSelectionPopupName));
            if (GetCurrentPopupChoice() == UnsavedChoice)
            {
                choices.Add(UnsavedChoice);
            }

            return choices;
        }

        private string GetCurrentPopupChoice()
        {
            var activeSet = feature.GetSelectionSet(feature.ActiveSelectionSetId);
            if (activeSet != null)
            {
                return GetSelectionPopupName(activeSet);
            }

            if (IsDefaultAllSelection())
            {
                return DefaultChoice;
            }

            return UnsavedChoice;
        }

        private string GetSelectionPopupName(BlendShapeSelectionState selection)
        {
            if (selection == null)
            {
                return string.Empty;
            }

            string name = selection.DisplayName;
            return selection.m_Id == feature.ActiveSelectionSetId && !feature.WorkingSelectionMatches(selection)
                ? $"{name}*"
                : name;
        }

        private bool IsDefaultAllSelection()
        {
            return activeIndices.SequenceEqual(Enumerable.Range(0, feature.BlendShapes.Count));
        }

        private BlendShapeSelectionState FindSelectionByPopupChoice(string displayName)
        {
            string normalized = displayName?.TrimEnd();
            if (!string.IsNullOrEmpty(normalized) && normalized.EndsWith("*", StringComparison.Ordinal))
            {
                normalized = normalized.Substring(0, normalized.Length - 1).TrimEnd();
            }

            return feature.SelectionSets.FirstOrDefault(selection => selection.DisplayName == normalized);
        }

        private bool IsFilterActive()
        {
            return !string.IsNullOrWhiteSpace(GetFilterText());
        }

        private string GetFilterText()
        {
            return filterField?.value ?? string.Empty;
        }

        private string GetNextSetName()
        {
            string baseName = Localization.S("features.blend-shapes.selection.default_name");
            var usedNames = new HashSet<string>(feature.SelectionSets.Select(selection => selection.DisplayName));
            if (!usedNames.Contains(baseName))
            {
                return baseName;
            }

            for (int i = 2; ; i++)
            {
                string candidate = $"{baseName} {i}";
                if (!usedNames.Contains(candidate))
                {
                    return candidate;
                }
            }
        }

        private string GetBlendShapeName(int index)
        {
            return index >= 0 && index < feature.BlendShapes.Count
                ? feature.BlendShapes[index]?.m_Name ?? Localization.SF("features.blend-shapes.fallback_name", index)
                : Localization.SF("features.blend-shapes.fallback_name", index);
        }

    }

    internal sealed class BlendShapeListView
    {
        private readonly List<int> visibleIndices;
        private readonly Func<int, string> getName;
        private readonly string actionLabel;
        private readonly Action<IEnumerable<int>> applyAction;
        private readonly bool showHeader;
        private readonly Func<string> getHeader;
        private readonly Action selectionChanged;
        private readonly HashSet<int> selectedBlendShapeIndices = new();
        private readonly IMGUIContainer element;
        private ReorderableList list;
        private int lastSelectedVisibleIndex = -1;
        private bool hasMultiSelection;
        private bool isDraggable;

        public BlendShapeListView(
            List<int> visibleIndices,
            Func<int, string> getName,
            string actionLabel,
            Action<IEnumerable<int>> applyAction,
            bool showHeader,
            Func<string> getHeader,
            Action selectionChanged)
        {
            this.visibleIndices = visibleIndices;
            this.getName = getName;
            this.actionLabel = actionLabel;
            this.applyAction = applyAction;
            this.showHeader = showHeader;
            this.getHeader = getHeader;
            this.selectionChanged = selectionChanged;
            element = new IMGUIContainer(Draw);
        }

        public VisualElement Element => element;

        public void Rebuild(bool draggable, Action<IEnumerable<int>> applyReorder)
        {
            isDraggable = draggable;
            list = new ReorderableList(visibleIndices, typeof(int), draggable, showHeader, false, false)
            {
                elementHeight = EditorGUIUtility.singleLineHeight + 4,
                footerHeight = 0,
                drawHeaderCallback = rect => EditorGUI.LabelField(rect, getHeader?.Invoke() ?? string.Empty),
                drawElementCallback = DrawElement,
                onReorderCallback = _ => applyReorder?.Invoke(visibleIndices)
            };
        }

        public IEnumerable<int> SelectedVisibleIndices()
        {
            return selectedBlendShapeIndices
                .Where(index => visibleIndices.Contains(index))
                .ToArray();
        }

        public int SelectedVisibleCount()
        {
            return selectedBlendShapeIndices.Count(index => visibleIndices.Contains(index));
        }

        public void RemoveSelection(IEnumerable<int> indices)
        {
            selectedBlendShapeIndices.ExceptWith(indices ?? Enumerable.Empty<int>());
            if (selectedBlendShapeIndices.Count == 0)
            {
                lastSelectedVisibleIndex = -1;
                hasMultiSelection = false;
            }

            selectionChanged?.Invoke();
        }

        public void PruneSelection(IEnumerable<int> validIndices)
        {
            var valid = new HashSet<int>(validIndices ?? Enumerable.Empty<int>());
            selectedBlendShapeIndices.RemoveWhere(index => !valid.Contains(index));
            hasMultiSelection = selectedBlendShapeIndices.Count > 1;
            if (selectedBlendShapeIndices.Count == 0)
            {
                lastSelectedVisibleIndex = -1;
            }

            selectionChanged?.Invoke();
        }

        private void Draw()
        {
            list?.DoLayoutList();
        }

        private void DrawElement(Rect rect, int visibleIndex, bool isActive, bool isFocused)
        {
            if (visibleIndex < 0 || visibleIndex >= visibleIndices.Count)
            {
                return;
            }

            int blendShapeIndex = visibleIndices[visibleIndex];
            bool hasAction = !string.IsNullOrEmpty(actionLabel) && applyAction != null;
            float actionWidth = hasAction ? 56f : 0f;
            const float listElementMargin = 6f;
            float handleWidth = isDraggable ? 14f : 0f;
            var selectionRect = new Rect(
                rect.x - listElementMargin - handleWidth,
                rect.y,
                rect.width + handleWidth + listElementMargin * 2,
                rect.height);
            var actionRect = new Rect(rect.xMax - actionWidth, rect.y + 2, actionWidth, EditorGUIUtility.singleLineHeight);
            var labelRect = new Rect(rect.x, rect.y + 2, rect.width - actionWidth - (hasAction ? 6 : 0), EditorGUIUtility.singleLineHeight);

            if (selectedBlendShapeIndices.Contains(blendShapeIndex))
            {
                EditorGUI.DrawRect(selectionRect, SelectionColor());
            }

            if (selectionChanged != null)
            {
                HandleSelection(selectionRect, actionRect, visibleIndex, blendShapeIndex);
            }
            EditorGUI.LabelField(labelRect, getName(blendShapeIndex));
            if (hasAction && GUI.Button(actionRect, actionLabel, EditorStyles.miniButton))
            {
                applyAction?.Invoke(new[] { blendShapeIndex });
            }
        }

        private void HandleSelection(Rect rowRect, Rect actionRect, int visibleIndex, int blendShapeIndex)
        {
            Event evt = Event.current;
            if (evt.type != EventType.MouseDown || evt.button != 0 || !rowRect.Contains(evt.mousePosition) || actionRect.Contains(evt.mousePosition))
            {
                return;
            }

            if (evt.shift && lastSelectedVisibleIndex >= 0)
            {
                int start = Mathf.Clamp(Math.Min(lastSelectedVisibleIndex, visibleIndex), 0, visibleIndices.Count - 1);
                int end = Mathf.Clamp(Math.Max(lastSelectedVisibleIndex, visibleIndex), 0, visibleIndices.Count - 1);
                for (int i = start; i <= end; i++)
                {
                    selectedBlendShapeIndices.Add(visibleIndices[i]);
                }

                hasMultiSelection = selectedBlendShapeIndices.Count > 1;
            }
            else if (evt.command || evt.control)
            {
                if (!selectedBlendShapeIndices.Remove(blendShapeIndex))
                {
                    selectedBlendShapeIndices.Add(blendShapeIndex);
                }

                lastSelectedVisibleIndex = visibleIndex;
                hasMultiSelection = selectedBlendShapeIndices.Count > 1;
            }
            else
            {
                selectedBlendShapeIndices.Clear();
                selectedBlendShapeIndices.Add(blendShapeIndex);
                lastSelectedVisibleIndex = visibleIndex;
                hasMultiSelection = false;
            }

            list.index = visibleIndex;
            selectionChanged?.Invoke();
        }

        private static Color SelectionColor()
        {
            return EditorGUIUtility.isProSkin
                ? new Color(0.24f, 0.38f, 0.56f, 0.55f)
                : new Color(0.30f, 0.55f, 0.88f, 0.35f);
        }
    }

    internal sealed class SelectionSetNamePrompt : EditorWindow
    {
        private string value;
        private bool submitted;

        public static string Show(string title, string defaultValue)
        {
            var window = CreateInstance<SelectionSetNamePrompt>();
            window.titleContent = new GUIContent(title);
            window.value = defaultValue ?? string.Empty;
            window.minSize = new Vector2(320, 76);
            window.maxSize = new Vector2(520, 76);
            window.ShowModalUtility();
            return window.submitted ? window.value : null;
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField(Localization.S("common.name"));
            GUI.SetNextControlName("SelectionSetName");
            value = EditorGUILayout.TextField(value);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(Localization.S("common.cancel"), GUILayout.Width(80)))
                {
                    submitted = false;
                    Close();
                }

                using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(value)))
                {
                    if (GUILayout.Button(Localization.S("common.save"), GUILayout.Width(80)))
                    {
                        submitted = true;
                        Close();
                    }
                }
            }

            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return && !string.IsNullOrWhiteSpace(value))
            {
                submitted = true;
                Close();
            }
        }

        private void OnEnable()
        {
            EditorApplication.delayCall += FocusTextField;
        }

        private void OnDisable()
        {
            EditorApplication.delayCall -= FocusTextField;
        }

        private void FocusTextField()
        {
            if (this != null)
            {
                Focus();
                EditorGUI.FocusTextInControl("SelectionSetName");
            }
        }
    }
}
