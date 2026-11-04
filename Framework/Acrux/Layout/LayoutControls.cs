using System;
using System.Collections.Generic;
using Acrux.Core;
using Acrux.Core.Invalidation;
using Acrux.Core.PropertySystem;
using VRageMath;

namespace Acrux.Layout
{
    public sealed class Grid : Panel
    {
        protected override void DrawSelf(Acrux.Rendering.RenderContext context) { DrawBackground(context); }
        sealed class Placement
        {
            public int Column;
            public int Row;
            public int ColumnSpan;
            public int RowSpan;
        }

        GridLength[] _columnLayout;
        GridLength[] _rowLayout;
        float[] _measuredAutoColumns;
        float[] _measuredAutoRows;

        public Grid()
            : this(1, 1)
        {
        }

        public Grid(int columns, int rows)
        {
            Columns = CreateSegments(columns);
            Rows = CreateSegments(rows);
        }

        public Grid(VisualElement parent, int columns, int rows)
            : this(columns, rows)
        {
            if (parent != null)
                parent.AddChild(this);
        }

        public float[] Columns { get; private set; }
        public float[] Rows { get; private set; }

        public void SetColumns(params float[] columns)
        {
            _columnLayout = null;
            _measuredAutoColumns = null;
            Columns = NormalizeSegments(columns);
            Invalidate(UiInvalidation.Measure);
        }

        public void SetRows(params float[] rows)
        {
            _rowLayout = null;
            _measuredAutoRows = null;
            Rows = NormalizeSegments(rows);
            Invalidate(UiInvalidation.Measure);
        }

        public void SetColumnDefinitions(params GridLength[] columns)
        {
            _columnLayout = NormalizeGridLengths(columns);
            _measuredAutoColumns = new float[_columnLayout.Length];
            Columns = CreateSegments(_columnLayout.Length);
            Invalidate(UiInvalidation.Measure);
        }

        public void SetRowDefinitions(params GridLength[] rows)
        {
            _rowLayout = NormalizeGridLengths(rows);
            _measuredAutoRows = new float[_rowLayout.Length];
            Rows = CreateSegments(_rowLayout.Length);
            Invalidate(UiInvalidation.Measure);
        }

        public void SetRowLayout(params GridLength[] rows)
        {
            SetRowDefinitions(rows);
        }

        public T Set<T>(T child, int column, int row, int columnSpan = 1, int rowSpan = 1)
            where T : VisualElement
        {
            ValidatePlacement(column, row, columnSpan, rowSpan);
            child.SetGridPlacement(column, row, columnSpan, rowSpan);
            AddChild(child);
            Invalidate(UiInvalidation.Measure);
            return child;
        }

        /// <summary>
        /// Measures legacy proportional segments and Auto, pixel, and
        /// star definitions without materializing separate row or column controls.
        /// </summary>
        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            if (_columnLayout != null || _rowLayout != null)
                return MeasureDefinitions(availableSize);

            float totalColumnWeight = SumSegments(Columns);
            float totalRowWeight = SumSegments(Rows);
            float desiredWidth = 0f;
            float desiredHeight = 0f;

            IReadOnlyList<VisualElement> children = VisualChildren;
            for (int i = 0; i < children.Count; i++)
            {
                VisualElement child = children[i];
                Placement placement = GetPlacement(child, i);

                float columnWeight = SumSegments(
                    Columns,
                    placement.Column,
                    placement.ColumnSpan);
                float rowWeight = SumSegments(
                    Rows,
                    placement.Row,
                    placement.RowSpan);

                float childAvailableWidth = GetProportionalConstraint(
                    availableSize.X,
                    columnWeight,
                    totalColumnWeight);
                float childAvailableHeight = GetProportionalConstraint(
                    availableSize.Y,
                    rowWeight,
                    totalRowWeight);
                Vector2 childDesired = child.Measure(new Vector2(
                    childAvailableWidth,
                    childAvailableHeight));

                if (columnWeight > 0f && totalColumnWeight > 0f)
                    desiredWidth = Math.Max(
                        desiredWidth,
                        childDesired.X * totalColumnWeight / columnWeight);
                else
                    desiredWidth = Math.Max(desiredWidth, childDesired.X);

                if (rowWeight > 0f && totalRowWeight > 0f)
                    desiredHeight = Math.Max(
                        desiredHeight,
                        childDesired.Y * totalRowWeight / rowWeight);
                else
                    desiredHeight = Math.Max(desiredHeight, childDesired.Y);
            }

