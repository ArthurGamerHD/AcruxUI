using Acrux.Interactivity;

namespace Acrux.Core.Input
{
    /// <summary>
    /// Avalonia's PointerEventArgs subset, raised by VisualElement.PointerEntered and VisualElement.PointerExited.
    /// Pointer positions and key modifiers are not carried yet.
    /// </summary>
    public class PointerEventArgs : RoutedEventArgs
    {
        public PointerEventArgs(RoutedEvent routedEvent, object source)
            : base(routedEvent, source)
        {
        }
    }
}
