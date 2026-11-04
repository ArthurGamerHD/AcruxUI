using System;
using System.Collections.Generic;
using Acrux.Core;
using Acrux.Core.Invalidation;
using Acrux.Core.PropertySystem;
using Acrux.Rendering;
using VRageMath;

namespace Acrux.Controls.Primitives
{
    /// <summary>A renderer-neutral overlay primitive positioned relative to another visual.</summary>
    public sealed class Popup : VisualElement, IOverlayProvider, ILightDismissOverlayProvider
    {
        static readonly IReadOnlyList<VisualElement> NoVisualChildren =
            new VisualElement[0];
        VisualElement _attachedChild;

        public static readonly StyledProperty<VisualElement> ChildProperty =
            StyledProperty.Register<Popup, VisualElement>(
                nameof(Child), null, UiInvalidation.Measure);
        public static readonly StyledProperty<bool> IsOpenProperty =
            StyledProperty.Register<Popup, bool>(nameof(IsOpen), false);
        public static readonly StyledProperty<PlacementMode> PlacementProperty =
            StyledProperty.Register<Popup, PlacementMode>(nameof(Placement), PlacementMode.Bottom);
        public static readonly StyledProperty<PopupAnchor> PlacementAnchorProperty =
            StyledProperty.Register<Popup, PopupAnchor>(nameof(PlacementAnchor), PopupAnchor.None);
        public static readonly StyledProperty<PopupGravity> PlacementGravityProperty =
            StyledProperty.Register<Popup, PopupGravity>(nameof(PlacementGravity), PopupGravity.None);
        public static readonly StyledProperty<PopupPositionerConstraintAdjustment> PlacementConstraintAdjustmentProperty =
            StyledProperty.Register<Popup, PopupPositionerConstraintAdjustment>(
                nameof(PlacementConstraintAdjustment), PopupPositionerConstraintAdjustment.All);
        public static readonly StyledProperty<RectangleF?> PlacementRectProperty =
            StyledProperty.Register<Popup, RectangleF?>(nameof(PlacementRect), null);
        public static readonly StyledProperty<VisualElement> PlacementTargetProperty =
            StyledProperty.Register<Popup, VisualElement>(nameof(PlacementTarget), null);
        public static readonly StyledProperty<float> HorizontalOffsetProperty =
            StyledProperty.Register<Popup, float>(nameof(HorizontalOffset), 0f);
        public static readonly StyledProperty<float> VerticalOffsetProperty =
            StyledProperty.Register<Popup, float>(nameof(VerticalOffset), 0f);
        public static readonly StyledProperty<bool> IsLightDismissEnabledProperty =
            StyledProperty.Register<Popup, bool>(nameof(IsLightDismissEnabled), false);
        public static readonly StyledProperty<bool> OverlayDismissEventPassThroughProperty =
            StyledProperty.Register<Popup, bool>(nameof(OverlayDismissEventPassThrough), false);
        public static readonly StyledProperty<VisualElement> OverlayInputPassThroughElementProperty =
            StyledProperty.Register<Popup, VisualElement>(nameof(OverlayInputPassThroughElement), null);
        public static readonly StyledProperty<bool> TopmostProperty =
            StyledProperty.Register<Popup, bool>(nameof(Topmost), true);
        public static readonly StyledProperty<bool> TakesFocusFromNativeControlProperty =
            StyledProperty.Register<Popup, bool>(nameof(TakesFocusFromNativeControl), true);
        public static readonly StyledProperty<bool?> ShouldUseOverlayLayerProperty =
            StyledProperty.Register<Popup, bool?>(nameof(ShouldUseOverlayLayer), null);
        public static readonly StyledProperty<bool> WindowManagerAddShadowHintProperty =
            StyledProperty.Register<Popup, bool>(nameof(WindowManagerAddShadowHint), false);
        public static readonly StyledProperty<bool> InheritsTransformProperty =
            StyledProperty.Register<Popup, bool>(nameof(InheritsTransform), false);

        public VisualElement Child
        {
            get { return GetValue(ChildProperty); }
            set { SetValue(ChildProperty, value); }
        }

        public bool IsOpen
        {
            get { return GetValue(IsOpenProperty); }
            set { SetValue(IsOpenProperty, value); }
        }

        public PlacementMode Placement
        {
            get { return GetValue(PlacementProperty); }
            set { SetValue(PlacementProperty, value); }
        }

