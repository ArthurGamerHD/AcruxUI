using Sandbox.ModAPI;
using VRage.Game.ModAPI;

namespace LcdEyeTrackingApi.Geometry
{
    /// <summary>What screen-area hit testing needs to know about a surface.</summary>
    internal interface ITrackedSurface
    {
        IMyCubeBlock Block { get; }
        IMyTextSurface Surface { get; }

        // LCD panels: selected rotation index; other providers: surface index; -1 if unknown.
        int RotationOrSurfaceIndex { get; }
    }

    internal static class TrackedSurfaceExtensions
    {
        public static string Describe(this ITrackedSurface screen)
        {
            if (screen == null || screen.Block == null)
                return "<null>";
            string subtype = screen.Block.BlockDefinition.SubtypeName;
            return "block=" + (string.IsNullOrWhiteSpace(subtype) ? "<no subtype>" : subtype) +
                   ", entityId=" + screen.Block.EntityId +
                   ", surfaceIndex=" + screen.RotationOrSurfaceIndex;
        }
    }
}
