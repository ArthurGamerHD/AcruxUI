using System;
using System.Collections.Generic;
using Acrux.Core;
using Acrux.Core.Invalidation;
using Acrux.Core.PropertySystem;
using Acrux.Layout;
using Acrux.Rendering;
using Acrux.Styling;
using Acrux.Core.Input;
using VRageMath;

namespace Acrux.Controls
{
    public enum ComboBoxOpenDirection
    {
        Down,
        Up
    }

    /// <summary>
    /// A drop-down selector whose popup is rendered by VisualTree's overlay pass. Items are objects, as Avalonia's
    /// <c>ComboBox</c>: XAML binds <see cref="ItemsSource"/> and <see cref="SelectedIndex"/> or
    /// <see cref="SelectedItem"/> (two-way); code may pass the options, a label and callbacks to the constructor.
    /// Each option shows <see cref="ItemTemplate"/>, else <see cref="ItemLabel"/>, else its ToString().
    /// </summary>
    public class ComboBox : TemplatedControl, IOverlayProvider
    {
        static ComboBox() { }

        public static readonly StyledProperty<float> HeaderHeightProperty =
            StyledProperty.RegisterInternal<ComboBox, float>(
                nameof(HeaderHeight), 34f, UiInvalidation.Measure);
        public static readonly StyledProperty<bool> IsDropDownOpenProperty =
            StyledProperty.Register<ComboBox, bool>(nameof(IsDropDownOpen), false);
        public static readonly StyledProperty<bool> IsEditableProperty =
            StyledProperty.Register<ComboBox, bool>(nameof(IsEditable), false);
        public static readonly StyledProperty<float> MaxDropDownHeightProperty =
            StyledProperty.Register<ComboBox, float>(
                nameof(MaxDropDownHeight), 200f, UiInvalidation.Arrange);
        public static readonly StyledProperty<string> PlaceholderTextProperty =
            StyledProperty.Register<ComboBox, string>(
                nameof(PlaceholderText), null, UiInvalidation.Measure);
        public static readonly StyledProperty<Color> PlaceholderForegroundProperty =
            StyledProperty.Register<ComboBox, Color>(
                nameof(PlaceholderForeground), new Color(255, 255, 255, 127));
        public static readonly StyledProperty<HorizontalAlignment> HorizontalContentAlignmentProperty =
            StyledProperty.Register<ComboBox, HorizontalAlignment>(
                nameof(HorizontalContentAlignment), HorizontalAlignment.Left, UiInvalidation.Arrange);
        public static readonly StyledProperty<VerticalAlignment> VerticalContentAlignmentProperty =
            StyledProperty.Register<ComboBox, VerticalAlignment>(
                nameof(VerticalContentAlignment), VerticalAlignment.Center, UiInvalidation.Arrange);
        public static readonly StyledProperty<string> TextProperty =
            StyledProperty.Register<ComboBox, string>(nameof(Text), null, UiInvalidation.Measure);
        public static readonly StyledProperty<IDataTemplate> SelectionBoxItemTemplateProperty =
            StyledProperty.Register<ComboBox, IDataTemplate>(nameof(SelectionBoxItemTemplate), null);
        public static readonly StyledProperty<IDataTemplate> ItemTemplateProperty =
            StyledProperty.Register<ComboBox, IDataTemplate>(nameof(ItemTemplate), null);
        public static readonly StyledProperty<float> OptionHeightProperty =
            StyledProperty.RegisterInternal<ComboBox, float>(
                nameof(OptionHeight), 32f, UiInvalidation.Arrange);
        public static readonly StyledProperty<float> SpacingProperty =
            StyledProperty.RegisterInternal<ComboBox, float>(
                nameof(Spacing), 0f, UiInvalidation.Arrange);
        public static readonly StyledProperty<Vector4> CornerRadiusProperty =
            StyledProperty.Register<ComboBox, Vector4>(nameof(CornerRadius), new Vector4(6f));
        public static readonly StyledProperty<Vector4> BorderThicknessProperty =
            StyledProperty.Register<ComboBox, Vector4>(nameof(BorderThickness), new Vector4(1f));
        public static readonly StyledProperty<Vector4> PaddingProperty =
            StyledProperty.Register<ComboBox, Vector4>(
                nameof(Padding), new Vector4(12f, 0f, 8f, 0f), UiInvalidation.Measure);
        public static readonly StyledProperty<Vector4> PopupPaddingProperty =
            StyledProperty.RegisterInternal<ComboBox, Vector4>(
                nameof(PopupPadding), Vector4.Zero, UiInvalidation.Measure);
        public static readonly StyledProperty<float> TextScaleProperty =
            StyledProperty.RegisterInternal<ComboBox, float>(
                nameof(TextScale), 0.68f, UiInvalidation.Measure);
        public static readonly StyledProperty<float> IndicatorSizeProperty =
            StyledProperty.RegisterInternal<ComboBox, float>(
                nameof(IndicatorSize), 9f, UiInvalidation.Measure);
        public static readonly StyledProperty<BackgroundBrush> BackgroundProperty =
            StyledProperty.Register<ComboBox, BackgroundBrush>(
                "Background", new SolidColorBrush(new Color(32, 38, 42, 245)));
        public static readonly StyledProperty<Color> ForegroundProperty =
            StyledProperty.Register<ComboBox, Color>(nameof(Foreground), Color.White);
        public static readonly StyledProperty<Color> BorderBrushProperty =
            StyledProperty.Register<ComboBox, Color>(nameof(BorderBrush), new Color(82, 98, 107));
        public static readonly StyledProperty<System.Collections.IEnumerable> ItemsSourceProperty =
            StyledProperty.Register<ComboBox, System.Collections.IEnumerable>(nameof(ItemsSource), null, UiInvalidation.Measure);
        public static readonly StyledProperty<int> SelectedIndexProperty =
            StyledProperty.Register<ComboBox, int>(nameof(SelectedIndex), -1, UiInvalidation.Measure);
        public static readonly StyledProperty<object> SelectedItemProperty =
            StyledProperty.Register<ComboBox, object>(nameof(SelectedItem), null, UiInvalidation.Measure);

