using System;
using Acrux.Controls.Primitives;
using Acrux.Core;
using Acrux.Core.Invalidation;
using Acrux.Core.PropertySystem;

namespace Acrux.Controls
{
    /// <summary>
    /// Avalonia's Flyout: content in a light-dismiss popup placed next to a target, a button's
    /// (<see cref="Button.Flyout"/>, opened by its click) or any element's (<see cref="ShowAt"/>). The popup takes the
    /// size its content measures: give the content its own size, not the flyout. The content sits in a presenter, a
    /// Border with the style class "flyout-presenter" (plus <see cref="FlyoutPresenterClasses"/>), which the themes
    /// style as the flyout's surface. It inherits the target's data context when it opens.
    /// </summary>
    public sealed class Flyout : Popup
    {
        internal const string PRESENTER_CLASS = "flyout-presenter";

        public static readonly StyledProperty<VisualElement> ContentProperty =
            StyledProperty.Register<Flyout, VisualElement>(nameof(Content), null, UiInvalidation.Measure);
        public static readonly StyledProperty<string> FlyoutPresenterClassesProperty =
            StyledProperty.Register<Flyout, string>(nameof(FlyoutPresenterClasses), null);

        readonly Border _presenter;
        // The element ShowAt attached the flyout to, so showing it elsewhere detaches it from there.
        VisualElement _shownAt;

        public Flyout()
        {
            _presenter = new Border { StyleClass = PRESENTER_CLASS };
            Child = _presenter;
            // Avalonia's flyout: above its target, flipped or slid to stay on screen, closed by a click outside.
            Placement = PlacementMode.Top;
            IsLightDismissEnabled = true;
        }

        /// <summary>What the flyout shows; the flyout sizes itself to it.</summary>
        public VisualElement Content
        {
            get { return GetValue(ContentProperty); }
            set { SetValue(ContentProperty, value); }
        }

        /// <summary>Style classes added to the presenter (space-separated), to style one flyout's surface.</summary>
        public string FlyoutPresenterClasses
        {
            get { return GetValue(FlyoutPresenterClassesProperty); }
            set { SetValue(FlyoutPresenterClassesProperty, value); }
        }

        /// <summary>The element the flyout is placed against while open (or was, last).</summary>
        public VisualElement Target
        {
            get { return PlacementTarget; }
        }

        /// <summary>Opens the flyout placed against <paramref name="placementTarget"/>, closing it elsewhere first.</summary>
        public void ShowAt(VisualElement placementTarget)
        {
            if (placementTarget == null)
                throw new ArgumentNullException(nameof(placementTarget));
            if (IsOpen && ReferenceEquals(PlacementTarget, placementTarget))
                return;
            if (IsOpen)
                Close();
            if (_shownAt != null && ReferenceEquals(_shownAt.AttachedFlyout, this))
                _shownAt.AttachedFlyout = null;
            _shownAt = placementTarget;
            placementTarget.AttachedFlyout = this;
            OverlayOwner = placementTarget;
            PlacementTarget = placementTarget;
            Open();
        }

        /// <summary>Closes the flyout.</summary>
        public void Hide()
        {
            Close();
        }

        protected override void OnOpening()
        {
            base.OnOpening();
            VisualElement target = PlacementTarget;
            if (target != null)
                InheritDataContext(target.DataContext);
        }

        protected override void OnPropertyChanged(IStyledProperty property)
        {
            base.OnPropertyChanged(property);
            if (property == ContentProperty)
                _presenter.Child = Content;
            else if (property == FlyoutPresenterClassesProperty)
                _presenter.StyleClass = string.IsNullOrWhiteSpace(FlyoutPresenterClasses)
                    ? PRESENTER_CLASS
                    : PRESENTER_CLASS + " " + FlyoutPresenterClasses.Trim();
        }
    }
}
