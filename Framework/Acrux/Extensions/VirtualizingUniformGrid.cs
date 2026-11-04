using System;
using Acrux.Controls;
using Acrux.Core;
using Acrux.Core.Invalidation;
using Acrux.Core.PropertySystem;
using Acrux.Layout;
using Acrux.Rendering;
using VRageMath;

namespace Acrux.Extensions
{
    /// <summary>
    /// Acrux extension for a vertically scrolling, fixed-height UniformGrid items panel.
    /// Only items near the enclosing ScrollViewer's viewport have visual containers.
    /// All cells use the height measured from the first item.
    /// </summary>
    public sealed class VirtualizingUniformGrid : Panel, IVirtualizingItemsPanel
    {
        const int BufferRows = 2;

        public static readonly StyledProperty<int> ColumnsProperty =
            StyledProperty.Register<VirtualizingUniformGrid, int>(nameof(Columns), 1, UiInvalidation.Measure);
        public static readonly StyledProperty<float> RowSpacingProperty =
            StyledProperty.Register<VirtualizingUniformGrid, float>(nameof(RowSpacing), 0f, UiInvalidation.Measure);
        public static readonly StyledProperty<float> ColumnSpacingProperty =
            StyledProperty.Register<VirtualizingUniformGrid, float>(nameof(ColumnSpacing), 0f, UiInvalidation.Measure);

        float _cellHeight;

        ItemsControl Owner => Parent as ItemsControl;

        public int Columns
        {
            get { return GetValue(ColumnsProperty); }
            set { SetValue(ColumnsProperty, value); }
        }

        public float RowSpacing
        {
            get { return GetValue(RowSpacingProperty); }
            set { SetValue(RowSpacingProperty, value); }
        }

        public float ColumnSpacing
        {
            get { return GetValue(ColumnSpacingProperty); }
            set { SetValue(ColumnSpacingProperty, value); }
        }

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
                _cellHeight = 0f;
                return Vector2.Zero;
            }

            int columns = Math.Max(1, Columns);
            float spacingX = Math.Max(0f, ColumnSpacing);
            float cellWidth = Math.Max(0f, (availableSize.X - (columns - 1) * spacingX) / columns);
            VisualElement sample = owner.RealizeContainer(0);
            Vector2 desired = sample.Measure(new Vector2(cellWidth, float.PositiveInfinity));
            _cellHeight = Math.Max(1f, desired.Y);

            int rows = (count - 1) / columns + 1;
            float spacingY = Math.Max(0f, RowSpacing);
            float width = availableSize.X == float.MaxValue || float.IsPositiveInfinity(availableSize.X)
                ? desired.X * columns + (columns - 1) * spacingX
                : availableSize.X;
            return new Vector2(width, rows * _cellHeight + (rows - 1) * spacingY);
        }

        protected override void ArrangeChildren(RectangleF bounds)
        {
            ItemsControl owner = Owner;
            int count = owner == null ? 0 : owner.ItemCount;
            if (count == 0 || _cellHeight <= 0f)
            {
                if (owner != null)
                    owner.RecycleOutside(0, -1);
                return;
            }

            int columns = Math.Max(1, Columns);
            float spacingX = Math.Max(0f, ColumnSpacing);
            float spacingY = Math.Max(0f, RowSpacing);
            float cellWidth = Math.Max(0f, (bounds.Width - (columns - 1) * spacingX) / columns);
            float rowPitch = _cellHeight + spacingY;
            RectangleF viewport = FindViewport(bounds);
            int firstRow = Math.Max(0, (int)Math.Floor((viewport.Y - bounds.Y) / rowPitch) - BufferRows);
            int lastRow = Math.Min((count - 1) / columns,
                (int)Math.Ceiling((viewport.Bottom - bounds.Y) / rowPitch) + BufferRows);
            int first = Math.Min(count, firstRow * columns);
            int last = Math.Min(count - 1, (lastRow + 1) * columns - 1);
            owner.RecycleOutside(first, last);

            for (int index = first; index <= last; index++)
            {
                VisualElement container = owner.RealizeContainer(index);
                container.Measure(new Vector2(cellWidth, _cellHeight));
                int row = index / columns;
                int column = index % columns;
                container.ArrangeInSlot(new RectangleF(
                    bounds.X + column * (cellWidth + spacingX),
                    bounds.Y + row * rowPitch,
                    cellWidth,
                    _cellHeight));
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
