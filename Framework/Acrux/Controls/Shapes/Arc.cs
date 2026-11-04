using System;
using Acrux.Core.Invalidation;
using Acrux.Core.PropertySystem;
using Acrux.Rendering;
using VRageMath;

namespace Acrux.Controls.Shapes
{
    /// <summary>
    /// Port of Arc: a circular arc inscribed in the bounds and stroked with
    /// <see cref="Shape.Stroke"/>. Angles are degrees, 0 at 3 o'clock, increasing clockwise.
    /// The stroke is rasterized into a generated texture mask and drawn tinted.
    /// </summary>
    public sealed class Arc : Shape
    {
        static Arc() { }

        static int _nextTextureId;

        public static readonly StyledProperty<float> StartAngleProperty =
            StyledProperty.Register<Arc, float>(nameof(StartAngle), 0f, UiInvalidation.Render);
        public static readonly StyledProperty<float> SweepAngleProperty =
            StyledProperty.Register<Arc, float>(nameof(SweepAngle), 0f, UiInvalidation.Render);

        readonly int _textureId = System.Threading.Interlocked.Increment(ref _nextTextureId);
        IDynamicTexture _texture;
        IDynamicTextureFactory _textureFactory;
        MaskKey _paintedKey;
        bool _hasPaintedKey;

        public float StartAngle
        {
            get { return GetValue(StartAngleProperty); }
            set { SetValue(StartAngleProperty, value); }
        }

        public float SweepAngle
        {
            get { return GetValue(SweepAngleProperty); }
            set { SetValue(SweepAngleProperty, value); }
        }

        protected override void DrawSelf(RenderContext context)
        {
            float sweep = MathHelper.Clamp(SweepAngle, -360f, 360f);
            if (Stroke == null || StrokeThickness <= 0f || Math.Abs(sweep) < 0.01f ||
                Bounds.Width <= 0f || Bounds.Height <= 0f || context.DynamicTextures == null)
                return;

            float side = Math.Min(Bounds.Width, Bounds.Height);
            float scaling = context.RenderScaling;
            int pixels = Math.Max(1, (int)Math.Ceiling(side * scaling));
            var key = new MaskKey(pixels, StrokeThickness * scaling, StartAngle, sweep, StrokeLineCap);
            if (!EnsureTexture(context, key))
                return;

            var rect = new RectangleF(Bounds.Center - new Vector2(side * .5f), new Vector2(side));
            context.DrawTexture(_texture.Source, rect, Stroke.FallbackColor);
        }

        bool EnsureTexture(RenderContext context, MaskKey key)
        {
            Vector2I size = context.DynamicTextures.FitToSupportedSize(new Vector2I(key.Pixels, key.Pixels));
            if (size.X <= 0 || size.Y <= 0)
                return false;

            if (_texture == null || !ReferenceEquals(_textureFactory, context.DynamicTextures) || _texture.Size != size)
            {
                DisposeTexture();
                _texture = context.DynamicTextures.Create("AcruxArc_" + _textureId, size);
                _textureFactory = context.DynamicTextures;
                _hasPaintedKey = false;
            }

            if (_texture == null || !_texture.IsAvailable)
                return false;
            if (_hasPaintedKey && _paintedKey.Equals(key))
                return true;

            float scale = size.X / (float)key.Pixels;
            _texture.Update(canvas => PaintMask(canvas, key.Thickness * scale, key.StartAngle, key.SweepAngle,
                key.Cap == PenLineCap.Round));
            _paintedKey = key;
            _hasPaintedKey = true;
            return true;
        }

        static void PaintMask(PixelCanvas canvas, float thickness, float startDegrees, float sweepDegrees, bool roundCaps)
        {
            canvas.Clear(Color.Transparent);
            float size = Math.Min(canvas.Width, canvas.Height);
            float half = thickness * .5f;
            float radius = Math.Max(0f, size * .5f - half);
            float centerX = canvas.Width * .5f;
            float centerY = canvas.Height * .5f;
            bool full = Math.Abs(sweepDegrees) >= 360f;
            float start = MathHelper.ToRadians(startDegrees);
            float sweep = MathHelper.ToRadians(sweepDegrees);
            float lower = Math.Min(start, start + sweep);
            float span = Math.Abs(sweep);
            var startCap = new Vector2(centerX + (float)Math.Cos(start) * radius, centerY + (float)Math.Sin(start) * radius);
            var endCap = new Vector2(centerX + (float)Math.Cos(start + sweep) * radius,
                centerY + (float)Math.Sin(start + sweep) * radius);

            for (int y = 0; y < canvas.Height; y++)
            for (int x = 0; x < canvas.Width; x++)
            {
                float px = x + .5f - centerX;
                float py = y + .5f - centerY;
                float distance = (float)Math.Sqrt(px * px + py * py);
                float coverage = MathHelper.Clamp(half + .5f - Math.Abs(distance - radius), 0f, 1f);
                if (coverage > 0f && !full)
                {
                    float angle = (float)Math.Atan2(py, px);
                    float offset = PositiveModulo(angle - lower, MathHelper.TwoPi);
                    float outside = Math.Min(offset - span, MathHelper.TwoPi - offset);
                    if (offset > span)
                        coverage *= MathHelper.Clamp(.5f - outside * Math.Max(1f, distance), 0f, 1f);
                }

                if (roundCaps && !full)
                {
                    coverage = Math.Max(coverage, CapCoverage(x + .5f, y + .5f, startCap, half));
                    coverage = Math.Max(coverage, CapCoverage(x + .5f, y + .5f, endCap, half));
                }

                if (coverage > 0f)
                    canvas.BlendPixel(x, y, Color.White, coverage);
            }
        }

        static float CapCoverage(float x, float y, Vector2 center, float radius)
        {
            float dx = x - center.X;
            float dy = y - center.Y;
            return MathHelper.Clamp(radius + .5f - (float)Math.Sqrt(dx * dx + dy * dy), 0f, 1f);
        }

        static float PositiveModulo(float value, float modulus)
        {
            float result = value % modulus;
            return result < 0f ? result + modulus : result;
        }

        void DisposeTexture()
        {
            if (_texture != null)
                _texture.Dispose();
            _texture = null;
            _textureFactory = null;
            _hasPaintedKey = false;
        }

        public override void Dispose()
        {
            DisposeTexture();
            base.Dispose();
        }

        struct MaskKey : IEquatable<MaskKey>
        {
            public readonly int Pixels;
            public readonly float Thickness;
            public readonly float StartAngle;
            public readonly float SweepAngle;
            public readonly PenLineCap Cap;

            public MaskKey(int pixels, float thickness, float startAngle, float sweepAngle, PenLineCap cap)
            {
                Pixels = pixels;
                Thickness = (float)Math.Round(thickness, 2);
                StartAngle = (float)Math.Round(startAngle, 1);
                SweepAngle = (float)Math.Round(sweepAngle, 1);
                Cap = cap;
            }

            public bool Equals(MaskKey other)
            {
                return Pixels == other.Pixels && Thickness == other.Thickness && StartAngle == other.StartAngle &&
                       SweepAngle == other.SweepAngle && Cap == other.Cap;
            }
        }
    }
}
