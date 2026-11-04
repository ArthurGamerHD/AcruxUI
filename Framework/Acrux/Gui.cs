using System;
using Acrux.Animation;
using Acrux.Rendering;

namespace Acrux
{
    /// <summary>
    /// Configures the renderer-neutral GUI for the current host.
    /// </summary>
    public sealed class Gui
    {
        readonly IUiRenderer _renderer;

        Gui(IUiRenderer renderer)
        {
            _renderer = renderer;
        }

        public IUiRenderer Renderer => _renderer;

        public static Gui PrepareRender(IUiRenderer renderer)
        {
            if (renderer == null)
                throw new ArgumentNullException(nameof(renderer));

            return new Gui(renderer);
        }

        public Core.VisualTree CreateVisualTree(Core.VisualElement root)
        {
            PublishWindowResources(root);
            return new Core.VisualTree(root, _renderer);
        }

        public Core.VisualTree CreateVisualTree(
            Core.VisualElement root,
            AnimationController animations)
        {
            PublishWindowResources(root);
            return new Core.VisualTree(root, _renderer, animations);
        }

        // A Window drawn in the host's own tree gets what its platform would give it (the game's UI opacities on screen).
        void PublishWindowResources(Core.VisualElement root)
        {
            var window = root as Controls.Window;
            var source = _renderer as Rendering.IWindowResourceSource;
            if (window != null && source != null)
                source.PublishWindowResources(window);
        }
    }
}
