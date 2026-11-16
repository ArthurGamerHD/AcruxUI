using System;
using Acrux.Core;
using Acrux.Core.Invalidation;
using Acrux.Core.PropertySystem;
using Acrux.Layout;
using Acrux.Rendering;
using Acrux.Styling;
using VRageMath;

namespace Acrux.Controls
{
    /// <summary>
    /// A single-child decorator with optional rounded background/border styling and
    /// pointer-state visuals. Standard generated textures are shared and reference
    /// counted; square borders bypass generated textures entirely.
    /// </summary>
    public sealed class Border : Decorator
    {
        static Border() { }

        static int _nextTextureId;

        readonly int _textureId = System.Threading.Interlocked.Increment(ref _nextTextureId);
        BorderTextureCache.Lease _sharedTexture;
        BorderTextureCache.Lease _ringTexture;
        BorderTextureCache _sharedTextureOwner;
        BorderTextureCache _ringTextureOwner;
        // A generated texture draws in the frame it is created and filled: the renderer handles the create and pixel
        // messages before that frame's sprites (only its lower mips and ready flag wait for frame end). So a new size's
        // texture replaces the old one at once; the pre-release API's one-frame hold is gone.
        IDynamicTexture _ownedTexture;
        IDynamicTextureFactory _ownedTextureFactory;
        bool _useGeneratedTexture;
        Action<PixelCanvas> _customTexturePainter;
        string _generatedTextureName;
        bool _textureDirty = true;

        public static readonly StyledProperty<BackgroundBrush> BackgroundProperty =
            StyledProperty.Register<Border, BackgroundBrush>(
                "Background", new SolidColorBrush(Color.Transparent),
                UiInvalidation.Render);
        public static readonly StyledProperty<Color> BorderBrushProperty =
            StyledProperty.Register<Border, Color>(
                nameof(BorderBrush), Color.Transparent, UiInvalidation.Render);
        public static readonly StyledProperty<Vector4> BorderThicknessProperty =
            StyledProperty.Register<Border, Vector4>(nameof(BorderThickness), new Vector4(0f), UiInvalidation.Measure);
        public static readonly StyledProperty<Vector4> CornerRadiusProperty =
            StyledProperty.Register<Border, Vector4>(
                nameof(CornerRadius), Vector4.Zero, UiInvalidation.Render);

        public BackgroundBrush Background
        {
            get { return GetValue(BackgroundProperty); }
            set { SetValue(BackgroundProperty, value); }
        }

        public Color BackgroundColor
        {
            get
            {
                BackgroundBrush brush = GetValue(BackgroundProperty);
                return brush == null ? Color.Transparent : brush.FallbackColor;
            }
            set { SetValue(BackgroundProperty, new SolidColorBrush(value)); }
        }

        public Func<Color> BackgroundColorProvider { get; set; }

        public Color BorderBrush
        {
            get { return GetValue(BorderBrushProperty); }
            set { SetValue(BorderBrushProperty, value); }
        }

        public Func<Color> BorderColorProvider { get; set; }
        public Vector4 BorderThickness
        {
            get { return GetValue(BorderThicknessProperty); }
            set { SetValue(BorderThicknessProperty, Vector4.Max(value, Vector4.Zero)); }
        }

        bool HasBorderThickness =>
            BorderThickness.X > 0f || BorderThickness.Y > 0f || BorderThickness.Z > 0f || BorderThickness.W > 0f;

        public Color ShadowColor { get; set; } = Color.Transparent;
        public Vector2 ShadowOffset { get; set; }

        public bool Interactive { get; set; }
        public Action Clicked { get; set; }
        public Action SecondaryClicked { get; set; }

        /// <summary>Top-left, top-right, bottom-right and bottom-left radii, like CornerRadius.</summary>
        public Vector4 CornerRadius
        {
            get { return GetValue(CornerRadiusProperty); }
            set
            {
                Vector4 next = Vector4.Max(value, Vector4.Zero);
                Vector4 current = CornerRadius;
                if (Math.Abs(current.X - next.X) < 0.001f && Math.Abs(current.Y - next.Y) < 0.001f &&
                    Math.Abs(current.Z - next.Z) < 0.001f && Math.Abs(current.W - next.W) < 0.001f)
                    return;
                SetValue(CornerRadiusProperty, next);
            }
        }

