using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShapeShare;
using Triturbo.BlendShare.EditorAssets.Settings;
using Triturbo.BlendShare.Inspector;
using Triturbo.BlendShare.Model.Features;
using Triturbo.BlendShare.Model.Settings;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Ui = Triturbo.BlendShare.Inspector.BlendShareInspectorUi;

namespace Triturbo.BlendShare.UI.Inspectors
{
    public sealed class BlendShapeInspectorDrawer : IFeatureInspectorDrawer
    {
        private const string k_LockKey = "blendshapes.locked";
        public string FeatureTypeId => BlendShapeFeature.FeatureTypeId;
        public int DisplayOrder => 10;

        public VisualElement CreateInspectorElement(BlendShareFeatureData feature, FeatureInspectorContext context) =>
            Create((BlendShapeFeature)feature, context, false);

        public VisualElement CreateCompactBadgeElement(BlendShareFeatureData feature, FeatureInspectorContext context) =>
            Create((BlendShapeFeature)feature, context, true);

        private static VisualElement Create(BlendShapeFeature feature, FeatureInspectorContext context, bool compact)
        {
            var selection = new BlendShapeWorkingSelection(feature, context);
            var box = Ui.Box();
            var header = Row();
            var title = Ui.ValueLabel(Localization.FeatureName("blend-shapes"));
            title.style.flexGrow = 1;
            Ui.StyleStrong(title);
            header.Add(title);
            bool locked = compact || context.IsReadOnly ||
                BlendShareInspectorUtility.GetAssetEditorPrefBool(context.PatchAsset, k_LockKey, true);
            var content = new VisualElement();
            var lockButton = new Button { name = "selection-lock" };
            if (!compact)
            {
                lockButton.SetEnabled(!context.IsReadOnly);
                header.Add(lockButton);
                box.Add(header);
            }
            box.viewDataKey = context.MeshId + "/" + feature.FeatureId;
            box.Add(content);
            void Build()
            {
                lockButton.Clear();
                lockButton.Add(Ui.IconElement(locked ? "Locked" : "Unlocked"));
                lockButton.tooltip = S(locked ? "unlock_tooltip" : "lock_tooltip");
                content.Clear();
                AddControls(content, selection, locked);
            }
            lockButton.clicked += () =>
            {
                locked = !locked;
                BlendShareInspectorUtility.SetAssetEditorPrefBool(context.PatchAsset, k_LockKey, locked, true);
                Build();
            };
            Build();
            box.Add(context.Advanced(feature));
            return compact ? new BlendShareFeatureBadge(Localization.SF("features.blend-shapes.status.compact",
                selection.Active.Count, selection.Ids.Count), box) : box;
        }

        private static string S(string key) => Localization.S("features.blend-shapes." + key);

        private static VisualElement Row() => new VisualElement
        {
            style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap, alignItems = Align.Center }
        };

        private static void AddControls(VisualElement parent, BlendShapeWorkingSelection selection, bool locked)
        {
            var controls = new VisualElement();
            var lists = new VisualElement();
            var filter = new TextField(Localization.S("common.filter")) { name = "shape-filter", viewDataKey = "shape-filter" };
            Ui.AddIconToPrefixLabel(filter, EditorGUIUtility.IconContent("Filter Icon"));
            if (!locked)
            {
                parent.Add(controls);
                parent.Add(filter);
            }
            parent.Add(lists);
            bool disabledExpanded = false;

            void Refresh()
            {
                controls.Clear();
                lists.Clear();
                var active = selection.Active;
                var disabled = selection.Ids.Except(active).ToList();
                string choice = selection.Choice;
                if (!locked) AddSelectionControls(controls, selection, Refresh);
                bool Visible(string id) => selection.Name(id).IndexOf(filter.value ?? "", StringComparison.OrdinalIgnoreCase) >= 0;
                var shown = active.Where(Visible).ToList();
                var hidden = disabled.Where(Visible).ToList();
                lists.Add(new Label(locked ? Localization.SF("features.blend-shapes.status.locked", active.Count, selection.Ids.Count)
                    : Localization.SF("features.blend-shapes.status.editing", active.Count, selection.Ids.Count, shown.Count, hidden.Count, choice)));
                AddList(lists, selection, shown, true, locked, !string.IsNullOrEmpty(filter.value), Refresh);
                if (locked) return;
                var foldout = new Foldout { text = S("selection.disabled_header"), value = disabledExpanded, viewDataKey = "disabled-shapes" };
                foldout.RegisterValueChangedCallback(evt => disabledExpanded = evt.newValue);
                AddList(foldout, selection, hidden, false, locked, false, Refresh);
                lists.Add(foldout);
            }
            filter.RegisterValueChangedCallback(_ => Refresh());
            Refresh();
        }

