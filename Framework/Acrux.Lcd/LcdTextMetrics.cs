using System;
using System.Collections.Generic;
using System.Text;
using Acrux.Rendering;
using VRageMath;
using IMyTextSurface = Sandbox.ModAPI.Ingame.IMyTextSurface;

namespace Acrux.Lcd
{
    /// <summary>
    /// Measures Acrux text with the exact LCD surface font metrics used by MySprite.
    /// </summary>
    public sealed class LcdTextMetrics : ITextMetrics
    {
        readonly IMyTextSurface _surface;
        readonly StringBuilder _buffer = new StringBuilder();

        public LcdTextMetrics(IMyTextSurface surface)
        {
            if (surface == null)
                throw new ArgumentNullException(nameof(surface));
            _surface = surface;
        }

        public float ScaleMultiplier { get; set; } = 1f;

        public Vector2 Measure(string font, string text, float scale)
        {
            string resolvedFont = string.IsNullOrWhiteSpace(font) ? "White" : font;
            string resolvedText = string.IsNullOrEmpty(text) ? "M" : text;
            float resolvedScale = Math.Max(0.001f, scale * ScaleMultiplier);
            var key = new MeasureKey(resolvedFont, resolvedText, resolvedScale);
            Vector2 size;
            if (_cache.TryGetValue(key, out size))
                return size;

            _buffer.Clear();
            _buffer.Append(resolvedText);
            size = _surface.MeasureStringInPixels(_buffer, resolvedFont, resolvedScale);
            if (_cache.Count >= MaxCachedMeasurements)
                _cache.Clear();
            _cache[key] = size;
            return size;
        }

        const int MaxCachedMeasurements = 1024;
        readonly Dictionary<MeasureKey, Vector2> _cache = new Dictionary<MeasureKey, Vector2>();

        struct MeasureKey : IEquatable<MeasureKey>
        {
            readonly string _font;
            readonly string _text;
            readonly float _scale;

            public MeasureKey(string font, string text, float scale)
            {
                _font = font;
                _text = text;
                _scale = scale;
            }

            public bool Equals(MeasureKey other)
            {
                return _scale == other._scale && string.Equals(_text, other._text, StringComparison.Ordinal) &&
                       string.Equals(_font, other._font, StringComparison.Ordinal);
            }

            public override bool Equals(object obj)
            {
                return obj is MeasureKey && Equals((MeasureKey)obj);
            }

            public override int GetHashCode()
            {
                return ((_text.GetHashCode() * 397) ^ _font.GetHashCode()) * 397 ^ _scale.GetHashCode();
            }
        }
    }
}
