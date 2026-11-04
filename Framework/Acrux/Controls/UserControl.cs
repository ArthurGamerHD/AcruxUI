using Acrux.Layout;

namespace Acrux.Controls
{
    /// <summary>
    /// A reusable view whose single content tree is normally supplied by generated
    /// XML initialization code.
    /// </summary>
    public class UserControl : Decorator
    {
        public Acrux.Core.VisualElement Content
        {
            get { return Child; }
            set { Child = value; }
        }
    }
}
