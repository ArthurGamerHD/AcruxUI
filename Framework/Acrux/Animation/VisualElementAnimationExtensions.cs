using System;
using Acrux.Core;
using Acrux.Core.Invalidation;
using Acrux.Core.PropertySystem;

namespace Acrux.Animation
{
    /// <summary>Animation operations available directly on retained visuals.</summary>
    public static class VisualElementAnimationExtensions
    {
        public static AnimationHandle RunAnimation(
            this VisualElement element,
            params IAnimationStep[] steps)
        {
            return GetController(element).Run(
                element,
                () => element.Invalidate(UiInvalidation.Render),
                steps);
        }

        public static AnimationHandle RunAnimation(
            this VisualElement element,
            string channel,
            params IAnimationStep[] steps)
        {
            return GetController(element).Run(
                element,
                () => element.Invalidate(UiInvalidation.Render),
                channel,
                steps);
        }

        public static AnimationHandle RunAnimation(
            this VisualElement element,
            string channel,
            AnimationConflict conflict,
            params IAnimationStep[] steps)
        {
            return GetController(element).Run(
                element,
                () => element.Invalidate(UiInvalidation.Render),
                channel,
                conflict,
                steps);
        }

        public static AnimationHandle Animate<T>(
            this VisualElement element,
            StyledProperty<T> property,
            T to,
            int durationFrames,
            AnimationInterpolator<T> interpolator,
            EasingMode easingMode = EasingMode.Linear)
        {
            if (property == null)
                throw new ArgumentNullException(nameof(property));
            if (!property.Matches(element))
                throw new ArgumentException(
                    element.GetType().Name + " cannot use " +
                    property.OwnerType.Name + "." + property.Name + ".",
                    nameof(property));

            string channel = property.OwnerType.FullName + "." + property.Name;
            return element.RunAnimation(
                channel,
                AnimationConflict.Replace,
                new Keyframe<T>(
                    () => element.GetValue(property),
                    value => element.SetValue(property, value),
                    to,
                    durationFrames,
                    easingMode,
                    interpolator));
        }

        public static void CancelAnimations(this VisualElement element)
        {
            GetController(element).CancelOwner(element);
        }

        public static void CancelAnimation(this VisualElement element, string channel)
        {
            GetController(element).Cancel(element, channel);
        }

        static AnimationController GetController(VisualElement element)
        {
            if (element == null)
                throw new ArgumentNullException(nameof(element));

            AnimationController controller = element.AnimationController;
            if (controller == null)
                throw new InvalidOperationException(
                    "The visual is not attached to a VisualTree with an animation controller.");
            return controller;
        }
    }
}
