using System;
using Acrux.Controls.Primitives;
using Acrux.Core;
using Acrux.Core.Invalidation;
using Acrux.Core.PropertySystem;
using Acrux.Layout;
using Acrux.Styling;
using VRageMath;

namespace Acrux.Controls
{
    /// <summary>
    /// ToolTip: the popup shown for a control's <c>ToolTip.Tip</c> after the pointer rests on it for
    /// <c>ToolTip.ShowDelay</c> milliseconds. Text tips are wrapped in a TextBlock; a visual tip is shown as is. Tooltips
    /// do not take input.
    /// </summary>
    public class ToolTip : Decorator
    {
        public static readonly StyledProperty<object> TipProperty =
            StyledProperty.RegisterAttached<Control, object>(typeof(ToolTip), "Tip", null);
        public static readonly StyledProperty<PlacementMode> PlacementProperty =
            StyledProperty.RegisterAttached<Control, PlacementMode>(typeof(ToolTip), "Placement", PlacementMode.Pointer);
        public static readonly StyledProperty<int> ShowDelayProperty =
            StyledProperty.RegisterAttached<Control, int>(typeof(ToolTip), "ShowDelay", 400);
        public static readonly StyledProperty<float> HorizontalOffsetProperty =
            StyledProperty.RegisterAttached<Control, float>(typeof(ToolTip), "HorizontalOffset", 0f);
        public static readonly StyledProperty<float> VerticalOffsetProperty =
            StyledProperty.RegisterAttached<Control, float>(typeof(ToolTip), "VerticalOffset", 20f);
        public static readonly StyledProperty<bool> IsOpenProperty =
            StyledProperty.RegisterAttached<Control, bool>(typeof(ToolTip), "IsOpen", false);

        readonly Border _chrome;
        readonly TextBlock _text;
        object _content;

        static ToolTip()
        {
            StylePropertyRegistry.Global.RegisterType<ToolTip>(nameof(ToolTip));
        }

        public ToolTip()
        {
            _text = new TextBlock { StyleClass = "tooltip-text", TextWrapping = TextWrapping.Wrap };
            _chrome = new Border { StyleClass = "tooltip-chrome", Child = _text };
            Child = _chrome;
        }

        public object Content
        {
            get { return _content; }
            set
            {
                _content = value;
                var visual = value as VisualElement;
                if (visual != null)
                {
                    _chrome.Child = visual;
                    return;
                }
                _text.Text = value == null ? string.Empty : value.ToString();
                _chrome.Child = _text;
                Invalidate(UiInvalidation.Measure);
            }
        }

        public static object GetTip(Control element) => element.GetValue(TipProperty);
        public static void SetTip(Control element, object value) => element.SetValue(TipProperty, value);
        public static PlacementMode GetPlacement(Control element) => element.GetValue(PlacementProperty);
        public static void SetPlacement(Control element, PlacementMode value) => element.SetValue(PlacementProperty, value);
        public static int GetShowDelay(Control element) => element.GetValue(ShowDelayProperty);
        public static void SetShowDelay(Control element, int value) => element.SetValue(ShowDelayProperty, value);
        public static float GetHorizontalOffset(Control element) => element.GetValue(HorizontalOffsetProperty);
        public static void SetHorizontalOffset(Control element, float value) => element.SetValue(HorizontalOffsetProperty, value);
        public static float GetVerticalOffset(Control element) => element.GetValue(VerticalOffsetProperty);
        public static void SetVerticalOffset(Control element, float value) => element.SetValue(VerticalOffsetProperty, value);
        public static bool GetIsOpen(Control element) => element.GetValue(IsOpenProperty);
        public static void SetIsOpen(Control element, bool value) => element.SetValue(IsOpenProperty, value);

        protected override bool HitTestSelf(Vector2 point)
        {
            return false;
        }

        internal void Place(Control target, Vector2 pointer, RectangleF area)
        {
            Vector2 size = Measure(new Vector2(area.Width, area.Height));
            size = new Vector2(Math.Min(size.X, area.Width), Math.Min(size.Y, area.Height));
            var offset = new Vector2(GetHorizontalOffset(target), GetVerticalOffset(target));
            RectangleF anchor = target.Bounds;
            Vector2 position;
            switch (GetPlacement(target))
            {
                case PlacementMode.Top:
                    position = new Vector2(anchor.Center.X - size.X * .5f, anchor.Y - size.Y) - new Vector2(-offset.X, offset.Y);
                    break;
                case PlacementMode.Bottom:
                    position = new Vector2(anchor.Center.X - size.X * .5f, anchor.Bottom) + offset;
                    break;
                case PlacementMode.Left:
                    position = new Vector2(anchor.X - size.X - offset.X, anchor.Center.Y - size.Y * .5f + offset.Y);
                    break;
                case PlacementMode.Right:
                    position = new Vector2(anchor.Right + offset.X, anchor.Center.Y - size.Y * .5f + offset.Y);
                    break;
                default:
                    position = pointer + offset;
                    if (position.Y + size.Y > area.Bottom)
                        position.Y = pointer.Y - offset.Y - size.Y;
                    break;
            }
            position.X = MathHelper.Clamp(position.X, area.X, Math.Max(area.X, area.Right - size.X));
            position.Y = MathHelper.Clamp(position.Y, area.Y, Math.Max(area.Y, area.Bottom - size.Y));
            Arrange(new RectangleF(position, size));
        }
    }
}
