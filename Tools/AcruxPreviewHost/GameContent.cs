using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace AcruxPreview
{
    /// <summary>
    /// The installed game's Content folder (next to its Bin64, see GameInstall) and a cache of decoded
    /// game textures, keyed by the game's build so it is rebuilt only after a game update.
    /// </summary>
    public static class GameContent
    {
        static readonly Lazy<string> ContentRoot = new Lazy<string>(FindContent);
        static readonly Lazy<string> CacheRoot = new Lazy<string>(FindCache);

        public static string Content => ContentRoot.Value;

        /// <summary>Per game build: decoded textures, parsed texts.</summary>
        public static string CacheDirectory => CacheRoot.Value;

        static string FindContent()
        {
            string bin = GameInstall.Bin;
            if (string.IsNullOrEmpty(bin))
                return null;
            string content = Path.GetFullPath(Path.Combine(bin, "..", "Content"));
            return Directory.Exists(content) ? content : null;
        }

        static string FindCache()
        {
            string baseDir = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            if (string.IsNullOrEmpty(baseDir))
                baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
            string dir = Path.Combine(baseDir, "acrux-preview", GameBuild());
            Directory.CreateDirectory(dir);
            return dir;
        }

        // Steam's build id of the installed game; the content folder's date when it cannot be read.
        static string GameBuild()
        {
            try
            {
                if (Content != null)
                {
                    string manifest = Path.GetFullPath(Path.Combine(Content, "..", "..", "..", "appmanifest_244850.acf"));
                    if (File.Exists(manifest))
                    {
                        Match match = Regex.Match(File.ReadAllText(manifest), "\"buildid\"\\s*\"(\\d+)\"");
                        if (match.Success)
                            return "build-" + match.Groups[1].Value;
                    }
                    return "content-" + Directory.GetLastWriteTimeUtc(Content).Ticks;
                }
            }
            catch (Exception)
            {
            }
            return "unknown";
        }

        // FNV-1a: string.GetHashCode differs between runs.
        static string StableHash(string text)
        {
            uint hash = 2166136261;
            foreach (char c in text)
                hash = (hash ^ c) * 16777619;
            return hash.ToString("x8");
        }

        /// <summary>An image file's pixels exactly as stored (RGBA, alpha not premultiplied by the decoder).</summary>
        public static SKBitmap DecodeRaw(string path)
        {
            using (SKCodec codec = SKCodec.Create(path))
            {
                if (codec == null)
                    return null;
                var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
                var bitmap = new SKBitmap(info);
                SKCodecResult result = codec.GetPixels(info, bitmap.GetPixels());
                if (result == SKCodecResult.Success || result == SKCodecResult.IncompleteInput)
                    return bitmap;
                bitmap.Dispose();
                return null;
            }
        }

        /// <summary>A game DDS texture decoded to a bitmap (bytes as stored); decoded once per game build.</summary>
        public static SKBitmap LoadTexture(string path)
        {
            if (path == null || !File.Exists(path))
                return null;
            string relative = Content != null && path.StartsWith(Content, StringComparison.Ordinal)
                ? path.Substring(Content.Length).TrimStart('/', '\\')
                // Outside the game (a mod's texture): keyed by its path and date, as it changes while the mod is edited.
                : Path.Combine("external", StableHash(path + "|" + File.GetLastWriteTimeUtc(path).Ticks), Path.GetFileName(path));
            string cached = Path.Combine(CacheRoot.Value, "textures", relative.Replace('\\', '/') + ".png");
            if (File.Exists(cached))
            {
                // Decoded as stored (not premultiplied by the decoder), so a hit holds the same bytes as a miss.
                SKBitmap hit = DecodeRaw(cached);
                if (hit != null)
                    return hit;
            }

            Adk.Image.RawRgbaBitmap raw;
            if (!Adk.Image.Dds.DdsDecoder.TryDecode(File.ReadAllBytes(path), out raw) || raw == null)
                return null;
            var info = new SKImageInfo(raw.Width, raw.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            var bitmap = new SKBitmap(info);
            System.Runtime.InteropServices.Marshal.Copy(raw.Pixels, 0, bitmap.GetPixels(), Math.Min(raw.Pixels.Length, info.BytesSize));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cached));
                using (SKData png = bitmap.Encode(SKEncodedImageFormat.Png, 100))
                using (FileStream file = File.Create(cached))
                    png.SaveTo(file);
            }
            catch (IOException)
            {
            }
            return bitmap;
        }
    }
}
