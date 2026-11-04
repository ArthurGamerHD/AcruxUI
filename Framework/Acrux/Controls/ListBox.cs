using System;
using Acrux.Core;
using Acrux.Core.Invalidation;
using Acrux.Core.PropertySystem;
using Acrux.Layout;
using Acrux.Rendering;
using Acrux.Styling;
using Acrux.Core.Input;
using VRageMath;

namespace Acrux.Controls
{
    /// <summary>A single-selection items control with renderer-neutral item containers.</summary>
    public sealed class ListBox : ItemsControl
    {
        static ListBox() { }

        object _selectedItem;

        public static readonly StyledProperty<BackgroundBrush> BackgroundProperty =
            StyledProperty.Register<ListBox, BackgroundBrush>(
                nameof(Background), new SolidColorBrush(Color.Transparent));
        public static readonly StyledProperty<Color> BorderProperty =
            StyledProperty.Register<ListBox, Color>(
                "Border", Color.Transparent);
        public static readonly StyledProperty<Vector4> BorderThicknessProperty =
            StyledProperty.Register<ListBox, Vector4>(nameof(BorderThickness), new Vector4(0f), UiInvalidation.Measure);
        public static readonly StyledProperty<BackgroundBrush> ItemBackgroundProperty =
            StyledProperty.Register<ListBox, BackgroundBrush>(
                nameof(ItemBackground), new SolidColorBrush(Color.Transparent));
        public static readonly StyledProperty<BackgroundBrush> SelectedItemBackgroundProperty =
            StyledProperty.Register<ListBox, BackgroundBrush>(
                nameof(SelectedItemBackground), new SolidColorBrush(new Color(80, 160, 255, 180)));
        public static readonly StyledProperty<Color> ItemBorderProperty =
            StyledProperty.Register<ListBox, Color>(
                nameof(ItemBorder), Color.Transparent);
        public static readonly StyledProperty<Vector4> ItemBorderThicknessProperty =
            StyledProperty.Register<ListBox, Vector4>(nameof(ItemBorderThickness), new Vector4(0f), UiInvalidation.Measure);
        public static readonly StyledProperty<Vector4> ItemPaddingProperty =
            StyledProperty.Register<ListBox, Vector4>(
                nameof(ItemPadding), new Vector4(8f, 4f, 8f, 4f), UiInvalidation.Measure);

        public BackgroundBrush Background
        {
            get { return GetValue(BackgroundProperty); }
            set { SetValue(BackgroundProperty, value); }
        }

        public Color BorderColor
        {
            get { return GetValue(BorderProperty); }
            set { SetValue(BorderProperty, value); }
        }

        public Vector4 BorderThickness
        {
            get { return GetValue(BorderThicknessProperty); }
            set { SetValue(BorderThicknessProperty, Vector4.Max(value, Vector4.Zero)); }
        }

        public BackgroundBrush ItemBackground
        {
            get { return GetValue(ItemBackgroundProperty); }
            set { SetValue(ItemBackgroundProperty, value); }
        }

        public BackgroundBrush SelectedItemBackground
        {
            get { return GetValue(SelectedItemBackgroundProperty); }
            set { SetValue(SelectedItemBackgroundProperty, value); }
        }

        public Color ItemBorder
        {
            get { return GetValue(ItemBorderProperty); }
            set { SetValue(ItemBorderProperty, value); }
        }

        public Vector4 ItemBorderThickness
        {
            get { return GetValue(ItemBorderThicknessProperty); }
            set { SetValue(ItemBorderThicknessProperty, Vector4.Max(value, Vector4.Zero)); }
        }

        public Vector4 ItemPadding
        {
            get { return GetValue(ItemPaddingProperty); }
            set { SetValue(ItemPaddingProperty, value); }
        }

        public object SelectedItem
        {
            get { return _selectedItem; }
            set { Select(value, false); }
        }

        public int SelectedIndex
        {
            get { return IndexOfItem(_selectedItem); }
        }

        public Action<object> SelectionChanged { get; set; }

        public void SelectIndex(int index, bool notify = false)
        {
            Select(index >= 0 && index < ItemCount ? GetItemAt(index) : null, notify);
        }

        public void Select(object item, bool notify = false)
        {
            object next = item;
            if (next != null && IndexOfItem(next) < 0)
                return;
            if (ReferenceEquals(_selectedItem, next) || Equals(_selectedItem, next))
                return;
            _selectedItem = next;
            SynchronizeSelection();
            if (notify && SelectionChanged != null)
                SelectionChanged(next);
        }

        protected override VisualElement CreateContainer(object item, VisualElement content)
        {
            return new ListBoxItem { Child = content };
        }

        protected override void PrepareContainer(
            VisualElement container,
            VisualElement content,
            object item)
        {
            ListBoxItem listBoxItem = container as ListBoxItem;
            if (listBoxItem == null)
                return;
            listBoxItem.Item = item;
            listBoxItem.Background = ItemBackground;
            listBoxItem.SelectedBackground = SelectedItemBackground;
            listBoxItem.BorderColor = ItemBorder;
            listBoxItem.BorderThickness = ItemBorderThickness;
            listBoxItem.Padding = ItemPadding;
            listBoxItem.IsSelected = IsSelected(item);
            listBoxItem.Clicked = OnItemClicked;
            listBoxItem.MoveSelection = MoveSelection;
        }