            if (!float.IsPositiveInfinity(availableSize.X))
                desiredWidth = Math.Min(Math.Max(0f, availableSize.X), desiredWidth);
            if (!float.IsPositiveInfinity(availableSize.Y))
                desiredHeight = Math.Min(Math.Max(0f, availableSize.Y), desiredHeight);
            return new Vector2(Math.Max(0f, desiredWidth), Math.Max(0f, desiredHeight));
        }

        protected override void ArrangeChildren(RectangleF bounds)
        {
            float[] resolvedColumns = _columnLayout == null
                ? null
                : ResolveDefinitionSizes(
                    _columnLayout, _measuredAutoColumns, bounds.Width);
            float[] resolvedRows = _rowLayout == null
                ? null
                : ResolveRowSizes(bounds.Height);
            IReadOnlyList<VisualElement> children = VisualChildren;
            for (int i = 0; i < children.Count; i++)
            {
                VisualElement child = children[i];
                if (!child.Visible)
                    continue;
                Placement placement = GetPlacement(child, i);
                child.ArrangeInSlot(GetCellBounds(
                    bounds,
                    placement.Column,
                    placement.Row,
                    placement.ColumnSpan,
                    placement.RowSpan,
                    resolvedColumns,
                    resolvedRows));
            }
        }