        public float HeaderHeight
        {
            get { return GetValue(HeaderHeightProperty); }
            set { SetValue(HeaderHeightProperty, value); }
        }

        public bool IsDropDownOpen
        {
            get { return GetValue(IsDropDownOpenProperty); }
            set { SetValue(IsDropDownOpenProperty, value); }
        }

        public bool IsEditable
        {
            get { return GetValue(IsEditableProperty); }
            set { SetValue(IsEditableProperty, value); }
        }

        public float MaxDropDownHeight
        {
            get { return GetValue(MaxDropDownHeightProperty); }
            set { SetValue(MaxDropDownHeightProperty, Math.Max(0f, value)); }
        }

        public string PlaceholderText
        {
            get { return GetValue(PlaceholderTextProperty); }
            set { SetValue(PlaceholderTextProperty, value); }
        }

        public Color PlaceholderForeground
        {
            get { return GetValue(PlaceholderForegroundProperty); }
            set { SetValue(PlaceholderForegroundProperty, value); }
        }

        public HorizontalAlignment HorizontalContentAlignment
        {
            get { return GetValue(HorizontalContentAlignmentProperty); }
            set { SetValue(HorizontalContentAlignmentProperty, value); }
        }

        public VerticalAlignment VerticalContentAlignment
        {
            get { return GetValue(VerticalContentAlignmentProperty); }
            set { SetValue(VerticalContentAlignmentProperty, value); }
        }

        public string Text
        {
            get { return GetValue(TextProperty); }
            set { SetValue(TextProperty, value); }
        }

        public IDataTemplate SelectionBoxItemTemplate
        {
            get { return GetValue(SelectionBoxItemTemplateProperty); }
            set { SetValue(SelectionBoxItemTemplateProperty, value); }
        }

        /// <summary>
        /// Builds each option's content. The
        /// selection box uses <see cref="SelectionBoxItemTemplate"/>, falling back to
        /// this, when the control template has a PART_ContentPresenter. Template content
        /// sets its own text appearance: ADK text properties are not inherited.
        /// </summary>
        public IDataTemplate ItemTemplate
        {
            get { return GetValue(ItemTemplateProperty); }
            set { SetValue(ItemTemplateProperty, value); }
        }

        /// <summary>The options. Replacing them keeps <see cref="SelectedIndex"/> while it is still in range, so a
        /// two-way bound index survives the items arriving after it; otherwise the first option is selected.</summary>
        public System.Collections.IEnumerable ItemsSource
        {
            get { return GetValue(ItemsSourceProperty); }
            set { SetValue(ItemsSourceProperty, value); }
        }

