using System;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Utils;

namespace Acrux.Diagnostics
{
    /// <summary>
    /// Shared error handling for mods built on the toolkit: writes the full error to the game log under the
    /// mod's name and shows the local player a short notification asking them to send the log.
    /// </summary>
    public static class ErrorReporter
    {
        /// <summary>How long the in-game notification stays visible, in milliseconds.</summary>
        public const int NotificationMilliseconds = 10000;

        public static void LogError(string modName, Exception error, Type source)
        {
            string name = string.IsNullOrEmpty(modName) ? "Mod" : modName;
            string where = source == null ? "unknown" : source.FullName;
            try
            {
                MyLog.Default.WriteLineAndConsole("[" + name + "] " + where + ": " + error);
            }
            catch (Exception)
            {
                // Logging must never throw back into the caller's error path.
            }

            try
            {
                if (MyAPIGateway.Session != null && MyAPIGateway.Session.Player != null && MyAPIGateway.Utilities != null)
                    MyAPIGateway.Utilities.ShowNotification(
                        "[ ERROR: " + where + ": " + (error == null ? "unknown error" : error.Message) +
                        " | Send SpaceEngineers.Log to the " + name + " author ]",
                        NotificationMilliseconds,
                        MyFontEnum.Red);
            }
            catch (Exception)
            {
                // Notifications are best effort.
            }
        }
    }
}
