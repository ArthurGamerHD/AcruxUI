using System;
using Acrux.Core.Invalidation;
using VRageMath;

namespace Acrux.Core
{
    public enum HorizontalAlignment
    {
        Stretch,
        Left,
        Center,
        Right
    }

    public enum VerticalAlignment
    {
        Stretch,
        Top,
        Center,
        Bottom
    }

    public abstract class FrameworkElement : Visual
    {
        float? _width;
        float? _height;
        float _minWidth;
        float _minHeight;
        float _maxWidth = float.PositiveInfinity;
        float _maxHeight = float.PositiveInfinity;
        Vector4 _margin;
        HorizontalAlignment _horizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment _verticalAlignment = VerticalAlignment.Stretch;

        public RectangleF Bounds { get; private set; }
        public Vector2 DesiredSize { get; private set; }
        Vector2 _previousMeasure;
        bool _hasMeasured;

        /// <summary>
        /// Optional explicit target width. Null leaves width to intrinsic measurement
        /// and the parent layout policy.
        /// </summary>
        public virtual float? Width
        {
            get { return _width; }
            set
            {
                if (_width == value)
                    return;
                _width = value;
                Invalidate(UiInvalidation.Measure);
            }
        }

        /// <summary>
        /// Optional explicit target height. Null leaves height to intrinsic measurement
        /// and the parent layout policy.
        /// </summary>
        public virtual float? Height
        {
            get { return _height; }
            set
            {
                if (_height == value)
                    return;
                _height = value;
                Invalidate(UiInvalidation.Measure);
            }
        }

        public virtual float MinWidth
        {
            get { return _minWidth; }
            set
            {
                float next = Math.Max(0f, value);
                if (Math.Abs(_minWidth - next) < 0.001f)
                    return;
                _minWidth = next;
                Invalidate(UiInvalidation.Measure);
            }
        }

        public virtual float MinHeight
        {
            get { return _minHeight; }
            set
            {
                float next = Math.Max(0f, value);
                if (Math.Abs(_minHeight - next) < 0.001f)
                    return;
                _minHeight = next;
                Invalidate(UiInvalidation.Measure);
            }
        }

        public virtual float MaxWidth
        {
            get { return _maxWidth; }
            set
            {
                float next = float.IsNaN(value)
                    ? float.PositiveInfinity
                    : Math.Max(0f, value);
                if (_maxWidth == next)
                    return;
                _maxWidth = next;
                Invalidate(UiInvalidation.Measure);
            }
        }

        public virtual float MaxHeight
        {
            get { return _maxHeight; }
            set
            {
                float next = float.IsNaN(value)
                    ? float.PositiveInfinity
                    : Math.Max(0f, value);
                if (_maxHeight == next)
                    return;
                _maxHeight = next;
                Invalidate(UiInvalidation.Measure);
            }
        }

        /// <summary>
        /// External spacing around this element: left, top, right, bottom.
        /// Margin participates in DesiredSize and is consumed by the parent slot.
        /// </summary>
        public virtual Vector4 Margin
        {
            get { return _margin; }
            set
            {
                if (_margin == value)
                    return;
                _margin = value;
                Invalidate(UiInvalidation.Measure);
            }
        }

        public virtual HorizontalAlignment HorizontalAlignment
        {
            get { return _horizontalAlignment; }
            set
            {
                if (_horizontalAlignment == value)
                    return;
                _horizontalAlignment = value;
                Invalidate(UiInvalidation.Arrange);
            }
        }

        public virtual VerticalAlignment VerticalAlignment
        {
            get { return _verticalAlignment; }
            set
            {
                if (_verticalAlignment == value)
                    return;
                _verticalAlignment = value;
                Invalidate(UiInvalidation.Arrange);
            }
        }

        public void Arrange(RectangleF bounds)
        {
            Control control = this as Control;
            if (control != null)
                control.EnsureStyleApplied();

            if ((PendingInvalidation & UiInvalidation.Measure) != 0)
                Measure(bounds.Size);

            Vector2 previousSize = Bounds.Size;
            Bounds = bounds;
            ArrangeChildren(bounds);
            ClearInvalidation(UiInvalidation.Arrange);
            if (control != null && previousSize != bounds.Size)
                control.RaiseSizeChanged(previousSize, bounds.Size);
        }

