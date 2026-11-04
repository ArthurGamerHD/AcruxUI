using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using SkiaSharp;
using VRageMath;

namespace AcruxPreview
{
    /// <summary>
    /// The game's LCD fonts (Content/Data/Fonts.sbc + the bitmap fonts it points to), measured and drawn exactly as
    /// the game's MyFont / MyRenderFont do it, so the preview lays text out like an LCD in game.
    /// </summary>
    public static class GameFonts
    {
        // MyRenderGuiConstants: LCD text is measured and drawn at 144/185 of the sprite's scale.
        const double GuiScale = 144.0 / 185.0;
        static readonly Dictionary<string, GameFont> Fonts = new Dictionary<string, GameFont>(StringComparer.OrdinalIgnoreCase);
        static bool _loaded;

        public static bool Available
        {
            get
            {
                EnsureLoaded();
                return Fonts.Count > 0;
            }
        }

        /// <summary>The font a sprite's FontId names; the game falls back to "Debug".</summary>
        public static GameFont Get(string fontId)
        {
            EnsureLoaded();
            GameFont font;
            if (fontId != null && Fonts.TryGetValue(fontId, out font))
                return font;
            return Fonts.TryGetValue("Debug", out font) ? font : Fonts.Values.FirstOrDefault();
        }

        static void EnsureLoaded()
        {
            lock (Fonts)
            {
                if (_loaded)
                    return;
                _loaded = true;
                try
                {
                    LoadDefinitions();
                }
                catch (Exception error)
                {
                    Program.Log("game fonts unavailable: " + error.Message);
                }
            }
        }

        static void LoadDefinitions()
        {
            string content = GameContent.Content;
            if (content == null)
                return;
            var files = new Dictionary<string, GameFontData>(StringComparer.OrdinalIgnoreCase);
            foreach (XElement font in XDocument.Load(Path.Combine(content, "Data", "Fonts.sbc")).Descendants("Font"))
            {
                string id = (string)font.Element("Id")?.Element("SubtypeId");
                string resource = (string)font.Element("Resources")?.Elements("Resource").FirstOrDefault()?.Attribute("Path");
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(resource))
                    continue;
                GameFontData data;
                if (!files.TryGetValue(resource, out data))
                {
                    data = GameFontData.Load(Path.Combine(content, resource.Replace('\\', '/')));
                    files[resource] = data;
                }
                if (data == null)
                    continue;
                XElement mask = font.Element("ColorMask");
                Color color = mask == null
                    ? Color.White
                    : new Color(Int(mask, "X", 255), Int(mask, "Y", 255), Int(mask, "Z", 255), Int(mask, "W", 255));
                Fonts[id] = new GameFont(id, data, color);
            }
            Program.Log("game fonts: " + string.Join(", ", Fonts.Keys));
        }

        static int Int(XElement parent, string name, int fallback)
        {
            int value;
            string text = (string)parent.Element(name);
            return text != null && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : fallback;
        }

