using Acrux.Rendering;
using Acrux.Styling;
using VRageMath;

namespace Acrux.Controls.Chrome
{
    /// <summary>A window caption button: its glyph in the foreground color, or a foreground disc with the glyph cut out while hovered or pressed.</summary>
    public sealed class CaptionButton : Button
    {
        public CaptionButton(CaptionGlyph glyph, string styleClass)
        {
            Glyph = glyph;
            StyleClass = "caption-button " + styleClass;
        }

        public CaptionGlyph Glyph { get; set; }

        protected override void DrawSelf(RenderContext context)
        {
            float side = System.Math.Min(Bounds.Width, Bounds.Height);
            if (side <= 0f)
                return;
            bool disc = IsEffectivelyEnabled &&
                        (HasPseudoClass(PseudoClassNames.PointerOver) || HasPseudoClass(PseudoClassNames.Pressed));
            TextureSource texture;
            if (!CaptionGlyphs.TryGet(context, Glyph, disc, out texture))
                return;
            context.DrawTexture(texture, new RectangleF(Bounds.Center - new Vector2(side * .5f), new Vector2(side)), Foreground);
        }
    }
}
