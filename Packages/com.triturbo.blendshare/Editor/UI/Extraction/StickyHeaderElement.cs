using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Triturbo.BlendShare.UI.Extraction
{
    internal sealed class StickyHeaderElement : VisualElement
    {
        private readonly VisualElement m_ContentRoot;
        private readonly StickyHeaderLayoutState m_LayoutState = new StickyHeaderLayoutState();
        private ScrollView m_ScrollView;
        private IReadOnlyList<StickyHeaderSection> m_Sections = Array.Empty<StickyHeaderSection>();
        private StickyHeaderSection m_Overview;
        private StickyHeaderSection m_DisplayedSection;
        private Action m_ContentChanged;
        private Action<float> m_ScrollCallback;
        private bool m_RefreshQueued;
        private bool m_ForceRefreshQueued;

        public StickyHeaderElement()
        {
            style.flexShrink = 0f;
            m_ContentRoot = new VisualElement();
            m_ContentRoot.style.flexGrow = 1f;
            Add(m_ContentRoot);
        }

        public void Bind(
            ScrollView targetScrollView,
            IReadOnlyList<StickyHeaderSection> targetSections,
            StickyHeaderSection targetOverview,
            Action onContentChanged = null)
        {
            UnregisterCallback<GeometryChangedEvent>(OnGeometryChanged);
            UnbindScrollView();
            m_ScrollView = targetScrollView;
            m_Sections = targetSections ?? Array.Empty<StickyHeaderSection>();
            m_Overview = targetOverview;
            m_ContentChanged = onContentChanged;
            m_LayoutState.Reset();
            if (m_ScrollView != null)
            {
                m_ScrollCallback = _ => RequestActiveSectionRefresh();
                m_ScrollView.verticalScroller.valueChanged += m_ScrollCallback;
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
            m_RefreshQueued = false;
            m_ForceRefreshQueued = false;
            var active = GetActiveSection() ?? m_Overview;
            if (!force && ReferenceEquals(active, m_DisplayedSection))
            {
                return;
            }

            m_DisplayedSection = active;
            m_ContentRoot.Clear();
            if (active?.CreateContent == null)
            {
                style.display = DisplayStyle.None;
                return;
            }

            style.display = DisplayStyle.Flex;
            var context = new StickyHeaderContext(
                ReferenceEquals(active, m_Overview),
                RequestRefresh,
                m_ContentChanged,
                () => ScrollToSectionTop(active));
            var content = active.CreateContent(context);
            if (content is StickyHeaderBar bar)
            {
                bar.BindToScrollView(() => m_ScrollView, m_LayoutState);
            }

            m_ContentRoot.Add(content);
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
            m_ForceRefreshQueued |= force;
            if (m_RefreshQueued)
            {
                return;
            }

            m_RefreshQueued = true;
            schedule.Execute(() => Refresh(m_ForceRefreshQueued));
        }

        public void Unbind()
        {
            UnbindScrollView();
            UnregisterCallback<GeometryChangedEvent>(OnGeometryChanged);
            m_ContentChanged = null;
            m_DisplayedSection = null;
            m_ContentRoot.Clear();
        }

        private StickyHeaderSection GetActiveSection()
        {
            if (m_ScrollView == null || m_Sections == null || m_Sections.Count == 0)
            {
                return null;
            }

            Rect scrollBounds = m_ScrollView.worldBound;
            if (m_Sections[0]?.Anchor == null ||
                m_Sections[0].Anchor.worldBound.yMin >= scrollBounds.yMin)
            {
                return null;
            }

            StickyHeaderSection activeSection = null;
            foreach (var section in m_Sections)
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
            if (m_ScrollView != null && m_ScrollCallback != null)
            {
                m_ScrollView.verticalScroller.valueChanged -= m_ScrollCallback;
            }

            m_ScrollView = null;
            m_ScrollCallback = null;
        }

        private void ScrollToSectionTop(StickyHeaderSection section)
        {
            if (m_ScrollView == null || section?.Root == null)
            {
                return;
            }

            m_ScrollView.schedule.Execute(() =>
            {
                float y = Mathf.Max(0f, section.Root.layout.y);
                m_ScrollView.scrollOffset = new Vector2(m_ScrollView.scrollOffset.x, y);
            });
        }
    }

    internal sealed class StickyHeaderContext
    {
        private readonly Action m_RequestRefresh;
        private readonly Action m_ContentChanged;

        public bool IsOverview { get; }
        public Action ScrollToSection { get; }

        public StickyHeaderContext(
            bool isOverview,
            Action requestRefresh,
            Action contentChanged,
            Action scrollToSection)
        {
            IsOverview = isOverview;
            m_RequestRefresh = requestRefresh;
            m_ContentChanged = contentChanged;
            ScrollToSection = scrollToSection;
        }

        public void ApplyChange(Action mutation)
        {
            mutation?.Invoke();
            m_ContentChanged?.Invoke();
            m_RequestRefresh?.Invoke();
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
        private const float k_HorizontalPadding = 6f;
        private const float k_MinimumTitleWidth = 140f;
        private readonly VisualElement m_Main;
        private Func<ScrollView> m_ScrollViewGetter;
        private ScrollView m_BoundScrollView;
        private StickyHeaderLayoutState m_LayoutState;
        private bool m_LayoutQueued;
        private float m_NaturalToggleWidth;
        private float m_NaturalActionWidth;
        private float m_RightContentPadding = k_HorizontalPadding;

        public VisualElement Leading { get; }
        public VisualElement Title { get; }
        public VisualElement Actions { get; }
        public VisualElement Trailing { get; }

        public StickyHeaderBar()
        {
            ApplyContainerStyle(this);

            m_Main = CreateSlot(true);
            m_Main.style.minWidth = k_MinimumTitleWidth;
            m_Main.style.overflow = Overflow.Hidden;
            Leading = CreateSlot(false);
            Title = CreateSlot(true);
            Title.style.minWidth = 0f;
            Actions = CreateSlot(false);
            Trailing = CreateSlot(false);

            m_Main.Add(Leading);
            m_Main.Add(Title);
            m_Main.Add(Actions);
            Add(m_Main);
            Add(Trailing);
        }

        internal void SetRightContentPadding(float value)
        {
            m_RightContentPadding = Mathf.Max(0f, value);
            style.paddingRight = m_RightContentPadding;
        }

        internal void BindToScrollView(
            Func<ScrollView> targetGetter,
            StickyHeaderLayoutState sharedLayoutState)
        {
            m_ScrollViewGetter = targetGetter;
            m_LayoutState = sharedLayoutState;
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
            element.style.paddingLeft = k_HorizontalPadding;
            element.style.paddingRight = k_HorizontalPadding;
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
            m_BoundScrollView = m_ScrollViewGetter?.Invoke();
            m_BoundScrollView?.contentViewport.RegisterCallback<GeometryChangedEvent>(OnLayoutGeometryChanged);
            QueueLayoutUpdate();
        }

        private void UnbindScrollView()
        {
            m_BoundScrollView?.contentViewport.UnregisterCallback<GeometryChangedEvent>(OnLayoutGeometryChanged);
            m_BoundScrollView = null;
        }

        private void OnLayoutGeometryChanged(GeometryChangedEvent evt)
        {
            QueueLayoutUpdate();
        }

        private void QueueLayoutUpdate()
        {
            if (m_LayoutQueued)
            {
                return;
            }

            m_LayoutQueued = true;
            schedule.Execute(UpdateResponsiveLayout);
        }

        private void UpdateResponsiveLayout()
        {
            m_LayoutQueued = false;
            var viewport = m_BoundScrollView?.contentViewport;
            if (viewport == null || viewport.panel == null ||
                float.IsNaN(layout.width) || layout.width <= 0f)
            {
                return;
            }

            if (Leading.resolvedStyle.display != DisplayStyle.None ||
                Trailing.resolvedStyle.display != DisplayStyle.None)
            {
                m_NaturalToggleWidth = Mathf.Max(
                    m_NaturalToggleWidth,
                    Mathf.Max(0f, Leading.layout.width) + Mathf.Max(0f, Trailing.layout.width));
            }

            if (Actions.resolvedStyle.display != DisplayStyle.None)
            {
                m_NaturalActionWidth = Mathf.Max(m_NaturalActionWidth, Mathf.Max(0f, Actions.layout.width));
            }

            if (m_LayoutState != null)
            {
                m_NaturalToggleWidth = Mathf.Max(m_NaturalToggleWidth, m_LayoutState.NaturalToggleWidth);
                m_NaturalActionWidth = Mathf.Max(m_NaturalActionWidth, m_LayoutState.NaturalActionWidth);
            }

            float scrollbarInset = Mathf.Max(
                0f,
                m_BoundScrollView.worldBound.xMax - viewport.worldBound.xMax);
            // The viewport shrinks by the scrollbar width, and the table content
            // is additionally inset by its own row padding. Pad by both so the
            // bar's trailing toggle columns line up with the table columns.
            float padding = m_RightContentPadding + scrollbarInset;
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
                k_MinimumTitleWidth,
                m_NaturalToggleWidth,
                m_NaturalActionWidth,
                out bool showToggles,
                out bool showActions);
            Leading.style.display = showToggles ? DisplayStyle.Flex : DisplayStyle.None;
            Trailing.style.display = showToggles ? DisplayStyle.Flex : DisplayStyle.None;
            Actions.style.display = showActions ? DisplayStyle.Flex : DisplayStyle.None;
            if (m_LayoutState != null)
            {
                m_LayoutState.HasValue = true;
                m_LayoutState.NaturalToggleWidth = m_NaturalToggleWidth;
                m_LayoutState.NaturalActionWidth = m_NaturalActionWidth;
                m_LayoutState.ResolvedRightPadding = padding;
                m_LayoutState.ShowToggles = showToggles;
                m_LayoutState.ShowActions = showActions;
            }
        }

        private void ApplyCachedLayoutState()
        {
            if (m_LayoutState?.HasValue != true)
            {
                return;
            }

            m_NaturalToggleWidth = m_LayoutState.NaturalToggleWidth;
            m_NaturalActionWidth = m_LayoutState.NaturalActionWidth;
            style.paddingRight = Mathf.Max(
                m_RightContentPadding,
                m_LayoutState.ResolvedRightPadding);
            Leading.style.display = m_LayoutState.ShowToggles ? DisplayStyle.Flex : DisplayStyle.None;
            Trailing.style.display = m_LayoutState.ShowToggles ? DisplayStyle.Flex : DisplayStyle.None;
            Actions.style.display = m_LayoutState.ShowActions ? DisplayStyle.Flex : DisplayStyle.None;
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
