using System;
using Acrux.Controls.Primitives;
using Acrux.Core;
using Acrux.Core.Input;
using Acrux.Core.Invalidation;
using Acrux.Core.PropertySystem;
using Acrux.Layout;
using Acrux.Media;
using Acrux.Rendering;
using Acrux.Styling;
using VRageMath;

namespace Acrux.Controls
{
    /// <summary>
    /// TabControl with the strip on top: each item gets a <see cref="TabItem"/> container whose header is the
    /// item (or its <see cref="ItemsControl.ItemTemplate"/>), and the selected item is shown below through
    /// <see cref="ContentTemplate"/>. A tab is always selected while there are items. Tabs stay on one line: when they
    /// overflow, the strip scrolls and arrow buttons appear on each side, and the selected tab is kept in view.
    /// </summary>
    public class TabControl : ItemsControl
    {
        static TabControl() { }

        public static readonly StyledProperty<int> SelectedIndexProperty =
            StyledProperty.Register<TabControl, int>(nameof(SelectedIndex), -1, UiInvalidation.Measure);
        public static readonly StyledProperty<IDataTemplate> ContentTemplateProperty =
            StyledProperty.Register<TabControl, IDataTemplate>(nameof(ContentTemplate), null, UiInvalidation.Measure);
        public static readonly StyledProperty<BackgroundBrush> BackgroundProperty =
            StyledProperty.Register<TabControl, BackgroundBrush>(
                nameof(Background), new SolidColorBrush(Color.Transparent));
        public static readonly StyledProperty<Color> BorderBrushProperty =
            StyledProperty.Register<TabControl, Color>(nameof(BorderBrush), Color.Transparent);
        public static readonly StyledProperty<Vector4> BorderThicknessProperty =
            StyledProperty.Register<TabControl, Vector4>(nameof(BorderThickness), Vector4.Zero, UiInvalidation.Measure);
        public static readonly StyledProperty<Vector4> PaddingProperty =
            StyledProperty.Register<TabControl, Vector4>(nameof(Padding), Vector4.Zero, UiInvalidation.Measure);
        public static readonly StyledProperty<float> FontSizeProperty =
            StyledProperty.Register<TabControl, float>(nameof(FontSize), float.NaN, UiInvalidation.Measure);

        readonly ScrollViewer _strip = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        readonly RepeatButton _scrollLeft = CreateScrollButton("M7 1 L2 6 L7 11");
        readonly RepeatButton _scrollRight = CreateScrollButton("M1 1 L6 6 L1 11");
        readonly ContentPresenter _contentHost;
        object _selectedContent;
        IDataTemplate _appliedContentTemplate;
        RectangleF _contentBounds;
        bool _bringSelectedIntoView = true;

        public TabControl()
        {
            AddChild(_scrollLeft);
            AddChild(_scrollRight);
            _scrollLeft.Clicked = () => ScrollTabs(-1);
            _scrollRight.Clicked = () => ScrollTabs(1);
            _strip.ScrollChanged = _ => UpdateScrollButtons();
            _contentHost = AddChild(new ContentPresenter());
        }

        public int SelectedIndex
        {
            get { return GetValue(SelectedIndexProperty); }
            set { SetValue(SelectedIndexProperty, value); }
        }

        public object SelectedItem
        {
            get
            {
                int index = SelectedIndex;
                return index >= 0 && index < ItemCount ? GetItemAt(index) : null;
            }
            set { SelectedIndex = value == null ? -1 : IndexOfItem(value); }
        }

        public object SelectedContent => _selectedContent;

        public IDataTemplate ContentTemplate
        {
            get { return GetValue(ContentTemplateProperty); }
            set { SetValue(ContentTemplateProperty, value); }
        }

        public BackgroundBrush Background
        {
            get { return GetValue(BackgroundProperty); }
            set { SetValue(BackgroundProperty, value); }
        }

        public Color BorderBrush
        {
            get { return GetValue(BorderBrushProperty); }
            set { SetValue(BorderBrushProperty, value); }
        }

