using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShapeShare;
using Triturbo.BlendShare.Components;
using Triturbo.BlendShare.Features.SkinWeights;
using Triturbo.BlendShare.Inspector;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace Triturbo.BlendShare.NDMF
{
    [CustomPropertyDrawer(typeof(BlendShareBoneProxyBinding))]
    internal sealed class BlendShareBoneProxyBindingDrawer : PropertyDrawer
    {
        private const int ExpandedRowCount = 4;


        private const float FoldoutArrowWidth = 1f;// margin between element Foldout Arrow and title
        private const float RowRightPadding = 2f;
        private const float StatusColumnGap = 6f;
        private const float StatusPadding = 8f;
        private const float MinimumStatusWidth = 48f;
        private const float MaximumStatusWidth = 160f;
        //private const float DropdownButtonWidth = 19f;

        private static readonly Lazy<GUIStyle> DropdownStyle = new(() =>
            new GUIStyle("TextFieldDropDown")
            {
                //fixedWidth = DropdownButtonWidth,
            });
        private static readonly Lazy<GUIContent> DropdownIcon = new(() =>
            EditorGUIUtility.IconContent("icon dropdown"));
        private static readonly Lazy<GUIStyle> DropdownTextStyle = new(() => new GUIStyle("TextFieldDropDownText"));
        private static readonly Lazy<GUIStyle> StatusStyle = new(() =>
            new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleRight,
                clipping = TextClipping.Clip
            });
        private static readonly Dictionary<int, IReadOnlyDictionary<BlendShareBoneProxyBinding,
            BlendShareBoneProxyBindingStatus>> BindingStatusesByProxy = new();

        internal static void SetBindingStatuses(
            BlendShareBoneProxy proxy,
            IReadOnlyDictionary<BlendShareBoneProxyBinding, BlendShareBoneProxyBindingStatus> statuses)
        {
            if (proxy != null)
            {
                BindingStatusesByProxy[proxy.GetInstanceID()] = statuses;
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
                row.xMax -= RowRightPadding;
                GUIContent statusContent = GetStatusContent(property);
                float maximumStatusWidth = Mathf.Max(
                    MinimumStatusWidth,
                    Mathf.Min(MaximumStatusWidth, row.width * 0.45f));
                float statusWidth = statusContent == null
                    ? 0f
                    : Mathf.Clamp(
                        StatusStyle.Value.CalcSize(statusContent).x + StatusPadding,
                        MinimumStatusWidth,
                        maximumStatusWidth);
                float statusGap = statusContent == null ? 0f : StatusColumnGap;
                var foldoutRect = row;
                foldoutRect.xMax -= statusWidth + statusGap;
                property.isExpanded = EditorGUI.Foldout(
                    foldoutRect,
                    property.isExpanded,
                    GUIContent.none,
                    true);

                Rect indentedFoldoutRect = EditorGUI.IndentedRect(foldoutRect);
                var pathRect = new Rect(
                    indentedFoldoutRect.x + FoldoutArrowWidth,
                    row.y,
                    Mathf.Max(0f, indentedFoldoutRect.width - FoldoutArrowWidth),
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
                    EditorGUI.LabelField(statusRect, statusContent, StatusStyle.Value);
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
            int rows = property.isExpanded ? ExpandedRowCount : 1;
            return rows * EditorGUIUtility.singleLineHeight +
                   (rows - 1) * EditorGUIUtility.standardVerticalSpacing;
        }

        private static void DrawSourcePath(
            Rect position,
            SerializedProperty bindingProperty,
            SerializedProperty sourcePathProperty,
            GUIContent label)
        {
            var armature = bindingProperty.serializedObject
                .FindProperty("m_SourceArmature")
                ?.objectReferenceValue as FbxArmatureObject;
            float dropdownWidth = DropdownStyle.Value.fixedWidth;
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
            sourcePathProperty.stringValue = EditorGUI.DelayedTextField(
                textRect,
                label,
                sourcePathProperty.stringValue,
                DropdownTextStyle.Value);

            using (new EditorGUI.DisabledScope(armature == null))
            {
                if (EditorGUI.DropdownButton(
                        dropdownRect,
                        GUIContent.none,
                        FocusType.Keyboard,
                        DropdownStyle.Value))
                {
                    string propertyPath = sourcePathProperty.propertyPath;
                    var targetObject = bindingProperty.serializedObject.targetObject;
                    var dropdown = new SourceBonePathDropdown(
                        Localization.S("ndmf.bone_proxy.source_bone_path"),
                        armature.GetBonePathsInHierarchyOrder(),
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
                !BindingStatusesByProxy.TryGetValue(proxy.GetInstanceID(), out var statuses) ||
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

            return labels.Count == 0
                ? null
                : new GUIContent(string.Join(" · ", labels), string.Join("\n", tooltips));

            void AddStatus(string key)
            {
                labels.Add(Localization.S($"ndmf.bone_proxy.status_{key}"));
                tooltips.Add(Localization.S($"ndmf.bone_proxy.status_{key}_tooltip"));
            }
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
            private static readonly Vector2 MinimumDropdownSize = new(320f, 240f);

            private readonly string title;
            private readonly PathNode pathRoot;
            private readonly Action<string> onSelected;

            public SourceBonePathDropdown(
                string title,
                IEnumerable<string> paths,
                Action<string> onSelected)
                : base(new AdvancedDropdownState())
            {
                this.title = title;
                pathRoot = BuildPathTree(paths);
                this.onSelected = onSelected;
                minimumSize = MinimumDropdownSize;
            }

            protected override AdvancedDropdownItem BuildRoot()
            {
                var root = new AdvancedDropdownItem(title);
                foreach (var child in pathRoot.Children.Values)
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
                    onSelected?.Invoke(bonePathItem.Path);
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
