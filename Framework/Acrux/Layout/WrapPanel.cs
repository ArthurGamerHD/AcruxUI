using System;
using System.Collections.Generic;
using Acrux.Core;
using Acrux.Core.Invalidation;
using Acrux.Core.PropertySystem;
using VRageMath;

namespace Acrux.Layout
{
    /// <summary>WrapPanel line alignment.</summary>
    public enum WrapPanelItemsAlignment
    {
        Start,
        Center,
        End,
        Justify,
        Stretch,
        StretchAll
    }

    /// <summary>
    /// Port of WrapPanel: places children consecutively and starts a new line when
    /// the available space on the primary axis is exhausted.
    /// </summary>
    public sealed class WrapPanel : Panel
    {
        const float Epsilon = 0.0001f;

        protected override void DrawSelf(Acrux.Rendering.RenderContext context) { DrawBackground(context); }
        static WrapPanel() { }

        public static readonly StyledProperty<float> ItemSpacingProperty =
            StyledProperty.Register<WrapPanel, float>(nameof(ItemSpacing), 0f, UiInvalidation.Measure);
        public static readonly StyledProperty<float> LineSpacingProperty =
            StyledProperty.Register<WrapPanel, float>(nameof(LineSpacing), 0f, UiInvalidation.Measure);
        public static readonly StyledProperty<Orientation> OrientationProperty =
            StyledProperty.Register<WrapPanel, Orientation>(
                nameof(Orientation), Orientation.Horizontal, UiInvalidation.Measure);
        public static readonly StyledProperty<WrapPanelItemsAlignment> ItemsAlignmentProperty =
            StyledProperty.Register<WrapPanel, WrapPanelItemsAlignment>(
                nameof(ItemsAlignment), WrapPanelItemsAlignment.Start, UiInvalidation.Arrange);
        public static readonly StyledProperty<float> ItemWidthProperty =
            StyledProperty.Register<WrapPanel, float>(nameof(ItemWidth), float.NaN, UiInvalidation.Measure);
        public static readonly StyledProperty<float> ItemHeightProperty =
            StyledProperty.Register<WrapPanel, float>(nameof(ItemHeight), float.NaN, UiInvalidation.Measure);

        public float ItemSpacing
        {
            get { return GetValue(ItemSpacingProperty); }
            set { SetValue(ItemSpacingProperty, value); }
        }

        public float LineSpacing
        {
            get { return GetValue(LineSpacingProperty); }
            set { SetValue(LineSpacingProperty, value); }
        }

        public Orientation Orientation
        {
            get { return GetValue(OrientationProperty); }
            set { SetValue(OrientationProperty, value); }
        }

        public WrapPanelItemsAlignment ItemsAlignment
        {
            get { return GetValue(ItemsAlignmentProperty); }
            set { SetValue(ItemsAlignmentProperty, value); }
        }

        public float ItemWidth
        {
            get { return GetValue(ItemWidthProperty); }
            set { SetValue(ItemWidthProperty, value); }
        }

        public float ItemHeight
        {
            get { return GetValue(ItemHeightProperty); }
            set { SetValue(ItemHeightProperty, value); }
        }

        protected override Vector2 MeasureOverride(Vector2 constraint)
        {
            bool horizontal = Orientation == Orientation.Horizontal;
            float itemWidth = ItemWidth;
            float itemHeight = ItemHeight;
            bool itemWidthSet = !float.IsNaN(itemWidth);
            bool itemHeightSet = !float.IsNaN(itemHeight);
            float limitU = horizontal ? constraint.X : constraint.Y;
            WrapPanelItemsAlignment alignment = float.IsPositiveInfinity(limitU)
                ? WrapPanelItemsAlignment.Start
                : ItemsAlignment;

            float lineU = 0f, lineV = 0f, panelU = 0f, panelV = 0f;
            bool itemExists = false, lineExists = false;
            if (alignment == WrapPanelItemsAlignment.Justify || alignment == WrapPanelItemsAlignment.StretchAll)
                panelU = limitU;

            var childConstraint = new Vector2(itemWidthSet ? itemWidth : constraint.X,
                itemHeightSet ? itemHeight : constraint.Y);
            IReadOnlyList<VisualElement> children = VisualChildren;
            for (int i = 0; i < children.Count; i++)
            {
                VisualElement child = children[i];
                if (!child.Visible)
                    continue;
                Vector2 desired = child.Measure(childConstraint);
                float childU = horizontal ? (itemWidthSet ? itemWidth : desired.X) : (itemHeightSet ? itemHeight : desired.Y);
                float childV = horizontal ? (itemHeightSet ? itemHeight : desired.Y) : (itemWidthSet ? itemWidth : desired.X);
                float spacing = itemExists ? ItemSpacing : 0f;
                if (lineU + childU + spacing > limitU + Epsilon)
                {
                    panelU = Math.Max(lineU, panelU);
                    panelV += lineV + (lineExists ? LineSpacing : 0f);
                    lineU = childU;
                    lineV = childV;
                    lineExists = true;
                }
                else
                {
                    lineU += childU + spacing;
                    lineV = Math.Max(childV, lineV);
                }
                itemExists = true;
            }

            if (lineExists && alignment == WrapPanelItemsAlignment.Stretch)
                panelU = limitU;
            panelU = Math.Max(lineU, panelU);
            panelV += lineV + (lineExists ? LineSpacing : 0f);
            return horizontal ? new Vector2(panelU, panelV) : new Vector2(panelV, panelU);
        }

