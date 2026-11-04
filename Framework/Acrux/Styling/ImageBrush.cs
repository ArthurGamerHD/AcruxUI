using System;
using Acrux.Rendering;
using VRageMath;

namespace Acrux.Styling
{
    /// <summary>Media.Stretch.</summary>
    public enum Stretch
    {
        None,
        Fill,
        Uniform,
        UniformToFill
    }

    /// <summary>Media.TileMode subset.</summary>
    public enum TileMode
    {
        None,
        Tile
    }

    /// <summary>
    /// ImageBrush over a renderer texture key. As an OpacityMask over a solid fill it
    /// renders the image tinted with that fill. Content is centred in <see cref="DestinationRect"/>;
    /// <see cref="Transform"/> honours scales.
    /// </summary>
    public sealed class ImageBrush : BackgroundBrush
    {
        const int MaxTiles = 2048;

        public ImageBrush()
        {
        }

        public ImageBrush(string source)
        {
            Source = source;
        }

        public string Source { get; set; }
        public Stretch Stretch { get; set; } = Stretch.Fill;
        public TileMode TileMode { get; set; }
        public RelativeRect DestinationRect { get; set; } = RelativeRect.Fill;
        public Transform Transform { get; set; }
        public RelativePoint TransformOrigin { get; set; } = RelativePoint.TopLeft;

        float _opacity = 1f;

        /// <summary>Brush.Opacity: the image's alpha (0-1), on top of the element's own Opacity.</summary>
        public float Opacity
        {
            get { return _opacity; }
            set { _opacity = MathHelper.Clamp(value, 0f, 1f); }
        }

        public override Color FallbackColor => Color.Transparent;

        internal ImageBrush WithOpacity(float opacity)
        {
            return new ImageBrush(Source)
            {
                Stretch = Stretch,
                TileMode = TileMode,
                DestinationRect = DestinationRect,
                Transform = Transform,
                TransformOrigin = TransformOrigin,
                Opacity = opacity
            };
        }

        protected override bool EqualsCore(BackgroundBrush other)
        {
            var brush = (ImageBrush)other;
            return string.Equals(Source, brush.Source, StringComparison.Ordinal) &&
                   Stretch == brush.Stretch &&
                   TileMode == brush.TileMode &&
                   DestinationRect.Equals(brush.DestinationRect) &&
                   Equals(Transform, brush.Transform) &&
                   TransformOrigin.Equals(brush.TransformOrigin) &&
                   Opacity.Equals(brush.Opacity);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = (Source ?? string.Empty).GetHashCode();
                hash = hash * 397 ^ (int)Stretch;
                hash = hash * 397 ^ (int)TileMode;
                hash = hash * 397 ^ DestinationRect.GetHashCode();
                hash = hash * 397 ^ (Transform == null ? 0 : Transform.GetHashCode());
                hash = hash * 397 ^ TransformOrigin.GetHashCode();
                return hash * 397 ^ Opacity.GetHashCode();
            }
        }

        internal void Draw(RenderContext context, RectangleF bounds)
        {
            if (string.IsNullOrWhiteSpace(Source) || bounds.Width <= 0f || bounds.Height <= 0f)
                return;
            if (Opacity < 1f)
            {
                using (context.PushOpacity(Opacity))
                    DrawImage(context, bounds);
                return;
            }
            DrawImage(context, bounds);
        }

        void DrawImage(RenderContext context, RectangleF bounds)
        {
            TextureSource texture = TextureSource.FromKey(Source);
            if (!context.Textures.IsAvailable(texture))
                texture = context.Textures.MissingTexture;

            Vector2 sourceSize;
            bool sized = context.Textures.TryGetSize(texture, out sourceSize);
            RectangleF tile = DestinationRect.ToPixels(bounds);
            RectangleF content = sized ? Arrange(tile, Stretch, sourceSize) : Arrange(tile, Stretch);
            if (Transform != null)
            {
                Vector2 origin = TransformOrigin.ToPixels(bounds);
                Vector2 scale = Transform.ScaleFactor;
                tile = Scale(tile, origin, scale);
                content = Scale(content, origin, scale);
            }
            if (tile.Width <= 0f || tile.Height <= 0f)
                return;

            using (context.PushClip(bounds))
            {
                if (TileMode == TileMode.None)
                {
                    using (context.PushClip(tile))
                        context.DrawTexture(texture, content, Color.White);
                    return;
                }

                int firstColumn = (int)Math.Floor((bounds.X - tile.X) / tile.Width);
                int firstRow = (int)Math.Floor((bounds.Y - tile.Y) / tile.Height);
                int columns = (int)Math.Ceiling((bounds.Right - tile.X) / tile.Width) - firstColumn;
                int rows = (int)Math.Ceiling((bounds.Bottom - tile.Y) / tile.Height) - firstRow;
                if ((long)columns * rows > MaxTiles)
                    return;
                for (int row = 0; row < rows; row++)
                for (int column = 0; column < columns; column++)
                {
                    var offset = new Vector2((firstColumn + column) * tile.Width, (firstRow + row) * tile.Height);
                    context.DrawTexture(texture, new RectangleF(content.Position + offset, content.Size), Color.White);
                }
            }
        }

        internal static RectangleF Arrange(RectangleF bounds, Stretch stretch, Vector2 sourceSize)
        {
            if (stretch == Stretch.Fill || sourceSize.X <= 0f || sourceSize.Y <= 0f)
                return bounds;
            float scale = stretch == Stretch.None ? 1f
                : stretch == Stretch.Uniform
                    ? Math.Min(bounds.Width / sourceSize.X, bounds.Height / sourceSize.Y)
                    : Math.Max(bounds.Width / sourceSize.X, bounds.Height / sourceSize.Y);
            Vector2 size = sourceSize * scale;
            return new RectangleF(bounds.Center - size * .5f, size);
        }

        internal static RectangleF Arrange(RectangleF bounds, Stretch stretch)
        {
            if (stretch == Stretch.Fill)
                return bounds;
            float side = stretch == Stretch.UniformToFill
                ? Math.Max(bounds.Width, bounds.Height)
                : Math.Min(bounds.Width, bounds.Height);
            return new RectangleF(bounds.Center - new Vector2(side * .5f), new Vector2(side));
        }

        static RectangleF Scale(RectangleF rect, Vector2 origin, Vector2 scale)
        {
            return new RectangleF(origin + (rect.Position - origin) * scale, rect.Size * scale);
        }
    }
}
