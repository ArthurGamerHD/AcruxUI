using System;
using System.Collections.Generic;
using Acrux.Styling;
using Acrux.Toolkit.Server;
using VRageMath;

namespace Acrux.Rendering
{
    // RGBA8 rows top to bottom, premultiplied as the game's DDS sprites store them.
    internal sealed class TexturePixels
    {
        internal TexturePixels(int width, int height, byte[] rgba)
        {
            if (width <= 0 || height <= 0 || rgba == null || rgba.Length < width * height * 4)
                throw new ArgumentException("Texture pixels need a positive size and width * height * 4 bytes.");
            Width = width;
            Height = height;
            Rgba = rgba;
        }

        internal int Width { get; }
        internal int Height { get; }
        internal byte[] Rgba { get; }
    }

    internal enum MaskState
    {
        Loading,
        Ready,
        Failed
    }

    // Sprites take one tint only, so a gradient is drawn as a texture Acrux paints: a Border's fill with its rounded
    // corners cut in, or a copy of an OpacityMask texture with the gradient multiplied into it.
    internal static class GradientTexture
    {
        internal const int MaxMaskSide = 512;

        sealed class Mask
        {
            public MaskState State;
            public TexturePixels Pixels;
            // A failure seen on the decoding thread, reported from the render thread on the next request.
            public string Failure;
        }

        static readonly Dictionary<string, Mask> Masks = new Dictionary<string, Mask>(StringComparer.Ordinal);
        // Bumped by ClearMasks, so a decode that finishes after the session unloaded is dropped.
        static int _generation;

        // The host finds the file on the render thread (definitions can be added at run time) and the decode runs in
        // the background; the result is kept for the session, failures included.
        internal static MaskState GetMask(string source, out TexturePixels pixels)
        {
            pixels = null;
            if (string.IsNullOrWhiteSpace(source))
                return MaskState.Failed;

            Mask mask;
            string failure = null;
            lock (Masks)
            {
                if (Masks.TryGetValue(source, out mask))
                {
                    failure = mask.Failure;
                    mask.Failure = null;
                    pixels = mask.Pixels;
                }
            }
            if (mask != null)
            {
                if (failure != null)
                    ExternalInterop.WarnOnce(failure);
                return mask.State;
            }

            ExternalInterop.WarnOnce(
                "A GradientBrush with an OpacityMask is slow: it is re-encoded into the mask on the CPU. The texture '" +
                source + "' will be cached.");
            mask = new Mask { State = MaskState.Loading };
            int generation;
            lock (Masks)
            {
                Masks[source] = mask;
                generation = _generation;
            }

            Func<TexturePixels> decode = null;
            try
            {
                Func<string, int, Func<TexturePixels>> resolve = ExternalInterop.LoadTexturePixels;
                if (resolve != null)
                    decode = resolve(source, MaxMaskSide);
            }
            catch (Exception error)
            {
                Complete(source, mask, generation, null, error.Message);
            }
            if (mask.State != MaskState.Loading)
                return GetMask(source, out pixels);
            if (decode == null)
            {
                Complete(source, mask, generation, null, null);
                return GetMask(source, out pixels);
            }

            Action work = () =>
            {
                TexturePixels decoded = null;
                string error = null;
                try
                {
                    decoded = decode();
                }
                catch (Exception exception)
                {
                    error = exception.Message;
                }
                Complete(source, mask, generation, decoded, error);
            };
            Action<Action> background = ExternalInterop.RunInBackground;
            try
            {
                if (background != null)
                    background(work);
                else
                    work();
            }
            catch (Exception error)
            {
                Complete(source, mask, generation, null, error.Message);
            }
            lock (Masks)
            {
                pixels = mask.Pixels;
                return mask.State;
            }
        }

        static void Complete(string source, Mask mask, int generation, TexturePixels pixels, string error)
        {
            lock (Masks)
            {
                if (generation != _generation)
                    return;
                mask.Pixels = pixels;
                mask.State = pixels != null ? MaskState.Ready : MaskState.Failed;
                if (pixels == null)
                    mask.Failure = "The OpacityMask texture '" + source + "' could not be read" +
                                   (error == null ? string.Empty : " (" + error + ")") +
                                   "; the GradientBrush is drawn as one color.";
            }
        }

        internal static void ClearMasks()
        {
            lock (Masks)
            {
                Masks.Clear();
                _generation++;
            }
        }

        // bounds is in layout units; the canvas is stretched over it. cornerRadius is in texels.
        internal static void PaintFill(PixelCanvas canvas, GradientBrush brush, Vector2 bounds, Vector4 cornerRadius)
        {
            var sampler = new GradientSampler(brush);
            var area = new RectangleF(0f, 0f, bounds.X, bounds.Y);
            float toLayoutX = bounds.X / canvas.Width;
            float toLayoutY = bounds.Y / canvas.Height;
            bool rounded = Math.Max(Math.Max(cornerRadius.X, cornerRadius.Y), Math.Max(cornerRadius.Z, cornerRadius.W)) > 0f;
            for (int y = 0; y < canvas.Height; y++)
            for (int x = 0; x < canvas.Width; x++)
            {
                float coverage = rounded
                    ? BorderTextureCache.RoundedRectCoverage(x + .5f, y + .5f, 0f, 0f, canvas.Width, canvas.Height,
                        cornerRadius)
                    : 1f;
                if (coverage <= 0f)
                {
                    canvas.SetPixel(x, y, Color.Transparent);
                    continue;
                }
                Vector4 color = sampler.Sample(new Vector2((x + .5f) * toLayoutX, (y + .5f) * toLayoutY), area);
                color.W *= coverage;
                canvas.SetPixelDithered(x, y, color);
            }
        }