        public PopupAnchor PlacementAnchor
        {
            get { return GetValue(PlacementAnchorProperty); }
            set { SetValue(PlacementAnchorProperty, value); }
        }

        public PopupGravity PlacementGravity
        {
            get { return GetValue(PlacementGravityProperty); }
            set { SetValue(PlacementGravityProperty, value); }
        }

        public PopupPositionerConstraintAdjustment PlacementConstraintAdjustment
        {
            get { return GetValue(PlacementConstraintAdjustmentProperty); }
            set { SetValue(PlacementConstraintAdjustmentProperty, value); }
        }

        public RectangleF? PlacementRect
        {
            get { return GetValue(PlacementRectProperty); }
            set { SetValue(PlacementRectProperty, value); }
        }

        public VisualElement PlacementTarget
        {
            get { return GetValue(PlacementTargetProperty); }
            set { SetValue(PlacementTargetProperty, value); }
        }

        public float HorizontalOffset
        {
            get { return GetValue(HorizontalOffsetProperty); }
            set { SetValue(HorizontalOffsetProperty, value); }
        }

        public float VerticalOffset
        {
            get { return GetValue(VerticalOffsetProperty); }
            set { SetValue(VerticalOffsetProperty, value); }
        }

        public bool IsLightDismissEnabled
        {
            get { return GetValue(IsLightDismissEnabledProperty); }
            set { SetValue(IsLightDismissEnabledProperty, value); }
        }

        public bool OverlayDismissEventPassThrough
        {
            get { return GetValue(OverlayDismissEventPassThroughProperty); }
            set { SetValue(OverlayDismissEventPassThroughProperty, value); }
        }

        public VisualElement OverlayInputPassThroughElement
        {
            get { return GetValue(OverlayInputPassThroughElementProperty); }
            set { SetValue(OverlayInputPassThroughElementProperty, value); }
        }

        public bool Topmost
        {
            get { return GetValue(TopmostProperty); }
            set { SetValue(TopmostProperty, value); }
        }

        public bool TakesFocusFromNativeControl
        {
            get { return GetValue(TakesFocusFromNativeControlProperty); }
            set { SetValue(TakesFocusFromNativeControlProperty, value); }
        }

        public bool? ShouldUseOverlayLayer
        {
            get { return GetValue(ShouldUseOverlayLayerProperty); }
            set { SetValue(ShouldUseOverlayLayerProperty, value); }
        }

        public bool WindowManagerAddShadowHint
        {
            get { return GetValue(WindowManagerAddShadowHintProperty); }
            set { SetValue(WindowManagerAddShadowHintProperty, value); }
        }

        public bool InheritsTransform
        {
            get { return GetValue(InheritsTransformProperty); }
            set { SetValue(InheritsTransformProperty, value); }
        }

        public bool IsUsingOverlayLayer
        {
            get { return true; }
        }

        public Action Opened { get; set; }
        public Action Closed { get; set; }

        public override IReadOnlyList<VisualElement> VisualChildren
        {
            get { return NoVisualChildren; }
        }

        public void Open()
        {
            if (IsOpen || Child == null)
                return;
            IsOpen = true;
            if (Opened != null) Opened();
        }

        public void Close()
        {
            if (!IsOpen)
                return;
            IsOpen = false;
            if (Closed != null) Closed();
        }

        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            return Vector2.Zero;
        }

        protected override void OnPropertyChanged(IStyledProperty property)
        {
            base.OnPropertyChanged(property);
            if (property == IsOpenProperty)
                VisualTree.OverlayOpenChanged(IsOpen);
            if (property != ChildProperty)
                return;
            VisualElement child = Child;
            if (_attachedChild != null && !ReferenceEquals(_attachedChild, child))
                RemoveChild(_attachedChild);
            _attachedChild = child;
            if (child != null && !ReferenceEquals(child.Parent, this))
                AddChild(child);
        }

        bool IOverlayProvider.IsOverlayOpen
        {
            get { return IsOpen && Child != null; }
        }

        VisualElement IOverlayProvider.OverlayElement
        {
            get { return Child; }
        }

        void IOverlayProvider.UpdateOverlayLayout() { UpdateOverlayLayout(); }
        void IOverlayProvider.CloseOverlay() { Close(); }
        bool ILightDismissOverlayProvider.IsLightDismissEnabled
        {
            get { return IsLightDismissEnabled; }
        }

