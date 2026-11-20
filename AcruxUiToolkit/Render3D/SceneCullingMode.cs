namespace Acrux.Render3D
{
    /// <summary>Which screens <see cref="SceneVisibility"/> reports hidden when the camera cannot see them.</summary>
    public enum SceneCullingMode : byte
    {
        /// <summary>
        /// Culls normal screens only. Transparent screens (glass, holo tables) always count as visible: what is
        /// behind them is seen through them.
        /// </summary>
        Auto = 0,

        /// <summary>Culls every screen; a transparent one is not culled for showing its back, it shows both sides.</summary>
        On = 1,

        /// <summary>Culls nothing: every screen counts as visible.</summary>
        Off = 2
    }
}
