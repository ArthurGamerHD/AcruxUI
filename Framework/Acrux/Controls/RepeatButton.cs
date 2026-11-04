using System;
using Acrux.Core.Invalidation;
using Acrux.Core.PropertySystem;
using Acrux.Rendering;
using VRageMath;

namespace Acrux.Controls
{
    /// <summary>A button that repeats its action while held.</summary>
    public sealed class RepeatButton : Button
    {
        // Register this type's styled properties before its first style pass.
        static RepeatButton() { }

        DateTime _nextRepeatUtc;

        public static readonly StyledProperty<int> IntervalProperty =
            StyledProperty.Register<RepeatButton, int>(nameof(Interval), 100);
        public static readonly StyledProperty<int> DelayProperty =
            StyledProperty.Register<RepeatButton, int>(nameof(Delay), 300);

        public int Interval { get { return GetValue(IntervalProperty); } set { SetValue(IntervalProperty, Math.Max(1, value)); } }
        public int Delay { get { return GetValue(DelayProperty); } set { SetValue(DelayProperty, Math.Max(0, value)); } }

        internal override void PointerPressed(Vector2 point)
        {
            base.PointerPressed(point);
            _nextRepeatUtc = DateTime.UtcNow.AddMilliseconds(Math.Max(0, Delay));
        }

        internal override void PointerReleased(Vector2 point, bool clicked)
        {
            base.PointerReleased(point, clicked);
            _nextRepeatUtc = default(DateTime);
        }

        protected override void DrawSelf(RenderContext context)
        {
            base.DrawSelf(context);
            if (!IsPressed || _nextRepeatUtc == default(DateTime))
                return;
            DateTime now = DateTime.UtcNow;
            if (now < _nextRepeatUtc)
                return;
            InvokeClick();
            _nextRepeatUtc = now.AddMilliseconds(Math.Max(1, Interval));
            Invalidate(UiInvalidation.Render);
        }
    }
}
