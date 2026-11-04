using System;
using Acrux.Controls;
using Acrux.Core;
using Acrux.Core.PropertySystem;
using Acrux.Interactivity;

namespace Acrux.Extensions
{
    /// <summary>An effect played when a button is clicked, such as a sound on the surface that shows it.</summary>
    public interface IClickEffect
    {
        void Play(Button button);
    }

    /// <summary>
    /// Acrux extension that hooks <see cref="Button.ClickEvent"/> and plays the <see cref="IClickEffect"/> found in the
    /// clicked button's resources under <see cref="ResourceKey"/>, so each window tree can supply its own effect.
    /// Styles can turn it off per button with the ClickEffect.IsEnabled attached property.
    /// </summary>
    public static class ClickEffect
    {
        public const string ResourceKey = "ClickEffect";

        public static readonly StyledProperty<bool> IsEnabledProperty =
            StyledProperty.RegisterAttached<Button, bool>(typeof(ClickEffect), "IsEnabled", true);

        static IDisposable _subscription;

        public static bool GetIsEnabled(Button button) => button.GetValue(IsEnabledProperty);
        public static void SetIsEnabled(Button button, bool value) => button.SetValue(IsEnabledProperty, value);

        /// <summary>Starts playing click effects; calling it again has no effect.</summary>
        public static void Install()
        {
            if (_subscription == null)
                _subscription = Button.ClickEvent.AddClassHandler<Button>(OnClick, handledEventsToo: true);
        }

        public static void Uninstall()
        {
            if (_subscription == null)
                return;
            _subscription.Dispose();
            _subscription = null;
        }

        static void OnClick(Button button, RoutedEventArgs e)
        {
            if (!GetIsEnabled(button))
                return;
            IClickEffect effect;
            if (button.TryFindResource(ResourceKey, out effect) && effect != null)
                effect.Play(button);
        }
    }
}
