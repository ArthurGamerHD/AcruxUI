using System;
using System.Globalization;
using Acrux.Styling;
using VRageMath;

namespace AcruxPreview
{
    /// <summary>
    /// Design-time stand-in for a mod's theme (Arthur's LCD Mod provides these from its app at run time), derived
    /// from the preview's background, foreground and seed (accent) colours.
    /// </summary>
    public sealed class DesignResources : IResourceProvider
    {
        readonly Color _background;
        readonly Color _foreground;
        readonly Color _seed;
        readonly string _font;
        readonly float _fontScale;

        public DesignResources(PreviewSettingsMessage settings)
        {
            settings = settings ?? new PreviewSettingsMessage();
            _background = Parse(settings.Background, new Color(0, 88, 151));
            _foreground = Parse(settings.Foreground, new Color(197, 237, 255));
            _seed = Parse(settings.Seed, new Color(58, 32, 63));
            _font = string.IsNullOrEmpty(settings.FontId) ? "White" : settings.FontId;
            _fontScale = (float)(settings.FontScale > 0 ? settings.FontScale : 0.7);
        }

        public Color Background => _background;

        public event Action<string> ResourceChanged
        {
            add { }
            remove { }
        }

        public bool TryGetResource<T>(string key, out T value)
        {
            object result = Lookup(key ?? string.Empty, typeof(T));
            if (result is T)
            {
                value = (T)result;
                return true;
            }
            value = default(T);
            return false;
        }

        object Lookup(string key, Type type)
        {
            string k = key.ToLowerInvariant();
            if (type == typeof(Color))
            {
                if (k.Contains("error")) return new Color(0xE0, 0x60, 0x60);
                bool on = k.StartsWith("on");
                if (k.Contains("accent") || k.Contains("primary"))
                {
                    if (on) return k.Contains("container") ? Mix(_foreground, _seed, 0.15f) : Mix(_background, _seed, 0.1f);
                    return k.Contains("container") ? Mix(_background, _seed, 0.45f) : _seed;
                }
                if (on) return k.Contains("variant") ? Mix(_foreground, _background, 0.3f) : _foreground;
                if (k.Contains("highest")) return Surface(0.22f);
                if (k.Contains("high")) return Surface(0.16f);
                if (k.Contains("low")) return Surface(0.06f);
                if (k.Contains("border") || k.Contains("outline")) return Mix(_background, _foreground, 0.3f);
                if (k.Contains("disabled")) return Mix(_background, _foreground, 0.35f);
                if (k.Contains("secondary")) return Surface(0.13f);
                if (k.Contains("surface") || k.Contains("container") || k.Contains("background")) return Surface(0.1f);
                return Mix(_foreground, _background, 0.15f);
            }
            if (type == typeof(float))
            {
                if (k.Contains("font") || k.Contains("scale")) return _fontScale;
                if (k.Contains("height")) return 48f;
                if (k.Contains("width")) return 120f;
                return 1f;
            }
            if (type == typeof(string))
                return k.Contains("font") ? _font : string.Empty;
            return null;
        }

        // Surfaces are the background tinted slightly towards the foreground and the seed.
        Color Surface(float amount)
        {
            return Mix(Mix(_background, _foreground, amount), _seed, amount * 0.25f);
        }

        static Color Mix(Color a, Color b, float t)
        {
            return new Color(
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        static Color Parse(string hex, Color fallback)
        {
            string text = (hex ?? string.Empty).Trim().TrimStart('#');
            int rgb;
            if (text.Length != 6 || !int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb))
                return fallback;
            return new Color((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        }
    }
}
