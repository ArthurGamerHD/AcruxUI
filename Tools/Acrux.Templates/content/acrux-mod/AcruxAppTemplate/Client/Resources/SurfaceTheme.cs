using System;
using Acrux.Styling;
using AcruxAppTemplate.Client.ViewModel;
using VRageMath;

namespace AcruxAppTemplate.Client.Resources
{
    /// <summary>
    /// The {DynamicResource} keys the toolkit's Fluent theme asks for, derived from the surface's own colours.
    /// Resources resolve through the data context, so the root view model exposes this (see <see cref="ViewModel.CounterViewModel"/>).
    /// </summary>
    public sealed class SurfaceTheme : IResourceProvider
    {
        Color _background;
        Color _foreground;

        public SurfaceTheme(Color background, Color foreground)
        {
            _background = background;
            _foreground = foreground;
        }

        public event Action<string> ResourceChanged;

        public string Font { get; set; } = "White";

        /// <summary>Follows the block's script colours; call when they change.</summary>
        public void SetColors(Color background, Color foreground)
        {
            if (background == _background && foreground == _foreground)
                return;
            _background = background;
            _foreground = foreground;
            var changed = ResourceChanged;
            if (changed != null)
                changed(null); // null: every key may have changed
        }

        public bool TryGetResource<T>(string key, out T value)
        {
            if (typeof(T) == typeof(string) && key == "textFont")
            {
                value = (T)(object)Font;
                return true;
            }

            Color color;
            if ((typeof(T) == typeof(Color) || typeof(T) == typeof(object)) && TryGetColor(key, out color))
            {
                value = (T)(object)color;
                return true;
            }

            value = default(T);
            return false;
        }

        bool TryGetColor(string key, out Color color)
        {
            switch (key)
            {
                // The surface paints the background itself; controls sit a step above it so they stand out.
                case "surfaceColor":
                case "controlBackgroundColor":
                    color = Mix(_background, _foreground, 0.12f);
                    return true;
                case "surfaceContainerLowColor":
                    color = Mix(_background, _foreground, 0.06f);
                    return true;
                case "surfaceContainerHighColor":
                    color = Mix(_background, _foreground, 0.2f);
                    return true;
                case "surfaceContainerHighestColor":
                    color = Mix(_background, _foreground, 0.28f);
                    return true;
                case "fontColor":
                case "onSurfaceColor":
                    color = _foreground;
                    return true;
                case "onSurfaceVariantColor":
                    color = Mix(_foreground, _background, 0.3f);
                    return true;
                case "borderColor":
                case "borderVariantColor":
                    color = Mix(_background, _foreground, 0.4f);
                    return true;
                case "disabledColor":
                    color = Mix(_background, _foreground, 0.35f);
                    return true;
                case "accentColor":
                    color = _foreground;
                    return true;
                case "onAccentColor":
                    color = _background;
                    return true;
                case "accentContainerColor":
                    color = Mix(_background, _foreground, 0.3f);
                    return true;
                case "errorColor":
                    color = new Color(220, 70, 60);
                    return true;
            }
            color = default(Color);
            return false;
        }

        static Color Mix(Color from, Color to, float amount)
        {
            return Color.Lerp(from, to, amount);
        }
    }
}
