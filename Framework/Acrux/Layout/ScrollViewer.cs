using System;
using Acrux.Animation;
using Acrux.Controls.Primitives;
using Acrux.Core;
using Acrux.Core.Input;
using Acrux.Core.Invalidation;
using Acrux.Core.PropertySystem;
using Acrux.Rendering;
using Acrux.Styling;
using VRageMath;

namespace Acrux.Layout
{
    public sealed class ScrollChangedEventArgs
    {
        public ScrollChangedEventArgs(Vector2 oldOffset, Vector2 newOffset, Vector2 extent, Vector2 viewport)
        {
            OldOffset = oldOffset;
            NewOffset = newOffset;
            Extent = extent;
            Viewport = viewport;
        }

        public Vector2 OldOffset { get; }
        public Vector2 NewOffset { get; }
        public Vector2 OffsetDelta => NewOffset - OldOffset;
        public Vector2 Extent { get; }
        public Vector2 Viewport { get; }
    }

    public sealed class ScrollViewer : Decorator
    {
        const float DEFAULT_SCROLLER_WIDTH_PIXELS = 6f;
        const float MINIMUM_THUMB_PIXELS = 8f;
        const float WHEEL_INERTIA_IMPULSE = 0.12f;
        const float INERTIA_DECAY_PER_FRAME = 0.88f;
        const float STOP_VELOCITY_PIXELS_PER_FRAME = 0.05f;

        float _horizontalOffset;
        float _verticalOffset;
        float _extentWidth;
        float _extentHeight;
        RectangleF _panelBounds;
        RectangleF _viewportBounds;
        bool _showHorizontalScrollBar;
        bool _showVerticalScrollBar;
        ScrollContentPresenter _contentPresenter;
        ScrollBar _horizontalScrollBar;
        ScrollBar _verticalScrollBar;
        bool _syncingOffsetProperty;
        Vector2 _scrollVelocity;
        AnimationHandle _scrollInertiaAnimation;

        sealed class ScrollInertiaKeyframe : IAnimationStep
        {
            readonly ScrollViewer _owner;
            Vector2 _startOffset;
            Vector2 _startVelocity;
            Vector2 _maxOffset;
            int _horizontalDurationFrames;
            int _verticalDurationFrames;

            public ScrollInertiaKeyframe(ScrollViewer owner)
            {
                _owner = owner;
            }

            public int DurationFrames { get; private set; }
            public bool RequiresRedraw => true;

            public void Begin()
            {
                _startOffset = _owner.Offset;
                _startVelocity = _owner._scrollVelocity;
                _maxOffset = new Vector2(
                    _owner.MaxHorizontalOffset,
                    _owner.MaxVerticalOffset);
                _horizontalDurationFrames = CalculateDuration(
                    _owner.IsHorizontallyScrollable,
                    _startOffset.X,
                    _startVelocity.X,
                    _maxOffset.X);
                _verticalDurationFrames = CalculateDuration(
                    _owner.IsVerticallyScrollable,
                    _startOffset.Y,
                    _startVelocity.Y,
                    _maxOffset.Y);
                DurationFrames = Math.Max(
                    _horizontalDurationFrames,
                    _verticalDurationFrames);
            }

            public void Apply(float progress)
            {
                int elapsedFrames = DurationFrames <= 0
                    ? 0
                    : progress >= 1f
                        ? DurationFrames
                        : Math.Max(0, Math.Min(
                            DurationFrames,
                            (int)Math.Floor(progress * DurationFrames)));

                float horizontalOffset;
                float horizontalVelocity;
                EvaluateAxis(
                    _owner.IsHorizontallyScrollable,
                    _startOffset.X,
                    _startVelocity.X,
                    _maxOffset.X,
                    _horizontalDurationFrames,
                    elapsedFrames,
                    out horizontalOffset,
                    out horizontalVelocity);

                float verticalOffset;
                float verticalVelocity;
                EvaluateAxis(
                    _owner.IsVerticallyScrollable,
                    _startOffset.Y,
                    _startVelocity.Y,
                    _maxOffset.Y,
                    _verticalDurationFrames,
                    elapsedFrames,
                    out verticalOffset,
                    out verticalVelocity);

                _owner.ApplyInertiaState(
                    new Vector2(horizontalOffset, verticalOffset),
                    new Vector2(horizontalVelocity, verticalVelocity));
            }

