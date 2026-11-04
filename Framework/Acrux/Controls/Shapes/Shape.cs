using Acrux.Core;
using Acrux.Core.Invalidation;
using Acrux.Core.PropertySystem;
using Acrux.Styling;
using VRageMath;

namespace Acrux.Controls.Shapes
{
    /// <summary>Media.PenLineCap.</summary>
    public enum PenLineCap
    {
        Flat,
        Round,
        Square
    }

    /// <summary>Base of stroked shapes.</summary>
    public abstract class Shape : VisualElement
    {
        static Shape() { }

        public static readonly StyledProperty<BackgroundBrush> StrokeProperty =
            StyledProperty.Register<Shape, BackgroundBrush>(nameof(Stroke), null, UiInvalidation.Render);
        public static readonly StyledProperty<float> StrokeThicknessProperty =
            StyledProperty.Register<Shape, float>(nameof(StrokeThickness), 0f, UiInvalidation.Render);
        public static readonly StyledProperty<PenLineCap> StrokeLineCapProperty =
            StyledProperty.Register<Shape, PenLineCap>(nameof(StrokeLineCap), PenLineCap.Flat, UiInvalidation.Render);

        public BackgroundBrush Stroke
        {
            get { return GetValue(StrokeProperty); }
            set { SetValue(StrokeProperty, value); }
        }

        public float StrokeThickness
        {
            get { return GetValue(StrokeThicknessProperty); }
            set { SetValue(StrokeThicknessProperty, value); }
        }

        public PenLineCap StrokeLineCap
        {
            get { return GetValue(StrokeLineCapProperty); }
            set { SetValue(StrokeLineCapProperty, value); }
        }

        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            return Vector2.Zero;
        }
    }
}
