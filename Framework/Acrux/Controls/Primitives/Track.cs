using System;
using Acrux.Controls;
using Acrux.Core;
using Acrux.Core.Invalidation;
using Acrux.Core.PropertySystem;
using Acrux.Rendering;
using Acrux.Styling;
using VRageMath;

namespace Acrux.Controls.Primitives
{
    /// <summary>
    /// Lays out the decrease region, thumb, and increase region for a range control.
    /// Its visual parts are supplied by the owning control template.
    /// </summary>
    public sealed class Track : VisualElement
    {
        RepeatButton _decreaseButton;
        RepeatButton _increaseButton;
        Thumb _thumb;
        float _grabOffset;

        public static readonly StyledProperty<float> MinimumProperty =
            StyledProperty.Register<Track, float>(nameof(Minimum), 0f, UiInvalidation.Arrange);
        public static readonly StyledProperty<float> MaximumProperty =
            StyledProperty.Register<Track, float>(nameof(Maximum), 100f, UiInvalidation.Arrange);
        public static readonly StyledProperty<float> ValueProperty =
            StyledProperty.Register<Track, float>(nameof(Value), 0f, UiInvalidation.Arrange);
        public static readonly StyledProperty<float> ViewportSizeProperty =
            StyledProperty.Register<Track, float>(nameof(ViewportSize), float.NaN, UiInvalidation.Arrange);
        public static readonly StyledProperty<float> MinimumThumbLengthProperty =
            StyledProperty.RegisterInternal<Track, float>(nameof(MinimumThumbLength), 8f, UiInvalidation.Arrange);
        public static readonly StyledProperty<Orientation> OrientationProperty =
            StyledProperty.Register<Track, Orientation>(nameof(Orientation), Orientation.Vertical, UiInvalidation.Arrange);
        public static readonly StyledProperty<BackgroundBrush> BackgroundProperty =
            StyledProperty.RegisterInternal<Track, BackgroundBrush>(
                nameof(Background),
                new SolidColorBrush(Color.Transparent),
                UiInvalidation.Render);

        public float Minimum
        {
            get { return GetValue(MinimumProperty); }
            set { SetValue(MinimumProperty, value); }
        }

        public float Maximum
        {
            get { return GetValue(MaximumProperty); }
            set { SetValue(MaximumProperty, value); }
        }

        public float Value
        {
            get { return GetValue(ValueProperty); }
            set { SetValue(ValueProperty, Clamp(value, Minimum, EffectiveMaximum)); }
        }

        public float ViewportSize
        {
            get { return GetValue(ViewportSizeProperty); }
            set { SetValue(ViewportSizeProperty, value); }
        }

        public float MinimumThumbLength
        {
            get { return GetValue(MinimumThumbLengthProperty); }
            set { SetValue(MinimumThumbLengthProperty, Math.Max(1f, value)); }
        }

        public Orientation Orientation
        {
            get { return GetValue(OrientationProperty); }
            set { SetValue(OrientationProperty, value); }
        }

        public BackgroundBrush Background
        {
            get { return GetValue(BackgroundProperty); }
            set { SetValue(BackgroundProperty, value); }
        }

        public float SmallChange { get; set; } = 1f;
        public float LargeChange { get; set; } = 10f;
        public Action<ScrollEventArgs> Scroll { get; set; }
        internal bool IsThumbDragging { get; private set; }

        float EffectiveMaximum
        {
            get { return Math.Max(Minimum, Maximum); }
        }

        internal void ConfigureParts(
            RepeatButton decreaseButton,
            RepeatButton increaseButton,
            Thumb thumb)
        {
            if (_decreaseButton != null)
                _decreaseButton.Clicked = null;
            if (_increaseButton != null)
                _increaseButton.Clicked = null;
            if (_thumb != null)
            {
                _thumb.DragStarted = null;
                _thumb.DragDelta = null;
                _thumb.DragCompleted = null;
            }

            _decreaseButton = decreaseButton;
            _increaseButton = increaseButton;
            _thumb = thumb;
            if (_decreaseButton != null)
                _decreaseButton.Clicked = () => Commit(Value - Math.Abs(LargeChange), ScrollEventType.LargeDecrement);
            if (_increaseButton != null)
                _increaseButton.Clicked = () => Commit(Value + Math.Abs(LargeChange), ScrollEventType.LargeIncrement);
            if (_thumb != null)
            {
                _thumb.DragStarted = BeginThumbDrag;
                _thumb.DragDelta = MoveThumb;
                _thumb.DragCompleted = EndThumbDrag;
            }
            Invalidate(UiInvalidation.Arrange);
        }

