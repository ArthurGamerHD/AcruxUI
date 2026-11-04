using System;
using System.Collections.Generic;
using Acrux.Controls.Primitives;
using Acrux.Core;
using Acrux.Core.Invalidation;
using Acrux.Core.PropertySystem;
using Acrux.Rendering;
using Acrux.Styling;
using Acrux.Core.Input;
using VRageMath;

namespace Acrux.Controls
{
    /// <summary>A horizontal range control with pointer and keyboard input.</summary>
    public sealed class Slider : RangeBase
    {
        static Slider() { }

        Border _track;
        Border _fill;
        Border _thumb;

        public static readonly StyledProperty<Orientation> OrientationProperty =
            StyledProperty.Register<Slider, Orientation>(
                nameof(Orientation), Orientation.Horizontal, UiInvalidation.Measure);
        public static readonly StyledProperty<bool> IsDirectionReversedProperty =
            StyledProperty.Register<Slider, bool>(
                nameof(IsDirectionReversed), false, UiInvalidation.Arrange);
        public static readonly StyledProperty<bool> IsSnapToTickEnabledProperty =
            StyledProperty.Register<Slider, bool>(nameof(IsSnapToTickEnabled), false);
        public static readonly StyledProperty<float> TickFrequencyProperty =
            StyledProperty.Register<Slider, float>(nameof(TickFrequency), 0f);
        public static readonly StyledProperty<TickPlacement> TickPlacementProperty =
            StyledProperty.Register<Slider, TickPlacement>(
                nameof(TickPlacement), TickPlacement.None, UiInvalidation.Measure);
        public static readonly StyledProperty<IList<float>> TicksProperty =
            StyledProperty.Register<Slider, IList<float>>(nameof(Ticks), null);
        // Track and thumb geometry are styled properties, so a theme can size them to its textures (Space
        // Engineers draws Keen's framed rail over the whole control and runs the thumb inside it).
        public static readonly StyledProperty<float> TrackHeightProperty =
            StyledProperty.Register<Slider, float>(
                nameof(TrackHeight), 6f, UiInvalidation.Arrange);
        public static readonly StyledProperty<float> TrackInsetProperty =
            StyledProperty.Register<Slider, float>(
                nameof(TrackInset), 12f, UiInvalidation.Arrange);
        public static readonly StyledProperty<float> TrackVisualHeightProperty =
            StyledProperty.Register<Slider, float>(
                nameof(TrackVisualHeight), -1f, UiInvalidation.Arrange);
        public static readonly StyledProperty<float> TrackVisualInsetProperty =
            StyledProperty.Register<Slider, float>(
                nameof(TrackVisualInset), -1f, UiInvalidation.Arrange);
        public static readonly StyledProperty<float> ThumbSizeProperty =
            StyledProperty.Register<Slider, float>(
                nameof(ThumbSize), 16f, UiInvalidation.Arrange);
        public static readonly StyledProperty<BackgroundBrush> TrackBackgroundProperty =
            StyledProperty.RegisterInternal<Slider, BackgroundBrush>(
                nameof(TrackBackground),
                new SolidColorBrush(new Color(82, 98, 107)));
        public static readonly StyledProperty<BackgroundBrush> FillBackgroundProperty =
            StyledProperty.RegisterInternal<Slider, BackgroundBrush>(
                nameof(FillBackground),
                new SolidColorBrush(new Color(80, 160, 255)));
        public static readonly StyledProperty<BackgroundBrush> ThumbBackgroundProperty =
            StyledProperty.RegisterInternal<Slider, BackgroundBrush>(
                nameof(ThumbBackground), new SolidColorBrush(Color.White));
        public static readonly StyledProperty<Color> ThumbShadowProperty =
            StyledProperty.RegisterInternal<Slider, Color>(
                nameof(ThumbShadow), new Color(0, 0, 0, 90));
        public static readonly StyledProperty<Vector2> ThumbShadowOffsetProperty =
            StyledProperty.RegisterInternal<Slider, Vector2>(
                nameof(ThumbShadowOffset), new Vector2(1f, 1f));

        public Slider()
        {
        }

        public Orientation Orientation
        {
            get { return GetValue(OrientationProperty); }
            set { SetValue(OrientationProperty, value); }
        }

        public bool IsDirectionReversed
        {
            get { return GetValue(IsDirectionReversedProperty); }
            set { SetValue(IsDirectionReversedProperty, value); }
        }

        public bool IsSnapToTickEnabled
        {
            get { return GetValue(IsSnapToTickEnabledProperty); }
            set { SetValue(IsSnapToTickEnabledProperty, value); }
        }

        public float TickFrequency
        {
            get { return GetValue(TickFrequencyProperty); }
            set { SetValue(TickFrequencyProperty, Math.Max(0f, value)); }
        }

        public TickPlacement TickPlacement
        {
            get { return GetValue(TickPlacementProperty); }
            set { SetValue(TickPlacementProperty, value); }
        }

