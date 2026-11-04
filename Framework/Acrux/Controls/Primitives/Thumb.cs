using System;
using Acrux.Core;
using Acrux.Core.Invalidation;
using Acrux.Core.PropertySystem;
using Acrux.Rendering;
using Acrux.Styling;
using VRageMath;

namespace Acrux.Controls.Primitives
{
    /// <summary>The draggable visual used by range tracks.</summary>
    public sealed class Thumb : TemplatedControl
    {
        static Thumb() { }

        public static readonly StyledProperty<BackgroundBrush> BackgroundProperty =
            StyledProperty.Register<Thumb, BackgroundBrush>(
                nameof(Background),
                new SolidColorBrush(new Color(56, 64, 70, 248)),
                UiInvalidation.Render);
        public static readonly StyledProperty<BackgroundBrush> PointerOverBackgroundProperty =
            StyledProperty.RegisterInternal<Thumb, BackgroundBrush>(
                nameof(PointerOverBackground), null, UiInvalidation.Render);
        public static readonly StyledProperty<BackgroundBrush> PressedBackgroundProperty =
            StyledProperty.RegisterInternal<Thumb, BackgroundBrush>(
                nameof(PressedBackground), null, UiInvalidation.Render);

        public BackgroundBrush Background
        {
            get { return GetValue(BackgroundProperty); }
            set { SetValue(BackgroundProperty, value); }
        }
        public BackgroundBrush PointerOverBackground
        {
            get { return GetValue(PointerOverBackgroundProperty); }
            set { SetValue(PointerOverBackgroundProperty, value); }
        }
        public BackgroundBrush PressedBackground
        {
            get { return GetValue(PressedBackgroundProperty); }
            set { SetValue(PressedBackgroundProperty, value); }
        }

        internal Action<Vector2> DragStarted { get; set; }
        internal Action<Vector2, Vector2> DragDelta { get; set; }
        internal Action DragCompleted { get; set; }

        protected override bool HitTestSelf(Vector2 point)
        {
            return true;
        }

        protected override void DrawSelf(RenderContext context)
        {
            if (TemplateRoot != null)
                return;

            BackgroundBrush brush = IsPressed
                ? PressedBackground ?? PointerOverBackground ?? Background
                : IsPointerOver
                    ? PointerOverBackground ?? Background
                    : Background;
            context.Fill(Bounds, brush);
        }

        protected override void UpdatePseudoClasses()
        {
            base.UpdatePseudoClasses();
            PseudoClasses.Set(PseudoClassNames.Pressed, IsPressed);
        }

        internal override void PointerPressed(Vector2 point)
        {
            Action<Vector2> handler = DragStarted;
            if (handler != null)
                handler(point);
        }

        internal override void PointerMoved(Vector2 point, Vector2 delta)
        {
            Action<Vector2, Vector2> handler = DragDelta;
            if (handler != null)
                handler(point, delta);
        }

        internal override void PointerReleased(Vector2 point, bool clicked)
        {
            Action handler = DragCompleted;
            if (handler != null)
                handler();
        }
    }
}
