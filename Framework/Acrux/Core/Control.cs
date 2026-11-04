using System;
using System.Collections.Generic;
using Acrux.Core.Input;
using Acrux.Core.Invalidation;
using Acrux.Core.PropertySystem;
using Acrux.Styling;
using VRageMath;

namespace Acrux.Core
{
    /// <summary>Base styled control with pseudoclass and property state.</summary>
    public abstract class Control : FrameworkElement
    {
        public static readonly StyledProperty<float> WidthProperty =
            StyledProperty.Register<Control, float>(
                nameof(Width), float.NaN, UiInvalidation.Measure);
        public static readonly StyledProperty<float> HeightProperty =
            StyledProperty.Register<Control, float>(
                nameof(Height), float.NaN, UiInvalidation.Measure);
        public static readonly StyledProperty<float> MinWidthProperty =
            StyledProperty.Register<Control, float>(
                nameof(MinWidth), 0f, UiInvalidation.Measure);
        public static readonly StyledProperty<float> MinHeightProperty =
            StyledProperty.Register<Control, float>(
                nameof(MinHeight), 0f, UiInvalidation.Measure);
        public static readonly StyledProperty<float> MaxWidthProperty =
            StyledProperty.Register<Control, float>(
                nameof(MaxWidth), float.PositiveInfinity, UiInvalidation.Measure);
        public static readonly StyledProperty<float> MaxHeightProperty =
            StyledProperty.Register<Control, float>(
                nameof(MaxHeight), float.PositiveInfinity, UiInvalidation.Measure);
        public static readonly StyledProperty<Vector4> MarginProperty =
            StyledProperty.Register<Control, Vector4>(
                nameof(Margin), Vector4.Zero, UiInvalidation.Measure);
        public static readonly StyledProperty<HorizontalAlignment> HorizontalAlignmentProperty =
            StyledProperty.Register<Control, HorizontalAlignment>(
                nameof(HorizontalAlignment), HorizontalAlignment.Stretch, UiInvalidation.Arrange);
        public static readonly StyledProperty<VerticalAlignment> VerticalAlignmentProperty =
            StyledProperty.Register<Control, VerticalAlignment>(
                nameof(VerticalAlignment), VerticalAlignment.Stretch, UiInvalidation.Arrange);
        public static readonly StyledProperty<bool> ClipToBoundsProperty =
            StyledProperty.Register<Control, bool>(
                nameof(ClipToBounds), false, UiInvalidation.Render);
        /// <summary>Visual.Opacity: multiplies the alpha of the element and its whole subtree (0-1).</summary>
        public static readonly StyledProperty<float> OpacityProperty =
            StyledProperty.Register<Control, float>(
                nameof(Opacity), 1f, UiInvalidation.Render);
        public static readonly StyledProperty<BackgroundBrush> OpacityMaskProperty =
            StyledProperty.Register<Control, BackgroundBrush>(
                nameof(OpacityMask), null, UiInvalidation.Render);
        public static readonly StyledProperty<Transform> RenderTransformProperty =
            StyledProperty.Register<Control, Transform>(
                nameof(RenderTransform), null, UiInvalidation.Render);
        public static readonly StyledProperty<PointerCursor> CursorProperty =
            StyledProperty.Register<Control, PointerCursor>(
                nameof(Cursor), PointerCursor.Inherit, UiInvalidation.Render);

        public static readonly StyledProperty<Acrux.Controls.ContextMenu> ContextMenuProperty =
            StyledProperty.Register<Control, Acrux.Controls.ContextMenu>(nameof(ContextMenu), null, UiInvalidation.None);

        static Control() { }

        readonly PropertyStore _properties = new PropertyStore();
        readonly List<IDisposable> _lifetimes = new List<IDisposable>();
        readonly Dictionary<string, UiInvalidation> _dynamicResources =
            new Dictionary<string, UiInvalidation>(StringComparer.Ordinal);
        ResourceDictionary _resources;
        string _styleClass = "default";
        StyleSheet _styles;
        StyleSheet _resolvedStyles;
        StyleSheet _resolvedInheritedStyles;
        StyleSheet _resolvedLocalStyles;
        StyleSheet _appliedStyles;
        string _appliedStyleClass;
        int _appliedStyleVersion = -1;
        readonly PseudoClassCollection _pseudoClasses;
        int _pseudoClassVersion;
        int _appliedPseudoClassVersion = -1;
        // The animations of the styles matching now, and those running: one runs while its style keeps matching.
        List<Acrux.Animation.StyleAnimation> _matchedAnimations;
        List<Acrux.Animation.StyleAnimationInstance> _styleAnimations;

