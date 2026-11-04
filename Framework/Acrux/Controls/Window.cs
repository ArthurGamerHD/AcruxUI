using System;
using System.Collections.Generic;
using Acrux.Core;
using Acrux.Core.Invalidation;
using Acrux.Styling;
using Acrux.Core.Input;
using Acrux.Core.PropertySystem;
using Acrux.Controls.Chrome;
using Acrux.Rendering;
using VRageMath;

namespace Acrux.Controls
{
    /// <summary>WindowState.</summary>
    public enum WindowState
    {
        Normal,
        Minimized,
        Maximized,
        FullScreen
    }

    /// <summary>WindowDecorations.</summary>
    public enum WindowDecorations
    {
        None,
        BorderOnly,
        Full
    }

    /// <summary>SizeToContent.</summary>
    public enum SizeToContent
    {
        Manual,
        Width,
        Height,
        WidthAndHeight
    }

    /// <summary>
    /// Renderer-independent Acrux top level. The same Window/XAML can be opened by
    /// any backend by assigning <see cref="Platform"/> before calling Open.
    /// </summary>
    public class Window : UserControl
    {
        const float DefaultTitleBarHeight = 32f;
        const float FrameMargin = 12f;
        const float CaptionSize = 22f;
        const float CaptionSpacing = 6f;
        static readonly Color ModalOverlay = new Color(0, 0, 0, 144);

        public static readonly StyledProperty<string> TitleProperty =
            StyledProperty.Register<Window, string>(nameof(Title), string.Empty, UiInvalidation.Arrange);
        public static readonly StyledProperty<WindowState> WindowStateProperty =
            StyledProperty.Register<Window, WindowState>(nameof(WindowState), WindowState.Normal, UiInvalidation.Measure);
        public static readonly StyledProperty<WindowDecorations> WindowDecorationsProperty =
            StyledProperty.Register<Window, WindowDecorations>(nameof(WindowDecorations), WindowDecorations.Full, UiInvalidation.Measure);
        public static readonly StyledProperty<bool> ExtendClientAreaToDecorationsHintProperty =
            StyledProperty.Register<Window, bool>(nameof(ExtendClientAreaToDecorationsHint), false, UiInvalidation.Measure);
        public static readonly StyledProperty<float> ExtendClientAreaTitleBarHeightHintProperty =
            StyledProperty.Register<Window, float>(nameof(ExtendClientAreaTitleBarHeightHint), -1f, UiInvalidation.Measure);
        public static readonly StyledProperty<bool> CanMaximizeProperty =
            StyledProperty.Register<Window, bool>(nameof(CanMaximize), true, UiInvalidation.Measure);
        public static readonly StyledProperty<bool> CanMinimizeProperty =
            StyledProperty.Register<Window, bool>(nameof(CanMinimize), true, UiInvalidation.Measure);
        public static readonly StyledProperty<SizeToContent> SizeToContentProperty =
            StyledProperty.Register<Window, SizeToContent>(nameof(SizeToContent), SizeToContent.Manual, UiInvalidation.Measure);
        public static readonly StyledProperty<BackgroundBrush> BackgroundProperty =
            StyledProperty.Register<Window, BackgroundBrush>(nameof(Background), null, UiInvalidation.Arrange);
        public static readonly StyledProperty<Color> BorderBrushProperty =
            StyledProperty.Register<Window, Color>(nameof(BorderBrush), Color.Transparent, UiInvalidation.Arrange);
        public static readonly StyledProperty<Vector4> BorderThicknessProperty =
            StyledProperty.Register<Window, Vector4>(nameof(BorderThickness), Vector4.Zero, UiInvalidation.Arrange);
        public static readonly StyledProperty<Vector4> CornerRadiusProperty =
            StyledProperty.Register<Window, Vector4>(nameof(CornerRadius), Vector4.Zero, UiInvalidation.Arrange);

