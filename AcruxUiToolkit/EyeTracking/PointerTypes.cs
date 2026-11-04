using System;

namespace LcdEyeTrackingApi
{
    /// <summary>Pointer buttons, in the order the button callback reports them.</summary>
    public enum PointerButton
    {
        Primary = 0,
        Secondary = 1,
        Middle = 2,
        Back = 3,
        Forward = 4
    }

    [Flags]
    public enum PointerFlags
    {
        None = 0,
        /// <summary>Only tracked while the look-around key (Alt) is held.</summary>
        RequiresAlt = 1,
        /// <summary>Only tracked while Heartbeat was called in the last 30 frames.</summary>
        RequiresHeartbeat = 2
    }
}
