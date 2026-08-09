using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Triturbo.BlendShare.Core
{
    internal sealed class StickyHeaderElement : VisualElement
    {
        private readonly VisualElement contentRoot;
        private readonly StickyHeaderLayoutState layoutState = new();
        private ScrollView scrollView;
        private IReadOnlyList<StickyHeaderSection> sections = Array.Empty<StickyHeaderSection>();
        private StickyHeaderSection overview;
        private StickyHeaderSection displayedSection;
        private Action contentChanged;
        private Action<float> scrollCallback;
        private bool refreshQueued;
        private bool forceRefreshQueued;

        public StickyHeaderElement()
        {
            style.flexShrink = 0f;
            contentRoot = new VisualElement();
            contentRoot.style.flexGrow = 1f;
            Add(contentRoot);
        }

        public void Bind(
            ScrollView targetScrollView,
            IReadOnlyList<StickyHeaderSection> targetSections,
            StickyHeaderSection targetOverview,
            Action onContentChanged = null)
        {
            UnregisterCallback<GeometryChangedEvent>(OnGeometryChanged);
            UnbindScrollView();
            scrollView = targetScrollView;
            sections = targetSections ?? Array.Empty<StickyHeaderSection>();
            overview = targetOverview;
            contentChanged = onContentChanged;
            layoutState.Reset();
            if (scrollView != null)
            {
                scrollCallback = _ => RequestActiveSectionRefresh();
                scrollView.verticalScroller.valueChanged += scrollCallback;
            }

            RegisterCallback<GeometryChangedEvent>(OnGeometryChanged);
            Refresh();
        }

        public void Refresh()
        {
            Refresh(true);
        }

        internal void RefreshActiveSection()
        {
            Refresh(false);
        }

        private void Refresh(bool force)
        {
            refreshQueued = false;
            forceRefreshQueued = false;
            var active = GetActiveSection() ?? overview;
            if (!force && ReferenceEquals(active, displayedSection))
            {
                return;
            }

            displayedSection = active;
            contentRoot.Clear();
            if (active?.CreateContent == null)
            {
                style.display = DisplayStyle.None;
                return;
            }

            style.display = DisplayStyle.Flex;
            var context = new StickyHeaderContext(
                ReferenceEquals(active, overview),
                RequestRefresh,
                contentChanged,
                () => ScrollToSectionTop(active));
            var content = active.CreateContent(context);
            if (content is StickyHeaderBar bar)
            {
                bar.BindToScrollView(() => scrollView, layoutState);
            }

            contentRoot.Add(content);
        }

        public void RequestRefresh()
        {
            QueueRefresh(true);
        }

        private void RequestActiveSectionRefresh()
        {
            QueueRefresh(false);
        }

        private void QueueRefresh(bool force)
        {
            forceRefreshQueued |= force;
            if (refreshQueued)
            {
                return;
            }

            refreshQueued = true;
            schedule.Execute(() => Refresh(forceRefreshQueued));
        }

        public void Unbind()
        {
            UnbindScrollView();
            UnregisterCallback<GeometryChangedEvent>(OnGeometryChanged);
            contentChanged = null;
            displayedSection = null;
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
            RequestActiveSectionRefresh();
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
        private readonly Action requestRefresh;
        private readonly Action contentChanged;

        public bool IsOverview { get; }
        public Action ScrollToSection { get; }

        public StickyHeaderContext(
            bool isOverview,
            Action requestRefresh,
            Action contentChanged,
            Action scrollToSection)
        {
            IsOverview = isOverview;
            this.requestRefresh = requestRefresh;
            this.contentChanged = contentChanged;
            ScrollToSection = scrollToSection;
        }

        public void ApplyChange(Action mutation)
        {
            mutation?.Invoke();
            contentChanged?.Invoke();
            requestRefresh?.Invoke();
        }

    }

    internal sealed class StickyHeaderSection
    {
        public VisualElement Root { get; }
        public VisualElement Anchor { get; }
        public Func<StickyHeaderContext, VisualElement> CreateContent { get; }

        public StickyHeaderSection(
            VisualElement root,
            VisualElement anchor,
            Func<StickyHeaderContext, VisualElement> createContent)
        {
            Root = root;
            Anchor = anchor;
            CreateContent = createContent;
        }
    }

    internal sealed class StickyHeaderBar : VisualElement
    {
        private const float HorizontalPadding = 6f;
        private const float MinimumTitleWidth = 140f;
        private readonly VisualElement main;
        private Func<ScrollView> scrollViewGetter;
        private ScrollView boundScrollView;
        private StickyHeaderLayoutState layoutState;
        private bool layoutQueued;
        private float naturalToggleWidth;
        private float naturalActionWidth;
        private float rightContentPadding = HorizontalPadding;

        public VisualElement Leading { get; }
        public VisualElement Title { get; }
        public VisualElement Actions { get; }
        public VisualElement Trailing { get; }

        public StickyHeaderBar()
        {
            ApplyContainerStyle(this);

            main = CreateSlot(true);
            main.style.minWidth = MinimumTitleWidth;
            main.style.overflow = Overflow.Hidden;
            Leading = CreateSlot(false);
            Title = CreateSlot(true);
            Title.style.minWidth = 0f;
            Actions = CreateSlot(false);
            Trailing = CreateSlot(false);

            main.Add(Leading);
            main.Add(Title);
            main.Add(Actions);
            Add(main);
            Add(Trailing);
        }

        internal void SetRightContentPadding(float value)
        {
            rightContentPadding = Mathf.Max(0f, value);
            style.paddingRight = rightContentPadding;
        }

        internal void BindToScrollView(
            Func<ScrollView> targetGetter,
            StickyHeaderLayoutState sharedLayoutState)
        {
            scrollViewGetter = targetGetter;
            layoutState = sharedLayoutState;
            ApplyCachedLayoutState();
            RegisterCallback<AttachToPanelEvent>(OnAttachToPanel);
            RegisterCallback<DetachFromPanelEvent>(OnDetachFromPanel);
            RegisterCallback<GeometryChangedEvent>(OnLayoutGeometryChanged);
            if (panel != null)
            {
                BindScrollView();
            }
        }

        private static VisualElement CreateSlot(bool grow)
        {
            var slot = new VisualElement();
            slot.style.flexDirection = FlexDirection.Row;
            slot.style.alignItems = Align.Center;
            slot.style.alignSelf = Align.Stretch;
            slot.style.flexGrow = grow ? 1f : 0f;
            slot.style.flexShrink = grow ? 1f : 0f;
            return slot;
        }

        private static void ApplyContainerStyle(VisualElement element)
        {
            element.style.flexDirection = FlexDirection.Row;
            element.style.alignItems = Align.Center;
            element.style.flexGrow = 0f;
            element.style.flexShrink = 0f;
            element.style.marginBottom = 4f;
            element.style.height = 28f;
            element.style.minHeight = 28f;
            element.style.maxHeight = 28f;
            element.style.paddingLeft = HorizontalPadding;
            element.style.paddingRight = HorizontalPadding;
            element.style.paddingTop = 0f;
            element.style.paddingBottom = 0f;
            element.style.backgroundColor = new Color(0.32f, 0.32f, 0.32f);
            element.style.borderBottomWidth = 1f;
            element.style.borderTopWidth = 1f;
            element.style.borderLeftWidth = 1f;
            element.style.borderRightWidth = 1f;
            element.style.borderBottomColor = new Color(0.35f, 0.35f, 0.35f);
            element.style.borderTopColor = new Color(0.35f, 0.35f, 0.35f);
            element.style.borderLeftColor = new Color(0.35f, 0.35f, 0.35f);
            element.style.borderRightColor = new Color(0.35f, 0.35f, 0.35f);
            element.style.borderTopLeftRadius = 6f;
            element.style.borderTopRightRadius = 6f;
            element.style.borderBottomLeftRadius = 6f;
            element.style.borderBottomRightRadius = 6f;
        }

        internal static void ResolveResponsiveVisibility(
            float availableWidth,
            float titleWidth,
            float toggleWidth,
            float actionWidth,
            out bool showToggles,
            out bool showActions)
        {
            showToggles = availableWidth >= titleWidth + toggleWidth;
            showActions = showToggles &&
                          availableWidth >= titleWidth + toggleWidth + actionWidth;
        }

        private void OnAttachToPanel(AttachToPanelEvent evt)
        {
            BindScrollView();
        }

        private void OnDetachFromPanel(DetachFromPanelEvent evt)
        {
            UnbindScrollView();
        }

        private void BindScrollView()
        {
            UnbindScrollView();
            boundScrollView = scrollViewGetter?.Invoke();
            boundScrollView?.contentViewport.RegisterCallback<GeometryChangedEvent>(OnLayoutGeometryChanged);
            QueueLayoutUpdate();
        }

        private void UnbindScrollView()
        {
            boundScrollView?.contentViewport.UnregisterCallback<GeometryChangedEvent>(OnLayoutGeometryChanged);
            boundScrollView = null;
        }

        private void OnLayoutGeometryChanged(GeometryChangedEvent evt)
        {
            QueueLayoutUpdate();
        }

        private void QueueLayoutUpdate()
        {
            if (layoutQueued)
            {
                return;
            }

            layoutQueued = true;
            schedule.Execute(UpdateResponsiveLayout);
        }

        private void UpdateResponsiveLayout()
        {
            layoutQueued = false;
            var viewport = boundScrollView?.contentViewport;
            if (viewport == null || viewport.panel == null ||
                float.IsNaN(layout.width) || layout.width <= 0f)
            {
                return;
            }

            if (Leading.resolvedStyle.display != DisplayStyle.None ||
                Trailing.resolvedStyle.display != DisplayStyle.None)
            {
                naturalToggleWidth = Mathf.Max(
                    naturalToggleWidth,
                    Mathf.Max(0f, Leading.layout.width) + Mathf.Max(0f, Trailing.layout.width));
            }

            if (Actions.resolvedStyle.display != DisplayStyle.None)
            {
                naturalActionWidth = Mathf.Max(naturalActionWidth, Mathf.Max(0f, Actions.layout.width));
            }

            if (layoutState != null)
            {
                naturalToggleWidth = Mathf.Max(naturalToggleWidth, layoutState.NaturalToggleWidth);
                naturalActionWidth = Mathf.Max(naturalActionWidth, layoutState.NaturalActionWidth);
            }

            float scrollbarInset = Mathf.Max(
                0f,
                boundScrollView.worldBound.xMax - viewport.worldBound.xMax);
            // The viewport shrinks by the scrollbar width, and the table content
            // is additionally inset by its own row padding. Pad by both so the
            // bar's trailing toggle columns line up with the table columns.
            float padding = rightContentPadding + scrollbarInset;
            if (!Mathf.Approximately(resolvedStyle.paddingRight, padding))
            {
                style.paddingRight = padding;
            }

            float availableWidth = Mathf.Max(
                0f,
                layout.width - resolvedStyle.paddingLeft - padding -
                resolvedStyle.borderLeftWidth - resolvedStyle.borderRightWidth);
            ResolveResponsiveVisibility(
                availableWidth,
                MinimumTitleWidth,
                naturalToggleWidth,
                naturalActionWidth,
                out bool showToggles,
                out bool showActions);
            Leading.style.display = showToggles ? DisplayStyle.Flex : DisplayStyle.None;
            Trailing.style.display = showToggles ? DisplayStyle.Flex : DisplayStyle.None;
            Actions.style.display = showActions ? DisplayStyle.Flex : DisplayStyle.None;
            if (layoutState != null)
            {
                layoutState.HasValue = true;
                layoutState.NaturalToggleWidth = naturalToggleWidth;
                layoutState.NaturalActionWidth = naturalActionWidth;
                layoutState.ResolvedRightPadding = padding;
                layoutState.ShowToggles = showToggles;
                layoutState.ShowActions = showActions;
            }
        }

        private void ApplyCachedLayoutState()
        {
            if (layoutState?.HasValue != true)
            {
                return;
            }

            naturalToggleWidth = layoutState.NaturalToggleWidth;
            naturalActionWidth = layoutState.NaturalActionWidth;
            style.paddingRight = Mathf.Max(
                rightContentPadding,
                layoutState.ResolvedRightPadding);
            Leading.style.display = layoutState.ShowToggles ? DisplayStyle.Flex : DisplayStyle.None;
            Trailing.style.display = layoutState.ShowToggles ? DisplayStyle.Flex : DisplayStyle.None;
            Actions.style.display = layoutState.ShowActions ? DisplayStyle.Flex : DisplayStyle.None;
        }
    }

    internal sealed class StickyHeaderLayoutState
    {
        public bool HasValue { get; set; }
        public float NaturalToggleWidth { get; set; }
        public float NaturalActionWidth { get; set; }
        public float ResolvedRightPadding { get; set; }
        public bool ShowToggles { get; set; } = true;
        public bool ShowActions { get; set; } = true;

        public void Reset()
        {
            HasValue = false;
            NaturalToggleWidth = 0f;
            NaturalActionWidth = 0f;
            ResolvedRightPadding = 0f;
            ShowToggles = true;
            ShowActions = true;
        }
    }

    internal readonly struct StickyHeaderToggleState
    {
        public bool Value { get; }
        public bool Mixed { get; }
        public bool Enabled { get; }

        public StickyHeaderToggleState(bool value, bool mixed = false, bool enabled = true)
        {
            Value = value;
            Mixed = mixed;
            Enabled = enabled;
        }

        public static StickyHeaderToggleState FromCounts(
            int selectedCount,
            int totalCount,
            bool enabled = true)
        {
            return new StickyHeaderToggleState(
                totalCount > 0 && selectedCount == totalCount,
                selectedCount > 0 && selectedCount < totalCount,
                enabled);
        }
    }

    internal static class StickyHeaderControls
    {
        public static Button CreateTitleButton(string text, Action clicked)
        {
            var button = new Button(clicked) { text = text };
            button.style.flexGrow = 1f;
            button.style.minWidth = 0f;
            button.style.overflow = Overflow.Hidden;
            button.style.textOverflow = TextOverflow.Ellipsis;
            button.style.unityTextOverflowPosition = TextOverflowPosition.End;
            ApplyFieldVerticalAlignment(button);
            button.style.unityTextAlign = TextAnchor.MiddleLeft;
            button.style.backgroundColor = Color.clear;
            button.style.borderBottomWidth = 0f;
            button.style.borderTopWidth = 0f;
            button.style.borderLeftWidth = 0f;
            button.style.borderRightWidth = 0f;
            return button;
        }

        public static Button CreateActionButton(string text, Action clicked)
        {
            var button = new Button(clicked) { text = text };
            ApplyFieldVerticalAlignment(button);
            button.style.marginLeft = 4f;
            return button;
        }

        public static Toggle CreatePrimaryToggle(
            StickyHeaderToggleState state,
            Action<bool> changed,
            string name = null)
        {
            var toggle = CreateToggle(state, changed, name);
            toggle.style.width = 22f;
            toggle.style.marginRight = 4f;
            return toggle;
        }

        public static VisualElement CreateToggleCell(
            string label,
            StickyHeaderToggleState state,
            Action<bool> changed,
            string name = null,
            float width = 170f,
            float toggleWidth = 0f)
        {
            var cell = new VisualElement();
            cell.style.width = width;
            cell.style.flexShrink = 0f;
            cell.style.flexDirection = FlexDirection.Row;
            cell.style.alignItems = Align.Center;
            cell.style.justifyContent = Justify.FlexEnd;

            if (!string.IsNullOrEmpty(label))
            {
                var text = new Label(label);
                text.style.flexShrink = 0f;
                text.style.alignSelf = Align.Center;
                text.style.marginTop = 0f;
                text.style.marginBottom = 0f;
                text.style.marginRight = 6f;
                cell.Add(text);
            }

            var toggle = CreateToggle(state, changed, name);
            // Keep the toggle's natural width so the checkbox lines up with the
            // feature table's right-justified toggles in the same column.
            // Callers with wider row toggles (e.g. blendshape rows) can override.
            if (toggleWidth > 0f)
            {
                toggle.style.width = toggleWidth;
            }

            cell.Add(toggle);
            return cell;
        }

        private static Toggle CreateToggle(
            StickyHeaderToggleState state,
            Action<bool> changed,
            string name)
        {
            var toggle = new Toggle { name = name };
            ApplyFieldVerticalAlignment(toggle);
            toggle.SetEnabled(state.Enabled);
            toggle.showMixedValue = state.Mixed;
            toggle.SetValueWithoutNotify(state.Value);
            toggle.RegisterValueChangedCallback(evt => changed?.Invoke(evt.newValue));
            return toggle;
        }

        private static void ApplyFieldVerticalAlignment(VisualElement field)
        {
            field.style.alignSelf = Align.Center;
            field.style.marginTop = 0f;
            field.style.marginBottom = 0f;
        }
    }
}
