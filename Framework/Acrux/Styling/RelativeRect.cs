using System;
using System.Globalization;
using VRageMath;

namespace Acrux.Styling
{
    /// <summary>RelativeUnit.</summary>
    public enum RelativeUnit
    {
        Relative,
        Absolute
    }

    /// <summary>RelativePoint: a point in absolute units or as a fraction of a target's bounds.</summary>
    public struct RelativePoint : IEquatable<RelativePoint>
    {
        public static readonly RelativePoint TopLeft = new RelativePoint(0f, 0f, RelativeUnit.Relative);
        public static readonly RelativePoint Center = new RelativePoint(.5f, .5f, RelativeUnit.Relative);
        public static readonly RelativePoint BottomRight = new RelativePoint(1f, 1f, RelativeUnit.Relative);

        public RelativePoint(float x, float y, RelativeUnit unit)
        {
            Point = new Vector2(x, y);
            Unit = unit;
        }

        public Vector2 Point { get; }
        public RelativeUnit Unit { get; }

        public Vector2 ToPixels(RectangleF bounds)
        {
            return Unit == RelativeUnit.Absolute
                ? bounds.Position + Point
                : bounds.Position + Point * bounds.Size;
        }

        public bool Equals(RelativePoint other)
        {
            return Point == other.Point && Unit == other.Unit;
        }

        public override bool Equals(object obj)
        {
            return obj is RelativePoint && Equals((RelativePoint)obj);
        }

        public override int GetHashCode()
        {
            return (Point.GetHashCode() * 397) ^ (int)Unit;
        }

        /// <summary>Parses "x,y" as layout units, or "x%,y%" as fractions of the bounds (both or neither in %).</summary>
        public static RelativePoint Parse(string value)
        {
            string[] parts = (value ?? string.Empty).Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2)
                throw new FormatException("Invalid RelativePoint '" + value + "'; expected \"x,y\" or \"x%,y%\".");
            bool xPercent = parts[0].EndsWith("%", StringComparison.Ordinal);
            bool yPercent = parts[1].EndsWith("%", StringComparison.Ordinal);
            if (xPercent != yPercent)
                throw new FormatException("Invalid RelativePoint '" + value + "'; give both coordinates in % or neither.");
            float x = float.Parse(parts[0].TrimEnd('%'), CultureInfo.InvariantCulture);
            float y = float.Parse(parts[1].TrimEnd('%'), CultureInfo.InvariantCulture);
            return xPercent
                ? new RelativePoint(x / 100f, y / 100f, RelativeUnit.Relative)
                : new RelativePoint(x, y, RelativeUnit.Absolute);
        }
    }

    /// <summary>RelativeScalar: a length in absolute units or as a fraction of a target length.</summary>
    public struct RelativeScalar : IEquatable<RelativeScalar>
    {
        public static readonly RelativeScalar Middle = new RelativeScalar(.5f, RelativeUnit.Relative);

        public RelativeScalar(float scalar, RelativeUnit unit)
        {
            Scalar = scalar;
            Unit = unit;
        }

        public float Scalar { get; }
        public RelativeUnit Unit { get; }

        public float ToValue(float size)
        {
            return Unit == RelativeUnit.Absolute ? Scalar : Scalar * size;
        }

        /// <summary>Parses "x" as layout units, or "x%" as a fraction of the target length.</summary>
        public static RelativeScalar Parse(string value)
        {
            string text = (value ?? string.Empty).Trim();
            if (text.EndsWith("%", StringComparison.Ordinal))
                return new RelativeScalar(
                    float.Parse(text.Substring(0, text.Length - 1), CultureInfo.InvariantCulture) / 100f,
                    RelativeUnit.Relative);
            return new RelativeScalar(float.Parse(text, CultureInfo.InvariantCulture), RelativeUnit.Absolute);
        }

        public bool Equals(RelativeScalar other)
        {
            return Scalar.Equals(other.Scalar) && Unit == other.Unit;
        }

        public override bool Equals(object obj)
        {
            return obj is RelativeScalar && Equals((RelativeScalar)obj);
        }

        public override int GetHashCode()
        {
            return (Scalar.GetHashCode() * 397) ^ (int)Unit;
        }
    }

    /// <summary>RelativeRect: a rectangle in absolute units or as fractions of a target's bounds.</summary>
    public struct RelativeRect : IEquatable<RelativeRect>
    {
        public static readonly RelativeRect Fill = new RelativeRect(0f, 0f, 1f, 1f, RelativeUnit.Relative);

        public RelativeRect(float x, float y, float width, float height, RelativeUnit unit)
        {
            Rect = new RectangleF(x, y, width, height);
            Unit = unit;
        }

        public RectangleF Rect { get; }
        public RelativeUnit Unit { get; }

        public RectangleF ToPixels(RectangleF bounds)
        {
            return Unit == RelativeUnit.Absolute
                ? new RectangleF(bounds.Position + Rect.Position, Rect.Size)
                : new RectangleF(bounds.Position + Rect.Position * bounds.Size, Rect.Size * bounds.Size);
        }

        public bool Equals(RelativeRect other)
        {
            return Rect == other.Rect && Unit == other.Unit;
        }

        public override bool Equals(object obj)
        {
            return obj is RelativeRect && Equals((RelativeRect)obj);
        }

        public override int GetHashCode()
        {
            return (Rect.GetHashCode() * 397) ^ (int)Unit;
        }
    }
}
