using System;
using System.Collections.Generic;
using System.IO;
using Acrux.Toolkit.Server;
using Sandbox.ModAPI;
using VRage.Game.Components;
using VRage.Utils;

[assembly: Acrux.Bridge.AcruxUiToolkitExport]

namespace Acrux.Bridge
{
    /// <summary>Switches on the Acrux mirror generator for this compilation.</summary>
    [AttributeUsage(AttributeTargets.Assembly)]
    internal sealed class AcruxUiToolkitExportAttribute : Attribute
    {
    }

    /// <summary>
    /// Publishes the Acrux object model to other mods. A client sends its reply channel (long) on
    /// <see cref="RequestChannel"/> and receives the delegate table synchronously on that channel.
    /// </summary>
    [MySessionComponentDescriptor(MyUpdateOrder.NoUpdate, priority: int.MinValue + 1)]
    public sealed class AcruxUiToolkitSession : MySessionComponentBase
    {
        /// <summary>"ACRUXUI" + protocol version 1. Clients must use the same constant.</summary>
        public const long RequestChannel = 0x4143525558554901L;
        public const int ProtocolVersion = 1;
        public const long BroadcastChannel = 0x4143525558554902L;

        Dictionary<string, Delegate> _api;

        public override void LoadData()
        {
            _api = new Dictionary<string, Delegate>(StringComparer.Ordinal);
            ServerUi.Register(_api);
            AcruxUiToolkitBridge.RegisterGenerated(_api);
            _api["Toolkit.ProtocolVersion"] = new Func<int>(() => ProtocolVersion);
            _api["Toolkit.Version"] = new Func<string>(() => Generated.Constants.Version.ToString());
            ServerUi.ClientBindings.Clear();
            ServerUi.ClientBindings.Add(BindCrashScreen);
            ServerUi.InstallSourceLoader(LoadToolkitSource);
            // LCD surfaces list only the textures defined when they were created; this finds the ones defined since
            // (block icons, textures a client mod registers at run time). One lookup serves every client mod.
            Acrux.Lcd.LcdTextureResolver.IsTextureDefined = IsLcdTextureDefined;
            Acrux.Toolkit.Server.ExternalInterop.Warning = message => MyLog.Default.WriteLineAndConsole("Acrux UI: " + message);
            Acrux.Toolkit.Server.ExternalInterop.Log = message => MyLog.Default.WriteLineAndConsole(message);
            Acrux.Toolkit.Server.ExternalInterop.LoadTexturePixels = TexturePixelLoader.Load;
            Acrux.Toolkit.Server.ExternalInterop.RunInBackground = work => MyAPIGateway.Parallel.StartBackground(work);
            MyAPIGateway.Utilities.RegisterMessageHandler(RequestChannel, OnRequest);
            MyAPIGateway.Utilities.MessageEntered += OnChatMessage;
            MyLog.Default.WriteLineAndConsole("Acrux UI: " + AcruxSettings.Load());
            // Clients that loaded before the toolkit wait on the broadcast channel. A failing client must
            // not take the toolkit (and every other client) down with it.
            try
            {
                MyAPIGateway.Utilities.SendModMessage(BroadcastChannel, _api);
            }
            catch (Exception error)
            {
                MyLog.Default.WriteLineAndConsole("Acrux UI: a client failed while connecting: " + error);
            }
            MyLog.Default.WriteLineAndConsole("Acrux UI: published " + _api.Count + " members on " + RequestChannel + ".");
        }

        // The crash screen's QR texture is drawn on the client's surface, so it is paid from the client's budget.
        static void BindCrashScreen(Acrux.Toolkit.GeneratedTexturePool pool, Dictionary<string, Delegate> overrides)
        {
            overrides["Acrux.Diagnostics.CrashScreen.Show`0(global::Sandbox.ModAPI.Ingame.IMyTextSurface,global::VRageMath.RectangleF,object,object,global::System.Exception)s"] =
                new Func<Sandbox.ModAPI.Ingame.IMyTextSurface, VRageMath.RectangleF, object, object, Exception, object>(
                    (surface, viewBox, animations, report, exception) => Acrux.Diagnostics.CrashScreen.Show(
                        surface, viewBox, (Acrux.Animation.AnimationController)animations,
                        (Acrux.Diagnostics.CrashReport)report, exception, pool));
        }