        public bool UseGeneratedTexture
        {
            get { return _useGeneratedTexture; }
            set
            {
                if (_useGeneratedTexture == value)
                    return;
                _useGeneratedTexture = value;
                _textureDirty = true;
            }
        }

        public Action<PixelCanvas> CustomTexturePainter
        {
            get { return _customTexturePainter; }
            set
            {
                if (ReferenceEquals(_customTexturePainter, value))
                    return;
                bool ownershipModeChanged = (_customTexturePainter == null) != (value == null);
                _customTexturePainter = value;
                if (ownershipModeChanged)
                    DisposeTexture();
                _textureDirty = true;
            }
        }

        public string GeneratedTextureName
        {
            get { return _generatedTextureName; }
            set
            {
                if (string.Equals(_generatedTextureName, value, StringComparison.Ordinal))
                    return;
                _generatedTextureName = value;
                DisposeOwnedTexture();
                _textureDirty = true;
            }
        }


        protected override void UpdatePseudoClasses()
        {
            base.UpdatePseudoClasses();
            PseudoClasses.Set(PseudoClassNames.Pressed, Interactive && IsPressed);
        }

        protected override Vector4 GetLayoutPadding()
        {
            return Padding + BorderThickness;
        }

        protected override bool HitTestSelf(Vector2 point)
        {
            return Interactive || ContextMenu != null;
        }

        protected override void DrawSelf(RenderContext context)
        {
            BackgroundBrush background = ResolveBackground();
            Color border = ResolveBorder();

            ImageBrush mask = OpacityMask as ImageBrush;
            if (mask != null)
            {
                ReleaseSharedTexture();
                DisposeOwnedTexture();
                if (background != null && !string.IsNullOrWhiteSpace(mask.Source))
                {
                    Transform transform = RenderTransform;
                    context.Texture(mask.Source, ImageBrush.Arrange(Bounds, mask.Stretch), background.FallbackColor,
                        true, transform == null ? 0f : transform.RotationRadians);
                }
                return;
            }

            if (background != null && !(background is SolidColorBrush))
            {
                ReleaseFillTexture();
                DisposeOwnedTexture();
                UpdateRing(context, HasBorderThickness && border.A > 0, Vector4.Zero);
                DrawBrush(context, background, border);
                return;
            }

            Color backgroundColor = background == null ? Color.Transparent : background.FallbackColor;

            if (CustomTexturePainter != null)
            {
                EnsureCustomTexture(context);
                UpdateRing(context, HasBorderThickness && border.A > 0, Vector4.Zero);
                DrawCustom(context, backgroundColor, border);
                return;
            }

            // A square Border without a stroke is one rectangle, and an opaque one with a stroke is two (the
            // stroke's, then the background's inside it): neither consumes a generated texture. Only a stroke that
            // something shows through needs the shared ring texture.
            Vector4 corners = CornerRadius;
            if (Math.Max(Math.Max(corners.X, corners.Y), Math.Max(corners.Z, corners.W)) <= 0f)
            {
                ReleaseFillTexture();
                DisposeOwnedTexture();
                UpdateRing(context,
                    NeedsRing(backgroundColor, border) && !CanStackStroke(context, backgroundColor, border),
                    Vector4.Zero);
                DrawSquare(context, backgroundColor, border);
                return;
            }

            // Overlapping caps are safe only when the final draw is fully opaque.
            // Translucent fills (including inherited opacity) use one cached mask.
            if (TryDrawOpaquePill(context, backgroundColor, border))
            {
                ReleaseSharedTexture();
                DisposeOwnedTexture();
                return;
            }

            EnsureStandardTexture(context, backgroundColor, border);
            DrawRounded(context, backgroundColor, border);
        }

