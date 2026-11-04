using System;
using System.Globalization;
using Acrux.Core;
using Acrux.Core.Invalidation;
using Acrux.Core.PropertySystem;
using Acrux.Layout;
using Acrux.Rendering;
using VRageMath;

namespace Acrux.Controls
{
    /// <summary>
    /// Numeric editor composed from a TextBox and two Buttons supplied by its
    /// control template. The control itself has no dependency on a host input API.
    /// </summary>
    public sealed class NumericUpDown : TemplatedControl
    {
        static NumericUpDown() { }

        Button _decreaseButton;
        TextBox _textBox;
        Button _increaseButton;
        Grid _layoutRoot;
        bool _synchronizingText;
        bool _hasAppliedCompact;
        bool _appliedCompact;

        public static readonly StyledProperty<double> MinimumProperty =
            StyledProperty.Register<NumericUpDown, double>(
                nameof(Minimum), double.MinValue, UiInvalidation.Render);
        public static readonly StyledProperty<double> MaximumProperty =
            StyledProperty.Register<NumericUpDown, double>(
                nameof(Maximum), double.MaxValue, UiInvalidation.Render);
        public static readonly StyledProperty<double> ValueProperty =
            StyledProperty.Register<NumericUpDown, double>(
                nameof(Value), 0d, UiInvalidation.Render);
        public static readonly StyledProperty<double> StepProperty =
            StyledProperty.Register<NumericUpDown, double>(nameof(Step), 1d);
        public static readonly StyledProperty<string> FormatProperty =
            StyledProperty.Register<NumericUpDown, string>(nameof(Format), "0.###");
        /// <summary>Same as <see cref="StepProperty"/>.</summary>
        public static readonly StyledProperty<double> IncrementProperty = StepProperty;
        /// <summary>Same as <see cref="FormatProperty"/>.</summary>
        public static readonly StyledProperty<string> FormatStringProperty = FormatProperty;
        public static readonly StyledProperty<bool> CompactProperty =
            StyledProperty.Register<NumericUpDown, bool>(
                nameof(Compact), false, UiInvalidation.Measure);

        public double Minimum
        {
            get { return GetValue(MinimumProperty); }
            set { SetValue(MinimumProperty, value); CoerceValue(); }
        }

        public double Maximum
        {
            get { return GetValue(MaximumProperty); }
            set { SetValue(MaximumProperty, value); CoerceValue(); }
        }

        public double Value
        {
            get { return Clamp(GetValue(ValueProperty)); }
            set { SetNumericValue(value, false); }
        }

        public double Step
        {
            get { return GetValue(StepProperty); }
            set { SetValue(StepProperty, Math.Max(0d, value)); }
        }

        public string Format
        {
            get { return GetValue(FormatProperty) ?? "0.###"; }
            set { SetValue(FormatProperty, string.IsNullOrWhiteSpace(value) ? "0.###" : value); }
        }

        /// <summary>Same as <see cref="Step"/>: the +/- button and wheel step.</summary>
        public double Increment
        {
            get { return Step; }
            set { Step = value; }
        }

        /// <summary>Same as <see cref="Format"/>: the .NET format of the shown value.</summary>
        public string FormatString
        {
            get { return Format; }
            set { Format = value; }
        }

        public bool Compact
        {
            get { return GetValue(CompactProperty); }
            set { SetValue(CompactProperty, value); }
        }

        public Action<double> ValueChanged { get; set; }

        protected override void OnApplyTemplate()
        {
            DetachParts();
            _layoutRoot = GetTemplateChild<Grid>("PART_LayoutRoot");
            _decreaseButton = GetTemplateChild<Button>("PART_DecreaseButton");
            _textBox = GetTemplateChild<TextBox>("PART_TextBox");
            _increaseButton = GetTemplateChild<Button>("PART_IncreaseButton");

            if (_decreaseButton != null)
            {
                _decreaseButton.Text = FormatStep(-Step);
                _decreaseButton.Clicked = delegate { Add(-Step, true); };
            }
            if (_increaseButton != null)
            {
                _increaseButton.Text = FormatStep(Step);
                _increaseButton.Clicked = delegate { Add(Step, true); };
            }
            if (_textBox != null)
                _textBox.TextChanged = OnTextChanged;
            SynchronizeParts(true);
        }

