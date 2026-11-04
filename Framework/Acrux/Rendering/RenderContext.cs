using System;
using Acrux.Core;
using Acrux.Styling;
using VRageMath;

namespace Acrux.Rendering
{
    /// <summary>
    /// Per-pass rendering state shared by controls. It applies inherited opacity
    /// before forwarding renderer-neutral primitives to the active backend.
    /// </summary>
    public sealed class RenderContext : global::Acrux.Toolkit.IExternalPeerHost
    {
        object _externalPeer;
        object global::Acrux.Toolkit.IExternalPeerHost.ExternalPeer { get { return _externalPeer; } set { _externalPeer = value; } }

        readonly IUiRenderer _renderer;
        internal float RenderScaling { get; private set; } = 1f;
        Vector2 _renderOffset;

        internal RenderContext(IUiRenderer renderer, float opacity, float scaling, Vector2 origin)
            : this(renderer, opacity)
        {
            RenderScaling = scaling;
            _renderOffset = origin * (1f - scaling);
        }

        internal Vector2 ToPixels(Vector2 point)
        {
            return _renderOffset + point * RenderScaling;
        }

        // Scales everything drawn until disposal by scale around origin.
        internal IDisposable PushScale(Vector2 origin, float scale)
        {
            Vector2 offset = _renderOffset;
            float scaling = RenderScaling;
            _renderOffset = offset + origin * scaling * (1f - scale);
            RenderScaling = scaling * scale;
            return new ScaleScope(this, offset, scaling);
        }

        // Moves everything drawn until disposal by offset logical units.
        internal IDisposable PushTranslate(Vector2 offset)
        {
            Vector2 previous = _renderOffset;
            _renderOffset = previous + offset * RenderScaling;
            return new ScaleScope(this, previous, RenderScaling);
        }

        sealed class ScaleScope : IDisposable
        {
            readonly RenderContext _context;
            readonly Vector2 _offset;
            readonly float _scaling;
            bool _disposed;

            public ScaleScope(RenderContext context, Vector2 offset, float scaling)
            {
                _context = context;
                _offset = offset;
                _scaling = scaling;
            }

            public void Dispose()
            {
                if (_disposed)
                    return;
                _disposed = true;
                _context._renderOffset = _offset;
                _context.RenderScaling = _scaling;
            }
        }

        internal RectangleF ToPixels(RectangleF bounds)
        {
            return new RectangleF(ToPixels(bounds.Position), bounds.Size * RenderScaling);
        }

        public RenderContext(IUiRenderer renderer, float opacity)
        {
            if (renderer == null)
                throw new ArgumentNullException(nameof(renderer));
            _renderer = renderer;
            Opacity = MathHelper.Clamp(opacity, 0f, 1f);
        }

        /// <summary>The opacity everything is drawn with: the frame's, times each enclosing element's Opacity.</summary>
        public float Opacity { get; private set; }

        // Multiplies the opacity of everything drawn until disposal by opacity.
        internal IDisposable PushOpacity(float opacity)
        {
            float previous = Opacity;
            Opacity = previous * MathHelper.Clamp(opacity, 0f, 1f);
            return new OpacityScope(this, previous);
        }

        sealed class OpacityScope : IDisposable
        {
            readonly RenderContext _context;
            readonly float _opacity;
            bool _disposed;

            public OpacityScope(RenderContext context, float opacity)
            {
                _context = context;
                _opacity = opacity;
            }

            public void Dispose()
            {
                if (_disposed)
                    return;
                _disposed = true;
                _context.Opacity = _opacity;
            }
        }
        internal bool IsVolatile { get; private set; }

        // Type of the element that last made this frame volatile, for render diagnostics.
        internal string VolatileSource { get; private set; }

        internal void MarkVolatile(object source = null)
        {
            IsVolatile = true;
            if (source != null)
                VolatileSource = source.GetType().Name;
        }
        public ITextMetrics TextMetrics => _renderer.TextMetrics;
        public ITextureResolver Textures => _renderer.Textures;
        public IDynamicTextureFactory DynamicTextures => _renderer.DynamicTextures;
        public BorderTextureCache BorderTextures => _renderer.BorderTextures;

