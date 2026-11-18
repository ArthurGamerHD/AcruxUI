using System;
using System.Collections.Generic;
using Acrux.Animation;
using Acrux.Core.Binding;
using Acrux.Core.Input;
using Acrux.Core.Invalidation;
using Acrux.Interactivity;
using Acrux.Rendering;
using Acrux.Styling;
using VRageMath;

namespace Acrux.Core
{
    /// <summary>
    /// Retained visual node shared by renderer hosts. It owns children, bindings,
    /// hit testing, and routed input while drawing through RenderContext.
    /// </summary>
    public abstract class VisualElement : Control, IControlContainer
    {
        readonly List<VisualElement> _logicalChildren = new List<VisualElement>();
        readonly List<IBind> _bindings = new List<IBind>();
        int _bindingAttachmentDepth;
        internal bool IsAttachingBindings => _bindingAttachmentDepth > 0;
        object _dataContext;
        bool _hasLocalDataContext;
        object _inheritedDataContext;
        IBind _dataContextBinding;
        bool _hasExplicitGridPlacement;
        int _gridColumn;
        int _gridRow;
        int _gridColumnSpan = 1;
        int _gridRowSpan = 1;
        ITextMetrics _textMetrics;
        AnimationController _animationController;
        IResourceProvider _resourceProvider;
        public VisualElement Parent { get; private set; }

        // For overlay roots (popups, dropdowns, dialogs) that have no visual parent: the control that owns them, whose
        // styles and resources they inherit, like popup placement target.
        internal VisualElement OverlayOwner { get; set; }

        // The flyout last shown at this element (Flyout.ShowAt): the tree finds it here while it is open.
        internal Acrux.Controls.Primitives.Popup AttachedFlyout { get; set; }

        // The tree this element is the root of, set by VisualTree.
        internal VisualTree RootTree { get; set; }

        /// <summary>
        /// InputElement.Focus: gives this element keyboard focus in the tree that hosts it, through popups
        /// and owned windows. False when it is not in a tree or cannot take focus.
        /// </summary>
        public bool Focus()
        {
            VisualElement root = this;
            while (true)
            {
                VisualElement next = root.Parent ?? root.OverlayOwner ?? (root as Acrux.Controls.Window)?.Owner;
                if (next == null)
                    break;
                root = next;
            }
            return root.RootTree != null && root.RootTree.Focus(this, false);
        }
        protected override IUiInvalidatable InvalidationParent => (IUiInvalidatable)Parent ?? OverlayOwner;
        public IReadOnlyList<VisualElement> LogicalChildren => _logicalChildren;
        public virtual IReadOnlyList<VisualElement> VisualChildren => _logicalChildren;
        protected ITextMetrics TextMetrics => _textMetrics;
        internal ITextMetrics AttachedTextMetrics => _textMetrics;
        internal AnimationController AnimationController => _animationController;
        internal override AnimationController StyleAnimationClock => _animationController;
        protected override Control ResourceParent => Parent ?? OverlayOwner;

        protected override bool TryGetContextResource<T>(string key, out T value)
        {
            IResourceProvider provider = _resourceProvider;
            if (provider != null && provider.TryGetResource(key, out value))
                return true;
            value = default(T);
            return false;
        }

        protected override void OnResourceChanged(string key)
        {
            base.OnResourceChanged(key);
            for (int i = 0; i < _logicalChildren.Count; i++)
                _logicalChildren[i].OnResourceChanged(key);
        }

        public int GridColumn
        {
            get { return _gridColumn; }
            set { SetGridPlacement(value, _gridRow, _gridColumnSpan, _gridRowSpan); }
        }

        public int GridRow
        {
            get { return _gridRow; }
            set { SetGridPlacement(_gridColumn, value, _gridColumnSpan, _gridRowSpan); }
        }

        public int GridColumnSpan
        {
            get { return _gridColumnSpan; }
            set { SetGridPlacement(_gridColumn, _gridRow, value, _gridRowSpan); }
        }

        public int GridRowSpan
        {
            get { return _gridRowSpan; }
            set { SetGridPlacement(_gridColumn, _gridRow, _gridColumnSpan, value); }
        }

        internal bool HasExplicitGridPlacement => _hasExplicitGridPlacement;

