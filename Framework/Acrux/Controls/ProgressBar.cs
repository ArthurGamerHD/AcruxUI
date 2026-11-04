using System;
using System.Globalization;
using Acrux.Controls.Primitives;
using Acrux.Core;
using Acrux.Core.Invalidation;
using Acrux.Core.PropertySystem;
using Acrux.Rendering;
using Acrux.Styling;
using VRageMath;

namespace Acrux.Controls
{
    /// <summary>A renderer-neutral determinate or indeterminate progress indicator.</summary>
    public sealed class ProgressBar : RangeBase
    {
        const float DefaultThickness = 8f;

        static ProgressBar() { }

        readonly Border _track;
        readonly Border _indicator;
        readonly TextBlock _progressText;
        int _renderFrame;

        public static readonly StyledProperty<Orientation> OrientationProperty =
            StyledProperty.Register<ProgressBar, Orientation>(
                nameof(Orientation), Orientation.Horizontal, UiInvalidation.Arrange);
        public static readonly StyledProperty<bool> IsIndeterminateProperty =
            StyledProperty.Register<ProgressBar, bool>(
                nameof(IsIndeterminate), false, UiInvalidation.Arrange);
        public static readonly StyledProperty<bool> ShowProgressTextProperty =
            StyledProperty.Register<ProgressBar, bool>(
                nameof(ShowProgressText), false, UiInvalidation.Measure);
        public static readonly StyledProperty<string> ProgressTextFormatProperty =
            StyledProperty.Register<ProgressBar, string>(
                nameof(ProgressTextFormat), "{1:0}%", UiInvalidation.Measure);
        public static readonly StyledProperty<bool> IsReversedProperty =
            StyledProperty.Register<ProgressBar, bool>(
                nameof(IsReversed), false, UiInvalidation.Arrange);
        public static readonly StyledProperty<Vector4> CornerRadiusProperty =
            StyledProperty.Register<ProgressBar, Vector4>(
                nameof(CornerRadius), new Vector4(4f), UiInvalidation.Arrange);
        public static readonly StyledProperty<BackgroundBrush> BackgroundProperty =
            StyledProperty.Register<ProgressBar, BackgroundBrush>(
                nameof(Background), new SolidColorBrush(new Color(82, 98, 107, 127)));
        public static readonly StyledProperty<float> PercentageProperty =
            StyledProperty.Register<ProgressBar, float>(nameof(Percentage), 0f, UiInvalidation.Render);
        public static readonly StyledProperty<BackgroundBrush> ForegroundProperty =
            StyledProperty.Register<ProgressBar, BackgroundBrush>(
                nameof(Foreground), new SolidColorBrush(new Color(80, 160, 255)));

        public ProgressBar()
        {
            _track = AddChild(new Border());
            _indicator = AddChild(new Border());
            _progressText = AddChild(new TextBlock
            {
                TextAlignment = HorizontalAlignment.Center,
                VerticalTextAlignment = VerticalAlignment.Center,
                Visible = false
            });
        }

        public Orientation Orientation
        {
            get { return GetValue(OrientationProperty); }
            set { SetValue(OrientationProperty, value); }
        }

        public bool IsIndeterminate
        {
            get { return GetValue(IsIndeterminateProperty); }
            set { SetValue(IsIndeterminateProperty, value); }
        }

        public bool ShowProgressText
        {
            get { return GetValue(ShowProgressTextProperty); }
            set { SetValue(ShowProgressTextProperty, value); }
        }

        public string ProgressTextFormat
        {
            get { return GetValue(ProgressTextFormatProperty); }
            set { SetValue(ProgressTextFormatProperty, value); }
        }

        /// <summary>read-only percentage of the range, for templates.</summary>
        public float Percentage
        {
            get { return GetValue(PercentageProperty); }
            private set { SetValue(PercentageProperty, value); }
        }

        bool IsTemplated => TemplateRoot != null;

        protected override void OnPropertyChanged(IStyledProperty property)
        {
            base.OnPropertyChanged(property);
            if (property == ValueProperty || property == MinimumProperty || property == MaximumProperty)
                Percentage = Fraction * 100f;
        }

        protected override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            bool builtIn = !IsTemplated;
            _track.Visible = builtIn;
            _indicator.Visible = builtIn;
            _progressText.Visible = builtIn && ShowProgressText;
            Percentage = Fraction * 100f;
        }

