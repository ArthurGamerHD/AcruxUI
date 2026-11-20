using LcdEyeTrackingApi;
using LcdEyeTrackingApi.Geometry;
using VRage.Game.ModAPI;
using IMyTextSurface = Sandbox.ModAPI.Ingame.IMyTextSurface;

namespace Acrux.Render3D
{
    /// <summary>
    /// Whether the local player can see a screen, so a client skips the frames of 3D scenes (and maps) nobody
    /// looks at: ask before <see cref="GridSceneRenderer.RequestRender"/> and paint only while it holds. Game
    /// thread only. With culling, always false on a dedicated server, which has no camera.
    /// </summary>
    public static partial class SceneVisibility
    {
        sealed class Probe : ITrackedSurface
        {
            public IMyCubeBlock Block { get; set; }
            public Sandbox.ModAPI.IMyTextSurface Surface { get; set; }
            public int RotationOrSurfaceIndex { get; set; }
        }

        // Reused across calls: the game thread is the only caller.
        static readonly Probe Shared = new Probe();

        /// <summary>
        /// True while the screen of <paramref name="block"/> showing <paramref name="surface"/> is not culled by
        /// <paramref name="mode"/>: it faces the camera, is within <paramref name="maxDistance"/> metres of it (the
        /// camera's far plane when not positive) and inside its view. It does not test what stands in between, nor
        /// whether the block is powered. Without a surface, or for a model whose screen area is not found, the
        /// block's bounds are tested instead. A closed block is never visible. Clients call the overload that uses
        /// their own <c>SetCullingMode</c>.
        /// </summary>
        public static bool IsSurfaceVisible(IMyCubeBlock block, IMyTextSurface surface, double maxDistance,
            SceneCullingMode mode)
        {
            if (block == null || block.Closed || block.MarkedForClose || !block.InScene)
                return false;
            if (mode == SceneCullingMode.Off)
                return true;

            int index = surface == null ? -1 : ScreenTracker.ResolveSurfaceIndex(block, surface);
            bool transparent = index >= 0 && ScreenAreaGeometry.IsTransparentScreenArea(block, index);
            if (transparent && mode == SceneCullingMode.Auto)
                return true;

            Shared.Block = block;
            Shared.Surface = surface as Sandbox.ModAPI.IMyTextSurface;
            Shared.RotationOrSurfaceIndex = index;
            try
            {
                return ScreenAreaGeometry.IsScreenAreaVisibleToCamera(Shared, maxDistance, !transparent);
            }
            finally
            {
                Shared.Block = null;
                Shared.Surface = null;
            }
        }
    }
}
