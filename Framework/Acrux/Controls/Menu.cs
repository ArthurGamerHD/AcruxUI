using System;
using System.Collections.Generic;
using Acrux.Core;
using Acrux.Core.Invalidation;
using Acrux.Core.PropertySystem;
using Acrux.Layout;
using Acrux.Rendering;
using Acrux.Styling;
using VRageMath;

namespace Acrux.Controls
{
    /// <summary>
    /// Menu: a horizontal bar of top-level <see cref="MenuItem"/>s. Clicking an item with children opens its
    /// submenu; the whole open chain is one light-dismiss overlay, so a click on another top-level item switches menus.
    /// </summary>
    public class Menu : VisualElement, IOverlayProvider, ILightDismissOverlayProvider
    {
        public static readonly StyledProperty<BackgroundBrush> BackgroundProperty =
            StyledProperty.Register<Menu, BackgroundBrush>(nameof(Background), null, UiInvalidation.Render);

        readonly List<MenuItem> _openPath = new List<MenuItem>();
        readonly MenuOverlayLayer _layer;

        static Menu()
        {
            StylePropertyRegistry.Global.RegisterType<Menu>(nameof(Menu));
        }

        public Menu()
        {
            Items = new MenuItemCollection(this, null);
            _layer = new MenuOverlayLayer(this);
        }

        public BackgroundBrush Background
        {
            get { return GetValue(BackgroundProperty); }
            set { SetValue(BackgroundProperty, value); }
        }

        public IList<MenuItem> Items { get; }

        internal IReadOnlyList<MenuItem> OpenPath => _openPath;

        public bool IsOpen => _openPath.Count > 0;

        public void Close()
        {
            if (_openPath.Count == 0)
                return;
            for (int i = 0; i < _openPath.Count; i++)
                _openPath[i].IsSubMenuOpen = false;
            _openPath.Clear();
            Invalidate(UiInvalidation.Render);
        }

        internal void Activate(MenuItem item)
        {
            if (item.HasSubMenu)
            {
                Toggle(item);
                return;
            }
            item.Execute();
            Close();
        }

        void Toggle(MenuItem item)
        {
            int level = item.Level;
            bool wasOpen = level < _openPath.Count && ReferenceEquals(_openPath[level], item);
            while (_openPath.Count > level)
            {
                _openPath[_openPath.Count - 1].IsSubMenuOpen = false;
                _openPath.RemoveAt(_openPath.Count - 1);
            }
            if (!wasOpen)
            {
                item.IsSubMenuOpen = true;
                _openPath.Add(item);
            }
            Invalidate(UiInvalidation.Render);
        }

        internal MenuItem TopLevelItemAt(Vector2 point)
        {
            for (int i = 0; i < Items.Count; i++)
                if (Items[i].Visible && Items[i].Bounds.Contains(point))
                    return Items[i];
            return null;
        }

        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            float width = 0f;
            float height = 0f;
            for (int i = 0; i < Items.Count; i++)
            {
                MenuItem item = Items[i];
                if (!item.Visible)
                    continue;
                Vector2 desired = item.Measure(new Vector2(float.PositiveInfinity, availableSize.Y));
                width += desired.X;
                height = Math.Max(height, desired.Y);
            }
            return new Vector2(width, height);
        }

        protected override void ArrangeChildren(RectangleF bounds)
        {
            float x = bounds.X;
            for (int i = 0; i < Items.Count; i++)
            {
                MenuItem item = Items[i];
                if (!item.Visible)
                    continue;
                float width = item.DesiredSize.X;
                item.ArrangeInSlot(new RectangleF(x, bounds.Y, width, bounds.Height));
                x += width;
            }
        }

        protected override void DrawSelf(RenderContext context)
        {
            BackgroundBrush background = Background;
            if (background != null)
                context.Fill(Bounds, background);
        }

        bool IOverlayProvider.IsOverlayOpen => IsOpen && Visible;
        VisualElement IOverlayProvider.OverlayElement => _layer;
        void IOverlayProvider.UpdateOverlayLayout() { _layer.UpdateLayout(); }
        void IOverlayProvider.CloseOverlay() { Close(); }
        bool ILightDismissOverlayProvider.IsLightDismissEnabled => true;
        bool ILightDismissOverlayProvider.OverlayDismissEventPassThrough => false;

        internal sealed class MenuItemCollection : IList<MenuItem>
        {
            readonly List<MenuItem> _items = new List<MenuItem>();
            readonly VisualElement _host;
            readonly MenuItem _parent;

            public MenuItemCollection(VisualElement host, MenuItem parent)
            {
                _host = host;
                _parent = parent;
            }

            public int Count => _items.Count;
            public bool IsReadOnly => false;

            public MenuItem this[int index]
            {
                get { return _items[index]; }
                set
                {
                    RemoveAt(index);
                    Insert(index, value);
                }
            }

            public void Add(MenuItem item)
            {
                Insert(_items.Count, item);
            }

