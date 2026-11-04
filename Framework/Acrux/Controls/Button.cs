using System;
using Acrux.Core;
using Acrux.Core.Invalidation;
using Acrux.Core.PropertySystem;
using Acrux.Core.Input;
using Acrux.Controls.Primitives;
using Acrux.Interactivity;
using Acrux.Layout;
using Acrux.Rendering;
using Acrux.Styling;
using VRageMath;

namespace Acrux.Controls
{
    public enum ButtonShape
    {
        Rectangle,
        RoundedRectangle,
        Pill,
        Circle
    }

    /// <summary>
    /// The framework's single clickable content control. Visual variants are
    /// selected by generic Style selectors and Setter elements, not subclasses.
    /// </summary>
    public class Button : TemplatedControl, IOverlayProvider, ILightDismissOverlayProvider
    {
        static Button() { }

        Border _chrome;
        Border _outline;
        Decorator _presenter;
        VisualElement _content;
        RectangleF _drawBounds;
        string _text;
        string _icon;
        bool _selected;
        bool _hoverClickInvoked;

        public static readonly StyledProperty<ButtonShape> ShapeProperty =
            StyledProperty.RegisterInternal<Button, ButtonShape>(
                nameof(Shape), ButtonShape.Rectangle, UiInvalidation.Arrange);
        public static readonly RoutedEvent<RoutedEventArgs> ClickEvent =
            RoutedEvent.Register<Button, RoutedEventArgs>(nameof(Click), RoutingStrategies.Bubble);
        public static readonly StyledProperty<ClickMode> ClickModeProperty =
            StyledProperty.Register<Button, ClickMode>(
                nameof(ClickMode), ClickMode.Release);
        public static readonly StyledProperty<ICommand> CommandProperty =
            StyledProperty.Register<Button, ICommand>(nameof(Command), null);
        public static readonly StyledProperty<object> CommandParameterProperty =
            StyledProperty.Register<Button, object>(nameof(CommandParameter), null);
        public static readonly StyledProperty<Key> HotKeyProperty =
            StyledProperty.Register<Button, Key>(nameof(HotKey), Key.None);
        public static readonly StyledProperty<bool> IsDefaultProperty =
            StyledProperty.Register<Button, bool>(nameof(IsDefault), false);
        public static readonly StyledProperty<bool> IsCancelProperty =
            StyledProperty.Register<Button, bool>(nameof(IsCancel), false);
        public static readonly StyledProperty<Popup> FlyoutProperty =
            StyledProperty.Register<Button, Popup>(nameof(Flyout), null);
        public static readonly StyledProperty<HorizontalAlignment> HorizontalContentAlignmentProperty =
            StyledProperty.Register<Button, HorizontalAlignment>(
                nameof(HorizontalContentAlignment), HorizontalAlignment.Center, UiInvalidation.Arrange);
        public static readonly StyledProperty<VerticalAlignment> VerticalContentAlignmentProperty =
            StyledProperty.Register<Button, VerticalAlignment>(
                nameof(VerticalContentAlignment), VerticalAlignment.Center, UiInvalidation.Arrange);
        public static readonly StyledProperty<Vector4> CornerRadiusProperty =
            StyledProperty.Register<Button, Vector4>(
                nameof(CornerRadius), Vector4.Zero);
        public static readonly StyledProperty<Vector4> BorderThicknessProperty =
            StyledProperty.Register<Button, Vector4>(nameof(BorderThickness), new Vector4(1f));
        public static readonly StyledProperty<Vector4> PaddingProperty =
            StyledProperty.Register<Button, Vector4>(
                nameof(Padding), Vector4.Zero, UiInvalidation.Measure);
        public static readonly StyledProperty<Vector2> ShadowOffsetProperty =
            StyledProperty.RegisterInternal<Button, Vector2>(
                nameof(ShadowOffset), Vector2.Zero);
        public static readonly StyledProperty<float> ScaleProperty =
            StyledProperty.RegisterInternal<Button, float>(nameof(Scale), 1f);
        public static readonly StyledProperty<float> IconScaleProperty =
            StyledProperty.RegisterInternal<Button, float>(
                nameof(IconScale), 0.75f, UiInvalidation.Measure);
        public static readonly StyledProperty<float> TextScaleProperty =
            StyledProperty.RegisterInternal<Button, float>(
                nameof(TextScale), 0.72f, UiInvalidation.Measure);
        public static readonly StyledProperty<bool> UseMissingIconFallbackProperty =
            StyledProperty.RegisterInternal<Button, bool>(nameof(UseMissingIconFallback), true);
        public static readonly StyledProperty<float> SeparatorInsetProperty =
            StyledProperty.RegisterInternal<Button, float>(nameof(SeparatorInset), 8f);
        public static readonly StyledProperty<float> SeparatorThicknessProperty =
            StyledProperty.RegisterInternal<Button, float>(nameof(SeparatorThickness), 1f);
        public static readonly StyledProperty<BackgroundBrush> BackgroundProperty =
            StyledProperty.Register<Button, BackgroundBrush>(
                "Background", new SolidColorBrush(Color.Transparent));
        public static readonly StyledProperty<Color> ForegroundProperty =
            StyledProperty.Register<Button, Color>(nameof(Foreground), Color.White);
        public static readonly StyledProperty<Color> BorderBrushProperty =
            StyledProperty.Register<Button, Color>(
                nameof(BorderBrush), new Color(82, 98, 107));
        public static readonly StyledProperty<Color> ShadowProperty =
            StyledProperty.RegisterInternal<Button, Color>("Shadow", Color.Transparent);
        public static readonly StyledProperty<Color> SeparatorProperty =
            StyledProperty.RegisterInternal<Button, Color>(nameof(Separator), Color.Transparent);

