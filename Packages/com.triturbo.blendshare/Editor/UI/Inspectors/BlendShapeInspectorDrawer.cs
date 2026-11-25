using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.UI;
using Triturbo.BlendShare.EditorAssets.Settings;
using Triturbo.BlendShare.Model.Features;
using Triturbo.BlendShare.Model.Settings;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.UIElements;

namespace Triturbo.BlendShare.UI.Inspectors
{
    /// <summary>
    /// Verbatim port of the original <c>BlendShapeFeatureObjectEditor</c> / <c>BlendShapeFeatureEditorElement</c>
    /// (preserving the exact lockable header, Selection Set controls, filter icon, multi-select
    /// <see cref="ReorderableList"/>, and collapsible disabled list) wired strictly to BlendShare 2.0
    /// <see cref="BlendShapeFeature"/> + <see cref="BlendShapeFeatureSettings"/> via <see cref="BlendShareSettingsUndoBuffer"/>.
    /// </summary>
    public sealed class BlendShapeInspectorDrawer : IFeatureInspectorDrawer
    {
        private const string k_LockedPrefKey = "blendshapes.locked";
        private const bool k_DefaultLocked = true;

        public string FeatureTypeId => BlendShapeFeature.FeatureTypeId;
        public int DisplayOrder => 10;

        private static string DisplayName => Localization.FeatureName("blend-shapes");

        public VisualElement CreateInspectorElement(BlendShareFeatureData feature, FeatureInspectorContext context)
        {
            var bsFeature = feature as BlendShapeFeature;
            bool locked = context.IsReadOnly ||
                          BlendShareInspectorUtility.GetAssetEditorPrefBool(context.PatchAsset, k_LockedPrefKey, k_DefaultLocked);
            return CreateBoxedLockableElement(
                bsFeature,
                context,
                locked,
                value => BlendShareInspectorUtility.SetAssetEditorPrefBool(context.PatchAsset, k_LockedPrefKey, value, k_DefaultLocked),
                !context.IsReadOnly);
        }

        public VisualElement CreateCompactBadgeElement(BlendShareFeatureData feature, FeatureInspectorContext context)
        {
            var bsFeature = feature as BlendShapeFeature;
            if (bsFeature == null)
            {
                return new BlendShareFeatureBadge(DisplayName);
            }

            var selection = new BlendShapeWorkingSelection(bsFeature, context);
            string label = Localization.SF("features.blend-shapes.status.compact", selection.Active.Count, selection.Ids.Count);
            return new BlendShareFeatureBadge(
                label,
                BlendShapeFeatureEditorElement.Create(bsFeature, context, lockedMode: true, showActiveListHeader: false, includeAdvanced: false));
        }

        private static VisualElement CreateBoxedLockableElement(
            BlendShapeFeature feature,
            FeatureInspectorContext context,
            bool initialLocked,
            Action<bool> lockChanged,
            bool showLockButton)
        {
            var box = BlendShareInspectorUi.Box();
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
                lockButton.tooltip = locked
                    ? Localization.S("features.blend-shapes.unlock_tooltip")
                    : Localization.S("features.blend-shapes.lock_tooltip");
                content.Clear();
                content.Add(BlendShapeFeatureEditorElement.Create(feature, context, locked, showActiveListHeader: true, includeAdvanced: true));
            }

            Rebuild();
            return box;
        }