            static int CalculateDuration(
                bool scrollable,
                float startOffset,
                float velocity,
                float maxOffset)
            {
                float absoluteVelocity = Math.Abs(velocity);
                if (!scrollable ||
                    maxOffset <= 0f ||
                    absoluteVelocity <= STOP_VELOCITY_PIXELS_PER_FRAME ||
                    startOffset <= 0f && velocity < 0f ||
                    startOffset >= maxOffset && velocity > 0f)
                    return 0;

                int duration = Math.Max(1, (int)Math.Ceiling(
                    Math.Log(STOP_VELOCITY_PIXELS_PER_FRAME / absoluteVelocity) /
                    Math.Log(INERTIA_DECAY_PER_FRAME)));
                float availableDistance = velocity > 0f
                    ? maxOffset - startOffset
                    : startOffset;
                double totalPossibleDistance = absoluteVelocity /
                                               (1d - INERTIA_DECAY_PER_FRAME);
                if (availableDistance < totalPossibleDistance)
                {
                    double ratio = availableDistance *
                                   (1d - INERTIA_DECAY_PER_FRAME) /
                                   absoluteVelocity;
                    if (ratio <= 0d)
                        return 0;
                    if (ratio < 1d)
                    {
                        int boundaryDuration = Math.Max(1, (int)Math.Ceiling(
                            Math.Log(1d - ratio) /
                            Math.Log(INERTIA_DECAY_PER_FRAME)));
                        duration = Math.Min(duration, boundaryDuration);
                    }
                }
                return Math.Min(duration, 600);
            }

            static void EvaluateAxis(
                bool scrollable,
                float startOffset,
                float startVelocity,
                float maxOffset,
                int durationFrames,
                int elapsedFrames,
                out float offset,
                out float velocity)
            {
                if (!scrollable || durationFrames <= 0)
                {
                    offset = scrollable ? Clamp(startOffset, 0f, maxOffset) : 0f;
                    velocity = 0f;
                    return;
                }

                int axisElapsedFrames = Math.Min(elapsedFrames, durationFrames);
                double decayPower = Math.Pow(INERTIA_DECAY_PER_FRAME, axisElapsedFrames);
                float displacement = startVelocity *
                                     (1f - (float)decayPower) /
                                     (1f - INERTIA_DECAY_PER_FRAME);
                offset = Clamp(startOffset + displacement, 0f, maxOffset);
                velocity = axisElapsedFrames >= durationFrames
                    ? 0f
                    : startVelocity * (float)decayPower;
                if (offset <= 0f && velocity < 0f ||
                    offset >= maxOffset && velocity > 0f ||
                    Math.Abs(velocity) <= STOP_VELOCITY_PIXELS_PER_FRAME)
                    velocity = 0f;
            }
        }

        public static readonly StyledProperty<float> ScrollerWidthPixelsProperty =
            StyledProperty.RegisterInternal<ScrollViewer, float>(
                nameof(ScrollerWidthPixels),
                DEFAULT_SCROLLER_WIDTH_PIXELS,
                UiInvalidation.Arrange);
        public static readonly StyledProperty<float> ScrollBarVisualWidthPixelsProperty =
            StyledProperty.RegisterInternal<ScrollViewer, float>(
                nameof(ScrollBarVisualWidthPixels),
                -1f,
                UiInvalidation.Arrange);
        public static readonly StyledProperty<float> ScrollBarSideMarginPixelsProperty =
            StyledProperty.RegisterInternal<ScrollViewer, float>(
                nameof(ScrollBarSideMarginPixels),
                -1f,
                UiInvalidation.Arrange);
        public static readonly StyledProperty<float> ScrollBarEndMarginPixelsProperty =
            StyledProperty.RegisterInternal<ScrollViewer, float>(
                nameof(ScrollBarEndMarginPixels),
                -1f,
                UiInvalidation.Arrange);
        public static readonly StyledProperty<float> ScrollBarMinimumThumbPixelsProperty =
            StyledProperty.RegisterInternal<ScrollViewer, float>(
                nameof(ScrollBarMinimumThumbPixels),
                MINIMUM_THUMB_PIXELS,
                UiInvalidation.Arrange);
        public static readonly StyledProperty<Vector2> OffsetProperty =
            StyledProperty.Register<ScrollViewer, Vector2>(
                nameof(Offset), Vector2.Zero, UiInvalidation.Arrange);
        public static readonly StyledProperty<ScrollBarVisibility> HorizontalScrollBarVisibilityProperty =
            StyledProperty.Register<ScrollViewer, ScrollBarVisibility>(
                nameof(HorizontalScrollBarVisibility), ScrollBarVisibility.Disabled, UiInvalidation.Measure);
        public static readonly StyledProperty<ScrollBarVisibility> VerticalScrollBarVisibilityProperty =
            StyledProperty.Register<ScrollViewer, ScrollBarVisibility>(
                nameof(VerticalScrollBarVisibility), ScrollBarVisibility.Auto, UiInvalidation.Measure);
        public static readonly StyledProperty<bool> BringIntoViewOnFocusChangeProperty =
            StyledProperty.Register<ScrollViewer, bool>(nameof(BringIntoViewOnFocusChange), true);
        public static readonly StyledProperty<bool> AllowAutoHideProperty =
            StyledProperty.Register<ScrollViewer, bool>(nameof(AllowAutoHide), true);
        public static readonly StyledProperty<bool> IsScrollChainingEnabledProperty =
            StyledProperty.Register<ScrollViewer, bool>(nameof(IsScrollChainingEnabled), true);
        public static readonly StyledProperty<bool> IsScrollInertiaEnabledProperty =
            StyledProperty.Register<ScrollViewer, bool>(nameof(IsScrollInertiaEnabled), true);
        public static readonly StyledProperty<bool> IsDeferredScrollingEnabledProperty =
            StyledProperty.Register<ScrollViewer, bool>(nameof(IsDeferredScrollingEnabled), false);
        public static readonly StyledProperty<SnapPointsType> HorizontalSnapPointsTypeProperty =
            StyledProperty.Register<ScrollViewer, SnapPointsType>(
                nameof(HorizontalSnapPointsType), SnapPointsType.None);
        public static readonly StyledProperty<SnapPointsType> VerticalSnapPointsTypeProperty =
            StyledProperty.Register<ScrollViewer, SnapPointsType>(
                nameof(VerticalSnapPointsType), SnapPointsType.None);
        public static readonly StyledProperty<SnapPointsAlignment> HorizontalSnapPointsAlignmentProperty =
            StyledProperty.Register<ScrollViewer, SnapPointsAlignment>(
                nameof(HorizontalSnapPointsAlignment), SnapPointsAlignment.Near);
        public static readonly StyledProperty<SnapPointsAlignment> VerticalSnapPointsAlignmentProperty =
            StyledProperty.Register<ScrollViewer, SnapPointsAlignment>(
                nameof(VerticalSnapPointsAlignment), SnapPointsAlignment.Near);
        public static readonly StyledProperty<Vector2> SmallChangeProperty =
            StyledProperty.Register<ScrollViewer, Vector2>(
                nameof(SmallChange), new Vector2(16f, 16f));

