using VRage.Game.ModAPI;
using IMyTextSurface = Sandbox.ModAPI.Ingame.IMyTextSurface;

namespace Acrux.Render3D
{
    public static partial class SceneVisibility
    {
        // Compiled into each client: every mod keeps its own mode, one mod's choice never reaches another's screens.
        static SceneCullingMode _cullingMode = SceneCullingMode.Auto;

        /// <summary>This mod's culling mode, <see cref="SceneCullingMode.Auto"/> until it sets one.</summary>
        public static SceneCullingMode CullingMode
        {
            get { return _cullingMode; }
        }

        /// <summary>Sets which of this mod's screens <see cref="IsSurfaceVisible(IMyCubeBlock, IMyTextSurface, double)"/> culls.</summary>
        public static void SetCullingMode(SceneCullingMode mode)
        {
            _cullingMode = mode;
        }

        /// <summary>
        /// True while the screen of <paramref name="block"/> showing <paramref name="surface"/> is not culled by this
        /// mod's <see cref="CullingMode"/>; paint its 3D scene only then. <paramref name="maxDistance"/> is in metres,
        /// the camera's far plane when not positive.
        /// </summary>
        public static bool IsSurfaceVisible(IMyCubeBlock block, IMyTextSurface surface, double maxDistance = 0d)
        {
            return IsSurfaceVisible(block, surface, maxDistance, _cullingMode);
        }
    }
}