        bool ILightDismissOverlayProvider.OverlayDismissEventPassThrough
        {
            get { return OverlayDismissEventPassThrough; }
        }

        void UpdateOverlayLayout()
        {
            VisualElement child = Child;
            if (!IsOpen || child == null)
                return;
            RectangleF anchorRect = PlacementRect ??
                (PlacementTarget == null ? Bounds : PlacementTarget.Bounds);
            RectangleF area = ConstraintBounds();
            Vector2 desired = child.Measure(new Vector2(area.Width, area.Height));
            var size = new Vector2(Math.Max(1f, desired.X), Math.Max(1f, desired.Y));
            PopupAnchor anchor;
            PopupGravity gravity;
            GetAnchorAndGravity(out anchor, out gravity);
            child.Arrange(Position(anchorRect, size, anchor, gravity, new Vector2(HorizontalOffset, VerticalOffset), area,
                PlacementConstraintAdjustment));
        }

        RectangleF ConstraintBounds()
        {
            VisualElement root = this;
            for (VisualElement next = root.Parent ?? root.OverlayOwner; next != null; next = next.Parent ?? next.OverlayOwner)
                root = next;
            return root.Bounds;
        }

        void GetAnchorAndGravity(out PopupAnchor anchor, out PopupGravity gravity)
        {
            switch (Placement)
            {
                case PlacementMode.Pointer:
                    anchor = PopupAnchor.Top | PopupAnchor.Left;
                    gravity = PopupGravity.Bottom | PopupGravity.Right;
                    return;
                case PlacementMode.Bottom:
                    anchor = PopupAnchor.Bottom;
                    gravity = PopupGravity.Bottom;
                    return;
                case PlacementMode.Right:
                    anchor = PopupAnchor.Right;
                    gravity = PopupGravity.Right;
                    return;
                case PlacementMode.Left:
                    anchor = PopupAnchor.Left;
                    gravity = PopupGravity.Left;
                    return;
                case PlacementMode.Top:
                    anchor = PopupAnchor.Top;
                    gravity = PopupGravity.Top;
                    return;
                case PlacementMode.AnchorAndGravity:
                    anchor = PlacementAnchor;
                    gravity = PlacementGravity;
                    return;
                case PlacementMode.BottomEdgeAlignedLeft:
                    anchor = PopupAnchor.Bottom | PopupAnchor.Left;
                    gravity = PopupGravity.Bottom | PopupGravity.Right;
                    return;
                case PlacementMode.BottomEdgeAlignedRight:
                    anchor = PopupAnchor.Bottom | PopupAnchor.Right;
                    gravity = PopupGravity.Bottom | PopupGravity.Left;
                    return;
                case PlacementMode.TopEdgeAlignedLeft:
                    anchor = PopupAnchor.Top | PopupAnchor.Left;
                    gravity = PopupGravity.Top | PopupGravity.Right;
                    return;
                case PlacementMode.TopEdgeAlignedRight:
                    anchor = PopupAnchor.Top | PopupAnchor.Right;
                    gravity = PopupGravity.Top | PopupGravity.Left;
                    return;
                case PlacementMode.LeftEdgeAlignedTop:
                    anchor = PopupAnchor.Top | PopupAnchor.Left;
                    gravity = PopupGravity.Bottom | PopupGravity.Left;
                    return;
                case PlacementMode.LeftEdgeAlignedBottom:
                    anchor = PopupAnchor.Bottom | PopupAnchor.Left;
                    gravity = PopupGravity.Top | PopupGravity.Left;
                    return;
                case PlacementMode.RightEdgeAlignedTop:
                    anchor = PopupAnchor.Top | PopupAnchor.Right;
                    gravity = PopupGravity.Bottom | PopupGravity.Right;
                    return;
                case PlacementMode.RightEdgeAlignedBottom:
                    anchor = PopupAnchor.Bottom | PopupAnchor.Right;
                    gravity = PopupGravity.Top | PopupGravity.Right;
                    return;
                default:
                    anchor = PopupAnchor.None;
                    gravity = PopupGravity.None;
                    return;
            }
        }

