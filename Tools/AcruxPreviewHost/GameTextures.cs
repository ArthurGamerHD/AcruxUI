using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using SkiaSharp;
using VRageMath;

namespace AcruxPreview
{
    /// <summary>
    /// The LCD sprites a surface can draw, as the game defines them: the game's and the previewed mod's
    /// LCDTextureDefinitions (a mod's sprite path is looked up in the mod first, then in the game, as the game does),
    /// the icons of physical items ("MyObjectBuilder_Ore/Ice") and faction icons (named by their path).
    /// </summary>
    public static class GameTextures
    {
        static readonly object Sync = new object();
        static Dictionary<string, string> _game;
        static Dictionary<string, string> _mod = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static string _modKey;
        static readonly Dictionary<string, SKImage> Images = new Dictionary<string, SKImage>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, string[]> Listings = new Dictionary<string, string[]>(StringComparer.Ordinal);

        // Item definition collections in the game's .sbc files; their icons become LCD sprites.
        static readonly HashSet<string> PhysicalItemCollections = new HashSet<string>(StringComparer.Ordinal)
        {
            "PhysicalItems", "Components", "AmmoMagazines",
        };

        /// <summary>Uses the mod's definitions (its Content folder; null for none); re-read when its .sbc files change.</summary>
        public static void UseMod(string modContent)
        {
            string data = modContent == null ? null : Path.Combine(modContent, "Data");
            string key = data == null || !Directory.Exists(data)
                ? string.Empty
                : data + "|" + string.Join("|", Directory.EnumerateFiles(data, "*.sbc", SearchOption.AllDirectories)
                    .Select(f => f + ":" + File.GetLastWriteTimeUtc(f).Ticks));
            lock (Sync)
            {
                EnsureGame();
                if (key == _modKey)
                    return;
                _modKey = key;
                lock (Listings)
                    Listings.Clear();
                var mod = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (key.Length > 0)
                {
                    foreach (string file in Directory.EnumerateFiles(data, "*.sbc", SearchOption.AllDirectories))
                        ReadDefinitions(file, mod, modContent);
                }
                _mod = mod;
                // A mod's texture may have been replaced; game textures stay cached.
                foreach (string name in Images.Keys.ToList())
                    if (!_game.ContainsKey(name) || mod.ContainsKey(name))
                        Images.Remove(name);
                Program.Log("textures: " + _game.Count + " game, " + mod.Count + " mod");
            }
        }

        /// <summary>Every sprite name, as IMyTextSurface.GetSprites lists them.</summary>
        public static List<string> Names()
        {
            lock (Sync)
            {
                EnsureGame();
                var names = new List<string>(_game.Keys);
                names.AddRange(_mod.Keys.Where(k => !_game.ContainsKey(k)));
                return names;
            }
        }

        public static bool IsDefined(string name)
        {
            return PreviewGeneratedTextures.IsDefined(name) || FindPath(name) != null;
        }

        /// <summary>The texture's pixel size; zero when unknown.</summary>
        public static Vector2 Size(string name)
        {
            if (PreviewGeneratedTextures.IsDefined(name))
                return PreviewGeneratedTextures.Size(name);
            SKImage image = Image(name);
            return image == null ? Vector2.Zero : new Vector2(image.Width, image.Height);
        }

        /// <summary>The decoded sprite (premultiplied, as the game draws it); null when it is not defined or cannot be read.</summary>
        public static SKImage Image(string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;
            // A generated texture (MyAPIGateway.GeneratedTextures) as last painted; none until its first update.
            if (PreviewGeneratedTextures.IsDefined(name))
                return PreviewGeneratedTextures.Image(name);
            lock (Sync)
            {
                SKImage image;
                if (Images.TryGetValue(name, out image))
                    return image;
                string path = FindPath(name);
                try
                {
                    image = path == null ? null : Load(path);
                }
                catch (Exception error)
                {
                    Program.Log("texture '" + name + "' (" + path + ") not loaded: " + error.Message);
                    image = null;
                }
                Images[name] = image;
                return image;
            }
        }

        static string FindPath(string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;
            lock (Sync)
            {
                EnsureGame();
                string path;
                // Only what the game makes sprites of: LCD texture definitions (game, mod, and those the mod registers
                // at run time), physical items and faction icons. A texture path is no sprite name, as in game.
                if (_runtime.TryGetValue(name, out path) || _mod.TryGetValue(name, out path) || _game.TryGetValue(name, out path))
                    return path;
            }
            return null;
        }