        protected Control()
        {
            _pseudoClasses = new PseudoClassCollection(OnPseudoClassesChanged);
        }

        public ResourceDictionary Resources
        {
            get
            {
                if (_resources == null)
                {
                    _resources = new ResourceDictionary();
                    _resources.Changed += OnLocalResourceChanged;
                }
                return _resources;
            }
        }

        /// <summary>
        /// Optional identifier used by the nearest control-template namescope.
        /// Names are local to a single template instance.
        /// </summary>
        public string Name { get; set; }

        // Identity used by style and template-property caches. A toolkit object that backs a client subclass reports
        // the client type, so different client controls never share cache entries.
        internal object StyleKey
        {
            get
            {
                var remote = this as Acrux.Toolkit.Server.IRemotePeer;
                return remote != null && remote.RemotePeer != null ? remote.RemotePeer.GetType() : (object)GetType();
            }
        }
        /// <summary>Same as <see cref="Visible"/>.</summary>
        public bool IsVisible { get { return Visible; } set { Visible = value; } }

        /// <summary>The control whose template created this control.</summary>
        public TemplatedControl TemplatedParent { get; internal set; }

        protected PseudoClassCollection PseudoClasses => _pseudoClasses;

        public override bool Enabled
        {
            get { return base.Enabled; }
            set
            {
                if (base.Enabled == value)
                    return;
                base.Enabled = value;
                PseudoClasses.Set(PseudoClassNames.Disabled, IsDisabledState);
            }
        }

        public override float? Width
        {
            get
            {
                float value = GetValue(WidthProperty);
                return float.IsNaN(value) ? (float?)null : value;
            }
            set { SetValue(WidthProperty, value.HasValue ? value.Value : float.NaN); }
        }

        public override float? Height
        {
            get
            {
                float value = GetValue(HeightProperty);
                return float.IsNaN(value) ? (float?)null : value;
            }
            set { SetValue(HeightProperty, value.HasValue ? value.Value : float.NaN); }
        }

        public override float MinWidth
        {
            get { return GetValue(MinWidthProperty); }
            set { SetValue(MinWidthProperty, Math.Max(0f, value)); }
        }

        public override float MinHeight
        {
            get { return GetValue(MinHeightProperty); }
            set { SetValue(MinHeightProperty, Math.Max(0f, value)); }
        }

        public override float MaxWidth
        {
            get { return GetValue(MaxWidthProperty); }
            set
            {
                SetValue(MaxWidthProperty, float.IsNaN(value)
                    ? float.PositiveInfinity
                    : Math.Max(0f, value));
            }
        }

        public override float MaxHeight
        {
            get { return GetValue(MaxHeightProperty); }
            set
            {
                SetValue(MaxHeightProperty, float.IsNaN(value)
                    ? float.PositiveInfinity
                    : Math.Max(0f, value));
            }
        }

        /// <summary>Visual.Opacity: multiplies the alpha of the element and its whole subtree (0-1).</summary>
        public float Opacity
        {
            get { return GetValue(OpacityProperty); }
            set { SetValue(OpacityProperty, MathHelper.Clamp(value, 0f, 1f)); }
        }

        public BackgroundBrush OpacityMask
        {
            get { return GetValue(OpacityMaskProperty); }
            set { SetValue(OpacityMaskProperty, value); }
        }

        public Transform RenderTransform
        {
            get { return GetValue(RenderTransformProperty); }
            set { SetValue(RenderTransformProperty, value); }
        }

        public bool ClipToBounds
        {
            get { return GetValue(ClipToBoundsProperty); }
            set { SetValue(ClipToBoundsProperty, value); }
        }

        public override Vector4 Margin
        {
            get { return GetValue(MarginProperty); }
            set { SetValue(MarginProperty, value); }
        }

