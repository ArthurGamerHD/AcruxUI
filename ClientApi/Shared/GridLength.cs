using System;

namespace Acrux.Layout
{
    public enum GridUnitType
    {
        Auto,
        Pixel,
        Star
    }

    /// <summary>GridLength: an Auto, pixel or star size of a grid row or column.</summary>
    public struct GridLength
    {
        public GridLength(float value)
            : this(value, GridUnitType.Pixel)
        {
        }

        public GridLength(float value, GridUnitType type)
        {
            if (float.IsNaN(value) || value < 0f)
                throw new ArgumentException("Grid length cannot be negative or NaN.", nameof(value));
            Value = value;
            GridUnitType = type;
        }

        public float Value { get; private set; }
        public GridUnitType GridUnitType { get; private set; }
        public bool IsAuto => GridUnitType == GridUnitType.Auto;
        public bool IsAbsolute => GridUnitType == GridUnitType.Pixel;
        public bool IsStar => GridUnitType == GridUnitType.Star;

        public static GridLength Auto => new GridLength(0f, GridUnitType.Auto);
        public static GridLength Star => new GridLength(1f, GridUnitType.Star);
    }
}
