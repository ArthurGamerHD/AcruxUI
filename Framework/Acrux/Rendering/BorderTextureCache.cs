using System;
using System.Collections.Generic;
using VRageMath;

namespace Acrux.Rendering
{
    /// <summary>
    /// Shares rounded Border textures by their geometry: a white fill mask, or a white border ring, both tinted when
    /// drawn so that color changes never need a new texture. Corner radii are top-left, top-right, bottom-right and
    /// bottom-left, like CornerRadius. The textures are shared by every surface of the same kind whose UI belongs to
    /// the same mod (every LCD of one client mod, for example), and unused entries stay cached for a while.
    /// </summary>
    public sealed class BorderTextureCache
    {
        const float RADIUS_PRECISION = 1000f;
        readonly IDynamicTextureFactory _textures;
        // For a factory whose textures cannot be shared between surfaces.
        BorderTextureStore _localStore;
        GradientTextureCache _localGradients;

        public BorderTextureCache(IDynamicTextureFactory textures)
        {
            if (textures == null)
                throw new ArgumentNullException(nameof(textures));
            _textures = textures;
        }

        // Resolved per use: the paying mod (and so the shared store) follows the surface's root.
        internal IDynamicTextureFactory Textures => _textures;

        internal GradientTextureCache Gradients =>
            SharedTextureStores.Gradient(_textures) ?? (_localGradients ?? (_localGradients = new GradientTextureCache()));

        BorderTextureStore Store =>
            SharedTextureStores.Border(_textures) ?? (_localStore ?? (_localStore = new BorderTextureStore()));

        public struct Key : IEquatable<Key>
        {
            public readonly int Width;
            public readonly int Height;
            public readonly Vector4I RadiusMilli;
            public readonly Vector4 BorderThickness;
            public readonly uint BorderColor;
            public readonly uint BackgroundColor;
            public readonly bool Styled;

            public Key(
                Vector2I size,
                Vector4 radius,
                Vector4 borderThickness,
                Color borderColor,
                Color backgroundColor,
                bool styled)
            {
                Width = size.X;
                Height = size.Y;
                RadiusMilli = new Vector4I(ToMilli(radius.X), ToMilli(radius.Y), ToMilli(radius.Z), ToMilli(radius.W));
                Styled = styled;
                BorderThickness = styled ? Vector4.Max(borderThickness, Vector4.Zero) : Vector4.Zero;
                BorderColor = styled ? Pack(borderColor) : 0u;
                BackgroundColor = styled ? Pack(backgroundColor) : 0u;
            }

            public Vector4 Radius => new Vector4(RadiusMilli.X, RadiusMilli.Y, RadiusMilli.Z, RadiusMilli.W) /
                RADIUS_PRECISION;

            static int ToMilli(float radius)
            {
                return Math.Max(0, (int)Math.Round(radius * RADIUS_PRECISION));
            }

            public Color UnpackedBorderColor => Unpack(BorderColor);
            public Color UnpackedBackgroundColor => Unpack(BackgroundColor);

            public bool Equals(Key other)
            {
                return Width == other.Width &&
                       Height == other.Height &&
                       RadiusMilli.X == other.RadiusMilli.X && RadiusMilli.Y == other.RadiusMilli.Y &&
                       RadiusMilli.Z == other.RadiusMilli.Z && RadiusMilli.W == other.RadiusMilli.W &&
                       BorderThickness == other.BorderThickness &&
                       BorderColor == other.BorderColor &&
                       BackgroundColor == other.BackgroundColor &&
                       Styled == other.Styled;
            }

            public override bool Equals(object obj)
            {
                return obj is Key && Equals((Key)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = Width;
                    hash = (hash * 397) ^ Height;
                    hash = (hash * 397) ^ RadiusMilli.X;
                    hash = (hash * 397) ^ RadiusMilli.Y;
                    hash = (hash * 397) ^ RadiusMilli.Z;
                    hash = (hash * 397) ^ RadiusMilli.W;
                    hash = (hash * 397) ^ BorderThickness.GetHashCode();
                    hash = (hash * 397) ^ (int)BorderColor;
                    hash = (hash * 397) ^ (int)BackgroundColor;
                    hash = (hash * 397) ^ (Styled ? 1 : 0);
                    return hash;
                }
            }

