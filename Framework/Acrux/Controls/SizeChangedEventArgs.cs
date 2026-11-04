using Acrux.Interactivity;
using VRageMath;

namespace Acrux.Controls
{
    /// <summary>SizeChangedEventArgs, raised by Control.SizeChanged after an arrange changes a control's size.</summary>
    public sealed class SizeChangedEventArgs : RoutedEventArgs
    {
        public SizeChangedEventArgs(object source, Vector2 previousSize, Vector2 newSize)
        {
            Source = source;
            PreviousSize = previousSize;
            NewSize = newSize;
        }

        public Vector2 PreviousSize { get; }
        public Vector2 NewSize { get; }
        public bool WidthChanged => PreviousSize.X != NewSize.X;
        public bool HeightChanged => PreviousSize.Y != NewSize.Y;
    }
}
