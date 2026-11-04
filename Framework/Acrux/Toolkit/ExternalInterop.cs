using System;
using System.Collections.Generic;
using Acrux.Styling;

namespace Acrux.Toolkit.Server
{
    /// <summary>
    /// Hooks the Acrux UI Toolkit bridge installs so Acrux can use objects from other mods (data contexts that
    /// provide resources) and report problems without throwing.
    /// </summary>
    internal static class ExternalInterop
    {
        internal static Func<object, IResourceProvider> ResourceProviderAdapter;
        internal static Action<string> Warning;
        // Writes one line to the game log as is (the caller formats it).
        internal static Action<string> Log;

        /// <summary>"[Acrux] - Client &lt;client&gt; &lt;level&gt;: &lt;message&gt;", for a problem Acrux found in one client's content.</summary>
        internal static void LogClient(string client, string level, string message)
        {
            var log = Log;
            if (log != null)
                log("[Acrux] - Client " + (string.IsNullOrEmpty(client) ? "unknown" : client) + " " + level + ": " + message);
        }

        static readonly HashSet<string> Warned = new HashSet<string>(StringComparer.Ordinal);

        internal static IResourceProvider AsResourceProvider(object value)
        {
            if (value == null)
                return null;
            var provider = value as IResourceProvider;
            if (provider != null)
                return provider;
            var adapter = ResourceProviderAdapter;
            return adapter == null ? null : adapter(value);
        }

        internal static void WarnOnce(string message)
        {
            lock (Warned)
                if (!Warned.Add(message))
                    return;
            var warning = Warning;
            if (warning != null)
                warning(message);
        }
    }
}
