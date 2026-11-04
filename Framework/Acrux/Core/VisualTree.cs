using System;
using System.Collections.Generic;
using System.Threading;
using Acrux.Animation;
using Acrux.Controls;
using Acrux.Core.Input;
using Acrux.Core.Invalidation;
using Acrux.Rendering;
using VRageMath;

namespace Acrux.Core
{
    internal interface IOverlayProvider
    {
        bool IsOverlayOpen { get; }
        VisualElement OverlayElement { get; }
        void UpdateOverlayLayout();
        void CloseOverlay();
    }

    internal interface IModalOverlayProvider
    {
        void HandleOverlayInput(IInputSource input);
    }

    internal interface ILightDismissOverlayProvider
    {
        bool IsLightDismissEnabled { get; }
        bool OverlayDismissEventPassThrough { get; }
    }

    public sealed class VisualTree
    {
        readonly VisualElement _root;
        readonly IUiRenderer _renderer;
        readonly AnimationController _animations;
        VisualElement _hovered;
        bool _overlayOpen;
        readonly ToolTip _toolTip = new ToolTip();
        Control _toolTipTarget;
        long _toolTipSince;
        bool _toolTipOpen;
        bool _toolTipSuppressed;
        bool _toolTipChanged;
        Vector2 _toolTipPointer;
        VisualElement _pressed;
        VisualElement _middlePressed;
        VisualElement _secondaryPressed;
        VisualElement _focused;
        readonly List<VisualElement> _pointerOverPath = new List<VisualElement>();
        readonly List<VisualElement> _nextPointerOverPath = new List<VisualElement>();
        readonly List<VisualElement> _pressedPath = new List<VisualElement>();
        readonly List<VisualElement> _focusWithinPath = new List<VisualElement>();
        Vector2 _lastPointer;
        bool _hasLastPointer;
        float _renderScaling = 1f;
        Vector2 _renderOrigin;
        readonly LogicalInputSource _logicalInput = new LogicalInputSource();
        VisualElement _inputRoot;
        PointerCursor _pointerCursor = PointerCursor.Arrow;
        readonly List<VisualElement> _drawnOverlays = new List<VisualElement>();
        bool _lastFrameVolatile;
        string _lastVolatileSource;

        public VisualTree(VisualElement root, IUiRenderer renderer)
            : this(root, renderer, AnimationServices.DefaultController)
        {
        }

        public VisualTree(
            VisualElement root,
            IUiRenderer renderer,
            AnimationController animations)
        {
            if (root == null)
                throw new ArgumentNullException(nameof(root));
            if (renderer == null)
                throw new ArgumentNullException(nameof(renderer));
            _root = root;
            _inputRoot = root;
            _renderer = renderer;
            _animations = animations;
            _root.AttachTextMetrics(_renderer.TextMetrics);
            _root.AttachAnimationController(_animations);
            _root.RootTree = this;
        }

        public VisualElement Root => _root;
        public AnimationController Animations => _animations;
        public VisualElement FocusedElement => _focused;
        public bool HasKeyboardFocus => _focused != null && _focused.Visible && _focused.IsEffectivelyEnabled;
        public PointerCursor PointerCursor => _pointerCursor;

        /// <summary>True while the pointer is over a hit-testable element or a popup is open, i.e. the tree consumes it.</summary>
        public bool OwnsPointer => _hovered != null || _overlayOpen;

        // Backend viewport coordinates are physical pixels; layout and input use
        // logical units. Keep the viewport origin fixed, including padded LCDs.
        internal void ArrangeViewport(RectangleF bounds, float scaling)
        {
            if (scaling <= 0f || float.IsNaN(scaling) || float.IsInfinity(scaling))
                throw new ArgumentException("Render scaling must be finite and positive.", nameof(scaling));
            if (_renderScaling != scaling || _renderOrigin != bounds.Position)
                _hasLastPointer = false;
            _renderScaling = scaling;
            _renderOrigin = bounds.Position;
            var window = _root as Acrux.Controls.Window;
            if (window != null)
                window.SetRenderScaling(scaling);
            Vector2 size = bounds.Size / scaling;
            _root.Measure(size);
            _root.Arrange(new RectangleF(bounds.Position, size));
        }