        public TRenderer GetRenderer<TRenderer>() where TRenderer : class
        {
            return _renderer as TRenderer;
        }

        readonly System.Collections.Generic.List<RectangleF> _clips = new System.Collections.Generic.List<RectangleF>();

        public IDisposable PushClip(RectangleF bounds)
        {
            RectangleF pixels = ToPixels(bounds);
            _clips.Add(_clips.Count == 0 ? pixels : Intersect(_clips[_clips.Count - 1], pixels));
            return new ClipScope(this, _renderer.PushClip(pixels));
        }

        // True when bounds lies entirely outside the active clip, so nothing drawn inside it can show.
        internal bool IsOutsideClip(RectangleF bounds)
        {
            if (_clips.Count == 0)
                return false;
            RectangleF clip = _clips[_clips.Count - 1];
            RectangleF pixels = ToPixels(bounds);
            return pixels.X >= clip.X + clip.Width || pixels.X + pixels.Width <= clip.X ||
                   pixels.Y >= clip.Y + clip.Height || pixels.Y + pixels.Height <= clip.Y;
        }

        static RectangleF Intersect(RectangleF a, RectangleF b)
        {
            float left = Math.Max(a.X, b.X);
            float top = Math.Max(a.Y, b.Y);
            float right = Math.Min(a.X + a.Width, b.X + b.Width);
            float bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);
            return new RectangleF(left, top, Math.Max(0f, right - left), Math.Max(0f, bottom - top));
        }

        sealed class ClipScope : IDisposable
        {
            readonly RenderContext _context;
            readonly IDisposable _rendererScope;
            bool _disposed;

            public ClipScope(RenderContext context, IDisposable rendererScope)
            {
                _context = context;
                _rendererScope = rendererScope;
            }

            public void Dispose()
            {
                if (_disposed)
                    return;
                _disposed = true;
                _context._clips.RemoveAt(_context._clips.Count - 1);
                if (_rendererScope != null)
                    _rendererScope.Dispose();
            }
        }

        public IDisposable Clip(RectangleF bounds)
        {
            return PushClip(bounds);
        }

        public void FillRectangle(RectangleF bounds, Color color)
        {
            _renderer.FillRectangle(ToPixels(bounds), ApplyOpacity(color));
        }

        public void Fill(RectangleF bounds, Color color)
        {
            FillRectangle(bounds, color);
        }

        public void Fill(RectangleF bounds, BackgroundBrush brush)
        {
            if (brush == null)
                return;

            ImageBrush imageBrush = brush as ImageBrush;
            if (imageBrush != null)
            {
                imageBrush.Draw(this, bounds);
                return;
            }

            TextureBrush textureBrush = brush as TextureBrush;
            if (textureBrush != null)
            {
                TextureSource texture = TextureSource.FromKey(textureBrush.Source);
                if (Textures.IsAvailable(texture))
                {
                    DrawTexture(texture, bounds, textureBrush.Tint);
                    return;
                }
            }

            CompositeTextureBrush compositeBrush = brush as CompositeTextureBrush;
            if (compositeBrush != null && DrawCompositeTexture(bounds, compositeBrush))
                return;

            NineSliceTextureBrush nineSliceBrush = brush as NineSliceTextureBrush;
            if (nineSliceBrush != null && DrawNineSliceTexture(bounds, nineSliceBrush))
                return;

            FillRectangle(bounds, brush.FallbackColor);
        }

