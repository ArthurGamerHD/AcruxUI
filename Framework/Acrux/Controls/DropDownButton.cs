using Acrux.Controls.Primitives;
using Acrux.Core.PropertySystem;

namespace Acrux.Controls
{
    /// <summary>A button whose primary action opens its Popup flyout.</summary>
    public sealed class DropDownButton : Button
    {
        static DropDownButton() { }

        public static readonly StyledProperty<bool> IsDropDownOpenProperty =
            StyledProperty.Register<DropDownButton, bool>(nameof(IsDropDownOpen), false);

        public bool IsDropDownOpen
        {
            get { return GetValue(IsDropDownOpenProperty); }
            set { SetValue(IsDropDownOpenProperty, value); }
        }

        protected override void InvokeClick()
        {
            base.InvokeClick();
            if (Flyout != null)
                IsDropDownOpen = true;
        }

        protected override void OnPropertyChanged(IStyledProperty property)
        {
            base.OnPropertyChanged(property);
            if (property != IsDropDownOpenProperty || Flyout == null)
                return;
            Flyout.PlacementTarget = this;
            if (IsDropDownOpen) Flyout.Open(); else Flyout.Close();
        }

        protected override void OnFlyoutClosed()
        {
            IsDropDownOpen = false;
        }
    }
}