        internal void SetGridPlacement(int column, int row, int columnSpan, int rowSpan)
        {
            if (column < 0)
                throw new ArgumentException("Grid column cannot be negative.", nameof(column));
            if (row < 0)
                throw new ArgumentException("Grid row cannot be negative.", nameof(row));
            if (columnSpan < 1)
                throw new ArgumentException("Grid column span must be positive.", nameof(columnSpan));
            if (rowSpan < 1)
                throw new ArgumentException("Grid row span must be positive.", nameof(rowSpan));

            bool changed = !_hasExplicitGridPlacement ||
                           _gridColumn != column || _gridRow != row ||
                           _gridColumnSpan != columnSpan || _gridRowSpan != rowSpan;
            _hasExplicitGridPlacement = true;
            _gridColumn = column;
            _gridRow = row;
            _gridColumnSpan = columnSpan;
            _gridRowSpan = rowSpan;
            if (changed)
                Invalidate(UiInvalidation.Measure);
        }

        internal void AttachTextMetrics(ITextMetrics textMetrics)
        {
            _textMetrics = textMetrics;
            for (int i = 0; i < _logicalChildren.Count; i++)
                _logicalChildren[i].AttachTextMetrics(textMetrics);
        }

        internal void AttachAnimationController(AnimationController controller)
        {
            if (ReferenceEquals(_animationController, controller))
                return;

            if (_animationController != null)
                _animationController.CancelOwner(this, false);

            _animationController = controller;
            RestartStyleAnimations();
            for (int i = 0; i < _logicalChildren.Count; i++)
                _logicalChildren[i].AttachAnimationController(controller);
        }

        public void SetVisible(bool visible)
        {
            Visible = visible;
        }

        public virtual VisualElement SetEnabled(bool enabled)
        {
            Enabled = enabled;
            return this;
        }

        public virtual void SetRect(RectangleF bounds)
        {
            Arrange(bounds);
        }
        bool _isPointerOver;
        bool _isPressed;
        bool _isFocused;
        bool _isFocusWithin;
        bool _isFocusVisible;

        public static readonly RoutedEvent<PointerEventArgs> PointerEnteredEvent =
            RoutedEvent.Register<VisualElement, PointerEventArgs>(nameof(PointerEntered), RoutingStrategies.Direct);
        public static readonly RoutedEvent<PointerEventArgs> PointerExitedEvent =
            RoutedEvent.Register<VisualElement, PointerEventArgs>(nameof(PointerExited), RoutingStrategies.Direct);

        /// <summary>Raised on each element the pointer enters, after IsPointerOver turns on (direct, not bubbled).</summary>
        public event Action<object, PointerEventArgs> PointerEntered;

        /// <summary>Raised on each element the pointer leaves, after IsPointerOver turns off (direct, not bubbled).</summary>
        public event Action<object, PointerEventArgs> PointerExited;

        public bool IsPointerOver
        {
            get { return _isPointerOver; }
            internal set
            {
                if (_isPointerOver == value) return;
                _isPointerOver = value;
                PseudoClasses.Set(PseudoClassNames.PointerOver, value);
                RoutedEvent<PointerEventArgs> routed = value ? PointerEnteredEvent : PointerExitedEvent;
                var e = new PointerEventArgs(routed, this);
                Action<object, PointerEventArgs> handler = value ? PointerEntered : PointerExited;
                if (handler != null)
                    handler(this, e);
                routed.RaiseClassHandlers(this, e);
            }
        }
        public bool IsPressed
        {
            get { return _isPressed; }
            internal set { _isPressed = value; }
        }
        public bool IsFocused
        {
            get { return _isFocused; }
            internal set
            {
                if (_isFocused == value) return;
                _isFocused = value;
                PseudoClasses.Set(PseudoClassNames.Focus, value);
            }
        }
        public bool IsFocusWithin
        {
            get { return _isFocusWithin; }
            internal set
            {
                if (_isFocusWithin == value) return;
                _isFocusWithin = value;
                PseudoClasses.Set(PseudoClassNames.FocusWithin, value);
            }
        }
        public bool IsFocusVisible
        {
            get { return _isFocusVisible; }
            internal set
            {
                if (_isFocusVisible == value) return;
                _isFocusVisible = value;
                PseudoClasses.Set(PseudoClassNames.FocusVisible, value);
            }
        }
        public virtual bool AcceptsKeyboardFocus => false;
        public bool IsEffectivelyEnabled
        {
            get
            {
                for (VisualElement current = this; current != null; current = current.Parent)
                    if (!current.Enabled)
                        return false;
                return true;
            }
        }