        public void Draw(float transitionAlpha)
        {
            var context = new RenderContext(_renderer, transitionAlpha, _renderScaling, _renderOrigin);
            ArrangeOwnedWindows(_root as Window);
            _drawnOverlays.Clear();
            DrawWindow(_root, context);
            DrawToolTip(context);
            _toolTipChanged = false;
            _lastFrameVolatile = context.IsVolatile;
            _lastVolatileSource = context.VolatileSource;
        }

        /// <summary>
        /// Why <see cref="NeedsRender"/> is true, naming the deepest invalidated element; null when the last frame can be
        /// reused. Meant for diagnosing screens that redraw every frame.
        /// </summary>
        public string DescribeRenderReason()
        {
            if (_lastFrameVolatile)
                return "volatile frame (" + (_lastVolatileSource ?? "unknown source") + ")";
            if (_toolTipChanged)
                return "tooltip changed";
            VisualElement invalidated = FindDeepestInvalidated(_root);
            if (invalidated != null)
                return DescribeElement(invalidated) + " " + invalidated.PendingInvalidation;
            for (int i = 0; i < _drawnOverlays.Count; i++)
            {
                invalidated = FindDeepestInvalidated(_drawnOverlays[i]);
                if (invalidated != null)
                    return "overlay " + DescribeElement(invalidated) + " " + invalidated.PendingInvalidation;
            }
            return NeedsRender ? "owned window invalidated" : null;
        }

        /// <summary>Describes the deepest visible invalidated element under <paramref name="root"/>, or null.</summary>
        public static string DescribeInvalidation(VisualElement root)
        {
            VisualElement invalidated = FindDeepestInvalidated(root);
            return invalidated == null ? null : DescribeElement(invalidated) + " " + invalidated.PendingInvalidation;
        }

        static VisualElement FindDeepestInvalidated(VisualElement element)
        {
            // Hidden elements are never drawn, so their flags are never cleared and say nothing about this frame.
            if (element == null || !element.Visible || element.PendingInvalidation == UiInvalidation.None)
                return null;
            IReadOnlyList<VisualElement> children = element.VisualChildren;
            for (int i = 0; i < children.Count; i++)
            {
                VisualElement found = FindDeepestInvalidated(children[i]);
                if (found != null)
                    return found;
            }
            return element;
        }

        static string DescribeElement(VisualElement element)
        {
            var text = new System.Text.StringBuilder();
            int depth = 0;
            for (VisualElement current = element; current != null && depth < 5; current = current.Parent, depth++)
            {
                if (depth > 0)
                    text.Append(" < ");
                text.Append(current.GetType().Name);
                if (!string.IsNullOrEmpty(current.StyleClass))
                    text.Append('.').Append(current.StyleClass);
            }
            return text.ToString();
        }

        /// <summary>The control whose tooltip is showing, if any.</summary>
        public Control OpenToolTipTarget => _toolTipOpen ? _toolTipTarget : null;

        void DrawToolTip(RenderContext context)
        {
            if (!_toolTipOpen || _toolTipTarget == null)
                return;
            VisualElement root = ResolveInputRoot();
            _toolTip.OverlayOwner = _toolTipTarget as VisualElement;
            if (!ReferenceEquals(_toolTip.AttachedTextMetrics, _renderer.TextMetrics))
                _toolTip.AttachTextMetrics(_renderer.TextMetrics);
            _toolTip.Place(_toolTipTarget, _toolTipPointer, root.Bounds);
            _toolTip.DrawTree(context);
            _drawnOverlays.Add(_toolTip);
        }

        void UpdateToolTip(VisualElement searchRoot, Vector2 pointer, bool buttonActivity)
        {
            Control target = FindToolTipTarget(searchRoot, pointer);
            long now = DateTime.UtcNow.Ticks;
            if (!ReferenceEquals(target, _toolTipTarget))
            {
                CloseToolTip();
                _toolTipTarget = target;
                _toolTipSince = now;
                _toolTipSuppressed = false;
            }
            if (buttonActivity)
            {
                CloseToolTip();
                _toolTipSuppressed = true;
            }
            if (target == null || _toolTipSuppressed)
                return;
            object tip = ToolTip.GetTip(target);
            if (_toolTipOpen)
            {
                if (!ReferenceEquals(tip, _toolTip.Content))
                {
                    _toolTip.Content = tip;
                    _toolTipChanged = true;
                }
                return;
            }
            long delay = Math.Max(0, ToolTip.GetShowDelay(target)) * TimeSpan.TicksPerMillisecond;
            if (now - _toolTipSince < delay)
                return;
            _toolTip.Content = tip;
            _toolTipPointer = pointer;
            _toolTipOpen = true;
            _toolTipChanged = true;
            ToolTip.SetIsOpen(target, true);
        }

