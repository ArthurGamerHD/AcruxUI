using System;
using Acrux.Controls;
using Sandbox.ModAPI;

namespace Acrux.ScreenSpace
{
    /// <summary>
    /// The game's Options > Interface opacities as a window's platform resources: uiOpacity for controls and text,
    /// uiBackgroundOpacity for window backgrounds, as MyGuiScreenBase draws them. Every screen-space window gets
    /// them: those ScreenSpaceWindowPlatform opens, and the Window root of any ScreenSpaceRenderer tree (a mod screen
    /// hosting its own window). Read once per opening, never per frame: the game parses its config text on every
    /// read, so a change in Options applies from the next opening.
    /// </summary>
    internal static class GameUiOpacity
    {
        internal const string UiOpacityResource = "uiOpacity";
        internal const string UiBackgroundOpacityResource = "uiBackgroundOpacity";

        internal static void Publish(Window window)
        {
            if (window == null)
                return;
            float ui = 1f;
            float background = 1f;
            var config = MyAPIGateway.Session != null ? MyAPIGateway.Session.Config : null;
            if (config != null)
            {
                ui = Clamp(config.UIOpacity);
                background = Clamp(config.UIBkOpacity);
            }
            window.SetPlatformResource(UiOpacityResource, ui);
            window.SetPlatformResource(UiBackgroundOpacityResource, background);
        }

        static float Clamp(float value)
        {
            return float.IsNaN(value) ? 1f : Math.Max(0f, Math.Min(1f, value));
        }
    }
}