        bool TryDrawOpaquePill(RenderContext context, Color background, Color border)
        {
            float diameter = Math.Min(Bounds.Width, Bounds.Height);
            Vector4 corners = CornerRadius;
            if (diameter <= 0f ||
                Math.Min(Math.Min(corners.X, corners.Y), Math.Min(corners.Z, corners.W)) < diameter * 0.5f ||
                background.A != 255 || context.Opacity < 1f || ShadowColor.A > 0 ||
                (HasBorderThickness && border.A > 0 && border != background))
                return false;

            var circle = TextureSource.FromKey("Circle");
            if (!context.Textures.IsAvailable(circle))
                return false;

            float radius = diameter * 0.5f;
            bool horizontal = Bounds.Width >= Bounds.Height;
            float middleLength = (horizontal ? Bounds.Width : Bounds.Height) - diameter;
            if (middleLength > 0f)
            {
                context.Fill(horizontal
                    ? new RectangleF(Bounds.X + radius, Bounds.Y, middleLength, diameter)
                    : new RectangleF(Bounds.X, Bounds.Y + radius, diameter, middleLength), background);
                context.DrawTexture(circle, horizontal
                    ? new RectangleF(Bounds.Right - diameter, Bounds.Y, diameter, diameter)
                    : new RectangleF(Bounds.X, Bounds.Bottom - diameter, diameter, diameter), background);
            }
            context.DrawTexture(circle, new RectangleF(Bounds.X, Bounds.Y, diameter, diameter), background);
            return true;
        }

        internal override void PointerReleased(Vector2 point, bool clicked)
        {
            if (clicked && Clicked != null)
                Clicked();
        }

        internal override void SecondaryPointerReleased(Vector2 point, bool clicked)
        {
            if (clicked && SecondaryClicked != null)
                SecondaryClicked();
        }

        internal override bool HandlesSecondaryClick => SecondaryClicked != null;

        BackgroundBrush ResolveBackground()
        {
            return BackgroundColorProvider == null
                ? Background
                : new SolidColorBrush(BackgroundColorProvider());
        }

        Color ResolveBorder()
        {
            return BorderColorProvider?.Invoke() ?? BorderBrush;
        }

        void DrawBrush(
            RenderContext context,
            BackgroundBrush background,
            Color border)
        {
            if (ShadowColor.A > 0)
            {
                context.Fill(
                    new RectangleF(Bounds.Position + ShadowOffset, Bounds.Size),
                    ShadowColor);
            }
            context.Fill(Bounds, background);
            if (HasBorderThickness && border.A > 0)
                DrawStroke(context, border);
        }

        protected override void OnPropertyChanged(IStyledProperty property)
        {
            base.OnPropertyChanged(property);
            if (ReferenceEquals(property, BackgroundProperty) ||
                ReferenceEquals(property, BorderBrushProperty) ||
                ReferenceEquals(property, BorderThicknessProperty) ||
                ReferenceEquals(property, CornerRadiusProperty))
            {
                _textureDirty = true;
            }
        }

        void DrawSquare(RenderContext context, Color background, Color border)
        {
            if (ShadowColor.A > 0)
                context.Fill(new RectangleF(Bounds.Position + ShadowOffset, Bounds.Size), ShadowColor);

            if (!NeedsRing(background, border))
            {
                if (background.A > 0)
                    context.Fill(Bounds, background);
                return;
            }

            if (CanStackStroke(context, background, border))
            {
                context.Fill(Bounds, border);
                RectangleF inner = StrokeInnerBounds();
                if (inner.Width > 0f && inner.Height > 0f)
                    context.Fill(inner, background);
                return;
            }

            if (background.A > 0)
                context.Fill(Bounds, background);
            DrawStroke(context, border);
        }

        // True when the stroke can be a full rectangle with the background drawn over its inside. That only looks the
        // same as a ring when nothing shows through either of them.
        static bool CanStackStroke(RenderContext context, Color background, Color border)
        {
            return background.A == 255 && border.A == 255 && context.Opacity >= 1f;
        }

