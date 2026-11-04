using System;
using Generated;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRageMath;

namespace LcdEyeTrackingApi
{
    /// <summary>
    /// One gaze/click tracker shared by every mod with interactive LCD surfaces, so
    /// screens from different mods don't compete for the same click. The nearest
    /// registered surface along the camera ray receives the pointer, buttons and
    /// wheel; positions are in surface texture pixels.
    /// </summary>
    [ApiProvider(
        ClientNamespace = "LcdEyeTracking.Api",
        ClientName = "LcdEyeTrackingApi")]
    internal partial class EyeTrackingApiServer
    {
        static readonly Version ApiVersion = new Version(1, 0, 0);
        readonly ScreenTracker _tracker;

        public EyeTrackingApiServer(ScreenTracker tracker)
        {
            _tracker = tracker;
        }

        /// <summary>The API version.</summary>
        [ApiMethod]
        public Version GetApiVersion()
        {
            return ApiVersion;
        }

        /// <summary>
        /// Starts tracking a surface and returns its handle, or 0 when it cannot be tracked.
        /// <paramref name="moved"/> fires every frame the surface is looked at;
        /// <paramref name="left"/> when it no longer is. <paramref name="button"/> reports a
        /// <see cref="PointerButton"/> (as int) going down (true) or up (false) while the
        /// surface owns the input; <paramref name="scrolled"/> the wheel delta.
        /// <paramref name="captureLost"/> means input moved elsewhere while a button was
        /// held: cancel the press, it is not a release. Surfaces sharing a non-zero
        /// <paramref name="captureGroup"/> act as one owner, so a held button may cross
        /// between their panels.
        /// </summary>
        [ApiMethod]
        public long Register(IMyCubeBlock block, IMyTextSurface surface, Action<Vector2> moved, Action left,
            Action<int, bool> button, Action<int> scrolled, Action captureLost, long captureGroup)
        {
            return _tracker.Register(block, surface, moved, left, button, scrolled, captureLost, captureGroup);
        }

        /// <summary>Stops tracking a surface. No further callbacks are made for it.</summary>
        [ApiMethod]
        public void Unregister(long handle)
        {
            _tracker.Unregister(handle);
        }

        /// <summary>Marks the surface as live for <see cref="PointerFlags.RequiresHeartbeat"/>.</summary>
        [ApiMethod]
        public void Heartbeat(long handle)
        {
            _tracker.Heartbeat(handle);
        }

        /// <summary>Sets when the surface is tracked.</summary>
        [ApiMethod]
        public void SetFlags(long handle, PointerFlags flags)
        {
            _tracker.SetFlags(handle, flags);
        }

        /// <summary>Whether a pointer button is held this frame, wherever the player looks.</summary>
        [ApiMethod]
        public bool IsButtonHeld(PointerButton button)
        {
            return _tracker.IsButtonHeld(button);
        }
    }
}