        readonly Border _frame = new Border { StyleClass = "window-frame" };
        readonly TextBlock _title = new TextBlock { StyleClass = "window-title" };
        // Shrinks a title too long for the caption bar instead of letting it run under the caption buttons.
        readonly Acrux.Layout.Viewbox _titleBox = new Acrux.Layout.Viewbox
        {
            StretchDirection = StretchDirection.DownOnly
        };
        readonly CaptionButton _minimize = new CaptionButton(CaptionGlyph.Minimize, "minimize") { Enabled = false };
        readonly CaptionButton _maximize = new CaptionButton(CaptionGlyph.Maximize, "maximize");
        readonly CaptionButton _close = new CaptionButton(CaptionGlyph.Close, "close");
        RectangleF _frameBounds;
        bool _modal;
        RectangleF _clientBounds;
        readonly List<Key> _openKeys = new List<Key>();
        IWindowPlatform _platform;
        IWindowImpl _implementation;
        bool _disposed;
        readonly List<Window> _ownedWindows = new List<Window>();
        IReadOnlyList<Window> _readOnlyOwnedWindows;
        StyleSheet _dialogStyles;

        ResourceDictionary _platformResources;

        // Sets a resource supplied by the window's platform, the lowest-priority tier of resource lookup (below the
        // window's own resources, its data context and its styles), and refreshes dynamic references to it.
        internal void SetPlatformResource(string key, object value)
        {
            if (_platformResources == null)
                _platformResources = new ResourceDictionary();
            object current;
            if (_platformResources.TryGetValue(key, out current) && Equals(current, value))
                return;
            _platformResources.Add(key, value);
            OnResourceChanged(key);
        }

        internal bool TryGetPlatformResource<T>(string key, out T value)
        {
            if (_platformResources != null)
                return _platformResources.TryGetValue(key, out value);
            value = default(T);
            return false;
        }

        public Window()
        {
            AddChild(_frame);
            _titleBox.Child = _title;
            AddChild(_titleBox);
            AddChild(_minimize);
            AddChild(_maximize);
            AddChild(_close);
            _close.Clicked = Close;
            _maximize.Clicked = () =>
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        public string Title
        {
            get { return GetValue(TitleProperty); }
            set { SetValue(TitleProperty, value ?? string.Empty); }
        }

        public WindowState WindowState
        {
            get { return GetValue(WindowStateProperty); }
            set { SetValue(WindowStateProperty, value); }
        }

        public WindowDecorations WindowDecorations
        {
            get { return GetValue(WindowDecorationsProperty); }
            set { SetValue(WindowDecorationsProperty, value); }
        }

        public bool ExtendClientAreaToDecorationsHint
        {
            get { return GetValue(ExtendClientAreaToDecorationsHintProperty); }
            set { SetValue(ExtendClientAreaToDecorationsHintProperty, value); }
        }

        public float ExtendClientAreaTitleBarHeightHint
        {
            get { return GetValue(ExtendClientAreaTitleBarHeightHintProperty); }
            set { SetValue(ExtendClientAreaTitleBarHeightHintProperty, value); }
        }

        public bool CanMaximize
        {
            get { return GetValue(CanMaximizeProperty); }
            set { SetValue(CanMaximizeProperty, value); }
        }

        public bool CanMinimize
        {
            get { return GetValue(CanMinimizeProperty); }
            set { SetValue(CanMinimizeProperty, value); }
        }

        public SizeToContent SizeToContent
        {
            get { return GetValue(SizeToContentProperty); }
            set { SetValue(SizeToContentProperty, value); }
        }

        public BackgroundBrush Background
        {
            get { return GetValue(BackgroundProperty); }
            set { SetValue(BackgroundProperty, value); }
        }

        public Color BorderBrush
        {
            get { return GetValue(BorderBrushProperty); }
            set { SetValue(BorderBrushProperty, value); }
        }

        public Vector4 BorderThickness
        {
            get { return GetValue(BorderThicknessProperty); }
            set { SetValue(BorderThicknessProperty, value); }
        }

        public Vector4 CornerRadius
        {
            get { return GetValue(CornerRadiusProperty); }
            set { SetValue(CornerRadiusProperty, value); }
        }

        /// <summary>Where the window's frame sits inside its viewport; the whole viewport when full screen.</summary>
        public RectangleF FrameBounds => _frameBounds;

        bool IsFramed => WindowState != WindowState.FullScreen && WindowDecorations != WindowDecorations.None;
        bool HasTitleBar => WindowState != WindowState.FullScreen && WindowDecorations == WindowDecorations.Full;
        float TitleBarHeight => ExtendClientAreaTitleBarHeightHint > 0f ? ExtendClientAreaTitleBarHeightHint : DefaultTitleBarHeight;

        protected override bool IsAuxiliaryChild(VisualElement child)
        {
            return ReferenceEquals(child, _frame) || ReferenceEquals(child, _titleBox) || child is CaptionButton;
        }

        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            return availableSize;
        }

