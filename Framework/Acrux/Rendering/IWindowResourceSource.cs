namespace Acrux.Rendering
{
    /// <summary>
    /// A renderer that supplies platform resources to the window at the root of the trees it draws (the screen-space
    /// renderer gives the game's UI opacities), for hosts that draw a Window in their own tree instead of opening it
    /// through a window platform.
    /// </summary>
    internal interface IWindowResourceSource
    {
        void PublishWindowResources(Acrux.Controls.Window window);
    }
}