        public override HorizontalAlignment HorizontalAlignment
        {
            get { return GetValue(HorizontalAlignmentProperty); }
            set { SetValue(HorizontalAlignmentProperty, value); }
        }

        public override VerticalAlignment VerticalAlignment
        {
            get { return GetValue(VerticalAlignmentProperty); }
            set { SetValue(VerticalAlignmentProperty, value); }
        }

        public PointerCursor Cursor
        {
            get { return GetValue(CursorProperty); }
            set { SetValue(CursorProperty, value); }
        }

        public Acrux.Controls.ContextMenu ContextMenu
        {
            get { return GetValue(ContextMenuProperty); }
            set { SetValue(ContextMenuProperty, value); }
        }

        public string StyleClass
        {
            get { return _styleClass; }
            set
            {
                string next = string.IsNullOrWhiteSpace(value) ? "default" : value.Trim();
                if (string.Equals(_styleClass, next, StringComparison.Ordinal))
                    return;
                _styleClass = next;
                InvalidateDescendantSelectors();
                Invalidate(UiInvalidation.Style | UiInvalidation.Measure |
                           UiInvalidation.Arrange | UiInvalidation.Render);
            }
        }
        
        public StyleSheet Styles
        {
            get { return _styles; }
            set
            {
                if (ReferenceEquals(_styles, value))
                    return;
                _styles = value;
                Invalidate(UiInvalidation.Style | UiInvalidation.Measure |
                           UiInvalidation.Arrange | UiInvalidation.Render);
            }
        }

        protected StyleSheet ResolveStyleSheet()
        {
            VisualElement visual = this as VisualElement;
            VisualElement styleParent = visual == null ? null : visual.Parent ?? visual.OverlayOwner;
            StyleSheet inherited = styleParent == null
                ? StyleSheet.Application
                : styleParent.ResolveStyleSheet();
            if (_styles == null)
                return inherited;
            if (!ReferenceEquals(_resolvedInheritedStyles, inherited) ||
                !ReferenceEquals(_resolvedLocalStyles, _styles))
            {
                _resolvedInheritedStyles = inherited;
                _resolvedLocalStyles = _styles;
                _resolvedStyles = StyleSheet.Cascade(inherited, _styles);
            }
            return _resolvedStyles;
        }

        internal void EnsureStyleApplied()
        {
            UpdatePseudoClasses();
            StyleSheet styles = ResolveStyleSheet();
            if (ReferenceEquals(styles, _appliedStyles) && string.Equals(
                    StyleClass,
                    _appliedStyleClass,
                    StringComparison.Ordinal) &&
                _appliedStyleVersion == styles.Version &&
                _appliedPseudoClassVersion == _pseudoClassVersion)
            {
                TemplatedControl unchangedTemplateOwner = this as TemplatedControl;
                if (unchangedTemplateOwner != null)
                    unchangedTemplateOwner.EnsureTemplateApplied();
                return;
            }
            BeginStyleBatch();
            try
            {
                if (_appliedStyles != null)
                    _appliedStyles.Reset(this);
                styles.Apply(this);
            }
            finally
            {
                EndStyleBatch();
            }
            _appliedStyles = styles;
            _appliedStyleClass = StyleClass;
            _appliedStyleVersion = styles.Version;
            _appliedPseudoClassVersion = _pseudoClassVersion;
            SyncStyleAnimations();

            TemplatedControl templateOwner = this as TemplatedControl;
            if (templateOwner != null)
                templateOwner.EnsureTemplateApplied();
        }


        /// <summary>Returns whether a pseudoclass is currently active.</summary>
        public bool HasPseudoClass(string pseudoClass)
        {
            return _pseudoClasses.Contains(pseudoClass);
        }

        internal virtual bool IsDisabledState => !Enabled;

        protected virtual void UpdatePseudoClasses()
        {
            PseudoClasses.Set(PseudoClassNames.Disabled, IsDisabledState);
        }