        private static Button CreateLockButton(Action clicked)
        {
            var button = new Button(clicked);
            button.name = "selection-lock";
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

    public sealed class BlendShapeFeatureEditorElement : VisualElement
    {
        private readonly BlendShapeFeature m_Feature;
        private readonly BlendShapeWorkingSelection m_Selection;
        private readonly FeatureInspectorContext m_Context;
        private readonly TextField m_FilterField;
        private readonly PopupField<string> m_SetPopup;
        private readonly Label m_StatusLabel;
        private Button m_SaveButton;
        private Button m_SaveAsButton;
        private Button m_RevertButton;
        private Button m_DeleteButton;
        private Button m_MuteVisibleButton;
        private Button m_EnableVisibleButton;
        private Button m_EnableAllButton;
        private Button m_ActiveSelectedActionButton;
        private Button m_DisabledSelectedActionButton;
        private readonly Foldout m_DisabledFoldout;
        private readonly List<string> m_ActiveIds = new List<string>();
        private readonly List<string> m_VisibleActiveIds = new List<string>();
        private readonly List<string> m_VisibleAvailableIds = new List<string>();
        private readonly BlendShapeListView m_ActiveBlendShapeList;
        private readonly BlendShapeListView m_DisabledBlendShapeList;
        private readonly bool m_LockedMode;

        private static string DefaultChoice => Localization.S("features.blend-shapes.selection.default");
        private static string UnsavedChoice => Localization.S("features.blend-shapes.selection.unsaved");

        private BlendShapeFeatureEditorElement(
            BlendShapeFeature feature,
            FeatureInspectorContext context,
            bool lockedMode,
            bool showActiveListHeader,
            bool includeAdvanced)
        {
            m_Feature = feature;
            m_Context = context;
            m_LockedMode = lockedMode;

            if (m_Feature == null)
            {
                Add(new HelpBox(Localization.S("features.blend-shapes.missing_data"), HelpBoxMessageType.Warning));
                return;
            }

            m_Selection = new BlendShapeWorkingSelection(m_Feature, m_Context);
            m_ActiveIds.Clear();
            m_ActiveIds.AddRange(m_Selection.Active);
            RefreshVisibleActiveIds();

            if (!m_LockedMode)
            {
                m_SetPopup = new PopupField<string>(Localization.S("features.blend-shapes.selection_set"), BuildPopupChoices(), GetCurrentPopupChoice());
                m_SetPopup.RegisterValueChangedCallback(evt => ApplySelectionChoice(evt.newValue));
                Add(m_SetPopup);

                Add(CreateSelectionSetButtons());

                m_FilterField = new TextField(Localization.S("common.filter")) { name = "shape-filter" };
                AddFilterIcon(m_FilterField);
                m_FilterField.RegisterValueChangedCallback(_ => RefreshAfterSelectionChange());
                Add(m_FilterField);
                Add(CreateMuteButtons());
            }

            m_StatusLabel = new Label();
            m_StatusLabel.style.opacity = 0.75f;
            Add(m_StatusLabel);

            m_ActiveBlendShapeList = new BlendShapeListView(
                m_VisibleActiveIds,
                id => m_Selection.Name(id),
                m_LockedMode ? null : Localization.S("features.blend-shapes.mute"),
                m_LockedMode ? null : ids => MuteActive(ids),
                showActiveListHeader,
                GetActiveListHeader,
                m_LockedMode ? null : RefreshSelectedActionButtons);
            m_ActiveBlendShapeList.Element.style.marginTop = 6;
            Add(m_ActiveBlendShapeList.Element);

            if (!m_LockedMode)
            {
                m_DisabledBlendShapeList = new BlendShapeListView(
                    m_VisibleAvailableIds,
                    id => m_Selection.Name(id),
                    Localization.S("features.blend-shapes.enable_action"),
                    ids => AddAvailable(ids),
                    false,
                    null,
                    RefreshSelectedActionButtons);
                m_DisabledFoldout = new Foldout { text = Localization.S("features.blend-shapes.selection.disabled_header"), value = false };
                m_DisabledFoldout.style.marginLeft = 0;
                m_DisabledFoldout.style.paddingLeft = 10;
                m_DisabledFoldout.contentContainer.style.marginLeft = 0;
                m_DisabledFoldout.contentContainer.style.paddingLeft = 0;
                m_DisabledFoldout.Add(CreateEnableButtons());
                m_DisabledFoldout.Add(m_DisabledBlendShapeList.Element);
                Add(m_DisabledFoldout);
            }

            m_ActiveBlendShapeList.Rebuild(
                !m_LockedMode && !IsFilterActive(),
                m_LockedMode ? null : ids => ApplyEditedActiveIds(ids, "Reorder BlendShapes"));
            RefreshVisibleAvailableIds();
            m_DisabledBlendShapeList?.Rebuild(false, null);

            if (!m_LockedMode)
            {
                RefreshSelectionSetUi();
            }

            RefreshStatus();
            RefreshSelectedActionButtons();

            if (includeAdvanced)
            {
                Add(m_Context.Advanced(m_Feature));
            }
        }

        public static VisualElement Create(
            BlendShapeFeature feature,
            FeatureInspectorContext context,
            bool lockedMode,
            bool showActiveListHeader,
            bool includeAdvanced)
        {
            return new BlendShapeFeatureEditorElement(feature, context, lockedMode, showActiveListHeader, includeAdvanced);
        }

        public bool IsActiveListDraggable => m_ActiveBlendShapeList != null && m_ActiveBlendShapeList.IsDraggable;
        public IReadOnlyList<string> VisibleActiveIds => m_VisibleActiveIds;

        private VisualElement CreateSelectionSetButtons()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.flexWrap = Wrap.Wrap;
            m_SaveButton = BlendShareInspectorUi.SmallButton(Localization.S("common.save"), SaveCurrentSet);
            m_SaveAsButton = BlendShareInspectorUi.SmallButton(Localization.S("common.save_as"), SaveAsNewSet);
            m_RevertButton = BlendShareInspectorUi.SmallButton(Localization.S("common.revert"), RevertCurrentSet);
            m_DeleteButton = BlendShareInspectorUi.SmallButton(Localization.S("common.delete"), DeleteCurrentSet);
            row.Add(m_SaveButton);
            row.Add(m_SaveAsButton);
            row.Add(m_RevertButton);
            row.Add(m_DeleteButton);
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
            m_ActiveSelectedActionButton = CreateSelectedActionButton(
                Localization.S("features.blend-shapes.mute"),
                () => m_ActiveBlendShapeList.SelectedVisibleIds(),
                MuteActive);
            m_MuteVisibleButton = BlendShareInspectorUi.SmallButton(
                Localization.S("features.blend-shapes.mute_visible"),
                () => MuteActive(m_VisibleActiveIds));
            row.Add(m_MuteVisibleButton);
            row.Add(m_ActiveSelectedActionButton);
            return row;
        }

        private VisualElement CreateEnableButtons()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.flexWrap = Wrap.Wrap;
            m_DisabledSelectedActionButton = CreateSelectedActionButton(
                Localization.S("features.blend-shapes.enable_action"),
                () => m_DisabledBlendShapeList.SelectedVisibleIds(),
                AddAvailable);
            m_EnableVisibleButton = BlendShareInspectorUi.SmallButton(
                Localization.S("features.blend-shapes.enable_visible"),
                () => AddAvailable(m_VisibleAvailableIds));
            m_EnableAllButton = BlendShareInspectorUi.SmallButton(
                Localization.S("features.blend-shapes.enable_all"),
                () => ApplyEditedActiveIds(m_Selection.Ids, "Enable BlendShapes"));
            row.Add(m_EnableVisibleButton);
            row.Add(m_DisabledSelectedActionButton);
            row.Add(m_EnableAllButton);
            return row;
        }

