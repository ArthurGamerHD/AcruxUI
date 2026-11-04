using System;
using System.Collections.Generic;
using Acrux.Core;
using Acrux.Rendering;
using VRage.Game.GUI.TextPanel;
using VRageMath;
using IMyTextSurface = Sandbox.ModAPI.Ingame.IMyTextSurface;

namespace Acrux.Lcd
{
    /// <summary>
    /// Acrux renderer backend that records a retained visual tree as LCD MySprite commands.
    /// One renderer is tied to one text surface so measurement and rendering stay exact.
    /// </summary>
    public sealed class LcdRenderer : IUiRenderer, global::Acrux.Toolkit.IExternalPeerHost
    {
        object _externalPeer;
        object global::Acrux.Toolkit.IExternalPeerHost.ExternalPeer { get { return _externalPeer; } set { _externalPeer = value; } }

        readonly LcdTextMetrics _textMetrics;
        readonly LcdTextureResolver _textures;
        readonly LcdDynamicTextureFactory _dynamicTextures;
        readonly BorderTextureCache _borderTextures;
        readonly Stack<RectangleF> _clips = new Stack<RectangleF>();
        List<MySprite> _sprites;

        public LcdRenderer(IMyTextSurface surface, string missingTexture = "MissingIcon")
        {
            if (surface == null)
                throw new ArgumentNullException(nameof(surface));

            _dynamicTextures = new LcdDynamicTextureFactory(
                () => PoolOverride ?? global::Acrux.Toolkit.GeneratedTexturePool.ForVisual(OwnerRoot));
            _textMetrics = new LcdTextMetrics(surface);
            _textures = new LcdTextureResolver(surface, missingTexture);
            _borderTextures = new BorderTextureCache(_dynamicTextures);
        }

        public float TextScaleMultiplier
        {
            get { return _textMetrics.ScaleMultiplier; }
            set { _textMetrics.ScaleMultiplier = value; }
        }

        // Root of the UI drawn by this renderer; its creator's mod pays for generated textures.
        internal global::Acrux.Core.Visual OwnerRoot { get; set; }

        // Mod that pays for this renderer's textures when the toolkit draws UI on a mod's behalf.
        internal global::Acrux.Toolkit.GeneratedTexturePool PoolOverride { get; set; }

        public ITextMetrics TextMetrics => _textMetrics;
        public ITextureResolver Textures => _textures;
        public IDynamicTextureFactory DynamicTextures => _dynamicTextures;
        public BorderTextureCache BorderTextures => _borderTextures;

        public IDisposable BeginFrame(List<MySprite> sprites)
        {
            if (sprites == null)
                throw new ArgumentNullException(nameof(sprites));
            if (_sprites != null)
                throw new InvalidOperationException("An LCD render frame is already active.");

            _sprites = sprites;
            _clips.Clear();
            return new FrameToken(this);
        }

        public void Draw(VisualTree tree, List<MySprite> sprites, float opacity = 1f)
        {
            if (tree == null)
                throw new ArgumentNullException(nameof(tree));

            using (BeginFrame(sprites))
                tree.Draw(opacity);
        }

        public void AppendSprites(IEnumerable<MySprite> sprites, float opacity = 1f)
        {
            EnsureFrame();
            if (sprites == null)
                return;

            float clampedOpacity = MathHelper.Clamp(opacity, 0f, 1f);
            foreach (MySprite source in sprites)
            {
                MySprite sprite = source;
                if (sprite.Type == SpriteType.CLIP_RECT && _clips.Count > 0)
                {
                    RectangleF ownerClip = _clips.Peek();
                    if (!sprite.Position.HasValue || !sprite.Size.HasValue)
                    {
                        _sprites.Add(MySprite.CreateClearClipRect());
                        _sprites.Add(MySprite.CreateClipRect(ToClipRectangle(ownerClip)));
                        continue;
                    }

                    RectangleF requested = new RectangleF(sprite.Position.Value, sprite.Size.Value);
                    RectangleF nested = Intersect(ownerClip, requested);
                    _sprites.Add(MySprite.CreateClipRect(ToClipRectangle(nested)));
                    continue;
                }
                if (sprite.Color.HasValue && clampedOpacity < 1f)
                {
                    Color color = sprite.Color.Value;
                    color.A = (byte)MathHelper.Clamp(
                        (int)Math.Round(color.A * clampedOpacity), 0, 255);
                    sprite.Color = color;
                }
                _sprites.Add(sprite);
            }
        }

        public IDisposable PushClip(RectangleF bounds)
        {
            EnsureFrame();
            RectangleF clip = _clips.Count == 0
                ? bounds
                : Intersect(_clips.Peek(), bounds);
            _clips.Push(clip);
            _sprites.Add(MySprite.CreateClipRect(ToClipRectangle(clip)));
            return new ClipToken(this);
        }

        public void FillRectangle(RectangleF bounds, Color color)
        {
            EnsureFrame();
            if (bounds.Width <= 0f || bounds.Height <= 0f || color.A == 0)
                return;

            _sprites.Add(new MySprite(
                SpriteType.TEXTURE,
                "SquareSimple",
                bounds.Center,
                bounds.Size,
                color));
        }

