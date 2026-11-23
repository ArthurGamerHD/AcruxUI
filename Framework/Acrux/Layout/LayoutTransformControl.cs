using System;
using Acrux.Core;
using Acrux.Core.Invalidation;
using Acrux.Core.PropertySystem;
using Acrux.Rendering;
using Acrux.Styling;
using VRageMath;

namespace Acrux.Layout
{
    /// <summary>
    /// Port of LayoutTransformControl: applies <see cref="LayoutTransform"/> to its child's layout as well as to its
    /// rendering, so the child is laid out for the scaled size and input reaches it in its own units. LCD rendering only
    /// supports a uniform scale, so the smaller scale factor is used and rotations are ignored.
    /// </summary>
    public sealed class LayoutTransformControl : Decorator
    {
        public static readonly StyledProperty<Transform> LayoutTransformProperty =
            StyledProperty.Register<LayoutTransformControl, Transform>(nameof(LayoutTransform), null,
                UiInvalidation.Measure);

        Vector2 _origin;

        public Transform LayoutTransform
        {
            get { return GetValue(LayoutTransformProperty); }
            set { SetValue(LayoutTransformProperty, value); }
        }

        float Scale
        {
            get
            {
                Transform transform = LayoutTransform;
                if (transform == null)
                    return 1f;
                Vector2 factor = transform.ScaleFactor;
                float scale = Math.Min(factor.X, factor.Y);
                return scale > 0.0001f && !float.IsNaN(scale) && !float.IsInfinity(scale) ? scale : 1f;
            }
        }

        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            VisualElement child = Child;
            if (child == null || !child.Visible)
                return Vector2.Zero;
            float scale = Scale;
            Vector2 childSize = child.Measure(new Vector2(availableSize.X / scale, availableSize.Y / scale));
            return childSize * scale;
        }

        protected override void ArrangeChildren(RectangleF bounds)
        {
            VisualElement child = Child;
            if (child == null || !child.Visible)
                return;
            float scale = Scale;
            _origin = bounds.Position;
            Vector2 size = bounds.Size / scale;
            child.ArrangeInSlot(new RectangleF(bounds.Position, size));
        }

        protected override IDisposable BeginChildDraw(RenderContext context, VisualElement child)
        {
            return ReferenceEquals(child, Child) ? PushChildTransform(context) : null;
        }

        internal override IDisposable PushChildTransform(RenderContext context)
        {
            float scale = Scale;
            return Math.Abs(scale - 1f) > 0.0001f ? context.PushScale(_origin, scale) : null;
        }

        internal override Vector2 MapPointToChildren(Vector2 point)
        {
            return _origin + (point - _origin) / Scale;
        }
    }
}