        public static readonly StyledProperty<BackgroundBrush> ScrollBarTrackBackgroundProperty =
            StyledProperty.RegisterInternal<ScrollViewer, BackgroundBrush>(
                nameof(ScrollBarTrackBackground),
                new SolidColorBrush(new Color(82, 98, 107, 127)),
                UiInvalidation.Render);
        public static readonly StyledProperty<BackgroundBrush> ScrollBarThumbBackgroundProperty =
            StyledProperty.RegisterInternal<ScrollViewer, BackgroundBrush>(
                nameof(ScrollBarThumbBackground),
                new SolidColorBrush(new Color(56, 64, 70, 248)),
                UiInvalidation.Render);
        public static readonly StyledProperty<BackgroundBrush> VerticalScrollBarTrackBackgroundProperty =
            StyledProperty.RegisterInternal<ScrollViewer, BackgroundBrush>(
                nameof(VerticalScrollBarTrackBackground),
                null,
                UiInvalidation.Render);
        public static readonly StyledProperty<BackgroundBrush> VerticalScrollBarThumbBackgroundProperty =
            StyledProperty.RegisterInternal<ScrollViewer, BackgroundBrush>(
                nameof(VerticalScrollBarThumbBackground),
                null,
                UiInvalidation.Render);
        public static readonly StyledProperty<BackgroundBrush> HorizontalScrollBarTrackBackgroundProperty =
            StyledProperty.RegisterInternal<ScrollViewer, BackgroundBrush>(
                nameof(HorizontalScrollBarTrackBackground),
                null,
                UiInvalidation.Render);
        public static readonly StyledProperty<BackgroundBrush> HorizontalScrollBarThumbBackgroundProperty =
            StyledProperty.RegisterInternal<ScrollViewer, BackgroundBrush>(
                nameof(HorizontalScrollBarThumbBackground),
                null,
                UiInvalidation.Render);

