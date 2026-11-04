using System;
using System.Reflection;
using System.Text;
using SkiaSharp;
using VRageMath;
using IMyTextSurface = Sandbox.ModAPI.Ingame.IMyTextSurface;

namespace AcruxPreview
{
    /// <summary>
    /// Stand-in for an LCD surface. Text is measured with the same font the rasterizer draws with, so Acrux
    /// lays text out exactly as the preview shows it.
    /// </summary>
    public class PreviewSurface : DispatchProxy
    {
        Vector2 _size;

        public static IMyTextSurface Create(Vector2 size)
        {
            var surface = Create<IMyTextSurface, PreviewSurface>();
            ((PreviewSurface)(object)surface)._size = size;
            return surface;
        }

        protected override object Invoke(MethodInfo method, object[] args)
        {
            switch (method.Name)
            {
                case "get_SurfaceSize":
                case "get_TextureSize":
                    return _size;
                case "get_TextPadding":
                    return 0f;
                case "MeasureStringInPixels":
                {
                    string text = args[0] is StringBuilder ? ((StringBuilder)args[0]).ToString() : (string)args[0];
                    // The game's own font metrics when the game is installed; a system font otherwise.
                    if (GameFonts.Available)
                        return GameFonts.Get((string)args[1]).Measure(text, (float)args[2]);
                    return PreviewFonts.Measure(text, (string)args[1], (float)args[2]);
                }
                case "GetSprites":
                {
                    var names = args[0] as System.Collections.Generic.List<string>;
                    if (names != null)
                        names.AddRange(GameTextures.Names());
                    return null;
                }
            }
            if (method.ReturnType == typeof(void))
                return null;
            return method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
        }
    }

    /// <summary>
    /// Approximates the game's LCD fonts with a system font. Sizes follow the game's convention: at scale 1 a
    /// line of the "White" font is about 37 px tall.
    /// </summary>
    public static class PreviewFonts
    {
        public const float LineHeightAtScale1 = 37f;
        static readonly SKTypeface Sans = SKTypeface.FromFamilyName("DejaVu Sans") ?? SKTypeface.Default;
        static readonly SKTypeface Mono = SKTypeface.FromFamilyName("DejaVu Sans Mono") ?? SKTypeface.Default;

        public static SKFont Font(string fontId, float scale)
        {
            bool mono = fontId != null && fontId.IndexOf("mono", StringComparison.OrdinalIgnoreCase) >= 0;
            return new SKFont(mono ? Mono : Sans, LineHeightAtScale1 * 0.78f * Math.Max(0.01f, scale));
        }

        public static float Width(SKFont font, string text)
        {
            using (var paint = new SKPaint(font))
                return paint.MeasureText(text ?? string.Empty);
        }

        public static Vector2 Measure(string text, string fontId, float scale)
        {
            using (SKFont font = Font(fontId, scale))
            {
                float width = 0f;
                int lines = 1;
                foreach (string line in (text ?? string.Empty).Split('\n'))
                    width = Math.Max(width, PreviewFonts.Width(font, line));
                foreach (char c in text ?? string.Empty)
                    if (c == '\n')
                        lines++;
                return new Vector2(width, LineHeightAtScale1 * Math.Max(0.01f, scale) * lines);
            }
        }
    }
}
