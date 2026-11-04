namespace Acrux.Core.Input
{
    /// <summary>
    /// Renderer-neutral cursor requested by the retained visual under the pointer. The styles are Avalonia's
    /// StandardCursorType, in its order. Inherit is Acrux's stand-in for Avalonia's unset (null) Cursor: an
    /// interactive ancestor provides the cursor. Inherit and None have fixed values so new styles never shift them.
    /// </summary>
    public enum PointerCursor : byte
    {
        Arrow,
        Ibeam,
        Wait,
        Cross,
        UpArrow,
        SizeWestEast,
        SizeNorthSouth,
        SizeAll,
        No,
        Hand,
        AppStarting,
        Help,
        TopSide,
        BottomSide,
        LeftSide,
        RightSide,
        TopLeftCorner,
        TopRightCorner,
        BottomLeftCorner,
        BottomRightCorner,
        DragMove,
        DragCopy,
        DragLink,
        Inherit = 254,
        None = 255
    }
}
