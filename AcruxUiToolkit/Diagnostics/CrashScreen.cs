using System;
using System.Collections.Generic;
using Acrux.Animation;
using Acrux.Controls;
using Acrux.Core;
using Acrux.Lcd;
using Acrux.Layout;
using Acrux.Rendering;
using Acrux.Toolkit;
using VRage.Game.GUI.TextPanel;
using VRageMath;
using IMyTextSurface = Sandbox.ModAPI.Ingame.IMyTextSurface;

namespace Acrux.Diagnostics
{
    /// <summary>
    /// Renders a mod's crash screen ("blue screen") for an LCD surface: a sad face, the mod's support text and
    /// QR code, and the exception. Drawn with toolkit controls; falls back to plain sprites if that fails.
    /// </summary>
    public sealed class CrashScreen
    {
        const string FontId = "Debug";
        static readonly Color Foreground = new Color(179, 237, 255);
        static readonly Color Background = new Color(0, 88, 151);

        readonly List<MySprite> _frame = new List<MySprite>();

        CrashScreen()
        {
        }

        /// <summary>Sprites of the crash screen, ready for the surface's frame.</summary>
        public List<MySprite> Frame => _frame;

        /// <summary>The frame shows the QR texture created while drawing it; draw the frame again once that texture is live.</summary>
        public bool NeedsReupload { get; private set; }

        /// <summary>Builds the crash screen for <paramref name="viewBox"/> of <paramref name="surface"/>.</summary>
        public static CrashScreen Show(
            IMyTextSurface surface,
            RectangleF viewBox,
            AnimationController animations,
            CrashReport report,
            Exception exception)
        {
            return Show(surface, viewBox, animations, report, exception, null);
        }

        internal static CrashScreen Show(
            IMyTextSurface surface,
            RectangleF viewBox,
            AnimationController animations,
            CrashReport report,
            Exception exception,
            GeneratedTexturePool pool)
        {
            var screen = new CrashScreen();
            if (surface == null)
                return screen;
            if (report == null)
                report = new CrashReport(null);
            if (!screen.DrawRetained(surface, viewBox, animations, report, exception, pool))
                screen.DrawSprites(surface, viewBox, report, exception);
            return screen;
        }

        bool DrawRetained(
            IMyTextSurface surface,
            RectangleF viewBox,
            AnimationController animations,
            CrashReport report,
            Exception exception,
            GeneratedTexturePool pool)
        {
            try
            {
                string details = report.SupportIntroOrDefault + "\n" + report.ExceptionLabelOrDefault + "\n" + exception;
                var root = new Border
                {
                    BackgroundColor = Background,
                    Padding = new Vector4(14f)
                };
                var layout = new Grid();
                layout.SetColumnDefinitions(GridLength.Star);
                layout.SetRowDefinitions(GridLength.Auto, GridLength.Auto, GridLength.Star);
                layout.Set(Text(":(", 3f, false), 0, 0);

                var summary = new StackPanel { Spacing = 8f, Margin = new Vector4(0f, 10f, 0f, 0f) };
                summary.AddChild(Text(report.HeaderOrDefault, 0.8f, true));
                var support = new Grid();
                support.SetColumnDefinitions(GridLength.Auto, GridLength.Star);
                if (report.QrRectangles != null && report.QrModules > 0)
                {
                    float requestedQr = Math.Min(viewBox.Width * 0.24f, viewBox.Height * 0.3f);
                    float qrSize = (float)Math.Round(Math.Max(40f, Math.Min(requestedQr, QuantizeQrSize(requestedQr, report.QrModules))));
                    support.Set(new CrashQrCode(report.QrRectangles, report.QrModules, Foreground, Background)
                    {
                        Width = qrSize,
                        Height = qrSize,
                        Margin = new Vector4(0f, 0f, 12f, 0f),
                        VerticalAlignment = VerticalAlignment.Top
                    }, 0, 0);
                }
                support.Set(Text(report.InfoOrDefault, 0.55f, true), 1, 0);
                summary.AddChild(support);
                layout.Set(summary, 0, 1);
                TextBlock detailsBlock = Text(details, 0.45f, true);
                detailsBlock.Margin = new Vector4(0f, 12f, 0f, 0f);
                layout.Set(detailsBlock, 0, 2);
                root.Child = layout;

                CrashQrCode.CreatedThisFrame = false;
                using (var host = new LcdGuiHost(surface, root, animations ?? AnimationServices.DefaultController))
                {
                    host.Renderer.PoolOverride = pool;
                    host.Arrange(viewBox);
                    host.Draw(_frame);
                }
                NeedsReupload = CrashQrCode.CreatedThisFrame;
                return true;
            }
            catch (Exception error)
            {
                Acrux.Toolkit.Server.ExternalInterop.WarnOnce("Crash screen fell back to plain sprites: " + error.Message);
                _frame.Clear();
                return false;
            }
        }