            public void Insert(int index, MenuItem item)
            {
                if (item == null)
                    throw new ArgumentNullException(nameof(item));
                _items.Insert(index, item);
                item.ParentItem = _parent;
                _host.AddChild(item);
                if (_parent != null)
                    _parent.ItemsChanged();
            }

            public void RemoveAt(int index)
            {
                MenuItem item = _items[index];
                _items.RemoveAt(index);
                item.ParentItem = null;
                _host.RemoveChild(item);
                if (_parent != null)
                    _parent.ItemsChanged();
            }

            public bool Remove(MenuItem item)
            {
                int index = _items.IndexOf(item);
                if (index < 0)
                    return false;
                RemoveAt(index);
                return true;
            }

            public void Clear()
            {
                while (_items.Count > 0)
                    RemoveAt(_items.Count - 1);
            }

            public int IndexOf(MenuItem item) => _items.IndexOf(item);
            public bool Contains(MenuItem item) => _items.Contains(item);
            public void CopyTo(MenuItem[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);
            public IEnumerator<MenuItem> GetEnumerator() => _items.GetEnumerator();
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _items.GetEnumerator();
        }
    }

    /// <summary>Full-window layer holding the open submenus; clicks on the menu bar are routed back to its items.</summary>
    sealed class MenuOverlayLayer : VisualElement
    {
        readonly Menu _menu;

        public MenuOverlayLayer(Menu menu)
        {
            _menu = menu;
        }

        public void UpdateLayout()
        {
            VisualElement root = _menu;
            while (root.Parent != null)
                root = root.Parent;
            RectangleF area = root.Bounds;

            IReadOnlyList<MenuItem> path = _menu.OpenPath;
            if (!MatchesPath(path))
            {
                ClearChildren();
                for (int i = 0; i < path.Count; i++)
                    AddChild(path[i].SubMenu);
            }

            Measure(area.Size);
            Arrange(area);
            for (int i = 0; i < path.Count; i++)
            {
                MenuItem owner = path[i];
                VisualElement panel = owner.SubMenu;
                Vector2 size = panel.Measure(new Vector2(area.Width, area.Height));
                RectangleF anchor = owner.Bounds;
                float x = owner.Level == 0 ? anchor.X : anchor.Right;
                float y = owner.Level == 0 ? anchor.Bottom : anchor.Y;
                x = MathHelper.Clamp(x, area.X, Math.Max(area.X, area.Right - size.X));
                y = MathHelper.Clamp(y, area.Y, Math.Max(area.Y, area.Bottom - size.Y));
                panel.Arrange(new RectangleF(x, y, size.X, size.Y));
            }
        }

        bool MatchesPath(IReadOnlyList<MenuItem> path)
        {
            IReadOnlyList<VisualElement> children = LogicalChildren;
            if (children.Count != path.Count)
                return false;
            for (int i = 0; i < path.Count; i++)
                if (!ReferenceEquals(children[i], path[i].SubMenu))
                    return false;
            return true;
        }

        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            return availableSize;
        }

        protected override void ArrangeChildren(RectangleF bounds)
        {
        }

        protected override bool HitTestSelf(Vector2 point)
        {
            return _menu.TopLevelItemAt(point) != null;
        }

        internal override bool OverlayContains(Vector2 point)
        {
            if (_menu.TopLevelItemAt(point) != null)
                return true;
            IReadOnlyList<VisualElement> panels = LogicalChildren;
            for (int i = 0; i < panels.Count; i++)
                if (panels[i].Visible && panels[i].Bounds.Contains(point))
                    return true;
            return false;
        }