        // The mask covers maskBounds and the gradient spans bounds, both in layout units.
        internal static void PaintMasked(
            PixelCanvas canvas,
            GradientBrush brush,
            RectangleF bounds,
            RectangleF maskBounds,
            TexturePixels mask)
        {
            var sampler = new GradientSampler(brush);
            var area = new RectangleF(0f, 0f, bounds.Width, bounds.Height);
            Vector2 offset = maskBounds.Position - bounds.Position;
            byte[] pixels = mask.Rgba;
            for (int y = 0; y < canvas.Height; y++)
            {
                float v = (y + .5f) / canvas.Height;
                int row = Math.Min(mask.Height - 1, (int)(v * mask.Height)) * mask.Width;
                for (int x = 0; x < canvas.Width; x++)
                {
                    float u = (x + .5f) / canvas.Width;
                    int index = (row + Math.Min(mask.Width - 1, (int)(u * mask.Width))) * 4;
                    byte alpha = pixels[index + 3];
                    if (alpha == 0)
                    {
                        canvas.SetPixel(x, y, Color.Transparent);
                        continue;
                    }
                    // As a solid background tints the mask, but per texel. The canvas premultiplies, so the texel goes
                    // back to straight alpha first; the clamp keeps a straight-alpha white texture white.
                    Vector4 color = sampler.Sample(
                        offset + new Vector2(u * maskBounds.Width, v * maskBounds.Height), area);
                    float straight = 1f / alpha;
                    color.X *= Math.Min(1f, pixels[index] * straight);
                    color.Y *= Math.Min(1f, pixels[index + 1] * straight);
                    color.Z *= Math.Min(1f, pixels[index + 2] * straight);
                    color.W *= alpha / 255f;
                    canvas.SetPixelDithered(x, y, color);
                }
            }
        }
    }

    // Shares baked gradient textures between Borders that draw the same brush at the same size (a grid of cards),
    // across every surface of one kind and paying mod.
    internal sealed class GradientTextureCache
    {
        static int _nextName;

        readonly SharedTextureTable<Key> _table = new SharedTextureTable<Key>();

        internal struct Key : IEquatable<Key>
        {
            public GradientBrush Brush;
            public Vector2I Size;
            public Vector4 Radius;
            public Vector2 Layout;
            public string Mask;
            public RectangleF MaskBounds;

            public bool Equals(Key other)
            {
                return Size == other.Size && Radius == other.Radius && Layout == other.Layout &&
                       string.Equals(Mask, other.Mask, StringComparison.Ordinal) && MaskBounds == other.MaskBounds &&
                       GradientBrushes.AreEqual(Brush, other.Brush);
            }

            public override bool Equals(object obj)
            {
                return obj is Key && Equals((Key)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = GradientBrushes.Hash(Brush);
                    hash = hash * 397 ^ Size.GetHashCode();
                    hash = hash * 397 ^ Radius.GetHashCode();
                    hash = hash * 397 ^ Layout.GetHashCode();
                    hash = hash * 397 ^ (Mask == null ? 0 : Mask.GetHashCode());
                    return hash * 397 ^ MaskBounds.GetHashCode();
                }
            }
        }

        internal sealed class Lease : IDisposable
        {
            readonly SharedTextureEntry _entry;
            bool _disposed;

            internal Lease(GradientTextureCache owner, Key key, SharedTextureEntry entry)
            {
                Owner = owner;
                CacheKey = key;
                _entry = entry;
            }

            internal Key CacheKey { get; }
            internal GradientTextureCache Owner { get; }
            internal IDynamicTexture Texture => _disposed ? null : _entry.Texture;

            public void Dispose()
            {
                if (_disposed)
                    return;
                _disposed = true;
                _entry.Release();
            }
        }

        // Null when the texture cannot be made (no generated textures, or the budget is spent).
        internal Lease Acquire(IDynamicTextureFactory textures, Key key, Action<PixelCanvas, GradientBrush> paint)
        {
            if (key.Size.X <= 0 || key.Size.Y <= 0 || key.Brush == null)
                return null;
            GradientBrush snapshot = null;
            bool created;
            SharedTextureEntry entry = _table.Acquire(key, null,
                // The kept key holds a snapshot, so later edits of the caller's brush are seen as a new key.
                lookup =>
                {
                    lookup.Brush = snapshot;
                    return lookup;
                },
                () =>
                {
                    snapshot = GradientBrushes.Clone(key.Brush);
                    IDynamicTexture texture = textures.Create(
                        "AcruxGradient_" + System.Threading.Interlocked.Increment(ref _nextName), key.Size);
                    if (texture == null)
                        return null;
                    if (!texture.IsAvailable || !texture.Update(canvas => paint(canvas, snapshot)))
                    {
                        texture.Dispose();
                        return null;
                    }
                    return texture;
                },
                out created);
            return entry == null ? null : new Lease(this, (Key)entry.StoredKey, entry);
        }

        internal void Clear()
        {
            _table.Clear();
        }
    }
}
