using System;
using System.Collections.Generic;
using Acrux.Core;
using Acrux.Rendering;
using VRage.Game.GUI.TextPanel;
using VRageMath;

namespace Acrux.Lcd
{
    /// <summary>
    /// Hosts an LCD sprite renderer inside the retained Acrux visual tree.
    /// Intended for maps, games, previews, and other sprite-native content.
    /// </summary>
    public sealed class LcdSpriteCanvas : VisualElement
    {
        static readonly IReadOnlyList<MySprite> EmptySprites = new MySprite[0];
        readonly List<MySprite> _scaledSprites = new List<MySprite>();

        public Func<object, RectangleF, IReadOnlyList<MySprite>> SpriteProvider { get; set; }

        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            return availableSize;
        }

        protected override void DrawSelf(RenderContext context)
        {
            context.MarkVolatile();
            LcdRenderer renderer = context.GetRenderer<LcdRenderer>();
            if (renderer == null || SpriteProvider == null)
                return;

            IReadOnlyList<MySprite> sprites =
                SpriteProvider(DataContext, Bounds) ?? EmptySprites;
            if (context.RenderScaling != 1f)
            {
                _scaledSprites.Clear();
                for (int i = 0; i < sprites.Count; i++)
                {
                    MySprite sprite = sprites[i];
                    if (sprite.Position.HasValue)
                        sprite.Position = context.ToPixels(sprite.Position.Value);
                    if (sprite.Size.HasValue)
                        sprite.Size = sprite.Size.Value * context.RenderScaling;
                    if (sprite.Type == SpriteType.TEXT)
                        sprite.RotationOrScale *= context.RenderScaling;
                    _scaledSprites.Add(sprite);
                }
                sprites = _scaledSprites;
            }
            using (context.PushClip(Bounds))
                renderer.AppendSprites(sprites, context.Opacity);
        }
    }
}