        public Vector4 BorderThickness
        {
            get { return GetValue(BorderThicknessProperty); }
            set { SetValue(BorderThicknessProperty, Vector4.Max(value, Vector4.Zero)); }
        }

        public Vector4 Padding
        {
            get { return GetValue(PaddingProperty); }
            set { SetValue(PaddingProperty, value); }
        }

        /// <summary>Passed down to the tabs like inherited FontSize; NaN leaves the theme's size.</summary>
        public float FontSize
        {
            get { return GetValue(FontSizeProperty); }
            set { SetValue(FontSizeProperty, value); }
        }

        protected override Panel CreateDefaultItemsPanel()
        {
            return new StackPanel { Orientation = StackOrientation.Horizontal };
        }

        protected override void AttachItemsPanel(Panel panel)
        {
            if (_strip.Parent == null)
                AddChild(_strip);
            _strip.Child = panel;
        }

        protected override void DetachItemsPanel(Panel panel)
        {
            if (ReferenceEquals(_strip.Child, panel))
                _strip.Child = null;
        }

        static RepeatButton CreateScrollButton(string glyph)
        {
            var button = new RepeatButton { StyleClass = "tab-scroll", Visible = false };
            button.AddChild(new Shapes.Path
            {
                Data = Geometry.Parse(glyph),
                Width = 8,
                Height = 12,
                StrokeThickness = 2,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
            return button;
        }

        protected override VisualElement CreateContainer(object item, VisualElement content)
        {
            var tab = new TabItem();
            tab.SetGeneratedHeader(content, ItemTemplate == null);
            return tab;
        }

        protected override void PrepareContainer(VisualElement container, VisualElement content, object item)
        {
            TabItem tab = container as TabItem;
            if (tab == null)
                return;
            tab.Header = item;
            tab.Content = item;
            tab.Owner = this;
            if (!float.IsNaN(FontSize))
                tab.FontSize = FontSize;
        }

        protected override void OnItemsRefreshed()
        {
            int count = ItemCount;
            int index = SelectedIndex;
            if (count == 0)
                index = -1;
            else if (index < 0)
                index = 0;
            else if (index >= count)
                index = count - 1;
            if (index != SelectedIndex)
                SelectedIndex = index;
            else
                SynchronizeSelection();
        }

        protected override void OnPropertyChanged(IStyledProperty property)
        {
            base.OnPropertyChanged(property);
            if (property == SelectedIndexProperty)
            {
                _bringSelectedIntoView = true;
                SynchronizeSelection();
                Invalidate(UiInvalidation.Arrange);
            }
            else if (property == ContentTemplateProperty)
                SynchronizeSelection();
            else if (property == FontSizeProperty && !float.IsNaN(FontSize))
                for (int i = 0; i < ItemCount; i++)
                {
                    TabItem tab = GetContainerAt(i) as TabItem;
                    if (tab != null)
                        tab.FontSize = FontSize;
                }
        }

        internal void Select(TabItem tab)
        {
            for (int i = 0; i < ItemCount; i++)
                if (ReferenceEquals(GetContainerAt(i), tab))
                {
                    SelectedIndex = i;
                    return;
                }
        }

        internal void MoveSelection(int direction)
        {
            if (ItemCount > 0)
                SelectedIndex = Math.Max(0, Math.Min(ItemCount - 1, SelectedIndex + direction));
        }

        // Scrolls just far enough to fully show the next tab cut off on that side.
        void ScrollTabs(int direction)
        {
            float left = _strip.HorizontalOffset;
            float right = left + _strip.ViewportWidth;
            float origin = ItemsPanelRoot.Bounds.X;
            if (direction > 0)
            {
                for (int i = 0; i < ItemCount; i++)
                {
                    VisualElement tab = GetContainerAt(i);
                    if (tab != null && tab.Bounds.Right - origin > right + 0.5f)
                    {
                        _strip.ScrollTo(tab.Bounds.Right - origin - _strip.ViewportWidth, 0f);
                        break;
                    }
                }
            }
            else
            {
                for (int i = ItemCount - 1; i >= 0; i--)
                {
                    VisualElement tab = GetContainerAt(i);
                    if (tab != null && tab.Bounds.X - origin < left - 0.5f)
                    {
                        _strip.ScrollTo(tab.Bounds.X - origin, 0f);
                        break;
                    }
                }
            }
            UpdateScrollButtons();
        }

        void UpdateScrollButtons()
        {
            _scrollLeft.Enabled = _strip.HorizontalOffset > 0.5f;
            _scrollRight.Enabled = _strip.HorizontalOffset < _strip.MaxHorizontalOffset - 0.5f;
        }

        void SynchronizeSelection()
        {
            int selected = SelectedIndex;
            for (int i = 0; i < ItemCount; i++)
            {
                TabItem tab = GetContainerAt(i) as TabItem;
                if (tab != null)
                    tab.IsSelected = i == selected;
            }

            object content = selected >= 0 && selected < ItemCount ? GetItemAt(selected) : null;
            IDataTemplate template = ContentTemplate;
            if (ReferenceEquals(content, _selectedContent) && ReferenceEquals(template, _appliedContentTemplate) &&
                (content == null) == (_contentHost.Content == null))
                return;

            _selectedContent = content;
            _appliedContentTemplate = template;
            VisualElement previous = _contentHost.Content;
            _contentHost.Content = null;
            if (previous != null)
                previous.Dispose();
            _contentHost.DataContext = content;
            _contentHost.Content = BuildContent(content, template);
            Invalidate(UiInvalidation.Measure | UiInvalidation.Render);
        }

        static VisualElement BuildContent(object content, IDataTemplate template)
        {
            if (content == null)
                return null;
            VisualElement root = template == null ? null : template.Build(content);
            return root ?? new TextBlock { Text = content.ToString() };
        }

        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            float tabsWidth = base.MeasureOverride(new Vector2(float.PositiveInfinity, availableSize.Y)).X;
            Vector2 arrows = _scrollLeft.Measure(new Vector2(float.PositiveInfinity, availableSize.Y)) +
                _scrollRight.Measure(new Vector2(float.PositiveInfinity, availableSize.Y));
            bool overflow = tabsWidth > availableSize.X + 0.5f;
            Vector2 strip = _strip.Measure(new Vector2(
                overflow ? Deflate(availableSize.X, arrows.X) : availableSize.X, availableSize.Y));
            strip = new Vector2(overflow ? availableSize.X : strip.X, Math.Max(strip.Y, ItemsPanelRoot.DesiredSize.Y));
            Vector4 inset = BorderThickness + Padding;
            float horizontal = inset.X + inset.Z;
            float vertical = inset.Y + inset.W;
            Vector2 content = _contentHost.Measure(new Vector2(
                Deflate(availableSize.X, horizontal),
                Deflate(availableSize.Y, strip.Y + vertical)));
            return new Vector2(
                Math.Max(strip.X, content.X + horizontal),
                strip.Y + content.Y + vertical);
        }

        protected override void ArrangeChildren(RectangleF bounds)
        {
            float stripHeight = Math.Min(bounds.Height, ItemsPanelRoot.DesiredSize.Y);
            bool overflow = ItemsPanelRoot.DesiredSize.X > bounds.Width + 0.5f;
            if (_scrollLeft.Visible != overflow)
            {
                _scrollLeft.Visible = overflow;
                _scrollRight.Visible = overflow;
            }
            float leftWidth = overflow ? _scrollLeft.DesiredSize.X : 0f;
            float rightWidth = overflow ? _scrollRight.DesiredSize.X : 0f;
            _scrollLeft.ArrangeInSlot(new RectangleF(bounds.X, bounds.Y, leftWidth, stripHeight));
            _scrollRight.ArrangeInSlot(new RectangleF(bounds.Right - rightWidth, bounds.Y, rightWidth, stripHeight));
            _strip.ArrangeInSlot(new RectangleF(bounds.X + leftWidth, bounds.Y,
                Math.Max(0f, bounds.Width - leftWidth - rightWidth), stripHeight));
            if (_bringSelectedIntoView && SelectedIndex >= 0 && SelectedIndex < ItemCount)
            {
                VisualElement selected = GetContainerAt(SelectedIndex);
                if (selected != null)
                {
                    float origin = ItemsPanelRoot.Bounds.X;
                    _strip.BringIntoView(new RectangleF(selected.Bounds.X - origin, 0f, selected.Bounds.Width,
                        selected.Bounds.Height));
                    _bringSelectedIntoView = false;
                }
            }
            UpdateScrollButtons();
            _contentBounds = new RectangleF(bounds.X, bounds.Y + stripHeight, bounds.Width,
                Math.Max(0f, bounds.Height - stripHeight));
            Vector4 inset = BorderThickness + Padding;
            _contentHost.ArrangeInSlot(new RectangleF(
                _contentBounds.X + inset.X,
                _contentBounds.Y + inset.Y,
                Math.Max(0f, _contentBounds.Width - inset.X - inset.Z),
                Math.Max(0f, _contentBounds.Height - inset.Y - inset.W)));
        }

        protected override void DrawSelf(RenderContext context)
        {
            context.Fill(_contentBounds, Background);
            context.Border(_contentBounds, BorderBrush, BorderThickness);
        }

        static float Deflate(float available, float amount)
        {
            return float.IsPositiveInfinity(available) ? available : Math.Max(0f, available - amount);
        }
    }