        public ButtonShape Shape
        {
            get { return GetValue(ShapeProperty); }
            set { SetValue(ShapeProperty, value); }
        }

        public ClickMode ClickMode
        {
            get { return GetValue(ClickModeProperty); }
            set { SetValue(ClickModeProperty, value); }
        }

        public ICommand Command
        {
            get { return GetValue(CommandProperty); }
            set { SetValue(CommandProperty, value); }
        }

        public object CommandParameter
        {
            get { return GetValue(CommandParameterProperty); }
            set { SetValue(CommandParameterProperty, value); }
        }

        public Key HotKey
        {
            get { return GetValue(HotKeyProperty); }
            set { SetValue(HotKeyProperty, value); }
        }

        public bool IsDefault
        {
            get { return GetValue(IsDefaultProperty); }
            set { SetValue(IsDefaultProperty, value); }
        }

        public bool IsCancel
        {
            get { return GetValue(IsCancelProperty); }
            set { SetValue(IsCancelProperty, value); }
        }

        public Popup Flyout
        {
            get { return GetValue(FlyoutProperty); }
            set { SetValue(FlyoutProperty, value); }
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

        public Vector2 ShadowOffset
        {
            get { return GetValue(ShadowOffsetProperty); }
            set { SetValue(ShadowOffsetProperty, value); }
        }

        public float Scale
        {
            get { return GetValue(ScaleProperty); }
            set { SetValue(ScaleProperty, value); }
        }

        public float IconScale
        {
            get { return GetValue(IconScaleProperty); }
            set { SetValue(IconScaleProperty, value); }
        }

        public float TextScale
        {
            get { return GetValue(TextScaleProperty); }
            set { SetValue(TextScaleProperty, value); }
        }

        public bool UseMissingIconFallback
        {
            get { return GetValue(UseMissingIconFallbackProperty); }
            set { SetValue(UseMissingIconFallbackProperty, value); }
        }

        public float SeparatorInset
        {
            get { return GetValue(SeparatorInsetProperty); }
            set { SetValue(SeparatorInsetProperty, value); }
        }

        public float SeparatorThickness
        {
            get { return GetValue(SeparatorThicknessProperty); }
            set { SetValue(SeparatorThicknessProperty, value); }
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

        public Color ShadowColor
        {
            get { return GetValue(ShadowProperty); }
            set { SetValue(ShadowProperty, value); }
        }

        public Color Separator
        {
            get { return GetValue(SeparatorProperty); }
            set { SetValue(SeparatorProperty, value); }
        }

        public Button()
        {
        }

        public string Text
        {
            get { return _text; }
            set
            {
                if (string.Equals(_text, value, StringComparison.Ordinal))
                    return;
                _text = value;
                Invalidate(UiInvalidation.Measure);
            }
        }

        public string Icon
        {
            get { return _icon; }
            set
            {
                if (string.Equals(_icon, value, StringComparison.Ordinal))
                    return;
                _icon = value;
                Invalidate(UiInvalidation.Measure);
            }
        }

        public float IconRotation { get; set; }
        public bool Selected
        {
            get { return _selected; }
            set
            {
                if (_selected == value) return;
                _selected = value;
                PseudoClasses.Set(PseudoClassNames.Selected, value);
            }
        }

        public Func<bool> SelectedProvider { get; set; }
        public Action Clicked { get; set; }

        /// <summary>Click event; SE's script whitelist forbids EventHandler&lt;T&gt;, so handlers are (sender, e) actions.</summary>
        public event Action<object, RoutedEventArgs> Click;
        public Action SecondaryClicked { get; set; }
        public object Tag { get; set; }
        public bool ShowTopSeparator { get; set; }
        public override bool AcceptsKeyboardFocus
        {
            get { return true; }
        }

        /// <summary>
        /// ContentControl.Content: a visual is hosted by the content presenter;
        /// any other value is presented as text with the button's foreground.
        /// </summary>
        public object Content
        {
            get { return (object)_content ?? Text; }
            set
            {
                var visual = value as VisualElement;
                if (visual == null)
                {
                    SetContentVisual(null);
                    Text = value == null ? null : value.ToString();
                    return;
                }
                Text = null;
                SetContentVisual(visual);
            }
        }

        void SetContentVisual(VisualElement value)
        {
            {
                if (ReferenceEquals(_content, value))
                    return;
                if (_content != null && _content.Parent != null)
                    _content.Parent.RemoveChild(_content);
                _content = value;
                if (_content != null)
                {
                    if (_presenter != null)
                        _presenter.Child = _content;
                    else
                        AddChild(_content);
                }
                Invalidate(UiInvalidation.Measure);
            }
        }

        internal bool IsSelected
        {
            get { return SelectedProvider?.Invoke() ?? Selected; }
        }

        protected override void UpdatePseudoClasses()
        {
            base.UpdatePseudoClasses();
            if (!IsPointerOver)
                _hoverClickInvoked = false;
            PseudoClasses.Set(PseudoClassNames.Pressed, IsPressed);
            PseudoClasses.Set(PseudoClassNames.Selected, IsSelected);
        }
        internal RectangleF DrawBounds
        {
            get { return _drawBounds; }
        }

        protected override void OnApplyTemplate()
        {
            _chrome = GetTemplateChild<Border>("PART_Chrome");
            _outline = GetTemplateChild<Border>("PART_Outline");
            _presenter = GetTemplateChild<Decorator>("PART_ContentPresenter");
            if (_presenter != null && _content != null)
            {
                VisualElement content = _content;
                _presenter.Child = content;
                _content = content;
            }
        }

        protected override void OnTemplateChanging()
        {
            if (_presenter != null && ReferenceEquals(_presenter.Child, _content))
                _presenter.Child = null;
            _chrome = null;
            _outline = null;
            _presenter = null;
        }

        protected override void ValidateChildForAdd(VisualElement child)
        {
            if (IsApplyingTemplate)
            {
                base.ValidateChildForAdd(child);
                return;
            }
            if (ReferenceEquals(child, _chrome) || ReferenceEquals(child, _outline) ||
                child is ContentPresenter)
                return;
            if (_content == null)
            {
                _content = child;
                return;
            }
            if (!ReferenceEquals(child, _content))
                throw new InvalidOperationException("Button accepts a single content visual.");
        }

        public override bool RemoveChild(VisualElement child)
        {
            bool removed = base.RemoveChild(child);
            if (removed && ReferenceEquals(child, _content))
                _content = null;
            return removed;
        }

        public override void ClearChildren()
        {
            Content = null;
        }

        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            EnsureStyleApplied();
            float horizontalPadding = Math.Max(0f, Padding.X) + Math.Max(0f, Padding.Z);
            float verticalPadding = Math.Max(0f, Padding.Y) + Math.Max(0f, Padding.W);
            Vector2 contentAvailable = new Vector2(
                float.IsPositiveInfinity(availableSize.X)
                    ? availableSize.X
                    : Math.Max(0f, availableSize.X - horizontalPadding),
                float.IsPositiveInfinity(availableSize.Y)
                    ? availableSize.Y
                    : Math.Max(0f, availableSize.Y - verticalPadding));

            Vector2 desired = Vector2.Zero;
            Vector2 templated = Vector2.Zero;
            if (TemplateRoot != null && _presenter != null)
            {
                // The template lays the content out, e.g. through a Viewbox that shrinks it to fit.
                _presenter.Padding = Padding;
                templated = TemplateRoot.Measure(availableSize);
            }
            else if (_content != null && _content.Visible)
                desired = _content.Measure(contentAvailable);

            desired = Vector2.Max(desired, MeasureDrawnContent());

            return Vector2.Max(templated, new Vector2(
                desired.X + horizontalPadding,
                desired.Y + verticalPadding));
        }

        protected override bool HitTestSelf(Vector2 point)
        {
            if (Shape != ButtonShape.Circle)
                return true;
            Vector2 delta = point - Bounds.Center;
            float radius = Math.Min(Bounds.Width, Bounds.Height) * 0.5f;
            return delta.LengthSquared() <= radius * radius;
        }

        protected override void ArrangeChildren(RectangleF bounds)
        {
            ApplyStyleAndLayout(bounds);
        }

        protected override void DrawSelf(RenderContext context)
        {
            ApplyStyleAndLayout(Bounds);
        }

        internal override void PointerReleased(Vector2 point, bool clicked)
        {
            if (clicked && ClickMode == ClickMode.Release)
                InvokeClick();
        }

        internal override void PointerPressed(Vector2 point)
        {
            if (ClickMode == ClickMode.Press)
                InvokeClick();
        }

        internal override void PointerHovered(Vector2 point)
        {
            if (ClickMode != ClickMode.Hover || _hoverClickInvoked)
                return;
            _hoverClickInvoked = true;
            InvokeClick();
        }

        internal override void KeyboardInput(IInputSource input)
        {
            if (input == null)
                return;
            if (input.IsNewKeyPressed(Key.Enter) || input.IsNewKeyPressed(Key.Space) ||
                (HotKey != Key.None && input.IsNewKeyPressed(HotKey)))
                InvokeClick();
        }

        internal override void SecondaryPointerReleased(Vector2 point, bool clicked)
        {
            if (clicked && SecondaryClicked != null)
                SecondaryClicked();
        }

        internal override bool HandlesSecondaryClick => SecondaryClicked != null;

        protected override void OnPropertyChanged(IStyledProperty property)
        {
            base.OnPropertyChanged(property);
            if (property == FlyoutProperty && Flyout != null)
                Flyout.OverlayOwner = this;
        }

        protected virtual void InvokeClick()
        {
            if (!IsEffectivelyEnabled)
                return;
            var e = new RoutedEventArgs(ClickEvent, this);
            Action<object, RoutedEventArgs> click = Click;
            if (click != null)
                click(this, e);
            ClickEvent.RaiseClassHandlers(this, e);
            ICommand command = Command;
            if (!e.Handled && command != null && command.CanExecute(CommandParameter))
            {
                command.Execute(CommandParameter);
                e.Handled = true;
            }
            if (Clicked != null)
                Clicked();
            Popup flyout = Flyout;
            if (flyout != null)
            {
                flyout.PlacementTarget = this;
                flyout.Open();
            }
        }

        bool IOverlayProvider.IsOverlayOpen
        {
            get { return Flyout != null && Flyout.IsOpen; }
        }

        VisualElement IOverlayProvider.OverlayElement
        {
            get { return Flyout == null ? null : Flyout.Child; }
        }

        void IOverlayProvider.UpdateOverlayLayout()
        {
            if (Flyout != null)
                ((IOverlayProvider)Flyout).UpdateOverlayLayout();
        }
        void IOverlayProvider.CloseOverlay()
        {
            if (Flyout != null)
                Flyout.Close();
            OnFlyoutClosed();
        }

        protected virtual void OnFlyoutClosed()
        {
        }
        bool ILightDismissOverlayProvider.IsLightDismissEnabled
        {
            get { return Flyout != null && Flyout.IsLightDismissEnabled; }
        }

        bool ILightDismissOverlayProvider.OverlayDismissEventPassThrough
        {
            get { return Flyout != null && Flyout.OverlayDismissEventPassThrough; }
        }

        void ApplyStyleAndLayout(RectangleF bounds)
        {
            _drawBounds = ScaleBounds(bounds, Math.Max(0f, Scale));

            Vector4 radius = ResolveCornerRadius(_drawBounds);
            bool rounded = Math.Max(Math.Max(radius.X, radius.Y), Math.Max(radius.Z, radius.W)) > 0f;
            if (_chrome != null)
            {
                _chrome.CornerRadius = radius;
                _chrome.UseGeneratedTexture = rounded;
            }
            if (_outline != null)
            {
                _outline.CornerRadius = radius;
                _outline.UseGeneratedTexture = rounded;
            }
            if (_presenter != null)
                _presenter.Padding = Padding;
            ArrangeTemplate(_drawBounds);
        }

        Vector4 ResolveCornerRadius(RectangleF bounds)
        {
            if (Shape == ButtonShape.Circle || Shape == ButtonShape.Pill)
                return new Vector4(Math.Min(bounds.Width, bounds.Height) * 0.5f);
            return Vector4.Max(CornerRadius, Vector4.Zero);
        }

        Color ResolveForeground(bool selected)
        {
            return Foreground;
        }

        static RectangleF ScaleBounds(RectangleF bounds, float scale)
        {
            Vector2 size = bounds.Size * scale;
            return new RectangleF(bounds.Center - size * 0.5f, size);
        }

        // Size of the text or icon the presenter draws for non-visual content.
        internal Vector2 MeasureDrawnContent()
        {
            Vector2 desired = Vector2.Zero;
            if (!string.IsNullOrEmpty(Text) && TextMetrics != null)
                desired = Vector2.Max(desired, TextMetrics.Measure("White", Text, TextScale));
            if (!string.IsNullOrEmpty(Icon) && _content == null)
                desired = Vector2.Max(desired, new Vector2(Math.Max(1f, 24f * Math.Max(0f, IconScale))));
            return desired;
        }

        internal void DrawPresenterContent(RenderContext context, RectangleF bounds)
        {
            Button owner = this;
            bool selected = owner.IsSelected;
            Color foreground = owner.ResolveForeground(selected);

            if (owner.ShowTopSeparator)
            {
                float inset = Math.Max(0f, owner.SeparatorInset);
                context.Fill(new RectangleF(
                    bounds.X + inset,
                    bounds.Y,
                    Math.Max(0f, bounds.Width - inset * 2f),
                    Math.Max(0f, owner.SeparatorThickness)),
                    owner.Separator);
            }

            if (!string.IsNullOrEmpty(owner.Icon))
            {
                float size = Math.Min(bounds.Width, bounds.Height) *
                             Math.Max(0f, owner.IconScale);
                context.Texture(
                    owner.Icon,
                    new RectangleF(
                        bounds.Center - new Vector2(size * 0.5f),
                        new Vector2(size)),
                    foreground,
                    owner.UseMissingIconFallback,
                    owner.IconRotation);
            }

            if (string.IsNullOrEmpty(owner.Text))
                return;

            Vector2 position;
            HorizontalAlignment horizontalAlignment;
            if (owner.HorizontalContentAlignment == HorizontalAlignment.Left)
            {
                position = new Vector2(
                    bounds.X + Math.Max(0f, owner.Padding.X),
                    bounds.Center.Y);
                horizontalAlignment = HorizontalAlignment.Left;
            }
            else if (owner.HorizontalContentAlignment == HorizontalAlignment.Right)
            {
                position = new Vector2(
                    bounds.Right - Math.Max(0f, owner.Padding.Z),
                    bounds.Center.Y);
                horizontalAlignment = HorizontalAlignment.Right;
            }
            else
            {
                position = bounds.Center;
                horizontalAlignment = HorizontalAlignment.Center;
            }
            context.Text(
                owner.Text,
                position,
                owner.TextScale,
                foreground,
                horizontalAlignment,
                owner.VerticalContentAlignment,
                TextOptions.GetDropShadow(owner));
        }

    }
}
