using System;
using System.Collections.Generic;
using Acrux.Animation;
using Acrux.Controls;
using Acrux.Core.Input;
using Sandbox.ModAPI;
using VRage.Game.GUI.TextPanel;
using VRageMath;
using IMyTextSurface = Sandbox.ModAPI.Ingame.IMyTextSurface;

namespace Acrux.Lcd
{
    /// <summary>
    /// LCD WindowImpl factory. Assign an instance to Window.Platform to open the
    /// exact same Window/XAML on a text surface instead of in screen space.
    /// </summary>
    public sealed class LcdWindowPlatform : IWindowPlatform, global::Acrux.Toolkit.IExternalPeerHost
    {
        object _externalPeer;
        object global::Acrux.Toolkit.IExternalPeerHost.ExternalPeer { get { return _externalPeer; } set { _externalPeer = value; } }

        readonly IMyTextSurface _surface;
        readonly Func<RectangleF> _boundsProvider;
        readonly AnimationController _animations;
        readonly bool _ownsSurfaceFrame;
        readonly Dictionary<Window, LcdWindowImpl> _implementations =
            new Dictionary<Window, LcdWindowImpl>();

        public Func<float> RenderScalingProvider { get; set; }

        public LcdWindowPlatform(
            IMyTextSurface surface,
            Func<RectangleF> boundsProvider = null,
            AnimationController animations = null,
            bool ownsSurfaceFrame = true)
        {
            if (surface == null)
                throw new ArgumentNullException(nameof(surface));
            _surface = surface;
            _boundsProvider = boundsProvider;
            _animations = animations;
            _ownsSurfaceFrame = ownsSurfaceFrame;
        }

        public IWindowImpl CreateWindowImpl(Window window)
        {
            var implementation = new LcdWindowImpl(
                window,
                _surface,
                _boundsProvider,
                _animations,
                _ownsSurfaceFrame,
                () => _implementations.Remove(window));
            _implementations[window] = implementation;
            implementation.Host.RenderScalingProvider = () =>
                RenderScalingProvider == null ? 1f : RenderScalingProvider();
            return implementation;
        }

        public LcdInputSource GetInput(Window window)
        {
            LcdWindowImpl implementation;
            return window != null && _implementations.TryGetValue(window, out implementation)
                ? implementation.Input
                : null;
        }

        public LcdGuiHost GetGuiHost(Window window)
        {
            LcdWindowImpl implementation;
            return window != null && _implementations.TryGetValue(window, out implementation)
                ? implementation.Host
                : null;
        }

        public void Draw(Window window, RectangleF bounds, List<MySprite> sprites)
        {
            LcdWindowImpl implementation;
            if (window == null || !_implementations.TryGetValue(window, out implementation))
                return;
            implementation.Draw(bounds, sprites);
        }
    }

    internal sealed class LcdWindowImpl : IWindowImpl
    {
        readonly Window _window;
        readonly IMyTextSurface _surface;
        readonly Func<RectangleF> _boundsProvider;
        readonly Action _disposedCallback;
        readonly LcdGuiHost _host;
        readonly List<Key> _openKeys = new List<Key>();
        readonly List<MySprite> _sprites = new List<MySprite>();
        RectangleF _lastDrawBounds;
        bool _hasDrawBounds;
        bool _clearSurface;
        bool _presented;
        bool _disposed;

        public LcdWindowImpl(
            Window window,
            IMyTextSurface surface,
            Func<RectangleF> boundsProvider,
            AnimationController animations,
            bool ownsSurfaceFrame,
            Action disposedCallback)
        {
            if (window == null)
                throw new ArgumentNullException(nameof(window));
            if (surface == null)
                throw new ArgumentNullException(nameof(surface));
            _window = window;
            _surface = surface;
            _boundsProvider = boundsProvider;
            _disposedCallback = disposedCallback;
            _host = new LcdGuiHost(surface, window, animations, "MissingIcon", false);
            OwnsSurfaceFrame = ownsSurfaceFrame;
            if (OwnsSurfaceFrame)
                LcdWindowManager.Register(this);
        }

        public bool IsOpen { get; private set; }
        public LcdInputSource Input => _host.Input;
        public LcdGuiHost Host => _host;
        public bool OwnsSurfaceFrame { get; private set; }

        public void Open()
        {
            ThrowIfDisposed();
            IsOpen = true;
            _clearSurface = false;
            _presented = false;
        }

        public void Close()
        {
            ThrowIfDisposed();
            IsOpen = false;
            _host.ClearPointer();
            _clearSurface = true;
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
                if (_host.Input.IsNewKeyPressed(_openKeys[i]))
                {
                    _window.Open();
                    break;
                }
            }
            if (IsOpen && _window.ActiveModalWindow == null && _window.CloseOnEscape && _host.Input.IsNewKeyPressed(Key.Escape))
                _window.Close();
            else if (IsOpen)
                _host.UpdateKeyboardInput();
        }

        internal void Draw()
        {
            if (_disposed || (!IsOpen && !_clearSurface))
                return;

            _sprites.Clear();
            if (IsOpen)
            {
                RectangleF bounds = _boundsProvider != null ? _boundsProvider() : GetDefaultBounds(_surface);
                if (!_clearSurface && _presented && _hasDrawBounds && BoundsEqual(_lastDrawBounds, bounds) &&
                    !_host.NeedsRender)
                    return;
                Draw(bounds, _sprites);
            }

            using (MySpriteDrawFrame frame = _surface.DrawFrame())
            {
                for (int i = 0; i < _sprites.Count; i++)
                    frame.Add(_sprites[i]);
            }
            _clearSurface = false;
            _presented = IsOpen;
        }

        internal void Draw(RectangleF bounds, List<MySprite> sprites)
        {
            if (_disposed || !IsOpen || sprites == null)
                return;
            if (!_hasDrawBounds || !BoundsEqual(_lastDrawBounds, bounds))
            {
                _lastDrawBounds = bounds;
                _hasDrawBounds = true;
                _host.Arrange(bounds);
            }
            _host.Draw(sprites);
        }

        static bool BoundsEqual(RectangleF left, RectangleF right)
        {
            return Math.Abs(left.X - right.X) < 0.001f &&
                   Math.Abs(left.Y - right.Y) < 0.001f &&
                   Math.Abs(left.Width - right.Width) < 0.001f &&
                   Math.Abs(left.Height - right.Height) < 0.001f;
        }

        static RectangleF GetDefaultBounds(IMyTextSurface surface)
        {
            Vector2 offset = (surface.TextureSize - surface.SurfaceSize) / 2f;
            Vector2 padding = surface.SurfaceSize * (surface.TextPadding / 100f);
            offset += padding / 2f;
            return new RectangleF(offset, surface.SurfaceSize - padding);
        }

        void ThrowIfDisposed()
        {
            if (_disposed)
                throw new InvalidOperationException("The LCD WindowImpl has been disposed.");
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            IsOpen = false;
            if (OwnsSurfaceFrame)
                LcdWindowManager.Unregister(this);
            _host.Dispose();
            if (_disposedCallback != null)
                _disposedCallback();
        }
    }

    internal static class LcdWindowManager
    {
        static readonly List<LcdWindowImpl> Windows = new List<LcdWindowImpl>();
        static readonly List<LcdWindowImpl> Snapshot = new List<LcdWindowImpl>();

        public static void Register(LcdWindowImpl window)
        {
            if (window != null && !Windows.Contains(window))
                Windows.Add(window);
        }

        public static void Unregister(LcdWindowImpl window) { Windows.Remove(window); }

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