        Vector2 MeasureDefinitions(Vector2 availableSize)
        {
            if (_columnLayout != null &&
                (_measuredAutoColumns == null ||
                 _measuredAutoColumns.Length != _columnLayout.Length))
                _measuredAutoColumns = new float[_columnLayout.Length];
            if (_rowLayout != null &&
                (_measuredAutoRows == null || _measuredAutoRows.Length != _rowLayout.Length))
                _measuredAutoRows = new float[_rowLayout.Length];
            if (_measuredAutoColumns != null)
                Array.Clear(_measuredAutoColumns, 0, _measuredAutoColumns.Length);
            if (_measuredAutoRows != null)
                Array.Clear(_measuredAutoRows, 0, _measuredAutoRows.Length);

            float totalColumnWeight = SumSegments(Columns);
            float desiredWidth = 0f;
            float desiredHeight = 0f;
            IReadOnlyList<VisualElement> children = VisualChildren;

            for (int i = 0; i < children.Count; i++)
            {
                VisualElement child = children[i];
                if (!child.Visible)
                    continue;
                Placement placement = GetPlacement(child, i);
                bool autoColumn = IsSingleAuto(
                    _columnLayout, placement.Column, placement.ColumnSpan);
                bool autoRow = IsSingleAuto(
                    _rowLayout, placement.Row, placement.RowSpan);
                if (!autoColumn && !autoRow)
                    continue;

                float childWidth = GetChildColumnConstraint(
                    availableSize.X, totalColumnWeight, placement);
                float childHeight = GetChildRowConstraint(
                    availableSize.Y, placement);
                Vector2 desired = child.Measure(new Vector2(childWidth, childHeight));
                UpdateAutoMeasurements(placement, desired);
                desiredWidth = Math.Max(desiredWidth, ExpandDesiredWidth(
                    desired.X, availableSize.X, totalColumnWeight, placement));
                desiredHeight = Math.Max(desiredHeight, desired.Y);
            }

            float[] columnSizes = _columnLayout == null
                ? null
                : ResolveDefinitionSizes(
                    _columnLayout, _measuredAutoColumns, availableSize.X);
            float[] rowSizes = ResolveRowSizes(availableSize.Y);
            float[] starColumns = _columnLayout == null ? null : new float[_columnLayout.Length];
            float[] starRows = _rowLayout == null ? null : new float[_rowLayout.Length];
            var spanning = new List<KeyValuePair<Placement, Vector2>>();
            // A control's first measure also applies its style and template, so the first pass can report a
            // size it no longer wants. Auto definitions take their size from this final pass.
            float[] finalAutoColumns = _measuredAutoColumns == null ? null : new float[_measuredAutoColumns.Length];
            float[] finalAutoRows = _measuredAutoRows == null ? null : new float[_measuredAutoRows.Length];
            for (int i = 0; i < children.Count; i++)
            {
                VisualElement child = children[i];
                if (!child.Visible)
                    continue;
                Placement placement = GetPlacement(child, i);
                float childWidth = _columnLayout == null
                    ? GetChildColumnConstraint(
                        availableSize.X, totalColumnWeight, placement)
                    : float.IsPositiveInfinity(availableSize.X)
                        ? float.PositiveInfinity
                        : SumSegments(
                            columnSizes, placement.Column, placement.ColumnSpan);
                float childHeight = _rowLayout == null
                    ? GetProportionalConstraint(
                        availableSize.Y,
                        SumSegments(Rows, placement.Row, placement.RowSpan),
                        SumSegments(Rows))
                    : float.IsPositiveInfinity(availableSize.Y)
                        ? float.PositiveInfinity
                        : SumSegments(rowSizes, placement.Row, placement.RowSpan);
                Vector2 desired = child.Measure(new Vector2(childWidth, childHeight));
                if (finalAutoColumns != null && IsSingleAuto(_columnLayout, placement.Column, placement.ColumnSpan))
                    finalAutoColumns[placement.Column] = Math.Max(finalAutoColumns[placement.Column], desired.X);
                if (finalAutoRows != null && IsSingleAuto(_rowLayout, placement.Row, placement.RowSpan))
                    finalAutoRows[placement.Row] = Math.Max(finalAutoRows[placement.Row], desired.Y);
                desiredWidth = Math.Max(desiredWidth, ExpandDesiredWidth(
                    desired.X, availableSize.X, totalColumnWeight, placement));
                desiredHeight = Math.Max(desiredHeight, desired.Y);
                if (IsSingleStar(_columnLayout, placement.Column, placement.ColumnSpan))
                    starColumns[placement.Column] = Math.Max(starColumns[placement.Column], desired.X);
                if (IsSingleStar(_rowLayout, placement.Row, placement.RowSpan))
                    starRows[placement.Row] = Math.Max(starRows[placement.Row], desired.Y);
                if (placement.ColumnSpan > 1 || placement.RowSpan > 1)
                    spanning.Add(new KeyValuePair<Placement, Vector2>(placement, desired));
            }

            if (finalAutoColumns != null)
                _measuredAutoColumns = finalAutoColumns;
            if (finalAutoRows != null)
                _measuredAutoRows = finalAutoRows;

            foreach (var item in spanning)
            {
                if (item.Key.ColumnSpan > 1)
                    GrowStars(_columnLayout, _measuredAutoColumns, starColumns, item.Key.Column, item.Key.ColumnSpan, item.Value.X);
                if (item.Key.RowSpan > 1)
                    GrowStars(_rowLayout, _measuredAutoRows, starRows, item.Key.Row, item.Key.RowSpan, item.Value.Y);
            }

            if (_columnLayout != null)
                desiredWidth = Math.Min(Math.Max(0f, availableSize.X),
                    SumFixedAndAuto(_columnLayout, _measuredAutoColumns) + Sum(starColumns));
            else if (!float.IsPositiveInfinity(availableSize.X))
                desiredWidth = Math.Min(Math.Max(0f, availableSize.X), desiredWidth);
            if (_rowLayout != null)
                desiredHeight = Math.Min(Math.Max(0f, availableSize.Y), SumFixedAndAutoRows() + Sum(starRows));
            else if (!float.IsPositiveInfinity(availableSize.Y))
                desiredHeight = Math.Min(Math.Max(0f, availableSize.Y), desiredHeight);
            return new Vector2(Math.Max(0f, desiredWidth), Math.Max(0f, desiredHeight));
        }