        private static void AddSelectionControls(VisualElement controls, BlendShapeWorkingSelection selection, Action refresh)
        {
            var choices = new List<string> { string.Empty };
            choices.AddRange(selection.Settings.SelectionSets.Where(set => set != null).Select(set => set.SetId));
            string selectedId = selection.Settings.ActiveSelectionSetId;
            if (!choices.Contains(selectedId)) selectedId = string.Empty;
            // Use a distinct ID for the unsaved choice so Default remains selectable.
            const string unsavedId = "$unsaved";
            if (selectedId.Length == 0 && !selection.Active.SequenceEqual(selection.Ids))
            {
                selectedId = unsavedId;
                choices.Add(unsavedId);
            }
            string Format(string id) => id == selectedId ? selection.Choice :
                selection.Settings.FindSelectionSet(id)?.DisplayName ?? S("selection.default");
            var popup = new PopupField<string>(S("selection_set"), choices, choices.IndexOf(selectedId), Format, Format);
            popup.RegisterValueChangedCallback(evt =>
            {
                if (evt.newValue == unsavedId) return;
                selection.Switch(evt.newValue);
                refresh();
            });
            controls.Add(popup);
            var buttons = Row();
            void Save(bool asNew)
            {
                var current = selection.CurrentSet;
                string name = SelectionSetNamePrompt.Show(S("selection.name_prompt"),
                    asNew ? S("selection.default_name") : current?.DisplayName);
                if (string.IsNullOrWhiteSpace(name)) return;
                selection.Save(name.Trim(), asNew);
                refresh();
            }
            var save = Ui.SmallButton(Localization.S("common.save"), () => Save(false));
            save.SetEnabled(selection.CurrentSet != null);
            buttons.Add(save);
            buttons.Add(Ui.SmallButton(Localization.S("common.save_as"), () => Save(true)));
            var revert = Ui.SmallButton(Localization.S("common.revert"), () =>
            {
                selection.Switch(selection.Settings.ActiveSelectionSetId);
                refresh();
            });
            revert.SetEnabled(selection.Choice != S("selection.default"));
            buttons.Add(revert);
            var delete = Ui.SmallButton(Localization.S("common.delete"), () =>
            {
                if (!EditorUtility.DisplayDialog(S("selection.delete_title"),
                    Localization.SF("features.blend-shapes.selection.delete_message", selection.CurrentSet.DisplayName),
                    Localization.S("common.delete"), Localization.S("common.cancel"))) return;
                selection.Delete();
                refresh();
            });
            delete.SetEnabled(selection.CurrentSet != null);
            buttons.Add(delete);
            controls.Add(buttons);
        }

        private static void AddList(VisualElement host, BlendShapeWorkingSelection selection, List<string> ids, bool enabled,
            bool locked, bool filtered, Action refresh)
        {
            void Toggle(IEnumerable<string> affected)
            {
                var active = selection.Active;
                selection.SetActive(enabled ? active.Except(affected) : active.Concat(affected));
                refresh();
            }
            var list = new ListView
            {
                itemsSource = ids, fixedItemHeight = 22, selectionType = SelectionType.Multiple,
                reorderable = enabled && !locked && !filtered,
                showBorder = true
            };
            list.makeItem = () =>
            {
                var row = Row();
                var name = new Label();
                name.style.flexGrow = 1;
                row.Add(name);
                if (!locked) row.Add(Ui.SmallButton(S(enabled ? "mute" : "enable_action"),
                    () => Toggle(new[] { (string)row.userData })));
                return row;
            };
            list.bindItem = (row, index) =>
            {
                row.userData = ids[index];
                row.Q<Label>().text = selection.Name(ids[index]);
            };
            list.style.height = Math.Min(ids.Count, 12) * 22 + 2;
            // Unity moves every selected item before the scheduled commit runs.
            IVisualElementScheduledItem reorder = null;
            list.itemIndexChanged += (_, __) =>
            {
                reorder?.Pause();
                reorder = list.schedule.Execute(() => { selection.SetActive(ids); refresh(); });
            };
            if (!locked)
            {
                var actions = Row();
                var visible = Ui.SmallButton(S(enabled ? "mute_visible" : "enable_visible"), () => Toggle(ids));
                visible.SetEnabled(ids.Count != 0);
                actions.Add(visible);
                string verb = S(enabled ? "mute" : "enable_action");
                var selected = Ui.SmallButton(Localization.SF("features.blend-shapes.selection.selected_action", verb, 0),
                    () => Toggle(list.selectedItems.Cast<string>().ToArray()));
                selected.SetEnabled(false);
                list.selectionChanged += items =>
                {
                    int count = items.Count();
                    selected.SetEnabled(count > 0);
                    selected.text = Localization.SF("features.blend-shapes.selection.selected_action", verb, count);
                };
                actions.Add(selected);
                if (!enabled) actions.Add(Ui.SmallButton(S("enable_all"), () => { selection.SetActive(selection.Ids); refresh(); }));
                host.Add(actions);
            }
            host.Add(list);
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
            m_Names = new Dictionary<string, string>();
            Ids = new List<string>();
            foreach (var shape in feature.Shapes)
            {
                if (string.IsNullOrEmpty(shape?.ShapeId) || m_Names.ContainsKey(shape.ShapeId)) continue;
                Ids.Add(shape.ShapeId);
                m_Names.Add(shape.ShapeId, shape.Name ?? shape.ShapeId);
            }
        }

        public string Name(string id) => m_Names[id];

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

        public void SetActive(IEnumerable<string> ids)
        {
            var active = ids.Distinct().ToList();
            Edit("Edit BlendShape Selection", settings => WriteWorking(settings, active));
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

        public void Save(string name, bool asNew) => Edit("Save BlendShape Selection Set", settings =>
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