        protected override void ArrangeChildren(RectangleF bounds)
        {
            KeepChromeAroundContent();
            bool fullScreen = WindowState == WindowState.FullScreen;
            bool framed = IsFramed;
            bool titleBar = HasTitleBar;
            bool extended = ExtendClientAreaToDecorationsHint;
            float barHeight = titleBar && !extended ? TitleBarHeight : 0f;
            VisualElement content = Child;

            _frameBounds = fullScreen || WindowState == WindowState.Maximized
                ? bounds
                : NormalFrame(bounds, content, barHeight);
            _clientBounds = new RectangleF(_frameBounds.X, _frameBounds.Y + barHeight, _frameBounds.Width,
                Math.Max(0f, _frameBounds.Height - barHeight));
            if (content != null && content.Visible)
            {
                Vector4 padding = Padding;
                var slot = new RectangleF(_clientBounds.X + padding.X, _clientBounds.Y + padding.Y,
                    Math.Max(0f, _clientBounds.Width - padding.X - padding.Z),
                    Math.Max(0f, _clientBounds.Height - padding.Y - padding.W));
                content.Measure(slot.Size);
                content.ArrangeInSlot(slot);
            }

            _frame.Visible = framed;
            if (framed)
            {
                _frame.Background = Background;
                _frame.BorderBrush = BorderBrush;
                _frame.BorderThickness = BorderThickness;
                _frame.CornerRadius = WindowState == WindowState.Maximized ? Vector4.Zero : CornerRadius;
                _frame.Measure(_frameBounds.Size);
                _frame.Arrange(_frameBounds);
            }

            _titleBox.Visible = titleBar && !extended;
            _minimize.Visible = titleBar && CanMinimize;
            _maximize.Visible = titleBar && CanMaximize;
            _close.Visible = titleBar;
            _maximize.Glyph = WindowState == WindowState.Maximized ? CaptionGlyph.Restore : CaptionGlyph.Maximize;
            if (!titleBar)
                return;

            float bar = TitleBarHeight;
            float inset = (bar - CaptionSize) * .5f;
            bool onLeft = WindowPlatforms.CaptionButtons == CaptionButtonsPlacement.Left;
            float offset = inset + 4f;
            float top = _frameBounds.Y + inset;
            foreach (var button in new VisualElement[] { _close, _maximize, _minimize })
            {
                if (!button.Visible)
                    continue;
                float x = onLeft ? _frameBounds.X + offset : _frameBounds.Right - offset - CaptionSize;
                button.Measure(new Vector2(CaptionSize, CaptionSize));
                button.Arrange(new RectangleF(x, top, CaptionSize, CaptionSize));
                offset += CaptionSize + CaptionSpacing;
            }
            if (_titleBox.Visible)
            {
                _title.Text = Title;
                float side = offset;
                var slot = new RectangleF(_frameBounds.X + side, _frameBounds.Y, Math.Max(0f, _frameBounds.Width - side * 2f), bar);
                _titleBox.Measure(slot.Size);
                Vector2 size = Vector2.Min(slot.Size, _titleBox.DesiredSize);
                _titleBox.Arrange(new RectangleF(slot.Center - size * .5f, size));
            }
        }

