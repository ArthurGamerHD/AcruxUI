using System;
using Acrux.Core;
using Acrux.Core.Invalidation;
using Acrux.Core.PropertySystem;
using Acrux.Rendering;
using Acrux.Styling;
using VRageMath;

namespace Acrux.Controls
{
    /// <summary>Image over a renderer texture key. Missing textures draw MissingIcon.</summary>
    public sealed class Image : VisualElement
    {
        const float DefaultSize = 20f;

        static Image() { }

        public static readonly StyledProperty<string> SourceProperty =
            StyledProperty.Register<Image, string>(nameof(Source), null, UiInvalidation.Render);
        public static readonly StyledProperty<Stretch> StretchProperty =
            StyledProperty.Register<Image, Stretch>(nameof(Stretch), Stretch.Uniform, UiInvalidation.Render);

        public string Source
        {
            get { return GetValue(SourceProperty); }
            set { SetValue(SourceProperty, value); }
        }

        public Stretch Stretch
        {
            get { return GetValue(StretchProperty); }
            set { SetValue(StretchProperty, value); }
        }

        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            return new Vector2(Math.Min(availableSize.X, DefaultSize), Math.Min(availableSize.Y, DefaultSize));
        }

        protected override void DrawSelf(RenderContext context)
        {
            if (string.IsNullOrWhiteSpace(Source) || Bounds.Width <= 0f || Bounds.Height <= 0f)
                return;
            Transform transform = RenderTransform;
            float rotation = transform == null ? 0f : transform.RotationRadians;
            Vector2 sourceSize;
            RectangleF target = context.Textures.TryGetSize(TextureSource.FromKey(Source), out sourceSize)
                ? ImageBrush.Arrange(Bounds, Stretch, sourceSize)
                : ImageBrush.Arrange(Bounds, Stretch);
            if (Stretch != Stretch.UniformToFill)
            {
                context.Texture(Source, target, Color.White, true, rotation);
                return;
            }
            using (context.PushClip(Bounds))
                context.Texture(Source, target, Color.White, true, rotation);
        }
    }
}