    /// <summary>
    /// A tab header in a <see cref="TabControl"/> strip; its <see cref="Content"/> is shown while selected. Its look comes from the theme's ControlTemplate, which presents the header in PART_ContentPresenter.
    /// </summary>
    public class TabItem : TemplatedControl
    {
        static TabItem() { }

        public static readonly StyledProperty<bool> IsSelectedProperty =
            StyledProperty.Register<TabItem, bool>(nameof(IsSelected), false, UiInvalidation.Render);
        public static readonly StyledProperty<BackgroundBrush> BackgroundProperty =
            StyledProperty.Register<TabItem, BackgroundBrush>(
                nameof(Background), new SolidColorBrush(Color.Transparent));
        public static readonly StyledProperty<Color> BorderBrushProperty =
            StyledProperty.Register<TabItem, Color>(nameof(BorderBrush), Color.Transparent);
        public static readonly StyledProperty<Vector4> BorderThicknessProperty =
            StyledProperty.Register<TabItem, Vector4>(nameof(BorderThickness), Vector4.Zero, UiInvalidation.Measure);
        public static readonly StyledProperty<Vector4> CornerRadiusProperty =
            StyledProperty.Register<TabItem, Vector4>(nameof(CornerRadius), Vector4.Zero);
        public static readonly StyledProperty<Vector4> PaddingProperty =
            StyledProperty.Register<TabItem, Vector4>(nameof(Padding), Vector4.Zero, UiInvalidation.Measure);
        public static readonly StyledProperty<float> FontSizeProperty =
            StyledProperty.Register<TabItem, float>(nameof(FontSize), float.NaN, UiInvalidation.Measure);

