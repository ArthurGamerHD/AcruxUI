using System;
using Acrux.Core;
using Acrux.Core.Invalidation;
using Acrux.Core.PropertySystem;

namespace Acrux.Controls.Primitives
{
    /// <summary>Common finite range contract shared by sliders, scrollbars and progress bars.</summary>
    public abstract class RangeBase : TemplatedControl
    {
        static RangeBase() { }

        bool _coercing;

        public static readonly StyledProperty<float> MinimumProperty =
            StyledProperty.Register<RangeBase, float>(
                nameof(Minimum), 0f, UiInvalidation.Arrange);
        public static readonly StyledProperty<float> MaximumProperty =
            StyledProperty.Register<RangeBase, float>(
                nameof(Maximum), 100f, UiInvalidation.Arrange);
        public static readonly StyledProperty<float> ValueProperty =
            StyledProperty.Register<RangeBase, float>(
                nameof(Value), 0f, UiInvalidation.Arrange);
        public static readonly StyledProperty<float> SmallChangeProperty =
            StyledProperty.Register<RangeBase, float>(nameof(SmallChange), 1f);
        public static readonly StyledProperty<float> LargeChangeProperty =
            StyledProperty.Register<RangeBase, float>(nameof(LargeChange), 10f);

        public float Minimum
        {
            get { return GetValue(MinimumProperty); }
            set { SetValue(MinimumProperty, FiniteOr(value, 0f)); }
        }

        public float Maximum
        {
            get { return GetValue(MaximumProperty); }
            set { SetValue(MaximumProperty, FiniteOr(value, Minimum)); }
        }

        public float Value
        {
            get { return GetValue(ValueProperty); }
            set { SetValue(ValueProperty, Coerce(value)); }
        }

        public float SmallChange
        {
            get { return GetValue(SmallChangeProperty); }
            set { SetValue(SmallChangeProperty, Math.Max(0f, FiniteOr(value, 0f))); }
        }

        public float LargeChange
        {
            get { return GetValue(LargeChangeProperty); }
            set { SetValue(LargeChangeProperty, Math.Max(0f, FiniteOr(value, 0f))); }
        }

        /// <summary>Raised for user initiated changes. Programmatic assignments remain quiet.</summary>
        public Action<float> ValueChanged { get; set; }

        public void SetCurrentValue(float value, bool notify = false)
        {
            float next = Coerce(value);
            if (Math.Abs(next - Value) < 0.0001f)
                return;
            SetValue(ValueProperty, next);
            if (notify)
                RaiseValueChanged(next);
        }

        protected float EffectiveMaximum
        {
            get { return Math.Max(Minimum, Maximum); }
        }

        protected float Coerce(float value)
        {
            value = FiniteOr(value, Minimum);
            if (value < Minimum) return Minimum;
            if (value > EffectiveMaximum) return EffectiveMaximum;
            return value;
        }

        protected void RaiseValueChanged(float value)
        {
            Action<float> changed = ValueChanged;
            if (changed != null)
                changed(value);
        }

        protected override void OnPropertyChanged(IStyledProperty property)
        {
            base.OnPropertyChanged(property);
            if (_coercing || (property != MinimumProperty && property != MaximumProperty))
                return;
            _coercing = true;
            try
            {
                float minimum = FiniteOr(Minimum, 0f);
                float maximum = Math.Max(minimum, FiniteOr(Maximum, minimum));
                if (Math.Abs(Minimum - minimum) > 0.0001f)
                    SetValue(MinimumProperty, minimum);
                if (Math.Abs(Maximum - maximum) > 0.0001f)
                    SetValue(MaximumProperty, maximum);
                float value = Coerce(Value);
                if (Math.Abs(Value - value) > 0.0001f)
                    SetValue(ValueProperty, value);
            }
            finally
            {
                _coercing = false;
            }
        }

        static float FiniteOr(float value, float fallback)
        {
            return float.IsNaN(value) || float.IsInfinity(value) ? fallback : value;
        }
    }
}
