using System;
using System.Collections.Generic;
using SkiaSharp;
using VRage.Game.GUI.TextPanel;
using VRageMath;

namespace AcruxPreview
{
    /// <summary>
    /// Paints an LCD sprite frame (what the game's LCD backend would receive) into a bitmap. Textures are the game's
    /// and the mod's (<see cref="GameTextures"/>), stretched to the sprite and tinted by its colour; a texture that
    /// cannot be found shows as a tinted placeholder with its name.
    /// </summary>
    public static class SpriteRasterizer
    {
        static readonly HashSet<string> SolidTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SquareSimple", "White", "SquareTapered",
        };

        public static void Paint(SKCanvas canvas, IList<MySprite> sprites, float scale, Vector2 surfaceSize)
        {
            canvas.Save();
            canvas.Scale(scale);
            int clips = 0;
            foreach (MySprite sprite in sprites)
            {
                switch (sprite.Type)
                {
                    case SpriteType.CLIP_RECT:
                        while (clips > 0)
                        {
                            canvas.Restore();
                            clips--;
                        }
                        if (sprite.Position.HasValue && sprite.Size.HasValue)
                        {
                            canvas.Save();
                            clips++;
                            Vector2 p = sprite.Position.Value, s = sprite.Size.Value;
                            canvas.ClipRect(SKRect.Create(p.X, p.Y, s.X, s.Y));
                        }
                        break;
                    case SpriteType.TEXTURE:
                        PaintTexture(canvas, sprite);
                        break;
                    case SpriteType.TEXT:
                        if (GameFonts.Available)
                            PaintGameText(canvas, sprite, surfaceSize);
                        else
                            PaintText(canvas, sprite);
                        break;
                }
            }
            while (clips-- > 0)
                canvas.Restore();
            canvas.Restore();
        }

        static SKColor ToSk(Color? color)
        {
            Color c = color ?? Color.White;
            return new SKColor(c.R, c.G, c.B, c.A);
        }

        // The game's sprite shader multiplies the (premultiplied) texel by the sprite's colour channel by channel and
        // blends premultiplied (src + dst * (1 - srcA)); the colour itself is not premultiplied, so a colour's alpha only
        // lets more of what is behind through, without darkening the sprite: out = colour * texel + dst * (1 - texelA *
        // colourA). A plain "over" draw gives colour * colourA * texel; the glow pass adds the rest, colour * texel *
        // (1 - colourA). That is why, for example, the LCD grid (white lines tinted with the background colour at 0.66)
        // shows on its own background colour.
        static SKColor GlowColour(SKColor colour)
        {
            return new SKColor(colour.Red, colour.Green, colour.Blue, (byte)(255 - colour.Alpha));
        }

