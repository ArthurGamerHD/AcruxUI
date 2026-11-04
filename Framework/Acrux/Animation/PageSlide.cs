using System;

namespace Acrux.Animation
{
    /// <summary>Animation.PageSlide.SlideAxis.</summary>
    public enum SlideAxis
    {
        Horizontal,
        Vertical
    }

    /// <summary>
    /// PageSlide transition: slides pages along an axis over <see cref="Duration"/>.
    /// </summary>
    public sealed class PageSlide : IEquatable<PageSlide>
    {
        const double FramesPerSecond = 60d;

        public PageSlide()
        {
        }

        public PageSlide(TimeSpan duration, SlideAxis orientation = SlideAxis.Horizontal)
        {
            Duration = duration;
            Orientation = orientation;
        }

        public TimeSpan Duration { get; set; }
        public SlideAxis Orientation { get; set; }

        internal int Frames => Math.Max(0, (int)Math.Round(Duration.TotalSeconds * FramesPerSecond));

        public bool Equals(PageSlide other)
        {
            return !ReferenceEquals(other, null) && Duration == other.Duration && Orientation == other.Orientation;
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as PageSlide);
        }

        public override int GetHashCode()
        {
            return (Duration.GetHashCode() * 397) ^ (int)Orientation;
        }
    }
}
