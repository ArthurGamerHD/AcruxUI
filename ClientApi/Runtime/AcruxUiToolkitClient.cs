using System;
using System.Collections.Generic;
using Sandbox.ModAPI;
using VRage.Utils;

namespace Acrux.Toolkit
{
    /// <summary>
    /// Connects this mod to Acrux UI. Call <see cref="TryConnect"/> from LoadData (or later);
    /// the toolkit answers synchronously, so the result is known when the call returns.
    /// </summary>
    public static class AcruxUiToolkitClient
    {
        /// <summary>
        /// Must match the toolkit's request channel ("ACRUXUI" + protocol 1). A toolkit with a newer protocol keeps
        /// answering this channel with the protocol 1 table, so clients built today keep working.
        /// </summary>
        public const long RequestChannel = 0x4143525558554901L;
        /// <summary>Wire protocol of this ClientApi; changes only when the bridge breaks compatibility.</summary>
        public const int ProtocolVersion = 1;

        /// <summary>Version of the loaded Acrux UI mod (for example "0.0.1"), or null before it connects.</summary>
        public static string ToolkitVersion { get; private set; }

        /// <summary>Why the last connection attempt was refused, or null.</summary>
        public static string Error { get; private set; }

        /// <summary>Requests the toolkit API on a private reply channel and loads it.</summary>
        public static bool TryConnect(long replyChannel)
        {
            if (Ui.IsLoaded)
                return true;
            if (replyChannel == RequestChannel)
                throw new ArgumentException("The reply channel must be private to this mod.", "replyChannel");

            IDictionary<string, Delegate> api = null;
            Action<object> handler = payload => api = payload as IDictionary<string, Delegate>;
            MyAPIGateway.Utilities.RegisterMessageHandler(replyChannel, handler);
            try
            {
                MyAPIGateway.Utilities.SendModMessage(RequestChannel, replyChannel);
            }
            finally
            {
                MyAPIGateway.Utilities.UnregisterMessageHandler(replyChannel, handler);
            }
            return api != null && Accept(api);
        }

        // A mismatch must not throw: the Acrux UI mod updates on its own schedule, and an exception in LoadData would
        // stop the whole world from loading instead of only this mod's UI.
        static bool Accept(IDictionary<string, Delegate> api)
        {
            Delegate entry;
            var protocol = api.TryGetValue("Toolkit.ProtocolVersion", out entry) ? entry as Func<int> : null;
            if (protocol == null || protocol() != ProtocolVersion)
            {
                Error = "Acrux UI speaks protocol " + (protocol == null ? "?" : protocol().ToString()) + ", " +
                        (Ui.ClientName ?? "this mod") + " needs protocol " + ProtocolVersion + ". Its UI is disabled.";
                MyLog.Default.WriteLineAndConsole(Error);
                return false;
            }
            var version = api.TryGetValue("Toolkit.Version", out entry) ? entry as Func<string> : null;
            ToolkitVersion = version == null ? null : version();
            Error = null;
            Ui.Load(api);
            if (Ui.Missing.Count > 0)
                MyLog.Default.WriteLineAndConsole((Ui.ClientName ?? "An Acrux UI client") + " was built against a newer Acrux UI than " +
                    (ToolkitVersion ?? "the one loaded") + ": " + Ui.Missing.Count + " members are missing (first: " +
                    Ui.Missing[0] + "). Update Acrux UI.");
            return true;
        }

        /// <summary>Sent by the toolkit when it loads after a client already asked for it.</summary>
        public const long BroadcastChannel = 0x4143525558554902L;

        static Action _onReady;
        static bool _listening;

        /// <summary>
        /// Connects now when the toolkit is loaded, or as soon as it announces itself (mods load in any order).
        /// <paramref name="onReady"/> runs once, after the toolkit API is loaded.
        /// </summary>
        public static void Connect(long replyChannel, Action onReady)
        {
            if (TryConnect(replyChannel))
            {
                if (onReady != null)
                    onReady();
                return;
            }
            _onReady = onReady;
            if (!_listening)
            {
                MyAPIGateway.Utilities.RegisterMessageHandler(BroadcastChannel, OnBroadcast);
                _listening = true;
            }
        }

        static void OnBroadcast(object payload)
        {
            var api = payload as IDictionary<string, Delegate>;
            if (api == null || Ui.IsLoaded)
                return;
            // The game is still walking this channel's handler list: unregistering now throws there and stops the
            // broadcast before it reaches the clients registered after this one.
            MyAPIGateway.Utilities.InvokeOnGameThread(StopListening);
            if (!Accept(api))
            {
                _onReady = null;
                return;
            }
            Action ready = _onReady;
            _onReady = null;
            if (ready != null)
                ready();
        }

        static void StopListening()
        {
            if (!_listening)
                return;
            MyAPIGateway.Utilities.UnregisterMessageHandler(BroadcastChannel, OnBroadcast);
            _listening = false;
        }

        public static void Disconnect()
        {
            StopListening();
            _onReady = null;
            Ui.Unload();
        }
    }
}
