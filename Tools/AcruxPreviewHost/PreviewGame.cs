using System;
using System.Reflection;
using VRage.Game.ModAPI;

namespace AcruxPreview
{
    /// <summary>
    /// Stand-ins for the game state mod controls read while they draw, so they render in the preview as on an idle
    /// LCD: a session (MyAPIGateway.Session) whose gameplay frame counter follows the preview's frames, and whose
    /// other members answer their defaults; and in-memory generated textures (MyAPIGateway.GeneratedTextures).
    /// </summary>
    public static class PreviewGame
    {
        static bool _installed;

        /// <summary>The preview's frame number (60 per second), as the game's GameplayFrameCounter.</summary>
        public static long Frame { get; set; }

        public static void Install()
        {
            if (_installed)
                return;
            _installed = true;
            try
            {
                if (Sandbox.ModAPI.MyAPIGateway.Session == null)
                    Sandbox.ModAPI.MyAPIGateway.Session = DispatchProxy.Create<IMySession, DefaultsProxy>();
                // Dynamic textures (rounded borders, arcs, paths, painted mod controls) draw into these.
                if (Sandbox.ModAPI.MyAPIGateway.GeneratedTextures == null)
                    Sandbox.ModAPI.MyAPIGateway.GeneratedTextures = new PreviewGeneratedTextures();
            }
            catch (Exception error)
            {
                Program.Log("no stand-in game session: " + error.Message);
            }
        }

        public class DefaultsProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo method, object[] args)
            {
                if (method.Name == "get_GameplayFrameCounter")
                    return (int)Frame;
                if (method.ReturnType == typeof(void))
                    return null;
                return method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
            }
        }
    }
}