        /// <summary>The selected option's index, -1 for none.</summary>
        public int SelectedIndex
        {
            get { return GetValue(SelectedIndexProperty); }
            set { SetValue(SelectedIndexProperty, value); }
        }

        /// <summary>The selected option. Setting a value that is not an option is ignored.</summary>
        public object SelectedItem
        {
            get { return GetValue(SelectedItemProperty); }
            set { SetValue(SelectedItemProperty, value); }
        }

        public object SelectionBoxItem
        {
            get { return SelectedItem; }
        }

        protected override void OnPropertyChanged(IStyledProperty property)
        {
            base.OnPropertyChanged(property);
            if (property == IsDropDownOpenProperty)
            {
                VisualTree.OverlayOpenChanged(IsDropDownOpen);
                OnDropDownOpenChanged(IsDropDownOpen);
            }
            else if (property == ItemTemplateProperty || property == SelectionBoxItemTemplateProperty)
            {
                OnItemTemplateChanged();
            }
            else if (property == ItemsSourceProperty)
            {
                OnItemsSourceChanged();
            }
            else if (property == SelectedIndexProperty)
            {
                OnSelectedIndexChanged();
            }
            else if (property == SelectedItemProperty)
            {
                OnSelectedItemChanged();
            }
        }
        public float OptionHeight
        {
            get { return GetValue(OptionHeightProperty); }
            set { SetValue(OptionHeightProperty, value); }
        }

        public float Spacing
        {
            get { return GetValue(SpacingProperty); }
            set { SetValue(SpacingProperty, value); }
        }

        public Vector4 CornerRadius
        {
            get { return GetValue(CornerRadiusProperty); }
            set { SetValue(CornerRadiusProperty, value); }
        }

        public Vector4 BorderThickness
        {
            get { return GetValue(BorderThicknessProperty); }
            set { SetValue(BorderThicknessProperty, value); }
        }

        public Vector4 Padding
        {
            get { return GetValue(PaddingProperty); }
            set { SetValue(PaddingProperty, value); }
        }

        public Vector4 PopupPadding
        {
            get { return GetValue(PopupPaddingProperty); }
            set { SetValue(PopupPaddingProperty, value); }
        }

        public float TextScale
        {
            get { return GetValue(TextScaleProperty); }
            set { SetValue(TextScaleProperty, value); }
        }

        public float IndicatorSize
        {
            get { return GetValue(IndicatorSizeProperty); }
            set { SetValue(IndicatorSizeProperty, value); }
        }

        public BackgroundBrush Background
        {
            get { return GetValue(BackgroundProperty); }
            set { SetValue(BackgroundProperty, value); }
        }

        public Color Foreground
        {
            get { return GetValue(ForegroundProperty); }
            set { SetValue(ForegroundProperty, value); }
        }


        public Color BorderBrush
        {
            get { return GetValue(BorderBrushProperty); }
            set { SetValue(BorderBrushProperty, value); }
        }

        // ------------------------------------------------------------------ options and selection

        readonly List<object> _options = new List<object>();
        readonly List<Border> _items = new List<Border>();
        readonly List<TextBlock> _itemTexts = new List<TextBlock>();
        Border _header;
        TextBlock _headerText;
        ContentPresenter _headerContent;
        IDataTemplate _headerContentTemplate;
        object _headerContentValue;
        bool _hasHeaderContent;
        bool _itemsDirty;
        // Set while the selection properties are written from inside, so their change handlers do not re-enter.
        bool _syncingSelection;
        static readonly Acrux.Styling.RotateTransform DownwardIndicator = new Acrux.Styling.RotateTransform(180f);
        Border _headerIndicator;
        readonly Border _popup;
        readonly ScrollViewer _popupScroller;
        readonly StackPanel _popupPanel;
        float _layoutScale = 1f;

        public ComboBox()
            : this(null)
        {
        }

        /// <summary>Code-built: the options, how to label them and the callbacks, in one call.</summary>
        public ComboBox(
            System.Collections.IEnumerable options,
            Func<object, string> itemLabel = null,
            Action<object> selectionChanged = null,
            Action stateChanged = null)
        {
            ItemLabel = itemLabel;
            SelectionChanged = selectionChanged;
            StateChanged = stateChanged;

            _popupPanel = new StackPanel();
            _popupScroller = new ScrollViewer
            {
                Child = _popupPanel,
                VerticalScrollEnabled = true,
                HorizontalScrollEnabled = false,
                WheelStep = 32f
            };
            _popup = new Border { Child = _popupScroller, Visible = false, StyleClass = "combo-box-popup" };
            if (options != null)
                ItemsSource = options;
        }

