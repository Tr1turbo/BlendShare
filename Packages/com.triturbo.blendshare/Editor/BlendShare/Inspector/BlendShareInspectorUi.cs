using System;
using Triturbo.BlendShapeShare;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Triturbo.BlendShare.Inspector
{
    public static class BlendShareInspectorUi
    {
        private const string ClickableObjectClass = "blendshare-clickable-object";

        public static VisualElement CreateRoot()
        {
            var root = new VisualElement();
            root.style.paddingLeft = 8;
            root.style.paddingRight = 8;
            root.style.paddingTop = 8;
            root.style.paddingBottom = 8;
            return root;
        }

        public static VisualElement Header(string title, string subtitle = null)
        {
            var header = new VisualElement();
            header.style.marginBottom = 2;

            var titleLabel = new Label(title);
            StyleHeading(titleLabel);
            titleLabel.style.fontSize = 15;
            header.Add(titleLabel);

            if (!string.IsNullOrWhiteSpace(subtitle))
            {
                var subtitleLabel = new Label(subtitle);
                subtitleLabel.style.opacity = 0.7f;
                subtitleLabel.style.whiteSpace = WhiteSpace.Normal;
                header.Add(subtitleLabel);
            }

            return header;
        }

        public static VisualElement Section(string title)
        {
            var section = new VisualElement();
            section.style.borderTopWidth = 1;
            section.style.borderTopColor = BorderColor();
            section.style.marginTop = 6;
            section.style.paddingTop = 7;
            section.style.paddingBottom = 6;

            var label = new Label(title);
            StyleHeading(label);
            label.style.marginBottom = 4;
            section.Add(label);
            return section;
        }

        public static void StyleHeading(Label label)
        {
            StyleStrong(label);
        }

        public static void StyleStrong(Label label)
        {
            if (label == null)
            {
                return;
            }

            label.style.unityFontStyleAndWeight = Localization.IsCjkLanguage ? FontStyle.Normal : FontStyle.Bold;
        }

        public static VisualElement Box()
        {
            var box = new VisualElement();
            box.style.borderTopWidth = 1;
            box.style.borderRightWidth = 1;
            box.style.borderBottomWidth = 1;
            box.style.borderLeftWidth = 1;
            box.style.borderTopColor = BorderColor();
            box.style.borderRightColor = BorderColor();
            box.style.borderBottomColor = BorderColor();
            box.style.borderLeftColor = BorderColor();
            box.style.borderTopLeftRadius = 4;
            box.style.borderTopRightRadius = 4;
            box.style.borderBottomLeftRadius = 4;
            box.style.borderBottomRightRadius = 4;
            box.style.paddingLeft = 6;
            box.style.paddingRight = 6;
            box.style.paddingTop = 5;
            box.style.paddingBottom = 5;
            box.style.marginTop = 4;
            box.style.marginBottom = 4;
            box.style.backgroundColor = BoxBackgroundColor();
            return box;
        }

        public static void RegisterClickAction(VisualElement element, Action clickAction)
        {
            if (element == null || clickAction == null)
            {
                return;
            }

            element.AddToClassList(ClickableObjectClass);
            element.RegisterCallback<MouseUpEvent>(evt =>
            {
                if (evt.button != 0 || evt.clickCount > 1 || IsNestedInteractiveTarget(evt, element))
                {
                    return;
                }

                clickAction.Invoke();
                evt.StopPropagation();
            });
        }

        public static void RegisterDoubleClickAction(VisualElement element, Action doubleClickAction)
        {
            if (element == null || doubleClickAction == null)
            {
                return;
            }

            element.AddToClassList(ClickableObjectClass);
            element.RegisterCallback<MouseDownEvent>(evt =>
            {
                if (evt.button != 0 || evt.clickCount < 2 || IsNestedInteractiveTarget(evt, element))
                {
                    return;
                }

                doubleClickAction.Invoke();
                evt.StopPropagation();
            }, TrickleDown.TrickleDown);
        }

        private static bool IsNestedInteractiveTarget(EventBase evt, VisualElement root)
        {
            for (var current = evt.target as VisualElement; current != null && current != root; current = current.parent)
            {
                if (current.ClassListContains(ClickableObjectClass) || IsInteractiveElement(current))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsInteractiveElement(VisualElement element)
        {
            return element is Button ||
                   element is Foldout ||
                   element is IMGUIContainer ||
                   element is ScrollView ||
                   IsBaseField(element);
        }

        private static bool IsBaseField(VisualElement element)
        {
            for (var type = element?.GetType(); type != null; type = type.BaseType)
            {
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(BaseField<>))
                {
                    return true;
                }
            }

            return false;
        }

        public static VisualElement Row(string label, string value)
        {
            var right = new Label(string.IsNullOrEmpty(value) ? "-" : value);
            right.style.whiteSpace = WhiteSpace.Normal;
            return LabeledRow(label, right);
        }

        public static Label ValueLabel(string value)
        {
            var label = new Label(string.IsNullOrEmpty(value) ? "-" : value);
            label.style.whiteSpace = WhiteSpace.Normal;
            return label;
        }

        public static VisualElement LabeledRow(string label, VisualElement content, VisualElement suffixIcon = null)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;

            var left = new Label(label);
            left.style.minWidth = 130;
            left.style.opacity = 0.72f;
            row.Add(left);

            content.style.flexGrow = 1;
            content.style.flexShrink = 1;
            content.style.marginLeft = 8;
            content.style.minWidth = 0;
            row.Add(content);

            if (suffixIcon != null)
            {
                row.Add(suffixIcon);
            }

            return row;
        }

        public static void AddIconToPrefixLabel<TValue>(BaseField<TValue> field, GUIContent iconContent, float iconSize = 16f, float spacing = 4f)
        {
            if (field == null || iconContent?.image == null)
            {
                return;
            }

            var label = field.labelElement;
            label.style.position = Position.Relative;
            label.style.paddingLeft = iconSize + spacing;

            var icon = new Image { image = iconContent.image };
            icon.pickingMode = PickingMode.Ignore;
            icon.style.width = iconSize;
            icon.style.height = iconSize;
            icon.style.position = Position.Absolute;
            icon.style.left = 0;
            icon.style.top = 1;
            label.Add(icon);
        }

        public static VisualElement CompatibilityIcon(MeshFbxCompatibilityStatus status)
        {
            return status.State switch
            {
                MeshFbxCompatibilityState.Verified => IconElement("d_GreenCheckmark", status.Message, "V"),
                MeshFbxCompatibilityState.Incompatible => IconElement("d_console.erroricon", status.Message, "X"),
                MeshFbxCompatibilityState.Unknown => IconElement("d__Help", status.Message, "?"),

                _ => IconElement("d_console.warnicon", status.Message, "!")
            };
        }

        public static VisualElement IconElement(string iconNameOrPath, string tooltip = null, string fallbackText = null)
        {
            var texture = BlendShareInspectorUtility.LoadIconTexture(iconNameOrPath);
            return IconElement(texture, tooltip, fallbackText);
        }

        public static VisualElement IconElement(Texture texture, string tooltip = null, string fallbackText = null)
        {
            if (texture == null)
            {
                var fallback = new Label(fallbackText ?? string.Empty) { tooltip = tooltip };
                fallback.style.width = 16;
                fallback.style.height = 16;
                fallback.style.marginLeft = 4;
                fallback.style.marginRight = 2;
                fallback.style.alignSelf = Align.Center;
                StyleStrong(fallback);
                fallback.style.unityTextAlign = TextAnchor.MiddleCenter;
                fallback.style.opacity = 0.82f;
                return fallback;
            }

            var icon = new Image
            {
                image = texture,
                tooltip = tooltip
            };
            icon.style.width = 16;
            icon.style.height = 16;
            icon.style.marginLeft = 4;
            icon.style.marginRight = 2;
            icon.style.alignSelf = Align.Center;
            icon.style.opacity = 0.82f;
            return icon;
        }

        public static Label Badge(string text, StatusKind kind = StatusKind.Neutral)
        {
            var badge = new Label(text);
            badge.style.paddingLeft = 6;
            badge.style.paddingRight = 6;
            badge.style.paddingTop = 1;
            badge.style.paddingBottom = 2;
            badge.style.borderTopLeftRadius = 3;
            badge.style.borderTopRightRadius = 3;
            badge.style.borderBottomLeftRadius = 3;
            badge.style.borderBottomRightRadius = 3;
            StyleStrong(badge);
            badge.style.fontSize = 10;
            badge.style.backgroundColor = BadgeColor(kind);
            return badge;
        }

        public static Color BoxBackgroundColor()
        {
            return EditorGUIUtility.isProSkin
                ? new Color(0.20f, 0.20f, 0.20f, 1f)
                : new Color(0.88f, 0.88f, 0.88f, 1f);
        }

        public static VisualElement BadgeRow(params Label[] badges)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.flexWrap = Wrap.Wrap;
            foreach (var badge in badges ?? Array.Empty<Label>())
            {
                if (badge != null)
                {
                    badge.style.marginRight = 4;
                    badge.style.marginBottom = 4;
                    row.Add(badge);
                }
            }

            return row;
        }

        public static Button SmallButton(string text, Action action)
        {
            var button = new Button(action) { text = text };
            button.style.height = 22;
            return button;
        }

        public static Button InlineButton(string text, Action action)
        {
            var button = SmallButton(text, action);
            button.style.minWidth = 48;
            button.style.marginLeft = 6;
            button.style.flexGrow = 0;
            return button;
        }

        public static void AddHelpBox(VisualElement parent, string message, HelpBoxMessageType type)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                parent.Add(new HelpBox(message, type));
            }
        }

        private static Color BorderColor()
        {
            return EditorGUIUtility.isProSkin
                ? new Color(0.28f, 0.28f, 0.28f, 1f)
                : new Color(0.68f, 0.68f, 0.68f, 1f);
        }

        private static Color BadgeColor(StatusKind kind)
        {
            return kind switch
            {
                StatusKind.Success => new Color(0.18f, 0.42f, 0.24f, 1f),
                StatusKind.Warning => new Color(0.55f, 0.38f, 0.12f, 1f),
                StatusKind.Error => new Color(0.54f, 0.16f, 0.16f, 1f),
                _ => EditorGUIUtility.isProSkin
                    ? new Color(0.24f, 0.24f, 0.24f, 1f)
                    : new Color(0.76f, 0.76f, 0.76f, 1f)
            };
        }
    }

    public enum StatusKind
    {
        Neutral,
        Success,
        Warning,
        Error
    }

    public sealed class BlendShareFeatureBadge : VisualElement
    {
        private readonly VisualElement expandedContent;
        private bool expanded;

        public BlendShareFeatureBadge(string label, VisualElement expandedContent = null)
        {
            this.expandedContent = expandedContent;

            style.flexDirection = FlexDirection.Column;
            style.alignSelf = Align.FlexStart;
            style.marginRight = 4;
            style.marginBottom = 4;

            var header = BlendShareInspectorUi.Badge(label);
            header.style.alignSelf = Align.FlexStart;
            Add(header);

            if (expandedContent == null)
            {
                return;
            }

            header.pickingMode = PickingMode.Position;
            header.tooltip = Localization.S("common.expand");
            this.expandedContent.style.display = DisplayStyle.None;
            this.expandedContent.style.marginTop = 6;
            Add(this.expandedContent);
            header.RegisterCallback<MouseUpEvent>(evt =>
            {
                if (evt.button != 0 || evt.clickCount > 1)
                {
                    return;
                }

                SetExpanded(!expanded);
                header.tooltip = expanded ? Localization.S("common.collapse") : Localization.S("common.expand");
                evt.StopPropagation();
            });
        }

        private void SetExpanded(bool value)
        {
            expanded = value;
            expandedContent.style.display = expanded ? DisplayStyle.Flex : DisplayStyle.None;

            if (expanded)
            {
                style.alignSelf = Align.Stretch;
                style.flexGrow = 1;
                style.flexBasis = Length.Percent(100);
                style.width = Length.Percent(100);
                return;
            }

            style.alignSelf = Align.FlexStart;
            style.flexGrow = 0;
            style.flexBasis = StyleKeyword.Auto;
            style.width = StyleKeyword.Auto;
            style.borderTopWidth = 0;
            style.borderRightWidth = 0;
            style.borderBottomWidth = 0;
            style.borderLeftWidth = 0;
            style.borderTopLeftRadius = 0;
            style.borderTopRightRadius = 0;
            style.borderBottomLeftRadius = 0;
            style.borderBottomRightRadius = 0;
            style.paddingLeft = 0;
            style.paddingRight = 0;
            style.paddingTop = 0;
            style.paddingBottom = 0;
            style.backgroundColor = StyleKeyword.Null;
        }
    }
}