        static TextBlock Text(string text, float size, bool wrap)
        {
            return new TextBlock
            {
                Text = text,
                FontFamily = FontId,
                FontSize = size,
                Foreground = Foreground,
                TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
                VerticalTextAlignment = VerticalAlignment.Top
            };
        }

        static float QuantizeQrSize(float requestedSize, int modules)
        {
            float moduleSize = Math.Max(1f, (float)Math.Floor(requestedSize / modules));
            return moduleSize * modules;
        }

        // ------------------------------------------------------------------ sprite fallback

        void DrawSprites(IMyTextSurface surface, RectangleF viewBox, CrashReport report, Exception exception)
        {
            _frame.Clear();
            _frame.Add(MySprite.CreateClipRect(new Rectangle(
                (int)viewBox.X, (int)viewBox.Y, Math.Max(1, (int)viewBox.Width), Math.Max(1, (int)viewBox.Height))));
            _frame.Add(new MySprite(SpriteType.TEXTURE, "SquareSimple", viewBox.Center, viewBox.Size, Background));

            float layout = Math.Max(0.45f, Math.Min(viewBox.Width, viewBox.Height) / 512f);
            float x = viewBox.X + 14f * layout;
            float width = Math.Max(8f, viewBox.Width - 28f * layout);
            float y = viewBox.Y + 14f * layout;
            y = DrawLines(surface, ":(", x, y, width, 3f * layout) + 10f * layout;
            y = DrawLines(surface, report.HeaderOrDefault, x, y, width, 0.8f * layout) + 10f * layout;

            float infoX = x;
            if (report.QrRectangles != null && report.QrModules > 0)
            {
                float side = (float)Math.Round(Math.Max(40f * layout, Math.Min(width * 0.24f, viewBox.Height * 0.3f)));
                DrawQr(report.QrRectangles, report.QrModules, new Vector2(x + side * .5f, y + side * .5f), side / report.QrModules);
                infoX = x + side + 12f * layout;
                float infoBottom = DrawLines(surface, report.InfoOrDefault, infoX, y, Math.Max(8f, x + width - infoX), 0.55f * layout);
                y = Math.Max(y + side, infoBottom) + 12f * layout;
            }
            else
                y = DrawLines(surface, report.InfoOrDefault, x, y, width, 0.55f * layout) + 12f * layout;

            string details = report.SupportIntroOrDefault + "\n" + report.ExceptionLabelOrDefault + "\n" + exception;
            DrawLines(surface, details, x, y, width, 0.45f * layout, viewBox.Bottom);
            _frame.Add(MySprite.CreateClearClipRect());
        }

        void DrawQr(int[] rectangles, int modules, Vector2 center, float moduleSize)
        {
            for (int i = 0; i + 4 < rectangles.Length; i += 5)
            {
                var size = new Vector2((float)Math.Round(rectangles[i + 2] * moduleSize), (float)Math.Round(rectangles[i + 3] * moduleSize));
                if (size.X <= 0f || size.Y <= 0f)
                    continue;
                var position = new Vector2(
                    (float)Math.Round(rectangles[i] * moduleSize + center.X),
                    (float)Math.Round(rectangles[i + 1] * moduleSize + center.Y));
                _frame.Add(new MySprite(SpriteType.TEXTURE, "SquareSimple", position, size,
                    rectangles[i + 4] == 1 ? Foreground : Background));
            }
        }

