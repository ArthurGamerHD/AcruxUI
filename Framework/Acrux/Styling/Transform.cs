using System;
using System.Globalization;
using VRageMath;

namespace Acrux.Styling
{
    /// <summary>
    /// Media.Transform subset. LCD sprites can only rotate whole images, so render
    /// transforms are honoured by Image and by OpacityMask-tinted Border; ImageBrush honours scales.
    /// </summary>
    public abstract class Transform
    {
        public abstract float RotationRadians { get; }
        internal virtual Vector2 ScaleFactor => Vector2.One;
        internal virtual Vector2 Offset => Vector2.Zero;

        /// <summary>Parses transform syntax, e.g. "rotate(180deg)", "scale(0.95)" or "translateY(12px)".</summary>
        public static Transform Parse(string value)
        {
            string text = (value ?? string.Empty).Trim();
            const string Rotate = "rotate(";
            if (text.StartsWith(Rotate, StringComparison.OrdinalIgnoreCase) && text.EndsWith(")", StringComparison.Ordinal))
            {
                string angle = text.Substring(Rotate.Length, text.Length - Rotate.Length - 1).Trim();
                float degrees;
                if (angle.EndsWith("deg", StringComparison.OrdinalIgnoreCase))
                    degrees = float.Parse(angle.Substring(0, angle.Length - 3), CultureInfo.InvariantCulture);
                else if (angle.EndsWith("rad", StringComparison.OrdinalIgnoreCase))
                    degrees = (float)(float.Parse(angle.Substring(0, angle.Length - 3), CultureInfo.InvariantCulture) * 180d / Math.PI);
                else
                    degrees = float.Parse(angle, CultureInfo.InvariantCulture);
                return new RotateTransform(degrees);
            }
            const string Scale = "scale(";
            if (text.StartsWith(Scale, StringComparison.OrdinalIgnoreCase) && text.EndsWith(")", StringComparison.Ordinal))
            {
                string[] factors = text.Substring(Scale.Length, text.Length - Scale.Length - 1)
                    .Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (factors.Length == 1 || factors.Length == 2)
                {
                    float x = float.Parse(factors[0], CultureInfo.InvariantCulture);
                    float y = factors.Length == 2 ? float.Parse(factors[1], CultureInfo.InvariantCulture) : x;
                    return new ScaleTransform(x, y);
                }
            }
            TranslateTransform translate;
            if (TryParseTranslate(text, out translate))
                return translate;
            throw new FormatException("Unsupported transform '" + value + "'. Only rotate(...), scale(...) and translate(...) are supported.");
        }

        static bool TryParseTranslate(string text, out TranslateTransform transform)
        {
            transform = null;
            string function;
            if (text.StartsWith("translateX(", StringComparison.OrdinalIgnoreCase))
                function = "translateX(";
            else if (text.StartsWith("translateY(", StringComparison.OrdinalIgnoreCase))
                function = "translateY(";
            else if (text.StartsWith("translate(", StringComparison.OrdinalIgnoreCase))
                function = "translate(";
            else
                return false;
            if (!text.EndsWith(")", StringComparison.Ordinal))
                return false;

            string[] parts = text.Substring(function.Length, text.Length - function.Length - 1)
                .Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            bool pair = function == "translate(";
            if (parts.Length < 1 || parts.Length > (pair ? 2 : 1))
                return false;
            float first = ParsePixels(parts[0]);
            float second = parts.Length == 2 ? ParsePixels(parts[1]) : 0f;
            if (function == "translateY(")
                transform = new TranslateTransform(0f, first);
            else
                transform = new TranslateTransform(first, second);
            return true;
        }

        // Like TransformParser, lengths need a px unit unless they are zero.
        static float ParsePixels(string text)
        {
            float value;
            if (text.EndsWith("px", StringComparison.OrdinalIgnoreCase))
                return float.Parse(text.Substring(0, text.Length - 2), CultureInfo.InvariantCulture);
            if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value == 0f)
                return 0f;
            throw new FormatException("Translate lengths need a px unit: '" + text + "'.");
        }
    }

    /// <summary>RotateTransform; Angle is in degrees.</summary>
    public sealed class RotateTransform : Transform
    {
        public RotateTransform()
        {
        }

        public RotateTransform(float angle)
        {
            Angle = angle;
        }

        public float Angle { get; set; }
        public override float RotationRadians => (float)(Angle * Math.PI / 180d);

        public override bool Equals(object obj)
        {
            var other = obj as RotateTransform;
            return other != null && other.Angle == Angle;
        }

        public override int GetHashCode()
        {
            return Angle.GetHashCode();
        }
    }

    /// <summary>Media.ScaleTransform.</summary>
    public sealed class ScaleTransform : Transform
    {
        public ScaleTransform()
        {
        }

        public ScaleTransform(float scaleX, float scaleY)
        {
            ScaleX = scaleX;
            ScaleY = scaleY;
        }

        public float ScaleX { get; set; } = 1f;
        public float ScaleY { get; set; } = 1f;
        public override float RotationRadians => 0f;
        internal override Vector2 ScaleFactor => new Vector2(ScaleX, ScaleY);

        public override bool Equals(object obj)
        {
            var other = obj as ScaleTransform;
            return other != null && other.ScaleX == ScaleX && other.ScaleY == ScaleY;
        }

        public override int GetHashCode()
        {
            return (ScaleX.GetHashCode() * 397) ^ ScaleY.GetHashCode();
        }
    }

    /// <summary>Media.TranslateTransform: moves the rendered element by X, Y without changing its layout.</summary>
    public sealed class TranslateTransform : Transform
    {
        public TranslateTransform()
        {
        }

        public TranslateTransform(float x, float y)
        {
            X = x;
            Y = y;
        }

        public float X { get; set; }
        public float Y { get; set; }
        public override float RotationRadians => 0f;
        internal override Vector2 Offset => new Vector2(X, Y);

        public override bool Equals(object obj)
        {
            var other = obj as TranslateTransform;
            return other != null && other.X == X && other.Y == Y;
        }

        public override int GetHashCode()
        {
            return (X.GetHashCode() * 397) ^ Y.GetHashCode();
        }
    }
}