        internal override bool IsDisabledState => !IsEffectivelyEnabled;

        public object DataContext
        {
            get { return _dataContext; }
            set
            {
                _hasLocalDataContext = true;
                SetEffectiveDataContext(value);
            }
        }

        public void ClearDataContext()
        {
            if (!_hasLocalDataContext)
                return;
            _hasLocalDataContext = false;
            SetEffectiveDataContext(Parent == null ? null : Parent.DataContext);
        }

        void RefreshContextBindings()
        {
            for (int i = 0; i < _bindings.Count; i++)
                if (_bindings[i].UsesContext)
                    _bindings[i].Refresh();
            for (int i = 0; i < _logicalChildren.Count; i++)
                _logicalChildren[i].RefreshContextBindings();
        }

        public CompiledBinding<TSource, TValue> Bind<TSource, TValue>(
            Func<TSource, TValue> getter,
            Action<TValue> setter,
            string propertyName)
            where TSource : class
        {
            var binding = new CompiledBinding<TSource, TValue>(
                getter, setter, propertyName);
            _bindings.Add(binding);
            ((IBind)binding).Attach(_dataContext);
            return binding;
        }

        public T Add<T>(T child) where T : VisualElement
        {
            return AddChild(child);
        }

        void IControlContainer.AddTemplateChild(Control child)
        {
            VisualElement element = child as VisualElement;
            if (element == null)
                throw new InvalidOperationException(
                    "The Minimap compatibility tree only accepts VisualElement children.");
            AddChild(element);
        }

        public T AddChild<T>(T child) where T : VisualElement
        {
            if (child == null)
                throw new ArgumentNullException(nameof(child));
            if (ReferenceEquals(child, this))
                throw new InvalidOperationException("A visual element cannot contain itself.");
            for (VisualElement parent = this; parent != null; parent = parent.Parent)
            {
                if (ReferenceEquals(parent, child))
                    throw new InvalidOperationException("Adding the child would create a cycle.");
            }

            ValidateChildForAdd(child);

            if (ReferenceEquals(child.Parent, this))
            {
                if (!_logicalChildren.Contains(child))
                {
                    _logicalChildren.Add(child);
                    Invalidate(UiInvalidation.Measure);
                }
                return child;
            }
            if (child.Parent != null)
                child.Parent.RemoveChild(child);

            child.Parent = this;
            child.SetInheritedDataContext(_dataContext);
            if (Acrux.Extensions.ValueConverters.ContextualConversions != 0)
                child.RefreshContextBindings();
            child.AttachTextMetrics(_textMetrics);
            child.AttachAnimationController(_animationController);
            _logicalChildren.Add(child);
            child.OnResourceChanged(null);
            Invalidate(UiInvalidation.Measure);
            return child;
        }

        protected virtual void ValidateChildForAdd(VisualElement child)
        {
        }

        public void AddChildren(IEnumerable<VisualElement> children)
        {
            if (children == null)
                return;
            foreach (VisualElement child in children)
                AddChild(child);
        }

        public virtual bool RemoveChild(VisualElement child)
        {
            if (child == null || !_logicalChildren.Remove(child))
                return false;
            child.AttachAnimationController(null);
            if (ReferenceEquals(child.Parent, this))
            {
                child.Parent = null;
                child.SetInheritedDataContext(null);
                child.OnResourceChanged(null);
            }
            Invalidate(UiInvalidation.Measure);
            return true;
        }

        public virtual void ClearChildren()
        {
            for (int i = 0; i < _logicalChildren.Count; i++)
            {
                VisualElement child = _logicalChildren[i];
                if (child != null)
                    child.AttachAnimationController(null);
                if (child != null && ReferenceEquals(child.Parent, this))
                {
                    child.Parent = null;
                    child.SetInheritedDataContext(null);
                    child.OnResourceChanged(null);
                }
            }
            if (_logicalChildren.Count == 0)
                return;
            _logicalChildren.Clear();
            Invalidate(UiInvalidation.Measure);
        }

