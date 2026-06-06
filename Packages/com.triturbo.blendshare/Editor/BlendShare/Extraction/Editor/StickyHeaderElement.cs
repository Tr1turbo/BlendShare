using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Triturbo.BlendShare.Core
{
    internal sealed class StickyHeaderElement : VisualElement
    {
        private readonly Func<StickyHeaderContext, VisualElement> contentFactory;
        private readonly VisualElement contentRoot;
        private ScrollView scrollView;
        private IReadOnlyList<StickyHeaderSection> sections = Array.Empty<StickyHeaderSection>();
        private StickyHeaderSection overview;
        private Action<float> scrollCallback;
        private bool refreshQueued;

        public StickyHeaderElement(Func<StickyHeaderContext, VisualElement> contentFactory)
        {
            this.contentFactory = contentFactory ?? throw new ArgumentNullException(nameof(contentFactory));
            style.flexDirection = FlexDirection.Row;
            style.alignItems = Align.Center;
            style.marginBottom = 4f;
            style.minHeight = 28f;
            style.paddingLeft = 6f;
            style.paddingRight = 6f;
            style.backgroundColor = new Color(0.32f, 0.32f, 0.32f);
            style.borderBottomWidth = 1f;
            style.borderTopWidth = 1f;
            style.borderLeftWidth = 1f;
            style.borderRightWidth = 1f;
            style.borderBottomColor = new Color(0.35f, 0.35f, 0.35f);
            style.borderTopColor = new Color(0.35f, 0.35f, 0.35f);
            style.borderLeftColor = new Color(0.35f, 0.35f, 0.35f);
            style.borderRightColor = new Color(0.35f, 0.35f, 0.35f);
            style.borderTopLeftRadius = 6f;
            style.borderTopRightRadius = 6f;
            style.borderBottomLeftRadius = 6f;
            style.borderBottomRightRadius = 6f;

            contentRoot = new VisualElement();
            contentRoot.style.flexGrow = 1f;
            contentRoot.style.flexDirection = FlexDirection.Row;
            contentRoot.style.alignItems = Align.Center;
            Add(contentRoot);
        }

        public static StickyHeaderElement CreateCommandBar(
            string enableAllLabel = "Enable All",
            string disableAllLabel = "Disable All",
            string restoreLabel = "Auto",
            string disableChangedLabel = "Disable Diff")
        {
            return new StickyHeaderElement(context =>
                CreateCommandBarContent(
                    context,
                    enableAllLabel,
                    disableAllLabel,
                    restoreLabel,
                    disableChangedLabel));
        }

        public void Bind(
            ScrollView targetScrollView,
            IReadOnlyList<StickyHeaderSection> targetSections,
            StickyHeaderSection targetOverview)
        {
            UnregisterCallback<GeometryChangedEvent>(OnGeometryChanged);
            UnbindScrollView();
            scrollView = targetScrollView;
            sections = targetSections ?? Array.Empty<StickyHeaderSection>();
            overview = targetOverview;
            if (scrollView != null)
            {
                scrollCallback = _ => RequestRefresh();
                scrollView.verticalScroller.valueChanged += scrollCallback;
            }

            RegisterCallback<GeometryChangedEvent>(OnGeometryChanged);
            Refresh();
        }

        public void Refresh()
        {
            refreshQueued = false;
            var active = GetActiveSection() ?? overview;
            contentRoot.Clear();
            if (active == null)
            {
                style.display = DisplayStyle.None;
                return;
            }

            style.display = DisplayStyle.Flex;
            var context = new StickyHeaderContext(
                active,
                ReferenceEquals(active, overview),
                RequestRefresh,
                () => ScrollToSectionTop(active));
            contentRoot.Add(contentFactory(context));
        }

        public void RequestRefresh()
        {
            if (refreshQueued)
            {
                return;
            }

            refreshQueued = true;
            schedule.Execute(Refresh);
        }

        public void Unbind()
        {
            UnbindScrollView();
            UnregisterCallback<GeometryChangedEvent>(OnGeometryChanged);
            contentRoot.Clear();
        }

        private StickyHeaderSection GetActiveSection()
        {
            if (scrollView == null || sections == null || sections.Count == 0)
            {
                return null;
            }

            Rect scrollBounds = scrollView.worldBound;
            if (sections[0]?.Anchor == null ||
                sections[0].Anchor.worldBound.yMin >= scrollBounds.yMin)
            {
                return null;
            }

            StickyHeaderSection activeSection = null;
            foreach (var section in sections)
            {
                if (section?.Root == null || section.Anchor == null)
                {
                    continue;
                }

                Rect anchorBounds = section.Anchor.worldBound;
                if (anchorBounds.yMin >= scrollBounds.yMin)
                {
                    break;
                }

                activeSection = section;
            }

            return activeSection;
        }

        private void OnGeometryChanged(GeometryChangedEvent evt)
        {
            RequestRefresh();
        }

        private void UnbindScrollView()
        {
            if (scrollView != null && scrollCallback != null)
            {
                scrollView.verticalScroller.valueChanged -= scrollCallback;
            }

            scrollView = null;
            scrollCallback = null;
        }

        private static VisualElement CreateCommandBarContent(
            StickyHeaderContext context,
            string enableAllLabel,
            string disableAllLabel,
            string restoreLabel,
            string disableChangedLabel)
        {
            var root = new VisualElement();
            root.style.flexDirection = FlexDirection.Row;
            root.style.alignItems = Align.Center;
            root.style.flexGrow = 1f;

            var itemEnabled = new Toggle();
            itemEnabled.style.width = 22f;
            itemEnabled.style.marginRight = 4f;
            itemEnabled.style.display = context.IsOverview ? DisplayStyle.None : DisplayStyle.Flex;
            itemEnabled.SetValueWithoutNotify(context.Section.IsEnabled());
            itemEnabled.RegisterValueChangedCallback(evt =>
            {
                context.Section.SetEnabled(evt.newValue);
                context.RequestRefresh();
            });

            var titleButton = new Button();
            titleButton.text = context.Section.Title;
            titleButton.style.flexGrow = 1f;
            titleButton.style.unityTextAlign = TextAnchor.MiddleLeft;
            titleButton.style.backgroundColor = Color.clear;
            titleButton.style.borderBottomWidth = 0f;
            titleButton.style.borderTopWidth = 0f;
            titleButton.style.borderLeftWidth = 0f;
            titleButton.style.borderRightWidth = 0f;
            titleButton.clicked += context.ScrollToSection;

            var enableAll = CreateHeaderButton(enableAllLabel);
            var disableAll = CreateHeaderButton(disableAllLabel);
            var restore = CreateHeaderButton(restoreLabel);
            var disableChanged = CreateHeaderButton(disableChangedLabel);
            enableAll.clicked += () => InvokeAndRefresh(context, context.Section.EnableAll);
            disableAll.clicked += () => InvokeAndRefresh(context, context.Section.DisableAll);
            restore.clicked += () => InvokeAndRefresh(context, context.Section.Restore);
            disableChanged.clicked += () => InvokeAndRefresh(context, context.Section.DisableChanged);

            root.Add(itemEnabled);
            root.Add(titleButton);
            root.Add(enableAll);
            root.Add(disableAll);
            root.Add(restore);
            root.Add(disableChanged);
            return root;
        }

        private static void InvokeAndRefresh(StickyHeaderContext context, Action action)
        {
            action?.Invoke();
            context.RequestRefresh();
        }

        private static Button CreateHeaderButton(string text)
        {
            var button = new Button { text = text };
            button.style.marginLeft = 4f;
            return button;
        }

        private void ScrollToSectionTop(StickyHeaderSection section)
        {
            if (scrollView == null || section?.Root == null)
            {
                return;
            }

            scrollView.schedule.Execute(() =>
            {
                float y = Mathf.Max(0f, section.Root.layout.y);
                scrollView.scrollOffset = new Vector2(scrollView.scrollOffset.x, y);
            });
        }
    }

    internal sealed class StickyHeaderContext
    {
        public StickyHeaderSection Section { get; }
        public bool IsOverview { get; }
        public Action RequestRefresh { get; }
        public Action ScrollToSection { get; }

        public StickyHeaderContext(
            StickyHeaderSection section,
            bool isOverview,
            Action requestRefresh,
            Action scrollToSection)
        {
            Section = section;
            IsOverview = isOverview;
            RequestRefresh = requestRefresh;
            ScrollToSection = scrollToSection;
        }
    }

    internal sealed class StickyHeaderSection
    {
        private readonly Func<string> titleGetter;
        private readonly Func<int> selectedCountGetter;

        public VisualElement Root { get; }
        public VisualElement Anchor { get; }
        public string Title => titleGetter();
        public int SelectedCount => selectedCountGetter();
        public int TotalCount { get; }
        public Func<bool> IsEnabled { get; }
        public Action<bool> SetEnabled { get; }
        public Action EnableAll { get; }
        public Action DisableAll { get; }
        public Action Restore { get; }
        public Action DisableChanged { get; }

        public StickyHeaderSection(
            VisualElement root,
            VisualElement anchor,
            Func<string> titleGetter,
            Func<int> selectedCountGetter,
            int totalCount,
            Func<bool> isEnabled,
            Action<bool> setEnabled,
            Action enableAll,
            Action disableAll,
            Action restore,
            Action disableChanged)
        {
            Root = root;
            Anchor = anchor;
            this.titleGetter = titleGetter;
            this.selectedCountGetter = selectedCountGetter;
            TotalCount = totalCount;
            IsEnabled = isEnabled;
            SetEnabled = setEnabled;
            EnableAll = enableAll;
            DisableAll = disableAll;
            Restore = restore;
            DisableChanged = disableChanged;
        }
    }

    internal static class StickyHeaderOverview
    {
        public static StickyHeaderSection Create(
            IReadOnlyList<StickyHeaderSection> sections,
            Func<string> titlePrefixGetter)
        {
            sections ??= Array.Empty<StickyHeaderSection>();
            return new StickyHeaderSection(
                null,
                null,
                () => $"{titlePrefixGetter()} ({sections.Sum(section => section.SelectedCount)}/{sections.Sum(section => section.TotalCount)})",
                () => sections.Sum(section => section.SelectedCount),
                sections.Sum(section => section.TotalCount),
                () => sections.Any(section => section.IsEnabled()),
                value =>
                {
                    foreach (var section in sections)
                    {
                        section.SetEnabled(value);
                    }
                },
                () =>
                {
                    foreach (var section in sections)
                    {
                        section.EnableAll();
                    }
                },
                () =>
                {
                    foreach (var section in sections)
                    {
                        section.DisableAll();
                    }
                },
                () =>
                {
                    foreach (var section in sections)
                    {
                        section.Restore();
                    }
                },
                () =>
                {
                    foreach (var section in sections)
                    {
                        section.DisableChanged();
                    }
                });
        }
    }
}