        public bool HorizontalScrollEnabled
        {
            get { return HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled; }
            set { HorizontalScrollBarVisibility = value ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled; }
        }
        public bool VerticalScrollEnabled
        {
            get { return VerticalScrollBarVisibility != ScrollBarVisibility.Disabled; }
            set { VerticalScrollBarVisibility = value ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled; }
        }
        public bool InputEnabled { get; set; } = true;
        public float WheelStep
        {
            get { return SmallChange.Y; }
            set { SmallChange = new Vector2(SmallChange.X, Math.Max(0f, value)); }
        }
        public Vector2 Offset
        {
            get { return new Vector2(_horizontalOffset, _verticalOffset); }
            set
            {
                SetValue(OffsetProperty, value);
                SetOffsets(value.X, value.Y, true);
            }
        }
        public ScrollBarVisibility HorizontalScrollBarVisibility
        {
            get { return GetValue(HorizontalScrollBarVisibilityProperty); }
            set { SetValue(HorizontalScrollBarVisibilityProperty, value); }
        }
        public ScrollBarVisibility VerticalScrollBarVisibility
        {
            get { return GetValue(VerticalScrollBarVisibilityProperty); }
            set { SetValue(VerticalScrollBarVisibilityProperty, value); }
        }
        public bool BringIntoViewOnFocusChange { get { return GetValue(BringIntoViewOnFocusChangeProperty); } set { SetValue(BringIntoViewOnFocusChangeProperty, value); } }
        public bool AllowAutoHide { get { return GetValue(AllowAutoHideProperty); } set { SetValue(AllowAutoHideProperty, value); } }
        public bool IsScrollChainingEnabled { get { return GetValue(IsScrollChainingEnabledProperty); } set { SetValue(IsScrollChainingEnabledProperty, value); } }
        public bool IsScrollInertiaEnabled { get { return GetValue(IsScrollInertiaEnabledProperty); } set { SetValue(IsScrollInertiaEnabledProperty, value); } }
        public bool IsDeferredScrollingEnabled { get { return GetValue(IsDeferredScrollingEnabledProperty); } set { SetValue(IsDeferredScrollingEnabledProperty, value); } }
        public SnapPointsType HorizontalSnapPointsType { get { return GetValue(HorizontalSnapPointsTypeProperty); } set { SetValue(HorizontalSnapPointsTypeProperty, value); } }
        public SnapPointsType VerticalSnapPointsType { get { return GetValue(VerticalSnapPointsTypeProperty); } set { SetValue(VerticalSnapPointsTypeProperty, value); } }
        public SnapPointsAlignment HorizontalSnapPointsAlignment { get { return GetValue(HorizontalSnapPointsAlignmentProperty); } set { SetValue(HorizontalSnapPointsAlignmentProperty, value); } }
        public SnapPointsAlignment VerticalSnapPointsAlignment { get { return GetValue(VerticalSnapPointsAlignmentProperty); } set { SetValue(VerticalSnapPointsAlignmentProperty, value); } }
        public Vector2 SmallChange { get { return GetValue(SmallChangeProperty); } set { SetValue(SmallChangeProperty, new Vector2(Math.Max(0f, value.X), Math.Max(0f, value.Y))); } }
        public float ScrollerWidthPixels
        {
            get { return GetValue(ScrollerWidthPixelsProperty); }
            set { SetValue(ScrollerWidthPixelsProperty, value); }
        }
        public float ScrollBarVisualWidthPixels
        {
            get { return GetValue(ScrollBarVisualWidthPixelsProperty); }
            set { SetValue(ScrollBarVisualWidthPixelsProperty, value); }
        }
        public float ScrollBarSideMarginPixels
        {
            get { return GetValue(ScrollBarSideMarginPixelsProperty); }
            set { SetValue(ScrollBarSideMarginPixelsProperty, value); }
        }
        public float ScrollBarEndMarginPixels
        {
            get { return GetValue(ScrollBarEndMarginPixelsProperty); }
            set { SetValue(ScrollBarEndMarginPixelsProperty, value); }
        }
        public float ScrollBarMinimumThumbPixels
        {
            get { return GetValue(ScrollBarMinimumThumbPixelsProperty); }
            set { SetValue(ScrollBarMinimumThumbPixelsProperty, value); }
        }
        public BackgroundBrush ScrollBarTrackBackground
        {
            get { return GetValue(ScrollBarTrackBackgroundProperty); }
            set { SetValue(ScrollBarTrackBackgroundProperty, value); }
        }
        public BackgroundBrush ScrollBarThumbBackground
        {
            get { return GetValue(ScrollBarThumbBackgroundProperty); }
            set { SetValue(ScrollBarThumbBackgroundProperty, value); }
        }
        public BackgroundBrush VerticalScrollBarTrackBackground
        {
            get { return GetValue(VerticalScrollBarTrackBackgroundProperty); }
            set { SetValue(VerticalScrollBarTrackBackgroundProperty, value); }
        }
        public BackgroundBrush VerticalScrollBarThumbBackground
        {
            get { return GetValue(VerticalScrollBarThumbBackgroundProperty); }
            set { SetValue(VerticalScrollBarThumbBackgroundProperty, value); }
        }
        public BackgroundBrush HorizontalScrollBarTrackBackground
        {
            get { return GetValue(HorizontalScrollBarTrackBackgroundProperty); }
            set { SetValue(HorizontalScrollBarTrackBackgroundProperty, value); }
        }
        public BackgroundBrush HorizontalScrollBarThumbBackground
        {
            get { return GetValue(HorizontalScrollBarThumbBackgroundProperty); }
            set { SetValue(HorizontalScrollBarThumbBackgroundProperty, value); }
        }

        public float HorizontalOffset
        {
            get { return _horizontalOffset; }
            set { SetOffsets(value, _verticalOffset, true); }
        }

        public float VerticalOffset
        {
            get { return _verticalOffset; }
            set { SetOffsets(_horizontalOffset, value, true); }
        }