        internal static double Scaled(float scale)
        {
            return scale * GuiScale;
        }
    }

    public sealed class GameFont
    {
        readonly GameFontData _data;

        internal GameFont(string id, GameFontData data, Color colorMask)
        {
            Id = id;
            _data = data;
            ColorMask = colorMask;
        }

        public string Id { get; }
        public Color ColorMask { get; }
        public int LineHeight => _data.LineHeight;

        /// <summary>MyFont.MeasureString(text, scale) with the LCD's GUI constants, as IMyTextSurface.MeasureStringInPixels.</summary>
        public Vector2 Measure(string text, float scale)
        {
            double s = GameFonts.Scaled(scale);
            double x = 0, width = 0;
            int lines = 1, extraAbove = 0, lineExtra = 0, below = 0;
            char last = '\0';
            text = text ?? string.Empty;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                GameGlyph glyph;
                if (c == '\n')
                {
                    lines++;
                    x = 0;
                    last = '\0';
                    below = 0;
                    extraAbove += lineExtra;
                    lineExtra = 0;
                }
                else if (_data.TryGetGlyph(c, out glyph))
                {
                    int top = glyph.Height + glyph.HeightOffset;
                    if (top - _data.LineHeight > lineExtra)
                        lineExtra = top - _data.LineHeight;
                    int under = glyph.Height - glyph.HeightOffset - _data.AverageLineHeight;
                    if (under > below)
                        below = under;
                    x += _data.Kern(last, c);
                    last = c;
                    x += glyph.Advance;
                    if (i < text.Length - 1)
                        x += _data.Spacing;
                    if (x > width)
                        width = x;
                }
            }
            return new Vector2((float)(width * s), (float)(lines * _data.LineHeight * s + extraAbove / 2.0 * s + below * s));
        }

        /// <summary>
        /// MyRenderFont.DrawStringAligned for an LCD text sprite: alignment is per line against the sprite's width.
        /// </summary>
        public void Draw(SKCanvas canvas, string text, Vector2 position, Color color, float scale, int alignment, int textureWidth)
        {
            if (string.IsNullOrEmpty(text))
                return;
            double s = GameFonts.Scaled(scale);
            double spacing = _data.Spacing * s;
            Color tint = Multiply(color, ColorMask);
            double x = position.X + AlignmentOffset(text, 0, s, spacing, alignment, textureWidth);
            int line = 0;
            char last = '\0';
            using (var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.Medium })
            {
                paint.ColorFilter = SKColorFilter.CreateBlendMode(new SKColor(tint.R, tint.G, tint.B, tint.A), SKBlendMode.Modulate);
                for (int i = 0; i < text.Length; i++)
                {
                    char c = text[i];
                    if (c == '\n')
                    {
                        // The game passes scale and spacing swapped here; reproduced so multi-line alignment matches.
                        x = position.X + AlignmentOffset(text, i + 1, spacing, s, alignment, textureWidth);
                        line++;
                        continue;
                    }
                    GameGlyph glyph;
                    if (!_data.TryGetGlyph(c, out glyph))
                        continue;
                    x += _data.Kern(last, c) * s;
                    last = c;
                    double y = position.Y + (glyph.LeftSideBearing + glyph.HeightOffset + 3.8333333f + line * _data.LineHeight) * s;
                    x += glyph.LeftSideBearing * s;
                    SKBitmap page = _data.Page(glyph.Page);
                    if (page != null && glyph.Width > 0 && glyph.Height > 0)
                        canvas.DrawBitmap(page,
                            SKRect.Create(glyph.X, glyph.Y, glyph.Width, glyph.Height),
                            SKRect.Create((float)x, (float)y, (float)(glyph.Width * s), (float)(glyph.Height * s)),
                            paint);
                    x += (glyph.Advance - glyph.LeftSideBearing) * s;
                    if (i < text.Length - 1)
                        x += spacing;
                }
            }
        }

        double AlignmentOffset(string text, int start, double scale, double spacing, int alignment, int textureWidth)
        {
            if (alignment != 1 && alignment != 2)
                return 0;
            double free = textureWidth - LineLength(text, start, scale, spacing);
            return alignment == 2 ? free / 2.0 : free;
        }

        // MyRenderFont.ComputeLineLength: advances (with kerning) and spacing up to the end of the line.
        double LineLength(string text, int start, double scale, double spacing)
        {
            double length = 0;
            char last = '\0';
            for (int i = start; i < text.Length && text[i] != '\n'; i++)
            {
                GameGlyph glyph;
                if (!_data.TryGetGlyph(text[i], out glyph))
                    continue;
                length += _data.Kern(last, text[i]) * scale + glyph.Advance * scale;
                last = text[i];
                if (i < text.Length - 1 && text[i + 1] != '\n')
                    length += spacing;
            }
            return length;
        }

        static Color Multiply(Color a, Color b)
        {
            return new Color(a.R * b.R / 255, a.G * b.G / 255, a.B * b.B / 255, a.A * b.A / 255);
        }
    }

    internal struct GameGlyph
    {
        public int Page, X, Y, Width, Height, Advance, LeftSideBearing, HeightOffset;
    }

    /// <summary>One bitmap font file (FontDataPA.xml) and its atlas pages.</summary>
    internal sealed class GameFontData
    {
        readonly Dictionary<char, GameGlyph> _glyphs = new Dictionary<char, GameGlyph>();
        readonly Dictionary<int, sbyte> _kern = new Dictionary<int, sbyte>();
        readonly Dictionary<int, string> _pagePaths = new Dictionary<int, string>();
        readonly Dictionary<int, SKBitmap> _pages = new Dictionary<int, SKBitmap>();

        public int LineHeight { get; private set; }
        public int Baseline { get; private set; }
        public int AverageLineHeight { get; private set; } = 45;
        public int Spacing => 1;

        public static GameFontData Load(string path)
        {
            if (!File.Exists(path))
                return null;
            var data = new GameFontData();
            XElement root = XDocument.Load(path).Root;
            data.LineHeight = Attr(root, "height");
            data.Baseline = Attr(root, "base");
            string dir = Path.GetDirectoryName(path);
            foreach (XElement bitmap in root.Descendants().Where(e => e.Name.LocalName == "bitmap"))
                data._pagePaths[Attr(bitmap, "id")] = Path.Combine(dir, (string)bitmap.Attribute("name"));
            foreach (XElement glyph in root.Descendants().Where(e => e.Name.LocalName == "glyph"))
            {
                string ch = (string)glyph.Attribute("ch");
                string code = (string)glyph.Attribute("code");
                char key;
                if (!string.IsNullOrEmpty(ch))
                    key = ch[0];
                else if (!string.IsNullOrEmpty(code))
                    key = (char)int.Parse(code, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                else
                    continue;
                string[] origin = ((string)glyph.Attribute("origin") ?? "0,0").Split(',');
                string[] size = ((string)glyph.Attribute("size") ?? "0x0").Split('x');
                data._glyphs[key] = new GameGlyph
                {
                    Page = Attr(glyph, "bm"),
                    X = int.Parse(origin[0], CultureInfo.InvariantCulture),
                    Y = int.Parse(origin[1], CultureInfo.InvariantCulture),
                    Width = int.Parse(size[0], CultureInfo.InvariantCulture),
                    Height = int.Parse(size[1], CultureInfo.InvariantCulture),
                    Advance = Attr(glyph, "aw"),
                    LeftSideBearing = Attr(glyph, "lsb"),
                    HeightOffset = Attr(glyph, "ho"),
                };
            }
            // The most common glyph height (MyFont.AverageLineHeight).
            if (data._glyphs.Count > 0)
                data.AverageLineHeight = data._glyphs.Values.GroupBy(g => g.Height).OrderByDescending(g => g.Count()).First().Key;
            foreach (XElement pair in root.Descendants().Where(e => e.Name.LocalName == "kernpair"))
            {
                string left = (string)pair.Attribute("left"), right = (string)pair.Attribute("right");
                if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
                    continue;
                data._kern[KernKey(left[0], right[0])] = (sbyte)Attr(pair, "adjust");
            }
            return data;
        }

        public bool TryGetGlyph(char c, out GameGlyph glyph)
        {
            if (_glyphs.TryGetValue(c, out glyph))
                return true;
            // MyFont substitutes missing characters with the replacement glyph.
            return _glyphs.TryGetValue('□', out glyph);
        }

        public int Kern(char left, char right)
        {
            sbyte value;
            return left != '\0' && _kern.TryGetValue(KernKey(left, right), out value) ? value : 0;
        }

        public SKBitmap Page(int id)
        {
            lock (_pages)
            {
                SKBitmap page;
                if (_pages.TryGetValue(id, out page))
                    return page;
                string path;
                page = _pagePaths.TryGetValue(id, out path) ? GameContent.LoadTexture(path) : null;
                _pages[id] = page;
                return page;
            }
        }

        static int KernKey(char left, char right)
        {
            return (left << 16) | right;
        }

        static int Attr(XElement element, string name)
        {
            int value;
            string text = (string)element.Attribute(name);
            return text != null && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : 0;
        }
    }
}