        object _header;
        VisualElement _headerVisual;
        ContentPresenter _presenter;
        bool _ownsHeaderText;

        public object Header
        {
            get { return _header; }
            set
            {
                if (ReferenceEquals(_header, value))
                    return;
                _header = value;
                VisualElement visual = value as VisualElement;
                if (visual != null)
                {
                    _ownsHeaderText = false;
                    SetHeaderVisual(visual);
                }
                else if (_ownsHeaderText || _headerVisual == null)
                {
                    _ownsHeaderText = true;
                    TextBlock text = _headerVisual as TextBlock ?? new TextBlock();
                    text.Text = value == null ? string.Empty : value.ToString();
                    SetHeaderVisual(text);
                    ApplyFontSize();
                }
            }
        }

        public object Content { get; set; }

        public bool IsSelected
        {
            get { return GetValue(IsSelectedProperty); }
            set { SetValue(IsSelectedProperty, value); }
        }

        public BackgroundBrush Background
        {
            get { return GetValue(BackgroundProperty); }
            set { SetValue(BackgroundProperty, value); }
        }

        public Color BorderBrush
        {
            get { return GetValue(BorderBrushProperty); }
            set { SetValue(BorderBrushProperty, value); }
        }

        public Vector4 BorderThickness
        {
            get { return GetValue(BorderThicknessProperty); }
            set { SetValue(BorderThicknessProperty, Vector4.Max(value, Vector4.Zero)); }
        }