        float GetChildColumnConstraint(
            float availableWidth,
            float totalColumnWeight,
            Placement placement)
        {
            if (_columnLayout != null)
            {
                if (IsSingleAuto(
                        _columnLayout, placement.Column, placement.ColumnSpan) ||
                    float.IsPositiveInfinity(availableWidth))
                    return float.PositiveInfinity;
                float[] sizes = ResolveDefinitionSizes(
                    _columnLayout, _measuredAutoColumns, availableWidth);
                return SumSegments(sizes, placement.Column, placement.ColumnSpan);
            }
            float columnWeight = SumSegments(
                Columns, placement.Column, placement.ColumnSpan);
            return GetProportionalConstraint(
                availableWidth, columnWeight, totalColumnWeight);
        }

        float ExpandDesiredWidth(
            float childDesiredWidth,
            float availableWidth,
            float totalColumnWeight,
            Placement placement)
        {
            if (_columnLayout != null)
                return float.IsPositiveInfinity(availableWidth)
                    ? childDesiredWidth
                    : Math.Max(0f, availableWidth);
            float columnWeight = SumSegments(
                Columns, placement.Column, placement.ColumnSpan);
            return columnWeight > 0f && totalColumnWeight > 0f
                ? childDesiredWidth * totalColumnWeight / columnWeight
                : childDesiredWidth;
        }

        float GetChildRowConstraint(float availableHeight, Placement placement)
        {
            if (_rowLayout == null)
            {
                return GetProportionalConstraint(
                    availableHeight,
                    SumSegments(Rows, placement.Row, placement.RowSpan),
                    SumSegments(Rows));
            }
            if (IsSingleAuto(_rowLayout, placement.Row, placement.RowSpan) ||
                float.IsPositiveInfinity(availableHeight))
                return float.PositiveInfinity;
            float[] sizes = ResolveRowSizes(availableHeight);
            return SumSegments(sizes, placement.Row, placement.RowSpan);
        }

        void UpdateAutoMeasurements(Placement placement, Vector2 desired)
        {
            if (IsSingleAuto(_columnLayout, placement.Column, placement.ColumnSpan))
            {
                _measuredAutoColumns[placement.Column] = Math.Max(
                    _measuredAutoColumns[placement.Column], desired.X);
            }
            if (IsSingleAuto(_rowLayout, placement.Row, placement.RowSpan))
            {
                _measuredAutoRows[placement.Row] = Math.Max(
                    _measuredAutoRows[placement.Row], desired.Y);
            }
        }

        float[] ResolveRowSizes(float availableHeight)
        {
            if (_rowLayout == null)
                return null;
            return ResolveDefinitionSizes(
                _rowLayout, _measuredAutoRows, availableHeight);
        }

        static float[] ResolveDefinitionSizes(
            GridLength[] definitions,
            float[] measuredAuto,
            float available)
        {
            var result = new float[definitions.Length];
            float reserved = 0f;
            float starWeight = 0f;
            for (int i = 0; i < definitions.Length; i++)
            {
                GridLength definition = definitions[i];
                if (definition.GridUnitType == GridUnitType.Auto)
                    result[i] = measuredAuto == null ? 0f : measuredAuto[i];
                else if (definition.GridUnitType == GridUnitType.Pixel)
                    result[i] = definition.Value;
                else
                    starWeight += definition.Value;

                if (definition.GridUnitType != GridUnitType.Star)
                    reserved += result[i];
            }

            float remaining = float.IsPositiveInfinity(available)
                ? 0f
                : Math.Max(0f, available - reserved);
            if (starWeight <= 0f)
                return result;
            for (int i = 0; i < definitions.Length; i++)
            {
                if (definitions[i].GridUnitType == GridUnitType.Star)
                    result[i] = remaining * definitions[i].Value / starWeight;
            }
            return result;
        }

