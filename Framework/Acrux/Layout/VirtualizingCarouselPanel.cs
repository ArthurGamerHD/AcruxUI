using System;
using System.Collections.Generic;
using Acrux.Animation;
using Acrux.Controls;
using Acrux.Core;
using Acrux.Core.Invalidation;
using Acrux.Rendering;
using VRageMath;

namespace Acrux.Layout
{
    /// <summary>
    /// Carousel items panel: arranges pages side by side, shows only those inside the
    /// viewport, and slides between selections using the owner's PageTransition.
    /// </summary>
    public sealed class VirtualizingCarouselPanel : Panel
    {
        const float Epsilon = 0.0001f;

        AnimationHandle _transition;
        int _arrangedTarget;
        int _arrangedCount = -1;
        float _position;
        float _transitionFrom;
        float _transitionTo;

        bool IsAnimating => _transition != null && _transition.IsRunning;
        Carousel Owner => Parent as Carousel;

        protected override void DrawSelf(RenderContext context) { }

        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            float pageWidth = PageWidth(availableSize.X);
            IReadOnlyList<VisualElement> children = VisualChildren;
            for (int i = 0; i < children.Count; i++)
                children[i].Measure(new Vector2(pageWidth, availableSize.Y));
            return new Vector2(Math.Max(0f, availableSize.X), Math.Max(0f, availableSize.Y));
        }

        protected override void ArrangeChildren(RectangleF bounds)
        {
            IReadOnlyList<VisualElement> children = VisualChildren;
            int count = children.Count;
            float pageWidth = PageWidth(bounds.Width);
            int visible = Math.Max(1, Math.Min(count, (int)Math.Floor(bounds.Width / Math.Max(1f, pageWidth) + Epsilon)));
            UpdateTransition(count, visible);
            if (count == 0)
                return;

            bool wrap = Owner != null && Owner.WrapSelection;
            float startX = bounds.X + Math.Max(0f, (bounds.Width - pageWidth * visible) * .5f);
            float routeStart = IsAnimating ? Math.Min(_transitionFrom, _transitionTo) : _position;
            float routeEnd = (IsAnimating ? Math.Max(_transitionFrom, _transitionTo) : _position) + visible - 1f;
            float center = _position + (visible - 1f) * .5f;
            for (int i = 0; i < count; i++)
            {
                float slot;
                bool onScreen = wrap
                    ? TryGetNearestEquivalent(i, center, routeStart, routeEnd, count, out slot)
                    : (slot = i) >= routeStart - Epsilon && i <= routeEnd + Epsilon;
                children[i].Visible = onScreen;
                if (onScreen)
                    children[i].ArrangeInSlot(new RectangleF(
                        startX + (slot - _position) * pageWidth, bounds.Y, pageWidth, bounds.Height));
            }
        }

        float PageWidth(float viewportWidth)
        {
            float fraction = Owner == null ? 1f : Owner.ViewportFraction;
            return Math.Max(0f, viewportWidth * MathHelper.Clamp(fraction, 0.01f, 1f));
        }

        void UpdateTransition(int count, int visible)
        {
            int target = Owner == null ? 0 : Owner.Position;
            if (count == _arrangedCount && target == _arrangedTarget)
                return;
            int frames = Owner == null || Owner.PageTransition == null ? 0 : Owner.PageTransition.Frames;
            bool canAnimate = count == _arrangedCount && count > 1 && visible < count &&
                              AnimationController != null && frames > 0;
            _arrangedCount = count;
            _arrangedTarget = target;
            if (!canAnimate)
            {
                if (_transition != null)
                    this.CancelAnimations();
                _transition = null;
                _transitionFrom = _transitionTo = _position = target;
                return;
            }

            _transitionFrom = _position;
            _transitionTo = target;
            _transition = this.RunAnimation(
                "PageSwitch",
                AnimationConflict.Replace,
                new Keyframe(() => _position, value =>
                {
                    _position = value;
                    Invalidate(UiInvalidation.Arrange);
                }, target, frames, EasingMode.EaseInOutCubic),
                new ActionKeyframe(() =>
                {
                    _transitionFrom = _transitionTo;
                    Invalidate(UiInvalidation.Arrange);
                }, false));
        }

        static bool TryGetNearestEquivalent(int page, float reference, float rangeStart, float rangeEnd, int count,
            out float position)
        {
            int minimumCycle = (int)Math.Ceiling((rangeStart - page - .001f) / count);
            int maximumCycle = (int)Math.Floor((rangeEnd - page + .001f) / count);
            if (minimumCycle > maximumCycle)
            {
                position = page;
                return false;
            }

            float cycle = (reference - page) / count;
            int lower = (int)Math.Floor(cycle);
            int nearest = Math.Abs(cycle - lower) < Math.Abs(lower + 1 - cycle) ? lower : lower + 1;
            nearest = Math.Max(minimumCycle, Math.Min(maximumCycle, nearest));
            position = page + nearest * count;
            return true;
        }
    }
}