        /// <summary>
        /// Measures the element using the layout contract: available size is a
        /// constraint, explicit Width/Height are targets, Min/Max constrain the result,
        /// and Margin is added to the reported DesiredSize.
        /// </summary>
        public Vector2 Measure(Vector2 availableSize)
        {
            Control control = this as Control;
            if (control != null)
                control.EnsureStyleApplied();

            if (!Visible)
            {
                DesiredSize = Vector2.Zero;
                _hasMeasured = false;
                ClearInvalidation(UiInvalidation.Measure);
                return DesiredSize;
            }

            if (_hasMeasured && (PendingInvalidation & UiInvalidation.Measure) == 0 &&
                _previousMeasure == availableSize)
                return DesiredSize;
            _previousMeasure = availableSize;
            _hasMeasured = true;

            // Each property read is a store lookup, so read them once.
            Vector4 margin = Margin;
            float? width = Width;
            float? height = Height;
            float minWidth = MinWidth;
            float minHeight = MinHeight;
            float availableWidth = NormalizeConstraint(availableSize.X);
            float availableHeight = NormalizeConstraint(availableSize.Y);
            float marginWidth = margin.X + margin.Z;
            float marginHeight = margin.Y + margin.W;
            float contentAvailableWidth = DeflateConstraint(availableWidth, marginWidth);
            float contentAvailableHeight = DeflateConstraint(availableHeight, marginHeight);

            float effectiveMaxWidth = Math.Max(minWidth, MaxWidth);
            float effectiveMaxHeight = Math.Max(minHeight, MaxHeight);
            float measureWidth = width.HasValue
                ? ClampDimension(Math.Max(0f, width.Value), minWidth, effectiveMaxWidth)
                : Math.Min(contentAvailableWidth, effectiveMaxWidth);
            float measureHeight = height.HasValue
                ? ClampDimension(Math.Max(0f, height.Value), minHeight, effectiveMaxHeight)
                : Math.Min(contentAvailableHeight, effectiveMaxHeight);

            Vector2 intrinsic = MeasureOverride(new Vector2(measureWidth, measureHeight));
            float desiredWidth = width.HasValue
                ? Math.Max(0f, width.Value)
                : Math.Max(0f, intrinsic.X);
            float desiredHeight = height.HasValue
                ? Math.Max(0f, height.Value)
                : Math.Max(0f, intrinsic.Y);

            desiredWidth = ClampDimension(desiredWidth, minWidth, effectiveMaxWidth);
            desiredHeight = ClampDimension(desiredHeight, minHeight, effectiveMaxHeight);
            if (!float.IsPositiveInfinity(contentAvailableWidth))
                desiredWidth = Math.Min(desiredWidth, contentAvailableWidth);
            if (!float.IsPositiveInfinity(contentAvailableHeight))
                desiredHeight = Math.Min(desiredHeight, contentAvailableHeight);

            DesiredSize = new Vector2(
                Math.Max(0f, desiredWidth + marginWidth),
                Math.Max(0f, desiredHeight + marginHeight));
            ClearInvalidation(UiInvalidation.Measure);
            return DesiredSize;
        }

        /// <summary>
        /// Leaf elements have no intrinsic size unless they override measurement.
        /// Containers and content controls should measure their children/content.
        /// </summary>
        protected virtual Vector2 MeasureOverride(Vector2 availableSize)
        {
            return Vector2.Zero;
        }

        internal void ArrangeInSlot(RectangleF slot)
        {
            Vector2 desired = (PendingInvalidation & UiInvalidation.Measure) != 0
                ? Measure(slot.Size)
                : DesiredSize;
            ArrangeInSlot(slot, desired);
        }

        internal void ArrangeInSlot(RectangleF slot, Vector2 desiredSize)
        {
            Vector4 margin = Margin;
            float? explicitWidth = Width;
            float? explicitHeight = Height;
            float minWidth = MinWidth;
            float minHeight = MinHeight;
            HorizontalAlignment horizontal = HorizontalAlignment;
            VerticalAlignment vertical = VerticalAlignment;
            RectangleF contentSlot = Deflate(slot, margin);
            float marginWidth = margin.X + margin.Z;
            float marginHeight = margin.Y + margin.W;
            float desiredWidth = Math.Max(0f, desiredSize.X - marginWidth);
            float desiredHeight = Math.Max(0f, desiredSize.Y - marginHeight);

            float effectiveMaxWidth = Math.Max(minWidth, MaxWidth);
            float effectiveMaxHeight = Math.Max(minHeight, MaxHeight);
            desiredWidth = ClampDimension(
                explicitWidth.HasValue ? Math.Max(0f, explicitWidth.Value) : desiredWidth,
                minWidth,
                effectiveMaxWidth);
            desiredHeight = ClampDimension(
                explicitHeight.HasValue ? Math.Max(0f, explicitHeight.Value) : desiredHeight,
                minHeight,
                effectiveMaxHeight);

            float width = explicitWidth.HasValue
                ? Math.Min(contentSlot.Width, desiredWidth)
                : horizontal == HorizontalAlignment.Stretch
                    ? contentSlot.Width
                    : Math.Min(contentSlot.Width, desiredWidth);
            float height = explicitHeight.HasValue
                ? Math.Min(contentSlot.Height, desiredHeight)
                : vertical == VerticalAlignment.Stretch
                    ? contentSlot.Height
                    : Math.Min(contentSlot.Height, desiredHeight);

            float x = contentSlot.X;
            float y = contentSlot.Y;

            if (horizontal == HorizontalAlignment.Center || horizontal == HorizontalAlignment.Stretch)
                x += (contentSlot.Width - width) * 0.5f;
            else if (horizontal == HorizontalAlignment.Right)
                x += contentSlot.Width - width;

            if (vertical == VerticalAlignment.Center || vertical == VerticalAlignment.Stretch)
                y += (contentSlot.Height - height) * 0.5f;
            else if (vertical == VerticalAlignment.Bottom)
                y += contentSlot.Height - height;

            Arrange(new RectangleF(x, y, Math.Max(0f, width), Math.Max(0f, height)));
        }

        protected virtual void ArrangeChildren(RectangleF bounds)
        {
        }

        static float NormalizeConstraint(float value)
        {
            if (float.IsPositiveInfinity(value))
                return value;
            if (float.IsNaN(value))
                return 0f;
            return Math.Max(0f, value);
        }

        static float DeflateConstraint(float available, float amount)
        {
            if (float.IsPositiveInfinity(available))
                return available;
            return Math.Max(0f, available - amount);
        }

        static float ClampDimension(float value, float minimum, float maximum)
        {
            float max = Math.Max(minimum, maximum);
            if (value < minimum)
                return minimum;
            if (value > max)
                return max;
            return value;
        }

        static RectangleF Deflate(RectangleF bounds, Vector4 thickness)
        {
            float left = bounds.X + thickness.X;
            float top = bounds.Y + thickness.Y;
            float right = bounds.Right - thickness.Z;
            float bottom = bounds.Bottom - thickness.W;
            return new RectangleF(
                left,
                top,
                Math.Max(0f, right - left),
                Math.Max(0f, bottom - top));
        }
    }
}
