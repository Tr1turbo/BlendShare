using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Model.Features;
using Triturbo.BlendShare.Pipeline.Extraction;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.UI.Extraction
{
    public sealed class BlendShapeExtractionOptionsDrawer : IFeatureExtractionOptionsDrawer
    {
        private string m_Search = string.Empty;
        public string FeatureTypeId => BlendShapeFeature.FeatureTypeId;
        public string DisplayName => "Blend Shapes";
        public int DisplayOrder => 100;

        public static IReadOnlyList<string> DiscoverChannelNames(IFbxMesh mesh)
        {
            var names = new SortedSet<string>(StringComparer.Ordinal);
            if (mesh?.BlendDeformers == null) return Array.Empty<string>();
            foreach (var deformer in mesh.BlendDeformers)
            {
                if (deformer?.Channels == null) continue;
                foreach (var channel in deformer.Channels)
                    if (!string.IsNullOrWhiteSpace(channel?.Name)) names.Add(channel.Name);
            }
            return names.ToArray();
        }

        public void DrawOptions(IFeatureExtractionOptions options, FeatureExtractionOptionsContext context)
        {
            if (!(options is BlendShapeExtractionOptions shapes))
            {
                EditorGUILayout.HelpBox("BlendShapeExtractionOptions expected.", MessageType.Warning);
                return;
            }
            string path = context?.ActiveMeshPath;
            EditorGUI.BeginChangeCheck();
            shapes.Enabled = EditorGUILayout.Toggle("Enabled", shapes.Enabled);
            shapes.DefaultBaseMode = (BlendShapeBaseMode)EditorGUILayout.EnumPopup("Default base mode", shapes.DefaultBaseMode);
            if (!string.IsNullOrEmpty(path))
            {
                bool hasOverride = shapes.HasBaseModeOverride(path);
                if (EditorGUILayout.Toggle("Override mesh base mode", hasOverride))
                    shapes.SetBaseModeOverride(path, (BlendShapeBaseMode)EditorGUILayout.EnumPopup("Mesh base mode", shapes.GetBaseMode(path)));
                else if (hasOverride)
                    shapes.SetBaseModeOverride(path, null);
            }
            shapes.IncludeNormalDeltas = EditorGUILayout.Toggle("Include normal deltas", shapes.IncludeNormalDeltas);
            shapes.ExtractAllWhenSelectionEmpty = EditorGUILayout.Toggle("Extract all when selection empty", shapes.ExtractAllWhenSelectionEmpty);
            bool changed = EditorGUI.EndChangeCheck();
            if (!string.IsNullOrEmpty(path))
            {
                var sourceMesh = context.SourceScene != null && !context.SourceScene.IsDisposed
                    ? context.SourceScene.FindMeshByNodePath(path)
                    : null;
                var names = DiscoverChannelNames(sourceMesh);
                var selected = new HashSet<string>(shapes.GetSelectedShapeNames(path), StringComparer.Ordinal);
                if (selected.Count == 0 && shapes.ExtractAllWhenSelectionEmpty && !shapes.HasSelectedShapeNamesForMesh(path))
                    selected.UnionWith(names);
                m_Search = EditorGUILayout.TextField("Search channels", m_Search ?? string.Empty) ?? string.Empty;
                bool selectionChanged = false;
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Select All"))
                    {
                        selected.UnionWith(names);
                        selectionChanged = true;
                    }
                    if (GUILayout.Button("Select None"))
                    {
                        selected.Clear();
                        selectionChanged = true;
                    }
                }
                foreach (string name in names)
                {
                    if (m_Search.Length > 0 && name.IndexOf(m_Search, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    bool wasSelected = selected.Contains(name);
                    if (EditorGUILayout.ToggleLeft(name, wasSelected) == wasSelected) continue;
                    if (wasSelected) selected.Remove(name);
                    else selected.Add(name);
                    selectionChanged = true;
                }
                if (selectionChanged)
                {
                    shapes.SetSelectedShapeNames(path, selected.OrderBy(n => n, StringComparer.Ordinal));
                    changed = true;
                }
            }
            if (changed) context?.NotifyChanged?.Invoke();
        }
    }
}