        void CloseToolTip()
        {
            if (!_toolTipOpen)
                return;
            _toolTipOpen = false;
            _toolTipChanged = true;
            if (_toolTipTarget != null)
                ToolTip.SetIsOpen(_toolTipTarget, false);
        }

        // Non-modal owned windows sit above their owner and take input only on their own frame.
        static VisualElement HitOwnedWindows(Window owner, Vector2 pointer)
        {
            if (owner == null)
                return null;
            IReadOnlyList<Window> windows = owner.OwnedWindows;
            for (int i = windows.Count - 1; i >= 0; i--)
            {
                Window window = windows[i];
                if (window.IsModal || !window.Visible || !window.FrameBounds.Contains(pointer))
                    continue;
                return window.HitTest(pointer) ?? window;
            }
            return null;
        }

        // Non-modal windows an owner opened after the active modal chain are drawn above that modal (windows draw in
        // opening order), so they take the pointer first; e.g. a hint shown while a dialog is open stays clickable.
        static VisualElement HitWindowsAboveModal(Window modal, Vector2 pointer)
        {
            if (modal == null || modal.Owner == null)
                return null;
            var chain = new List<Window>();
            for (Window window = modal; window.Owner != null; window = window.Owner)
                chain.Add(window);
            for (int level = chain.Count - 1; level >= 0; level--)
            {
                Window child = chain[level];
                IReadOnlyList<Window> windows = child.Owner.OwnedWindows;
                for (int i = windows.Count - 1; i >= 0 && !ReferenceEquals(windows[i], child); i--)
                {
                    Window window = windows[i];
                    if (window.IsModal || !window.Visible || !window.FrameBounds.Contains(pointer))
                        continue;
                    return window.HitTest(pointer) ?? window;
                }
            }
            return null;
        }

        static Control FindToolTipTarget(VisualElement element, Vector2 point)
        {
            if (element == null || !element.Visible || !element.Bounds.Contains(point))
                return null;
            IReadOnlyList<VisualElement> children = element.VisualChildren;
            Vector2 childPoint = element.MapPointToChildren(point);
            for (int i = children.Count - 1; i >= 0; i--)
            {
                Control found = FindToolTipTarget(children[i], childPoint);
                if (found != null)
                    return found;
            }
            return ToolTip.GetTip(element) != null ? element : null;
        }

        /// <summary>
        /// True when the last drawn frame is stale: an element, open overlay or owned window was
        /// invalidated since, or the frame contained content that changes without invalidation.
        /// </summary>
        public bool NeedsRender
        {
            get
            {
                if (_lastFrameVolatile || _toolTipChanged || _root.PendingInvalidation != UiInvalidation.None)
                    return true;
                for (int i = 0; i < _drawnOverlays.Count; i++)
                    if (_drawnOverlays[i].PendingInvalidation != UiInvalidation.None)
                        return true;
                return OwnedWindowsNeedRender(_root as Window);
            }
        }

        static bool OwnedWindowsNeedRender(Window owner)
        {
            if (owner == null)
                return false;
            IReadOnlyList<Window> dialogs = owner.OwnedWindows;
            for (int i = 0; i < dialogs.Count; i++)
                if (dialogs[i].PendingInvalidation != UiInvalidation.None || OwnedWindowsNeedRender(dialogs[i]))
                    return true;
            return false;
        }

        void ArrangeOwnedWindows(Window owner)
        {
            if (owner == null) return;
            foreach (var dialog in owner.OwnedWindows)
            {
                dialog.AttachTextMetrics(_renderer.TextMetrics);
                dialog.AttachAnimationController(_animations);
                dialog.SetRenderScaling(_renderScaling);
                dialog.Measure(owner.Bounds.Size);
                dialog.Arrange(owner.Bounds);
                ArrangeOwnedWindows(dialog);
            }
        }

        void DrawWindow(VisualElement root, RenderContext context)
        {
            root.DrawTree(context);

            // Parent flyouts must remain drawn underneath nested dropdowns.
            foreach (IOverlayProvider overlay in GetOpenOverlayStack(root))
            {
                VisualElement element = overlay.OverlayElement;
                if (element != null)
                {
                    element.DrawTree(context);
                    _drawnOverlays.Add(element);
                }
            }
            var window = root as Window;
            if (window != null)
                foreach (var dialog in window.OwnedWindows)
                    DrawWindow(dialog, context);
        }