        RectangleF NormalFrame(RectangleF bounds, VisualElement content, float barHeight)
        {
            float inset = Math.Min(FrameMargin, Math.Min(bounds.Width, bounds.Height) * .05f);
            float roomWidth = Math.Max(1f, bounds.Width - inset * 2f);
            float roomHeight = Math.Max(1f, bounds.Height - inset * 2f);
            float width = Fit(Width ?? roomWidth, MinWidth, MaxWidth, roomWidth);
            float height = Fit(Height ?? roomHeight, MinHeight, MaxHeight, roomHeight);
            SizeToContent sizing = SizeToContent;
            if (content != null && sizing != SizeToContent.Manual)
            {
                Vector4 padding = Padding;
                Vector2 desired = content.Measure(new Vector2(
                    sizing == SizeToContent.Height ? width - padding.X - padding.Z : roomWidth,
                    roomHeight - barHeight));
                if (sizing != SizeToContent.Height)
                    width = Fit(desired.X + padding.X + padding.Z, MinWidth, MaxWidth, roomWidth);
                if (sizing != SizeToContent.Width)
                    height = Fit(desired.Y + padding.Y + padding.W + barHeight, MinHeight, MaxHeight, roomHeight);
            }
            Vector4 margin = Margin;
            float x = HorizontalAlignment == HorizontalAlignment.Left ? bounds.X + margin.X
                : HorizontalAlignment == HorizontalAlignment.Right ? bounds.Right - margin.Z - width
                : bounds.Center.X - width * .5f;
            float y = VerticalAlignment == VerticalAlignment.Top ? bounds.Y + margin.Y
                : VerticalAlignment == VerticalAlignment.Bottom ? bounds.Bottom - margin.W - height
                : bounds.Center.Y - height * .5f;
            return new RectangleF(x, y, width, height);
        }

        static float Fit(float preferred, float minimum, float maximum, float room)
        {
            return Math.Min(room, Math.Min(maximum, Math.Max(minimum, preferred)));
        }

        void KeepChromeAroundContent()
        {
            MoveChild(_frame, 0);
            int last = VisualChildren.Count - 1;
            MoveChild(_close, last);
            MoveChild(_maximize, last);
            MoveChild(_minimize, last);
            MoveChild(_titleBox, last);
        }

        protected override void DrawSelf(RenderContext context)
        {
            if (Owner != null && _modal && WindowState != WindowState.FullScreen)
                context.Fill(Bounds, ModalOverlay);
        }

        protected override IDisposable BeginChildDraw(RenderContext context, VisualElement child)
        {
            return ReferenceEquals(child, Child) && WindowState != WindowState.FullScreen
                ? context.Clip(_clientBounds)
                : base.BeginChildDraw(context, child);
        }

        public Window Owner { get; private set; }
        public IReadOnlyList<Window> OwnedWindows => _readOnlyOwnedWindows ?? (_readOnlyOwnedWindows = _ownedWindows.AsReadOnly());
        protected override Control ResourceParent => Owner ?? base.ResourceParent;
        protected override IUiInvalidatable InvalidationParent => Owner ?? base.InvalidationParent;
        internal Window ActiveModalWindow
        {
            get
            {
                for (int i = _ownedWindows.Count - 1; i >= 0; i--)
                    if (_ownedWindows[i]._modal)
                        return _ownedWindows[i];
                return null;
            }
        }

        internal bool IsModal => _modal;

        protected override void OnResourceChanged(string key)
        {
            base.OnResourceChanged(key);
            foreach (var dialog in _ownedWindows) dialog.OnResourceChanged(key);
        }

        /// <summary>Show(owner): an owned window drawn above its owner that does not block the owner's input.</summary>
        public void Show(Window owner)
        {
            OpenOwned(owner, false);
        }

        internal void OpenModal(Window owner)
        {
            OpenOwned(owner, true);
        }

        void OpenOwned(Window owner, bool modal)
        {
            ThrowIfDisposed();
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            owner.ThrowIfDisposed();
            if (ReferenceEquals(owner, this) || IsOpen || !owner.IsOpen)
                throw new InvalidOperationException("A dialog requires an open owner and cannot already be open.");
            _dialogStyles = Styles;
            Styles = StyleSheet.Cascade(owner.ResolveStyleSheet(), Styles);
            Owner = owner;
            _modal = modal;
            owner._ownedWindows.Add(this);
            owner.Invalidate(UiInvalidation.Measure | UiInvalidation.Render);
            Action handler = Opened;
            if (handler != null) handler();
        }