        /// <summary>The preferred side for the drop-down. It opens on the other side when it does not fit and that side
        /// has more room, and is shortened to the room on the side it opens.</summary>
        public ComboBoxOpenDirection OpenDirection { get; set; } = ComboBoxOpenDirection.Down;

        public bool IsOpen
        {
            get { return IsDropDownOpen; }
        }

        /// <summary>An option's text when there is no <see cref="ItemTemplate"/>; null uses ToString().</summary>
        public Func<object, string> ItemLabel { get; set; }

        /// <summary>The user picked another option (not raised when code sets the selection).</summary>
        public Action<object> SelectionChanged { get; set; }

        /// <summary>The drop-down opened or closed, or the keyboard moved the selection.</summary>
        public Action StateChanged { get; set; }

        public override bool AcceptsKeyboardFocus
        {
            get { return true; }
        }

        protected override void OnApplyTemplate()
        {
            _header = GetTemplateChild<Border>("PART_Header");
            _headerText = GetTemplateChild<TextBlock>("PART_HeaderText");
            _headerIndicator = GetTemplateChild<Border>("PART_Indicator");
            _headerContent = GetTemplateChild<ContentPresenter>("PART_ContentPresenter");
            _hasHeaderContent = false;
            if (_header != null)
            {
                _header.Interactive = true;
                _header.Clicked = Toggle;
            }
        }

        protected override void UpdatePseudoClasses()
        {
            base.UpdatePseudoClasses();
            PseudoClasses.Set(PseudoClassNames.Pressed, IsPressed);
        }

        public void Configure(RectangleF bounds, float scale)
        {
            _layoutScale = Math.Max(0.01f, scale);
            Arrange(bounds);
            Visible = true;
        }

        public void Open()
        {
            if (IsOpen || !Enabled || _options.Count == 0)
                return;
            IsDropDownOpen = true;
        }

        public void Close()
        {
            if (!IsOpen)
                return;
            IsDropDownOpen = false;
        }

        void OnItemsSourceChanged()
        {
            _options.Clear();
            System.Collections.IEnumerable source = ItemsSource;
            if (source != null)
                foreach (object option in source)
                    _options.Add(option);
            int index = SelectedIndex;
            if (index < 0 || index >= _options.Count)
                index = _options.Count > 0 ? 0 : -1;
            ApplySelection(index);
            RebuildItems();
        }

        void OnSelectedIndexChanged()
        {
            if (_syncingSelection)
                return;
            int index = SelectedIndex;
            ApplySelection(index >= -1 && index < _options.Count ? index : IndexOf(SelectedItem));
        }

        void OnSelectedItemChanged()
        {
            if (_syncingSelection)
                return;
            int index = IndexOf(SelectedItem);
            // Not an option: keep the current selection.
            ApplySelection(index >= 0 || _options.Count == 0 ? index : SelectedIndex);
        }

        // Writes both selection properties for the option at index (-1: none).
        void ApplySelection(int index)
        {
            object item = index >= 0 && index < _options.Count ? _options[index] : null;
            _syncingSelection = true;
            try
            {
                SelectedIndex = item == null && index >= _options.Count ? -1 : index;
                SelectedItem = item;
            }
            finally
            {
                _syncingSelection = false;
            }
            _hasHeaderContent = false;
            Invalidate(UiInvalidation.Measure);
        }

        protected virtual void OnDropDownOpenChanged(bool isOpen)
        {
            PseudoClasses.Set(PseudoClassNames.DropdownOpen, isOpen);
            _popup.Visible = isOpen;
            Invalidate(UiInvalidation.Render);
            // Templated options are built when the list opens, after styles have applied.
            if (isOpen && _itemsDirty)
                RebuildItems();
            if (isOpen)
            {
                UpdateOverlayLayout();
                int selectedIndex = SelectedIndex;
                if (selectedIndex >= 0)
                    _popupScroller.VerticalOffset = Math.Max(0f,
                        selectedIndex * (OptionHeight + Spacing * _layoutScale) -
                        _popup.Bounds.Height * 0.5f);
            }
            RaiseStateChanged();
        }

        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            EnsureStyleApplied();
            string label = GetDisplayText();
            Vector2 textSize = TextMetrics == null
                ? Vector2.Zero
                : TextMetrics.Measure("White", label, TextScale);
            float horizontalPadding = Math.Max(0f, Padding.X) + Math.Max(0f, Padding.Z);
            float desiredWidth = textSize.X + horizontalPadding + Math.Max(0f, IndicatorSize) + 8f;
            float desiredHeight = Math.Max(HeaderHeight, textSize.Y);
            return new Vector2(
                Math.Min(availableSize.X, desiredWidth),
                Math.Min(availableSize.Y, desiredHeight));
        }

