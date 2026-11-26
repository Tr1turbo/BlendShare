using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.UI;
using Triturbo.BlendShare.Components;
using Triturbo.BlendShare.UI.Inspectors;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace Triturbo.BlendShare.UI.Authoring
{
    [CustomPropertyDrawer(typeof(BlendShareBoneProxyBinding))]
    internal sealed class BlendShareBoneProxyBindingDrawer : PropertyDrawer
    {
        private const int k_ExpandedRowCount = 4;
        private const int k_ShadowedExpandedRowCount = 6;


        private const float k_FoldoutArrowWidth = 1f;// margin between element Foldout Arrow and title
        private const float k_RowRightPadding = 2f;
        private const float k_StatusColumnGap = 6f;
        private const float k_StatusPadding = 8f;
        private const float k_MinimumStatusWidth = 48f;
        private const float k_MaximumStatusWidth = 160f;
        //private const float DropdownButtonWidth = 19f;

        private static readonly Lazy<GUIStyle> s_DropdownStyle = new(() =>
            new GUIStyle("TextFieldDropDown")
            {
                //fixedWidth = DropdownButtonWidth,
            });
        private static readonly Lazy<GUIContent> s_DropdownIcon = new(() =>
            EditorGUIUtility.IconContent("icon dropdown"));
        private static readonly Lazy<GUIStyle> s_DropdownTextStyle = new(() => new GUIStyle("TextFieldDropDownText"));
        private static readonly Lazy<GUIStyle> s_StatusStyle = new(() =>
            new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleRight,
                clipping = TextClipping.Clip
            });
        private static readonly Dictionary<int, IReadOnlyDictionary<BlendShareBoneProxyBinding,
            BlendShareBoneProxyBindingStatus>> s_BindingStatusesByProxy = new();
        private static readonly Dictionary<int, BlendShareBoneProxyResolution> s_ResolutionsByProxy = new();

        internal static void SetBindingState(
            BlendShareBoneProxy proxy,
            IReadOnlyDictionary<BlendShareBoneProxyBinding, BlendShareBoneProxyBindingStatus> statuses,
            BlendShareBoneProxyResolution resolution)
        {
            if (proxy != null)
            {
                s_BindingStatusesByProxy[proxy.GetInstanceID()] = statuses;
                s_ResolutionsByProxy[proxy.GetInstanceID()] = resolution;
            }
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            label = EditorGUI.BeginProperty(position, label, property);
            try
            {
                var sourcePathProperty = property.FindPropertyRelative("m_SourceBonePath");
                var transformProperty = property.FindPropertyRelative("m_Transform");
                if (sourcePathProperty == null || transformProperty == null)
                {
                    EditorGUI.LabelField(position, label);
                    return;
                }

                string summary = string.IsNullOrWhiteSpace(sourcePathProperty.stringValue)
                    ? label.text
                    : sourcePathProperty.stringValue;
                var row = TakeRow(ref position);
                row.xMax -= k_RowRightPadding;
                GUIContent statusContent = GetStatusContent(property);
                float maximumStatusWidth = Mathf.Max(
                    k_MinimumStatusWidth,
                    Mathf.Min(k_MaximumStatusWidth, row.width * 0.45f));
                float statusWidth = statusContent == null
                    ? 0f
                    : Mathf.Clamp(
                        s_StatusStyle.Value.CalcSize(statusContent).x + k_StatusPadding,
                        k_MinimumStatusWidth,
                        maximumStatusWidth);
                float statusGap = statusContent == null ? 0f : k_StatusColumnGap;
                var foldoutRect = row;
                foldoutRect.xMax -= statusWidth + statusGap;
                property.isExpanded = EditorGUI.Foldout(
                    foldoutRect,
                    property.isExpanded,
                    GUIContent.none,
                    true);

                Rect indentedFoldoutRect = EditorGUI.IndentedRect(foldoutRect);
                var pathRect = new Rect(
                    indentedFoldoutRect.x + k_FoldoutArrowWidth,
                    row.y,
                    Mathf.Max(0f, indentedFoldoutRect.width - k_FoldoutArrowWidth),
                    row.height);
                BlendShareInspectorWidgets.DrawPathLabel(
                    pathRect,
                    summary,
                    sourcePathProperty.stringValue,
                    fadeClippedHead: true);

                if (statusContent != null)
                {
                    var statusRect = new Rect(
                        row.xMax - statusWidth,
                        row.y,
                        statusWidth,
                        row.height);
                    EditorGUI.LabelField(statusRect, statusContent, s_StatusStyle.Value);
                }
                if (!property.isExpanded)
                {
                    return;
                }

                int previousIndent = EditorGUI.indentLevel;
                EditorGUI.indentLevel++;
                try
                {
                    DrawSourcePath(
                        TakeRow(ref position),
                        property,
                        sourcePathProperty,
                        new GUIContent(Localization.S("ndmf.bone_proxy.source_bone_path")));
                    EditorGUI.PropertyField(
                        TakeRow(ref position),
                        transformProperty);
                    DrawFinalPath(
                        TakeRow(ref position),
                        property,
                        new GUIContent(Localization.S("ndmf.bone_proxy.final_path")));
                    if (TryGetEffectiveOverride(property, out var effective))
                    {
                        DrawEffectiveTransform(
                            TakeRow(ref position),
                            effective,
                            new GUIContent(Localization.S("ndmf.bone_proxy.effective_transform")));
                        DrawEffectiveFinalPath(
                            TakeRow(ref position),
                            effective,
                            new GUIContent(Localization.S("ndmf.bone_proxy.effective_final_path")));
                    }
                }
                finally
                {
                    EditorGUI.indentLevel = previousIndent;
                }
            }
            finally
            {
                EditorGUI.EndProperty();
            }
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            int rows = property.isExpanded
                ? TryGetEffectiveOverride(property, out _) ? k_ShadowedExpandedRowCount : k_ExpandedRowCount
                : 1;
            return rows * EditorGUIUtility.singleLineHeight +
                   (rows - 1) * EditorGUIUtility.standardVerticalSpacing;
        }

        private static void DrawSourcePath(
            Rect position,
            SerializedProperty bindingProperty,
            SerializedProperty sourcePathProperty,
            GUIContent label)
        {
            var skeleton = BlendShareComponentSetupService.GetSkeleton(bindingProperty.serializedObject.targetObject as BlendShareBoneProxy);
            float dropdownWidth = s_DropdownStyle.Value.fixedWidth;
            var textRect = Rect.MinMaxRect(
                position.xMin,
                position.yMin,
                position.xMax - dropdownWidth,
                position.yMax);
            var dropdownRect = Rect.MinMaxRect(
                position.xMax - dropdownWidth,
                position.yMin,
                position.xMax,
                position.yMax);

            // Stabilize delayed text editing when array elements are reordered or selected.
            GUIUtility.GetControlID(sourcePathProperty.propertyPath.GetHashCode(), FocusType.Passive);
            EditorGUI.BeginChangeCheck();
            var editedPath = EditorGUI.DelayedTextField(
                textRect,
                label,
                sourcePathProperty.stringValue,
                s_DropdownTextStyle.Value);
            if (EditorGUI.EndChangeCheck()) sourcePathProperty.stringValue = editedPath;

            using (new EditorGUI.DisabledScope(skeleton == null))
            {
                if (EditorGUI.DropdownButton(
                        dropdownRect,
                        GUIContent.none,
                        FocusType.Keyboard,
                        s_DropdownStyle.Value))
                {
                    string propertyPath = sourcePathProperty.propertyPath;
                    var targetObject = bindingProperty.serializedObject.targetObject;
                    var dropdown = new SourceBonePathDropdown(
                        Localization.S("ndmf.bone_proxy.source_bone_path"),
                        skeleton.Nodes.Where(bone => bone != null).Select(bone => bone.Path),
                        path => SetSourcePath(targetObject, propertyPath, path));
                    dropdown.Show(position);
                }
            }
        }

        private static void DrawFinalPath(
            Rect position,
            SerializedProperty bindingProperty,
            GUIContent label)
        {
            string finalPath = string.Empty;
            if (bindingProperty.serializedObject.targetObject is BlendShareBoneProxy proxy &&
                TryGetBinding(proxy, bindingProperty.propertyPath, out var binding) &&
                binding.Transform != null)
            {
                finalPath = proxy.GetFinalPath(binding, proxy.transform.root);
            }

            Rect valueRect = EditorGUI.PrefixLabel(position, label);
            int previousIndent = EditorGUI.indentLevel;
            EditorGUI.indentLevel = 0;
            EditorGUI.SelectableLabel(valueRect, finalPath, EditorStyles.miniLabel);
            EditorGUI.indentLevel = previousIndent;
        }

        private static void DrawEffectiveTransform(
            Rect position,
            BlendShareResolvedBone effective,
            GUIContent label)
        {
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUI.ObjectField(position, label, effective.Transform, typeof(Transform), true);
            }
        }

        private static void DrawEffectiveFinalPath(
            Rect position,
            BlendShareResolvedBone effective,
            GUIContent label)
        {
            string finalPath = effective.Proxy != null && effective.Binding != null
                ? effective.Proxy.GetFinalPath(effective.Binding, effective.Proxy.transform.root)
                : string.Empty;
            Rect valueRect = EditorGUI.PrefixLabel(position, label);
            int previousIndent = EditorGUI.indentLevel;
            EditorGUI.indentLevel = 0;
            EditorGUI.SelectableLabel(valueRect, finalPath, EditorStyles.miniLabel);
            EditorGUI.indentLevel = previousIndent;
        }

        private static void SetSourcePath(UnityEngine.Object targetObject, string propertyPath, string path)
        {
            if (targetObject == null)
            {
                return;
            }

            var serializedObject = new SerializedObject(targetObject);
            serializedObject.Update();
            var sourcePathProperty = serializedObject.FindProperty(propertyPath);
            if (sourcePathProperty == null)
            {
                return;
            }

            sourcePathProperty.stringValue = path;
            if (serializedObject.ApplyModifiedProperties() && targetObject is BlendShareBoneProxy proxy)
            {
                BlendShareBoneProxyEditor.NotifyPreviewInputChanged(proxy);
            }
        }

        private static bool TryGetBinding(
            BlendShareBoneProxy proxy,
            string propertyPath,
            out BlendShareBoneProxyBinding binding)
        {
            binding = null;
            const string marker = ".Array.data[";
            int markerIndex = propertyPath.LastIndexOf(marker, StringComparison.Ordinal);
            if (markerIndex < 0)
            {
                return false;
            }

            int indexStart = markerIndex + marker.Length;
            int indexEnd = propertyPath.IndexOf(']', indexStart);
            if (indexEnd < 0 ||
                !int.TryParse(propertyPath.Substring(indexStart, indexEnd - indexStart), out int index) ||
                index < 0 ||
                index >= proxy.Bindings.Count)
            {
                return false;
            }

            binding = proxy.Bindings[index];
            return binding != null;
        }

        private static GUIContent GetStatusContent(SerializedProperty bindingProperty)
        {
            if (bindingProperty.serializedObject.targetObject is not BlendShareBoneProxy proxy ||
                !TryGetBinding(proxy, bindingProperty.propertyPath, out var binding) ||
                !s_BindingStatusesByProxy.TryGetValue(proxy.GetInstanceID(), out var statuses) ||
                statuses == null ||
                !statuses.TryGetValue(binding, out var status) ||
                status == BlendShareBoneProxyBindingStatus.Unavailable)
            {
                return null;
            }

            var labels = new List<string>();
            var tooltips = new List<string>();
            if ((status & BlendShareBoneProxyBindingStatus.Source) != 0)
            {
                AddStatus("source");
            }
            if ((status & BlendShareBoneProxyBindingStatus.Override) != 0)
            {
                AddStatus("override");
            }
            if ((status & BlendShareBoneProxyBindingStatus.Modified) != 0)
            {
                AddStatus("modified");
            }
            if ((status & BlendShareBoneProxyBindingStatus.Shadowed) != 0)
            {
                AddStatus("shadowed");
            }
            return labels.Count == 0
                ? null
                : new GUIContent(string.Join(" · ", labels), string.Join("\n", tooltips));

            void AddStatus(string key)
            {
                labels.Add(Localization.S($"ndmf.bone_proxy.status_{key}"));
                tooltips.Add(Localization.S($"ndmf.bone_proxy.status_{key}_tooltip"));
            }
        }

        private static bool TryGetEffectiveOverride(
            SerializedProperty bindingProperty,
            out BlendShareResolvedBone effective)
        {
            effective = default;
            if (bindingProperty.serializedObject.targetObject is not BlendShareBoneProxy proxy ||
                !TryGetBinding(proxy, bindingProperty.propertyPath, out var binding) ||
                binding?.IsConfigured != true ||
                !s_ResolutionsByProxy.TryGetValue(proxy.GetInstanceID(), out var resolution) ||
                resolution == null ||
                !resolution.TryGetBinding(binding.SourceBonePath, out effective))
            {
                return false;
            }

            return effective.Proxy != proxy || !ReferenceEquals(effective.Binding, binding);
        }

        private static Rect TakeRow(ref Rect position)
        {
            var row = new Rect(
                position.x,
                position.y,
                position.width,
                EditorGUIUtility.singleLineHeight);
            position.y += EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;
            return row;
        }

        private sealed class SourceBonePathDropdown : AdvancedDropdown
        {
            private static readonly Vector2 s_MinimumDropdownSize = new(320f, 240f);

            private readonly string m_Title;
            private readonly PathNode m_PathRoot;
            private readonly Action<string> m_OnSelected;

            public SourceBonePathDropdown(
                string title,
                IEnumerable<string> paths,
                Action<string> onSelected)
                : base(new AdvancedDropdownState())
            {
                this.m_Title = title;
                m_PathRoot = BuildPathTree(paths);
                this.m_OnSelected = onSelected;
                minimumSize = s_MinimumDropdownSize;
            }

            protected override AdvancedDropdownItem BuildRoot()
            {
                var root = new AdvancedDropdownItem(m_Title);
                foreach (var child in m_PathRoot.Children.Values)
                {
                    root.AddChild(BuildDropdownItem(child));
                }
                return root;
            }

            private static AdvancedDropdownItem BuildDropdownItem(PathNode node)
            {
                string label = node.Name;
                var displayedNode = node;

                // Collapse folder-only chains while preserving every selectable bone as its own level.
                while (!displayedNode.IsBone && displayedNode.Children.Count == 1)
                {
                    displayedNode = displayedNode.Children.Values.First();
                    label = $"{label}/{displayedNode.Name}";
                }

                if (displayedNode.IsBone && displayedNode.Children.Count == 0)
                {
                    return new BonePathItem(label, displayedNode.Path);
                }

                var item = new AdvancedDropdownItem(label);
                if (displayedNode.IsBone)
                {
                    // AdvancedDropdown opens items with children as folders. Add an explicit leaf so
                    // a bone can be selected while also acting as the parent of more bone choices.
                    item.AddChild(new BonePathItem(displayedNode.Path, displayedNode.Path));
                }

                foreach (var child in displayedNode.Children.Values)
                {
                    item.AddChild(BuildDropdownItem(child));
                }
                return item;
            }

            private static PathNode BuildPathTree(IEnumerable<string> paths)
            {
                var root = new PathNode(string.Empty, MeshNodePath.Root);
                foreach (string sourcePath in paths ?? Array.Empty<string>())
                {
                    string normalizedPath = MeshNodePath.NormalizeOptional(sourcePath);
                    if (string.IsNullOrEmpty(normalizedPath) || normalizedPath == MeshNodePath.Root)
                    {
                        continue;
                    }

                    var current = root;
                    string currentPath = string.Empty;
                    foreach (string segment in normalizedPath.Split('/'))
                    {
                        currentPath = string.IsNullOrEmpty(currentPath)
                            ? segment
                            : $"{currentPath}/{segment}";
                        if (!current.Children.TryGetValue(segment, out var child))
                        {
                            child = new PathNode(segment, currentPath);
                            current.Children.Add(segment, child);
                        }
                        current = child;
                    }
                    current.IsBone = true;
                }
                return root;
            }

            protected override void ItemSelected(AdvancedDropdownItem item)
            {
                if (item is BonePathItem bonePathItem)
                {
                    m_OnSelected?.Invoke(bonePathItem.Path);
                }
            }

            private sealed class BonePathItem : AdvancedDropdownItem
            {
                public BonePathItem(string label, string path) : base(label)
                {
                    Path = path;
                }

                public string Path { get; }
            }

            private sealed class PathNode
            {
                public PathNode(string name, string path)
                {
                    Name = name;
                    Path = path;
                }

                public string Name { get; }
                public string Path { get; }
                public bool IsBone { get; set; }
                public SortedDictionary<string, PathNode> Children { get; } =
                    new(StringComparer.Ordinal);
            }
        }
    }
}