        internal override void PointerReleased(Vector2 point, bool clicked)
        {
            MenuItem item = clicked ? _menu.TopLevelItemAt(point) : null;
            if (item != null)
                _menu.Activate(item);
        }
    }

    /// <summary>
    /// MenuItem: a header, optional icon, and child items that open as a submenu. Leaf items run their
    /// command and close the menu.
    /// </summary>
    public class MenuItem : Button
    {
        // Whether this control counts as open in VisualTree's open-overlay count. Setting the open property to its default
        // (a first "close") reports a change too, so only real transitions may move the count.
        bool _overlayCounted;

        public static readonly StyledProperty<object> HeaderProperty =
            StyledProperty.Register<MenuItem, object>(nameof(Header), null, UiInvalidation.Measure);
        public static readonly StyledProperty<object> IconProperty =
            StyledProperty.Register<MenuItem, object>(nameof(Icon), null, UiInvalidation.Measure);
        public static readonly StyledProperty<bool> IsSubMenuOpenProperty =
            StyledProperty.Register<MenuItem, bool>(nameof(IsSubMenuOpen), false, UiInvalidation.Render);

        readonly Border _subMenu;
        readonly StackPanel _subMenuItems;
        readonly Grid _row;
        readonly Border _iconHost;
        readonly TextBlock _headerText;
        readonly Border _arrow;
        VisualElement _headerVisual;
        VisualElement _iconVisual;

        static MenuItem()
        {
            StylePropertyRegistry.Global.RegisterType<MenuItem>(nameof(MenuItem));
        }

        public MenuItem()
        {
            _subMenuItems = new StackPanel();
            _subMenu = new Border { Child = _subMenuItems, StyleClass = "menu-dropdown" };
            Items = new Menu.MenuItemCollection(_subMenuItems, this);

            _iconHost = new Border { StyleClass = "menu-icon-host", IsVisible = false };
            _headerText = new TextBlock { StyleClass = "menu-header", VerticalAlignment = VerticalAlignment.Center };
            _arrow = new Border
            {
                StyleClass = "menu-arrow",
                IsVisible = false,
                OpacityMask = new ImageBrush("RightArrow") { Stretch = Stretch.Uniform }
            };
            _row = new Grid();
            _row.SetColumnDefinitions(GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto);
            _row.Set(_iconHost, 0, 0);
            _row.Set(_headerText, 1, 0);
            _row.Set(_arrow, 2, 0);
            Content = _row;
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
        }

        public object Header
        {
            get { return GetValue(HeaderProperty); }
            set { SetValue(HeaderProperty, value); }
        }

        public new object Icon
        {
            get { return GetValue(IconProperty); }
            set { SetValue(IconProperty, value); }
        }

        public bool IsSubMenuOpen
        {
            get { return GetValue(IsSubMenuOpenProperty); }
            set { SetValue(IsSubMenuOpenProperty, value); }
        }

        public IList<MenuItem> Items { get; }

        public bool HasSubMenu => Items.Count > 0;

        public bool IsTopLevel => ParentItem == null;

        internal MenuItem ParentItem { get; set; }

        internal int Level => ParentItem == null ? 0 : ParentItem.Level + 1;

        internal Border SubMenu => _subMenu;

        internal Menu OwnerMenu
        {
            get
            {
                MenuItem top = this;
                while (top.ParentItem != null)
                    top = top.ParentItem;
                for (VisualElement current = top.Parent; current != null; current = current.Parent)
                {
                    var menu = current as Menu;
                    if (menu != null)
                        return menu;
                }
                return null;
            }
        }

        internal void ItemsChanged()
        {
            UpdateArrow();
            Invalidate(UiInvalidation.Measure);
        }

        internal void Execute()
        {
            base.InvokeClick();
        }

        internal ContextMenu OwnerContextMenu
        {
            get
            {
                for (VisualElement current = Parent; current != null; current = current.Parent)
                {
                    var menu = current as ContextMenu;
                    if (menu != null)
                        return menu;
                }
                return null;
            }
        }

        protected override void InvokeClick()
        {
            if (!IsEffectivelyEnabled)
                return;
            Menu menu = OwnerMenu;
            ContextMenu contextMenu = menu == null ? OwnerContextMenu : null;
            if (menu != null)
                menu.Activate(this);
            else if (contextMenu != null)
                contextMenu.Activate(this);
            else
                base.InvokeClick();
        }

        protected override void OnPropertyChanged(IStyledProperty property)
        {
            base.OnPropertyChanged(property);
            if (property == IsSubMenuOpenProperty && _overlayCounted != IsSubMenuOpen)
            {
                _overlayCounted = IsSubMenuOpen;
                VisualTree.OverlayOpenChanged(_overlayCounted);
            }
            if (property == HeaderProperty)
                UpdateHeader();
            else if (property == IconProperty)
                UpdateIcon();
        }

        protected override void UpdatePseudoClasses()
        {
            base.UpdatePseudoClasses();
            PseudoClasses.Set(PseudoClassNames.Open, IsSubMenuOpen);
            UpdateArrow();
        }

        void UpdateHeader()
        {
            object header = Header;
            var visual = header as VisualElement;
            if (_headerVisual != null && !ReferenceEquals(_headerVisual, visual))
                _row.RemoveChild(_headerVisual);
            _headerVisual = visual;
            if (visual != null)
            {
                if (!ReferenceEquals(visual.Parent, _row))
                    _row.Set(visual, 1, 0);
                _headerText.IsVisible = false;
                return;
            }
            _headerText.Text = header == null ? string.Empty : header.ToString();
            _headerText.IsVisible = true;
        }

        void UpdateIcon()
        {
            object icon = Icon;
            VisualElement visual = icon as VisualElement;
            string texture = icon as string;
            if (visual == null && !string.IsNullOrEmpty(texture))
                visual = new Border
                {
                    StyleClass = "menu-icon",
                    OpacityMask = new ImageBrush(texture) { Stretch = Stretch.Uniform }
                };
            _iconVisual = visual;
            _iconHost.Child = visual;
            _iconHost.IsVisible = visual != null;
        }

        void UpdateArrow()
        {
            bool visible = HasSubMenu && !IsTopLevel;
            if (_arrow.IsVisible != visible)
                _arrow.IsVisible = visible;
        }
    }
}
