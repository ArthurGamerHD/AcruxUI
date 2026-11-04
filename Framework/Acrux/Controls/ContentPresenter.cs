using System;
using Acrux.Core;
using Acrux.Core.Invalidation;
using Acrux.Core.PropertySystem;
using Acrux.Layout;
using Acrux.Rendering;
using VRageMath;

namespace Acrux.Controls
{
    public sealed class ContentPresenter : Decorator
    {
        public static readonly StyledProperty<HorizontalAlignment> HorizontalContentAlignmentProperty =
            StyledProperty.Register<ContentPresenter, HorizontalAlignment>(
                nameof(HorizontalContentAlignment), HorizontalAlignment.Stretch, UiInvalidation.Arrange);
        public static readonly StyledProperty<VerticalAlignment> VerticalContentAlignmentProperty =
            StyledProperty.Register<ContentPresenter, VerticalAlignment>(
                nameof(VerticalContentAlignment), VerticalAlignment.Stretch, UiInvalidation.Arrange);

        public Acrux.Core.VisualElement Content
        {
            get { return Child; }
            set { Child = value; }
        }

        public HorizontalAlignment HorizontalContentAlignment
        {
            get { return GetValue(HorizontalContentAlignmentProperty); }
            set { SetValue(HorizontalContentAlignmentProperty, value); }
        }

        public VerticalAlignment VerticalContentAlignment
        {
            get { return GetValue(VerticalContentAlignmentProperty); }
            set { SetValue(VerticalContentAlignmentProperty, value); }
        }

        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            Vector2 size = base.MeasureOverride(availableSize);
            var button = TemplatedParent as Button;
            if (button == null || Child != null)
                return size;
            Vector4 padding = Padding;
            return Vector2.Max(size, button.MeasureDrawnContent() +
                new Vector2(Math.Max(0f, padding.X) + Math.Max(0f, padding.Z), Math.Max(0f, padding.Y) + Math.Max(0f, padding.W)));
        }

        protected override void ArrangeChildren(RectangleF bounds)
        {
            Acrux.Core.VisualElement child = Child;
            if (child == null || !child.Visible)
            {
                base.ArrangeChildren(bounds);
                return;
            }
            Vector4 padding = Padding;
            float left = bounds.X + Math.Max(0f, padding.X);
            float top = bounds.Y + Math.Max(0f, padding.Y);
            float width = Math.Max(0f, bounds.Right - Math.Max(0f, padding.Z) - left);
            float height = Math.Max(0f, bounds.Bottom - Math.Max(0f, padding.W) - top);
            float childWidth = HorizontalContentAlignment == HorizontalAlignment.Stretch
                ? width
                : Math.Min(width, child.DesiredSize.X);
            float childHeight = VerticalContentAlignment == VerticalAlignment.Stretch
                ? height
                : Math.Min(height, child.DesiredSize.Y);
            float x = HorizontalContentAlignment == HorizontalAlignment.Center ? left + (width - childWidth) * 0.5f
                : HorizontalContentAlignment == HorizontalAlignment.Right ? left + width - childWidth
                : left;
            float y = VerticalContentAlignment == VerticalAlignment.Center ? top + (height - childHeight) * 0.5f
                : VerticalContentAlignment == VerticalAlignment.Bottom ? top + height - childHeight
                : top;
            child.ArrangeInSlot(new RectangleF(x, y, childWidth, childHeight));
            ArrangeTemplate(bounds);
        }

        protected override void DrawSelf(RenderContext context)
        {
            var button = TemplatedParent as Button;
            if (button != null)
                button.DrawPresenterContent(context, Bounds);
        }
    }
}
