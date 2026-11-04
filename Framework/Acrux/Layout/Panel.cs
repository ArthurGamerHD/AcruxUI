using System.Collections.Generic;
using Acrux.Core;
using Acrux.Core.Invalidation;
using Acrux.Core.PropertySystem;
using Acrux.Rendering;
using Acrux.Styling;
using VRageMath;

namespace Acrux.Layout
{
    public class Panel : VisualElement
    {
        public static readonly StyledProperty<BackgroundBrush> BackgroundProperty =
            StyledProperty.Register<Panel, BackgroundBrush>(nameof(Background), null, UiInvalidation.Render);

        public BackgroundBrush Background
        {
            get { return GetValue(BackgroundProperty); }
            set { SetValue(BackgroundProperty, value); }
        }

        protected override void ArrangeChildren(RectangleF bounds)
        {
            IReadOnlyList<VisualElement> children = VisualChildren;
            for (int i = 0; i < children.Count; i++)
            {
                VisualElement child = children[i];
                if (child.Visible)
                    child.ArrangeInSlot(bounds);
            }
        }

        protected void DrawBackground(RenderContext context)
        {
            BackgroundBrush background = Background;
            if (background != null)
                context.Fill(Bounds, background);
        }

        protected override void DrawSelf(RenderContext context)
        {
            DrawBackground(context);
        }
    }
}