        protected override void OnTemplateChanging()
        {
            DetachParts();
            base.OnTemplateChanging();
        }

        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            EnsureStyleApplied();
            if (TemplateRoot != null)
                return TemplateRoot.Measure(availableSize);
            return new Vector2(
                Math.Min(availableSize.X, 160f),
                Math.Min(availableSize.Y, 34f));
        }

        protected override void ArrangeChildren(RectangleF bounds)
        {
            SynchronizeParts(false);
            ArrangeTemplate(bounds);
        }

        protected override void DrawSelf(RenderContext context)
        {
            SynchronizeParts(false);
        }

        internal override bool PointerScrolled(Vector2 point, int delta)
        {
            if (delta == 0 || !IsEffectivelyEnabled)
                return false;
            Add(delta > 0 ? Step : -Step, true);
            return true;
        }

        public void Add(double delta, bool notify = false)
        {
            SetNumericValue(Value + delta, notify);
        }

        public bool TrySetValue(string text, bool notify = false)
        {
            double parsed;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentUICulture, out parsed) &&
                !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                return false;
            SetNumericValue(parsed, notify);
            return true;
        }

        void SetNumericValue(double value, bool notify)
        {
            double next = Clamp(value);
            if (Math.Abs(next - GetValue(ValueProperty)) <= 0.0000001d)
                return;
            SetValue(ValueProperty, next);
            SynchronizeParts(true);
            if (notify && ValueChanged != null)
                ValueChanged(next);
        }

        void CoerceValue()
        {
            double current = GetValue(ValueProperty);
            double next = Clamp(current);
            if (Math.Abs(next - current) > 0.0000001d)
                SetValue(ValueProperty, next);
            SynchronizeParts(true);
        }

        double Clamp(double value)
        {
            double maximum = Math.Max(Minimum, Maximum);
            if (value < Minimum)
                return Minimum;
            if (value > maximum)
                return maximum;
            return value;
        }

        void OnTextChanged(string text)
        {
            if (_synchronizingText)
                return;
            TrySetValue(text, true);
        }

        void SynchronizeParts(bool forceText)
        {
            if (_layoutRoot != null &&
                (!_hasAppliedCompact || _appliedCompact != Compact))
            {
                _layoutRoot.SetColumns(Compact
                    ? new[] { 0f, 1f, 0f }
                    : new[] { 0.2f, 0.6f, 0.2f });
                _appliedCompact = Compact;
                _hasAppliedCompact = true;
            }
            if (_decreaseButton != null)
            {
                _decreaseButton.Visible = !Compact;
                _decreaseButton.Text = FormatStep(-Step);
                _decreaseButton.Enabled = Enabled;
            }
            if (_increaseButton != null)
            {
                _increaseButton.Visible = !Compact;
                _increaseButton.Text = FormatStep(Step);
                _increaseButton.Enabled = Enabled;
            }
            if (_textBox == null)
                return;

            _textBox.Enabled = Enabled;
            if (!forceText && _textBox.IsFocused)
                return;
            string text = Value.ToString(Format, CultureInfo.CurrentUICulture);
            if (string.Equals(_textBox.Text, text, StringComparison.Ordinal))
                return;
            _synchronizingText = true;
            try
            {
                _textBox.Text = text;
            }
            finally
            {
                _synchronizingText = false;
            }
        }

        void DetachParts()
        {
            if (_decreaseButton != null)
                _decreaseButton.Clicked = null;
            if (_increaseButton != null)
                _increaseButton.Clicked = null;
            if (_textBox != null)
                _textBox.TextChanged = null;
            _decreaseButton = null;
            _textBox = null;
            _increaseButton = null;
            _layoutRoot = null;
            _hasAppliedCompact = false;
        }

        static string FormatStep(double value)
        {
            return value > 0d
                ? "+" + value.ToString("0.###", CultureInfo.CurrentUICulture)
                : value.ToString("0.###", CultureInfo.CurrentUICulture);
        }
    }
}
