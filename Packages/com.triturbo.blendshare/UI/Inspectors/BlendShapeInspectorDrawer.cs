using System;
using System.Collections.Generic;
using Triturbo.BlendShare.EditorAssets.Settings;
using Triturbo.BlendShare.Model.Features;
using Triturbo.BlendShare.Model.Settings;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.UI.Inspectors
{
    /// <summary>
    /// Inspector drawer for BlendShapeFeature editing DisabledShapeIds + OrderOverrides (and active SelectionSets)
    /// in .blendshare.meta via UndoBuffer while preserving unknown IDs across temporary downgrades.
    /// </summary>
    public sealed class BlendShapeInspectorDrawer : IFeatureInspectorDrawer
    {
        public string FeatureTypeId => BlendShapeFeature.FeatureTypeId;
        public int DisplayOrder => 10;

        public void DrawInspector(BlendShareFeatureData featureData, FeatureInspectorContext context)
        {
            if (!(featureData is BlendShapeFeature feature)) return;

            var shapes = feature.Shapes ?? new List<BlendShapeEntry>();
            var patchIds = new List<string>(shapes.Count);
            var byId = new Dictionary<string, BlendShapeEntry>(StringComparer.Ordinal);
            for (int i = 0; i < shapes.Count; i++)
            {
                var s = shapes[i];
                if (s == null || string.IsNullOrEmpty(s.ShapeId)) continue;
                patchIds.Add(s.ShapeId);
                byId[s.ShapeId] = s;
            }

            var settings = context.Buffer != null
                ? EffectiveSettingsResolver.FindFeatureSettings(context.Buffer.Settings, context.MeshId, feature.FeatureId) as BlendShapeFeatureSettings
                : null;

            // Resolve active SelectionSet consistently with BlendShapeFeatureSettings.ResolveEffectiveShapeIds.
            BlendShapeSelectionSet activeSet = FindActiveSet(settings);
            List<string> activeDisabledList = activeSet != null ? activeSet.DisabledShapeIds : settings?.DisabledShapeIds;
            List<string> activeOrderList = activeSet != null ? activeSet.OrderOverrides : settings?.OrderOverrides;

            var disabledSet = new HashSet<string>(StringComparer.Ordinal);
            if (activeDisabledList != null)
            {
                for (int i = 0; i < activeDisabledList.Count; i++)
                {
                    if (!string.IsNullOrEmpty(activeDisabledList[i])) disabledSet.Add(activeDisabledList[i]);
                }
            }

            var displayOrder = new List<string>(patchIds.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (activeOrderList != null)
            {
                for (int i = 0; i < activeOrderList.Count; i++)
                {
                    string id = activeOrderList[i];
                    if (byId.ContainsKey(id) && seen.Add(id)) displayOrder.Add(id);
                }
            }
            for (int i = 0; i < patchIds.Count; i++)
            {
                if (seen.Add(patchIds[i])) displayOrder.Add(patchIds[i]);
            }

            int enabledCount = 0;
            for (int i = 0; i < displayOrder.Count; i++)
            {
                if (!disabledSet.Contains(displayOrder[i])) enabledCount++;
            }

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            // SelectionSet selector bar
            DrawSelectionSetBar(feature, settings, activeSet, context);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"BlendShapes ({enabledCount} / {displayOrder.Count} enabled)", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(context.IsReadOnly))
            {
                if (GUILayout.Button("Enable All", EditorStyles.miniButtonLeft, GUILayout.Width(72)))
                {
                    context.RecordEditAndCommit("Enable All BlendShapes", s =>
                    {
                        var fs = EffectiveSettingsResolver.FindOrCreateFeatureSettings<BlendShapeFeatureSettings>(s, context.MeshId, feature.FeatureId);
                        GetTargetLists(fs, out var targetDisabled, out _);
                        // Preserve unknown IDs (absent from current patch) across temporary downgrades!
                        targetDisabled.RemoveAll(id => byId.ContainsKey(id));
                    });
                }
                if (GUILayout.Button("Disable All", EditorStyles.miniButtonMid, GUILayout.Width(72)))
                {
                    context.RecordEditAndCommit("Disable All BlendShapes", s =>
                    {
                        var fs = EffectiveSettingsResolver.FindOrCreateFeatureSettings<BlendShapeFeatureSettings>(s, context.MeshId, feature.FeatureId);
                        GetTargetLists(fs, out var targetDisabled, out _);
                        var existing = new HashSet<string>(targetDisabled, StringComparer.Ordinal);
                        for (int i = 0; i < patchIds.Count; i++)
                        {
                            if (existing.Add(patchIds[i])) targetDisabled.Add(patchIds[i]);
                        }
                    });
                }
                if (GUILayout.Button("Reset Order", EditorStyles.miniButtonRight, GUILayout.Width(80)))
                {
                    context.RecordEditAndCommit("Reset BlendShape Order", s =>
                    {
                        var fs = EffectiveSettingsResolver.FindOrCreateFeatureSettings<BlendShapeFeatureSettings>(s, context.MeshId, feature.FeatureId);
                        GetTargetLists(fs, out _, out var targetOrder);
                        // Preserve unknown IDs while clearing order overrides for current patch shapes.
                        targetOrder.RemoveAll(id => byId.ContainsKey(id));
                    });
                }
            }
            EditorGUILayout.EndHorizontal();

            for (int i = 0; i < displayOrder.Count; i++)
            {
                string shapeId = displayOrder[i];
                var shape = byId[shapeId];
                bool isEnabled = !disabledSet.Contains(shapeId);
                int frameCount = shape.Frames != null ? shape.Frames.Count : 0;

                EditorGUILayout.BeginHorizontal();
                using (new EditorGUI.DisabledScope(context.IsReadOnly))
                {
                    bool nextEnabled = EditorGUILayout.ToggleLeft(
                        frameCount > 1 ? $"{shape.Name} ({frameCount} frames)" : shape.Name,
                        isEnabled);
                    if (nextEnabled != isEnabled)
                    {
                        string capturedId = shapeId;
                        bool enable = nextEnabled;
                        context.RecordEditAndCommit($"Toggle BlendShape {shape.Name}", s =>
                        {
                            var fs = EffectiveSettingsResolver.FindOrCreateFeatureSettings<BlendShapeFeatureSettings>(s, context.MeshId, feature.FeatureId);
                            GetTargetLists(fs, out var targetDisabled, out _);
                            if (enable)
                            {
                                targetDisabled.RemoveAll(id => string.Equals(id, capturedId, StringComparison.Ordinal));
                            }
                            else if (!targetDisabled.Contains(capturedId))
                            {
                                targetDisabled.Add(capturedId);
                            }
                        });
                    }

                    using (new EditorGUI.DisabledScope(i == 0))
                    {
                        if (GUILayout.Button("\u25B2", EditorStyles.miniButtonLeft, GUILayout.Width(22)))
                        {
                            int swapIndex = i;
                            var snapshotOrder = new List<string>(displayOrder);
                            context.RecordEditAndCommit($"Move BlendShape {shape.Name} Up", s =>
                            {
                                var fs = EffectiveSettingsResolver.FindOrCreateFeatureSettings<BlendShapeFeatureSettings>(s, context.MeshId, feature.FeatureId);
                                GetTargetLists(fs, out _, out var targetOrder);
                                var tmp = snapshotOrder[swapIndex - 1];
                                snapshotOrder[swapIndex - 1] = snapshotOrder[swapIndex];
                                snapshotOrder[swapIndex] = tmp;
                                ApplyOrderPreservingUnknownIds(targetOrder, snapshotOrder, byId);
                            });
                        }
                    }

                    using (new EditorGUI.DisabledScope(i == displayOrder.Count - 1))
                    {
                        if (GUILayout.Button("\u25BC", EditorStyles.miniButtonRight, GUILayout.Width(22)))
                        {
                            int swapIndex = i;
                            var snapshotOrder = new List<string>(displayOrder);
                            context.RecordEditAndCommit($"Move BlendShape {shape.Name} Down", s =>
                            {
                                var fs = EffectiveSettingsResolver.FindOrCreateFeatureSettings<BlendShapeFeatureSettings>(s, context.MeshId, feature.FeatureId);
                                GetTargetLists(fs, out _, out var targetOrder);
                                var tmp = snapshotOrder[swapIndex + 1];
                                snapshotOrder[swapIndex + 1] = snapshotOrder[swapIndex];
                                snapshotOrder[swapIndex] = tmp;
                                ApplyOrderPreservingUnknownIds(targetOrder, snapshotOrder, byId);
                            });
                        }
                    }
                }
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndVertical();
        }

        private static void DrawSelectionSetBar(
            BlendShapeFeature feature,
            BlendShapeFeatureSettings settings,
            BlendShapeSelectionSet activeSet,
            FeatureInspectorContext context)
        {
            var sets = settings != null ? settings.SelectionSets : null;
            int setCount = sets != null ? sets.Count : 0;
            var labels = new string[setCount + 1];
            labels[0] = "Default";
            int currentIdx = 0;
            for (int i = 0; i < setCount; i++)
            {
                var set = sets[i];
                labels[i + 1] = set != null && !string.IsNullOrEmpty(set.DisplayName) ? set.DisplayName : $"Set {i + 1}";
                if (activeSet != null && set == activeSet) currentIdx = i + 1;
            }

            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(context.IsReadOnly))
            {
                int nextIdx = EditorGUILayout.Popup("Selection Set", currentIdx, labels);
                if (nextIdx != currentIdx)
                {
                    string nextId = nextIdx == 0 ? string.Empty : sets[nextIdx - 1].SetId;
                    context.RecordEditAndCommit("Switch BlendShape Selection Set", s =>
                    {
                        var fs = EffectiveSettingsResolver.FindOrCreateFeatureSettings<BlendShapeFeatureSettings>(s, context.MeshId, feature.FeatureId);
                        fs.ActiveSelectionSetId = nextId;
                    });
                }

                if (GUILayout.Button("+ Set", EditorStyles.miniButtonLeft, GUILayout.Width(46)))
                {
                    string newId = Guid.NewGuid().ToString("N");
                    string newName = $"Set {setCount + 1}";
                    context.RecordEditAndCommit("Create BlendShape Selection Set", s =>
                    {
                        var fs = EffectiveSettingsResolver.FindOrCreateFeatureSettings<BlendShapeFeatureSettings>(s, context.MeshId, feature.FeatureId);
                        GetTargetLists(fs, out var curDisabled, out var curOrder);
                        var created = new BlendShapeSelectionSet { SetId = newId, DisplayName = newName };
                        created.DisabledShapeIds.AddRange(curDisabled);
                        created.OrderOverrides.AddRange(curOrder);
                        fs.SelectionSets.Add(created);
                        fs.ActiveSelectionSetId = newId;
                    });
                }

                using (new EditorGUI.DisabledScope(activeSet == null))
                {
                    if (GUILayout.Button("- Set", EditorStyles.miniButtonRight, GUILayout.Width(46)))
                    {
                        string removeId = activeSet.SetId;
                        context.RecordEditAndCommit("Delete BlendShape Selection Set", s =>
                        {
                            var fs = EffectiveSettingsResolver.FindOrCreateFeatureSettings<BlendShapeFeatureSettings>(s, context.MeshId, feature.FeatureId);
                            fs.SelectionSets.RemoveAll(item => item != null && string.Equals(item.SetId, removeId, StringComparison.Ordinal));
                            if (string.Equals(fs.ActiveSelectionSetId, removeId, StringComparison.Ordinal))
                            {
                                fs.ActiveSelectionSetId = string.Empty;
                            }
                        });
                    }
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        private static BlendShapeSelectionSet FindActiveSet(BlendShapeFeatureSettings settings)
        {
            if (settings == null || string.IsNullOrEmpty(settings.ActiveSelectionSetId)) return null;
            var sets = settings.SelectionSets;
            for (int i = 0; i < sets.Count; i++)
            {
                if (sets[i] != null && string.Equals(sets[i].SetId, settings.ActiveSelectionSetId, StringComparison.Ordinal))
                {
                    return sets[i];
                }
            }
            return null;
        }

        private static void GetTargetLists(
            BlendShapeFeatureSettings settings,
            out List<string> disabledList,
            out List<string> orderList)
        {
            var activeSet = FindActiveSet(settings);
            if (activeSet != null)
            {
                disabledList = activeSet.DisabledShapeIds;
                orderList = activeSet.OrderOverrides;
            }
            else
            {
                disabledList = settings.DisabledShapeIds;
                orderList = settings.OrderOverrides;
            }
        }

        private static void ApplyOrderPreservingUnknownIds(
            List<string> targetOrder,
            List<string> knownOrder,
            Dictionary<string, BlendShapeEntry> knownIds)
        {
            var preservedUnknown = new List<string>();
            for (int i = 0; i < targetOrder.Count; i++)
            {
                string id = targetOrder[i];
                if (!string.IsNullOrEmpty(id) && !knownIds.ContainsKey(id))
                {
                    preservedUnknown.Add(id);
                }
            }

            targetOrder.Clear();
            targetOrder.AddRange(knownOrder);
            targetOrder.AddRange(preservedUnknown);
        }
    }
}
