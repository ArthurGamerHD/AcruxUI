using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml.Linq;

namespace AcruxPreview
{
    /// <summary>
    /// Texts for the previewed language: the game's (Content/Data/Localization, parsed once and cached per game build
    /// and language) overlaid with the mod's own (its Content/Data/Localization, re-read when changed). They are also
    /// fed to the game's MyTexts, so mod code that localizes at run time sees them.
    /// </summary>
    public static class PreviewLocalization
    {
        static readonly string[] Bases = { "MyCommonTexts", "MyTexts", "MyCoreTexts" };
        static Dictionary<string, string> _game = new Dictionary<string, string>(StringComparer.Ordinal);
        static Dictionary<string, string> _mod = new Dictionary<string, string>(StringComparer.Ordinal);
        static string _gameCulture;
        static string _modKey;

        static string GameDirectory => GameContent.Content == null ? null : Path.Combine(GameContent.Content, "Data", "Localization");

        /// <summary>Cultures the game ships ("en" is the neutral files).</summary>
        public static string[] Languages()
        {
            var cultures = new SortedSet<string>(StringComparer.OrdinalIgnoreCase) { "en" };
            string dir = GameDirectory;
            if (dir != null && Directory.Exists(dir))
                foreach (string file in Directory.EnumerateFiles(dir, "MyTexts.*.resx"))
                {
                    string[] parts = Path.GetFileNameWithoutExtension(file).Split('.');
                    if (parts.Length == 2)
                        cultures.Add(parts[1]);
                }
            return cultures.ToArray();
        }

        /// <summary>Loads (or reuses) the texts for <paramref name="culture"/> and the mod's localization folder.</summary>
        public static void Use(string culture, string modContent)
        {
            culture = string.IsNullOrEmpty(culture) ? "en" : culture;
            bool changed = false;
            if (!string.Equals(culture, _gameCulture, StringComparison.OrdinalIgnoreCase))
            {
                _game = LoadGame(culture);
                _gameCulture = culture;
                changed = true;
            }
            string modDir = modContent == null ? null : Path.Combine(modContent, "Data", "Localization");
            string modKey = culture + "|" + Stamp(modDir);
            if (modKey != _modKey)
            {
                _mod = modDir != null && Directory.Exists(modDir) ? Load(modDir, culture) : new Dictionary<string, string>(StringComparer.Ordinal);
                _modKey = modKey;
                changed = true;
            }
            if (changed)
                PushToGameTexts();
        }

        public static bool TryGet(string key, out string text)
        {
            if (key != null && (_mod.TryGetValue(key, out text) || _game.TryGetValue(key, out text)))
                return true;
            text = null;
            return false;
        }

        static Dictionary<string, string> LoadGame(string culture)
        {
            string dir = GameDirectory;
            if (dir == null || !Directory.Exists(dir))
                return new Dictionary<string, string>(StringComparer.Ordinal);
            string cache = Path.Combine(GameContent.CacheDirectory, "localization", culture + ".tsv");
            if (File.Exists(cache))
            {
                Dictionary<string, string> cached = ReadCache(cache);
                if (cached != null)
                    return cached;
            }
            var started = DateTime.UtcNow;
            Dictionary<string, string> texts = Load(dir, culture);
            WriteCache(cache, texts);
            Program.Log("game texts (" + culture + "): " + texts.Count + " in " + (int)(DateTime.UtcNow - started).TotalMilliseconds + " ms, cached");
            return texts;
        }

        // As MyTexts.LoadTexts: the neutral files, then the culture's (e.g. "pt-BR" also reads "pt").
        static Dictionary<string, string> Load(string dir, string culture)
        {
            var texts = new Dictionary<string, string>(StringComparer.Ordinal);
            var files = Directory.EnumerateFiles(dir, "*.resx", SearchOption.AllDirectories).ToList();
            var suffixes = new List<string> { string.Empty };
            if (!string.Equals(culture, "en", StringComparison.OrdinalIgnoreCase))
            {
                int dash = culture.IndexOf('-');
                if (dash > 0)
                    suffixes.Add("." + culture.Substring(0, dash));
                suffixes.Add("." + culture);
            }
            foreach (string suffix in suffixes)
                foreach (string baseName in Bases)
                    foreach (string file in files.Where(f => string.Equals(Path.GetFileName(f), baseName + suffix + ".resx", StringComparison.OrdinalIgnoreCase)))
                        ReadResx(file, texts);
            return texts;
        }

        static void ReadResx(string file, Dictionary<string, string> texts)
        {
            try
            {
                foreach (XElement data in XDocument.Load(file).Root.Elements("data"))
                {
                    string name = (string)data.Attribute("name");
                    string value = (string)data.Element("value");
                    if (!string.IsNullOrEmpty(name) && value != null)
                        texts[name] = value;
                }
            }
            catch (Exception error)
            {
                Program.Log("texts skipped (" + file + "): " + error.Message);
            }
        }

        static string Stamp(string dir)
        {
            if (dir == null || !Directory.Exists(dir))
                return "none";
            long latest = 0;
            foreach (string file in Directory.EnumerateFiles(dir, "*.resx", SearchOption.AllDirectories))
                latest = Math.Max(latest, File.GetLastWriteTimeUtc(file).Ticks);
            return dir + "@" + latest;
        }

        // The game's MyTexts, so mod code calling MyTexts.GetString (e.g. a mod's LocHelper) gets the same texts.
        static void PushToGameTexts()
        {
            try
            {
                Type texts = typeof(VRage.MyTexts);
                MethodInfo update = texts.GetMethod("UpdatePackage", BindingFlags.NonPublic | BindingFlags.Static);
                if (update == null)
                    return;
                VRage.MyTexts.Clear();
                foreach (var pair in _game)
                    update.Invoke(null, new object[] { pair.Key, pair.Value, true });
                foreach (var pair in _mod)
                    update.Invoke(null, new object[] { pair.Key, pair.Value, true });
            }
            catch (Exception error)
            {
                Program.Log("game texts not shared with the mod: " + (error.InnerException ?? error).Message);
            }
        }

        static Dictionary<string, string> ReadCache(string path)
        {
            try
            {
                var texts = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    int tab = line.IndexOf('\t');
                    if (tab > 0)
                        texts[Unescape(line.Substring(0, tab))] = Unescape(line.Substring(tab + 1));
                }
                return texts;
            }
            catch (IOException)
            {
                return null;
            }
        }

        static void WriteCache(string path, Dictionary<string, string> texts)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllLines(path, texts.Select(p => Escape(p.Key) + "\t" + Escape(p.Value)), Encoding.UTF8);
            }
            catch (IOException)
            {
            }
        }

        static string Escape(string text)
        {
            return text.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");
        }

        static string Unescape(string text)
        {
            var result = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\\' && i + 1 < text.Length)
                {
                    char next = text[++i];
                    result.Append(next == 'n' ? '\n' : next == 't' ? '\t' : next == 'r' ? '\r' : next);
                }
                else
                    result.Append(c);
            }
            return result.ToString();
        }
    }
}