        void OnPseudoClassesChanged(string pseudoClass)
        {
            Action<Control, string> trace = ChangeTrace;
            if (trace != null)
                trace(this, "pseudo-classes now {" + _pseudoClasses + "}");
            _pseudoClassVersion++;
            // Descendants derive their own :disabled from ancestors, so it always reaches them.
            if (string.Equals(pseudoClass, PseudoClassNames.Disabled, StringComparison.OrdinalIgnoreCase) ||
                StyleSheet.IsAncestorPseudoClass(this, pseudoClass))
                InvalidateDescendantSelectors();
            Invalidate(UiInvalidation.Style | UiInvalidation.Render);
        }

        // A descendant selector can depend on any ancestor's class or state.
        // Clear only its match cache; style batches still preserve unchanged values/templates.
        void InvalidateDescendantSelectors()
        {
            var visual = this as VisualElement;
            if (visual == null) return;
            foreach (var child in visual.LogicalChildren)
            {
                ((Control)child)._appliedPseudoClassVersion = -1;
                child.Invalidate(UiInvalidation.Style | UiInvalidation.Render);
                ((Control)child).InvalidateDescendantSelectors();
            }
        }

        public T GetValue<T>(StyledProperty<T> property)
        {
            if (property == null)
                throw new ArgumentNullException(nameof(property));
            return _properties.GetValue(property);
        }

        public void SetValue<T>(StyledProperty<T> property, T value)
        {
            if (property == null)
                throw new ArgumentNullException(nameof(property));
            if (!_properties.SetValue(property, value))
                return;
            Invalidate(property.Invalidation);
            OnPropertyChanged(property);
        }

        internal T GetBaseValue<T>(StyledProperty<T> property)
        {
            return _properties.GetBaseValue(property);
        }

        internal bool SetAnimatedValue<T>(StyledProperty<T> property, T value)
        {
            if (!_properties.SetAnimatedValue(property, value))
                return false;
            Invalidate(property.Invalidation);
            OnPropertyChanged(property);
            return true;
        }

        internal bool ClearAnimatedValue<T>(StyledProperty<T> property)
        {
            if (!_properties.ClearAnimatedValue(property))
                return false;
            Invalidate(property.Invalidation);
            OnPropertyChanged(property);
            return true;
        }

        internal virtual Acrux.Animation.AnimationController StyleAnimationClock => null;

        internal void BeginStyleAnimationMatch()
        {
            if (_matchedAnimations != null)
                _matchedAnimations.Clear();
        }

        internal void MatchStyleAnimations(IList<Acrux.Animation.StyleAnimation> animations)
        {
            if (_matchedAnimations == null)
                _matchedAnimations = new List<Acrux.Animation.StyleAnimation>();
            for (int i = 0; i < animations.Count; i++)
                if (!_matchedAnimations.Contains(animations[i]))
                    _matchedAnimations.Add(animations[i]);
        }

        void SyncStyleAnimations()
        {
            int matched = _matchedAnimations == null ? 0 : _matchedAnimations.Count;
            if (matched == 0 && (_styleAnimations == null || _styleAnimations.Count == 0))
                return;
            if (_styleAnimations == null)
                _styleAnimations = new List<Acrux.Animation.StyleAnimationInstance>();
            for (int i = _styleAnimations.Count - 1; i >= 0; i--)
            {
                if (matched > 0 && _matchedAnimations.Contains(_styleAnimations[i].Definition))
                    continue;
                _styleAnimations[i].Stop();
                _styleAnimations.RemoveAt(i);
            }
            for (int i = 0; i < matched; i++)
            {
                Acrux.Animation.StyleAnimation definition = _matchedAnimations[i];
                if (FindStyleAnimation(definition) != null)
                    continue;
                var instance = new Acrux.Animation.StyleAnimationInstance(definition, this);
                _styleAnimations.Add(instance);
                instance.Start(StyleAnimationClock);
            }
        }

        Acrux.Animation.StyleAnimationInstance FindStyleAnimation(Acrux.Animation.StyleAnimation definition)
        {
            for (int i = 0; i < _styleAnimations.Count; i++)
                if (ReferenceEquals(_styleAnimations[i].Definition, definition))
                    return _styleAnimations[i];
            return null;
        }

