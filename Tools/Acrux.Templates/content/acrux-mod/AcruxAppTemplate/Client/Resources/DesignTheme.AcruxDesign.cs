using System;
using Acrux.Styling;
using VRageMath;

namespace AcruxAppTemplate.Client.Resources
{
    /// <summary>
    /// The theme previewed views get in Rider: a <see cref="SurfaceTheme"/> whose colours and font come from the
    /// preview toolbar instead of a block. Named by <c>AcruxPreviewDesignResources</c> in the project file; the
    /// preview creates it and sets Background / Foreground / FontId. A *.AcruxDesign.cs file: MDK does not pack it.
    /// </summary>
    public sealed class DesignTheme : IResourceProvider
    {
        static readonly Color DefaultBackground = new Color(0, 88, 151);
        static readonly Color DefaultForeground = new Color(179, 237, 255);

        readonly SurfaceTheme _theme = new SurfaceTheme(DefaultBackground, DefaultForeground);
        Color _background = DefaultBackground;
        Color _foreground = DefaultForeground;

        public event Action<string> ResourceChanged
        {
            add { _theme.ResourceChanged += value; }
            remove { _theme.ResourceChanged -= value; }
        }

        /// <summary>The surface's background (the block's script background colour).</summary>
        public Color Background
        {
            get { return _background; }
            set { _background = value; _theme.SetColors(_background, _foreground); }
        }

        /// <summary>The surface's text colour (the block's script foreground colour).</summary>
        public Color Foreground
        {
            get { return _foreground; }
            set { _foreground = value; _theme.SetColors(_background, _foreground); }
        }

        public string FontId
        {
            get { return _theme.Font; }
            set { _theme.Font = string.IsNullOrEmpty(value) ? "White" : value; }
        }

        public bool TryGetResource<T>(string key, out T value)
        {
            return _theme.TryGetResource(key, out value);
        }
    }
}
