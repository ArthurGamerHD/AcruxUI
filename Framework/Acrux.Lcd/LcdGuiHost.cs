using System;
using System.Collections.Generic;
using Acrux.Animation;
using Acrux.Core;
using Acrux.Core.Input;
using Acrux.Core.Invalidation;
using VRage.Game.GUI.TextPanel;
using VRageMath;
using IMyTextSurface = Sandbox.ModAPI.Ingame.IMyTextSurface;

namespace Acrux.Lcd
{
    /// <summary>
    /// Owns the complete Acrux UI lifecycle for one LCD surface: retained tree,
    /// layout, surface-space input, and sprite-list rendering.
    /// </summary>
    public sealed class LcdGuiHost : IDisposable, global::Acrux.Toolkit.IExternalPeerHost
    {
        object _externalPeer;
        object global::Acrux.Toolkit.IExternalPeerHost.ExternalPeer { get { return _externalPeer; } set { _externalPeer = value; } }

        readonly VisualElement _root;
        readonly LcdRenderer _renderer;
        readonly LcdInputSource _input = new LcdInputSource();
        readonly VisualTree _tree;
        readonly bool _ownsRoot;
        RectangleF _bounds;
        bool _hasBounds;
        bool _disposed;
        float _renderScaling = 1f;
        readonly List<MySprite> _frame = new List<MySprite>();
        readonly List<MySprite> _verification = new List<MySprite>();
        float _frameOpacity;
        bool _hasFrame;
        public Func<float> RenderScalingProvider { get; set; }

        /// <summary>Backend DPI scale. Each LCD host has its own independent scale.</summary>
        public float RenderScaling
        {
            get { return _renderScaling; }
            set
            {
                ThrowIfDisposed();
                if (value <= 0f || float.IsNaN(value) || float.IsInfinity(value))
                    throw new ArgumentException("Render scaling must be finite and positive.", nameof(value));
                if (_renderScaling == value)
                    return;
                _renderScaling = value;
                _root.Invalidate(UiInvalidation.Measure | UiInvalidation.Arrange | UiInvalidation.Render);
            }
        }

        public LcdGuiHost(
            IMyTextSurface surface,
            VisualElement root,
            string missingTexture = "MissingIcon",
            bool ownsRoot = true)
            : this(surface, root, AnimationServices.DefaultController, missingTexture, ownsRoot)
        {
        }

        public LcdGuiHost(
            IMyTextSurface surface,
            VisualElement root,
            AnimationController animations,
            string missingTexture = "MissingIcon",
            bool ownsRoot = true)
        {
            if (surface == null)
                throw new ArgumentNullException(nameof(surface));
            if (root == null)
                throw new ArgumentNullException(nameof(root));

            _root = root;
            _ownsRoot = ownsRoot;
            _renderer = new LcdRenderer(surface, missingTexture);
            _renderer.OwnerRoot = root;
            _tree = Acrux.Gui.PrepareRender(_renderer).CreateVisualTree(root, animations);
        }

        public VisualElement Root => _root;
        public VisualTree VisualTree => _tree;
        public LcdRenderer Renderer => _renderer;
        public LcdInputSource Input => _input;
        public RectangleF Bounds => _bounds;
        public bool HasKeyboardFocus => _tree.HasKeyboardFocus;
        public PointerCursor PointerCursor => _tree.PointerCursor;
        public bool OwnsPointer => _tree.OwnsPointer;
        public bool IsDisposed => _disposed;

        public void Arrange(RectangleF bounds)
        {
            ThrowIfDisposed();
            if (RenderScalingProvider != null)
                RenderScaling = RenderScalingProvider();
            if (!_hasBounds || _bounds != bounds)
                _hasFrame = false;
            _bounds = bounds;
            _hasBounds = true;
            _tree.ArrangeViewport(bounds, _renderScaling);
        }

        public void EnsureLayout()
        {
            ThrowIfDisposed();
            if (RenderScalingProvider != null)
                RenderScaling = RenderScalingProvider();
            if (!_hasBounds)
                return;

            UiInvalidation invalidation = _root.PendingInvalidation;
            if ((invalidation & (UiInvalidation.Measure | UiInvalidation.Arrange)) != 0)
                Arrange(_bounds);
        }

