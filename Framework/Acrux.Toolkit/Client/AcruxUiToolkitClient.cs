using System;
using System.Collections.Generic;
using System.IO;
using Sandbox.ModAPI;
using VRage;
using VRage.Game;
using VRage.Game.ModAPI;
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
            _crashScheduled = false;
            _modContext = null;
        }

        /// <summary>Workshop page of the Acrux UI Toolkit mod.</summary>
        public const string ToolkitWorkshopUrl = "https://steamcommunity.com/sharedfiles/filedetails/?id=3841029557";

        static IMyModContext _modContext;

        /// <summary>
        /// The context of the mod this ClientApi copy is compiled into; null outside a session (or outside the game).
        /// </summary>
        public static IMyModContext ModContext
        {
            get
            {
                if (_modContext == null)
                    _modContext = FindModContext();
                return _modContext;
            }
        }

        // ModScopeName is the calling assembly's name, and this code is compiled into the client mod's own assembly. The
        // game names a mod's script assemblies "[Service_]ModId_<Data/Scripts folder>" (Steam omits the service), with
        // ModId the mod item's Name, so the mod is the one whose prefix matches (longest wins if names nest).
        static IMyModContext FindModContext()
        {
            if (MyAPIGateway.Utilities == null || MyAPIGateway.Utilities.GamePaths == null || MyAPIGateway.Session == null ||
                MyAPIGateway.Session.Mods == null)
                return null;
            string scope = MyAPIGateway.Utilities.GamePaths.ModScopeName;
            if (string.IsNullOrEmpty(scope))
                return null;
            var best = default(MyObjectBuilder_Checkpoint.ModItem);
            int bestLength = -1;
            foreach (var mod in MyAPIGateway.Session.Mods)
            {
                string service = mod.PublishedServiceName;
                string prefix = (string.IsNullOrEmpty(service) || string.Equals(service, "steam", StringComparison.OrdinalIgnoreCase)
                    ? "" : service + "_") + mod.Name + "_";
                if (prefix.Length > bestLength && scope.StartsWith(prefix, StringComparison.Ordinal))
                {
                    best = mod;
                    bestLength = prefix.Length;
                }
            }
            return bestLength < 0 ? null : new ModItemContext(best);
        }

        /// <summary>A mod context built from its mod item, as the game builds its own.</summary>
        sealed class ModItemContext : IMyModContext
        {
            public ModItemContext(MyObjectBuilder_Checkpoint.ModItem mod)
            {
                ModItem = mod;
                ModName = mod.FriendlyName;
                ModId = mod.Name;
                ModServiceName = mod.PublishedServiceName;
                ModPath = mod.GetPath();
                ModPathData = Path.Combine(ModPath, "Data");
            }

            public string ModName { get; private set; }
            public string ModId { get; private set; }
            public string ModServiceName { get; private set; }
            public string ModPath { get; private set; }
            public string ModPathData { get; private set; }
            public bool IsBaseGame { get { return false; } }
            public MyObjectBuilder_Checkpoint.ModItem ModItem { get; private set; }
        }

        static bool _crashScheduled;
        static ModCrashedException _pendingCrash;
        // Replacement for the dialog's text (MyCommonTexts.ModCrashedTheGameInfo), or null to keep the game's.
        static string _pendingDialogText;
        // MyAPIGateway.Utilities is cleared when the session unloads, but the object keeps queueing on the game thread.
        static IMyUtilities _utilities;

        // Next game-thread frame; also arms the menu fallback below. A failed world load unloads the session first, and
        // MySession.Unload drains this queue (frame counter at int.MaxValue) inside a catch: throwing there aborts the
        // unload halfway and the game later dies on the half-unloaded world with an unrelated error. So during unload the
        // crash waits for the menu instead.
        static void ThrowPendingCrashInSession()
        {
            if (_pendingCrash != null && MyAPIGateway.Gui != null)
                MyAPIGateway.Gui.GuiControlCreated += ThrowPendingCrashAfterUnload;
            var session = MyAPIGateway.Session;
            if (_pendingCrash == null || session == null || session.GameplayFrameCounter == int.MaxValue)
                return;
            ThrowPendingCrash();
        }

        // The game never clears GUI subscriptions on unload, so the first screen of the menu (MyAPIGateway.Session is null
        // by then) tells that the session unloaded cleanly. Not thrown here: menu screens are created by reflection, and
        // the game only shows its mod crash dialog when the exception it gets is the ModCrashedException itself, not a
        // TargetInvocationException around it. The game thread's invoke queue runs outside any catch and wrapper.
        static void ThrowPendingCrashAfterUnload(object screen)
        {
            if (_pendingCrash == null || MyAPIGateway.Session != null)
                return;
            if (MyAPIGateway.Gui != null)
                MyAPIGateway.Gui.GuiControlCreated -= ThrowPendingCrashAfterUnload;
            if (_utilities != null)
                _utilities.InvokeOnGameThread(ThrowPendingCrash, "AcruxUiToolkitClient");
            else
                ThrowPendingCrash();
        }

        static void ThrowPendingCrash()
        {
            ModCrashedException crash = _pendingCrash;
            if (crash == null)
                return;
            _pendingCrash = null;
            if (MyAPIGateway.Gui != null)
                MyAPIGateway.Gui.GuiControlCreated -= ThrowPendingCrashAfterUnload;
            if (_pendingDialogText != null)
                OverrideDialogText(_pendingDialogText);
            _pendingDialogText = null;
            _utilities = null;
            throw crash;
        }

        // The game formats its mod crash dialog from the localized ModCrashedTheGameInfo text ({0} mod name, {1} service,
        // {2} mod id, {3} "log", whose last three characters link to the log file). Mods cannot set a text directly, but
        // MyTexts.LoadTexts overwrites texts from .resx files on disk, so the text is written to this mod's local storage
        // and loaded from there (the override culture loads only MyTexts.override.resx). Done right before the crash: it
        // replaces the text for every mod until the game restarts.
        static void OverrideDialogText(string text)
        {
            try
            {
                IMyUtilities utilities = _utilities ?? MyAPIGateway.Utilities;
                if (utilities == null || utilities.GamePaths == null)
                    return;
                const string file = "MyTexts.override.resx";
                using (TextWriter writer = utilities.WriteFileInLocalStorage(file, typeof(AcruxUiToolkitClient)))
                {
                    writer.Write("<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<root>\r\n" +
                        "  <resheader name=\"resmimetype\"><value>text/microsoft-resx</value></resheader>\r\n" +
                        "  <resheader name=\"version\"><value>2.0</value></resheader>\r\n" +
                        "  <resheader name=\"reader\"><value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>\r\n" +
                        "  <resheader name=\"writer\"><value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>\r\n" +
                        "  <data name=\"ModCrashedTheGameInfo\" xml:space=\"preserve\">\r\n    <value>" +
                        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;") +
                        "</value>\r\n  </data>\r\n</root>\r\n");
                }
                // WriteFileInLocalStorage writes to <user data>/Storage/<this assembly's scope name>/.
                string folder = Path.Combine(utilities.GamePaths.UserDataPath, "Storage", utilities.GamePaths.ModScopeName);
                MyTexts.LoadTexts(folder, "override", null);
            }
            catch (Exception e)
            {
                MyLog.Default.WriteLineAndConsole("Acrux UI: could not replace the crash dialog text: " + e.Message);
            }
        }

        /// <summary>
        /// Crashes the game with Space Engineers' "mod crashed the game" dialog naming this mod (see <see cref="ModContext"/>)
        /// instead of the generic crash report.
        /// </summary>
        /// <param name="innerException">The actual error; it is written to the game log.</param>
        /// <param name="schedule">
        /// Also queue the crash on the game thread. The dialog only appears for an exception nothing catches, and the game
        /// catches exceptions in many places (LCD scripts are created inside entity init, which logs and swallows them), so
        /// pass true from constructors, loading code and other threads. Only one crash is queued per session; if the
        /// session unloads first (a failed world load), it is raised once the game is back at the main menu.
        /// </param>
        public static void Crash(Exception innerException, bool schedule)
        {
            if (innerException == null)
                throw new ArgumentNullException("innerException");
            string name = DescribeClient();
            // The dialog never wraps and continues {0} with " (ModId: {1}:{2})": after a URL that would run off the
            // window, so it gets a line of its own.
            if (name.Contains("\r\n"))
                name += "\r\n";
            throw CreateCrash(innerException, name, null, schedule);
        }

        // Name of this mod for messages, with its workshop page on the next line when it is published on Steam.
        static string DescribeClient()
        {
            IMyModContext mod = ModContext;
            string name = mod != null ? mod.ModName : (Ui.ClientName ?? "An Acrux UI client");
            if (mod == null || mod.ModItem.PublishedFileId == 0 ||
                !string.Equals(mod.ModItem.PublishedServiceName, "steam", StringComparison.OrdinalIgnoreCase))
                return name;
            return name + "\r\nhttps://steamcommunity.com/sharedfiles/filedetails/?id=" + mod.ModItem.PublishedFileId;
        }

        static ModCrashedException CreateCrash(Exception innerException, string dialogName, string dialogText, bool schedule)
        {
            var crash = new ModCrashedException(innerException, new CrashContext(ModContext, dialogName));
            if (schedule && !_crashScheduled)
            {
                _crashScheduled = true;
                MyLog.Default.WriteLineAndConsole(crash.ModContext.ModName.Replace("\r\n", " ").TrimEnd() + " crashed: " + innerException);
                _pendingCrash = crash;
                _pendingDialogText = dialogText;
                _utilities = MyAPIGateway.Utilities;
                // Wrappers are often built on worker threads (parallel entity loading): everything else runs on the game thread.
                if (MyAPIGateway.Utilities != null)
                    MyAPIGateway.Utilities.InvokeOnGameThread(ThrowPendingCrashInSession, "AcruxUiToolkitClient");
            }
            return crash;
        }

        // Called by every wrapper constructor while the toolkit is not loaded: the mod used Acrux UI without it.
        internal static void ThrowNotLoaded(string typeName)
        {
            IMyModContext mod = ModContext;
            string name = mod != null ? mod.ModName : (Ui.ClientName ?? "An Acrux UI client");
            bool workshop = mod != null && mod.ModItem.PublishedFileId != 0;

            // One line each: the dialog is a fixed-size window that never wraps (about 70 characters a line).
            string text;
            if (Error != null)
                text = "Mod {0} called Acrux API with an incompatible Toolkit Mod\r\n" +
                       "Update Acrux UI Toolkit and this mod to their latest versions\r\n" +
                       ToolkitWorkshopUrl + "\r\n";
            else
                text = "Mod {0} called Acrux API without the Toolkit Mod\r\n" +
                       "Acrux UI Toolkit is required to display UI\r\n" +
                       ToolkitWorkshopUrl + "\r\n\r\n" +
                       (workshop
                           ? "Please contact the mod author and ask him/her to add\r\nAcrux UI Toolkit as dependency on Steam\r\n"
                           : "Please add the Acrux UI Toolkit to the mod list\r\n");
            text += "\r\nYou can find more info in the {3}";

            var error = new InvalidOperationException("Mod " + name + " called Acrux API (" + typeName +
                ") without the Toolkit Mod. " + (Error ?? "The Acrux UI Toolkit mod is not loaded in this world.") +
                " Acrux UI Toolkit: " + ToolkitWorkshopUrl);
            throw CreateCrash(error, name, text, true);
        }

        /// <summary>
        /// The mod named by the crash dialog ({0} in its text): this mod's name as given, its real service and id.
        /// </summary>
        sealed class CrashContext : IMyModContext
        {
            readonly IMyModContext _mod;

            public CrashContext(IMyModContext mod, string dialogName)
            {
                _mod = mod;
                ModName = dialogName;
            }

            public string ModName { get; private set; }
            public string ModId { get { return _mod != null ? _mod.ModId : "unknown"; } }
            public string ModServiceName { get { return _mod != null ? _mod.ModServiceName : "Steam"; } }
            public string ModPath { get { return _mod != null ? _mod.ModPath : string.Empty; } }
            public string ModPathData { get { return _mod != null ? _mod.ModPathData : string.Empty; } }
            public bool IsBaseGame { get { return false; } }
            public MyObjectBuilder_Checkpoint.ModItem ModItem { get { return _mod != null ? _mod.ModItem : default(MyObjectBuilder_Checkpoint.ModItem); } }
        }
    }
}