        protected override void ArrangeChildren(RectangleF bounds)
        {
            ArrangeTemplate(bounds);
        }

        protected virtual void OnItemTemplateChanged()
        {
            _itemsDirty = true;
            _hasHeaderContent = false;
            if (IsOpen)
                RebuildItems();
            Invalidate(UiInvalidation.Measure);
        }

        protected override void DrawSelf(RenderContext context)
        {
            ApplyPopupAppearance();
            bool templated = UpdateHeaderContent();
            if (_headerText != null)
                _headerText.Visible = !templated;
            if (_headerText != null && !templated)
            {
                _headerText.Text = GetDisplayText();
                _headerText.Foreground = string.IsNullOrEmpty(Text) &&
                    string.IsNullOrEmpty(LabelOf(SelectedItem))
                    ? PlaceholderForeground
                    : Foreground;
                _headerText.TextAlignment = HorizontalContentAlignment;
                _headerText.VerticalTextAlignment = VerticalContentAlignment;
            }
            if (_headerIndicator != null)
                _headerIndicator.RenderTransform = IsOpen ? null : DownwardIndicator;
            UpdateOverlayLayout();
        }

        internal override void KeyboardInput(IInputSource input)
        {
            if (input == null)
                return;
            if (input.IsNewKeyPressed(Key.Escape))
            {
                Close();
                return;
            }
            if (input.IsNewKeyPressed(Key.Enter) || input.IsNewKeyPressed(Key.Space))
            {
                Toggle();
                return;
            }
            if (!IsOpen || _options.Count == 0)
                return;
            int index = SelectedIndex;
            if (input.IsNewKeyPressed(Key.Down))
                SelectOption(Math.Min(_options.Count - 1, Math.Max(0, index + 1)), false);
            else if (input.IsNewKeyPressed(Key.Up))
                SelectOption(Math.Max(0, index - 1), false);
        }

        // Shows the selected option through the item template; false to use the header text.
        bool UpdateHeaderContent()
        {
            IDataTemplate template = SelectionBoxItemTemplate ?? ItemTemplate;
            if (_headerContent == null)
                return false;
            if (template == null || !string.IsNullOrEmpty(Text) || _options.Count == 0)
            {
                _headerContent.Content = null;
                _headerContent.Visible = false;
                _hasHeaderContent = false;
                return false;
            }
            object selected = SelectedItem;
            if (!_hasHeaderContent || !ReferenceEquals(_headerContentTemplate, template) ||
                !Equals(_headerContentValue, selected))
            {
                _headerContent.Content = template.Build(selected);
                _headerContentTemplate = template;
                _headerContentValue = selected;
                _hasHeaderContent = true;
            }
            _headerContent.Visible = true;
            return true;
        }

        void Toggle()
        {
            if (IsOpen) Close(); else Open();
        }

        void RebuildItems()
        {
            for (int i = 0; i < _items.Count; i++)
                _popupPanel.RemoveChild(_items[i]);
            _items.Clear();
            _itemTexts.Clear();
            _itemsDirty = false;
            IDataTemplate template = ItemTemplate;

            for (int i = 0; i < _options.Count; i++)
            {
                object option = _options[i];
                int index = i;
                VisualElement content = template == null ? null : template.Build(option);
                TextBlock text = content == null
                    ? new TextBlock
                    {
                        Text = LabelOf(option),
                        TextAlignment = HorizontalAlignment.Left,
                        VerticalTextAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.PrefixCharacterEllipsis
                    }
                    : null;
                Border border = _popupPanel.Add(new Border
                {
                    Interactive = true,
                    StyleClass = "combo-box-item",
                    // A template wider than the drop-down is cut at its edge, as the selection box cuts it.
                    ClipToBounds = true,
                    Child = content ?? text
                });
                border.Clicked = () => SelectOption(index, true);
                _items.Add(border);
                _itemTexts.Add(text);
            }
            ApplyPopupAppearance();
        }