        protected override void UnloadData()
        {
            ServerUi.ClientBindings.Clear();
            MyAPIGateway.Utilities.UnregisterMessageHandler(RequestChannel, OnRequest);
            MyAPIGateway.Utilities.MessageEntered -= OnChatMessage;
            Acrux.Lcd.LcdSurfaceLayers.ShowFrameRate = false;
            _api = null;
            Acrux.Lcd.LcdTextureResolver.IsTextureDefined = null;
            Acrux.Toolkit.Server.ExternalInterop.LoadTexturePixels = null;
            Acrux.Toolkit.Server.ExternalInterop.RunInBackground = null;
            Acrux.Rendering.GradientTexture.ClearMasks();
            Acrux.Rendering.SharedTextureStores.Clear();
#if DEBUG
            SurfaceCaptureCommand.Unload();
#endif
            Acrux.Cartography.CartographyModule.ReleaseShared();
            Acrux.Render3D.Render3DModule.ReleaseShared();
            Acrux.Sprites.BlockSprites.ReleaseShared();
            Acrux.Toolkit.GeneratedTexturePool.Clear();
            Acrux.Diagnostics.CrashQrCode.Release();
            Acrux.ScreenSpace.KeyboardCaptureOwners.Set.Clear();
        }

        const string COMMAND_PREFIX = "/acrux";

        static void OnChatMessage(string message, ref bool sendToOthers)
        {
            if (message == null || !message.StartsWith(COMMAND_PREFIX, StringComparison.OrdinalIgnoreCase))
                return;
            string[] words = message.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0 || !string.Equals(words[0], COMMAND_PREFIX, StringComparison.OrdinalIgnoreCase))
                return;
            sendToOthers = false;
            string command = words.Length > 1 ? words[1] : string.Empty;
            if (string.Equals(command, "fps", StringComparison.OrdinalIgnoreCase))
            {
                Acrux.Lcd.LcdSurfaceLayers.ShowFrameRate = !Acrux.Lcd.LcdSurfaceLayers.ShowFrameRate;
                MyAPIGateway.Utilities.ShowMessage("Acrux UI",
                    "Frame-rate counter " + (Acrux.Lcd.LcdSurfaceLayers.ShowFrameRate ? "on." : "off."));
                return;
            }
#if DEBUG
            if (string.Equals(command, "capture", StringComparison.OrdinalIgnoreCase))
            {
                SurfaceCaptureCommand.Run();
                return;
            }
            if (string.Equals(command, "capture-all", StringComparison.OrdinalIgnoreCase))
            {
                SurfaceCaptureCommand.RunAll();
                return;
            }
#endif
            if (string.Equals(command, "reload", StringComparison.OrdinalIgnoreCase))
            {
                MyAPIGateway.Utilities.ShowMessage("Acrux UI", "Settings: " + AcruxSettings.Load());
                return;
            }
            MyAPIGateway.Utilities.ShowMessage("Acrux UI",
                "Commands: /acrux fps (toggle the frame-rate counter), /acrux reload (re-read " +
                AcruxSettings.FILE_NAME + ")" +
#if DEBUG
                ", /acrux capture (dev build: save the looked-at screen for AcruxPreviewHost --render-capture), " +
                "/acrux capture-all (dev build: save every Acrux screen)" +
#endif
                ".");
        }

        static bool IsLcdTextureDefined(string name)
        {
            return Sandbox.Definitions.MyDefinitionManager.Static != null &&
                   Sandbox.Definitions.MyDefinitionManager.Static.GetDefinition<Sandbox.Definitions.MyLCDTextureDefinition>(
                       MyStringHash.GetOrCompute(name)) != null;
        }

        void OnRequest(object message)
        {
            if (!(message is long) || _api == null)
                return;
            long reply = (long)message;
            if (reply == RequestChannel || reply == BroadcastChannel)
                return;
            MyAPIGateway.Utilities.SendModMessage(reply, _api);
        }

        string LoadToolkitSource(string source)
        {
            using (TextReader reader = MyAPIGateway.Utilities.ReadFileInModLocation(source, ModContext.ModItem))
                return reader.ReadToEnd();
        }
    }
}