        // Overlay content is not part of the tree, so it gets its owner and text metrics here, before it is laid out.
        void AdoptOverlay(IOverlayProvider overlay, VisualElement element)
        {
            if (element.Parent == null)
                element.OverlayOwner = overlay as VisualElement;
            if (!ReferenceEquals(element.AttachedTextMetrics, _renderer.TextMetrics))
                element.AttachTextMetrics(_renderer.TextMetrics);
        }

        List<IOverlayProvider> GetOpenOverlayStack(VisualElement root)
        {
            var stack = new List<IOverlayProvider>();
            IOverlayProvider overlay = FindOpenOverlay(root);
            while (overlay != null && !stack.Contains(overlay))
            {
                stack.Add(overlay);
                VisualElement element = overlay.OverlayElement;
                if (element == null) break;
                AdoptOverlay(overlay, element);
                overlay.UpdateOverlayLayout();
                // Popup content isn't necessarily a logical child of its owner.
                // Discover descendants inside the overlay as a separate layer.
                overlay = FindOpenOverlay(element);
            }
            return stack;
        }

        IOverlayProvider GetTopmostOpenOverlay(VisualElement root)
        {
            List<IOverlayProvider> stack = GetOpenOverlayStack(root);
            return stack.Count == 0 ? null : stack[stack.Count - 1];
        }

        VisualElement ResolveInputRoot()
        {
            var window = _root as Window;
            while (window != null && window.ActiveModalWindow != null)
                window = window.ActiveModalWindow;
            return window ?? _root;
        }

        public bool HandleInput(IInputSource input)
        {
            return HandleInput(input, true);
        }