        RectangleF StrokeInnerBounds()
        {
            Vector4 thickness = BorderThickness;
            float left = Math.Min(Math.Max(0f, thickness.X), Bounds.Width);
            float top = Math.Min(Math.Max(0f, thickness.Y), Bounds.Height);
            float right = Math.Min(Math.Max(0f, thickness.Z), Bounds.Width);
            float bottom = Math.Min(Math.Max(0f, thickness.W), Bounds.Height);
            return new RectangleF(
                Bounds.X + left,
                Bounds.Y + top,
                Bounds.Width - left - right,
                Bounds.Height - top - bottom);
        }

        // The stroke alone, as the shared ring texture: one sprite. Until that texture can be drawn (SE does not draw a
        // generated texture in the frame it is created, and the texture pool may be full) it is the four edge
        // rectangles.
        void DrawStroke(RenderContext context, Color border)
        {
            if (_ringTexture != null && _ringTexture.Texture != null && _ringTexture.Texture.IsAvailable)
            {
                context.DrawTexture(_ringTexture.Texture.Source, Bounds, border);
                return;
            }

            context.Border(Bounds, border, BorderThickness);
            if (_ringTexture != null)
                context.MarkVolatile(this);
        }

        void DrawRounded(RenderContext context, Color background, Color border)
        {
            IDynamicTexture texture = ActiveTexture;
            if (texture == null || !texture.IsAvailable)
            {
                DrawSquare(context, background, border);
                return;
            }

            if (ShadowColor.A > 0)
                context.DrawTexture(texture.Source, new RectangleF(Bounds.Position + ShadowOffset, Bounds.Size), ShadowColor);
            if (background.A > 0)
                context.DrawTexture(texture.Source, Bounds, background);
            if (!NeedsRing(background, border))
                return;
            if (_ringTexture != null && _ringTexture.Texture != null && _ringTexture.Texture.IsAvailable)
                context.DrawTexture(_ringTexture.Texture.Source, Bounds, border);
            else
            {
                context.Border(Bounds, border, BorderThickness);
                context.MarkVolatile(this);
            }
        }

        bool NeedsRing(Color background, Color border)
        {
            return HasBorderThickness && border.A > 0 && border != background;
        }

        void DrawCustom(RenderContext context, Color background, Color border)
        {
            IDynamicTexture texture = ActiveTexture;
            if (texture == null || !texture.IsAvailable)
            {
                DrawSquare(context, background, border);
                return;
            }

            if (ShadowColor.A > 0)
            {
                context.DrawTexture(
                    texture.Source,
                    new RectangleF(Bounds.Position + ShadowOffset, Bounds.Size),
                    ShadowColor);
            }
            if (background.A > 0)
                context.DrawTexture(texture.Source, Bounds, background);
            if (HasBorderThickness && border.A > 0)
                DrawStroke(context, border);
        }

        IDynamicTexture ActiveTexture
        {
            get
            {
                if (_sharedTexture != null)
                    return _sharedTexture.Texture;
                return _ownedTexture;
            }
        }

        void EnsureStandardTexture(RenderContext context, Color background, Color border)
        {
            DisposeOwnedTexture();

            Vector2I supported = GetSupportedSize(context);
            if (supported.X <= 0 || supported.Y <= 0)
                return;

            Vector4 radius = Vector4.Min(
                CornerRadius * GetTextureScale(supported),
                new Vector4(Math.Min(supported.X, supported.Y) * 0.5f));
            var fill = new BorderTextureCache.Key(supported, radius, Vector4.Zero, Color.White, Color.White, false);
            bool sameOwner = ReferenceEquals(_sharedTextureOwner, context.BorderTextures);
            if (_sharedTexture == null || !sameOwner || !_sharedTexture.CacheKey.Equals(fill))
            {
                BorderTextureCache.Lease next = context.BorderTextures.AcquireMask(supported, radius, context);
                ReleaseFillTexture();
                _sharedTexture = next;
            }

            UpdateRing(context, NeedsRing(background, border), CornerRadius);

            _sharedTextureOwner = context.BorderTextures;
            _textureDirty = false;
        }