            static uint Pack(Color color)
            {
                return (uint)(color.R |
                    (color.G << 8) |
                    (color.B << 16) |
                    (color.A << 24));
            }

            static Color Unpack(uint color)
            {
                return new Color(
                    (byte)(color & 0xff),
                    (byte)((color >> 8) & 0xff),
                    (byte)((color >> 16) & 0xff),
                    (byte)((color >> 24) & 0xff));
            }
        }

        public sealed class Lease : IDisposable
        {
            readonly Key _key;
            bool _disposed;

            readonly SharedTextureEntry _entry;

            internal Lease(Key key, SharedTextureEntry entry, bool isNew)
            {
                _key = key;
                _entry = entry;
                Texture = entry.Texture;
                IsNew = isNew;
            }

            public IDynamicTexture Texture { get; private set; }

            /// <summary>True when acquiring this lease created the texture rather than sharing an existing one.</summary>
            public bool IsNew { get; }

            /// <summary>True when the texture was created during <paramref name="frame"/>, where SE does not draw it yet.</summary>
            public bool IsCreatedIn(object frame)
            {
                return frame != null && ReferenceEquals(_entry.CreatedIn, frame);
            }
            public Key CacheKey => _key;

            public void Dispose()
            {
                if (_disposed)
                    return;

                _disposed = true;
                _entry.Release();
                Texture = null;
            }
        }

        public Lease AcquireMask(Vector2I supportedSize, Vector4 cornerRadius, object frame = null)
        {
            return Store.Acquire(_textures, frame,
                new Key(
                    supportedSize,
                    ClampRadius(supportedSize, cornerRadius),
                    Vector4.Zero,
                    Color.White,
                    Color.White,
                    false));
        }

        public Lease AcquireRing(Vector2I supportedSize, Vector4 cornerRadius, Vector4 borderThickness, object frame = null)
        {
            return Store.Acquire(_textures, frame,
                new Key(
                    supportedSize,
                    ClampRadius(supportedSize, cornerRadius),
                    borderThickness,
                    Color.White,
                    Color.White,
                    true));
        }

        static Vector4 ClampRadius(Vector2I size, Vector4 cornerRadius)
        {
            return Vector4.Min(Vector4.Max(cornerRadius, Vector4.Zero), new Vector4(Math.Min(size.X, size.Y) * 0.5f));
        }

        internal static void PaintRoundedMask(PixelCanvas canvas, Vector4 cornerRadius)
        {
            canvas.Clear(Color.Transparent);
            for (int y = 0; y < canvas.Height; y++)
            for (int x = 0; x < canvas.Width; x++)
            {
                float coverage = RoundedRectCoverage(x + 0.5f, y + 0.5f, 0f, 0f, canvas.Width, canvas.Height,
                    cornerRadius);
                if (coverage > 0f)
                    canvas.BlendPixel(x, y, Color.White, coverage);
            }
        }

        internal static void PaintRoundedRing(PixelCanvas canvas, Vector4 cornerRadius, Vector4 borderThickness)
        {
            canvas.Clear(Color.Transparent);

            float left = Math.Max(0f, borderThickness.X);
            float top = Math.Max(0f, borderThickness.Y);
            float right = Math.Max(0f, borderThickness.Z);
            float bottom = Math.Max(0f, borderThickness.W);
            float innerWidth = canvas.Width - left - right;
            float innerHeight = canvas.Height - top - bottom;
            Vector4 innerRadius = Vector4.Max(Vector4.Zero, cornerRadius - new Vector4(
                Math.Max(left, top), Math.Max(right, top), Math.Max(right, bottom), Math.Max(left, bottom)));

            for (int y = 0; y < canvas.Height; y++)
            for (int x = 0; x < canvas.Width; x++)
            {
                float sampleX = x + 0.5f;
                float sampleY = y + 0.5f;
                float outerCoverage = RoundedRectCoverage(sampleX, sampleY, 0f, 0f, canvas.Width, canvas.Height, cornerRadius);
                if (outerCoverage <= 0f)
                    continue;
                float innerCoverage = innerWidth > 0f && innerHeight > 0f
                    ? RoundedRectCoverage(sampleX, sampleY, left, top, innerWidth, innerHeight, innerRadius)
                    : 0f;
                float ringCoverage = outerCoverage * (1f - innerCoverage);
                if (ringCoverage > 0f)
                    canvas.BlendPixel(x, y, Color.White, ringCoverage);
            }
        }