        float SumFixedAndAutoRows()
        {
            return SumFixedAndAuto(_rowLayout, _measuredAutoRows);
        }

        static float SumFixedAndAuto(
            GridLength[] definitions,
            float[] measuredAuto)
        {
            float result = 0f;
            for (int i = 0; i < definitions.Length; i++)
            {
                if (definitions[i].GridUnitType == GridUnitType.Auto)
                    result += measuredAuto == null ? 0f : measuredAuto[i];
                else if (definitions[i].GridUnitType == GridUnitType.Pixel)
                    result += definitions[i].Value;
            }
            return result;
        }

        static bool IsSingleStar(GridLength[] definitions, int index, int span)
        {
            return definitions != null && span == 1 && definitions[index].GridUnitType == GridUnitType.Star;
        }

        // A child spanning several definitions grows the star ones it covers by whatever the rest lack.
        static void GrowStars(GridLength[] definitions, float[] measuredAuto, float[] stars, int start, int span, float desired)
        {
            if (definitions == null)
                return;
            float covered = 0f;
            float weight = 0f;
            for (int i = start; i < start + span && i < definitions.Length; i++)
            {
                if (definitions[i].GridUnitType == GridUnitType.Auto)
                    covered += measuredAuto == null ? 0f : measuredAuto[i];
                else if (definitions[i].GridUnitType == GridUnitType.Pixel)
                    covered += definitions[i].Value;
                else
                {
                    covered += stars[i];
                    weight += definitions[i].Value;
                }
            }
            float missing = desired - covered;
            if (missing <= 0f || weight <= 0f)
                return;
            for (int i = start; i < start + span && i < definitions.Length; i++)
                if (definitions[i].GridUnitType == GridUnitType.Star)
                    stars[i] += missing * definitions[i].Value / weight;
        }

        static float Sum(float[] values)
        {
            float total = 0f;
            if (values != null)
                for (int i = 0; i < values.Length; i++)
                    total += values[i];
            return total;
        }

        static bool IsSingleAuto(
            GridLength[] definitions,
            int index,
            int span)
        {
            return definitions != null && span == 1 &&
                   definitions[index].GridUnitType == GridUnitType.Auto;
        }

        Placement GetPlacement(VisualElement child, int index)
        {
            if (!child.HasExplicitGridPlacement)
                return GetSequentialPlacement(index);
            return new Placement
            {
                Column = child.GridColumn,
                Row = child.GridRow,
                ColumnSpan = child.GridColumnSpan,
                RowSpan = child.GridRowSpan
            };
        }

        Placement GetSequentialPlacement(int index)
        {
            int columnCount = Columns.Length;
            return new Placement
            {
                Column = index % columnCount,
                Row = Math.Min(Rows.Length - 1, index / columnCount),
                ColumnSpan = 1,
                RowSpan = 1
            };
        }

        RectangleF GetCellBounds(
            RectangleF bounds,
            int column,
            int row,
            int columnSpan,
            int rowSpan,
            float[] resolvedColumns,
            float[] resolvedRows)
        {
            float x = resolvedColumns == null
                ? GetSegmentStart(bounds.X, bounds.Width, Columns, column)
                : GetAbsoluteSegmentStart(bounds.X, resolvedColumns, column);
            float y = resolvedRows == null
                ? GetSegmentStart(bounds.Y, bounds.Height, Rows, row)
                : GetAbsoluteSegmentStart(bounds.Y, resolvedRows, row);
            float right = resolvedColumns == null
                ? GetSegmentStart(
                    bounds.X,
                    bounds.Width,
                    Columns,
                    Math.Min(Columns.Length, column + columnSpan))
                : GetAbsoluteSegmentStart(
                    bounds.X,
                    resolvedColumns,
                    Math.Min(resolvedColumns.Length, column + columnSpan));
            float bottom = resolvedRows == null
                ? GetSegmentStart(
                    bounds.Y,
                    bounds.Height,
                    Rows,
                    Math.Min(Rows.Length, row + rowSpan))
                : GetAbsoluteSegmentStart(
                    bounds.Y,
                    resolvedRows,
                    Math.Min(resolvedRows.Length, row + rowSpan));
            return new RectangleF(x, y, Math.Max(0f, right - x), Math.Max(0f, bottom - y));
        }

