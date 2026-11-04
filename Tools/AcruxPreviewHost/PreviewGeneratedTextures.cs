using System;
using System.Collections.Generic;
using System.Threading;
using SkiaSharp;
using VRage.Game.ModAPI;
using VRage.Utils;
using VRageMath;

namespace AcruxPreview
{
    /// <summary>
    /// The game's generated textures (MyAPIGateway.GeneratedTextures) in memory: what Acrux draws through dynamic
    /// textures (rounded Border masks and rings, arcs, paths, mod controls painting pixels) shows in the preview as in
    /// game. Pixels are RGBA, premultiplied, as <c>PixelCanvas</c> writes them; the rasterizer draws a sprite whose
    /// data names one of these textures from its last update.
    /// </summary>
    public sealed class PreviewGeneratedTextures : IMyGeneratedTextures
    {
        public const int MaxSide = 2048;
        static readonly object Sync = new object();
        static readonly Dictionary<string, Texture> Live = new Dictionary<string, Texture>(StringComparer.Ordinal);
        static int _next;

        public bool IsSupported => true;
        public int MaxTextureSide => MaxSide;

        public IMyGeneratedTexture Create(string nameHint, Vector2I size, MyGeneratedTextureFormat format = MyGeneratedTextureFormat.Srgb)
        {
            if (size.X <= 0 || size.Y <= 0 || size.X > MaxSide || size.Y > MaxSide)
                throw new ArgumentOutOfRangeException(nameof(size));
            string name = "AcruxPreviewGenerated/" + (nameHint ?? "Texture") + "/" + Interlocked.Increment(ref _next);
            var texture = new Texture(name, size, format);
            lock (Sync)
                Live[name] = texture;
            return texture;
        }

        /// <summary>The generated texture named <paramref name="name"/> as last updated; null when there is none.</summary>
        public static SKImage Image(string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;
            lock (Sync)
            {
                Texture texture;
                return Live.TryGetValue(name, out texture) ? texture.Image : null;
            }
        }

        /// <summary>Whether <paramref name="name"/> is a live generated texture (so the sprite is defined).</summary>
        public static bool IsDefined(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;
            lock (Sync)
                return Live.ContainsKey(name);
        }

        public static Vector2 Size(string name)
        {
            lock (Sync)
            {
                Texture texture;
                return name != null && Live.TryGetValue(name, out texture) ? new Vector2(texture.Size.X, texture.Size.Y) : Vector2.Zero;
            }
        }

        sealed class Texture : IMyGeneratedTexture
        {
            readonly byte[] _pixels;
            SKImage _image;
            bool _disposed;

            public Texture(string name, Vector2I size, MyGeneratedTextureFormat format)
            {
                Name = name;
                Size = size;
                Format = format;
                MaterialId = MyStringId.GetOrCompute(name);
                _pixels = new byte[size.X * size.Y * 4];
            }

            public string Name { get; }
            public MyStringId MaterialId { get; }
            public Vector2I Size { get; }
            public MyGeneratedTextureFormat Format { get; }
            public int ByteCount => _pixels.Length;
            public bool IsValid => !_disposed;

            public SKImage Image
            {
                get { return _image; }
            }

            public byte[] BeginUpdate()
            {
                return _disposed ? null : _pixels;
            }

            public void EndUpdate(byte[] pixels)
            {
                if (_disposed || pixels == null)
                    return;
                var info = new SKImageInfo(Size.X, Size.Y, SKColorType.Rgba8888, SKAlphaType.Premul);
                SKImage image;
                using (SKData data = SKData.CreateCopy(pixels))
                    image = SKImage.FromPixels(info, data, Size.X * 4);
                lock (Sync)
                {
                    SKImage old = _image;
                    _image = image;
                    if (old != null)
                        old.Dispose();
                }
            }

            public void Dispose()
            {
                lock (Sync)
                {
                    if (_disposed)
                        return;
                    _disposed = true;
                    Live.Remove(Name);
                    if (_image != null)
                        _image.Dispose();
                    _image = null;
                }
            }
        }
    }
}
