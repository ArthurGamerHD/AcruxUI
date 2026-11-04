using System;
using Acrux.Core;
using VRageMath;

namespace Acrux.Rendering
{
    /// <summary>
    /// Drawing surface consumed by the retained control tree. Implementations own
    /// all backend API calls, texture lookup, clipping, and text measurement.
    /// </summary>
    public interface IUiRenderer
    {
        ITextMetrics TextMetrics { get; }
        ITextureResolver Textures { get; }
        IDynamicTextureFactory DynamicTextures { get; }
        BorderTextureCache BorderTextures { get; }
        IDisposable PushClip(RectangleF bounds);
        void FillRectangle(RectangleF bounds, Color color);
        void DrawTexture(TextureSource texture, RectangleF bounds, Color color, float rotation);
        void DrawText(
            string font,
            string text,
            Vector2 position,
            float scale,
            Color color,
            HorizontalAlignment horizontalAlignment,
            VerticalAlignment verticalAlignment);
    }
}
