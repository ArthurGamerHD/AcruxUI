using System;
using Acrux.Core;
using Acrux.Core.Invalidation;
using Acrux.Core.Input;
using Acrux.Core.PropertySystem;
using Acrux.Rendering;
using Acrux.Styling;
using VRageMath;

namespace Acrux.Controls.Primitives
{
    /// <summary>A standalone range scrollbar suitable for templates and custom presenters.</summary>
    public sealed class ScrollBar : RangeBase
    {
        static ScrollBar() { }

        bool _dragging;
        float _grabOffset;
        RepeatButton _lineDecreaseButton;
        RepeatButton _lineIncreaseButton;
        Track _track;

        public static readonly StyledProperty<float> ViewportSizeProperty =
            StyledProperty.Register<ScrollBar, float>(
                nameof(ViewportSize), float.NaN, UiInvalidation.Arrange);
        public static readonly StyledProperty<ScrollBarVisibility> VisibilityProperty =
            StyledProperty.Register<ScrollBar, ScrollBarVisibility>(
                nameof(Visibility), ScrollBarVisibility.Visible, UiInvalidation.Measure);
        public static readonly StyledProperty<Orientation> OrientationProperty =
            StyledProperty.Register<ScrollBar, Orientation>(
                nameof(Orientation), Orientation.Vertical, UiInvalidation.Measure);
        public static readonly StyledProperty<bool> AllowAutoHideProperty =
            StyledProperty.Register<ScrollBar, bool>(nameof(AllowAutoHide), true);
        public static readonly StyledProperty<TimeSpan> HideDelayProperty =
            StyledProperty.Register<ScrollBar, TimeSpan>(
                nameof(HideDelay), TimeSpan.FromSeconds(2));
        public static readonly StyledProperty<TimeSpan> ShowDelayProperty =
            StyledProperty.Register<ScrollBar, TimeSpan>(
                nameof(ShowDelay), TimeSpan.FromSeconds(0.5));
        public static readonly StyledProperty<float> ThicknessProperty =
            StyledProperty.RegisterInternal<ScrollBar, float>(
                nameof(Thickness), 10f, UiInvalidation.Measure);
        public static readonly StyledProperty<float> MinimumThumbLengthProperty =
            StyledProperty.RegisterInternal<ScrollBar, float>(
                nameof(MinimumThumbLength), 8f, UiInvalidation.Arrange);
        public static readonly StyledProperty<BackgroundBrush> TrackBackgroundProperty =
            StyledProperty.RegisterInternal<ScrollBar, BackgroundBrush>(
                nameof(TrackBackground), new SolidColorBrush(new Color(82, 98, 107, 127)));
        public static readonly StyledProperty<BackgroundBrush> ThumbBackgroundProperty =
            StyledProperty.RegisterInternal<ScrollBar, BackgroundBrush>(
                nameof(ThumbBackground), new SolidColorBrush(new Color(56, 64, 70, 248)));

        public float ViewportSize
        {
            get { return GetValue(ViewportSizeProperty); }
            set { SetValue(ViewportSizeProperty, value); }
        }

        public ScrollBarVisibility Visibility
        {
            get { return GetValue(VisibilityProperty); }
            set { SetValue(VisibilityProperty, value); }
        }

        public Orientation Orientation
        {
            get { return GetValue(OrientationProperty); }
            set { SetValue(OrientationProperty, value); }
        }

        public bool AllowAutoHide
        {
            get { return GetValue(AllowAutoHideProperty); }
            set { SetValue(AllowAutoHideProperty, value); }
        }

        public TimeSpan HideDelay
        {
            get { return GetValue(HideDelayProperty); }
            set { SetValue(HideDelayProperty, value); }
        }

        public TimeSpan ShowDelay
        {
            get { return GetValue(ShowDelayProperty); }
            set { SetValue(ShowDelayProperty, value); }
        }

        public float Thickness
        {
            get { return GetValue(ThicknessProperty); }
            set { SetValue(ThicknessProperty, Math.Max(0f, value)); }
        }

        public float MinimumThumbLength
        {
            get { return GetValue(MinimumThumbLengthProperty); }
            set { SetValue(MinimumThumbLengthProperty, Math.Max(1f, value)); }
        }

        public BackgroundBrush TrackBackground
        {
            get { return GetValue(TrackBackgroundProperty); }
            set { SetValue(TrackBackgroundProperty, value); }
        }