        /// <summary>
        /// A pass with <paramref name="keyboard"/> false delivers pointer input only, leaving focused controls'
        /// key state (held keys, repeat timing) untouched, for hosts whose keyboard arrives in a separate pass.
        /// </summary>
        public bool HandleInput(IInputSource input, bool keyboard)
        {
            var inputRoot = ResolveInputRoot();
            if (!ReferenceEquals(_inputRoot, inputRoot))
            {
                ResetInput();
                _inputRoot = inputRoot;
            }
            bool modal = !ReferenceEquals(inputRoot, _root);
            if (modal) ArrangeOwnedWindows(_root as Window);
            if (_focused != null && (!_focused.Visible || !_focused.IsEffectivelyEnabled))
                SetFocus(null, false);

            if (input == null)
                return false;

            _logicalInput.Source = input;
            _logicalInput.Origin = _renderOrigin;
            _logicalInput.Scaling = _renderScaling;
            input = _logicalInput;

            Vector2 pointer;
            if (!input.TryGetPointerPosition(out pointer))
            {
                if (keyboard)
                    HandleKeyboardInput(input, GetTopmostOpenOverlay(inputRoot));
                return modal;
            }

            Vector2 delta = _hasLastPointer ? pointer - _lastPointer : Vector2.Zero;
            _lastPointer = pointer;
            _hasLastPointer = true;

            IOverlayProvider overlay = GetTopmostOpenOverlay(inputRoot);
            _overlayOpen = overlay != null;
            VisualElement overlayElement = overlay == null ? null : overlay.OverlayElement;
            if (overlayElement != null)
                AdoptOverlay(overlay, overlayElement);
            if (overlay != null)
                overlay.UpdateOverlayLayout();
            VisualElement overlayHit = overlayElement == null ? null : overlayElement.HitTest(pointer);
            VisualElement hit = overlayHit ?? HitWindowsAboveModal(inputRoot as Window, pointer) ??
                                HitOwnedWindows(inputRoot as Window, pointer) ?? inputRoot.HitTest(pointer);
            if (!ReferenceEquals(hit, _hovered))
            {
                SetPointerOverPath(hit);
                _hovered = hit;
            }
            for (VisualElement current = hit; current != null; current = current.Parent)
                current.PointerHovered(pointer);
            var dismissable = overlay as ILightDismissOverlayProvider;
            bool passThrough = dismissable != null && dismissable.OverlayDismissEventPassThrough;
            UpdateToolTip(overlayHit == null && passThrough ? inputRoot : overlayElement ?? inputRoot, pointer,
                input.IsNewPrimaryButtonPressed() || input.IsNewSecondaryButtonPressed() || input.IsNewMiddleButtonPressed() ||
                input.DeltaMouseScrollWheelValue() != 0);

            if ((input.IsNewPrimaryButtonPressed() || input.IsNewSecondaryButtonPressed()) &&
                overlay != null && overlayHit == null &&
                (overlayElement == null || !overlayElement.OverlayContains(pointer)))
            {
                if (dismissable == null || dismissable.IsLightDismissEnabled)
                    overlay.CloseOverlay();
                if (!passThrough)
                {
                    ClearPointerState();
                    return true;
                }
            }

            if (input.IsNewPrimaryButtonPressed())
            {
                SetFocus(FindFocusable(hit), false);
                _pressed = hit;
                SetPressedPath(_pressed);
                if (_pressed != null)
                    _pressed.PointerPressed(pointer);
            }

            if (_pressed != null && input.IsPrimaryButtonPressed())
                _pressed.PointerMoved(pointer, delta);

            if (input.IsNewPrimaryButtonReleased())
            {
                VisualElement pressed = _pressed;
                _pressed = null;
                ClearPressedPath();
                if (pressed != null)
                    pressed.PointerReleased(pointer, ReferenceEquals(pressed, hit));
            }

            if (input.IsNewMiddleButtonPressed())
            {
                _middlePressed = hit;
                if (_middlePressed != null)
                    _middlePressed.MiddlePointerPressed(pointer);
            }

            if (_middlePressed != null && input.IsMiddleButtonPressed())
                _middlePressed.MiddlePointerMoved(pointer, delta);

            if (input.IsNewMiddleButtonReleased())
            {
                VisualElement middlePressed = _middlePressed;
                _middlePressed = null;
                if (middlePressed != null)
                    middlePressed.MiddlePointerReleased(pointer);
            }

            if (input.IsNewSecondaryButtonPressed())
            {
                _secondaryPressed = hit;
                if (_secondaryPressed != null)
                    _secondaryPressed.SecondaryPointerPressed(pointer);
            }

            if (_secondaryPressed != null && input.IsSecondaryButtonPressed())
                _secondaryPressed.SecondaryPointerMoved(pointer, delta);

            if (input.IsNewSecondaryButtonReleased())
            {
                VisualElement secondaryPressed = _secondaryPressed;
                _secondaryPressed = null;
                bool clicked = ReferenceEquals(secondaryPressed, hit);
                if (secondaryPressed != null)
                    secondaryPressed.SecondaryPointerReleased(pointer, clicked);
                if (clicked)
                    OpenContextMenu(secondaryPressed, pointer);
            }

            int wheel = input.DeltaMouseScrollWheelValue();
            bool wheelHandled = false;
            if (wheel != 0 && hit != null)
            {
                for (VisualElement current = hit; current != null; current = current.Parent)
                {
                    if (current.PointerScrolled(pointer, wheel))
                    {
                        wheelHandled = true;
                        break;
                    }
                }
            }

            ResolvePointerCursor(_pressed ?? _middlePressed ?? hit, pointer);

            // Opening or closing a dialog during this pass must not deliver the
            // same keyboard event to the window that just became active.
            if (keyboard && ReferenceEquals(inputRoot, ResolveInputRoot()))
                HandleKeyboardInput(input, overlay);
            return wheelHandled || modal || !ReferenceEquals(inputRoot, ResolveInputRoot());
        }

        void ResolvePointerCursor(VisualElement element, Vector2 point)
        {
            for (VisualElement current = element; current != null; current = current.Parent)
            {
                PointerCursor cursor = current.GetPointerCursor(point);
                if (cursor == PointerCursor.Inherit)
                    continue;
                _pointerCursor = cursor;
                return;
            }
            _pointerCursor = PointerCursor.Arrow;
        }

        void HandleKeyboardInput(IInputSource input, IOverlayProvider overlay)
        {
            if (overlay != null && input.IsNewKeyPressed(Key.Escape))
            {
                overlay.CloseOverlay();
                return;
            }

            var dialog = _inputRoot as Window;
            if (dialog != null && dialog.Owner != null && dialog.CloseOnEscape && input.IsNewKeyPressed(Key.Escape))
            {
                dialog.Close();
                ResetInput();
                return;
            }

            IModalOverlayProvider modalOverlay = overlay as IModalOverlayProvider;
            if (modalOverlay != null)
            {
                modalOverlay.HandleOverlayInput(input);
                return;
            }

            if (_focused != null && _focused.Visible && _focused.IsEffectivelyEnabled)
                _focused.KeyboardInput(input);
        }