        private static Button CreateSelectedActionButton(
            string actionLabel,
            Func<IEnumerable<string>> getSelection,
            Action<IEnumerable<string>> applyAction)
        {
            var button = BlendShareInspectorUi.SmallButton(string.Empty, () => applyAction?.Invoke(getSelection?.Invoke() ?? Array.Empty<string>()));
            button.userData = actionLabel;
            button.SetEnabled(false);
            return button;
        }

        private void RefreshSelectedActionButtons()
        {
            m_MuteVisibleButton?.SetEnabled(IsFilterActive() && m_VisibleActiveIds.Count > 0);
            m_EnableVisibleButton?.SetEnabled(m_VisibleAvailableIds.Count > 0);
            m_EnableAllButton?.SetEnabled(m_ActiveIds.Count < m_Selection.Ids.Count);
            RefreshSelectedActionButton(m_ActiveSelectedActionButton, m_ActiveBlendShapeList?.SelectedVisibleCount() ?? 0);
            RefreshSelectedActionButton(m_DisabledSelectedActionButton, m_DisabledBlendShapeList?.SelectedVisibleCount() ?? 0);
        }

        private static void RefreshSelectedActionButton(Button button, int selectedCount)
        {
            if (button == null) return;
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

        private void RefreshVisibleAvailableIds()
        {
            m_VisibleAvailableIds.Clear();
            var active = new HashSet<string>(m_ActiveIds, StringComparer.Ordinal);
            string search = GetFilterText();
            for (int i = 0; i < m_Selection.Ids.Count; i++)
            {
                string id = m_Selection.Ids[i];
                if (active.Contains(id)) continue;
                string name = m_Selection.Name(id);
                if (!string.IsNullOrWhiteSpace(search) && name.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                m_VisibleAvailableIds.Add(id);
            }

            RefreshStatus();
        }

        private void RefreshVisibleActiveIds()
        {
            m_VisibleActiveIds.Clear();
            string search = GetFilterText();
            for (int i = 0; i < m_ActiveIds.Count; i++)
            {
                string id = m_ActiveIds[i];
                string name = m_Selection.Name(id);
                if (string.IsNullOrWhiteSpace(search) || name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    m_VisibleActiveIds.Add(id);
                }
            }
        }

        private void AddAvailable(IEnumerable<string> ids)
        {
            var next = new List<string>(m_ActiveIds);
            foreach (string id in ids ?? Enumerable.Empty<string>())
            {
                if (!string.IsNullOrEmpty(id) && !next.Contains(id))
                {
                    next.Add(id);
                }
            }

            ApplyEditedActiveIds(next, "Enable BlendShapes");
        }

        private void MuteActive(IEnumerable<string> ids)
        {
            var muted = new HashSet<string>(ids ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            if (muted.Count == 0) return;

            var next = m_ActiveIds.Where(id => !muted.Contains(id)).ToList();
            m_ActiveBlendShapeList.RemoveSelection(muted);
            ApplyEditedActiveIds(next, muted.Count == 1 ? "Mute BlendShape" : "Mute BlendShapes");
        }

        private void ApplySelectionChoice(string choice)
        {
            if (choice == DefaultChoice)
            {
                m_Selection.Switch(string.Empty);
                RefreshAfterSelectionChange();
                return;
            }

            if (choice == UnsavedChoice)
            {
                RefreshSelectionSetUi();
                return;
            }

            var selectionSet = FindSelectionByPopupChoice(choice);
            if (selectionSet != null)
            {
                m_Selection.Switch(selectionSet.SetId);
                RefreshAfterSelectionChange();
            }
        }

        private void SaveCurrentSet()
        {
            var existing = m_Selection.CurrentSet;
            if (existing == null)
            {
                SaveAsNewSet();
                return;
            }

            SaveSet(asNew: false);
        }

        private void SaveAsNewSet()
        {
            SaveSet(asNew: true);
        }

        private void SaveSet(bool asNew)
        {
            var existing = m_Selection.CurrentSet;
            string defaultName = !asNew && existing != null ? existing.DisplayName : GetNextSetName();
            string setName = SelectionSetNamePrompt.Show(Localization.S("features.blend-shapes.selection.name_prompt"), defaultName);
            if (string.IsNullOrWhiteSpace(setName)) return;

            setName = setName.Trim();
            if (string.IsNullOrWhiteSpace(setName)) return;

            m_Selection.Save(setName, asNew);
            RefreshAfterSelectionChange(setName);
        }

        private void RevertCurrentSet()
        {
            var current = m_Selection.CurrentSet;
            m_Selection.Switch(current != null ? current.SetId : string.Empty);
            RefreshAfterSelectionChange();
        }

        private void DeleteCurrentSet()
        {
            var current = m_Selection.CurrentSet;
            if (current == null) return;

            if (!EditorUtility.DisplayDialog(
                    Localization.S("features.blend-shapes.selection.delete_title"),
                    Localization.SF("features.blend-shapes.selection.delete_message", current.DisplayName),
                    Localization.S("common.delete"),
                    Localization.S("common.cancel")))
            {
                return;
            }

            m_Selection.Delete();
            RefreshAfterSelectionChange(UnsavedChoice);
        }

        private void ApplyEditedActiveIds(IEnumerable<string> ids, string undoName)
        {
            m_Selection.SetActive(ids, undoName);
            RefreshAfterSelectionChange();
        }

        private void RefreshAfterSelectionChange(string selectedChoice = null)
        {
            RefreshActiveCache();
            RefreshVisibleActiveIds();
            m_ActiveBlendShapeList.Rebuild(
                !m_LockedMode && !IsFilterActive(),
                m_LockedMode ? null : ids => ApplyEditedActiveIds(ids, "Reorder BlendShapes"));
            if (!m_LockedMode)
            {
                RefreshSelectionSetUi(selectedChoice);
            }

            RefreshVisibleAvailableIds();
            m_DisabledBlendShapeList?.Rebuild(false, null);
            RefreshStatus();
            RefreshSelectedActionButtons();
        }

        private void RefreshActiveCache()
        {
            m_ActiveIds.Clear();
            m_ActiveIds.AddRange(m_Selection.Active);
            m_ActiveBlendShapeList?.PruneSelection(m_ActiveIds);
            m_DisabledBlendShapeList?.PruneSelection(m_Selection.Ids.Except(m_ActiveIds));
        }

        private void RefreshSelectionSetUi(string selectedChoice = null)
        {
            var choices = BuildPopupChoices();
            m_SetPopup.choices = choices;
            string choice = selectedChoice != null && choices.Contains(selectedChoice) ? selectedChoice : GetCurrentPopupChoice();
            m_SetPopup.SetValueWithoutNotify(choice);
            RefreshSelectionSetButtons(choice);
        }

        private void RefreshSelectionSetButtons(string choice)
        {
            bool hasSavedSet = m_Selection.CurrentSet != null;
            m_SaveButton?.SetEnabled(hasSavedSet);
            m_SaveAsButton?.SetEnabled(true);
            m_RevertButton?.SetEnabled(choice != DefaultChoice);
            m_DeleteButton?.SetEnabled(hasSavedSet);
        }

        private void RefreshStatus()
        {
            if (m_LockedMode)
            {
                m_StatusLabel.text = Localization.SF("features.blend-shapes.status.locked", m_ActiveIds.Count, m_Selection.Ids.Count);
                return;
            }

            string choice = GetCurrentPopupChoice();
            m_StatusLabel.text = Localization.SF(
                "features.blend-shapes.status.editing",
                m_ActiveIds.Count,
                m_Selection.Ids.Count,
                m_VisibleActiveIds.Count,
                m_VisibleAvailableIds.Count,
                choice);
        }

        private List<string> BuildPopupChoices()
        {
            var choices = new List<string> { DefaultChoice };
            choices.AddRange(m_Selection.Settings.SelectionSets.Where(s => s != null).Select(GetSelectionPopupName));
            if (GetCurrentPopupChoice() == UnsavedChoice)
            {
                choices.Add(UnsavedChoice);
            }

            return choices;
        }

        private string GetCurrentPopupChoice()
        {
            var activeSet = m_Selection.CurrentSet;
            if (activeSet != null)
            {
                return GetSelectionPopupName(activeSet);
            }

            return m_ActiveIds.SequenceEqual(m_Selection.Ids) ? DefaultChoice : UnsavedChoice;
        }

        private string GetSelectionPopupName(BlendShapeSelectionSet selection)
        {
            if (selection == null) return string.Empty;
            string name = selection.DisplayName;
            return string.Equals(selection.SetId, m_Selection.Settings.ActiveSelectionSetId, StringComparison.Ordinal) &&
                   !m_Selection.Settings.WorkingSelectionMatches(selection, m_Selection.Ids)
                ? $"{name}*"
                : name;
        }

        private BlendShapeSelectionSet FindSelectionByPopupChoice(string displayName)
        {
            string normalized = displayName?.TrimEnd();
            if (!string.IsNullOrEmpty(normalized) && normalized.EndsWith("*", StringComparison.Ordinal))
            {
                normalized = normalized.Substring(0, normalized.Length - 1).TrimEnd();
            }

            return m_Selection.Settings.SelectionSets.FirstOrDefault(s => s != null && string.Equals(s.DisplayName, normalized, StringComparison.Ordinal));
        }

        private bool IsFilterActive() => !string.IsNullOrWhiteSpace(GetFilterText());
        private string GetFilterText() => m_FilterField?.value ?? string.Empty;

        private string GetNextSetName()
        {
            string baseName = Localization.S("features.blend-shapes.selection.default_name");
            var usedNames = new HashSet<string>(
                m_Selection.Settings.SelectionSets.Where(s => s != null).Select(s => s.DisplayName),
                StringComparer.Ordinal);
            if (!usedNames.Contains(baseName)) return baseName;
            for (int i = 2; ; i++)
            {
                string candidate = $"{baseName} {i}";
                if (!usedNames.Contains(candidate)) return candidate;
            }
        }
    }

    internal sealed class BlendShapeListView
    {
        private readonly List<string> m_VisibleIds;
        private readonly Func<string, string> m_GetName;
        private readonly string m_ActionLabel;
        private readonly Action<IEnumerable<string>> m_ApplyAction;
        private readonly bool m_ShowHeader;
        private readonly Func<string> m_GetHeader;
        private readonly Action m_SelectionChanged;
        private readonly HashSet<string> m_SelectedIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly IMGUIContainer m_Element;
        private ReorderableList m_List;
        private int m_LastSelectedVisibleIndex = -1;
        private bool m_IsDraggable;

        public BlendShapeListView(
            List<string> visibleIds,
            Func<string, string> getName,
            string actionLabel,
            Action<IEnumerable<string>> applyAction,
            bool showHeader,
            Func<string> getHeader,
            Action selectionChanged)
        {
            m_VisibleIds = visibleIds;
            m_GetName = getName;
            m_ActionLabel = actionLabel;
            m_ApplyAction = applyAction;
            m_ShowHeader = showHeader;
            m_GetHeader = getHeader;
            m_SelectionChanged = selectionChanged;
            m_Element = new IMGUIContainer(Draw);
        }

        public VisualElement Element => m_Element;
        public bool IsDraggable => m_IsDraggable;

        public void Rebuild(bool draggable, Action<IEnumerable<string>> applyReorder)
        {
            m_IsDraggable = draggable;
            m_List = new ReorderableList(m_VisibleIds, typeof(string), draggable, m_ShowHeader, false, false)
            {
                elementHeight = EditorGUIUtility.singleLineHeight + 4,
                footerHeight = 0,
                drawHeaderCallback = rect => EditorGUI.LabelField(rect, m_GetHeader?.Invoke() ?? string.Empty),
                drawElementCallback = DrawElement,
                onReorderCallback = _ => applyReorder?.Invoke(m_VisibleIds)
            };
        }

        public IEnumerable<string> SelectedVisibleIds()
        {
            return m_SelectedIds.Where(id => m_VisibleIds.Contains(id)).ToArray();
        }

        public int SelectedVisibleCount()
        {
            return m_SelectedIds.Count(id => m_VisibleIds.Contains(id));
        }

        public void RemoveSelection(IEnumerable<string> ids)
        {
            m_SelectedIds.ExceptWith(ids ?? Enumerable.Empty<string>());
            if (m_SelectedIds.Count == 0)
            {
                m_LastSelectedVisibleIndex = -1;
            }

            m_SelectionChanged?.Invoke();
        }

        public void PruneSelection(IEnumerable<string> validIds)
        {
            var valid = new HashSet<string>(validIds ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            m_SelectedIds.RemoveWhere(id => !valid.Contains(id));
            if (m_SelectedIds.Count == 0)
            {
                m_LastSelectedVisibleIndex = -1;
            }

            m_SelectionChanged?.Invoke();
        }

        private void Draw()
        {
            bool prevEnabled = GUI.enabled;
            GUI.enabled = true;
            try
            {
                m_List?.DoLayoutList();
            }
            finally
            {
                GUI.enabled = prevEnabled;
            }
        }

        private void DrawElement(Rect rect, int visibleIndex, bool isActive, bool isFocused)
        {
            if (visibleIndex < 0 || visibleIndex >= m_VisibleIds.Count)
            {
                return;
            }

            string shapeId = m_VisibleIds[visibleIndex];
            bool hasAction = !string.IsNullOrEmpty(m_ActionLabel) && m_ApplyAction != null;
            float actionWidth = hasAction ? 56f : 0f;
            const float listElementMargin = 6f;
            float handleWidth = m_IsDraggable ? 14f : 0f;
            var selectionRect = new Rect(
                rect.x - listElementMargin - handleWidth,
                rect.y,
                rect.width + handleWidth + listElementMargin * 2,
                rect.height);
            var actionRect = new Rect(rect.xMax - actionWidth, rect.y + 2, actionWidth, EditorGUIUtility.singleLineHeight);
            var labelRect = new Rect(rect.x, rect.y + 2, rect.width - actionWidth - (hasAction ? 6 : 0), EditorGUIUtility.singleLineHeight);

            if (m_SelectedIds.Contains(shapeId))
            {
                EditorGUI.DrawRect(selectionRect, SelectionColor());
            }

            if (m_SelectionChanged != null)
            {
                HandleSelection(selectionRect, actionRect, visibleIndex, shapeId);
            }

            EditorGUI.LabelField(labelRect, m_GetName(shapeId));
            if (hasAction && GUI.Button(actionRect, m_ActionLabel, EditorStyles.miniButton))
            {
                m_ApplyAction?.Invoke(new[] { shapeId });
            }
        }

        private void HandleSelection(Rect rowRect, Rect actionRect, int visibleIndex, string shapeId)
        {
            Event evt = Event.current;
            if (evt.type != EventType.MouseDown || evt.button != 0 || !rowRect.Contains(evt.mousePosition) || actionRect.Contains(evt.mousePosition))
            {
                return;
            }

            if (evt.shift && m_LastSelectedVisibleIndex >= 0)
            {
                int start = Mathf.Clamp(Math.Min(m_LastSelectedVisibleIndex, visibleIndex), 0, m_VisibleIds.Count - 1);
                int end = Mathf.Clamp(Math.Max(m_LastSelectedVisibleIndex, visibleIndex), 0, m_VisibleIds.Count - 1);
                for (int i = start; i <= end; i++)
                {
                    m_SelectedIds.Add(m_VisibleIds[i]);
                }
            }
            else if (evt.command || evt.control)
            {
                if (!m_SelectedIds.Remove(shapeId))
                {
                    m_SelectedIds.Add(shapeId);
                }

                m_LastSelectedVisibleIndex = visibleIndex;
            }
            else
            {
                m_SelectedIds.Clear();
                m_SelectedIds.Add(shapeId);
                m_LastSelectedVisibleIndex = visibleIndex;
            }

            m_List.index = visibleIndex;
            m_SelectionChanged?.Invoke();
        }

        private static Color SelectionColor()
        {
            return EditorGUIUtility.isProSkin
                ? new Color(0.24f, 0.38f, 0.56f, 0.55f)
                : new Color(0.30f, 0.55f, 0.88f, 0.35f);
        }
    }

    public sealed class BlendShapeWorkingSelection
    {
        private readonly FeatureInspectorContext m_Context;
        private readonly string m_FeatureId;
        private readonly Dictionary<string, string> m_Names;

        public List<string> Ids { get; }
        public BlendShapeFeatureSettings Settings => EffectiveSettingsResolver.FindFeatureSettings(
            m_Context.Buffer?.Settings, m_Context.MeshId, m_FeatureId) as BlendShapeFeatureSettings ?? new BlendShapeFeatureSettings();
        public BlendShapeSelectionSet CurrentSet => Settings.FindSelectionSet(Settings.ActiveSelectionSetId);
        public List<string> Active => Settings.ResolveEffectiveShapeIds(Ids);
        public string Choice => CurrentSet != null
            ? CurrentSet.DisplayName + (Settings.WorkingSelectionMatches(CurrentSet, Ids) ? "" : "*")
            : Localization.S(Active.SequenceEqual(Ids) ? "features.blend-shapes.selection.default" : "features.blend-shapes.selection.unsaved");

        public BlendShapeWorkingSelection(BlendShapeFeature feature, FeatureInspectorContext context)
        {
            m_Context = context;
            m_FeatureId = feature.FeatureId;
            m_Names = new Dictionary<string, string>(StringComparer.Ordinal);
            Ids = new List<string>();
            foreach (var shape in feature.Shapes)
            {
                if (string.IsNullOrEmpty(shape?.ShapeId) || m_Names.ContainsKey(shape.ShapeId)) continue;
                Ids.Add(shape.ShapeId);
                m_Names.Add(shape.ShapeId, shape.Name ?? shape.ShapeId);
            }
        }

        public string Name(string id) => m_Names.TryGetValue(id, out var name) ? name : id;

        private void Edit(string label, Action<BlendShapeFeatureSettings> change) =>
            m_Context.RecordEditAndCommit(label, settings => change(EffectiveSettingsResolver.FindOrCreateFeatureSettings<BlendShapeFeatureSettings>(
                settings, m_Context.MeshId, m_FeatureId)));

        private void WriteWorking(BlendShapeFeatureSettings settings, List<string> active)
        {
            var current = settings.FindSelectionSet(settings.ActiveSelectionSetId);
            if (!settings.HasWorkingOverride && current != null)
            {
                Copy(current.DisabledShapeIds, settings.DisabledShapeIds);
                Copy(current.OrderOverrides, settings.OrderOverrides);
            }

            settings.DisabledShapeIds.RemoveAll(m_Names.ContainsKey);
            settings.DisabledShapeIds.AddRange(Ids.Except(active));
            settings.OrderOverrides.RemoveAll(m_Names.ContainsKey);
            settings.OrderOverrides.AddRange(active);
            settings.HasWorkingOverride = true;
        }

        public void SetActive(IEnumerable<string> ids, string undoLabel = "Edit BlendShape Selection")
        {
            var active = ids.Distinct().ToList();
            Edit(undoLabel, settings => WriteWorking(settings, active));
        }

        public void Switch(string id) => Edit("Apply BlendShape Selection", settings =>
        {
            settings.ActiveSelectionSetId = id;
            settings.HasWorkingOverride = false;
            if (string.IsNullOrEmpty(id))
            {
                settings.DisabledShapeIds.RemoveAll(m_Names.ContainsKey);
                settings.OrderOverrides.RemoveAll(m_Names.ContainsKey);
            }
        });

        public void Save(string name, bool asNew) => Edit(asNew ? "Save BlendShape Selection Set" : "Update BlendShape Selection Set", settings =>
        {
            var source = settings.FindSelectionSet(settings.ActiveSelectionSetId);
            var set = asNew ? null : source;
            if (set == null)
            {
                set = new BlendShapeSelectionSet { SetId = Guid.NewGuid().ToString("N") };
                settings.SelectionSets.Add(set);
            }

            bool useSaved = !settings.HasWorkingOverride && source != null;
            Copy(useSaved ? source.DisabledShapeIds : settings.DisabledShapeIds, set.DisabledShapeIds);
            Copy(useSaved ? source.OrderOverrides : settings.OrderOverrides, set.OrderOverrides);
            set.DisplayName = name;
            settings.ActiveSelectionSetId = set.SetId;
            settings.HasWorkingOverride = false;
        });

        public void Delete() => Edit("Delete BlendShape Selection Set", settings =>
        {
            var current = settings.FindSelectionSet(settings.ActiveSelectionSetId);
            if (current == null) return;
            WriteWorking(settings, settings.ResolveEffectiveShapeIds(Ids));
            settings.SelectionSets.Remove(current);
            settings.ActiveSelectionSetId = string.Empty;
        });

        private static void Copy(List<string> source, List<string> destination)
        {
            if (ReferenceEquals(source, destination)) return;
            destination.Clear();
            destination.AddRange(source);
        }
    }

    internal sealed class SelectionSetNamePrompt : EditorWindow
    {
        private string m_Value;
        private bool m_Submitted;

        public static string Show(string title, string defaultValue)
        {
            var window = CreateInstance<SelectionSetNamePrompt>();
            window.titleContent = new GUIContent(title);
            window.m_Value = defaultValue ?? string.Empty;
            window.minSize = new Vector2(320, 76);
            window.maxSize = new Vector2(520, 76);
            window.ShowModalUtility();
            return window.m_Submitted ? window.m_Value : null;
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField(Localization.S("common.name"));
            GUI.SetNextControlName("SelectionSetName");
            m_Value = EditorGUILayout.TextField(m_Value);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(Localization.S("common.cancel"), GUILayout.Width(80)))
                {
                    m_Submitted = false;
                    Close();
                }

                using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(m_Value)))
                {
                    if (GUILayout.Button(Localization.S("common.save"), GUILayout.Width(80)))
                    {
                        m_Submitted = true;
                        Close();
                    }
                }
            }

            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return && !string.IsNullOrWhiteSpace(m_Value))
            {
                m_Submitted = true;
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