        public IList<float> Ticks
        {
            get { return GetValue(TicksProperty); }
            set { SetValue(TicksProperty, value); }
        }

        public float TrackHeight
        {
            get { return GetValue(TrackHeightProperty); }
            set { SetValue(TrackHeightProperty, value); }
        }

        public float TrackInset
        {
            get { return GetValue(TrackInsetProperty); }
            set { SetValue(TrackInsetProperty, value); }
        }

        public float TrackVisualHeight
        {
            get { return GetValue(TrackVisualHeightProperty); }
            set { SetValue(TrackVisualHeightProperty, value); }
        }

        public float TrackVisualInset
        {
            get { return GetValue(TrackVisualInsetProperty); }
            set { SetValue(TrackVisualInsetProperty, value); }
        }

        public float ThumbSize
        {
            get { return GetValue(ThumbSizeProperty); }
            set { SetValue(ThumbSizeProperty, value); }
        }

        public BackgroundBrush TrackBackground
        {
            get { return GetValue(TrackBackgroundProperty); }
            set { SetValue(TrackBackgroundProperty, value); }
        }

        public BackgroundBrush FillBackground
        {
            get { return GetValue(FillBackgroundProperty); }
            set { SetValue(FillBackgroundProperty, value); }
        }

        public BackgroundBrush ThumbBackground
        {
            get { return GetValue(ThumbBackgroundProperty); }
            set { SetValue(ThumbBackgroundProperty, value); }
        }

        public Color ThumbShadow
        {
            get { return GetValue(ThumbShadowProperty); }
            set { SetValue(ThumbShadowProperty, value); }
        }

        public Vector2 ThumbShadowOffset
        {
            get { return GetValue(ThumbShadowOffsetProperty); }
            set { SetValue(ThumbShadowOffsetProperty, value); }
        }

        public Func<float> ValueProvider { get; set; }
        public override bool AcceptsKeyboardFocus
        {
            get { return true; }
        }

        protected override void OnApplyTemplate()
        {
            _track = GetTemplateChild<Border>("PART_Track");
            _fill = GetTemplateChild<Border>("PART_Fill");
            _thumb = GetTemplateChild<Border>("PART_Thumb");
        }

        protected override void UpdatePseudoClasses()
        {
            base.UpdatePseudoClasses();
            PseudoClasses.Set(PseudoClassNames.Pressed, IsPressed);
        }

        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            EnsureStyleApplied();
            return Orientation == Orientation.Horizontal
                ? new Vector2(Math.Min(availableSize.X, 260f), Math.Min(availableSize.Y, 34f))
                : new Vector2(Math.Min(availableSize.X, 34f), Math.Min(availableSize.Y, 260f));
        }

        protected override bool HitTestSelf(Vector2 point) { return true; }

        protected override void ArrangeChildren(RectangleF bounds)
        {
            ArrangeTemplate(bounds);
            float normalized = VisualFraction(CurrentValue);
            RectangleF track = GetTrackBounds(bounds);
            if (_track != null)
                _track.Arrange(GetVisualTrackBounds(bounds));
            float thumbSize = Math.Max(1f, Math.Min(ThumbSize,
                Orientation == Orientation.Horizontal ? bounds.Height : bounds.Width));
            if (Orientation == Orientation.Horizontal)
            {
                if (_fill != null)
                    _fill.Arrange(new RectangleF(
                        track.X, track.Y, Math.Max(1f, track.Width * normalized), track.Height));
                if (_thumb != null)
                    _thumb.Arrange(new RectangleF(
                        track.X + track.Width * normalized - thumbSize * 0.5f,
                        bounds.Center.Y - thumbSize * 0.5f, thumbSize, thumbSize));
            }
            else
            {
                // Like vertical Track, the minimum sits at the bottom and the fill grows upwards.
                float filled = Math.Max(1f, track.Height * normalized);
                if (_fill != null)
                    _fill.Arrange(new RectangleF(track.X, track.Bottom - filled, track.Width, filled));
                if (_thumb != null)
                    _thumb.Arrange(new RectangleF(
                        bounds.Center.X - thumbSize * 0.5f,
                        track.Bottom - track.Height * normalized - thumbSize * 0.5f,
                        thumbSize, thumbSize));
            }
        }

        protected override void DrawSelf(RenderContext context)
        {
            ArrangeChildren(Bounds);
        }

        internal override void PointerPressed(Vector2 point) { CommitFromPoint(point); }

        internal override void PointerMoved(Vector2 point, Vector2 delta)
        {
            if (IsPressed)
                CommitFromPoint(point);
        }

