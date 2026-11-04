namespace Acrux.Render3D
{
    /// <summary>How a <see cref="GridSceneRenderer"/> draws the blocks it highlights (damaged, missing).</summary>
    public enum GridHighlightStyle : byte
    {
        /// <summary>The block in the highlight color, flat, without its texture's detail.</summary>
        Solid,
        /// <summary>The block's model painted in the highlight color, keeping its texture's light and dark.</summary>
        Tint,
        /// <summary>The block's box outlined in the highlight color where it can be seen.</summary>
        Wireframe,
        /// <summary>The block's box outlined in the highlight color over everything, seen through the hull.</summary>
        SeeThroughWireframe,
        /// <summary>
        /// The block's own model outlined in the highlight color (its edges, not its box) where it can be seen. A
        /// block too small on screen, or whose model has not loaded yet, is outlined by its box.
        /// </summary>
        ModelWireframe,
        /// <summary>The block in the highlight color exactly, unlit and without its texture: an overlay.</summary>
        Overlay
    }
}