        // Antialiased coverage of a rectangle whose corner in the sample's quadrant uses its own radius.
        internal static float RoundedRectCoverage(
            float sampleX,
            float sampleY,
            float left,
            float top,
            float width,
            float height,
            Vector4 cornerRadius)
        {
            if (width <= 0f || height <= 0f)
                return 0f;

            float right = left + width;
            float bottom = top + height;
            bool isLeft = sampleX < left + width * 0.5f;
            bool isTop = sampleY < top + height * 0.5f;
            float corner = isTop ? (isLeft ? cornerRadius.X : cornerRadius.Y) : (isLeft ? cornerRadius.W : cornerRadius.Z);
            float radius = Math.Min(Math.Max(0f, corner), Math.Min(width, height) * 0.5f);

            if (radius <= 0f)
            {
                float dx = Math.Min(sampleX - left, right - sampleX);
                float dy = Math.Min(sampleY - top, bottom - sampleY);
                return MathHelper.Clamp(Math.Min(dx, dy) + 0.5f, 0f, 1f);
            }

            float nearestX = MathHelper.Clamp(sampleX, left + radius, right - radius);
            float nearestY = MathHelper.Clamp(sampleY, top + radius, bottom - radius);
            float offsetX = sampleX - nearestX;
            float offsetY = sampleY - nearestY;
            float distance = (float)Math.Sqrt(offsetX * offsetX + offsetY * offsetY);
            return MathHelper.Clamp(radius + 0.5f - distance, 0f, 1f);
        }
    }

    // The textures behind BorderTextureCache, for one kind of surface and paying mod.
    internal sealed class BorderTextureStore
    {
        readonly SharedTextureTable<BorderTextureCache.Key> _table = new SharedTextureTable<BorderTextureCache.Key>();

        internal BorderTextureCache.Lease Acquire(IDynamicTextureFactory textures, object frame, BorderTextureCache.Key key)
        {
            if (key.Width <= 0 || key.Height <= 0)
                return null;
            bool created;
            SharedTextureEntry entry = _table.Acquire(key, frame, null, () => Create(textures, key), out created);
            return entry == null ? null : new BorderTextureCache.Lease(key, entry, created);
        }

        internal void Clear()
        {
            _table.Clear();
        }

        static IDynamicTexture Create(IDynamicTextureFactory textures, BorderTextureCache.Key key)
        {
            string name = "AcruxBorderShared_" +
                          key.Width + "x" + key.Height +
                          "_r" + key.RadiusMilli.X + "_" + key.RadiusMilli.Y + "_" + key.RadiusMilli.Z + "_" +
                          key.RadiusMilli.W +
                          (key.Styled
                              ? "_ring" + key.BorderThickness.X + "_" + key.BorderThickness.Y + "_" +
                                key.BorderThickness.Z + "_" + key.BorderThickness.W
                              : "_mask");
            IDynamicTexture texture = textures.Create(name, new Vector2I(key.Width, key.Height));
            if (texture == null)
                return null;
            if (!texture.IsAvailable)
            {
                texture.Dispose();
                return null;
            }
            if (key.Styled)
                texture.Update(canvas => BorderTextureCache.PaintRoundedRing(canvas, key.Radius, key.BorderThickness));
            else
                texture.Update(canvas => BorderTextureCache.PaintRoundedMask(canvas, key.Radius));
            return texture;
        }
    }
}