        protected override void OnItemsRefreshed()
        {
            if (_selectedItem != null)
            {
                int selectedIndex = IndexOfItem(_selectedItem);
                _selectedItem = selectedIndex < 0 ? null : GetItemAt(selectedIndex);
            }
            SynchronizeSelection();
        }

        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            SynchronizeContainers();
            return base.MeasureOverride(availableSize);
        }

        protected override void DrawSelf(RenderContext context)
        {
            SynchronizeContainers();
            context.Fill(Bounds, Background);
            context.Border(Bounds, BorderColor, BorderThickness);
        }

        void OnItemClicked(ListBoxItem item)
        {
            if (item != null)
                Select(item.Item, true);
        }

        void MoveSelection(ListBoxItem item, int direction)
        {
            int current = item == null ? SelectedIndex : IndexOfItem(item.Item);
            int next = Math.Max(0, Math.Min(ItemCount - 1, current + direction));
            if (ItemCount > 0)
                SelectIndex(next, true);
        }

        void SynchronizeSelection()
        {
            for (int i = 0; i < ItemCount; i++)
            {
                ListBoxItem item = GetContainerAt(i) as ListBoxItem;
                if (item != null)
                    item.IsSelected = IsSelected(GetItemAt(i));
            }
            Invalidate(UiInvalidation.Render);
        }

        void SynchronizeContainers()
        {
            for (int i = 0; i < ItemCount; i++)
            {
                ListBoxItem item = GetContainerAt(i) as ListBoxItem;
                if (item == null)
                    continue;
                item.Background = ItemBackground;
                item.SelectedBackground = SelectedItemBackground;
                item.BorderColor = ItemBorder;
                item.BorderThickness = ItemBorderThickness;
                item.Padding = ItemPadding;
                item.IsSelected = IsSelected(GetItemAt(i));
            }
        }

        bool IsSelected(object item)
        {
            return ReferenceEquals(_selectedItem, item) || Equals(_selectedItem, item);
        }
    }

    public sealed class ListBoxItem : Decorator
    {
        static ListBoxItem() { }

        bool _isSelected;

        public static readonly StyledProperty<BackgroundBrush> BackgroundProperty =
            StyledProperty.Register<ListBoxItem, BackgroundBrush>(
                nameof(Background), new SolidColorBrush(Color.Transparent));
        public static readonly StyledProperty<BackgroundBrush> SelectedBackgroundProperty =
            StyledProperty.Register<ListBoxItem, BackgroundBrush>(
                nameof(SelectedBackground), new SolidColorBrush(new Color(80, 160, 255, 180)));
        public static readonly StyledProperty<Color> BorderProperty =
            StyledProperty.Register<ListBoxItem, Color>("Border", Color.Transparent);
        public static readonly StyledProperty<Vector4> BorderThicknessProperty =
            StyledProperty.Register<ListBoxItem, Vector4>(nameof(BorderThickness), new Vector4(0f), UiInvalidation.Measure);

        public object Item { get; internal set; }
        public Action<ListBoxItem> Clicked { get; set; }
        public Action<ListBoxItem, int> MoveSelection { get; set; }
        public override bool AcceptsKeyboardFocus
        {
            get { return true; }
        }

        public BackgroundBrush Background
        {
            get { return GetValue(BackgroundProperty); }
            set { SetValue(BackgroundProperty, value); }
        }

        public BackgroundBrush SelectedBackground
        {
            get { return GetValue(SelectedBackgroundProperty); }
            set { SetValue(SelectedBackgroundProperty, value); }
        }

        public Color BorderColor
        {
            get { return GetValue(BorderProperty); }
            set { SetValue(BorderProperty, value); }
        }

        public Vector4 BorderThickness
        {
            get { return GetValue(BorderThicknessProperty); }
            set { SetValue(BorderThicknessProperty, Vector4.Max(value, Vector4.Zero)); }
        }

        public bool IsSelected
        {
            get { return _isSelected; }
            set
            {
                if (_isSelected == value)
                    return;
                _isSelected = value;
                PseudoClasses.Set(PseudoClassNames.Selected, value);
                Invalidate(UiInvalidation.Render);
            }
        }

        protected override Vector4 GetLayoutPadding()
        {
            return Padding + BorderThickness;
        }

        protected override bool HitTestSelf(Vector2 point)
        {
            return true;
        }

        protected override void UpdatePseudoClasses()
        {
            base.UpdatePseudoClasses();
            PseudoClasses.Set(PseudoClassNames.Selected, IsSelected);
            PseudoClasses.Set(PseudoClassNames.Pressed, IsPressed);
        }

        protected override void DrawSelf(RenderContext context)
        {
            context.Fill(Bounds, IsSelected ? SelectedBackground : Background);
            context.Border(Bounds, BorderColor, BorderThickness);
        }

        internal override void PointerReleased(Vector2 point, bool clicked)
        {
            if (clicked && Clicked != null)
                Clicked(this);
        }

        internal override void KeyboardInput(IInputSource input)
        {
            if (input == null)
                return;
            if ((input.IsNewKeyPressed(Key.Enter) || input.IsNewKeyPressed(Key.Space)) && Clicked != null)
                Clicked(this);
            else if (input.IsNewKeyPressed(Key.Up) && MoveSelection != null)
                MoveSelection(this, -1);
            else if (input.IsNewKeyPressed(Key.Down) && MoveSelection != null)
                MoveSelection(this, 1);
        }
    }
}
