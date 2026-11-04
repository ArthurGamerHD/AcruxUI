namespace Acrux.Toolkit.Server
{
    /// <summary>
    /// Implemented by toolkit objects that back a subclass defined in another mod (a client).
    /// <see cref="RemotePeer"/> is the client's wrapper instance; it is opaque to the toolkit.
    /// </summary>
    public interface IRemotePeer
    {
        object RemotePeer { get; }
    }
}