        protected override void ArrangeChildren(RectangleF bounds)
        {
            bool horizontal = Orientation == Orientation.Horizontal;
            float itemWidth = ItemWidth;
            float itemHeight = ItemHeight;
            bool itemWidthSet = !float.IsNaN(itemWidth);
            bool itemHeightSet = !float.IsNaN(itemHeight);
            float finalU = horizontal ? bounds.Width : bounds.Height;
            WrapPanelItemsAlignment alignment = float.IsPositiveInfinity(finalU)
                ? WrapPanelItemsAlignment.Start
                : ItemsAlignment;
            IReadOnlyList<VisualElement> children = VisualChildren;

            float accumulatedV = 0f, lineU = 0f, lineV = 0f;
            bool itemExists = false, lineExists = false;
            int firstInLine = 0;
            for (int i = 0; i < children.Count; i++)
            {
                VisualElement child = children[i];
                if (!child.Visible)
                    continue;
                float childU = ChildU(child, horizontal, itemWidthSet, itemWidth, itemHeightSet, itemHeight);
                float childV = horizontal
                    ? (itemHeightSet ? itemHeight : child.DesiredSize.Y)
                    : (itemWidthSet ? itemWidth : child.DesiredSize.X);
                float spacing = itemExists ? ItemSpacing : 0f;
                if (lineU + childU + spacing > finalU + Epsilon)
                {
                    accumulatedV += lineExists ? LineSpacing : 0f;
                    ArrangeLine(bounds, alignment, lineV, accumulatedV, firstInLine, i, false);
                    accumulatedV += lineV;
                    lineU = childU;
                    lineV = childV;
                    firstInLine = i;
                    lineExists = true;
                }
                else
                {
                    lineU += childU + spacing;
                    lineV = Math.Max(childV, lineV);
                }
                itemExists = true;
            }

            if (itemExists)
            {
                accumulatedV += lineExists ? LineSpacing : 0f;
                ArrangeLine(bounds, alignment, lineV, accumulatedV, firstInLine, children.Count, true);
            }
        }

        void ArrangeLine(RectangleF bounds, WrapPanelItemsAlignment alignment, float lineV, float v,
            int start, int end, bool lastLine)
        {
            bool horizontal = Orientation == Orientation.Horizontal;
            float itemWidth = ItemWidth;
            float itemHeight = ItemHeight;
            bool itemWidthSet = !float.IsNaN(itemWidth);
            bool itemHeightSet = !float.IsNaN(itemHeight);
            float finalU = horizontal ? bounds.Width : bounds.Height;
            IReadOnlyList<VisualElement> children = VisualChildren;
            if (alignment == WrapPanelItemsAlignment.Stretch && lastLine)
                alignment = WrapPanelItemsAlignment.Start;

            float spacing = ItemSpacing;
            float totalU = 0f;
            int spacingCount = -1;
            for (int i = start; i < end; i++)
            {
                if (!children[i].Visible)
                    continue;
                totalU += ChildU(children[i], horizontal, itemWidthSet, itemWidth, itemHeightSet, itemHeight);
                spacingCount++;
            }

            float u = 0f;
            float stretchRatio = 1f;
            switch (alignment)
            {
                case WrapPanelItemsAlignment.Center:
                    u = (finalU - totalU - spacing * spacingCount) / 2f;
                    break;
                case WrapPanelItemsAlignment.End:
                    u = finalU - totalU - spacing * spacingCount;
                    break;
                case WrapPanelItemsAlignment.Justify:
                    if (spacingCount > 0)
                        spacing = Math.Max(finalU - totalU, 0f) / spacingCount;
                    break;
                case WrapPanelItemsAlignment.Stretch:
                case WrapPanelItemsAlignment.StretchAll:
                    if (Math.Abs(totalU) > Epsilon)
                        stretchRatio = Math.Max(finalU - spacing * spacingCount, 0f) / totalU;
                    break;
            }

            for (int i = start; i < end; i++)
            {
                VisualElement child = children[i];
                if (!child.Visible)
                    continue;
                float slotU = ChildU(child, horizontal, itemWidthSet, itemWidth, itemHeightSet, itemHeight) * stretchRatio;
                RectangleF slot = horizontal
                    ? new RectangleF(bounds.X + u, bounds.Y + v, slotU, lineV)
                    : new RectangleF(bounds.X + v, bounds.Y + u, lineV, slotU);
                child.ArrangeInSlot(slot, child.DesiredSize);
                u += slotU + spacing;
            }
        }

        static float ChildU(VisualElement child, bool horizontal, bool itemWidthSet, float itemWidth,
            bool itemHeightSet, float itemHeight)
        {
            return horizontal
                ? (itemWidthSet ? itemWidth : child.DesiredSize.X)
                : (itemHeightSet ? itemHeight : child.DesiredSize.Y);
        }
    }
}