        void ValidatePlacement(int column, int row, int columnSpan, int rowSpan)
        {
            if (column < 0 || column >= Columns.Length)
                throw new InvalidOperationException("Grid column is outside the configured columns.");
            if (row < 0 || row >= Rows.Length)
                throw new InvalidOperationException("Grid row is outside the configured rows.");
            if (columnSpan < 1 || column + columnSpan > Columns.Length)
                throw new InvalidOperationException("Grid column span is outside the configured columns.");
            if (rowSpan < 1 || row + rowSpan > Rows.Length)
                throw new InvalidOperationException("Grid row span is outside the configured rows.");
        }

        static float GetSegmentStart(float origin, float size, float[] segments, int index)
        {
            if (index <= 0)
                return origin;
            if (index >= segments.Length)
                return origin + size;
            float total = SumSegments(segments);
            if (total <= 0f)
                return origin + size * index / segments.Length;
            float offset = SumSegments(segments, 0, index);
            return origin + size * offset / total;
        }

        static float GetAbsoluteSegmentStart(float origin, float[] segments, int index)
        {
            return origin + SumSegments(segments, 0, Math.Min(index, segments.Length));
        }

        static float GetProportionalConstraint(float available, float part, float total)
        {
            if (float.IsPositiveInfinity(available))
                return available;
            if (available <= 0f || part <= 0f || total <= 0f)
                return 0f;
            return available * part / total;
        }

        static float SumSegments(float[] segments)
        {
            return SumSegments(segments, 0, segments == null ? 0 : segments.Length);
        }

        static float SumSegments(float[] segments, int start, int count)
        {
            if (segments == null || segments.Length == 0 || count <= 0)
                return 0f;
            int first = Math.Max(0, start);
            int end = Math.Min(segments.Length, first + count);
            float total = 0f;
            for (int i = first; i < end; i++)
                total += Math.Max(0f, segments[i]);
            return total;
        }

        static float[] CreateSegments(int count)
        {
            int length = Math.Max(1, count);
            float[] result = new float[length];
            for (int i = 0; i < result.Length; i++)
                result[i] = 1f;
            return result;
        }

        static float[] NormalizeSegments(float[] segments)
        {
            if (segments == null || segments.Length == 0)
                return new[] { 1f };
            float[] result = new float[segments.Length];
            for (int i = 0; i < segments.Length; i++)
                result[i] = Math.Max(0f, segments[i]);
            return result;
        }