        static int _openOverlayFlags;

        // Counts open overlay states so frames with none open skip the tree walk.
        internal static void OverlayOpenChanged(bool open)
        {
            if (open)
                Interlocked.Increment(ref _openOverlayFlags);
            else
                Interlocked.Decrement(ref _openOverlayFlags);
        }

        static IOverlayProvider FindOpenOverlay(VisualElement root)
        {
            return _openOverlayFlags <= 0 ? null : FindOpenOverlayIn(root);
        }

        static IOverlayProvider FindOpenOverlayIn(VisualElement element)
        {
            if (element == null || !element.Visible || !element.Enabled)
                return null;

            IReadOnlyList<VisualElement> children = element.LogicalChildren;
            for (int i = children.Count - 1; i >= 0; i--)
            {
                IOverlayProvider nested = FindOpenOverlayIn(children[i]);
                if (nested != null)
                    return nested;
            }

            ContextMenu contextMenu = element.ContextMenu;
            if (contextMenu != null && contextMenu.IsOpen)
                return contextMenu;

            IOverlayProvider provider = element as IOverlayProvider;
            return provider != null && provider.IsOverlayOpen ? provider : null;
        }

        // Raises ContextRequested on, or opens the context menu of, the nearest element under a secondary click, unless
        // an element on the way handles the click itself.
        static void OpenContextMenu(VisualElement element, Vector2 pointer)
        {
            for (VisualElement current = element; current != null; current = current.Parent)
            {
                if (current.HandlesSecondaryClick)
                    return;
                if (current.ContextRequested != null && current.IsEffectivelyEnabled)
                {
                    current.ContextRequested();
                    return;
                }
                ContextMenu contextMenu = current.ContextMenu;
                if (contextMenu != null && current.IsEffectivelyEnabled)
                {
                    contextMenu.Open(current, pointer);
                    return;
                }
            }
        }

        void ClearPointerState()
        {
            NotifyPointerCaptureLost();
            ClearPointerOverPath();
            ClearPressedPath();
            _hovered = null;
            _pressed = null;
            _middlePressed = null;
            _secondaryPressed = null;
            _pointerCursor = PointerCursor.Arrow;
        }

        static VisualElement FindFocusable(VisualElement element)
        {
            for (VisualElement current = element; current != null; current = current.Parent)
            {
                if (current.AcceptsKeyboardFocus)
                    return current;
            }
            return null;
        }

        public bool Focus(VisualElement element, bool focusVisible = true)
        {
            if (element != null)
            {
                var ancestor = element;
                while (ancestor.Parent != null) ancestor = ancestor.Parent;
                if (!ReferenceEquals(ancestor, ResolveInputRoot())) return false;
            }
            if (element != null && (!element.Visible || !element.IsEffectivelyEnabled ||
                                    !element.AcceptsKeyboardFocus))
                return false;
            SetFocus(element, focusVisible);
            return true;
        }

        void SetFocus(VisualElement element, bool focusVisible)
        {
            if (ReferenceEquals(_focused, element))
            {
                if (_focused != null)
                    _focused.IsFocusVisible = focusVisible;
                return;
            }
            if (_focused != null)
            {
                _focused.IsFocused = false;
                _focused.IsFocusVisible = false;
            }
            ClearFocusWithinPath();
            _focused = element;
            if (_focused != null)
            {
                _focused.IsFocused = true;
                _focused.IsFocusVisible = focusVisible;
                SetFocusWithinPath(_focused);
            }
        }

        // Moves :pointerover to the path under element, touching only the elements the pointer left or entered, like
        // PointerExited/PointerEntered. Flipping the shared ancestors off and on would restyle the whole tree each time
        // the hovered leaf changes.
        void SetPointerOverPath(VisualElement element)
        {
            _nextPointerOverPath.Clear();
            for (VisualElement current = element; current != null; current = current.Parent)
                _nextPointerOverPath.Add(current);

            for (int i = 0; i < _pointerOverPath.Count; i++)
                if (!_nextPointerOverPath.Contains(_pointerOverPath[i]))
                    _pointerOverPath[i].IsPointerOver = false;
            for (int i = _nextPointerOverPath.Count - 1; i >= 0; i--)
                _nextPointerOverPath[i].IsPointerOver = true;

            _pointerOverPath.Clear();
            _pointerOverPath.AddRange(_nextPointerOverPath);
            _nextPointerOverPath.Clear();
        }

