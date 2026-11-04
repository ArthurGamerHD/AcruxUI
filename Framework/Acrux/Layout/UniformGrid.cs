using System;
using System.Collections.Generic;
using Acrux.Core;
using Acrux.Core.Invalidation;
using Acrux.Core.PropertySystem;
using VRageMath;

namespace Acrux.Layout
{
    /// <summary>
    /// Port of UniformGrid: arranges children in a grid where every cell has the same size.
    /// </summary>
    public sealed class UniformGrid : Panel
    {
        protected override void DrawSelf(Acrux.Rendering.RenderContext context) { DrawBackground(context); }
        static UniformGrid() { }

        public static readonly StyledProperty<int> RowsProperty =
            StyledProperty.Register<UniformGrid, int>(nameof(Rows), 0, UiInvalidation.Measure);
        public static readonly StyledProperty<int> ColumnsProperty =
            StyledProperty.Register<UniformGrid, int>(nameof(Columns), 0, UiInvalidation.Measure);
        public static readonly StyledProperty<int> FirstColumnProperty =
            StyledProperty.Register<UniformGrid, int>(nameof(FirstColumn), 0, UiInvalidation.Measure);
        public static readonly StyledProperty<float> RowSpacingProperty =
            StyledProperty.Register<UniformGrid, float>(nameof(RowSpacing), 0f, UiInvalidation.Measure);
        public static readonly StyledProperty<float> ColumnSpacingProperty =
            StyledProperty.Register<UniformGrid, float>(nameof(ColumnSpacing), 0f, UiInvalidation.Measure);

        int _rows;
        int _columns;

        public int Rows
        {
            get { return GetValue(RowsProperty); }
            set { SetValue(RowsProperty, value); }
        }

        public int Columns
        {
            get { return GetValue(ColumnsProperty); }
            set { SetValue(ColumnsProperty, value); }
        }

        public int FirstColumn
        {
            get { return GetValue(FirstColumnProperty); }
            set { SetValue(FirstColumnProperty, value); }
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

        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            UpdateRowsAndColumns();
            if (_rows == 0 || _columns == 0)
                return Vector2.Zero;

            var childAvailableSize = new Vector2(
                (availableSize.X - (_columns - 1) * ColumnSpacing) / _columns,
                (availableSize.Y - (_rows - 1) * RowSpacing) / _rows);

            float maxWidth = 0f;
            float maxHeight = 0f;
            IReadOnlyList<VisualElement> children = VisualChildren;
            for (int i = 0; i < children.Count; i++)
            {
                VisualElement child = children[i];
                Vector2 desired = child.Measure(childAvailableSize);
                if (!child.Visible)
                    continue;
                maxWidth = Math.Max(maxWidth, desired.X);
                maxHeight = Math.Max(maxHeight, desired.Y);
            }

            return new Vector2(
                Math.Max(0f, maxWidth * _columns + ColumnSpacing * (_columns - 1)),
                Math.Max(0f, maxHeight * _rows + RowSpacing * (_rows - 1)));
        }

        protected override void ArrangeChildren(RectangleF bounds)
        {
            if (_rows == 0 || _columns == 0)
                return;

            int x = FirstColumn;
            int y = 0;
            float columnSpacing = ColumnSpacing;
            float rowSpacing = RowSpacing;
            float width = Math.Max((bounds.Width - (_columns - 1) * columnSpacing) / _columns, 0f);
            float height = Math.Max((bounds.Height - (_rows - 1) * rowSpacing) / _rows, 0f);

            IReadOnlyList<VisualElement> children = VisualChildren;
            for (int i = 0; i < children.Count; i++)
            {
                VisualElement child = children[i];
                if (!child.Visible)
                    continue;

                child.ArrangeInSlot(new RectangleF(
                    bounds.X + x * (width + columnSpacing),
                    bounds.Y + y * (height + rowSpacing),
                    width,
                    height));

                x++;
                if (x >= _columns)
                {
                    x = 0;
                    y++;
                }
            }
        }

        void UpdateRowsAndColumns()
        {
            _rows = Rows;
            _columns = Columns;
            if (FirstColumn >= _columns)
                FirstColumn = 0;

            int itemCount = FirstColumn;
            IReadOnlyList<VisualElement> children = VisualChildren;
            for (int i = 0; i < children.Count; i++)
            {
                if (children[i].Visible)
                    itemCount++;
            }

            if (_rows == 0)
            {
                if (_columns == 0)
                {
                    _rows = _columns = (int)Math.Ceiling(Math.Sqrt(itemCount));
                }
                else
                {
                    int remainder;
                    _rows = Math.DivRem(itemCount, _columns, out remainder);
                    if (remainder != 0)
                        _rows++;
                }
            }
            else if (_columns == 0)
            {
                int remainder;
                _columns = Math.DivRem(itemCount, _rows, out remainder);
                if (remainder != 0)
                    _columns++;
            }
        }
    }
}