        public float ExtentWidth => _extentWidth;
        public float ExtentHeight => _extentHeight;
        public float ViewportWidth => _viewportBounds.Width;
        public float ViewportHeight => _viewportBounds.Height;
        public RectangleF ViewportBounds => _viewportBounds;
        public float MaxHorizontalOffset => Math.Max(0f, _extentWidth - _viewportBounds.Width);
        public float MaxVerticalOffset => Math.Max(0f, _extentHeight - _viewportBounds.Height);
        public bool IsHorizontallyScrollable => CanScrollHorizontally && MaxHorizontalOffset > 0f;
        public bool IsVerticallyScrollable => CanScrollVertically && MaxVerticalOffset > 0f;
        public Vector2 Extent => new Vector2(_extentWidth, _extentHeight);
        public Vector2 Viewport => new Vector2(_viewportBounds.Width, _viewportBounds.Height);
        public Vector2 ScrollBarMaximum => new Vector2(MaxHorizontalOffset, MaxVerticalOffset);
        public Vector2 LargeChange => Viewport;
        public bool IsExpanded => !AllowAutoHide ||
                                  (_horizontalScrollBar != null && _horizontalScrollBar.IsExpanded) ||
                                  (_verticalScrollBar != null && _verticalScrollBar.IsExpanded);
        public override bool AcceptsKeyboardFocus => InputEnabled;
        public Action<ScrollChangedEventArgs> ScrollChanged { get; set; }

        public void SetScrollBarColors(Color trackColor, Color thumbColor)
        {
            ScrollBarTrackBackground = trackColor;
            ScrollBarThumbBackground = thumbColor;
        }

        protected override void UpdatePseudoClasses()
        {
            base.UpdatePseudoClasses();
            PseudoClasses.Set(":expanded", IsExpanded);
        }

        protected override void OnApplyTemplate()
        {
            _contentPresenter = GetTemplateChild<ScrollContentPresenter>("PART_ContentPresenter");
            _horizontalScrollBar = GetTemplateChild<ScrollBar>("PART_HorizontalScrollBar");
            _verticalScrollBar = GetTemplateChild<ScrollBar>("PART_VerticalScrollBar");
            if (_horizontalScrollBar != null)
                _horizontalScrollBar.Scroll = OnHorizontalScroll;
            if (_verticalScrollBar != null)
                _verticalScrollBar.Scroll = OnVerticalScroll;
            SyncScrollBars();
        }

        protected override void OnTemplateChanging()
        {
            if (_horizontalScrollBar != null)
                _horizontalScrollBar.Scroll = null;
            if (_verticalScrollBar != null)
                _verticalScrollBar.Scroll = null;
            _contentPresenter = null;
            _horizontalScrollBar = null;
            _verticalScrollBar = null;
        }

        protected override void OnPropertyChanged(IStyledProperty property)
        {
            base.OnPropertyChanged(property);
            if (property == OffsetProperty && !_syncingOffsetProperty)
            {
                Vector2 value = GetValue(OffsetProperty);
                SetOffsets(value.X, value.Y, true);
            }
            else if (property == IsScrollInertiaEnabledProperty && !IsScrollInertiaEnabled)
            {
                StopScrollInertia();
            }
            SyncScrollBars();
        }

        protected override void OnResourceChanged(string key)
        {
            base.OnResourceChanged(key);
            SyncScrollBars();
        }

        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            VisualElement child = Child;
            if (child == null)
                return Vector2.Zero;

            float horizontalPadding = Math.Max(0f, Padding.X) + Math.Max(0f, Padding.Z);
            float verticalPadding = Math.Max(0f, Padding.Y) + Math.Max(0f, Padding.W);
            float verticalGutter = _showVerticalScrollBar ? GetScrollBarThickness(_verticalScrollBar, true) : 0f;
            float horizontalGutter = _showHorizontalScrollBar ? GetScrollBarThickness(_horizontalScrollBar, false) : 0f;
            Vector2 childAvailable = new Vector2(
                CanScrollHorizontally ? float.MaxValue : Math.Max(0f, availableSize.X - horizontalPadding - verticalGutter),
                CanScrollVertically ? float.MaxValue : Math.Max(0f, availableSize.Y - verticalPadding - horizontalGutter));
            Vector2 desired = child.Measure(childAvailable);

            return new Vector2(
                Math.Min(availableSize.X, desired.X + horizontalPadding),
                Math.Min(availableSize.Y, desired.Y + verticalPadding));
        }

        protected override void ArrangeChildren(RectangleF bounds)
        {
            RefreshContentLayout();
            SyncScrollBars();
            ArrangeTemplate(_panelBounds);
        }