        void ClearPointerOverPath()
        {
            for (int i = 0; i < _pointerOverPath.Count; i++)
                _pointerOverPath[i].IsPointerOver = false;
            _pointerOverPath.Clear();
        }

        void SetPressedPath(VisualElement element)
        {
            ClearPressedPath();
            for (VisualElement current = element; current != null; current = current.Parent)
            {
                current.IsPressed = true;
                _pressedPath.Add(current);
            }
        }

        void ClearPressedPath()
        {
            for (int i = 0; i < _pressedPath.Count; i++)
                _pressedPath[i].IsPressed = false;
            _pressedPath.Clear();
        }

        void SetFocusWithinPath(VisualElement element)
        {
            for (VisualElement current = element; current != null; current = current.Parent)
            {
                current.IsFocusWithin = true;
                _focusWithinPath.Add(current);
            }
        }

        void ClearFocusWithinPath()
        {
            for (int i = 0; i < _focusWithinPath.Count; i++)
                _focusWithinPath[i].IsFocusWithin = false;
            _focusWithinPath.Clear();
        }

        public void ResetInput()
        {
            ReleasePointer();
            SetFocus(null, false);
        }

        /// <summary>
        /// The pointer left the surface: drops hover, presses, capture and tooltips, but keeps keyboard focus, as a
        /// text box keeps its focus when the mouse leaves it.
        /// </summary>
        public void ReleasePointer()
        {
            CloseToolTip();
            _toolTipTarget = null;
            NotifyPointerCaptureLost();
            ClearPointerOverPath();
            ClearPressedPath();
            _hovered = null;
            _pressed = null;
            _middlePressed = null;
            _secondaryPressed = null;
            _hasLastPointer = false;
            _pointerCursor = PointerCursor.Arrow;
        }

        void NotifyPointerCaptureLost()
        {
            if (_pressed != null)
                _pressed.PointerCaptureLost();
            if (_middlePressed != null && !ReferenceEquals(_middlePressed, _pressed))
                _middlePressed.PointerCaptureLost();
            if (_secondaryPressed != null && !ReferenceEquals(_secondaryPressed, _pressed) &&
                !ReferenceEquals(_secondaryPressed, _middlePressed))
                _secondaryPressed.PointerCaptureLost();
        }

        // Keyboard/modal handlers can also query the pointer; all consumers must
        // see the same logical coordinate space as hit testing and dragging.
        sealed class LogicalInputSource : IInputSource
        {
            internal IInputSource Source;
            internal Vector2 Origin;
            internal float Scaling;
            public IEnumerable<char> TextInput => Source.TextInput;
            public bool TryGetPointerPosition(out Vector2 position)
            {
                bool found = Source.TryGetPointerPosition(out position);
                if (found) position = Origin + (position - Origin) / Scaling;
                return found;
            }
            public int DeltaMouseScrollWheelValue() => Source.DeltaMouseScrollWheelValue();
            public bool IsPrimaryButtonPressed() => Source.IsPrimaryButtonPressed();
            public bool IsNewPrimaryButtonPressed() => Source.IsNewPrimaryButtonPressed();
            public bool IsNewPrimaryButtonReleased() => Source.IsNewPrimaryButtonReleased();
            public bool IsMiddleButtonPressed() => Source.IsMiddleButtonPressed();
            public bool IsNewMiddleButtonPressed() => Source.IsNewMiddleButtonPressed();
            public bool IsNewMiddleButtonReleased() => Source.IsNewMiddleButtonReleased();
            public bool IsSecondaryButtonPressed() => Source.IsSecondaryButtonPressed();
            public bool IsNewSecondaryButtonPressed() => Source.IsNewSecondaryButtonPressed();
            public bool IsNewSecondaryButtonReleased() => Source.IsNewSecondaryButtonReleased();
            public bool IsKeyPressed(Key key) => Source.IsKeyPressed(key);
            public bool IsNewKeyPressed(Key key) => Source.IsNewKeyPressed(key);
            public void GetPressedKeys(List<Key> keys) { Source.GetPressedKeys(keys); }
        }
    }
}