        static GridLength[] NormalizeGridLengths(GridLength[] rows)
        {
            if (rows == null || rows.Length == 0)
                return new[] { GridLength.Star };
            var result = new GridLength[rows.Length];
            for (int i = 0; i < rows.Length; i++)
                result[i] = rows[i];
            return result;
        }
    }

    public enum StackOrientation
    {
        Vertical,
        Horizontal
    }

    public sealed class StackPanel : Panel
    {
        protected override void DrawSelf(Acrux.Rendering.RenderContext context) { DrawBackground(context); }
        public static readonly StyledProperty<float> SpacingProperty =
            StyledProperty.Register<StackPanel, float>(nameof(Spacing), 0f, UiInvalidation.Measure);

        StackOrientation _orientation = StackOrientation.Vertical;

        public StackPanel()
        {
        }

        public StackPanel(VisualElement parent)
        {
            if (parent != null)
                parent.AddChild(this);
        }

        public StackOrientation Orientation
        {
            get { return _orientation; }
            set
            {
                if (_orientation == value)
                    return;
                _orientation = value;
                Invalidate(UiInvalidation.Measure);
            }
        }

        public float Spacing
        {
            get { return GetValue(SpacingProperty); }
            set { SetValue(SpacingProperty, value); }
        }

        /// <summary>
        /// Stack measurement: children receive infinite space on the
        /// stacking axis and the parent's constraint on the cross axis. Desired size is
        /// the sum on the stacking axis and the maximum child on the cross axis.
        /// </summary>
        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            return Orientation == StackOrientation.Horizontal
                ? MeasureHorizontal(availableSize)
                : MeasureVertical(availableSize);
        }

        Vector2 MeasureVertical(Vector2 availableSize)
        {
            float width = 0f;
            float height = 0f;
            bool hasVisibleChild = false;
            IReadOnlyList<VisualElement> children = VisualChildren;
            for (int i = 0; i < children.Count; i++)
            {
                VisualElement child = children[i];
                if (!child.Visible)
                    continue;
                Vector2 desired = child.Measure(new Vector2(
                    availableSize.X,
                    float.PositiveInfinity));
                if (hasVisibleChild)
                    height += Spacing;
                width = Math.Max(width, Math.Max(0f, desired.X));
                height += Math.Max(0f, desired.Y);
                hasVisibleChild = true;
            }
            return new Vector2(width, Math.Max(0f, height));
        }

        Vector2 MeasureHorizontal(Vector2 availableSize)
        {
            float width = 0f;
            float height = 0f;
            bool hasVisibleChild = false;
            IReadOnlyList<VisualElement> children = VisualChildren;
            for (int i = 0; i < children.Count; i++)
            {
                VisualElement child = children[i];
                if (!child.Visible)
                    continue;
                Vector2 desired = child.Measure(new Vector2(
                    float.PositiveInfinity,
                    availableSize.Y));
                if (hasVisibleChild)
                    width += Spacing;
                width += Math.Max(0f, desired.X);
                height = Math.Max(height, Math.Max(0f, desired.Y));
                hasVisibleChild = true;
            }
            return new Vector2(Math.Max(0f, width), height);
        }

        protected override void ArrangeChildren(RectangleF bounds)
        {
            if (Orientation == StackOrientation.Horizontal)
                ArrangeHorizontal(bounds);
            else
                ArrangeVertical(bounds);
        }

        void ArrangeVertical(RectangleF bounds)
        {
            float y = bounds.Y;
            bool hasVisibleChild = false;
            IReadOnlyList<VisualElement> children = VisualChildren;
            for (int i = 0; i < children.Count; i++)
            {
                VisualElement child = children[i];
                if (!child.Visible)
                    continue;
                Vector2 desired = child.DesiredSize;
                if (hasVisibleChild)
                    y += Spacing;
                float slotHeight = Math.Max(0f, desired.Y);
                child.ArrangeInSlot(
                    new RectangleF(bounds.X, y, bounds.Width, slotHeight),
                    desired);
                y += slotHeight;
                hasVisibleChild = true;
            }
        }

        void ArrangeHorizontal(RectangleF bounds)
        {
            float x = bounds.X;
            bool hasVisibleChild = false;
            IReadOnlyList<VisualElement> children = VisualChildren;
            for (int i = 0; i < children.Count; i++)
            {
                VisualElement child = children[i];
                if (!child.Visible)
                    continue;
                Vector2 desired = child.DesiredSize;
                if (hasVisibleChild)
                    x += Spacing;
                float slotWidth = Math.Max(0f, desired.X);
                child.ArrangeInSlot(
                    new RectangleF(x, bounds.Y, slotWidth, bounds.Height),
                    desired);
                x += slotWidth;
                hasVisibleChild = true;
            }
        }
    }
}
