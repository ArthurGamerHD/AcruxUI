namespace Acrux.Animation
{
    /// <summary>
    /// Supplies the host animation clock used by visual trees that do not receive
    /// an explicit controller. Renderer backends may install a controller while
    /// the application or game session is active.
    /// </summary>
    public static class AnimationServices
    {
        public static AnimationController DefaultController { get; set; }
    }
}
