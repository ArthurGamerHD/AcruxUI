using System;
using Acrux.Core.PropertySystem;
using Acrux.Styling;

namespace Acrux.Controls.Primitives
{
    public class ToggleButton : Button
    {
        static ToggleButton()
        {
        }

        public static readonly StyledProperty<bool?> IsCheckedProperty =
            StyledProperty.Register<ToggleButton, bool?>(nameof(IsChecked), false);

        public static readonly StyledProperty<bool> IsThreeStateProperty =
            StyledProperty.Register<ToggleButton, bool>(nameof(IsThreeState), false);

        public bool? IsChecked
        {
            get { return GetValue(IsCheckedProperty); }
            set { SetValue(IsCheckedProperty, value); }
        }

        public bool IsThreeState
        {
            get { return GetValue(IsThreeStateProperty); }
            set { SetValue(IsThreeStateProperty, value); }
        }

        public Func<bool?> IsCheckedProvider { get; set; }
        public Action<bool?> CheckedChanged { get; set; }

        protected virtual bool? CurrentCheckedValue
        {
            get { return IsCheckedProvider == null ? IsChecked : IsCheckedProvider(); }
        }

        protected override void UpdatePseudoClasses()
        {
            base.UpdatePseudoClasses();
            bool? value = CurrentCheckedValue;
            PseudoClasses.Set(PseudoClassNames.Checked, value == true);
            PseudoClasses.Set(PseudoClassNames.Unchecked, value == false);
            PseudoClasses.Set(PseudoClassNames.Indeterminate, !value.HasValue);
        }

        protected override void InvokeClick()
        {
            if (!IsEffectivelyEnabled)
                return;
            Toggle();
            base.InvokeClick();
        }

        protected virtual void Toggle()
        {
            if (!IsEffectivelyEnabled)
                return;
            bool? current = CurrentCheckedValue;
            bool? next = current == false ? (bool?)true
                : current == true && IsThreeState ? (bool?)null
                : false;
            IsChecked = next;
            Action<bool?> changed = CheckedChanged;
            if (changed != null)
                changed(next);
        }
    }
}