        public BackgroundBrush ThumbBackground
        {
            get { return GetValue(ThumbBackgroundProperty); }
            set { SetValue(ThumbBackgroundProperty, value); }
        }

        public bool IsExpanded
        {
            get
            {
                return !AllowAutoHide || IsPointerOver || _dragging ||
                       (_track != null && _track.IsThumbDragging);
            }
        }

        public Action<ScrollEventArgs> Scroll { get; set; }
        public override bool AcceptsKeyboardFocus
        {
            get { return true; }
        }

        protected override void OnApplyTemplate()
        {
            _lineDecreaseButton = GetTemplateChild<RepeatButton>("PART_LineDecreaseButton");
            _lineIncreaseButton = GetTemplateChild<RepeatButton>("PART_LineIncreaseButton");
            _track = GetTemplateChild<Track>("PART_Track");
            RepeatButton pageDecrease = GetTemplateChild<RepeatButton>("PART_PageDecreaseButton");
            RepeatButton pageIncrease = GetTemplateChild<RepeatButton>("PART_PageIncreaseButton");
            Thumb thumb = GetTemplateChild<Thumb>("PART_Thumb");

            if (_lineDecreaseButton != null)
                _lineDecreaseButton.Clicked = () => Commit(
                    Value - Math.Abs(SmallChange),
                    ScrollEventType.SmallDecrement);
            if (_lineIncreaseButton != null)
                _lineIncreaseButton.Clicked = () => Commit(
                    Value + Math.Abs(SmallChange),
                    ScrollEventType.SmallIncrement);
            if (_track != null)
            {
                _track.ConfigureParts(pageDecrease, pageIncrease, thumb);
                _track.Scroll = args => Commit(args.NewValue, args.Type);
                SyncTrack();
            }
        }

        protected override void OnTemplateChanging()
        {
            if (_lineDecreaseButton != null)
                _lineDecreaseButton.Clicked = null;
            if (_lineIncreaseButton != null)
                _lineIncreaseButton.Clicked = null;
            if (_track != null)
            {
                _track.Scroll = null;
                _track.ConfigureParts(null, null, null);
            }
            _lineDecreaseButton = null;
            _lineIncreaseButton = null;
            _track = null;
        }

        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            float thickness = Math.Max(0f, Thickness);
            return Orientation == Orientation.Vertical
                ? new Vector2(Math.Min(availableSize.X, thickness), Math.Min(availableSize.Y, 100f))
                : new Vector2(Math.Min(availableSize.X, 100f), Math.Min(availableSize.Y, thickness));
        }

        protected override void ArrangeChildren(RectangleF bounds)
        {
            if (_track != null)
                SyncTrack();
            ArrangeTemplate(bounds);
        }

        protected override void DrawSelf(RenderContext context)
        {
            if (Visibility == ScrollBarVisibility.Disabled || Visibility == ScrollBarVisibility.Hidden)
                return;
            if (_track != null)
                return;
            context.Fill(Bounds, TrackBackground);
            context.Fill(GetThumbBounds(), ThumbBackground);
        }

        protected override bool HitTestSelf(Vector2 point)
        {
            return _track == null &&
                   Visibility != ScrollBarVisibility.Disabled &&
                   Visibility != ScrollBarVisibility.Hidden;
        }

        protected override void UpdatePseudoClasses()
        {
            base.UpdatePseudoClasses();
            PseudoClasses.Set(PseudoClassNames.Pressed, _dragging);
            PseudoClasses.Set(":expanded", IsExpanded);
            PseudoClasses.Set(":vertical", Orientation == Orientation.Vertical);
            PseudoClasses.Set(":horizontal", Orientation == Orientation.Horizontal);
        }

        internal override void PointerPressed(Vector2 point)
        {
            if (_track != null)
                return;
            RectangleF thumb = GetThumbBounds();
            if (thumb.Contains(point))
            {
                _dragging = true;
                _grabOffset = Coordinate(point) - Start(thumb);
                return;
            }
            float delta = Coordinate(point) < Start(thumb)
                ? -Math.Max(0f, LargeChange)
                : Math.Max(0f, LargeChange);
            Commit(Value + delta, delta < 0f
                ? ScrollEventType.LargeDecrement
                : ScrollEventType.LargeIncrement);
        }