        static Dictionary<string, string> _runtime = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The LCD textures the previewed mod registers at run time (name, then texture path in the mod's or the game's
        /// content), which its .sbc files cannot tell; replaces the previous set.
        /// </summary>
        public static void UseRuntimeTextures(IEnumerable<KeyValuePair<string, string>> textures, string modContent)
        {
            var runtime = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (textures != null)
                foreach (var texture in textures)
                    Add(runtime, texture.Key, texture.Value, modContent);
            lock (Sync)
                _runtime = runtime;
        }

        static SKImage Load(string path)
        {
            string extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension == ".dds")
            {
                // The game's DDS sprites hold premultiplied colour (what its sprite blending expects).
                using (SKBitmap raw = GameContent.LoadTexture(path))
                {
                    if (raw == null)
                        return null;
                    var info = new SKImageInfo(raw.Width, raw.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
                    return SKImage.FromPixelCopy(info, raw.GetPixels(), raw.RowBytes);
                }
            }
            // PNG / JPG: straight alpha, premultiplied on decode.
            using (SKBitmap bitmap = SKBitmap.Decode(path))
                return bitmap == null ? null : SKImage.FromBitmap(bitmap);
        }

        static void EnsureGame()
        {
            if (_game != null)
                return;
            var game = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _game = game;
            string content = GameContent.Content;
            if (content == null)
                return;
            try
            {
                foreach (string file in Directory.EnumerateFiles(Path.Combine(content, "Data"), "*.sbc", SearchOption.AllDirectories))
                    ReadDefinitions(file, game, null);
            }
            catch (Exception error)
            {
                Program.Log("game textures unavailable: " + error.Message);
            }
        }

        // LCD textures (both Id forms), physical item icons and faction icons of one .sbc file.
        static void ReadDefinitions(string file, Dictionary<string, string> into, string modContent)
        {
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (IOException)
            {
                return;
            }
            bool lcd = text.IndexOf("LCDTextureDefinition", StringComparison.Ordinal) >= 0;
            bool items = text.IndexOf("<Icon>", StringComparison.Ordinal) >= 0;
            bool factions = text.IndexOf("FactionIcon", StringComparison.Ordinal) >= 0;
            if (!lcd && !items && !factions)
                return;
            XDocument document;
            try
            {
                document = XDocument.Parse(text);
            }
            catch (Exception error)
            {
                Program.Log("skipped " + file + ": " + error.Message);
                return;
            }
            foreach (XElement definition in document.Descendants())
            {
                string kind = definition.Name.LocalName;
                if (kind == "LCDTextureDefinition")
                {
                    string id = SubtypeId(definition);
                    string sprite = (string)definition.Element("SpritePath") ?? (string)definition.Element("TexturePath");
                    Add(into, id, sprite, modContent);
                }
                else if (definition.Parent != null && PhysicalItemCollections.Contains(definition.Parent.Name.LocalName))
                {
                    string type = TypeId(definition);
                    string id = SubtypeId(definition);
                    string icon = (string)definition.Element("Icon");
                    if (type != null && id != null)
                        Add(into, "MyObjectBuilder_" + type + "/" + id, icon, modContent);
                }
                else if (kind == "Definition" && (TypeId(definition) ?? string.Empty).IndexOf("FactionIcon", StringComparison.Ordinal) >= 0)
                {
                    foreach (XElement icon in definition.Descendants("Icon"))
                        Add(into, (string)icon, (string)icon, modContent);
                }
            }
        }

        static bool Add(Dictionary<string, string> into, string name, string sprite, string modContent)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(sprite))
                return false;
            string path = modContent == null ? null : ResolveFile(modContent, sprite);
            if (path == null && GameContent.Content != null)
                path = ResolveFile(GameContent.Content, sprite);
            if (path == null)
                return false;
            into[name.Trim()] = path;
            return true;
        }

        static string SubtypeId(XElement definition)
        {
            XElement id = definition.Element("Id");
            if (id == null)
                return null;
            return (string)id.Attribute("Subtype") ?? (string)id.Element("SubtypeId");
        }

        static string TypeId(XElement definition)
        {
            XElement id = definition.Element("Id");
            string type = id == null ? null : (string)id.Attribute("Type") ?? (string)id.Element("TypeId");
            if (type == null)
            {
                XAttribute xsiType = definition.Attributes().FirstOrDefault(a => a.Name.LocalName == "type");
                type = xsiType == null ? null : xsiType.Value;
            }
            if (type == null)
                return null;
            type = type.Trim();
            return type.StartsWith("MyObjectBuilder_", StringComparison.Ordinal) ? type.Substring("MyObjectBuilder_".Length) : type;
        }

        /// <summary>
        /// <paramref name="relative"/> (as written in a definition: any slash, any case) under <paramref name="root"/>;
        /// a .png/.jpg reference also matches the .dds the game builds from it. Null when there is no such file.
        /// </summary>
        public static string ResolveFile(string root, string relative)
        {
            string clean = (relative ?? string.Empty).Trim().Replace('\\', '/').TrimStart('/');
            if (clean.Length == 0)
                return null;
            string found = ResolveExact(root, clean);
            if (found != null)
                return found;
            string extension = Path.GetExtension(clean);
            if (!string.Equals(extension, ".dds", StringComparison.OrdinalIgnoreCase))
                return ResolveExact(root, Path.ChangeExtension(clean, ".dds"));
            return null;
        }

        static string ResolveExact(string root, string clean)
        {
            string direct = Path.Combine(root, clean);
            if (File.Exists(direct))
                return direct;
            // Definitions are written for Windows: match each segment ignoring case.
            string current = root;
            string[] segments = clean.Split('/');
            for (int i = 0; i < segments.Length; i++)
            {
                string match = Match(current, segments[i], i == segments.Length - 1);
                if (match == null)
                    return null;
                current = match;
            }
            return current;
        }

        static string Match(string directory, string name, bool file)
        {
            string[] entries;
            lock (Listings)
            {
                if (!Listings.TryGetValue(directory, out entries))
                {
                    entries = Directory.Exists(directory) ? Directory.GetFileSystemEntries(directory) : new string[0];
                    Listings[directory] = entries;
                }
            }
            foreach (string entry in entries)
            {
                if (!string.Equals(Path.GetFileName(entry), name, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (file ? File.Exists(entry) : Directory.Exists(entry))
                    return entry;
            }
            return null;
        }
    }
}