        // Greedy word wrap with the surface's own text measurement; returns the y below the last line.
        float DrawLines(IMyTextSurface surface, string text, float x, float y, float width, float scale, float bottom = float.MaxValue)
        {
            float lineHeight = Math.Max(1f, surface.MeasureStringInPixels(new System.Text.StringBuilder("Ag"), FontId, scale).Y + 2f);
            var line = new System.Text.StringBuilder();
            var probe = new System.Text.StringBuilder();
            foreach (string paragraph in (text ?? string.Empty).Split('\n'))
            {
                line.Clear();
                foreach (string word in paragraph.Split(' '))
                {
                    probe.Clear().Append(line);
                    if (probe.Length > 0)
                        probe.Append(' ');
                    probe.Append(word);
                    if (line.Length > 0 && surface.MeasureStringInPixels(probe, FontId, scale).X > width)
                    {
                        if (!AddLine(line.ToString(), x, ref y, scale, lineHeight, bottom))
                            return y;
                        line.Clear().Append(word);
                    }
                    else
                    {
                        line.Clear().Append(probe);
                    }
                }
                if (!AddLine(line.ToString(), x, ref y, scale, lineHeight, bottom))
                    return y;
            }
            return y;
        }

        bool AddLine(string text, float x, ref float y, float scale, float lineHeight, float bottom)
        {
            if (y + lineHeight > bottom)
                return false;
            _frame.Add(new MySprite
            {
                Type = SpriteType.TEXT,
                Data = text,
                Position = new Vector2((float)Math.Round(x), (float)Math.Round(y)),
                Color = Foreground,
                FontId = FontId,
                Alignment = TextAlignment.LEFT,
                RotationOrScale = scale
            });
            y += lineHeight;
            return true;
        }
    }

    /// <summary>A crash screen's support QR code, painted once into a generated texture per mod.</summary>
    internal sealed class CrashQrCode : VisualElement
    {
        const int PixelsPerModule = 8;

        // One texture per QR code (mods have different codes and different texture budgets).
        static readonly Dictionary<int[], IDynamicTexture> Textures = new Dictionary<int[], IDynamicTexture>();

        readonly int[] _rectangles;
        readonly int _modules;
        readonly Color _light;
        readonly Color _dark;

        public CrashQrCode(int[] rectangles, int modules, Color light, Color dark)
        {
            _rectangles = rectangles;
            _modules = modules;
            _light = light;
            _dark = dark;
        }

        // True when a QR texture was created during the last draw and is not live yet.
        internal static bool CreatedThisFrame { get; set; }

        internal static void Release()
        {
            lock (Textures)
            {
                foreach (var texture in Textures.Values)
                    texture.Dispose();
                Textures.Clear();
            }
        }

        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            return Vector2.Zero;
        }

        protected override void DrawSelf(RenderContext context)
        {
            float side = Math.Min(Bounds.Width, Bounds.Height);
            IDynamicTexture texture = side > 0f ? EnsureTexture(context) : null;
            if (texture == null)
                return;
            var rect = new RectangleF(Bounds.Center - new Vector2(side * .5f), new Vector2(side));
            context.DrawTexture(texture.Source, rect, Color.White);
        }

        IDynamicTexture EnsureTexture(RenderContext context)
        {
            lock (Textures)
            {
                IDynamicTexture texture;
                if (Textures.TryGetValue(_rectangles, out texture) && texture.IsAvailable)
                    return texture;
                if (texture != null)
                    texture.Dispose();
                Textures.Remove(_rectangles);
                if (context.DynamicTextures == null)
                    return null;
                int size = _modules * PixelsPerModule;
                texture = context.DynamicTextures.Create("CrashQr", new Vector2I(size, size));
                if (texture == null || !texture.IsAvailable || !texture.Update(Paint))
                {
                    if (texture != null)
                        texture.Dispose();
                    return null;
                }
                Textures[_rectangles] = texture;
                CreatedThisFrame = true;
                return texture;
            }
        }

        void Paint(PixelCanvas canvas)
        {
            canvas.Clear(_dark);
            float scale = canvas.Width / (float)_modules;
            for (int i = 0; i + 4 < _rectangles.Length; i += 5)
            {
                float left = (_rectangles[i] - _rectangles[i + 2] * .5f + _modules * .5f) * scale;
                float top = (_rectangles[i + 1] - _rectangles[i + 3] * .5f + _modules * .5f) * scale;
                canvas.FillRectangle((int)Math.Round(left), (int)Math.Round(top),
                    (int)Math.Round(_rectangles[i + 2] * scale), (int)Math.Round(_rectangles[i + 3] * scale),
                    _rectangles[i + 4] == 1 ? _light : _dark);
            }
        }
    }
}