        static void PaintTexture(SKCanvas canvas, MySprite sprite)
        {
            if (!sprite.Size.HasValue || !sprite.Position.HasValue)
                return;
            Vector2 size = sprite.Size.Value, center = sprite.Position.Value;
            if (sprite.Alignment == TextAlignment.LEFT)
                center.X += size.X * 0.5f;
            else if (sprite.Alignment == TextAlignment.RIGHT)
                center.X -= size.X * 0.5f;
            var rect = SKRect.Create(center.X - size.X * 0.5f, center.Y - size.Y * 0.5f, size.X, size.Y);

            canvas.Save();
            if (Math.Abs(sprite.RotationOrScale) > 1e-5f)
                canvas.RotateRadians(sprite.RotationOrScale, center.X, center.Y);
            using (var paint = new SKPaint { Color = ToSk(sprite.Color), IsAntialias = true })
            {
                SKImage image;
                SKColor colour = paint.Color;
                if (SolidTextures.Contains(sprite.Data ?? string.Empty))
                {
                    canvas.DrawRect(rect, paint);
                    if (colour.Alpha < 255)
                        using (var glow = new SKPaint { Color = GlowColour(colour), IsAntialias = true, BlendMode = SKBlendMode.Plus })
                            canvas.DrawRect(rect, glow);
                }
                else if ((image = GameTextures.Image(sprite.Data)) != null)
                {
                    // The game multiplies the texture by the sprite's colour.
                    var source = SKRect.Create(0, 0, image.Width, image.Height);
                    using (SKColorFilter tint = SKColorFilter.CreateBlendMode(colour, SKBlendMode.Modulate))
                    using (var imagePaint = new SKPaint { ColorFilter = tint, IsAntialias = true, FilterQuality = SKFilterQuality.High })
                        canvas.DrawImage(image, source, rect, imagePaint);
                    if (colour.Alpha < 255)
                        using (SKColorFilter tint = SKColorFilter.CreateBlendMode(GlowColour(colour), SKBlendMode.Modulate))
                        using (var glow = new SKPaint { ColorFilter = tint, IsAntialias = true, FilterQuality = SKFilterQuality.High, BlendMode = SKBlendMode.Plus })
                            canvas.DrawImage(image, source, rect, glow);
                }
                else
                {
                    // Unloaded texture: a faint tinted box with its name, so layout stays readable.
                    paint.Color = paint.Color.WithAlpha((byte)(paint.Color.Alpha / 4));
                    canvas.DrawRect(rect, paint);
                    paint.Color = paint.Color.WithAlpha(160);
                    paint.Style = SKPaintStyle.Stroke;
                    canvas.DrawRect(rect, paint);
                    if (rect.Width > 24 && rect.Height > 10)
                        using (var font = new SKFont(SKTypeface.Default, Math.Min(12f, rect.Height * 0.6f)))
                        using (var text = new SKPaint { Color = paint.Color, IsAntialias = true })
                        {
                            canvas.Save();
                            canvas.ClipRect(rect);
                            canvas.DrawText(sprite.Data ?? string.Empty, rect.Left + 2, rect.Top + font.Size, font, text);
                            canvas.Restore();
                        }
                }
            }
            canvas.Restore();
        }

        // As the game's LCD renderer: a text sprite's width (the surface width when unset) is the line the text is
        // aligned on, and the font aligns each line within it.
        static void PaintGameText(SKCanvas canvas, MySprite sprite, Vector2 surfaceSize)
        {
            if (string.IsNullOrEmpty(sprite.Data))
                return;
            Vector2 size = sprite.Size ?? surfaceSize;
            Vector2 position = sprite.Position ?? surfaceSize / 2f;
            if (sprite.Alignment == TextAlignment.RIGHT)
                position.X -= size.X;
            else if (sprite.Alignment == TextAlignment.CENTER)
                position.X -= size.X * 0.5f;
            GameFonts.Get(sprite.FontId).Draw(canvas, sprite.Data, position, sprite.Color ?? Color.White,
                sprite.RotationOrScale, (int)sprite.Alignment, (int)Math.Round(size.X));
        }

        static void PaintText(SKCanvas canvas, MySprite sprite)
        {
            if (!sprite.Position.HasValue || string.IsNullOrEmpty(sprite.Data))
                return;
            float scale = sprite.RotationOrScale <= 0f ? 1f : sprite.RotationOrScale;
            using (SKFont font = PreviewFonts.Font(sprite.FontId, scale))
            using (var paint = new SKPaint { Color = ToSk(sprite.Color), IsAntialias = true })
            {
                float lineHeight = PreviewFonts.LineHeightAtScale1 * scale;
                float ascent = -font.Metrics.Ascent;
                float baselineOffset = (lineHeight - (font.Metrics.Descent - font.Metrics.Ascent)) * 0.5f + ascent;
                Vector2 p = sprite.Position.Value;
                string[] lines = sprite.Data.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    float width = PreviewFonts.Width(font, lines[i]);
                    float x = p.X;
                    if (sprite.Alignment == TextAlignment.CENTER)
                        x -= width * 0.5f;
                    else if (sprite.Alignment == TextAlignment.RIGHT)
                        x -= width;
                    canvas.DrawText(lines[i], x, p.Y + i * lineHeight + baselineOffset, font, paint);
                }
            }
        }
    }
}
