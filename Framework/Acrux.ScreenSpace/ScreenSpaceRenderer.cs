using System;
using Acrux.Core;
using Acrux.Rendering;
using Sandbox.ModAPI;
using VRage.Utils;
using VRageMath;

namespace Acrux.ScreenSpace
{
    public sealed class ScreenSpaceRenderer : IUiRenderer, global::Acrux.Toolkit.IExternalPeerHost, IWindowResourceSource
    {
        // A mod screen drawing its own Window (a settings dialog) gets the game's UI opacities, as platform windows do.
        void IWindowResourceSource.PublishWindowResources(Acrux.Controls.Window window)
        {
            GameUiOpacity.Publish(window);
        }

        object _externalPeer;
        object global::Acrux.Toolkit.IExternalPeerHost.ExternalPeer { get { return _externalPeer; } set { _externalPeer = value; } }

        static readonly MyStringId Square = MyStringId.GetOrCompute("SquareSimple");
        readonly ScreenSpaceTextureResolver _textures = new ScreenSpaceTextureResolver();
        readonly ScreenSpaceTextMetrics _textMetrics = new ScreenSpaceTextMetrics();
        readonly ScreenSpaceDynamicTextureFactory _dynamicTextures;
        readonly BorderTextureCache _borderTextures;
        readonly MyScreenSpaceLayer _layer;

        public ScreenSpaceRenderer(MyScreenSpaceLayer layer = MyScreenSpaceLayer.BelowHud)
        {
            _layer = layer;
            _dynamicTextures = new ScreenSpaceDynamicTextureFactory(
                () => global::Acrux.Toolkit.GeneratedTexturePool.ForVisual(OwnerRoot));
            _borderTextures = new BorderTextureCache(_dynamicTextures);
        }

        // Root of the UI drawn by this renderer; its creator's mod pays for generated textures.
        internal Visual OwnerRoot { get; set; }

        public ITextMetrics TextMetrics => _textMetrics;
        public ITextureResolver Textures => _textures;
        public IDynamicTextureFactory DynamicTextures => _dynamicTextures;
        public BorderTextureCache BorderTextures => _borderTextures;

        public IDisposable PushClip(RectangleF bounds)
        {
            return MyScreenSpace.UsingScissorRectanglePixels(ToRectangle(bounds), _layer);
        }

        public void FillRectangle(RectangleF bounds, Color color)
        {
            MyScreenSpaceSprite.DrawPixels(
                Square,
                ToRectangle(bounds),
                null,
                color,
                0f,
                _layer);
        }

        public void DrawTexture(
            TextureSource texture,
            RectangleF bounds,
            Color color,
            float rotation)
        {
            object resolved;
            if (!_textures.TryResolve(texture, out resolved))
                return;

            MyScreenSpaceSprite.DrawPixels(
                (MyStringId)resolved,
                ToRectangle(bounds),
                null,
                color,
                rotation,
                _layer);
        }

        public void DrawText(string font, string text, Vector2 position, float scale, Color color,
            HorizontalAlignment horizontalAlignment, VerticalAlignment verticalAlignment)
        {
            if (string.IsNullOrEmpty(text))
                return;

            MyScreenSpaceText.DrawPixels(
                string.IsNullOrWhiteSpace(font) ? "White" : font,
                text,
                position,
                scale,
                color,
                ToGuiDrawAlign(horizontalAlignment, verticalAlignment),
                float.PositiveInfinity,
                _layer);
        }

        public static MyGuiDrawAlignEnum ToGuiDrawAlign(
            HorizontalAlignment horizontal,
            VerticalAlignment vertical)
        {
            if (horizontal == HorizontalAlignment.Stretch)
                horizontal = HorizontalAlignment.Left;

            if (vertical == VerticalAlignment.Stretch)
                vertical = VerticalAlignment.Top;

            switch (horizontal)
            {
                case HorizontalAlignment.Left:
                    switch (vertical)
                    {
                        case VerticalAlignment.Top:
                            return MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP;
                        case VerticalAlignment.Center:
                            return MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER;
                        case VerticalAlignment.Bottom:
                            return MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_BOTTOM;
                    }
                    break;

                case HorizontalAlignment.Center:
                    switch (vertical)
                    {
                        case VerticalAlignment.Top:
                            return MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_TOP;
                        case VerticalAlignment.Center:
                            return MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER;
                        case VerticalAlignment.Bottom:
                            return MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_BOTTOM;
                    }
                    break;

                case HorizontalAlignment.Right:
                    switch (vertical)
                    {
                        case VerticalAlignment.Top:
                            return MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_TOP;
                        case VerticalAlignment.Center:
                            return MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_CENTER;
                        case VerticalAlignment.Bottom:
                            return MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_BOTTOM;
                    }
                    break;
            }

            return MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP;
        }

        static Rectangle ToRectangle(RectangleF value)
        {
            int left = (int)Math.Round(value.X);
            int top = (int)Math.Round(value.Y);
            int right = (int)Math.Round(value.Right);
            int bottom = (int)Math.Round(value.Bottom);
            return new Rectangle(
                left,
                top,
                Math.Max(0, right - left),
                Math.Max(0, bottom - top));
        }

    }
}