        public void RefreshContentLayout()
        {
            _panelBounds = GetPaddedBounds(Bounds);
            VisualElement child = Child;
            if (child == null)
            {
                _viewportBounds = _panelBounds;
                _extentWidth = _viewportBounds.Width;
                _extentHeight = _viewportBounds.Height;
                _horizontalOffset = 0f;
                _verticalOffset = 0f;
                _showHorizontalScrollBar = false;
                _showVerticalScrollBar = false;
                SyncScrollBars();
                return;
            }

            // Measure with the last viewport so a settled layout measures content at the size it is arranged at;
            // content sized from its constraint would otherwise flip between the panel and viewport sizes.
            RectangleF measuredViewport = CalculateViewport(_showHorizontalScrollBar, _showVerticalScrollBar);
            Vector2 desired = MeasureContent(child, measuredViewport);

            // Resolve scrollbar visibility twice because adding one gutter can make the
            // other axis overflow.
            bool overflowH = CanScrollHorizontally && desired.X > _panelBounds.Width + 0.5f;
            bool overflowV = CanScrollVertically && desired.Y > _panelBounds.Height + 0.5f;
            bool showH = ShouldShowScrollBar(HorizontalScrollBarVisibility, overflowH);
            bool showV = ShouldShowScrollBar(VerticalScrollBarVisibility, overflowV);
            RectangleF viewport = CalculateViewport(showH, showV);
            overflowH = CanScrollHorizontally && desired.X > viewport.Width + 0.5f;
            overflowV = CanScrollVertically && desired.Y > viewport.Height + 0.5f;
            showH = ShouldShowScrollBar(HorizontalScrollBarVisibility, overflowH);
            showV = ShouldShowScrollBar(VerticalScrollBarVisibility, overflowV);
            viewport = CalculateViewport(showH, showV);
            if ((!CanScrollHorizontally && viewport.Width != measuredViewport.Width) ||
                (!CanScrollVertically && viewport.Height != measuredViewport.Height))
                desired = MeasureContent(child, viewport);

            _showHorizontalScrollBar = showH;
            _showVerticalScrollBar = showV;
            _viewportBounds = viewport;
            _extentWidth = CanScrollHorizontally
                ? Math.Max(_viewportBounds.Width, Math.Max(0f, desired.X))
                : _viewportBounds.Width;
            _extentHeight = CanScrollVertically
                ? Math.Max(_viewportBounds.Height, Math.Max(0f, desired.Y))
                : _viewportBounds.Height;

            SetOffsets(
                Clamp(_horizontalOffset, 0f, MaxHorizontalOffset),
                Clamp(_verticalOffset, 0f, MaxVerticalOffset),
                false);
            ArrangeContent(child);
        }

        public void ScrollTo(float horizontalOffset, float verticalOffset)
        {
            SetOffsets(horizontalOffset, verticalOffset, true);
        }

        public void ScrollBy(float horizontalDelta, float verticalDelta)
        {
            SetOffsets(_horizontalOffset + horizontalDelta, _verticalOffset + verticalDelta, true);
        }

        public void LineUp() { ScrollBy(0f, -Math.Max(1f, SmallChange.Y)); }
        public void LineDown() { ScrollBy(0f, Math.Max(1f, SmallChange.Y)); }
        public void LineLeft() { ScrollBy(-Math.Max(1f, SmallChange.X), 0f); }
        public void LineRight() { ScrollBy(Math.Max(1f, SmallChange.X), 0f); }
        public void PageUp() { ScrollBy(0f, -Math.Max(1f, ViewportHeight)); }
        public void PageDown() { ScrollBy(0f, Math.Max(1f, ViewportHeight)); }
        public void PageLeft() { ScrollBy(-Math.Max(1f, ViewportWidth), 0f); }
        public void PageRight() { ScrollBy(Math.Max(1f, ViewportWidth), 0f); }
        public void ScrollToHome() { ScrollTo(0f, 0f); }
        public void ScrollToEnd() { ScrollTo(MaxHorizontalOffset, MaxVerticalOffset); }

        public void BringIntoView(RectangleF contentRectangle, float margin = 0f)
        {
            float nextX = _horizontalOffset;
            float nextY = _verticalOffset;
            margin = Math.Max(0f, margin);

            if (CanScrollHorizontally && _viewportBounds.Width > 0f)
            {
                float visibleLeft = _horizontalOffset + margin;
                float visibleRight = _horizontalOffset + _viewportBounds.Width - margin;
                if (contentRectangle.X < visibleLeft)
                    nextX = contentRectangle.X - margin;
                else if (contentRectangle.Right > visibleRight)
                    nextX = contentRectangle.Right - _viewportBounds.Width + margin;
            }

            if (CanScrollVertically && _viewportBounds.Height > 0f)
            {
                float visibleTop = _verticalOffset + margin;
                float visibleBottom = _verticalOffset + _viewportBounds.Height - margin;
                if (contentRectangle.Y < visibleTop)
                    nextY = contentRectangle.Y - margin;
                else if (contentRectangle.Bottom > visibleBottom)
                    nextY = contentRectangle.Bottom - _viewportBounds.Height + margin;
            }

            SetOffsets(nextX, nextY, true);
        }