        public void Draw(List<MySprite> sprites, float opacity = 1f)
        {
            ThrowIfDisposed();
            if (sprites == null)
                throw new ArgumentNullException(nameof(sprites));
            EnsureLayout();
            if (_hasFrame && _frameOpacity == opacity && !_tree.NeedsRender)
            {
                if (FrameCacheMismatch != null)
                    VerifyFrame(opacity);
                sprites.AddRange(_frame);
                return;
            }

            int start = sprites.Count;
            _renderer.Draw(_tree, sprites, opacity);
            if ((_root.PendingInvalidation & (UiInvalidation.Measure | UiInvalidation.Arrange)) != 0)
            {
                // Styles and templates are applied while drawing; when that changed layout, the
                // frame just drawn used stale bounds.
                sprites.RemoveRange(start, sprites.Count - start);
                EnsureLayout();
                _renderer.Draw(_tree, sprites, opacity);
            }
            _frame.Clear();
            for (int i = start; i < sprites.Count; i++)
                _frame.Add(sprites[i]);
            _frameOpacity = opacity;
            _hasFrame = true;
        }

        /// <summary>
        /// Diagnostics: when set, every reused frame is also redrawn and compared, and the handler
        /// receives a description of elements that changed without invalidating.
        /// </summary>
        public static Action<string> FrameCacheMismatch { get; set; }

        void VerifyFrame(float opacity)
        {
            _verification.Clear();
            _renderer.Draw(_tree, _verification, opacity);
            int count = Math.Max(_verification.Count, _frame.Count);
            for (int i = 0; i < count; i++)
            {
                if (i < _verification.Count && i < _frame.Count && SameSprite(_verification[i], _frame[i]))
                    continue;
                FrameCacheMismatch(string.Format(
                    "Stale LCD frame for {0}: sprite {1} cached '{2}' but tree draws '{3}'.",
                    _root.GetType().Name, i,
                    i < _frame.Count ? _frame[i].Data : "<none>",
                    i < _verification.Count ? _verification[i].Data : "<none>"));
                return;
            }
        }

        static bool SameSprite(MySprite left, MySprite right)
        {
            return left.Type == right.Type &&
                   left.Alignment == right.Alignment &&
                   left.RotationOrScale == right.RotationOrScale &&
                   left.Position == right.Position &&
                   left.Size == right.Size &&
                   left.Color == right.Color &&
                   string.Equals(left.Data, right.Data, StringComparison.Ordinal) &&
                   string.Equals(left.FontId, right.FontId, StringComparison.Ordinal);
        }

        /// <summary>True when the next <see cref="Draw"/> would produce a different frame.</summary>
        public bool NeedsRender => !_hasFrame || _tree.NeedsRender;

        /// <summary>Why the cached frame cannot be reused; null when it can. For render diagnostics.</summary>
        public string DescribeRenderReason()
        {
            return _hasFrame ? _tree.DescribeRenderReason() : "no cached frame";
        }

        /// <summary>Discards the cached frame so the next draw walks the tree again.</summary>
        public void InvalidateFrame()
        {
            _hasFrame = false;
        }

        public void MovePointer(Vector2 position)
        {
            ThrowIfDisposed();
            _input.SetPointer(position);
            HandleInputPass();
        }

        public void ClearPointer()
        {
            ThrowIfDisposed();
            _input.ClearPointer();
            _tree.ReleasePointer();
            _input.EndInputPass();
        }

        /// <summary>Updates the primary button; an unchanged state runs no input pass, as callers report it every frame.</summary>
        public void SetPrimaryButton(bool pressed)
        {
            ThrowIfDisposed();
            if (_input.IsPrimaryButtonPressed() == pressed)
                return;
            _input.SetPrimaryButton(pressed);
            HandleInputPass();
        }

        public void SetMiddleButton(bool pressed)
        {
            ThrowIfDisposed();
            if (_input.IsMiddleButtonPressed() == pressed)
                return;
            _input.SetMiddleButton(pressed);
            HandleInputPass();
        }

        public void SetSecondaryButton(bool pressed)
        {
            ThrowIfDisposed();
            if (_input.IsSecondaryButtonPressed() == pressed)
                return;
            _input.SetSecondaryButton(pressed);
            HandleInputPass();
        }

        public void ClickPrimary()
        {
            SetPrimaryButton(true);
            SetPrimaryButton(false);
        }

        public void ClickMiddle()
        {
            SetMiddleButton(true);
            SetMiddleButton(false);
        }

        public void ClickSecondary()
        {
            SetSecondaryButton(true);
            SetSecondaryButton(false);
        }

        public bool Scroll(int delta)
        {
            ThrowIfDisposed();
            _input.Scroll(delta);
            return HandleInputPass();
        }

        public void UpdateKeyboardInput()
        {
            ThrowIfDisposed();
            HandleInputPass();
        }

        bool HandleInputPass()
        {
            EnsureLayout();
            bool handled = _tree.HandleInput(_input, _input.KeyboardEnabled);
            _input.EndInputPass();
            return handled;
        }

        void ThrowIfDisposed()
        {
            if (_disposed)
                throw new InvalidOperationException("The LCD GUI host has been disposed.");
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _tree.ResetInput();
            if (_ownsRoot)
                _root.Dispose();
        }
    }
}
