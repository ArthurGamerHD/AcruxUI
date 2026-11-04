using System;
using System.IO;
using Sandbox.ModAPI;
using VRage.Utils;

namespace Acrux.Bridge
{
    /// <summary>
    /// The toolkit's user settings, read from AcruxSettings.xml in the toolkit's local storage (per player,
    /// shared by all worlds). A missing file is written with the defaults so it can be edited; a missing or
    /// unreadable field keeps its default. "/acrux reload" reads the file again.
    /// </summary>
    public sealed class AcruxSettings
    {
        public const string FILE_NAME = "AcruxSettings.xml";

        public Acrux.Render3D.Render3DSettings Render3D = new Acrux.Render3D.Render3DSettings();

        static AcruxSettings _current = new AcruxSettings();

        /// <summary>The settings in effect (defaults until <see cref="Load"/> succeeds).</summary>
        public static AcruxSettings Current
        {
            get { return _current; }
        }

        /// <summary>Reads the file, writing the defaults first when there is none. Returns a status line.</summary>
        public static string Load()
        {
            Type owner = typeof(AcruxSettings);
            try
            {
                if (!MyAPIGateway.Utilities.FileExistsInLocalStorage(FILE_NAME, owner))
                {
                    _current = new AcruxSettings();
                    Save(_current);
                    return "wrote the default " + FILE_NAME + ".";
                }
                string text;
                using (TextReader reader = MyAPIGateway.Utilities.ReadFileInLocalStorage(FILE_NAME, owner))
                    text = reader.ReadToEnd();
                var loaded = MyAPIGateway.Utilities.SerializeFromXML<AcruxSettings>(text) ?? new AcruxSettings();
                if (loaded.Render3D == null)
                    loaded.Render3D = new Acrux.Render3D.Render3DSettings();
                loaded.Render3D.Clamp();
                _current = loaded;
                return "loaded " + FILE_NAME + ".";
            }
            catch (Exception error)
            {
                MyLog.Default.WriteLineAndConsole("Acrux UI: could not read " + FILE_NAME + ", using defaults: " +
                                                  error.Message);
                _current = new AcruxSettings();
                return "could not read " + FILE_NAME + " (" + error.Message + "); using defaults.";
            }
        }

        static void Save(AcruxSettings settings)
        {
            string text = MyAPIGateway.Utilities.SerializeToXML(settings);
            using (TextWriter writer = MyAPIGateway.Utilities.WriteFileInLocalStorage(FILE_NAME, typeof(AcruxSettings)))
                writer.Write(text);
        }
    }
}
