using Acrux.Controls.Primitives;
using Acrux.Styling;
using VRageMath;

namespace Acrux.Controls
{
    /// <summary>A toggle button with checkbox semantics and style identity.</summary>
    public sealed class CheckBox : ToggleButton
    {
        static CheckBox()
        {
            StylePropertyRegistry.Global.RegisterType<CheckBox>(nameof(CheckBox));
        }

        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            Vector2 content = base.MeasureOverride(availableSize);
            // measures the indicator and spacing as part of its template.
            return new Vector2(content.X + 28f, System.Math.Max(28f, content.Y));
        }
    }
}
