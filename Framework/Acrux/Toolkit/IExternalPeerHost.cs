namespace Acrux.Toolkit
{
    /// <summary>
    /// A toolkit object that remembers the wrapper another mod (a client) created for it, so the client
    /// gets the same wrapper back every time the object crosses the bridge. Mods cannot use weak
    /// references, so long-lived non-visual objects carry this slot themselves.
    /// </summary>
    internal interface IExternalPeerHost
    {
        object ExternalPeer { get; set; }
    }
}