        internal override void PointerMoved(Vector2 point, Vector2 delta)
        {
            if (_track != null)
                return;
            if (!_dragging)
                return;
            RectangleF thumb = GetThumbBounds();
            float trackLength = Length(Bounds);
            float travel = Math.Max(0f, trackLength - Length(thumb));
            if (travel <= 0f)
                return;
            float fraction = Clamp((Coordinate(point) - _grabOffset - Start(Bounds)) / travel, 0f, 1f);
            Commit(Minimum + (EffectiveMaximum - Minimum) * fraction, ScrollEventType.ThumbTrack);
        }

        internal override void PointerReleased(Vector2 point, bool clicked)
        {
            if (_track != null)
                return;
            bool wasDragging = _dragging;
            _dragging = false;
            if (wasDragging)
                RaiseScroll(ScrollEventType.EndScroll, Value);
        }

        internal override void KeyboardInput(IInputSource input)
        {
            if (input == null)
                return;
            if (input.IsNewKeyPressed(Key.Up) || input.IsNewKeyPressed(Key.Left))
                Commit(Value - Math.Abs(SmallChange), ScrollEventType.SmallDecrement);
            else if (input.IsNewKeyPressed(Key.Down) || input.IsNewKeyPressed(Key.Right))
                Commit(Value + Math.Abs(SmallChange), ScrollEventType.SmallIncrement);
            else if (input.IsNewKeyPressed(Key.PageUp))
                Commit(Value - Math.Abs(LargeChange), ScrollEventType.LargeDecrement);
            else if (input.IsNewKeyPressed(Key.PageDown))
                Commit(Value + Math.Abs(LargeChange), ScrollEventType.LargeIncrement);
            else if (input.IsNewKeyPressed(Key.Home))
                Commit(Minimum, ScrollEventType.First);
            else if (input.IsNewKeyPressed(Key.End))
                Commit(EffectiveMaximum, ScrollEventType.Last);
        }

        RectangleF GetThumbBounds()
        {
            float trackLength = Length(Bounds);
            float range = Math.Max(0f, EffectiveMaximum - Minimum);
            float viewport = float.IsNaN(ViewportSize) ? LargeChange : Math.Max(0f, ViewportSize);
            float thumbLength = range <= 0f
                ? trackLength
                : trackLength * viewport / Math.Max(0.0001f, range + viewport);
            thumbLength = Math.Min(trackLength, Math.Max(MinimumThumbLength, thumbLength));
            float travel = Math.Max(0f, trackLength - thumbLength);
            float fraction = range <= 0f ? 0f : Clamp((Value - Minimum) / range, 0f, 1f);
            return Orientation == Orientation.Vertical
                ? new RectangleF(Bounds.X, Bounds.Y + travel * fraction, Bounds.Width, thumbLength)
                : new RectangleF(Bounds.X + travel * fraction, Bounds.Y, thumbLength, Bounds.Height);
        }

        void Commit(float value, ScrollEventType type)
        {
            float next = Coerce(value);
            if (Math.Abs(next - Value) < 0.0001f)
                return;
            SetCurrentValue(next, true);
            RaiseScroll(type, next);
        }

        void SyncTrack()
        {
            if (_track == null)
                return;
            _track.Minimum = Minimum;
            _track.Maximum = EffectiveMaximum;
            _track.Value = Value;
            _track.ViewportSize = ViewportSize;
            _track.MinimumThumbLength = MinimumThumbLength;
            _track.Orientation = Orientation;
            _track.SmallChange = SmallChange;
            _track.LargeChange = LargeChange;
        }

        void RaiseScroll(ScrollEventType type, float value)
        {
            Action<ScrollEventArgs> scroll = Scroll;
            if (scroll != null)
                scroll(new ScrollEventArgs(type, value));
        }

        float Coordinate(Vector2 point) { return Orientation == Orientation.Vertical ? point.Y : point.X; }
        float Start(RectangleF bounds) { return Orientation == Orientation.Vertical ? bounds.Y : bounds.X; }
        float Length(RectangleF bounds) { return Orientation == Orientation.Vertical ? bounds.Height : bounds.Width; }

        static float Clamp(float value, float minimum, float maximum)
        {
            if (value < minimum) return minimum;
            if (value > maximum) return maximum;
            return value;
        }
    }
}