        public bool MoveChild(VisualElement child, int index)
        {
            if (child == null || !ReferenceEquals(child.Parent, this))
                return false;
            int current = _logicalChildren.IndexOf(child);
            if (current < 0)
                return false;
            int target = Math.Max(0, Math.Min(index, _logicalChildren.Count - 1));
            if (current == target)
                return false;
            _logicalChildren.RemoveAt(current);
            _logicalChildren.Insert(target, child);
            Invalidate(UiInvalidation.Arrange);
            return true;
        }

        internal void InheritDataContext(object value)
        {
            SetInheritedDataContext(value);
        }

        void SetInheritedDataContext(object value)
        {
            _inheritedDataContext = value;
            if (_dataContextBinding != null)
                _dataContextBinding.Attach(value);
            if (!_hasLocalDataContext)
                SetEffectiveDataContext(value);
        }

        /// <summary>
        /// Binds this element's own DataContext. The binding reads the inherited data context (the
        /// parent's), since the element's own one is what it produces.
        /// </summary>
        public CompiledBinding<TSource, TValue> BindDataContext<TSource, TValue>(Func<TSource, TValue> getter, string propertyName)
            where TSource : class
        {
            // DoNothing (a source the getter does not know) leaves the data context as it is.
            var binding = new CompiledBinding<TSource, TValue>(getter, value =>
            {
                if (!ReferenceEquals(value, Acrux.Data.BindingOperations.DoNothing))
                    DataContext = value;
            }, propertyName);
            _dataContextBinding = binding;
            ((IBind)binding).Attach(_inheritedDataContext);
            return binding;
        }

        void SetEffectiveDataContext(object value)
        {
            if (ReferenceEquals(_dataContext, value))
                return;
            if (_resourceProvider != null)
                _resourceProvider.ResourceChanged -= OnContextResourceChanged;
            _dataContext = value;
            _resourceProvider = Acrux.Toolkit.Server.ExternalInterop.AsResourceProvider(value);
            if (_resourceProvider != null)
                _resourceProvider.ResourceChanged += OnContextResourceChanged;
            _bindingAttachmentDepth++;
            try
            {
                for (int i = 0; i < _bindings.Count; i++)
                    _bindings[i].Attach(value);
            }
            finally
            {
                _bindingAttachmentDepth--;
            }
            for (int i = 0; i < _logicalChildren.Count; i++)
                _logicalChildren[i].SetInheritedDataContext(value);
            OnResourceChanged(null);
        }

        void OnContextResourceChanged(string key)
        {
            // An ancestor with this same provider forwards the notification to
            // its whole subtree, including children with local data contexts.
            for (VisualElement ancestor = Parent; ancestor != null; ancestor = ancestor.Parent)
                if (ReferenceEquals(ancestor._resourceProvider, _resourceProvider))
                    return;
            OnResourceChanged(key);
        }

        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            float width = 0f;
            float height = 0f;
            IReadOnlyList<VisualElement> children = VisualChildren;
            for (int i = 0; i < children.Count; i++)
            {
                VisualElement child = children[i];
                Vector2 desired = child.Measure(availableSize);
                width = Math.Max(width, Math.Max(0f, desired.X));
                height = Math.Max(height, Math.Max(0f, desired.Y));
            }
            return new Vector2(width, height);
        }

        internal VisualElement HitTest(Vector2 point)
        {
            EnsureStyleApplied();
            if (!Visible || !Enabled || !Bounds.Contains(point))
                return null;

            IReadOnlyList<VisualElement> children = VisualChildren;
            Vector2 childPoint = MapPointToChildren(point);
            for (int i = children.Count - 1; i >= 0; i--)
            {
                VisualElement hit = children[i].HitTest(childPoint);
                if (hit != null)
                    return hit;
            }

            return HitTestSelf(point) ? this : null;
        }

        protected virtual bool HitTestSelf(Vector2 point)
        {
            return false;
        }

        internal virtual Vector2 MapPointToChildren(Vector2 point)
        {
            return point;
        }

        internal void DrawTree(RenderContext context)
        {
            EnsureStyleApplied();
            if (!Visible)
                return;

            float opacity = Opacity;
            IDisposable fade = opacity < 1f ? context.PushOpacity(opacity) : null;
            IDisposable transform = PushRenderTransform(context);
            IDisposable clip = ClipToBounds ? context.PushClip(Bounds) : null;
            try
            {
                DrawSelfAndChildren(context);
            }
            finally
            {
                if (clip != null)
                    clip.Dispose();
                if (transform != null)
                    transform.Dispose();
                if (fade != null)
                    fade.Dispose();
            }
        }