        public Vector4 CornerRadius
        {
            get { return GetValue(CornerRadiusProperty); }
            set { SetValue(CornerRadiusProperty, value); }
        }

        public Vector4 Padding
        {
            get { return GetValue(PaddingProperty); }
            set { SetValue(PaddingProperty, value); }
        }

        public float FontSize
        {
            get { return GetValue(FontSizeProperty); }
            set { SetValue(FontSizeProperty, value); }
        }

        public override bool AcceptsKeyboardFocus => true;

        internal TabControl Owner { get; set; }

        // Uses a header built by the owning TabControl; text headers follow FontSize.
        internal void SetGeneratedHeader(VisualElement visual, bool isText)
        {
            _ownsHeaderText = isText;
            SetHeaderVisual(visual);
        }

        void SetHeaderVisual(VisualElement visual)
        {
            if (ReferenceEquals(_headerVisual, visual))
                return;
            DetachHeader();
            _headerVisual = visual;
            AttachHeader();
            Invalidate(UiInvalidation.Measure);
        }

        void AttachHeader()
        {
            if (_headerVisual == null)
                return;
            if (_presenter != null)
                _presenter.Content = _headerVisual;
            else if (_headerVisual.Parent == null)
                AddChild(_headerVisual);
        }

        void DetachHeader()
        {
            if (_headerVisual == null)
                return;
            if (_presenter != null && ReferenceEquals(_presenter.Content, _headerVisual))
                _presenter.Content = null;
            else if (ReferenceEquals(_headerVisual.Parent, this))
                RemoveChild(_headerVisual);
        }

        protected override void OnApplyTemplate()
        {
            DetachHeader();
            _presenter = GetTemplateChild<ContentPresenter>("PART_ContentPresenter");
            AttachHeader();
        }

        protected override void OnTemplateChanging()
        {
            DetachHeader();
            _presenter = null;
        }

        protected override void OnPropertyChanged(IStyledProperty property)
        {
            base.OnPropertyChanged(property);
            if (property == IsSelectedProperty)
                PseudoClasses.Set(PseudoClassNames.Selected, IsSelected);
            else if (property == FontSizeProperty)
                ApplyFontSize();
        }

        void ApplyFontSize()
        {
            TextBlock text = _headerVisual as TextBlock;
            if (_ownsHeaderText && text != null && !float.IsNaN(FontSize))
                text.FontSize = FontSize;
        }

        protected override void ArrangeChildren(RectangleF bounds)
        {
            ArrangeTemplate(bounds);
            if (_headerVisual != null && ReferenceEquals(_headerVisual.Parent, this))
            {
                Vector4 padding = Padding;
                _headerVisual.ArrangeInSlot(new RectangleF(bounds.X + padding.X, bounds.Y + padding.Y,
                    Math.Max(0f, bounds.Width - padding.X - padding.Z),
                    Math.Max(0f, bounds.Height - padding.Y - padding.W)));
            }
        }

        protected override bool HitTestSelf(Vector2 point)
        {
            return true;
        }

        protected override void UpdatePseudoClasses()
        {
            base.UpdatePseudoClasses();
            PseudoClasses.Set(PseudoClassNames.Selected, IsSelected);
            PseudoClasses.Set(PseudoClassNames.Pressed, IsPressed);
        }

        internal override void PointerReleased(Vector2 point, bool clicked)
        {
            if (clicked && Owner != null)
                Owner.Select(this);
        }

        internal override void KeyboardInput(IInputSource input)
        {
            if (input == null || Owner == null)
                return;
            if (input.IsNewKeyPressed(Key.Enter) || input.IsNewKeyPressed(Key.Space))
                Owner.Select(this);
            else if (input.IsNewKeyPressed(Key.Left))
                Owner.MoveSelection(-1);
            else if (input.IsNewKeyPressed(Key.Right))
                Owner.MoveSelection(1);
        }
    }
}