        // A new clock (attached to a tree, or detached from it) starts the style animations over.
        internal void RestartStyleAnimations()
        {
            if (_styleAnimations == null)
                return;
            for (int i = 0; i < _styleAnimations.Count; i++)
            {
                _styleAnimations[i].Stop();
                _styleAnimations[i].Start(StyleAnimationClock);
            }
        }

        public void ClearValue<T>(StyledProperty<T> property)
        {
            if (property == null)
                throw new ArgumentNullException(nameof(property));
            if (!_properties.ClearValue(property))
                return;
            Invalidate(property.Invalidation);
            OnPropertyChanged(property);
        }

        internal void SetStyleValue<T>(StyledProperty<T> property, Func<T> provider)
        {
            if (property == null)
                throw new ArgumentNullException(nameof(property));
            if (provider == null)
                throw new ArgumentNullException(nameof(provider));
            if (_styleBatch != null)
            {
                CaptureStyleValue(property);
                _properties.SetStyleValue(property, provider);
                return;
            }
            _properties.SetStyleValue(property, provider);
            Invalidate(property.Invalidation);
            OnPropertyChanged(property);
        }

        // Re-applying styles (on every pseudo-class change) clears and re-sets each style value. Inside a batch
        // only properties whose effective value differs afterwards are invalidated and reported, so an unchanged
        // setter does not rebuild textures or templates.
        Dictionary<IStyledProperty, object> _styleBatch;
        Dictionary<IStyledProperty, object> _styleBatchBuffer;

        void BeginStyleBatch()
        {
            _styleBatch = _styleBatchBuffer ?? new Dictionary<IStyledProperty, object>();
            _styleBatchBuffer = null;
        }

        void CaptureStyleValue(IStyledProperty property)
        {
            if (!_styleBatch.ContainsKey(property))
                _styleBatch.Add(property, property.GetValueObject(this));
        }

        void EndStyleBatch()
        {
            Dictionary<IStyledProperty, object> batch = _styleBatch;
            _styleBatch = null;
            if (batch == null)
                return;
            foreach (KeyValuePair<IStyledProperty, object> entry in batch)
            {
                if (Equals(entry.Value, entry.Key.GetValueObject(this)))
                    continue;
                Invalidate(entry.Key.Invalidation);
                OnPropertyChanged(entry.Key);
            }
            batch.Clear();
            _styleBatchBuffer = batch;
        }

        public void SetDynamicResource<T>(StyledProperty<T> property, string key)
        {
            if (property == null)
                throw new ArgumentNullException(nameof(property));
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("A dynamic resource key is required.", nameof(key));
            _properties.SetDynamicValue(property, () => ResolveDynamicResource(key, property.DefaultValue));
            WatchDynamicResource(key, property.Invalidation);
            Invalidate(property.Invalidation);
            OnPropertyChanged(property);
        }

        public void WatchDynamicResource(string key, UiInvalidation invalidation)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("A dynamic resource key is required.", nameof(key));
            UiInvalidation current;
            _dynamicResources.TryGetValue(key, out current);
            _dynamicResources[key] = current | invalidation;
        }

        public T ResolveResource<T>(string key)
        {
            T value;
            if (TryFindResource(key, out value))
                return value;
            // A missing resource degrades to the default value with a single log line instead of failing the view.
            Acrux.Toolkit.Server.ExternalInterop.WarnOnce("Unknown resource '" + key + "' (" + typeof(T).FullName + ") requested by " + GetType().Name + ".");
            return default(T);
        }

        internal T ResolveDynamicResource<T>(string key, T fallback)
        {
            T value;
            if (TryFindResource(key, out value)) return value;
            Color color;
            if (typeof(T) == typeof(BackgroundBrush) && TryFindResource(key, out color))
                return (T)(object)new SolidColorBrush(color);
            float number;
            if (typeof(T) == typeof(Vector4) && TryFindResource(key, out number))
                return (T)(object)new Vector4(number);
            // A detached control can temporarily lose its inherited resource.
            // An unresolved dynamic value must not make reparenting throw.
            return fallback;
        }

