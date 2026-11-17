using System.Collections.Generic;

namespace Acrux.ScreenSpace
{
    /// <summary>
    /// Which mods currently own the keyboard, shared by every Acrux client. A mod screen that takes the keyboard
    /// without showing the mouse cursor (typing into an LCD, a game on a panel) is invisible to other mods: the
    /// ModAPI has no screen stack, and the cursor, terminal and chat checks all pass. Such a screen calls
    /// <see cref="Acquire"/> when it opens and <see cref="Release"/> when it closes, and a global hotkey checks
    /// <see cref="IsCaptured"/> before acting. Claims are keyed by owner name, so taking one twice is harmless and
    /// one owner's release never drops another's. The toolkit drops every claim when the session unloads.
    /// </summary>
    public static class KeyboardCapture
    {
        /// <summary>Whether any mod holds a keyboard claim.</summary>
        public static bool IsCaptured
        {
            get { return KeyboardCaptureOwners.Set.Count > 0; }
        }

        /// <summary>Claims the keyboard for <paramref name="owner"/>, a name unique to the claiming mod and screen.</summary>
        public static void Acquire(string owner)
        {
            if (!string.IsNullOrEmpty(owner))
                KeyboardCaptureOwners.Set.Add(owner);
        }

        /// <summary>Drops <paramref name="owner"/>'s claim; does nothing when it holds none.</summary>
        public static void Release(string owner)
        {
            if (!string.IsNullOrEmpty(owner))
                KeyboardCaptureOwners.Set.Remove(owner);
        }

        /// <summary>Whether <paramref name="owner"/> holds a claim.</summary>
        public static bool IsCapturedBy(string owner)
        {
            return !string.IsNullOrEmpty(owner) && KeyboardCaptureOwners.Set.Contains(owner);
        }
    }

    /// <summary>
    /// The claims behind <see cref="KeyboardCapture"/>, kept in an internal type so the client mirror does not
    /// expose clearing them: only the toolkit session drops every claim, when it unloads.
    /// </summary>
    internal static class KeyboardCaptureOwners
    {
        internal static readonly HashSet<string> Set = new HashSet<string>();
    }
}