        protected override void ArrangeChildren(RectangleF bounds)
        {
            RectangleF thumbBounds = GetThumbBounds(bounds);
            if (_thumb != null && _thumb.Visible)
                _thumb.Arrange(thumbBounds);

            if (Orientation == Orientation.Vertical)
            {
                if (_decreaseButton != null && _decreaseButton.Visible)
                    _decreaseButton.Arrange(new RectangleF(
                        bounds.X, bounds.Y, bounds.Width,
                        Math.Max(0f, thumbBounds.Y - bounds.Y)));
                if (_increaseButton != null && _increaseButton.Visible)
                    _increaseButton.Arrange(new RectangleF(
                        bounds.X, thumbBounds.Bottom, bounds.Width,
                        Math.Max(0f, bounds.Bottom - thumbBounds.Bottom)));
            }
            else
            {
                if (_decreaseButton != null && _decreaseButton.Visible)
                    _decreaseButton.Arrange(new RectangleF(
                        bounds.X, bounds.Y,
                        Math.Max(0f, thumbBounds.X - bounds.X), bounds.Height));
                if (_increaseButton != null && _increaseButton.Visible)
                    _increaseButton.Arrange(new RectangleF(
                        thumbBounds.Right, bounds.Y,
                        Math.Max(0f, bounds.Right - thumbBounds.Right), bounds.Height));
            }
        }

        protected override void DrawSelf(RenderContext context)
        {
            context.Fill(Bounds, Background);
        }

        void BeginThumbDrag(Vector2 point)
        {
            IsThumbDragging = true;
            RectangleF thumbBounds = GetThumbBounds(Bounds);
            _grabOffset = Coordinate(point) - Start(thumbBounds);
        }

        void MoveThumb(Vector2 point, Vector2 delta)
        {
            RectangleF thumbBounds = GetThumbBounds(Bounds);
            float travel = Math.Max(0f, Length(Bounds) - Length(thumbBounds));
            if (travel <= 0f)
                return;
            float fraction = Clamp(
                (Coordinate(point) - _grabOffset - Start(Bounds)) / travel,
                0f,
                1f);
            Commit(Minimum + (EffectiveMaximum - Minimum) * fraction, ScrollEventType.ThumbTrack);
        }

        void EndThumbDrag()
        {
            IsThumbDragging = false;
            RaiseScroll(ScrollEventType.EndScroll, Value);
        }

        void Commit(float value, ScrollEventType type)
        {
            float next = Clamp(value, Minimum, EffectiveMaximum);
            if (Math.Abs(next - Value) < 0.0001f)
                return;
            Value = next;
            RaiseScroll(type, next);
        }

        void RaiseScroll(ScrollEventType type, float value)
        {
            Action<ScrollEventArgs> handler = Scroll;
            if (handler != null)
                handler(new ScrollEventArgs(type, value));
        }

        RectangleF GetThumbBounds(RectangleF bounds)
        {
            float trackLength = Length(bounds);
            float range = Math.Max(0f, EffectiveMaximum - Minimum);
            float viewport = float.IsNaN(ViewportSize)
                ? Math.Max(0f, LargeChange)
                : Math.Max(0f, ViewportSize);
            float thumbLength = range <= 0f
                ? trackLength
                : trackLength * viewport / Math.Max(0.0001f, range + viewport);
            thumbLength = Math.Min(trackLength, Math.Max(MinimumThumbLength, thumbLength));
            float travel = Math.Max(0f, trackLength - thumbLength);
            float fraction = range <= 0f ? 0f : Clamp((Value - Minimum) / range, 0f, 1f);
            return Orientation == Orientation.Vertical
                ? new RectangleF(bounds.X, bounds.Y + travel * fraction, bounds.Width, thumbLength)
                : new RectangleF(bounds.X + travel * fraction, bounds.Y, thumbLength, bounds.Height);
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
