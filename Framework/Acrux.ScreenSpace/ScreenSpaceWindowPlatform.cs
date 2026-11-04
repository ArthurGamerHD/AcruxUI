using System;
using System.Collections.Generic;
using Acrux.Controls;
using Acrux.Core;
using Acrux.Core.Input;
using Acrux.Core.Invalidation;
using Sandbox.ModAPI;
using VRageMath;

namespace Acrux.ScreenSpace
{
    /// <summary>Screen-space WindowImpl factory.</summary>
    public sealed class ScreenSpaceWindowPlatform : IWindowPlatform, global::Acrux.Toolkit.IExternalPeerHost
    {
        object _externalPeer;
        object global::Acrux.Toolkit.IExternalPeerHost.ExternalPeer { get { return _externalPeer; } set { _externalPeer = value; } }

        public static readonly ScreenSpaceWindowPlatform Instance =
            new ScreenSpaceWindowPlatform();

        readonly MyScreenSpaceLayer _layer;
        readonly IInputSource _input;
        public Func<float> RenderScalingProvider { get; set; }

        /// <param name="layer">The screen-space layer the platform's windows draw on.</param>
        /// <param name="input">
        /// Where the platform's windows read pointer, button and key input from. Null reads the game's mouse and
        /// keyboard; a scripted source drives the windows without the player's input (as Avalonia.Headless does).
        /// </param>
        public ScreenSpaceWindowPlatform(MyScreenSpaceLayer layer = MyScreenSpaceLayer.AboveHud, IInputSource input = null)
        {
            _layer = layer;
            _input = input;
        }

        public IWindowImpl CreateWindowImpl(Window window)
        {
            return new ScreenSpaceWindowImpl(window, _layer, _input)
            {
                RenderScalingProvider = () => RenderScalingProvider == null ? 1f : RenderScalingProvider()
            };
        }
    }

    internal sealed class ScreenSpaceWindowImpl : IWindowImpl
    {
        readonly Window _window;
        readonly IInputSource _input;
        readonly VisualTree _tree;
        readonly List<Key> _openKeys = new List<Key>();
        RectangleF _bounds;
        bool _hasBounds;
        bool _disposed;
        float _renderScaling = 1f;
        internal Func<float> RenderScalingProvider { get; set; }

        public ScreenSpaceWindowImpl(Window window, MyScreenSpaceLayer layer, IInputSource input = null)
        {
            if (window == null)
                throw new ArgumentNullException(nameof(window));
            _window = window;
            var renderer = new ScreenSpaceRenderer(layer);
            renderer.OwnerRoot = window;
            _input = input ?? new ScreenSpaceInputSource(MyAPIGateway.Input);
            _tree = Acrux.Gui.PrepareRender(renderer).CreateVisualTree(
                window,
                GuiAnimationSessionComponent.Animations);
            ScreenSpaceWindowManager.Register(this);
        }

        public bool IsOpen { get; private set; }

        public void Open()
        {
            ThrowIfDisposed();
            IsOpen = true;
            _hasBounds = false;
            PublishGameOpacities();
        }

        // The game's Options > Interface opacities as platform resources (uiOpacity for controls and text,
        // uiBackgroundOpacity for window backgrounds, as MyGuiScreenBase draws them). Read once per opening, never per
        // frame: the game parses its config text on every read. A change in Options applies from the next opening.
        void PublishGameOpacities()
        {
            GameUiOpacity.Publish(_window);
        }

        public void Close()
        {
            ThrowIfDisposed();
            IsOpen = false;
            _tree.ResetInput();
        }

        public void BindOpen(Key key)
        {
            ThrowIfDisposed();
            if (key != Key.None && !_openKeys.Contains(key))
                _openKeys.Add(key);
        }

        public bool UnbindOpen(Key key)
        {
            ThrowIfDisposed();
            return _openKeys.Remove(key);
        }

