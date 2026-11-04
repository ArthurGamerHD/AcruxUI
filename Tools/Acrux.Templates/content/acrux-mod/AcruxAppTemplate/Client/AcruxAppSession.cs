using System;
using System.IO;
using Acrux.Styling;
using Acrux.Toolkit;
using LcdEyeTracking.Api;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Components;
using VRage.Utils;

namespace AcruxAppTemplate
{
    /// <summary>
    /// Connects this mod to the Acrux UI mod. No Acrux type may be touched before <see cref="IsUiReady"/>:
    /// mods load in any order, so the toolkit may announce itself after this LoadData.
    /// </summary>
    [MySessionComponentDescriptor(MyUpdateOrder.Simulation)]
    public sealed class AcruxAppSession : MySessionComponentBase
    {
        // Private mod-message channels: mod messages are global, so every mod needs its own. dotnet new picked these at
        // random; keep them unique if you copy this file into another mod.
        const long TOOLKIT_REPLY_CHANNEL = 4706349933472337921L;
        const long EYE_TRACKING_REPLY_CHANNEL = 4706349933472337922L;

        public static AcruxAppSession Instance { get; private set; }

        /// <summary>The Acrux UI API is connected; surfaces may build their views.</summary>
        public static bool IsUiReady { get; private set; }

        /// <summary>The shared gaze/click tracker, or null when unavailable (surfaces then render without input).</summary>
        public static LcdEyeTrackingApi EyeTracking { get; private set; }

        public MyObjectBuilder_Checkpoint.ModItem Mod { get; private set; }

        public override void LoadData()
        {
            Instance = this;
            Mod = ModContext.ModItem;
            if (MyAPIGateway.Utilities.IsDedicated)
                return;

            Ui.ClientName = "Acrux App Template";
            AcruxUiToolkitClient.Connect(TOOLKIT_REPLY_CHANNEL, OnUiReady);
        }

        void OnUiReady()
        {
            // Style sources are read from this mod first, then from the toolkit (its Fluent theme lives there).
            StyleSheet.SourceLoader = LoadModFile;
            StyleSheet.SourceLoadFailed = (source, error) =>
                MyLog.Default.WriteLineAndConsole("AcruxAppTemplate: skipped style '" + source + "': " + error.Message);

            EyeTracking = AcruxClient.TryGet(EYE_TRACKING_REPLY_CHANNEL);
            IsUiReady = true;
        }

        string LoadModFile(string path)
        {
            if (!MyAPIGateway.Utilities.FileExistsInModLocation(path, Mod))
                return null;
            using (TextReader reader = MyAPIGateway.Utilities.ReadFileInModLocation(path, Mod))
                return reader.ReadToEnd();
        }

        protected override void UnloadData()
        {
            if (!MyAPIGateway.Utilities.IsDedicated)
            {
                if (IsUiReady)
                {
                    StyleSheet.SourceLoader = null;
                    StyleSheet.SourceLoadFailed = null;
                }
                AcruxUiToolkitClient.Disconnect();
            }
            IsUiReady = false;
            EyeTracking = null;
            Instance = null;
        }
    }
}
