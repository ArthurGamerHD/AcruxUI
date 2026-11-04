using System;
using Acrux.Controls.Chrome;
using Acrux.Core;
using Acrux.Rendering;
using VRageMath;

namespace Acrux.Controls
{
    public sealed class TextPresenter : VisualElement
    {
        const string Font = "White";

        public string Text { get; set; } = string.Empty;
        public float Scale { get; set; } = 0.72f;
        public Color Color { get; set; } = Color.White;
        public bool ShowCaret { get; set; }
        public int CaretIndex { get; set; }
        public float CaretWidth { get; set; } = 1f;

        // Shows an up-chevron beside the caret while a host-side caps lock is on.
        internal bool ShowCapsLock { get; set; }

        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            Vector2 measured = MeasureText(Text);
            float width = Math.Max(1f, measured.X + (ShowCaret ? Math.Max(1f, CaretWidth) : 0f) +
                                       (ShowCaret && ShowCapsLock ? CaretHeight() * .5f : 0f));
            float height = Math.Max(1f, measured.Y);
            return new Vector2(
                Math.Min(availableSize.X, width),
                Math.Min(availableSize.Y, height));
        }

        protected override void DrawSelf(RenderContext context)
        {
            if (!string.IsNullOrEmpty(Text))
            {
                context.Text(
                    Text,
                    new Vector2(Bounds.X, Bounds.Center.Y),
                    Scale,
                    Color,
                    HorizontalAlignment.Left,
                    VerticalAlignment.Center,
                    TextOptions.GetDropShadow(this));
            }

            if (!ShowCaret)
                return;

            float x = Bounds.X + GetCaretOffset();
            float height = CaretHeight();
            float top = Bounds.Center.Y - height * 0.5f;
            context.Fill(new RectangleF(x, top, Math.Max(1f, CaretWidth), height), Color);
            TextureSource chevron;
            if (ShowCapsLock && CaptionGlyphs.TryGet(context, CaptionGlyph.Maximize, false, out chevron))
            {
                float side = height * .9f;
                context.DrawTexture(chevron, new RectangleF(x - side * .2f, top + height * .25f - side * .5f, side, side), Color);
            }
        }

        float CaretHeight()
        {
            return Math.Max(4f, MeasureText("M").Y * 0.82f);
        }

        public float GetCaretOffset()
        {
            string text = Text ?? string.Empty;
            int index = Math.Max(0, Math.Min(CaretIndex, text.Length));
            if (index == 0)
                return 0f;
            return MeasureText(text.Substring(0, index)).X;
        }

        public int GetCaretIndexAt(float contentX)
        {
            string text = Text ?? string.Empty;
            if (text.Length == 0 || contentX <= 0f)
                return 0;

            float fullWidth = MeasureText(text).X;
            if (contentX >= fullWidth)
                return text.Length;

            int low = 0;
            int high = text.Length;
            while (low < high)
            {
                int mid = (low + high) / 2;
                float width = mid == 0 ? 0f : MeasureText(text.Substring(0, mid)).X;
                if (width < contentX)
                    low = mid + 1;
                else
                    high = mid;
            }

            int rightIndex = Math.Max(0, Math.Min(low, text.Length));
            int leftIndex = Math.Max(0, rightIndex - 1);
            float leftWidth = leftIndex == 0 ? 0f : MeasureText(text.Substring(0, leftIndex)).X;
            float rightWidth = rightIndex == 0 ? 0f : MeasureText(text.Substring(0, rightIndex)).X;
            return Math.Abs(contentX - leftWidth) <= Math.Abs(rightWidth - contentX)
                ? leftIndex
                : rightIndex;
        }

        Vector2 MeasureText(string value)
        {
            return TextMetrics?.Measure(Font, value, Scale) ?? Vector2.Zero;
        }
    }
}