        bool DrawCompositeTexture(RectangleF bounds, CompositeTextureBrush brush)
        {
            TextureSource start = TextureSource.FromKey(brush.StartSource);
            TextureSource center = TextureSource.FromKey(brush.CenterSource);
            TextureSource end = TextureSource.FromKey(brush.EndSource);
            if (!Textures.IsAvailable(start) ||
                !Textures.IsAvailable(center) ||
                !Textures.IsAvailable(end))
                return false;

            float crossLength = brush.Orientation == CompositeBrushOrientation.Horizontal
                ? bounds.Height
                : bounds.Width;
            if (crossLength <= 0f)
                return true;

            float scale = crossLength / brush.ReferenceCrossLength;
            float startLength = Math.Max(0f, brush.StartLength * scale);
            float endLength = Math.Max(0f, brush.EndLength * scale);
            float availableLength = brush.Orientation == CompositeBrushOrientation.Horizontal
                ? Math.Max(0f, bounds.Width)
                : Math.Max(0f, bounds.Height);
            float capsLength = startLength + endLength;
            if (capsLength > availableLength && capsLength > 0f)
            {
                float capScale = availableLength / capsLength;
                startLength *= capScale;
                endLength *= capScale;
            }
            float centerLength = Math.Max(0f, availableLength - startLength - endLength);
            if (brush.Orientation == CompositeBrushOrientation.Horizontal)
            {
                if (startLength > 0f)
                    DrawTexture(start, new RectangleF(
                        bounds.X, bounds.Y, startLength, bounds.Height), brush.Tint);
                if (centerLength > 0f)
                    DrawTexture(center, new RectangleF(
                        bounds.X + startLength, bounds.Y, centerLength, bounds.Height), brush.Tint);
                if (endLength > 0f)
                    DrawTexture(end, new RectangleF(
                        bounds.Right - endLength, bounds.Y, endLength, bounds.Height), brush.Tint);
            }
            else
            {
                if (startLength > 0f)
                    DrawTexture(start, new RectangleF(
                        bounds.X, bounds.Y, bounds.Width, startLength), brush.Tint);
                if (centerLength > 0f)
                    DrawTexture(center, new RectangleF(
                        bounds.X, bounds.Y + startLength, bounds.Width, centerLength), brush.Tint);
                if (endLength > 0f)
                    DrawTexture(end, new RectangleF(
                        bounds.X, bounds.Bottom - endLength, bounds.Width, endLength), brush.Tint);
            }
            return true;
        }

