using System;
using Acrux.Core;
using Acrux.Core.Invalidation;
using Acrux.Core.PropertySystem;
using Acrux.Rendering;
using Acrux.Styling;
using VRageMath;

namespace Acrux.Styling
{
    /// <summary>Media.StretchDirection.</summary>
    public enum StretchDirection
    {
        UpOnly,
        DownOnly,
        Both
    }
}

namespace Acrux.Layout
{
    /// <summary>
    /// Port of Viewbox: measures its child without constraints and scales it to the available
    /// space. LCD rendering only supports a uniform scale, so <see cref="Stretch.Fill"/> behaves as
    /// <see cref="Stretch.Uniform"/>.
    /// </summary>
    public sealed class Viewbox : Decorator
    {
        public static readonly StyledProperty<Stretch> StretchProperty =
            StyledProperty.Register<Viewbox, Stretch>(nameof(Stretch), Stretch.Uniform, UiInvalidation.Measure);

        public static readonly StyledProperty<StretchDirection> StretchDirectionProperty =
            StyledProperty.Register<Viewbox, StretchDirection>(nameof(StretchDirection), StretchDirection.Both,
                UiInvalidation.Measure);

        float _scale = 1f;
        Vector2 _origin;

        public Viewbox()
        {
            ClipToBounds = true;
        }

        public Stretch Stretch
        {
            get { return GetValue(StretchProperty); }
            set { SetValue(StretchProperty, value); }
        }

        public StretchDirection StretchDirection
        {
            get { return GetValue(StretchDirectionProperty); }
            set { SetValue(StretchDirectionProperty, value); }
        }

        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            VisualElement child = Child;
            if (child == null || !child.Visible)
                return Vector2.Zero;
            Vector2 childSize = child.Measure(new Vector2(float.PositiveInfinity, float.PositiveInfinity));
            Vector2 scale = CalculateScaling(availableSize, childSize);
            return new Vector2(childSize.X * scale.X, childSize.Y * scale.Y);
        }

        protected override void ArrangeChildren(RectangleF bounds)
        {
            VisualElement child = Child;
            if (child == null || !child.Visible)
                return;
            Vector2 childSize = child.DesiredSize;
            _scale = CalculateScaling(bounds.Size, childSize).X;
            _origin = bounds.Position;
            child.ArrangeInSlot(new RectangleF(bounds.Position, childSize), childSize);
        }

        protected override IDisposable BeginChildDraw(RenderContext context, VisualElement child)
        {
            return ReferenceEquals(child, Child) && Math.Abs(_scale - 1f) > 0.0001f
                ? context.PushScale(_origin, _scale)
                : null;
        }

        internal override Vector2 MapPointToChildren(Vector2 point)
        {
            return _scale > 0f ? _origin + (point - _origin) / _scale : point;
        }

        Vector2 CalculateScaling(Vector2 destination, Vector2 source)
        {
            Stretch stretch = Stretch;
            if (stretch == Stretch.None)
                return Vector2.One;
            bool constrainedWidth = !float.IsPositiveInfinity(destination.X);
            bool constrainedHeight = !float.IsPositiveInfinity(destination.Y);
            if (!constrainedWidth && !constrainedHeight)
                return Vector2.One;

            float scaleX = source.X <= 0f ? 0f : destination.X / source.X;
            float scaleY = source.Y <= 0f ? 0f : destination.Y / source.Y;
            float scale;
            if (!constrainedWidth)
                scale = scaleY;
            else if (!constrainedHeight)
                scale = scaleX;
            else
                scale = stretch == Stretch.UniformToFill ? Math.Max(scaleX, scaleY) : Math.Min(scaleX, scaleY);

            switch (StretchDirection)
            {
                case StretchDirection.UpOnly:
                    scale = Math.Max(1f, scale);
                    break;
                case StretchDirection.DownOnly:
                    scale = Math.Min(1f, scale);
                    break;
            }
            return new Vector2(scale);
        }
    }
}