        protected override bool HitTestSelf(Vector2 point)
        {
            return InputEnabled;
        }

        protected override IDisposable BeginChildDraw(
            RenderContext context,
            VisualElement child)
        {
            return ReferenceEquals(child, Child)
                ? context.Clip(_viewportBounds)
                : null;
        }

        protected override void DrawSelf(RenderContext context)
        {
        }

        internal override bool PointerScrolled(Vector2 point, int delta)
        {
            if (!InputEnabled || delta == 0)
                return false;

            float direction = delta > 0 ? -1f : 1f;
            Vector2 before = Offset;
            if (CanScrollVertically && MaxVerticalOffset > 0f)
            {
                float step = Math.Max(1f, SmallChange.Y);
                ScrollBy(0f, direction * step);
                AddScrollVelocity(Offset - before);
                return !IsScrollChainingEnabled || (Offset - before).LengthSquared() > 0.0001f;
            }
            if (CanScrollHorizontally && MaxHorizontalOffset > 0f)
            {
                float step = Math.Max(1f, SmallChange.X);
                ScrollBy(direction * step, 0f);
                AddScrollVelocity(Offset - before);
                return !IsScrollChainingEnabled || (Offset - before).LengthSquared() > 0.0001f;
            }
            return !IsScrollChainingEnabled;
        }

        internal override void KeyboardInput(IInputSource input)
        {
            if (!InputEnabled || input == null)
                return;
            if (input.IsNewKeyPressed(Key.Up)) LineUp();
            else if (input.IsNewKeyPressed(Key.Down)) LineDown();
            else if (input.IsNewKeyPressed(Key.Left)) LineLeft();
            else if (input.IsNewKeyPressed(Key.Right)) LineRight();
            else if (input.IsNewKeyPressed(Key.PageUp)) PageUp();
            else if (input.IsNewKeyPressed(Key.PageDown)) PageDown();
            else if (input.IsNewKeyPressed(Key.Home)) ScrollToHome();
            else if (input.IsNewKeyPressed(Key.End)) ScrollToEnd();
        }

        void OnHorizontalScroll(ScrollEventArgs args)
        {
            StopScrollInertia();
            SetOffsets(args.NewValue, _verticalOffset, true);
            SyncScrollBars();
        }

        void OnVerticalScroll(ScrollEventArgs args)
        {
            StopScrollInertia();
            SetOffsets(_horizontalOffset, args.NewValue, true);
            SyncScrollBars();
        }

        void SyncScrollBars()
        {
            SyncScrollBar(
                _horizontalScrollBar,
                Orientation.Horizontal,
                _showHorizontalScrollBar,
                _horizontalOffset,
                MaxHorizontalOffset,
                _viewportBounds.Width,
                SmallChange.X);
            SyncScrollBar(
                _verticalScrollBar,
                Orientation.Vertical,
                _showVerticalScrollBar,
                _verticalOffset,
                MaxVerticalOffset,
                _viewportBounds.Height,
                SmallChange.Y);
        }

        void SyncScrollBar(
            ScrollBar bar,
            Orientation orientation,
            bool visible,
            float value,
            float maximum,
            float viewportSize,
            float smallChange)
        {
            if (bar == null)
                return;
            bar.Visible = visible;
            bar.Enabled = InputEnabled && visible;
            bar.Visibility = visible
                ? ScrollBarVisibility.Visible
                : ScrollBarVisibility.Hidden;
            bar.Orientation = orientation;
            bar.Minimum = 0f;
            bar.Maximum = Math.Max(0f, maximum);
            bar.SetCurrentValue(Clamp(value, 0f, Math.Max(0f, maximum)), false);
            bar.ViewportSize = Math.Max(0f, viewportSize);
            bar.SmallChange = Math.Max(1f, smallChange);
            bar.LargeChange = Math.Max(1f, viewportSize);
            bar.AllowAutoHide = AllowAutoHide;
            bar.MinimumThumbLength = Math.Max(1f, ScrollBarMinimumThumbPixels);
        }

        RectangleF CalculateViewport(bool showHorizontal, bool showVertical)
        {
            float verticalGutter = showVertical ? GetScrollBarThickness(_verticalScrollBar, true) : 0f;
            float horizontalGutter = showHorizontal ? GetScrollBarThickness(_horizontalScrollBar, false) : 0f;
            return new RectangleF(
                _panelBounds.X,
                _panelBounds.Y,
                Math.Max(0f, _panelBounds.Width - verticalGutter),
                Math.Max(0f, _panelBounds.Height - horizontalGutter));
        }

        float GetScrollBarThickness(ScrollBar bar, bool vertical)
        {
            if (bar == null)
                return Math.Max(0f, ScrollerWidthPixels);
            bar.EnsureStyleApplied();
            float? styled = vertical ? bar.Width : bar.Height;
            return Math.Max(0f, styled ?? ScrollerWidthPixels);
        }

