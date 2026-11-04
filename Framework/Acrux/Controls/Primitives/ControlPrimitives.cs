using System;

namespace Acrux.Controls.Primitives
{
    public enum ClickMode
    {
        Release,
        Press,
        Hover
    }

    public enum ScrollBarVisibility
    {
        Disabled,
        Auto,
        Hidden,
        Visible
    }

    public enum ScrollEventType
    {
        EndScroll,
        First,
        LargeDecrement,
        LargeIncrement,
        Last,
        SmallDecrement,
        SmallIncrement,
        ThumbPosition,
        ThumbTrack
    }

    public sealed class ScrollEventArgs
    {
        public ScrollEventArgs(ScrollEventType type, float newValue)
        {
            Type = type;
            NewValue = newValue;
        }

        public ScrollEventType Type { get; }
        public float NewValue { get; }
    }

    public enum SnapPointsType
    {
        None,
        Mandatory,
        MandatorySingle
    }

    public enum SnapPointsAlignment
    {
        Near,
        Center,
        Far
    }

    public enum TickPlacement
    {
        None,
        TopLeft,
        BottomRight,
        Outside
    }

    public enum PlacementMode
    {
        Pointer,
        Bottom,
        Right,
        Left,
        Top,
        Center,
        AnchorAndGravity,
        Custom,
        BottomEdgeAlignedLeft,
        BottomEdgeAlignedRight,
        TopEdgeAlignedLeft,
        TopEdgeAlignedRight,
        LeftEdgeAlignedTop,
        LeftEdgeAlignedBottom,
        RightEdgeAlignedTop,
        RightEdgeAlignedBottom
    }

    [Flags]
    public enum PopupAnchor
    {
        None = 0,
        Top = 1,
        Bottom = 2,
        Left = 4,
        Right = 8,
        HorizontalCenter = 16,
        VerticalCenter = 32
    }

    [Flags]
    public enum PopupGravity
    {
        None = 0,
        Top = 1,
        Bottom = 2,
        Left = 4,
        Right = 8
    }

    [Flags]
    public enum PopupPositionerConstraintAdjustment
    {
        None = 0,
        FlipX = 1,
        FlipY = 2,
        SlideX = 4,
        SlideY = 8,
        ResizeX = 16,
        ResizeY = 32,
        All = FlipX | FlipY | SlideX | SlideY | ResizeX | ResizeY
    }
}