        public void DrawTexture(
            TextureSource texture,
            RectangleF bounds,
            Color color,
            float rotation)
        {
            EnsureFrame();
            if (bounds.Width <= 0f || bounds.Height <= 0f || color.A == 0)
                return;

            object resolved;
            if (!_textures.TryResolve(texture, out resolved))
                return;

            string sprite = resolved as string;
            if (string.IsNullOrWhiteSpace(sprite))
                return;

            _sprites.Add(new MySprite(
                SpriteType.TEXTURE,
                sprite,
                bounds.Center,
                bounds.Size,
                color,
                alignment: TextAlignment.CENTER,
                rotation: rotation));
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
            EnsureFrame();
            if (string.IsNullOrEmpty(text) || color.A == 0)
                return;

            string resolvedFont = string.IsNullOrWhiteSpace(font) ? "White" : font;
            float resolvedScale = Math.Max(0.001f, scale * TextScaleMultiplier);
            Vector2 size = _textMetrics.Measure(resolvedFont, text, scale);
            Vector2 lcdPosition = ToLcdTextPosition(position, size, verticalAlignment);
            _sprites.Add(new MySprite(
                SpriteType.TEXT,
                text,
                lcdPosition,
                null,
                color,
                resolvedFont,
                ToTextAlignment(horizontalAlignment),
                resolvedScale));
        }

        void PopClip()
        {
            if (_sprites == null || _clips.Count == 0)
                return;

            _clips.Pop();
            _sprites.Add(MySprite.CreateClearClipRect());
            if (_clips.Count > 0)
                _sprites.Add(MySprite.CreateClipRect(ToClipRectangle(_clips.Peek())));
        }

        void EndFrame()
        {
            if (_sprites == null)
                return;

            if (_clips.Count > 0)
            {
                _clips.Clear();
                _sprites.Add(MySprite.CreateClearClipRect());
            }
            _sprites = null;
        }

        void EnsureFrame()
        {
            if (_sprites == null)
                throw new InvalidOperationException(
                    "Call BeginFrame before drawing an Acrux visual tree to an LCD surface.");
        }

        static Vector2 ToLcdTextPosition(
            Vector2 position,
            Vector2 measuredSize,
            VerticalAlignment verticalAlignment)
        {
            // LCD text sprites only encode horizontal alignment. Convert the screen-space
            // vertical anchor to the top edge expected by MySprite text rendering.
            switch (verticalAlignment)
            {
                case VerticalAlignment.Center:
                    position.Y -= measuredSize.Y * 0.5f;
                    break;
                case VerticalAlignment.Bottom:
                    position.Y -= measuredSize.Y;
                    break;
            }
            return position;
        }

        static TextAlignment ToTextAlignment(HorizontalAlignment alignment)
        {
            switch (alignment)
            {
                case HorizontalAlignment.Center:
                    return TextAlignment.CENTER;
                case HorizontalAlignment.Right:
                    return TextAlignment.RIGHT;
                default:
                    return TextAlignment.LEFT;
            }
        }

        static RectangleF Intersect(RectangleF left, RectangleF right)
        {
            float x = Math.Max(left.X, right.X);
            float y = Math.Max(left.Y, right.Y);
            float rightEdge = Math.Min(left.Right, right.Right);
            float bottomEdge = Math.Min(left.Bottom, right.Bottom);
            return new RectangleF(
                x,
                y,
                Math.Max(0f, rightEdge - x),
                Math.Max(0f, bottomEdge - y));
        }

        static Rectangle ToClipRectangle(RectangleF bounds)
        {
            // Logical -> physical round trips at fractional DPI can leave a
            // nominal integer edge a few float ULPs above/below that pixel.
            int x = (int)Math.Floor(SnapClipEdge(bounds.X));
            int y = (int)Math.Floor(SnapClipEdge(bounds.Y));
            int right = (int)Math.Ceiling(SnapClipEdge(bounds.Right));
            int bottom = (int)Math.Ceiling(SnapClipEdge(bounds.Bottom));
            return new Rectangle(
                x,
                y,
                Math.Max(0, right - x),
                Math.Max(0, bottom - y));
        }

        static double SnapClipEdge(float edge)
        {
            double pixel = Math.Round(edge);
            return Math.Abs(edge - pixel) < 0.0001d ? pixel : edge;
        }

        sealed class ClipToken : IDisposable
        {
            LcdRenderer _renderer;

            public ClipToken(LcdRenderer renderer)
            {
                _renderer = renderer;
            }

            public void Dispose()
            {
                if (_renderer == null)
                    return;
                _renderer.PopClip();
                _renderer = null;
            }
        }

        sealed class FrameToken : IDisposable
        {
            LcdRenderer _renderer;

            public FrameToken(LcdRenderer renderer)
            {
                _renderer = renderer;
            }

            public void Dispose()
            {
                if (_renderer == null)
                    return;
                _renderer.EndFrame();
                _renderer = null;
            }
        }
    }
}