        bool DrawNineSliceTexture(RectangleF bounds, NineSliceTextureBrush brush)
        {
            TextureSource leftTop = TextureSource.FromKey(brush.LeftTopSource);
            TextureSource centerTop = TextureSource.FromKey(brush.CenterTopSource);
            TextureSource rightTop = TextureSource.FromKey(brush.RightTopSource);
            TextureSource leftCenter = TextureSource.FromKey(brush.LeftCenterSource);
            TextureSource center = TextureSource.FromKey(brush.CenterSource);
            TextureSource rightCenter = TextureSource.FromKey(brush.RightCenterSource);
            TextureSource leftBottom = TextureSource.FromKey(brush.LeftBottomSource);
            TextureSource centerBottom = TextureSource.FromKey(brush.CenterBottomSource);
            TextureSource rightBottom = TextureSource.FromKey(brush.RightBottomSource);

            if (!Textures.IsAvailable(leftTop) || !Textures.IsAvailable(centerTop) ||
                !Textures.IsAvailable(rightTop) || !Textures.IsAvailable(leftCenter) ||
                !Textures.IsAvailable(center) || !Textures.IsAvailable(rightCenter) ||
                !Textures.IsAvailable(leftBottom) || !Textures.IsAvailable(centerBottom) ||
                !Textures.IsAvailable(rightBottom))
                return false;

            if (bounds.Width <= 0f || bounds.Height <= 0f)
                return true;

            float scale = bounds.Width / Math.Max(1f, brush.ReferenceWidth);
            float left = Math.Max(0f, brush.LeftWidth * scale);
            float right = Math.Max(0f, brush.RightWidth * scale);
            float top = Math.Max(0f, brush.TopHeight * scale);
            float bottom = Math.Max(0f, brush.BottomHeight * scale);

            float horizontalCaps = left + right;
            if (horizontalCaps > bounds.Width && horizontalCaps > 0f)
            {
                float capScale = bounds.Width / horizontalCaps;
                left *= capScale;
                right *= capScale;
            }
            float verticalCaps = top + bottom;
            if (verticalCaps > bounds.Height && verticalCaps > 0f)
            {
                float capScale = bounds.Height / verticalCaps;
                top *= capScale;
                bottom *= capScale;
            }

            float middleWidth = Math.Max(0f, bounds.Width - left - right);
            float middleHeight = Math.Max(0f, bounds.Height - top - bottom);
            float x0 = bounds.X;
            float x1 = x0 + left;
            float x2 = bounds.Right - right;
            float y0 = bounds.Y;
            float y1 = y0 + top;
            float y2 = bounds.Bottom - bottom;

            if (left > 0f && top > 0f)
                DrawTexture(leftTop, new RectangleF(x0, y0, left, top), brush.Tint);
            if (middleWidth > 0f && top > 0f)
                DrawTexture(centerTop, new RectangleF(x1, y0, middleWidth, top), brush.Tint);
            if (right > 0f && top > 0f)
                DrawTexture(rightTop, new RectangleF(x2, y0, right, top), brush.Tint);

            if (left > 0f && middleHeight > 0f)
                DrawTexture(leftCenter, new RectangleF(x0, y1, left, middleHeight), brush.Tint);
            if (middleWidth > 0f && middleHeight > 0f)
                DrawTexture(center, new RectangleF(x1, y1, middleWidth, middleHeight), brush.Tint);
            if (right > 0f && middleHeight > 0f)
                DrawTexture(rightCenter, new RectangleF(x2, y1, right, middleHeight), brush.Tint);

            if (left > 0f && bottom > 0f)
                DrawTexture(leftBottom, new RectangleF(x0, y2, left, bottom), brush.Tint);
            if (middleWidth > 0f && bottom > 0f)
                DrawTexture(centerBottom, new RectangleF(x1, y2, middleWidth, bottom), brush.Tint);
            if (right > 0f && bottom > 0f)
                DrawTexture(rightBottom, new RectangleF(x2, y2, right, bottom), brush.Tint);
            return true;
        }

        public void DrawBorder(RectangleF bounds, Color color, int thickness)
        {
            if (thickness <= 0)
                return;
            FillRectangle(new RectangleF(bounds.X, bounds.Y, bounds.Width, thickness), color);
            FillRectangle(new RectangleF(bounds.X, bounds.Bottom - thickness, bounds.Width, thickness), color);
            FillRectangle(new RectangleF(bounds.X, bounds.Y, thickness, bounds.Height), color);
            FillRectangle(new RectangleF(bounds.Right - thickness, bounds.Y, thickness, bounds.Height), color);
        }

        public void Border(RectangleF bounds, Color color, int thickness)
        {
            DrawBorder(bounds, color, thickness);
        }

        public void DrawBorder(RectangleF bounds, Color color, Vector4 thickness)
        {
            float left = Math.Min(Math.Max(0f, thickness.X), bounds.Width);
            float top = Math.Min(Math.Max(0f, thickness.Y), bounds.Height);
            float right = Math.Min(Math.Max(0f, thickness.Z), bounds.Width);
            float bottom = Math.Min(Math.Max(0f, thickness.W), bounds.Height);
            if (top > 0f)
                FillRectangle(new RectangleF(bounds.X, bounds.Y, bounds.Width, top), color);
            if (bottom > 0f)
                FillRectangle(new RectangleF(bounds.X, bounds.Bottom - bottom, bounds.Width, bottom), color);
            float middle = Math.Max(0f, bounds.Height - top - bottom);
            if (left > 0f && middle > 0f)
                FillRectangle(new RectangleF(bounds.X, bounds.Y + top, left, middle), color);
            if (right > 0f && middle > 0f)
                FillRectangle(new RectangleF(bounds.Right - right, bounds.Y + top, right, middle), color);
        }

        public void Border(RectangleF bounds, Color color, Vector4 thickness)
        {
            DrawBorder(bounds, color, thickness);
        }