        public IWindowPlatform Platform
        {
            get { return _platform; }
            set
            {
                ThrowIfDisposed();
                if (ReferenceEquals(_platform, value))
                    return;
                if (IsOpen)
                    throw new InvalidOperationException("A Window platform cannot change while the window is open.");
                DisposeImplementation();
                _platform = value;
                if (_openKeys.Count > 0)
                    EnsureImplementation();
            }
        }

        public bool IsOpen
        {
            get { return Owner != null || _implementation != null && _implementation.IsOpen; }
        }

        /// <summary>Physical pixels per logical unit, supplied by the window backend.</summary>
        public double RenderScaling { get; private set; } = 1d;

        internal void SetRenderScaling(float scaling)
        {
            RenderScaling = scaling;
        }
        public bool CloseOnEscape { get; set; } = true;

        public event Action Opened;
        public event Action Closed;

        /// <summary>
        /// Binds a platform-neutral key to Open(). The active WindowImpl owns the
        /// native input subscription.
        /// </summary>
        public Window BindOpen(Key key)
        {
            ThrowIfDisposed();
            if (key == Key.None || _openKeys.Contains(key))
                return this;
            _openKeys.Add(key);
            EnsureImplementation().BindOpen(key);
            return this;
        }

        public bool UnbindOpen(Key key)
        {
            ThrowIfDisposed();
            if (!_openKeys.Remove(key))
                return false;
            if (_implementation != null)
                _implementation.UnbindOpen(key);
            return true;
        }

        public void Open()
        {
            ThrowIfDisposed();
            if (Owner != null) return;
            IWindowImpl implementation = EnsureImplementation();
            if (implementation.IsOpen)
                return;
            implementation.Open();
            Action handler = Opened;
            if (handler != null)
                handler();
        }

        public void Close()
        {
            ThrowIfDisposed();
            if (!IsOpen)
                return;
            while (_ownedWindows.Count > 0)
                _ownedWindows[_ownedWindows.Count - 1].Close();
            if (Owner != null)
            {
                var owner = Owner;
                owner._ownedWindows.Remove(this);
                Owner = null;
                Styles = _dialogStyles;
                _dialogStyles = null;
                owner.Invalidate(UiInvalidation.Measure | UiInvalidation.Render);
            }
            else
                _implementation.Close();
            Action handler = Closed;
            if (handler != null) handler();
        }

        IWindowImpl EnsureImplementation()
        {
            if (_implementation != null)
                return _implementation;

            IWindowPlatform platform = _platform ?? WindowPlatforms.Default;
            if (platform == null)
                throw new InvalidOperationException(
                    "Window.Platform is not set and no default Acrux window platform is registered.");

            _implementation = platform.CreateWindowImpl(this);
            if (_implementation == null)
                throw new InvalidOperationException("The Acrux window platform returned no WindowImpl.");
            for (int i = 0; i < _openKeys.Count; i++)
                _implementation.BindOpen(_openKeys[i]);
            return _implementation;
        }

        void DisposeImplementation()
        {
            if (_implementation == null)
                return;
            _implementation.Dispose();
            _implementation = null;
        }

        void ThrowIfDisposed()
        {
            if (_disposed)
                throw new InvalidOperationException("The Window has been disposed.");
        }

        public override void Dispose()
        {
            if (_disposed)
                return;
            if (IsOpen)
                Close();
            _disposed = true;
            DisposeImplementation();
            Opened = null;
            Closed = null;
            base.Dispose();
        }
    }

    public interface IWindowPlatform
    {
        IWindowImpl CreateWindowImpl(Window window);
    }

    public interface IWindowImpl : IDisposable
    {
        bool IsOpen { get; }
        void Open();
        void Close();
        void BindOpen(Key key);
        bool UnbindOpen(Key key);
    }

    /// <summary>Which side of the title bar the system puts the caption buttons on.</summary>
    public enum CaptionButtonsPlacement
    {
        Right,
        Left
    }

    public static class WindowPlatforms
    {
        public static IWindowPlatform Default { get; set; }

        /// <summary>
        /// The system's caption button layout, which every window follows as follows the OS: on the right
        /// as minimize, maximize, close; on the left mirrored as close, maximize, minimize.
        /// </summary>
        public static CaptionButtonsPlacement CaptionButtons { get; set; }
    }
}
