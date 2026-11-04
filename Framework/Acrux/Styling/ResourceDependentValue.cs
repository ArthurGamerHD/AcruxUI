using Acrux.Core;

namespace Acrux.Styling
{
    /// <summary>
    /// A style setter value with a part that is a dynamic resource of the styled control, such as
    /// <c>&lt;ImageBrush Opacity="{DynamicResource uiBackgroundOpacity}" /&gt;</c>: resolved for each control, and again
    /// when that resource changes, as a setter's own {DynamicResource} is.
    /// </summary>
    internal interface IResourceDependentValue
    {
        string ResourceKey { get; }
        object Resolve(Control control);
    }

    /// <summary>An ImageBrush whose Opacity is a dynamic resource (1 when the resource is absent).</summary>
    internal sealed class ImageBrushOpacityResource : IResourceDependentValue
    {
        readonly ImageBrush _brush;
        ImageBrush _resolved;

        public ImageBrushOpacityResource(ImageBrush brush, string opacityKey)
        {
            _brush = brush;
            ResourceKey = opacityKey;
        }

        public string ResourceKey { get; }

        public object Resolve(Control control)
        {
            float opacity = control.ResolveDynamicResource(ResourceKey, 1f);
            // Read on every property read: reuse the brush while the opacity holds.
            ImageBrush resolved = _resolved;
            if (resolved == null || !resolved.Opacity.Equals(opacity))
                _resolved = resolved = _brush.WithOpacity(opacity);
            return resolved;
        }

        public override string ToString()
        {
            return "ImageBrush " + _brush.Source + " (Opacity {DynamicResource " + ResourceKey + "})";
        }
    }
}