        public void DrawTexture(
            TextureSource texture,
            RectangleF bounds,
            Color color,
            float rotation = 0f)
        {
            _renderer.DrawTexture(texture, ToPixels(bounds), ApplyOpacity(color), rotation);
        }

        public void Texture(
            object nativeTexture,
            RectangleF bounds,
            Color color,
            float rotation = 0f)
        {
            if (nativeTexture == null)
                return;
            DrawTexture(TextureSource.FromNative(nativeTexture), bounds, color, rotation);
        }

        public void Texture(
            string texture,
            RectangleF bounds,
            Color color,
            bool useMissingIconFallback = false,
            float rotation = 0f)
        {
            if (string.IsNullOrWhiteSpace(texture))
                return;
            TextureSource source = TextureSource.FromKey(texture);
            if (useMissingIconFallback && !Textures.IsAvailable(source))
                source = Textures.MissingTexture;
            DrawTexture(source, bounds, color, rotation);
        }

        public void DrawText(
            string font,
            string text,
            Vector2 position,
            float scale,
            Color color,
            HorizontalAlignment horizontalAlignment,
            VerticalAlignment verticalAlignment)
        {
            _renderer.DrawText(
                font,
                text,
                ToPixels(position),
                scale * RenderScaling,
                ApplyOpacity(color),
                horizontalAlignment,
                verticalAlignment);
        }

        public void Text(
            string text,
            Vector2 position,
            float scale,
            Color color,
            HorizontalAlignment horizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment verticalAlignment = VerticalAlignment.Top,
            bool dropShadow = false)
        {
            Text(
                "White",
                text,
                position,
                scale,
                color,
                horizontalAlignment,
                verticalAlignment,
                dropShadow);
        }

        public void Text(
            string font,
            string text,
            Vector2 position,
            float scale,
            Color color,
            HorizontalAlignment horizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment verticalAlignment = VerticalAlignment.Top,
            bool dropShadow = false)
        {
            if (string.IsNullOrEmpty(text))
                return;

            string resolvedFont = string.IsNullOrWhiteSpace(font) ? "White" : font;

            if (!dropShadow)
            {
                DrawText(resolvedFont, text, position, scale, color, horizontalAlignment, verticalAlignment);
                return;
            }

            // The backend is theme-neutral: a text-owning control requests this pass
            // through the TextOptions.DropShadow attached property. Dark face colors get a
            // hue-preserving highlight; already-bright faces get a dark offset shadow.
            float shadeOffset = scale * .3f;
            Vector2 offset = new Vector2(shadeOffset);
            Color shade = DeriveTextShade(color);
            if (UsesLightTextShade(color))
            {
                DrawText(resolvedFont, text, position + offset, scale, color, horizontalAlignment, verticalAlignment);
                DrawText(resolvedFont, text, position, scale, shade, horizontalAlignment, verticalAlignment);
            }
            else
            {
                DrawText(resolvedFont, text, position + offset, scale, shade, horizontalAlignment, verticalAlignment);
                DrawText(resolvedFont, text, position, scale, color, horizontalAlignment, verticalAlignment);
            }
        }

        static bool UsesLightTextShade(Color source)
        {
            return RelativeLuminance(source) < 0.42f;
        }

        static Color DeriveTextShade(Color source)
        {
            Vector3 hsv = source.ColorToHSV();
            if (UsesLightTextShade(source))
            {
                hsv.Y = MathHelper.Clamp(hsv.Y * 0.82f, 0f, 1f);
                hsv.Z = Math.Max(hsv.Z, 0.92f);
            }
            else
            {
                hsv.Z = Math.Min(hsv.Z, 0.12f);
            }

            Color shade = hsv.HSVtoColor();
            shade.A = (byte)Math.Min((int)source.A, 220);
            return shade;
        }

        static float RelativeLuminance(Color color)
        {
            return (0.2126f * color.R + 0.7152f * color.G + 0.0722f * color.B) / 255f;
        }

        Color ApplyOpacity(Color color)
        {
            Vector4 value = color.ToVector4();
            value *= Opacity;
            return new Color(value);
        }
    }
}