        // Keeps the shared ring texture of this Border's stroke (its size, thickness and corners) while wanted, and
        // gives it back otherwise. Borders of one geometry share one texture, tinted when drawn.
        void UpdateRing(RenderContext context, bool wanted, Vector4 cornerRadius)
        {
            Vector2I supported = wanted ? GetSupportedSize(context) : Vector2I.Zero;
            if (supported.X <= 0 || supported.Y <= 0)
            {
                ReleaseRingTexture();
                return;
            }

            float scale = GetTextureScale(supported);
            Vector4 thickness = BorderThickness * scale;
            Vector4 radius = Vector4.Min(
                Vector4.Max(cornerRadius, Vector4.Zero) * scale,
                new Vector4(Math.Min(supported.X, supported.Y) * 0.5f));
            var ring = new BorderTextureCache.Key(supported, radius, thickness, Color.White, Color.White, true);
            if (_ringTexture == null || !ReferenceEquals(_ringTextureOwner, context.BorderTextures) ||
                !_ringTexture.CacheKey.Equals(ring))
            {
                BorderTextureCache.Lease next = context.BorderTextures.AcquireRing(supported, radius, thickness, context);
                ReleaseRingTexture();
                _ringTexture = next;
            }
            _ringTextureOwner = context.BorderTextures;
        }

        void EnsureCustomTexture(RenderContext context)
        {
            ReleaseFillTexture();

            Vector2I requested = new Vector2I(
                Math.Max(1, (int)Math.Round(Bounds.Width)),
                Math.Max(1, (int)Math.Round(Bounds.Height)));
            Vector2I supported = context.DynamicTextures.FitToSupportedSize(requested);
            if (supported.X <= 0 || supported.Y <= 0)
                return;

            if (_ownedTexture == null ||
                !ReferenceEquals(_ownedTextureFactory, context.DynamicTextures) ||
                _ownedTexture.Size != supported)
            {
                DisposeOwnedTexture();
                string prefix = string.IsNullOrWhiteSpace(GeneratedTextureName)
                    ? "AcruxBorder"
                    : GeneratedTextureName;
                _ownedTexture = context.DynamicTextures.Create(prefix + "_" + _textureId, requested);
                _ownedTextureFactory = context.DynamicTextures;
                _textureDirty = true;
            }

            if (!_textureDirty || _ownedTexture == null || !_ownedTexture.IsAvailable)
                return;

            _ownedTexture.Update(CustomTexturePainter);
            _textureDirty = false;
        }

        Vector2I GetSupportedSize(RenderContext context)
        {
            Vector2I requested = new Vector2I(
                Math.Max(1, (int)Math.Round(Bounds.Width)),
                Math.Max(1, (int)Math.Round(Bounds.Height)));
            return context.DynamicTextures.FitToSupportedSize(requested);
        }

        // Texture pixels per layout unit: the texture is allocated at a renderer-friendly size and stretched over
        // Bounds, so lengths painted into it (corner radius, stroke) are scaled to match.
        float GetTextureScale(Vector2I supported)
        {
            return Bounds.Width >= Bounds.Height
                ? supported.X / Math.Max(1f, (float)Math.Round(Bounds.Width))
                : supported.Y / Math.Max(1f, (float)Math.Round(Bounds.Height));
        }

        void DisposeTexture()
        {
            ReleaseSharedTexture();
            DisposeOwnedTexture();
        }

        void ReleaseSharedTexture()
        {
            ReleaseFillTexture();
            ReleaseRingTexture();
            _sharedTextureOwner = null;
        }

        void ReleaseFillTexture()
        {
            if (_sharedTexture != null)
                _sharedTexture.Dispose();
            _sharedTexture = null;
        }

        void ReleaseRingTexture()
        {
            if (_ringTexture != null)
                _ringTexture.Dispose();
            _ringTexture = null;
            _ringTextureOwner = null;
        }

        void DisposeOwnedTexture()
        {
            if (_ownedTexture != null)
                _ownedTexture.Dispose();
            _ownedTexture = null;
            _ownedTextureFactory = null;
        }

        public override void Dispose()
        {
            DisposeTexture();
            base.Dispose();
        }
    }
}
