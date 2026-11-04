using System;
using Acrux.Core;
using Acrux.Core.Invalidation;
using Acrux.Core.PropertySystem;
using VRageMath;

namespace Acrux.Layout
{
    public abstract class Decorator : TemplatedControl
    {
        public static readonly StyledProperty<Vector4> PaddingProperty =
            StyledProperty.Register<Decorator, Vector4>(nameof(Padding), Vector4.Zero, UiInvalidation.Measure);

        VisualElement _child;

        public Vector4 Padding
        {
            get { return GetValue(PaddingProperty); }
            set { SetValue(PaddingProperty, value); }
        }

        public VisualElement Child
        {
            get { return _child; }
            set
            {
                VisualElement current = _child;
                if (ReferenceEquals(current, value))
                    return;
                if (current != null)
                    RemoveChild(current);
                if (value != null)
                {
                    _child = value;
                    AddChild(value);
                    if (TemplateRoot != null)
                        MoveChild(TemplateRoot, LogicalChildren.Count - 1);
                }
            }
        }

        /// <summary>Elements a subclass lays out alongside the content child, such as window decorations.</summary>
        protected virtual bool IsAuxiliaryChild(VisualElement child)
        {
            return false;
        }

        protected override void ValidateChildForAdd(VisualElement child)
        {
            if (IsApplyingTemplate || IsAuxiliaryChild(child))
            {
                base.ValidateChildForAdd(child);
                return;
            }
            VisualElement current = _child;
            if (current != null && !ReferenceEquals(current, child))
                throw new InvalidOperationException("A decorator can contain only one child.");
            if (current == null)
                _child = child;
        }

        public override bool RemoveChild(VisualElement child)
        {
            bool removed = base.RemoveChild(child);
            if (removed && ReferenceEquals(child, _child))
                _child = null;
            return removed;
        }

        public override void ClearChildren()
        {
            Child = null;
        }

        protected virtual Vector4 GetLayoutPadding()
        {
            return Padding;
        }

        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            Vector4 padding = GetLayoutPadding();
            float horizontal = Math.Max(0f, padding.X) + Math.Max(0f, padding.Z);
            float vertical = Math.Max(0f, padding.Y) + Math.Max(0f, padding.W);
            VisualElement child = Child;
            if (child == null || !child.Visible)
                return new Vector2(horizontal, vertical);

            Vector2 childAvailable = new Vector2(
                DeflateConstraint(availableSize.X, horizontal),
                DeflateConstraint(availableSize.Y, vertical));
            Vector2 childSize = child.Measure(childAvailable);
            return new Vector2(
                Math.Max(0f, childSize.X + horizontal),
                Math.Max(0f, childSize.Y + vertical));
        }

        protected override void ArrangeChildren(RectangleF bounds)
        {
            VisualElement child = Child;
            if (child != null && child.Visible)
            {
                Vector4 padding = GetLayoutPadding();
                float left = bounds.X + Math.Max(0f, padding.X);
                float top = bounds.Y + Math.Max(0f, padding.Y);
                float right = bounds.Right - Math.Max(0f, padding.Z);
                float bottom = bounds.Bottom - Math.Max(0f, padding.W);
                child.ArrangeInSlot(new RectangleF(
                    left,
                    top,
                    Math.Max(0f, right - left),
                    Math.Max(0f, bottom - top)));
            }
            ArrangeTemplate(bounds);
        }

        static float DeflateConstraint(float available, float amount)
        {
            if (float.IsPositiveInfinity(available))
                return available;
            return Math.Max(0f, available - amount);
        }
    }
}
