using System;
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
