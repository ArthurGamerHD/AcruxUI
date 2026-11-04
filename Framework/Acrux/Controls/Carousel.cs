using System;
using Acrux.Animation;
using Acrux.Core.Invalidation;
using Acrux.Core.PropertySystem;
using Acrux.Layout;

namespace Acrux.Controls
{
    /// <summary>
    /// Carousel: presents its items as pages, sliding to <see cref="SelectedIndex"/>.
    /// A <see cref="ViewportFraction"/> below 1 shows several adjacent pages at once.
    /// </summary>
    public sealed class Carousel : ItemsControl
    {
        static Carousel() { }

        public static readonly StyledProperty<int> SelectedIndexProperty =
            StyledProperty.Register<Carousel, int>(nameof(SelectedIndex), 0, UiInvalidation.Arrange);
        public static readonly StyledProperty<bool> WrapSelectionProperty =
            StyledProperty.Register<Carousel, bool>(nameof(WrapSelection), false, UiInvalidation.Arrange);
        public static readonly StyledProperty<float> ViewportFractionProperty =
            StyledProperty.Register<Carousel, float>(nameof(ViewportFraction), 1f, UiInvalidation.Measure);
        public static readonly StyledProperty<PageSlide> PageTransitionProperty =
            StyledProperty.Register<Carousel, PageSlide>(nameof(PageTransition), null, UiInvalidation.None);

        int _position;
        int _pendingStep;

        public int SelectedIndex
        {
            get { return GetValue(SelectedIndexProperty); }
            set { SetValue(SelectedIndexProperty, value); }
        }

        public bool WrapSelection
        {
            get { return GetValue(WrapSelectionProperty); }
            set { SetValue(WrapSelectionProperty, value); }
        }

        public float ViewportFraction
        {
            get { return GetValue(ViewportFractionProperty); }
            set { SetValue(ViewportFractionProperty, value); }
        }

        public PageSlide PageTransition
        {
            get { return GetValue(PageTransitionProperty); }
            set { SetValue(PageTransitionProperty, value); }
        }

        // Unwrapped slide position, so wrapping keeps sliding in the travel direction.
        internal int Position => _position;

        public void Next()
        {
            Step(1);
        }

        public void Previous()
        {
            Step(-1);
        }

        void Step(int direction)
        {
            int count = ItemCount;
            if (count == 0)
                return;
            int next = SelectedIndex + direction;
            if (!WrapSelection && (next < 0 || next >= count))
                return;
            _pendingStep = direction;
            SelectedIndex = Normalize(next, count);
        }

        protected override Panel CreateDefaultItemsPanel()
        {
            return new VirtualizingCarouselPanel();
        }

        protected override void OnPropertyChanged(IStyledProperty property)
        {
            base.OnPropertyChanged(property);
            if (property != SelectedIndexProperty)
                return;

            int count = ItemCount;
            int index = SelectedIndex;
            if (_pendingStep != 0)
            {
                _position += _pendingStep;
                _pendingStep = 0;
            }
            else if (WrapSelection && count > 0)
            {
                int lower = index + (int)Math.Floor((_position - index) / (float)count) * count;
                int upper = lower + count;
                _position = _position - lower <= upper - _position ? lower : upper;
            }
            else
            {
                _position = index;
            }
            ItemsPanelRoot.Invalidate(UiInvalidation.Arrange);
        }

        protected override void OnItemsRefreshed()
        {
            base.OnItemsRefreshed();
            int count = ItemCount;
            if (count > 0 && (SelectedIndex < 0 || SelectedIndex >= count))
                SelectedIndex = WrapSelection ? Normalize(SelectedIndex, count) : Math.Max(0, Math.Min(count - 1, SelectedIndex));
        }

        static int Normalize(int index, int count)
        {
            return (index % count + count) % count;
        }
    }
}