        internal override void KeyboardInput(IInputSource input)
        {
            if (input == null)
                return;
            if (input.IsNewKeyPressed(Key.Left) || input.IsNewKeyPressed(Key.Down))
                SetValue(CurrentValue - DirectionSign * Math.Abs(SmallChange), true);
            else if (input.IsNewKeyPressed(Key.Right) || input.IsNewKeyPressed(Key.Up))
                SetValue(CurrentValue + DirectionSign * Math.Abs(SmallChange), true);
            else if (input.IsNewKeyPressed(Key.PageDown))
                SetValue(CurrentValue - DirectionSign * Math.Abs(LargeChange), true);
            else if (input.IsNewKeyPressed(Key.PageUp))
                SetValue(CurrentValue + DirectionSign * Math.Abs(LargeChange), true);
            else if (input.IsNewKeyPressed(Key.Home))
                SetValue(Minimum, true);
            else if (input.IsNewKeyPressed(Key.End))
                SetValue(Maximum, true);
        }

        public void SetValue(float value, bool notify = false)
        {
            SetCurrentValue(Snap(value), notify);
        }

        void CommitFromPoint(Vector2 point)
        {
            RectangleF track = GetTrackBounds(Bounds);
            float length = Orientation == Orientation.Horizontal ? track.Width : track.Height;
            if (length <= 0f)
                return;
            float normalized = Orientation == Orientation.Horizontal
                ? MathHelper.Clamp((point.X - track.X) / track.Width, 0f, 1f)
                : MathHelper.Clamp((track.Bottom - point.Y) / track.Height, 0f, 1f);
            if (IsDirectionReversed)
                normalized = 1f - normalized;
            SetValue(MathHelper.Lerp(Minimum, EffectiveMaximum, normalized), true);
        }

        float CurrentValue
        {
            get { return Coerce(ValueProvider?.Invoke() ?? Value); }
        }

        float Normalize(float value)
        {
            float range = EffectiveMaximum - Minimum;
            return range <= 0.0001f ? 0f : MathHelper.Clamp((value - Minimum) / range, 0f, 1f);
        }

        float VisualFraction(float value)
        {
            float fraction = Normalize(value);
            return IsDirectionReversed ? 1f - fraction : fraction;
        }

        float DirectionSign
        {
            get { return IsDirectionReversed ? -1f : 1f; }
        }

        float Snap(float value)
        {
            value = Coerce(value);
            if (!IsSnapToTickEnabled)
                return value;
            IList<float> ticks = Ticks;
            if (ticks != null && ticks.Count > 0)
            {
                float nearest = Coerce(ticks[0]);
                float distance = Math.Abs(value - nearest);
                for (int i = 1; i < ticks.Count; i++)
                {
                    float candidate = Coerce(ticks[i]);
                    float candidateDistance = Math.Abs(value - candidate);
                    if (candidateDistance < distance)
                    {
                        nearest = candidate;
                        distance = candidateDistance;
                    }
                }
                return nearest;
            }
            float frequency = Math.Abs(TickFrequency);
            if (frequency <= 0.0001f)
                return value;
            return Coerce(Minimum + (float)Math.Round((value - Minimum) / frequency) * frequency);
        }

        RectangleF GetVisualTrackBounds(RectangleF rect)
        {
            float configuredHeight = TrackVisualHeight >= 0f ? TrackVisualHeight : TrackHeight;
            float configuredInset = TrackVisualInset >= 0f ? TrackVisualInset : TrackInset;
            if (Orientation == Orientation.Horizontal)
            {
                float height = Math.Max(1f, Math.Min(configuredHeight, rect.Height));
                float inset = Math.Max(0f, Math.Min(configuredInset, rect.Width * 0.5f));
                return new RectangleF(rect.X + inset, rect.Center.Y - height * 0.5f,
                    Math.Max(1f, rect.Width - inset * 2f), height);
            }
            float width = Math.Max(1f, Math.Min(configuredHeight, rect.Width));
            float verticalInset = Math.Max(0f, Math.Min(configuredInset, rect.Height * 0.5f));
            return new RectangleF(rect.Center.X - width * 0.5f, rect.Y + verticalInset,
                width, Math.Max(1f, rect.Height - verticalInset * 2f));
        }

        RectangleF GetTrackBounds(RectangleF rect)
        {
            if (Orientation == Orientation.Horizontal)
            {
                float height = Math.Max(1f, Math.Min(TrackHeight, rect.Height));
                float inset = Math.Max(0f, Math.Min(TrackInset, rect.Width * 0.5f));
                return new RectangleF(rect.X + inset, rect.Center.Y - height * 0.5f,
                    Math.Max(1f, rect.Width - inset * 2f), height);
            }
            float width = Math.Max(1f, Math.Min(TrackHeight, rect.Width));
            float verticalInset = Math.Max(0f, Math.Min(TrackInset, rect.Height * 0.5f));
            return new RectangleF(rect.Center.X - width * 0.5f, rect.Y + verticalInset,
                width, Math.Max(1f, rect.Height - verticalInset * 2f));
        }
    }
}