        public bool IsReversed
        {
            get { return GetValue(IsReversedProperty); }
            set { SetValue(IsReversedProperty, value); }
        }

        public Vector4 CornerRadius
        {
            get { return GetValue(CornerRadiusProperty); }
            set { SetValue(CornerRadiusProperty, Vector4.Max(value, Vector4.Zero)); }
        }

        public BackgroundBrush Background
        {
            get { return GetValue(BackgroundProperty); }
            set { SetValue(BackgroundProperty, value); }
        }

        public BackgroundBrush Foreground
        {
            get { return GetValue(ForegroundProperty); }
            set { SetValue(ForegroundProperty, value); }
        }

        public float Fraction
        {
            get
            {
                float range = EffectiveMaximum - Minimum;
                return range <= 0.0001f
                    ? 0f
                    : MathHelper.Clamp((Value - Minimum) / range, 0f, 1f);
            }
            set { Value = MathHelper.Lerp(Minimum, EffectiveMaximum, MathHelper.Clamp(value, 0f, 1f)); }
        }

        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            if (IsTemplated)
                return TemplateRoot.Measure(availableSize);
            return Orientation == Orientation.Horizontal
                ? new Vector2(Math.Min(availableSize.X, 100f), Math.Min(availableSize.Y, DefaultThickness))
                : new Vector2(Math.Min(availableSize.X, DefaultThickness), Math.Min(availableSize.Y, 100f));
        }

        protected override void ArrangeChildren(RectangleF bounds)
        {
            if (IsTemplated)
            {
                ArrangeTemplate(bounds);
                return;
            }
            ApplyAppearance();
            RectangleF track = bounds;
            _track.Arrange(track);

            float fraction = IsIndeterminate ? 0.3f : Fraction;
            RectangleF indicator;
            if (Orientation == Orientation.Horizontal)
            {
                float width = track.Width * fraction;
                float indeterminatePosition = IsIndeterminate
                    ? IndeterminatePosition(track.Width, width)
                    : IsReversed ? track.Width - width : 0f;
                float x = track.X + indeterminatePosition;
                indicator = new RectangleF(x, track.Y, width, track.Height);
            }
            else
            {
                float height = track.Height * fraction;
                float indeterminatePosition = IsIndeterminate
                    ? IndeterminatePosition(track.Height, height)
                    : IsReversed ? 0f : track.Height - height;
                float y = track.Y + indeterminatePosition;
                indicator = new RectangleF(track.X, y, track.Width, height);
            }
            _indicator.Visible = IsIndeterminate || fraction > 0f;
            if (_indicator.Visible)
                _indicator.Arrange(indicator);

            _progressText.Visible = ShowProgressText && !IsIndeterminate;
            if (_progressText.Visible)
            {
                _progressText.Text = FormatProgressText();
                _progressText.Arrange(bounds);
            }
        }

        protected override void DrawSelf(RenderContext context)
        {
            _renderFrame++;
            if (IsTemplated)
                return;
            ApplyAppearance();
            if (IsIndeterminate)
            {
                ArrangeChildren(Bounds);
                context.MarkVolatile();
            }
        }


        void ApplyAppearance()
        {
            Vector4 radius = Vector4.Max(CornerRadius, Vector4.Zero);
            bool rounded = Math.Max(Math.Max(radius.X, radius.Y), Math.Max(radius.Z, radius.W)) > 0f;
            _track.Background = Background;
            _track.CornerRadius = radius;
            _track.UseGeneratedTexture = rounded;
            _indicator.Background = Foreground;
            _indicator.CornerRadius = radius;
            _indicator.UseGeneratedTexture = rounded;
        }

        float IndeterminatePosition(float trackLength, float indicatorLength)
        {
            float travel = Math.Max(0f, trackLength - indicatorLength);
            float phase = (_renderFrame % 90) / 89f;
            if (IsReversed)
                phase = 1f - phase;
            return travel * phase;
        }

        string FormatProgressText()
        {
            string format = string.IsNullOrEmpty(ProgressTextFormat)
                ? "{1:0}%"
                : ProgressTextFormat;
            try
            {
                return string.Format(CultureInfo.InvariantCulture, format, Value, Percentage);
            }
            catch (FormatException)
            {
                return Percentage.ToString("0", CultureInfo.InvariantCulture) + "%";
            }
        }
    }
}