        RectangleF GetPaddedBounds(RectangleF bounds)
        {
            float left = bounds.X + Math.Max(0f, Padding.X);
            float top = bounds.Y + Math.Max(0f, Padding.Y);
            float right = bounds.Right - Math.Max(0f, Padding.Z);
            float bottom = bounds.Bottom - Math.Max(0f, Padding.W);
            return new RectangleF(left, top, Math.Max(0f, right - left), Math.Max(0f, bottom - top));
        }

        void SetOffsets(float horizontal, float vertical, bool arrange)
        {
            float nextX = CanScrollHorizontally ? Clamp(horizontal, 0f, MaxHorizontalOffset) : 0f;
            float nextY = CanScrollVertically ? Clamp(vertical, 0f, MaxVerticalOffset) : 0f;
            if (Math.Abs(nextX - _horizontalOffset) < 0.001f && Math.Abs(nextY - _verticalOffset) < 0.001f)
                return;

            Vector2 oldOffset = new Vector2(_horizontalOffset, _verticalOffset);
            _horizontalOffset = nextX;
            _verticalOffset = nextY;
            _syncingOffsetProperty = true;
            try
            {
                SetValue(OffsetProperty, new Vector2(nextX, nextY));
            }
            finally
            {
                _syncingOffsetProperty = false;
            }
            if (arrange && Child != null && _viewportBounds.Width >= 0f && _viewportBounds.Height >= 0f)
                ArrangeContent(Child);
            Action<ScrollChangedEventArgs> changed = ScrollChanged;
            if (changed != null)
                changed(new ScrollChangedEventArgs(oldOffset, Offset, Extent, Viewport));
            SyncScrollBars();
        }

        void AddScrollVelocity(Vector2 appliedDelta)
        {
            if (!IsScrollInertiaEnabled || AnimationController == null ||
                appliedDelta.LengthSquared() <= 0.0001f)
            {
                if (!IsScrollInertiaEnabled)
                    StopScrollInertia();
                return;
            }

            float maximumX = Math.Max(1f, SmallChange.X);
            float maximumY = Math.Max(1f, SmallChange.Y);
            _scrollVelocity.X = Clamp(
                _scrollVelocity.X + appliedDelta.X * WHEEL_INERTIA_IMPULSE,
                -maximumX,
                maximumX);
            _scrollVelocity.Y = Clamp(
                _scrollVelocity.Y + appliedDelta.Y * WHEEL_INERTIA_IMPULSE,
                -maximumY,
                maximumY);
            StartScrollInertia();
        }

        void StartScrollInertia()
        {
            if (!IsScrollInertiaEnabled || AnimationController == null)
                return;
            _scrollInertiaAnimation = this.RunAnimation(
                "ScrollInertia",
                AnimationConflict.Replace,
                new ScrollInertiaKeyframe(this),
                new ActionKeyframe(CompleteScrollInertia, false));
        }

        void StopScrollInertia()
        {
            if (_scrollInertiaAnimation != null)
                _scrollInertiaAnimation.Cancel();
            _scrollInertiaAnimation = null;
            _scrollVelocity = Vector2.Zero;
        }

        void ApplyInertiaState(Vector2 offset, Vector2 velocity)
        {
            _scrollVelocity = velocity;
            SetOffsets(offset.X, offset.Y, true);
        }

        void CompleteScrollInertia()
        {
            _scrollVelocity = Vector2.Zero;
            _scrollInertiaAnimation = null;
        }

        bool CanScrollHorizontally => HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled;
        bool CanScrollVertically => VerticalScrollBarVisibility != ScrollBarVisibility.Disabled;

        static bool ShouldShowScrollBar(ScrollBarVisibility visibility, bool overflow)
        {
            return visibility == ScrollBarVisibility.Visible ||
                   (visibility == ScrollBarVisibility.Auto && overflow);
        }

        // Like ScrollContentPresenter, the content gets a slot at least the viewport's size and its own
        // alignment applies inside it, so smaller centered content stays centered.
        Vector2 MeasureContent(VisualElement child, RectangleF viewport)
        {
            return child.Measure(new Vector2(
                CanScrollHorizontally ? float.MaxValue : viewport.Width,
                CanScrollVertically ? float.MaxValue : viewport.Height));
        }

        void ArrangeContent(VisualElement child)
        {
            child.ArrangeInSlot(new RectangleF(
                _viewportBounds.X - _horizontalOffset,
                _viewportBounds.Y - _verticalOffset,
                _extentWidth,
                _extentHeight));
        }

        static float Clamp(float value, float min, float max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        public sealed class ScrollContentPresenter : VisualElement
        {
            protected override void DrawSelf(RenderContext context)
            {
            }
        }
    }
}