        void ApplyPopupAppearance()
        {
            _popup.BorderBrush = BorderBrush;
            _popup.BorderThickness = BorderThickness;
            _popup.CornerRadius = CornerRadius;
            _popup.Padding = EffectivePopupPadding;
            float itemLeftPadding = Math.Max(0f, Padding.X - PopupPadding.X);
            Color itemForeground = Foreground;
            for (int i = 0; i < _items.Count; i++)
            {
                _items[i].BorderBrush = Color.Transparent;
                _items[i].BorderThickness = Vector4.Zero;
                _items[i].CornerRadius = Vector4.Zero;
                _items[i].Padding = new Vector4(itemLeftPadding, 0f, 0f, 0f);
                if (_itemTexts[i] == null)
                    continue;
                _itemTexts[i].FontSize = TextScale;
                _itemTexts[i].Foreground = itemForeground;
            }
        }

        // The user picked an option (click or arrow key).
        void SelectOption(int index, bool close)
        {
            bool changed = index != SelectedIndex;
            if (changed)
                ApplySelection(index);
            if (close)
                IsDropDownOpen = false;
            if (changed && SelectionChanged != null)
                SelectionChanged(SelectedItem);
            if (!close)
                RaiseStateChanged();
        }

        int IndexOf(object value)
        {
            for (int i = 0; i < _options.Count; i++)
                if (Equals(_options[i], value))
                    return i;
            return -1;
        }

        string LabelOf(object option)
        {
            if (option == null)
                return string.Empty;
            Func<object, string> label = ItemLabel;
            return label != null ? label(option) ?? string.Empty : option.ToString();
        }

        bool IOverlayProvider.IsOverlayOpen
        {
            get { return IsOpen; }
        }

        VisualElement IOverlayProvider.OverlayElement
        {
            get { return _popup; }
        }

        // Items are square and span the popup's width; top and bottom padding keep them clear of its rounded corners.
        Vector4 EffectivePopupPadding
        {
            get
            {
                Vector4 padding = PopupPadding;
                Vector4 corners = CornerRadius;
                padding.Y = Math.Max(padding.Y, Math.Max(corners.X, corners.Y));
                padding.W = Math.Max(padding.W, Math.Max(corners.Z, corners.W));
                return padding;
            }
        }

        void IOverlayProvider.UpdateOverlayLayout() { UpdateOverlayLayout(); }
        void IOverlayProvider.CloseOverlay() { Close(); }

        void UpdateOverlayLayout()
        {
            if (!IsOpen)
                return;
            float spacing = Spacing * _layoutScale;
            _popupPanel.Spacing = spacing;
            for (int i = 0; i < _items.Count; i++)
                _items[i].Height = OptionHeight;
            Vector4 popupPadding = EffectivePopupPadding;
            float height = BorderThickness.Y + BorderThickness.W + popupPadding.Y + popupPadding.W +
                _items.Count * OptionHeight + Math.Max(0, _items.Count - 1) * spacing;
            height = Math.Min(Math.Max(0f, MaxDropDownHeight), height);
            RectangleF area = ConstraintBounds();
            float below = Math.Max(0f, area.Bottom - Bounds.Bottom);
            float above = Math.Max(0f, Bounds.Y - area.Y);
            bool up = OpenDirection == ComboBoxOpenDirection.Up;
            float preferred = up ? above : below;
            float other = up ? below : above;
            if (height > preferred && other > preferred)
                up = !up;
            height = Math.Min(height, up ? above : below);
            float y = up ? Bounds.Y - height : Bounds.Bottom;
            _popup.Arrange(new RectangleF(Bounds.X, y, Bounds.Width, Math.Max(1f, height)));
        }

        RectangleF ConstraintBounds()
        {
            VisualElement root = this;
            for (VisualElement next = root.Parent ?? root.OverlayOwner; next != null; next = next.Parent ?? next.OverlayOwner)
                root = next;
            return root.Bounds;
        }

        string GetDisplayText()
        {
            if (!string.IsNullOrEmpty(Text))
                return Text;
            string label = LabelOf(SelectedItem);
            return string.IsNullOrEmpty(label) ? PlaceholderText ?? string.Empty : label;
        }

        public override void Dispose()
        {
            _popup.Dispose();
            base.Dispose();
        }

        void RaiseStateChanged()
        {
            if (StateChanged != null) StateChanged();
        }
    }
}
