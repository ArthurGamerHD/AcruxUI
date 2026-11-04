using System;
using Acrux.Core.Invalidation;
using Acrux.Core.PropertySystem;
using Acrux.Media;
using Acrux.Rendering;
using Acrux.Styling;
using VRageMath;

namespace Acrux.Controls.Shapes
{
    /// <summary>Path subset rendering an open stroked geometry.</summary>
    public sealed class Path : Shape
    {
        static Path()
        {
            StylePropertyRegistry.Global.RegisterType<Path>(nameof(Path));
        }

        static int _nextTextureId;
        readonly int _textureId = System.Threading.Interlocked.Increment(ref _nextTextureId);
        IDynamicTexture _texture;
        IDynamicTextureFactory _factory;
        Geometry _paintedData;
        Vector2I _paintedSize;
        float _paintedThickness;

        public static readonly StyledProperty<Geometry> DataProperty =
            StyledProperty.Register<Path, Geometry>(nameof(Data), null, UiInvalidation.Measure | UiInvalidation.Render);

        public Geometry Data
        {
            get { return GetValue(DataProperty); }
            set { SetValue(DataProperty, value); }
        }

        protected override void DrawSelf(RenderContext context)
        {
            if (Data == null || Stroke == null || StrokeThickness <= 0f ||
                Bounds.Width <= 0f || Bounds.Height <= 0f || context.DynamicTextures == null)
                return;
            Vector2I requested = new Vector2I(
                Math.Max(1, (int)Math.Ceiling(Bounds.Width * context.RenderScaling)),
                Math.Max(1, (int)Math.Ceiling(Bounds.Height * context.RenderScaling)));
            Vector2I size = context.DynamicTextures.FitToSupportedSize(requested);
            if (size.X <= 0 || size.Y <= 0) return;
            if (_texture == null || !ReferenceEquals(_factory, context.DynamicTextures) || _texture.Size != size)
            {
                DisposeTexture();
                _factory = context.DynamicTextures;
                _texture = _factory.Create("AcruxPath_" + _textureId, size);
            }
            if (_texture == null || !_texture.IsAvailable) return;
            float thickness = StrokeThickness * context.RenderScaling;
            if (!ReferenceEquals(_paintedData, Data) || !_paintedSize.Equals(size) ||
                Math.Abs(_paintedThickness - thickness) > .01f)
            {
                Vector2[] points = Data.Points;
                _texture.Update(canvas =>
                {
                    canvas.Clear(Color.Transparent);
                    float scaleX = size.X / (float)requested.X;
                    float scaleY = size.Y / (float)requested.Y;
                    for (int i = 1; i < points.Length; i++)
                        canvas.DrawLine(
                            (int)Math.Round(points[i - 1].X * context.RenderScaling * scaleX),
                            (int)Math.Round(points[i - 1].Y * context.RenderScaling * scaleY),
                            (int)Math.Round(points[i].X * context.RenderScaling * scaleX),
                            (int)Math.Round(points[i].Y * context.RenderScaling * scaleY),
                            Color.White, Math.Max(1, (int)Math.Round(thickness * Math.Min(scaleX, scaleY))));
                });
                _paintedData = Data;
                _paintedSize = size;
                _paintedThickness = thickness;
            }
            context.DrawTexture(_texture.Source, Bounds, Stroke.FallbackColor);
        }

        void DisposeTexture()
        {
            if (_texture != null) _texture.Dispose();
            _texture = null;
            _factory = null;
            _paintedData = null;
        }

        public override void Dispose()
        {
            DisposeTexture();
            base.Dispose();
        }
    }
}
