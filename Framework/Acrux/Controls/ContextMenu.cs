using System.Collections.Generic;
using Acrux.Controls.Primitives;
using Acrux.Core;
using Acrux.Layout;
using Acrux.Styling;
using VRageMath;

namespace Acrux.Controls
{
    /// <summary>
    /// ContextMenu: a list of menu items attached to a control through <see cref="Control.ContextMenu"/>,
    /// opened at the pointer by a secondary click on that control and closed by picking an item or clicking away.
    /// </summary>
    public class ContextMenu : VisualElement, IOverlayProvider, ILightDismissOverlayProvider
    {
        readonly Popup _popup;

        static ContextMenu()
        {
            StylePropertyRegistry.Global.RegisterType<ContextMenu>(nameof(ContextMenu));
        }

        public ContextMenu()
        {
            var items = new StackPanel();
            Items = new Menu.MenuItemCollection(items, null);
            _popup = new Popup
            {
                Child = new Border { Child = items, StyleClass = "menu-dropdown" },
                Placement = PlacementMode.Pointer,
                IsLightDismissEnabled = true
            };
            _popup.Closed = OnPopupClosed;
            AddChild(_popup);
        }

        public IList<MenuItem> Items { get; }

        public bool IsOpen => _popup.IsOpen;

        public Control PlacementTarget { get; private set; }

        public System.Action Opened { get; set; }

        public System.Action Closed { get; set; }

        public void Open(Control control)
        {
            var element = control as VisualElement;
            Open(control, element == null ? Vector2.Zero : element.Bounds.Position);
        }

        internal void Open(Control control, Vector2 point)
        {
            if (IsOpen || Items.Count == 0)
                return;
            PlacementTarget = control;
            var element = control as VisualElement;
            if (element != null)
            {
                OverlayOwner = element;
                InheritDataContext(element.DataContext);
            }
            _popup.PlacementRect = new RectangleF(point.X, point.Y, 0f, 0f);
            _popup.Open();
            if (Opened != null)
                Opened();
        }

        public void Close()
        {
            _popup.Close();
        }

        internal void Activate(MenuItem item)
        {
            item.Execute();
            Close();
        }

        void OnPopupClosed()
        {
            if (Closed != null)
                Closed();
        }

        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            return Vector2.Zero;
        }

        bool IOverlayProvider.IsOverlayOpen => IsOpen;
        VisualElement IOverlayProvider.OverlayElement => _popup.Child;
        void IOverlayProvider.UpdateOverlayLayout() { ((IOverlayProvider)_popup).UpdateOverlayLayout(); }
        void IOverlayProvider.CloseOverlay() { Close(); }
        bool ILightDismissOverlayProvider.IsLightDismissEnabled => true;
        bool ILightDismissOverlayProvider.OverlayDismissEventPassThrough => false;
    }
}