        // managed popup positioner: place by anchor and gravity, flip to the opposite side on an axis that does not
        // fit, then slide and finally shrink the popup into the constraint area.
        internal static RectangleF Position(RectangleF anchorRect, Vector2 size, PopupAnchor anchor, PopupGravity gravity,
            Vector2 offset, RectangleF area, PopupPositionerConstraintAdjustment adjustment)
        {
            RectangleF placed = Place(anchorRect, size, anchor, gravity, offset);
            if ((adjustment & PopupPositionerConstraintAdjustment.FlipX) != 0 && !FitsX(placed, area))
            {
                RectangleF flipped = Place(anchorRect, size, FlipX(anchor), FlipX(gravity), new Vector2(-offset.X, offset.Y));
                if (FitsX(flipped, area))
                {
                    placed = flipped;
                    anchor = FlipX(anchor);
                    gravity = FlipX(gravity);
                    offset.X = -offset.X;
                }
            }
            if ((adjustment & PopupPositionerConstraintAdjustment.FlipY) != 0 && !FitsY(placed, area))
            {
                RectangleF flipped = Place(anchorRect, size, FlipY(anchor), FlipY(gravity), new Vector2(offset.X, -offset.Y));
                if (FitsY(flipped, area))
                    placed = flipped;
            }
            if ((adjustment & PopupPositionerConstraintAdjustment.SlideX) != 0)
                placed.X = Slide(placed.X, placed.Width, area.X, area.Right);
            if ((adjustment & PopupPositionerConstraintAdjustment.SlideY) != 0)
                placed.Y = Slide(placed.Y, placed.Height, area.Y, area.Bottom);
            float x = placed.X, width = placed.Width, y = placed.Y, height = placed.Height;
            if ((adjustment & PopupPositionerConstraintAdjustment.ResizeX) != 0)
                Clip(ref x, ref width, area.X, area.Right);
            if ((adjustment & PopupPositionerConstraintAdjustment.ResizeY) != 0)
                Clip(ref y, ref height, area.Y, area.Bottom);
            return new RectangleF(x, y, width, height);
        }

        static RectangleF Place(RectangleF anchorRect, Vector2 size, PopupAnchor anchor, PopupGravity gravity, Vector2 offset)
        {
            float x = (anchor & PopupAnchor.Left) != 0 ? anchorRect.X
                : (anchor & PopupAnchor.Right) != 0 ? anchorRect.Right : anchorRect.Center.X;
            float y = (anchor & PopupAnchor.Top) != 0 ? anchorRect.Y
                : (anchor & PopupAnchor.Bottom) != 0 ? anchorRect.Bottom : anchorRect.Center.Y;
            x = (gravity & PopupGravity.Left) != 0 ? x - size.X
                : (gravity & PopupGravity.Right) != 0 ? x : x - size.X * .5f;
            y = (gravity & PopupGravity.Top) != 0 ? y - size.Y
                : (gravity & PopupGravity.Bottom) != 0 ? y : y - size.Y * .5f;
            return new RectangleF(x + offset.X, y + offset.Y, size.X, size.Y);
        }

        static bool FitsX(RectangleF rect, RectangleF area) => rect.X >= area.X && rect.Right <= area.Right;
        static bool FitsY(RectangleF rect, RectangleF area) => rect.Y >= area.Y && rect.Bottom <= area.Bottom;

        static PopupAnchor FlipX(PopupAnchor anchor)
        {
            if ((anchor & (PopupAnchor.Left | PopupAnchor.Right)) == 0)
                return anchor;
            return anchor ^ (PopupAnchor.Left | PopupAnchor.Right);
        }

        static PopupAnchor FlipY(PopupAnchor anchor)
        {
            if ((anchor & (PopupAnchor.Top | PopupAnchor.Bottom)) == 0)
                return anchor;
            return anchor ^ (PopupAnchor.Top | PopupAnchor.Bottom);
        }

        static PopupGravity FlipX(PopupGravity gravity)
        {
            if ((gravity & (PopupGravity.Left | PopupGravity.Right)) == 0)
                return gravity;
            return gravity ^ (PopupGravity.Left | PopupGravity.Right);
        }

        static PopupGravity FlipY(PopupGravity gravity)
        {
            if ((gravity & (PopupGravity.Top | PopupGravity.Bottom)) == 0)
                return gravity;
            return gravity ^ (PopupGravity.Top | PopupGravity.Bottom);
        }

        static float Slide(float start, float length, float min, float max)
        {
            if (start + length > max)
                start = max - length;
            return Math.Max(min, start);
        }

        static void Clip(ref float start, ref float length, float min, float max)
        {
            float end = Math.Min(max, start + length);
            start = Math.Max(min, start);
            length = Math.Max(1f, end - start);
        }
    }
}