        public bool TryFindResource<T>(string key, out T value)
        {
            Control root = this;
            for (Control current = this; current != null; current = current.ResourceParent)
            {
                if (current.TryGetLocalResource(key, out value) ||
                    current.TryGetContextResource(key, out value))
                    return true;
                root = current;
            }

            StyleSheet styles = ResolveStyleSheet();
            if (styles.TryGetResource(key, out value))
                return true;
            // Last, what the window's platform supplies (e.g. the game's UI opacities in screen space): anything the
            // app, its views or its styles define wins over it.
            var window = root as Acrux.Controls.Window;
            if (window != null && window.TryGetPlatformResource(key, out value))
                return true;
            value = default(T);
            return false;
        }

        protected virtual Control ResourceParent => null;
        internal event Action<string> ResourcesChanged;

        protected virtual void OnResourceChanged(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                // Parent/resource-context changes can change ancestor selector matches.
                _appliedPseudoClassVersion = -1;
                Invalidate(UiInvalidation.Style);
            }
            UiInvalidation invalidation = UiInvalidation.None;
            if (string.IsNullOrEmpty(key))
            {
                foreach (UiInvalidation value in _dynamicResources.Values)
                    invalidation |= value;
            }
            else
            {
                _dynamicResources.TryGetValue(key, out invalidation);
            }
            if (invalidation != UiInvalidation.None)
                Invalidate(invalidation);
            var handler = ResourcesChanged;
            if (handler != null) handler(key);
        }

        protected virtual bool TryGetContextResource<T>(string key, out T value)
        {
            value = default(T);
            return false;
        }

        bool TryGetLocalResource<T>(string key, out T value)
        {
            if (_resources == null)
            {
                value = default(T);
                return false;
            }
            Func<T> provider;
            if (_resources.TryGetValue(key, out provider))
            {
                value = provider();
                return true;
            }
            return _resources.TryGetValue(key, out value);
        }

        void OnLocalResourceChanged(string key)
        {
            OnResourceChanged(key);
        }

        internal void ClearStyleValues()
        {
            if (_styleBatch != null)
            {
                _properties.ClearStyleValues(CaptureStyleValue);
                return;
            }
            UiInvalidation invalidation = _properties.ClearStyleValues(OnPropertyChanged);
            if (invalidation != UiInvalidation.None)
                Invalidate(invalidation);
        }

        /// <summary>
        /// Diagnostics hook raised for every styled-property and pseudo-class change on any control, with a short
        /// description. Null outside diagnostics, so it costs one null check.
        /// </summary>
        public static Action<Control, string> ChangeTrace;

        protected virtual void OnPropertyChanged(IStyledProperty property)
        {
            Action<Control, string> trace = ChangeTrace;
            if (trace != null)
                trace(this, StyleSheet.IsSelectorProperty(property.Name) ? property.Name + " [selector]" : property.Name);
            if (StyleSheet.IsSelectorProperty(property.Name))
            {
                _appliedPseudoClassVersion = -1;
                Invalidate(UiInvalidation.Style);
            }
            if (property == ContextMenuProperty && ContextMenu != null)
                ContextMenu.OverlayOwner = this as VisualElement;
            Action<Control, IStyledProperty> changed = StyledPropertyChanged;
            if (changed != null)
                changed(this, property);
        }

        internal event Action<Control, IStyledProperty> StyledPropertyChanged;

        /// <summary>SizeChanged; SE's script whitelist forbids EventHandler&lt;T&gt;, so handlers are (sender, e) actions.</summary>
        public event Action<object, Acrux.Controls.SizeChangedEventArgs> SizeChanged;

        internal void RaiseSizeChanged(Vector2 previousSize, Vector2 newSize)
        {
            Action<object, Acrux.Controls.SizeChangedEventArgs> changed = SizeChanged;
            if (changed != null)
                changed(this, new Acrux.Controls.SizeChangedEventArgs(this, previousSize, newSize));
        }

        internal void RegisterLifetime(IDisposable lifetime)
        {
            if (lifetime != null)
                _lifetimes.Add(lifetime);
        }

        public override void Dispose()
        {
            if (_resources != null)
                _resources.Changed -= OnLocalResourceChanged;
            for (int i = 0; i < _lifetimes.Count; i++)
                _lifetimes[i].Dispose();
            _lifetimes.Clear();
            StyledPropertyChanged = null;
            ResourcesChanged = null;
            base.Dispose();
        }
    }
}
