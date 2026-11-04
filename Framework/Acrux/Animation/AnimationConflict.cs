namespace Acrux.Animation
{
    public enum AnimationConflict
    {
        // Permit multiple animations on the same owner and channel.
        Allow,

        // Cancel an existing animation on the same owner and channel.
        Replace,

        // Keep the existing animation and return its handle.
        Ignore
    }
}