        // Translates the element and its subtree, or scales them around its center, default RenderTransformOrigin. LCD
        // sprites cannot be stretched unevenly, so a non-uniform scale uses its smaller factor.
        IDisposable PushRenderTransform(RenderContext context)
        {
            Transform transform = RenderTransform;
            if (transform == null)
                return null;
            Vector2 offset = transform.Offset;
            if (offset != Vector2.Zero)
                return context.PushTranslate(offset);
            Vector2 factor = transform.ScaleFactor;
            float scale = Math.Min(factor.X, factor.Y);
            return Math.Abs(scale - 1f) < 0.0001f ? null : context.PushScale(Bounds.Center, Math.Max(0f, scale));
        }

        void DrawSelfAndChildren(RenderContext context)
        {
            DrawSelf(context);
            ClearInvalidation(UiInvalidation.Render | UiInvalidation.Style);
            IReadOnlyList<VisualElement> children = VisualChildren;
            for (int i = 0; i < children.Count; i++)
            {
                VisualElement child = children[i];
                IDisposable childDrawScope = BeginChildDraw(context, child);
                try
                {
                    // Like renderer, skip subtrees wholly outside the clip: a scrolled list would otherwise
                    // emit sprites for every off-screen row. Rotated or scaled children may draw beyond their bounds.
                    if (child.RenderTransform == null && context.IsOutsideClip(child.Bounds))
                        child.ClearInvalidation(UiInvalidation.Render);
                    else
                        children[i].DrawTree(context);
                }
                finally
                {
                    if (childDrawScope != null)
                        childDrawScope.Dispose();
                }
            }
        }

        protected virtual IDisposable BeginChildDraw(
            RenderContext context,
            VisualElement child)
        {
            return null;
        }

        protected virtual void DrawSelf(RenderContext context)
        {
        }

        // Whether a point lies on this overlay's surface, so a click there must not light-dismiss it.
        internal virtual bool OverlayContains(Vector2 point)
        {
            return Visible && Bounds.Contains(point);
        }

        internal virtual void PointerPressed(Vector2 point)
        {
        }

        internal virtual void PointerMoved(Vector2 point, Vector2 delta)
        {
        }

        internal virtual void PointerReleased(Vector2 point, bool clicked)
        {
        }

        internal virtual void PointerCaptureLost()
        {
        }

        internal virtual void PointerHovered(Vector2 point)
        {
        }

        internal virtual PointerCursor GetPointerCursor(Vector2 point)
        {
            return Cursor;
        }

        internal virtual bool PointerScrolled(Vector2 point, int delta)
        {
            return false;
        }

        internal virtual void KeyboardInput(IInputSource input)
        {
        }

        internal virtual void MiddlePointerPressed(Vector2 point)
        {
        }

        internal virtual void MiddlePointerMoved(Vector2 point, Vector2 delta)
        {
        }

        internal virtual void MiddlePointerReleased(Vector2 point)
        {
        }

        internal virtual void SecondaryPointerPressed(Vector2 point)
        {
        }

        internal virtual void SecondaryPointerMoved(Vector2 point, Vector2 delta)
        {
        }

        internal virtual void SecondaryPointerReleased(Vector2 point, bool clicked)
        {
        }

        // A secondary click on this element is its own action, so it does not open a context menu.
        internal virtual bool HandlesSecondaryClick => false;

        /// <summary>
        /// Raised on a secondary (right) click on this element or anything inside it,
        /// bubbling up. The nearest element with a handler
        /// takes the click, and no context menu opens.
        /// </summary>
        public Action ContextRequested { get; set; }

        public override void Dispose()
        {
            var reloadable = this as Acrux.Markup.IHotReloadableView;
            if (reloadable != null)
                Acrux.Markup.HotReload.Untrack(reloadable);
            AttachAnimationController(null);
            if (_resourceProvider != null)
                _resourceProvider.ResourceChanged -= OnContextResourceChanged;
            _resourceProvider = null;
            for (int i = 0; i < _bindings.Count; i++)
                _bindings[i].Dispose();
            _bindings.Clear();
            _dataContext = null;
            for (int i = 0; i < _logicalChildren.Count; i++)
                _logicalChildren[i].Dispose();
            _logicalChildren.Clear();
            Parent = null;
            base.Dispose();
        }
    }
}
