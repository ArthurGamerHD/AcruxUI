using System;
using System.Globalization;
using Acrux.Styling;
using VRageMath;

namespace Acrux.Markup
{
    public sealed class PropertyConverter
    {
        public object Convert(string value, Type targetType)
        {
            if (targetType == null)
                throw new ArgumentNullException(nameof(targetType));
            if (targetType == typeof(bool?))
            {
                if (string.IsNullOrWhiteSpace(value) ||
                    string.Equals(value.Trim(), "null", StringComparison.OrdinalIgnoreCase))
                    return null;
                return (bool?)bool.Parse(value);
            }
            if (targetType == typeof(RectangleF?))
            {
                if (string.IsNullOrWhiteSpace(value) ||
                    string.Equals(value.Trim(), "null", StringComparison.OrdinalIgnoreCase))
                    return null;
                return (RectangleF?)ParseRectangle(value);
            }
            if (targetType == typeof(string))
                return value;
            if (targetType == typeof(bool))
                return bool.Parse(value);
            if (targetType == typeof(int))
                return int.Parse(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(float))
                return float.Parse(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(double))
                return double.Parse(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(TimeSpan))
                return TimeSpan.Parse(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(Color))
                return ParseColor(value);
            if (targetType == typeof(BackgroundBrush))
                return new SolidColorBrush(ParseColor(value));
            if (targetType == typeof(Vector2))
                return ParseVector2(value);
            if (targetType == typeof(Vector4))
                return ParseVector4(value);
            if (targetType == typeof(RectangleF))
                return ParseRectangle(value);
            if (targetType == typeof(Acrux.Styling.Transform))
                return Acrux.Styling.Transform.Parse(value);
            try
            {
                return Enum.Parse(targetType, value, true);
            }
            catch (ArgumentException)
            {
                throw new NotSupportedException(
                    $"No XML conversion is registered for value '{value}' on type '{targetType.FullName}'.");
            }
        }

        static RectangleF ParseRectangle(string value)
        {
            float[] values = ParseFloats(value, 4);
            return new RectangleF(values[0], values[1], values[2], values[3]);
        }


        static Vector2 ParseVector2(string value)
        {
            float[] values = ParseFloats(value, 1, 2);
            if (values.Length == 1)
                return new Vector2(values[0], values[0]);
            return new Vector2(values[0], values[1]);
        }

        static Vector4 ParseVector4(string value)
        {
            float[] values = ParseFloats(value, 1, 4);
            if (values.Length == 1)
                return new Vector4(values[0]);
            if (values.Length == 2)
                return new Vector4(values[0], values[1], values[0], values[1]);
            if (values.Length != 4)
                throw new FormatException(
                    "Expected 1, 2, or 4 components in '" + value + "'.");
            return new Vector4(values[0], values[1], values[2], values[3]);
        }

        static Color ParseColor(string value)
        {
            uint argb;
            if (KnownColors.TryGetArgb(value, out argb))
                return FromArgb(argb);

            string text = value == null ? string.Empty : value.Trim();
            if (text.Length > 0 && text[0] == '#')
            {
                string hex = text.Substring(1);
                uint parsed;
                if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out parsed))
                    throw new FormatException("Invalid color '" + value + "'.");
                switch (hex.Length)
                {
                    case 3:
                        return new Color(Nibble(parsed >> 8), Nibble(parsed >> 4), Nibble(parsed));
                    case 4:
                        return new Color(Nibble(parsed >> 8), Nibble(parsed >> 4), Nibble(parsed), Nibble(parsed >> 12));
                    case 6:
                        return FromArgb(0xff000000u | parsed);
                    case 8:
                        return FromArgb(parsed);
                }
                throw new FormatException("Invalid color '" + value + "'.");
            }

            float[] components = ParseFloats(value, 3, 4);
            return new Color(
                (byte)components[0],
                (byte)components[1],
                (byte)components[2],
                components.Length == 4 ? (byte)components[3] : (byte)255);
        }

        static Color FromArgb(uint argb)
        {
            return new Color((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb, (byte)(argb >> 24));
        }

        static byte Nibble(uint value)
        {
            return (byte)((value & 0x0F) * 17);
        }

        static readonly char[] ComponentSeparators = { ',', ' ', '\t', '\r', '\n' };

        static float[] ParseFloats(string value, int requiredCount)
        {
            return ParseFloats(value, requiredCount, requiredCount);
        }

        static float[] ParseFloats(string value, int minimumCount, int maximumCount)
        {
            string[] parts = (value ?? string.Empty).Split(ComponentSeparators, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < minimumCount || parts.Length > maximumCount)
                throw new FormatException("Unexpected component count in '" + value + "'.");
            var result = new float[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                result[i] = float.Parse(parts[i].Trim(), CultureInfo.InvariantCulture);
            return result;
        }
    }
}
