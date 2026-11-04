using System;
using Acrux.Controls;
using Acrux.Core;
using Acrux.Extensions;
using Acrux.Rendering;
using VRageMath;

namespace Acrux.Layout
{
    /// <summary>
    /// VirtualizingStackPanel as an ItemsControl items panel: stacks items vertically and only
    /// realizes the ones inside the enclosing ScrollViewer's viewport. Items share the height of the first one.
    /// </summary>
    public sealed class VirtualizingStackPanel : Panel, IVirtualizingItemsPanel
    {
        const int Buffer = 2;

        float _itemHeight;

        ItemsControl Owner => Parent as ItemsControl;

        protected override void DrawSelf(RenderContext context)
        {
            DrawBackground(context);
        }

        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            ItemsControl owner = Owner;
            int count = owner == null ? 0 : owner.ItemCount;
            if (count == 0)
            {
                _itemHeight = 0f;
                return Vector2.Zero;
            }

            VisualElement sample = owner.RealizeContainer(0);
            Vector2 desired = sample.Measure(new Vector2(availableSize.X, float.PositiveInfinity));
            _itemHeight = Math.Max(1f, desired.Y);
            return new Vector2(desired.X, count * _itemHeight);
        }

        protected override void ArrangeChildren(RectangleF bounds)
        {
            ItemsControl owner = Owner;
            int count = owner == null ? 0 : owner.ItemCount;
            if (count == 0 || _itemHeight <= 0f)
            {
                if (owner != null)
                    owner.RecycleOutside(0, -1);
                return;
            }

            RectangleF viewport = FindViewport(bounds);
            int first = Math.Max(0, (int)Math.Floor((viewport.Y - bounds.Y) / _itemHeight) - Buffer);
            int last = Math.Min(count - 1, (int)Math.Ceiling((viewport.Bottom - bounds.Y) / _itemHeight) + Buffer);
            owner.RecycleOutside(first, last);
            for (int index = first; index <= last; index++)
            {
                VisualElement container = owner.RealizeContainer(index);
                container.Measure(new Vector2(bounds.Width, _itemHeight));
                container.ArrangeInSlot(new RectangleF(bounds.X, bounds.Y + index * _itemHeight, bounds.Width, _itemHeight));
            }
        }

        RectangleF FindViewport(RectangleF bounds)
        {
            for (VisualElement ancestor = Parent; ancestor != null; ancestor = ancestor.Parent)
            {
                var scrollViewer = ancestor as ScrollViewer;
                if (scrollViewer != null)
                    return scrollViewer.ViewportBounds;
            }
            return bounds;
        }
    }
}