        internal void Update()
        {
            if (_disposed)
                return;

            for (int i = 0; i < _openKeys.Count; i++)
            {
                if (_input.IsNewKeyPressed(_openKeys[i]))
                {
                    _window.Open();
                    break;
                }
            }

            if (!IsOpen)
                return;
            if (_window.ActiveModalWindow == null && _window.CloseOnEscape && _input.IsNewKeyPressed(Key.Escape))
            {
                _window.Close();
                return;
            }
            EnsureLayout();
            _tree.HandleInput(_input);
        }

        internal void Draw()
        {
            if (_disposed || !IsOpen)
                return;

            EnsureLayout();
            _tree.Draw(1f);
        }

        void EnsureLayout()
        {
            // As in Avalonia: FullScreen covers the whole screen, every other state the work area, which in game is the
            // safe GUI rectangle (4:3 at 1080p, so a full-screen view would otherwise come out boxed in).
            RectangleF bounds = _window.WindowState == WindowState.FullScreen
                ? GetFullScreenBoundsPixels()
                : GetSafeGuiBoundsPixels();
            float scaling = RenderScalingProvider == null ? 1f : RenderScalingProvider();
            UiInvalidation invalidation = _window.PendingInvalidation;
            if (!_hasBounds || _renderScaling != scaling || !NearlyEqual(_bounds, bounds) ||
                (invalidation & (UiInvalidation.Measure | UiInvalidation.Arrange)) != 0)
            {
                _bounds = bounds;
                _hasBounds = true;
                _tree.ArrangeViewport(bounds, scaling);
                _renderScaling = scaling;
            }
        }

        // The whole game viewport in window pixels; the safe GUI rectangle when there is no camera yet.
        static RectangleF GetFullScreenBoundsPixels()
        {
            var camera = MyAPIGateway.Session != null ? MyAPIGateway.Session.Camera : null;
            if (camera == null || camera.ViewportSize.X <= 0f || camera.ViewportSize.Y <= 0f)
                return GetSafeGuiBoundsPixels();
            return new RectangleF(Vector2.Zero, camera.ViewportSize);
        }

        static RectangleF GetSafeGuiBoundsPixels()
        {
            // Already in window pixels: the area normalized [0,1] maps onto.
            return MyScreenSpace.GetSafeGuiArea();
        }

        static bool NearlyEqual(RectangleF a, RectangleF b)
        {
            const float epsilon = 0.5f;
            return Math.Abs(a.X - b.X) <= epsilon &&
                   Math.Abs(a.Y - b.Y) <= epsilon &&
                   Math.Abs(a.Width - b.Width) <= epsilon &&
                   Math.Abs(a.Height - b.Height) <= epsilon;
        }

        void ThrowIfDisposed()
        {
            if (_disposed)
                throw new InvalidOperationException("The screen-space WindowImpl has been disposed.");
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            IsOpen = false;
            _tree.ResetInput();
            ScreenSpaceWindowManager.Unregister(this);
        }
    }

    internal static class ScreenSpaceWindowManager
    {
        static readonly List<ScreenSpaceWindowImpl> Windows =
            new List<ScreenSpaceWindowImpl>();
        static readonly List<ScreenSpaceWindowImpl> Snapshot =
            new List<ScreenSpaceWindowImpl>();

        public static void Register(ScreenSpaceWindowImpl window)
        {
            if (window != null && !Windows.Contains(window))
                Windows.Add(window);
        }

        public static void Unregister(ScreenSpaceWindowImpl window) { Windows.Remove(window); }

        public static void Update()
        {
            Snapshot.Clear();
            Snapshot.AddRange(Windows);
            for (int i = 0; i < Snapshot.Count; i++) Snapshot[i].Update();
            Snapshot.Clear();
        }

        public static void Draw()
        {
            Snapshot.Clear();
            Snapshot.AddRange(Windows);
            for (int i = 0; i < Snapshot.Count; i++) Snapshot[i].Draw();
            Snapshot.Clear();
        }

        public static void Clear()
        {
            Windows.Clear();
            Snapshot.Clear();
        }
    }
}
