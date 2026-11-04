using System;
using System.Collections.Generic;
using Acrux.Rendering;
using VRageMath;

namespace Acrux.Controls.Chrome
{
    public enum CaptionGlyph
    {
        Close,
        Maximize,
        Minimize,
        Restore
    }

    /// <summary>
    /// Generated bitmaps for the caption buttons: each glyph alone, and a disc with the glyph cut out of it. White on
    /// transparent, tinted when drawn. One set per texture factory, shared by every window that factory renders.
    /// </summary>
    internal static class CaptionGlyphs
    {
        const int Size = 64;
        const float Stroke = .045f;
        static readonly Dictionary<IDynamicTextureFactory, IDynamicTexture[]> Textures =
            new Dictionary<IDynamicTextureFactory, IDynamicTexture[]>();
        static int _sets;

        // Stroke segments as x0, y0, x1, y1 in a unit square.
        static readonly float[][] Segments =
        {
            new[] { .36f, .36f, .64f, .64f, .64f, .36f, .36f, .64f },
            new[] { .3f, .6f, .5f, .4f, .5f, .4f, .7f, .6f },
            new[] { .3f, .4f, .5f, .6f, .5f, .6f, .7f, .4f },
            new[] { .32f, .46f, .5f, .28f, .5f, .28f, .68f, .46f, .32f, .54f, .5f, .72f, .5f, .72f, .68f, .54f }
        };

        // The glyph's texture, or the disc variant; false while it is being created.
        public static bool TryGet(RenderContext context, CaptionGlyph glyph, bool disc, out TextureSource source)
        {
            lock (Textures)
                return TryGetLocked(context, glyph, disc, out source);
        }

        static bool TryGetLocked(RenderContext context, CaptionGlyph glyph, bool disc, out TextureSource source)
        {
            source = default(TextureSource);
            IDynamicTextureFactory factory = context.DynamicTextures;
            if (factory == null)
                return false;
            IDynamicTexture[] set;
            if (!Textures.TryGetValue(factory, out set))
                Textures[factory] = set = new IDynamicTexture[8];
            int index = (int)glyph * 2 + (disc ? 1 : 0);
            IDynamicTexture texture = set[index];
            if (texture != null && texture.IsAvailable)
            {
                source = texture.Source;
                return true;
            }
            if (texture != null)
                texture.Dispose();
            texture = factory.Create("AcruxCaption" + _sets++ + "_" + glyph + (disc ? "Disc" : string.Empty),
                new Vector2I(Size, Size));
            set[index] = texture;
            if (texture == null || !texture.IsAvailable)
                return false;
            texture.Update(canvas => Paint(canvas, Segments[(int)glyph], disc));
            context.MarkVolatile();
            return false;
        }

        public static void Release()
        {
            lock (Textures)
                ReleaseLocked();
        }

        static void ReleaseLocked()
        {
            foreach (var set in Textures.Values)
                foreach (var texture in set)
                    if (texture != null)
                        texture.Dispose();
            Textures.Clear();
        }

        static void Paint(PixelCanvas canvas, float[] segments, bool disc)
        {
            float pixel = 1f / canvas.Width;
            for (int y = 0; y < canvas.Height; y++)
            for (int x = 0; x < canvas.Width; x++)
            {
                float u = (x + .5f) / canvas.Width;
                float v = (y + .5f) / canvas.Height;
                float glyph = Coverage(Stroke - Distance(segments, u, v), pixel);
                float alpha = disc
                    ? Coverage(.5f - (float)Math.Sqrt((u - .5f) * (u - .5f) + (v - .5f) * (v - .5f)), pixel) * (1f - glyph)
                    : glyph;
                canvas.SetPixel(x, y, new Color(255, 255, 255, (int)Math.Round(alpha * 255f)));
            }
        }

        static float Coverage(float signedDistance, float pixel)
        {
            return Math.Max(0f, Math.Min(1f, signedDistance / pixel + .5f));
        }

        static float Distance(float[] segments, float u, float v)
        {
            float best = float.MaxValue;
            for (int i = 0; i + 3 < segments.Length; i += 4)
            {
                float ax = segments[i], ay = segments[i + 1], bx = segments[i + 2], by = segments[i + 3];
                float dx = bx - ax, dy = by - ay;
                float t = Math.Max(0f, Math.Min(1f, ((u - ax) * dx + (v - ay) * dy) / (dx * dx + dy * dy)));
                float px = ax + t * dx - u, py = ay + t * dy - v;
                best = Math.Min(best, (float)Math.Sqrt(px * px + py * py));
            }
            return best;
        }
    }
}
