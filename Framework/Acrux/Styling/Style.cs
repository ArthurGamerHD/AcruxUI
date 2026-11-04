using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Acrux.Core;
using Acrux.Core.PropertySystem;
using Acrux.Markup;
using VRageMath;

namespace Acrux.Styling
{
    public sealed class Setter
    {
        public Setter(string property, object value)
        {
            if (string.IsNullOrWhiteSpace(property))
                throw new ArgumentException("A setter requires a property name.", nameof(property));
            Property = property.Trim();
            Value = value ?? string.Empty;
        }

        public string Property { get; }
        public object Value { get; }
    }

    public sealed class Style
    {
        readonly List<Setter> _setters = new List<Setter>();

        public Style(string selector)
        {
            if (string.IsNullOrWhiteSpace(selector))
                throw new ArgumentException("A style requires a selector.", nameof(selector));
            Selector = selector.Trim();
        }

        public string Selector { get; }
        public IList<Setter> Setters => _setters;
        internal List<Acrux.Animation.StyleAnimation> Animations { get; } = new List<Acrux.Animation.StyleAnimation>();
    }

    /// <summary>
    /// Generic runtime-parsed styles. Entries whose control metadata has not been
    /// registered remain cached and resolve when StyledProperty.Register publishes it.
    /// </summary>
    public sealed class StyleSheet
    {
        static readonly StyleSheet EmptySheet = new StyleSheet(
            StylePropertyRegistry.Global,
            new ResourceDictionary(),
            new List<Style>());
        static StyleSheet _application = EmptySheet;

        readonly StylePropertyRegistry _registry;
        readonly ResourceDictionary _resources;
        readonly List<StyleEntry> _styles;
        readonly StyleSheet _inheritedStyles;
        readonly StyleSheet _localStyles;
        bool _attached;
        int _version;
        // Shared by every screen (the application sheet above all) and read by screens rendering in parallel: a
        // published snapshot is read without locking, misses add to a copy under the lock.
        sealed class CandidateIndex
        {
            public int Version;
            public int Generation;
            public Dictionary<object, int[]> ByType;
        }

        volatile CandidateIndex _candidates = new CandidateIndex
            { Version = -1, Generation = -1, ByType = new Dictionary<object, int[]>() };
        readonly object _candidatesLock = new object();

        StyleSheet(
            StylePropertyRegistry registry,
            ResourceDictionary resources,
            List<Style> styles)
        {
            GeneratedTypeCatalog.EnsureStyledProperties();
            _registry = registry;
            _resources = resources;
            _styles = new List<StyleEntry>(styles.Count);
            foreach (var style in styles)
            {
                Selector selector = Selector.Parse(style.Selector);
                _styles.Add(new StyleEntry(style, selector));
            }
            ResolvePending(null);
        }

        StyleSheet(StyleSheet inheritedStyles, StyleSheet localStyles)
        {
            _inheritedStyles = inheritedStyles;
            _localStyles = localStyles;
            _registry = localStyles._registry;
            _resources = localStyles._resources;
            _styles = new List<StyleEntry>();
        }

        public static StyleSheet Empty => EmptySheet;

        public static StyleSheet Application
        {
            get { return _application; }
            set
            {
                StyleSheet next = value ?? EmptySheet;
                if (ReferenceEquals(_application, next))
                    return;
                _application.Detach();
                _application = next;
                _application.Attach();
            }
        }

        /// <summary>
        /// Reports a style source that could not be read (e.g. a file missing from a deployment). The source is
        /// skipped so the UI still loads, unstyled where needed, instead of failing the view that included it.
        /// </summary>
        public static Action<string, Exception> SourceLoadFailed { get; set; }

        /// <summary>
        /// Application asset loader used by StyleInclude declarations in views.
        /// Hosts configure this once before constructing XAML views.
        /// </summary>
        public static Func<string, string> SourceLoader
        {
            get { return _sourceLoader; }
            set
            {
                _sourceLoader = value;
                ClearSourceCache();
            }
        }

        /// <summary>
        /// When true (the default), each style source is read and parsed once and reused by every
        /// later load. Disable it while editing styles so every new view reads them again.
        /// </summary>
        public static bool CacheSources
        {
            get { return _cacheSources; }
            set
            {
                _cacheSources = value;
                if (!value)
                    ClearSourceCache();
            }
        }

        public static void ClearSourceCache()
        {
            lock (SourceCache)
            {
                SourceCache.Clear();
                InlineCache.Clear();
                _cachedSourceLoader = null;
            }
        }

        // A view's own Styles documents (LoadInline), by exact text: each is parsed once, its includes expanded.
        static readonly Dictionary<string, List<Style>> InlineCache = new Dictionary<string, List<Style>>(StringComparer.Ordinal);

        static readonly Dictionary<string, List<Style>> SourceCache =
            new Dictionary<string, List<Style>>(StringComparer.OrdinalIgnoreCase);
        static Func<string, string> _sourceLoader;
        static Func<string, string> _cachedSourceLoader;
        static bool _cacheSources = true;

        internal int Version
        {
            get
            {
                if (_inheritedStyles == null)
                    return _version;
                unchecked
                {
                    return (_inheritedStyles.Version * 397) ^ _localStyles.Version;
                }
            }
        }

        internal static StyleSheet Cascade(StyleSheet inheritedStyles, StyleSheet localStyles)
        {
            if (localStyles == null || ReferenceEquals(localStyles, EmptySheet))
                return inheritedStyles ?? EmptySheet;
            if (inheritedStyles == null || ReferenceEquals(inheritedStyles, EmptySheet) ||
                ReferenceEquals(inheritedStyles, localStyles))
                return localStyles;
            return new StyleSheet(inheritedStyles, localStyles);
        }

        internal bool TryGetResource<T>(string key, out T value)
        {
            if (_localStyles != null)
            {
                if (_localStyles.TryGetResource(key, out value))
                    return true;
                return _inheritedStyles.TryGetResource(key, out value);
            }
            Func<T> provider;
            if (_resources.TryGetValue(key, out provider))
            {
                value = provider();
                return true;
            }
            return _resources.TryGetValue(key, out value);
        }

        public int PendingStyleCount
        {
            get
            {
                if (_localStyles != null)
                    return _inheritedStyles.PendingStyleCount + _localStyles.PendingStyleCount;
                int count = 0;
                for (int i = 0; i < _styles.Count; i++)
                    if (!_styles[i].IsResolved)
                        count++;
                return count;
            }
        }

        public static StyleSheet Load(
            string xml,
            ResourceDictionary resources = null)
        {
            return Load(xml, StylePropertyRegistry.Global, resources);
        }

        public static StyleSheet Load(
            string xml,
            StylePropertyRegistry registry,
            ResourceDictionary resources = null)
        {
            if (string.IsNullOrWhiteSpace(xml))
                throw new ArgumentException("Style XML cannot be empty.", nameof(xml));
            if (registry == null)
                throw new ArgumentNullException(nameof(registry));
            return new StyleSheet(
                registry,
                resources ?? new ResourceDictionary(),
                new StyleParser(xml).Parse());
        }

        public static StyleSheet Load(
            IEnumerable<string> xmlDocuments,
            ResourceDictionary resources = null)
        {
            return Load(xmlDocuments, StylePropertyRegistry.Global, resources);
        }

        public static StyleSheet Load(
            IEnumerable<string> xmlDocuments,
            StylePropertyRegistry registry,
            ResourceDictionary resources = null)
        {
            if (xmlDocuments == null)
                throw new ArgumentNullException(nameof(xmlDocuments));
            if (registry == null)
                throw new ArgumentNullException(nameof(registry));
            var styles = new List<Style>();
            foreach (string xml in xmlDocuments)
            {
                if (string.IsNullOrWhiteSpace(xml))
                    continue;
                styles.AddRange(new StyleParser(xml).Parse());
            }
            if (styles.Count == 0)
                throw new ArgumentException(
                    "At least one style XML document is required.", nameof(xmlDocuments));
            return new StyleSheet(
                registry,
                resources ?? new ResourceDictionary(),
                styles);
        }

        /// <summary>
        /// Loads a style document and recursively expands StyleInclude elements.
        /// Relative include sources are resolved beside the document that contains them.
        /// </summary>
        public static StyleSheet LoadFromSource(
            string source,
            ResourceDictionary resources = null)
        {
            Func<string, string> sourceLoader = SourceLoader;
            if (sourceLoader == null)
                throw new InvalidOperationException(
                    "StyleSheet.SourceLoader must be configured before loading a StyleInclude.");
            return LoadFromSource(source, sourceLoader, resources);
        }

        /// <summary>Loads and combines multiple root StyleInclude sources in order.</summary>
        public static StyleSheet LoadFromSources(
            IEnumerable<string> sources,
            ResourceDictionary resources = null)
        {
            Func<string, string> sourceLoader = SourceLoader;
            if (sourceLoader == null)
                throw new InvalidOperationException(
                    "StyleSheet.SourceLoader must be configured before loading a StyleInclude.");
            if (sources == null)
                throw new ArgumentNullException(nameof(sources));

            var styles = new List<Style>();
            foreach (string source in sources)
            {
                if (string.IsNullOrWhiteSpace(source))
                    throw new ArgumentException("A style source is required.", nameof(sources));
                styles.AddRange(LoadSource(
                    NormalizeSource(source),
                    sourceLoader,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
            }
            return new StyleSheet(
                StylePropertyRegistry.Global,
                resources ?? new ResourceDictionary(),
                styles);
        }

        /// <summary>
        /// Loads a control's own <c>Styles</c> (a view's <c>&lt;UserControl.Styles&gt;</c>): a <c>Styles</c> document
        /// whose <c>Style</c> and <c>StyleInclude</c> elements apply in document order, as in Avalonia. Includes are read
        /// with <see cref="SourceLoader"/> (relative sources from the content root).
        /// </summary>
        public static StyleSheet LoadInline(
            string xml,
            ResourceDictionary resources = null)
        {
            return LoadInline(xml, null, resources, true);
        }

        // Hot reload's Styles: the Style/StyleInclude markup with the namespaces in scope in the view.
        internal static StyleSheet LoadInline(string xml, Acrux.Markup.XamlNamespaceScope namespaces)
        {
            return LoadInline(xml, namespaces, null, false);
        }

        static StyleSheet LoadInline(
            string xml,
            Acrux.Markup.XamlNamespaceScope namespaces,
            ResourceDictionary resources,
            bool allowCache)
        {
            if (string.IsNullOrWhiteSpace(xml))
                throw new ArgumentException("Style XML cannot be empty.", nameof(xml));
            Func<string, string> sourceLoader = SourceLoader;
            List<Style> styles = null;
            bool cache = allowCache && _cacheSources && sourceLoader != null;
            if (cache)
                lock (SourceCache)
                    if (!ReferenceEquals(_cachedSourceLoader, sourceLoader) || !InlineCache.TryGetValue(xml, out styles))
                        styles = null;
            if (styles == null)
            {
                var activeSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                styles = new StyleParser(xml, namespaces).Parse(includeSource =>
                {
                    if (sourceLoader == null)
                        throw new InvalidOperationException(
                            "StyleSheet.SourceLoader must be configured before loading a StyleInclude.");
                    return LoadSource(ResolveSource(string.Empty, includeSource), sourceLoader, activeSources);
                });
                if (cache)
                    lock (SourceCache)
                    {
                        if (!ReferenceEquals(_cachedSourceLoader, sourceLoader))
                        {
                            SourceCache.Clear();
                            InlineCache.Clear();
                            _cachedSourceLoader = sourceLoader;
                        }
                        InlineCache[xml] = styles;
                    }
            }
            return new StyleSheet(
                StylePropertyRegistry.Global,
                resources ?? new ResourceDictionary(),
                styles);
        }

        /// <summary>
        /// Loads a style document and recursively expands StyleInclude elements.
        /// Relative include sources are resolved beside the document that contains them.
        /// </summary>
        public static StyleSheet LoadFromSource(
            string source,
            Func<string, string> sourceLoader,
            ResourceDictionary resources = null)
        {
            return LoadFromSource(
                source,
                sourceLoader,
                StylePropertyRegistry.Global,
                resources);
        }

        /// <summary>
        /// Loads a style document and recursively expands StyleInclude elements.
        /// Relative include sources are resolved beside the document that contains them.
        /// </summary>
        public static StyleSheet LoadFromSource(
            string source,
            Func<string, string> sourceLoader,
            StylePropertyRegistry registry,
            ResourceDictionary resources = null)
        {
            if (string.IsNullOrWhiteSpace(source))
                throw new ArgumentException("A style source is required.", nameof(source));
            if (sourceLoader == null)
                throw new ArgumentNullException(nameof(sourceLoader));
            if (registry == null)
                throw new ArgumentNullException(nameof(registry));

            var activeSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<Style> styles = LoadSource(
                NormalizeSource(source),
                sourceLoader,
                activeSources);
            return new StyleSheet(
                registry,
                resources ?? new ResourceDictionary(),
                styles);
        }

        static List<Style> LoadSource(
            string source,
            Func<string, string> sourceLoader,
            ISet<string> activeSources)
        {
            List<Style> cached;
            if (TryGetCachedSource(source, sourceLoader, out cached))
                return cached;

            if (!activeSources.Add(source))
                throw new FormatException(
                    "Circular StyleInclude reference to '" + source + "'.");

            try
            {
                string xml;
                Acrux.Markup.HotReload.NoteStyleSource(source);
                try
                {
                    xml = sourceLoader(source);
                }
                catch (Exception exception)
                {
                    ReportLoadFailure(source, exception);
                    return new List<Style>();
                }
                if (string.IsNullOrWhiteSpace(xml))
                {
                    ReportLoadFailure(source, new FormatException("Style source '" + source + "' is empty."));
                    return new List<Style>();
                }

                try
                {
                    List<Style> styles = new StyleParser(xml).Parse(includeSource => LoadSource(
                        ResolveSource(source, includeSource),
                        sourceLoader,
                        activeSources));
                    CacheSource(source, sourceLoader, styles);
                    return styles;
                }
                catch (FormatException exception)
                {
                    throw new FormatException(
                        "Could not parse style source '" + source + "': " +
                        exception.Message,
                        exception);
                }
            }
            finally
            {
                activeSources.Remove(source);
            }
        }

        static void ReportLoadFailure(string source, Exception exception)
        {
            Action<string, Exception> report = SourceLoadFailed;
            if (report == null)
                return;
            try
            {
                report(source, exception);
            }
            catch (Exception)
            {
                // A failing reporter must not turn a skipped style back into a crash.
            }
        }

        static bool TryGetCachedSource(string source, Func<string, string> sourceLoader, out List<Style> styles)
        {
            styles = null;
            if (!_cacheSources)
                return false;
            lock (SourceCache)
                return ReferenceEquals(_cachedSourceLoader, sourceLoader) && SourceCache.TryGetValue(source, out styles);
        }

        static void CacheSource(string source, Func<string, string> sourceLoader, List<Style> styles)
        {
            if (!_cacheSources)
                return;
            lock (SourceCache)
            {
                if (!ReferenceEquals(_cachedSourceLoader, sourceLoader))
                {
                    SourceCache.Clear();
                    InlineCache.Clear();
                    _cachedSourceLoader = sourceLoader;
                }
                SourceCache[source] = styles;
            }
        }

        static string ResolveSource(string parentSource, string includeSource)
        {
            string include = (includeSource ?? string.Empty).Trim().Replace('\\', '/');
            if (include.Length == 0)
                throw new FormatException("StyleInclude requires a 'Source' attribute.");
            if (include[0] == '/')
                return NormalizeSource(include.Substring(1));

            int separator = parentSource.LastIndexOf('/');
            return NormalizeSource(separator < 0
                ? include
                : parentSource.Substring(0, separator + 1) + include);
        }

        static string NormalizeSource(string source)
        {
            string value = source.Trim().Replace('\\', '/');
            string[] parts = value.Split('/');
            var normalized = new List<string>(parts.Length);
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                if (part.Length == 0 || part == ".")
                    continue;
                if (part == "..")
                {
                    if (normalized.Count == 0)
                        throw new FormatException(
                            "Style source escapes its root: '" + source + "'.");
                    normalized.RemoveAt(normalized.Count - 1);
                    continue;
                }
                normalized.Add(part);
            }
            return string.Join("/", normalized.ToArray());
        }

        public void Apply(Control control)
        {
            if (control == null)
                throw new ArgumentNullException(nameof(control));
            _registry.Reset(control);
            control.BeginStyleAnimationMatch();
            if (!HasPropertySelectors)
            {
                ApplyEntries(control, null, null);
                return;
            }

            // A [Property=Value] selector can depend on a value that a later style sets, so the matches are settled
            // against the applied result and the styles re-applied until they agree.
            Dictionary<StyleEntry, bool> settled = null;
            for (int pass = 0; pass < MaxPropertySelectorPasses; pass++)
            {
                if (pass > 0)
                {
                    Reset(control);
                    control.BeginStyleAnimationMatch();
                }
                var used = new Dictionary<StyleEntry, bool>();
                ApplyEntries(control, settled, used);
                var live = new Dictionary<StyleEntry, bool>();
                EvaluatePropertySelectors(control, live);
                if (SameMatches(used, live))
                    return;
                settled = live;
            }
        }

        const int MaxPropertySelectorPasses = 4;

        bool HasPropertySelectors
        {
            get
            {
                if (_localStyles != null)
                    return _inheritedStyles.HasPropertySelectors || _localStyles.HasPropertySelectors;
                for (int i = 0; i < _styles.Count; i++)
                    if (_styles[i].Selector.HasPropertyConditions)
                        return true;
                return false;
            }
        }

        void EvaluatePropertySelectors(Control control, Dictionary<StyleEntry, bool> matches)
        {
            if (_localStyles != null)
            {
                _inheritedStyles.EvaluatePropertySelectors(control, matches);
                _localStyles.EvaluatePropertySelectors(control, matches);
                return;
            }
            int[] candidates = CandidatesFor(control);
            for (int i = 0; i < candidates.Length; i++)
            {
                StyleEntry entry = _styles[candidates[i]];
                if (entry.Selector.HasPropertyConditions)
                    matches[entry] = entry.Selector.Matches(control, _registry);
            }
        }

        static bool SameMatches(Dictionary<StyleEntry, bool> left, Dictionary<StyleEntry, bool> right)
        {
            if (left.Count != right.Count)
                return false;
            foreach (KeyValuePair<StyleEntry, bool> pair in left)
            {
                bool other;
                if (!right.TryGetValue(pair.Key, out other) || other != pair.Value)
                    return false;
            }
            return true;
        }

        void ApplyEntries(Control control, Dictionary<StyleEntry, bool> settled, Dictionary<StyleEntry, bool> used)
        {
            if (_localStyles != null)
            {
                _inheritedStyles.ApplyEntries(control, settled, used);
                _localStyles.ApplyEntries(control, settled, used);
                return;
            }
            ResolvePending(null);
            var context = new StyleSetterContext(_resources, control);
            int[] candidates = CandidatesFor(control);
            for (int candidate = 0; candidate < candidates.Length; candidate++)
            {
                StyleEntry entry = _styles[candidates[candidate]];
                bool matches;
                if (!entry.Selector.HasPropertyConditions || settled == null || !settled.TryGetValue(entry, out matches))
                    matches = entry.Selector.Matches(control, _registry);
                if (used != null && entry.Selector.HasPropertyConditions)
                    used[entry] = matches;
                if (!matches)
                    continue;
                for (int setterIndex = 0; setterIndex < entry.Style.Setters.Count; setterIndex++)
                {
                    Setter setter = entry.Style.Setters[setterIndex];
                    _registry.Apply(
                        entry.Selector.TypeName,
                        setter.Property,
                        control,
                        setter.Value,
                        context);
                }
                if (entry.Style.Animations.Count > 0)
                    control.MatchStyleAnimations(entry.Style.Animations);
            }
        }

        // The resolved entries whose selector type accepts the control's type. The type test depends only on the
        // runtime type, so each control is checked against just these instead of every style in the sheet.
        int[] CandidatesFor(Control control)
        {
            object type = control.StyleKey;
            CandidateIndex index = _candidates;
            int[] candidates;
            if (index.Version == _version && index.Generation == _registry.Generation &&
                index.ByType.TryGetValue(type, out candidates))
                return candidates;

            lock (_candidatesLock)
            {
                index = _candidates;
                bool current = index.Version == _version && index.Generation == _registry.Generation;
                if (current && index.ByType.TryGetValue(type, out candidates))
                    return candidates;
                var list = new List<int>();
                for (int i = 0; i < _styles.Count; i++)
                {
                    StyleEntry entry = _styles[i];
                    if (entry.IsResolved && _registry.Matches(entry.Selector.TypeName, control))
                        list.Add(i);
                }
                candidates = list.ToArray();
                var byType = current ? new Dictionary<object, int[]>(index.ByType) : new Dictionary<object, int[]>();
                byType[type] = candidates;
                _candidates = new CandidateIndex
                    { Version = _version, Generation = _registry.Generation, ByType = byType };
                return candidates;
            }
        }

        internal void Reset(Control control)
        {
            if (control != null)
            {
                _registry.Reset(control);
                if (_localStyles != null &&
                    !ReferenceEquals(_inheritedStyles._registry, _registry))
                    _inheritedStyles._registry.Reset(control);
            }
        }

        void Attach()
        {
            if (_attached)
                return;
            if (_localStyles != null)
            {
                _inheritedStyles.Attach();
                _localStyles.Attach();
                _attached = true;
                return;
            }
            _registry.MetadataChanged += ResolvePending;
            _attached = true;
            ResolvePending(null);
        }

        void Detach()
        {
            if (!_attached)
                return;
            if (_localStyles != null)
            {
                _inheritedStyles.Detach();
                _localStyles.Detach();
                _attached = false;
                return;
            }
            _registry.MetadataChanged -= ResolvePending;
            _attached = false;
        }

        void ResolvePending(string selectorName)
        {
            if (_localStyles != null)
            {
                _inheritedStyles.ResolvePending(selectorName);
                _localStyles.ResolvePending(selectorName);
                return;
            }
            bool changed = false;
            for (int i = 0; i < _styles.Count; i++)
            {
                StyleEntry entry = _styles[i];
                if (entry.IsResolved || selectorName != null && !string.Equals(
                        entry.Selector.TypeName, selectorName, StringComparison.Ordinal))
                    continue;
                if (!_registry.ContainsType(entry.Selector.TypeName))
                    continue;
                bool resolved = true;
                for (int setterIndex = 0;
                     setterIndex < entry.Style.Setters.Count;
                     setterIndex++)
                {
                    if (_registry.ContainsProperty(
                            entry.Selector.TypeName,
                            entry.Style.Setters[setterIndex].Property))
                        continue;
                    resolved = false;
                    break;
                }
                if (!resolved)
                    continue;
                entry.IsResolved = true;
                changed = true;
            }
            if (changed)
                _version++;
        }

        // Property names tested by [Property=Value] selectors; changing one re-applies the control's styles.
        // Written while parsing, read on every property change; readers take the current snapshot without locking.
        static volatile HashSet<string> WatchedProperties = new HashSet<string>(StringComparer.Ordinal);
        static readonly object SelectorIndexLock = new object();

        // Pseudo-classes tested on an ancestor by a descendant selector; only these changes can alter a descendant's
        // matches, so others skip re-matching the whole subtree.
        static volatile Dictionary<string, Selector[]> AncestorPseudoClasses =
            new Dictionary<string, Selector[]>(StringComparer.OrdinalIgnoreCase);

        static void WatchProperty(string name)
        {
            lock (SelectorIndexLock)
            {
                if (WatchedProperties.Contains(name))
                    return;
                var next = new HashSet<string>(WatchedProperties, StringComparer.Ordinal);
                next.Add(name);
                WatchedProperties = next;
            }
        }

        static void IndexAncestorPseudoClasses(Selector descendant)
        {
            lock (SelectorIndexLock)
            {
                var next = new Dictionary<string, Selector[]>(AncestorPseudoClasses, StringComparer.OrdinalIgnoreCase);
                for (Selector ancestor = descendant.Ancestor; ancestor != null; ancestor = ancestor.Ancestor)
                    for (int i = 0; i < ancestor.PseudoClasses.Length; i++)
                    {
                        Selector[] owners;
                        next.TryGetValue(ancestor.PseudoClasses[i], out owners);
                        var grown = new Selector[owners == null ? 1 : owners.Length + 1];
                        if (owners != null)
                            Array.Copy(owners, grown, owners.Length);
                        grown[grown.Length - 1] = ancestor;
                        next[ancestor.PseudoClasses[i]] = grown;
                    }
                AncestorPseudoClasses = next;
            }
        }

        // Whether the control fits the type and classes of an ancestor segment that tests the pseudo-class.
        internal static bool IsAncestorPseudoClass(Control control, string pseudoClass)
        {
            Selector[] owners;
            if (!AncestorPseudoClasses.TryGetValue(pseudoClass, out owners))
                return false;
            for (int i = 0; i < owners.Length; i++)
                if (owners[i].MatchesTypeAndClasses(control, StylePropertyRegistry.Global))
                    return true;
            return false;
        }

        internal static bool IsSelectorProperty(string name)
        {
            return name != null && WatchedProperties.Contains(name);
        }

        /// <summary>[Property=Value] selector condition, compared against the control's current value.</summary>
        sealed class PropertyCondition
        {
            static readonly PropertyConverter Converter = new PropertyConverter();
            readonly string _name;
            readonly string _value;
            IStyledProperty _property;
            Type _expectedType;
            object _expected;

            public PropertyCondition(string name, string value)
            {
                _name = name;
                _value = value;
            }

            public bool Matches(Control control)
            {
                IStyledProperty property = Resolve(control);
                if (property == null)
                    return false;
                if (_expectedType != property.ValueType)
                {
                    _expected = Converter.Convert(_value, property.ValueType);
                    _expectedType = property.ValueType;
                }
                return Equals(property.GetValueObject(control), _expected);
            }

            IStyledProperty Resolve(Control control)
            {
                if (_property != null && _property.Matches(control))
                    return _property;
                IList<IStyledProperty> properties = StyledPropertyCatalog.Snapshot();
                for (int i = 0; i < properties.Count; i++)
                    if (string.Equals(properties[i].Name, _name, StringComparison.Ordinal) && properties[i].Matches(control))
                        return _property = properties[i];
                return null;
            }
        }

        sealed class StyleEntry
        {
            public StyleEntry(Style style, Selector selector)
            {
                Style = style;
                Selector = selector;
            }

            public Style Style { get; }
            public Selector Selector { get; }
            public bool IsResolved { get; set; }
        }

        sealed class Selector
        {
            public string TypeName;
            public string[] ClassNames;
            public string[] PseudoClasses;
            public PropertyCondition[] Properties = new PropertyCondition[0];
            public Selector Ancestor;

            public bool HasPropertyConditions => Properties.Length > 0;

            public static Selector Parse(string text)
            {
                string selector = (text ?? string.Empty).Trim();
                var properties = new List<PropertyCondition>();
                int open;
                while ((open = selector.IndexOf('[')) >= 0)
                {
                    int close = selector.IndexOf(']', open);
                    int equals = close < 0 ? -1 : selector.IndexOf('=', open, close - open);
                    if (equals < 0)
                        throw new FormatException("Unsupported style selector '" + text + "'.");
                    string name = selector.Substring(open + 1, equals - open - 1).Trim();
                    string value = selector.Substring(equals + 1, close - equals - 1).Trim();
                    if (name.Length == 0 || name.IndexOf('(') >= 0)
                        throw new FormatException("Unsupported style selector '" + text + "'.");
                    // Only the last compound of a descendant selector may test properties.
                    if (selector.IndexOfAny(new[] { ' ', '\t', '\r', '\n' }, close) >= 0)
                        throw new FormatException("Property conditions are only supported on the styled control: '" +
                                                  text + "'.");
                    properties.Add(new PropertyCondition(name, value));
                    WatchProperty(name);
                    selector = selector.Remove(open, close - open + 1);
                }
                int descendantSeparator = selector.LastIndexOfAny(new[] { ' ', '\t', '\r', '\n' });
                if (descendantSeparator >= 0)
                {
                    Selector descendant = Parse(selector.Substring(descendantSeparator + 1));
                    descendant.Ancestor = Parse(selector.Substring(0, descendantSeparator));
                    IndexAncestorPseudoClasses(descendant);
                    return descendant;
                }
                int namespaceSeparator = selector.IndexOf('|');
                int localStart = namespaceSeparator + 1;
                int pseudoSeparator = selector.IndexOf(':', localStart);
                string head = pseudoSeparator < 0
                    ? selector
                    : selector.Substring(0, pseudoSeparator);
                string pseudoText = pseudoSeparator < 0
                    ? string.Empty
                    : selector.Substring(pseudoSeparator + 1);

                int classSeparator = head.IndexOf('.', localStart);
                string typeName = classSeparator < 0
                    ? head.Trim()
                    : head.Substring(0, classSeparator).Trim();
                if (namespaceSeparator >= 0)
                    typeName = typeName.Replace('|', '.');
                string[] classNames = classSeparator < 0
                    ? new string[0]
                    : head.Substring(classSeparator + 1).Split('.');
                for (int i = 0; i < classNames.Length; i++)
                {
                    classNames[i] = classNames[i].Trim();
                    if (classNames[i].Length == 0)
                        throw new FormatException("Unsupported style selector '" + text + "'.");
                }
                if (string.IsNullOrEmpty(typeName))
                    throw new FormatException("Unsupported style selector '" + text + "'.");

                string[] pseudos = new string[0];
                if (pseudoSeparator >= 0)
                {
                    string[] raw = pseudoText.Split(':');
                    pseudos = new string[raw.Length];
                    for (int i = 0; i < raw.Length; i++)
                    {
                        string pseudo = raw[i].Trim();
                        if (pseudo.Length == 0)
                            throw new FormatException("Unsupported style selector '" + text + "'.");
                        pseudos[i] = ":" + pseudo;
                    }
                }

                return new Selector
                {
                    TypeName = typeName,
                    ClassNames = classNames,
                    PseudoClasses = pseudos,
                    Properties = properties.ToArray()
                };
            }

            public bool MatchesTypeAndClasses(Control control, StylePropertyRegistry registry)
            {
                if (!registry.Matches(TypeName, control))
                    return false;
                for (int i = 0; i < ClassNames.Length; i++)
                    if (!HasClass(control.StyleClass, ClassNames[i]))
                        return false;
                return true;
            }

            public bool Matches(Control control, StylePropertyRegistry registry)
            {
                if (!registry.Matches(TypeName, control))
                    return false;
                for (int i = 0; i < ClassNames.Length; i++)
                    if (!HasClass(control.StyleClass, ClassNames[i]))
                        return false;
                for (int i = 0; i < PseudoClasses.Length; i++)
                    if (!control.HasPseudoClass(PseudoClasses[i]))
                        return false;
                for (int i = 0; i < Properties.Length; i++)
                    if (!Properties[i].Matches(control))
                        return false;
                if (Ancestor != null)
                {
                    var visual = control as VisualElement;
                    for (var parent = visual == null ? null : visual.Parent; parent != null; parent = parent.Parent)
                        if (Ancestor.Matches(parent, registry))
                            return true;
                    return false;
                }
                return true;
            }

            static bool HasClass(string classes, string className)
            {
                if (string.IsNullOrEmpty(classes))
                    return false;
                int start = 0;
                while (start < classes.Length)
                {
                    while (start < classes.Length && char.IsWhiteSpace(classes[start]))
                        start++;
                    int end = start;
                    while (end < classes.Length && !char.IsWhiteSpace(classes[end]))
                        end++;
                    if (end - start == className.Length &&
                        string.Compare(classes, start, className, 0, className.Length,
                            StringComparison.OrdinalIgnoreCase) == 0)
                        return true;
                    start = end;
                }
                return false;
            }
        }

        sealed class StyleParser
        {
            readonly string _xml;
            int _position;
            XamlNamespaceScope _namespaces = XamlNamespaceScope.Default;

            public StyleParser(string xml)
            {
                _xml = xml;
            }

            public StyleParser(string xml, XamlNamespaceScope namespaces)
            {
                _xml = xml;
                if (namespaces != null)
                    _namespaces = namespaces;
            }

            public List<Style> Parse()
            {
                return Parse(null);
            }

            public List<Style> Parse(
                Func<string, IEnumerable<Style>> includeResolver)
            {
                var result = new List<Style>();
                SkipTrivia();
                if (PeekElement("Styles"))
                {
                    var rootAttributes = new Dictionary<string, string>(StringComparer.Ordinal);
                    ParseStartTag("Styles", rootAttributes, false, true);
                    _namespaces = _namespaces.With(rootAttributes);
                }
                string elementName;
                while (MoveToStyleElement(out elementName))
                {
                    if (string.Equals(
                            elementName, "StyleInclude", StringComparison.Ordinal))
                    {
                        if (includeResolver == null)
                            Fail("StyleInclude requires a source loader.");
                        var includeAttributes =
                            new Dictionary<string, string>(StringComparer.Ordinal);
                        ParseStartTag("StyleInclude", includeAttributes, true);
                        IEnumerable<Style> includedStyles = includeResolver(
                            GetRequired(includeAttributes, "Source"));
                        if (includedStyles != null)
                            result.AddRange(includedStyles);
                        continue;
                    }

                    var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
                    ParseStartTag("Style", attributes, false);
                    var style = new Style(ResolveSelector(GetRequired(attributes, "Selector")));
                    while (true)
                    {
                        SkipTrivia();
                        if (Consume("</Style>"))
                            break;
                        if (PeekElement("Style.Animations"))
                        {
                            ParseAnimations(style);
                            continue;
                        }
                        if (!PeekElement("Setter"))
                            Fail("Only Setter and Style.Animations elements are supported inside Style.");
                        style.Setters.Add(ParseSetter());
                    }
                    result.Add(style);
                }
                if (result.Count == 0)
                    Fail("No Style elements were found.");
                return result;
            }

            Setter ParseSetter()
            {
                var setterAttributes =
                    new Dictionary<string, string>(StringComparer.Ordinal);
                bool selfClosing = ParseStartTag(
                    "Setter", setterAttributes, false, true);
                object setterValue;
                if (selfClosing)
                {
                    setterValue = GetRequired(setterAttributes, "Value");
                }
                else
                {
                    if (setterAttributes.ContainsKey("Value"))
                        Fail("A Setter cannot have both Value and a child value.");
                    setterValue = ParseSetterValue();
                    SkipTrivia();
                    if (!Consume("</Setter>"))
                        Fail("Expected </Setter>.");
                }
                return new Setter(GetRequired(setterAttributes, "Property"), setterValue);
            }

            // <Style.Animations><Animation ...><KeyFrame Cue="..."><Setter .../></KeyFrame></Animation></Style.Animations>
            void ParseAnimations(Style style)
            {
                var ignored = new Dictionary<string, string>(StringComparer.Ordinal);
                if (ParseStartTag("Style.Animations", ignored, false, true))
                    return;
                while (true)
                {
                    SkipTrivia();
                    if (Consume("</Style.Animations>"))
                        return;
                    if (!PeekElement("Animation"))
                        Fail("Only Animation elements are supported inside Style.Animations.");
                    var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
                    bool empty = ParseStartTag("Animation", attributes, false, true);
                    Acrux.Animation.StyleAnimation animation;
                    try
                    {
                        animation = Acrux.Animation.StyleAnimation.Create(attributes);
                    }
                    catch (FormatException exception)
                    {
                        Fail(exception.Message);
                        return;
                    }
                    style.Animations.Add(animation);
                    if (empty)
                        continue;
                    while (true)
                    {
                        SkipTrivia();
                        if (Consume("</Animation>"))
                            break;
                        if (!PeekElement("KeyFrame"))
                            Fail("Only KeyFrame elements are supported inside Animation.");
                        var frameAttributes = new Dictionary<string, string>(StringComparer.Ordinal);
                        bool emptyFrame = ParseStartTag("KeyFrame", frameAttributes, false, true);
                        Acrux.Animation.StyleKeyFrame frame;
                        try
                        {
                            frame = animation.AddKeyFrame(frameAttributes);
                        }
                        catch (FormatException exception)
                        {
                            Fail(exception.Message);
                            return;
                        }
                        if (emptyFrame)
                            continue;
                        while (true)
                        {
                            SkipTrivia();
                            if (Consume("</KeyFrame>"))
                                break;
                            if (!PeekElement("Setter"))
                                Fail("Only Setter elements are supported inside KeyFrame.");
                            frame.Setters.Add(ParseSetter());
                        }
                    }
                }
            }

            bool MoveToStyleElement(out string name)
            {
                while (_position < _xml.Length)
                {
                    SkipTrivia();
                    // Design-time only, as in Avalonia: the control a previewer shows the styles on.
                    if (PeekElement("Design.PreviewWith"))
                    {
                        ReadElementXml("Design.PreviewWith");
                        continue;
                    }
                    if (PeekElement("StyleInclude"))
                    {
                        name = "StyleInclude";
                        return true;
                    }
                    if (PeekElement("Style"))
                    {
                        name = "Style";
                        return true;
                    }
                    int next = _xml.IndexOf('<', _position + 1);
                    if (next < 0)
                        break;
                    _position = next;
                }
                name = null;
                return false;
            }

            object ParseSetterValue()
            {
                SkipTrivia();
                if (PeekElement("ItemsPanelTemplate"))
                    return ItemsPanelTemplate.Parse(ReadElementXml("ItemsPanelTemplate"), _namespaces);
                if (PeekElement("ControlTemplate"))
                    return ControlTemplate.Parse(ReadElementXml("ControlTemplate"), _namespaces);
                if (PeekElement("SolidColorBrush"))
                {
                    var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
                    ParseStartTag("SolidColorBrush", attributes, true);
                    return new SolidColorBrush(ParseColor(
                        GetRequired(attributes, "Color")));
                }
                if (PeekElement("PageSlide"))
                {
                    var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
                    ParseStartTag("PageSlide", attributes, true);
                    string duration, orientation;
                    return new Acrux.Animation.PageSlide(
                        attributes.TryGetValue("Duration", out duration)
                            ? TimeSpan.Parse(duration, System.Globalization.CultureInfo.InvariantCulture)
                            : TimeSpan.Zero,
                        attributes.TryGetValue("Orientation", out orientation)
                            ? (Acrux.Animation.SlideAxis)Enum.Parse(typeof(Acrux.Animation.SlideAxis), orientation, true)
                            : Acrux.Animation.SlideAxis.Horizontal);
                }
                if (PeekElement("ImageBrush"))
                {
                    var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
                    ParseStartTag("ImageBrush", attributes, true);
                    string stretch;
                    string opacity;
                    string opacityKey = null;
                    bool dynamicOpacity = false;
                    bool hasOpacity = attributes.TryGetValue("Opacity", out opacity);
                    if (hasOpacity && StyleSetterContext.TryParseResource(opacity, out opacityKey, out dynamicOpacity) &&
                        !dynamicOpacity)
                        Fail("ImageBrush.Opacity takes a number or a {DynamicResource}.");
                    var imageBrush = new ImageBrush(GetRequired(attributes, "Source"))
                    {
                        Stretch = attributes.TryGetValue("Stretch", out stretch)
                            ? (Stretch)Enum.Parse(typeof(Stretch), stretch, true)
                            : Stretch.Fill,
                        Opacity = hasOpacity && !dynamicOpacity
                            ? float.Parse(opacity, System.Globalization.CultureInfo.InvariantCulture)
                            : 1f
                    };
                    // Avalonia brushes are styled objects, so their properties take {DynamicResource}; here the
                    // setter resolves it per control instead.
                    if (dynamicOpacity)
                        return new ImageBrushOpacityResource(imageBrush, opacityKey);
                    return imageBrush;
                }
                if (PeekElement("TextureBrush"))
                {
                    var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
                    ParseStartTag("TextureBrush", attributes, true);
                    string fallback;
                    string tint;
                    return new TextureBrush(
                        GetRequired(attributes, "Source"),
                        attributes.TryGetValue("Fallback", out fallback) ? ParseColor(fallback) : Color.Transparent,
                        attributes.TryGetValue("Tint", out tint)
                            ? ParseColor(tint)
                            : Color.White);
                }
                if (PeekElement("CompositeTextureBrush"))
                {
                    var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
                    ParseStartTag("CompositeTextureBrush", attributes, true);
                    CompositeBrushOrientation orientation;
                    if (!Enum.TryParse(
                            GetRequired(attributes, "Orientation"), true, out orientation))
                        Fail("CompositeTextureBrush Orientation must be Horizontal or Vertical.");
                    string fallback;
                    string tint;
                    return new CompositeTextureBrush(
                        GetRequired(attributes, "StartSource"),
                        GetRequired(attributes, "CenterSource"),
                        GetRequired(attributes, "EndSource"),
                        orientation,
                        ParseBrushFloat(GetRequired(attributes, "StartLength")),
                        ParseBrushFloat(GetRequired(attributes, "EndLength")),
                        ParseBrushFloat(GetRequired(attributes, "ReferenceCrossLength")),
                        attributes.TryGetValue("Fallback", out fallback) ? ParseColor(fallback) : Color.Transparent,
                        attributes.TryGetValue("Tint", out tint)
                            ? ParseColor(tint)
                            : Color.White);
                }
                if (PeekElement("NineSliceTextureBrush"))
                {
                    var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
                    ParseStartTag("NineSliceTextureBrush", attributes, true);
                    string fallback;
                    string tint;
                    return new NineSliceTextureBrush(
                        GetRequired(attributes, "LeftTopSource"),
                        GetRequired(attributes, "CenterTopSource"),
                        GetRequired(attributes, "RightTopSource"),
                        GetRequired(attributes, "LeftCenterSource"),
                        GetRequired(attributes, "CenterSource"),
                        GetRequired(attributes, "RightCenterSource"),
                        GetRequired(attributes, "LeftBottomSource"),
                        GetRequired(attributes, "CenterBottomSource"),
                        GetRequired(attributes, "RightBottomSource"),
                        ParseBrushFloat(GetRequired(attributes, "LeftWidth")),
                        ParseBrushFloat(GetRequired(attributes, "RightWidth")),
                        ParseBrushFloat(GetRequired(attributes, "TopHeight")),
                        ParseBrushFloat(GetRequired(attributes, "BottomHeight")),
                        ParseBrushFloat(GetRequired(attributes, "ReferenceWidth")),
                        attributes.TryGetValue("Fallback", out fallback) ? ParseColor(fallback) : Color.Transparent,
                        attributes.TryGetValue("Tint", out tint) ? ParseColor(tint) : Color.White);
                }
                Fail("Only ControlTemplate, ItemsPanelTemplate, PageSlide, SolidColorBrush, ImageBrush, TextureBrush, CompositeTextureBrush, or NineSliceTextureBrush is supported as a Setter value.");
                return null;
            }

            string ResolveSelector(string selector)
            {
                string[] parts = selector.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 1)
                {
                    for (int i = 0; i < parts.Length; i++)
                        parts[i] = ResolveSelector(parts[i]);
                    return string.Join(" ", parts);
                }
                int separator = selector.IndexOf('|');
                if (separator < 0)
                    return selector;
                int end = separator + 1;
                while (end < selector.Length && selector[end] != '.' && selector[end] != ':')
                    end++;
                string prefix = selector.Substring(0, separator).Trim();
                string typeName = selector.Substring(separator + 1, end - separator - 1).Trim();
                try
                {
                    string resolved = _namespaces.ResolveSelectorType(prefix, typeName);
                    int split = resolved.LastIndexOf('.');
                    return resolved.Substring(0, split) + "|" + typeName + selector.Substring(end);
                }
                catch (FormatException error)
                {
                    Fail(error.Message);
                    return null;
                }
            }

            string ReadElementXml(string expectedName)
            {
                int start = _position;
                int depth = 0;
                while (_position < _xml.Length)
                {
                    int open = _xml.IndexOf('<', _position);
                    if (open < 0)
                        Fail("Unterminated " + expectedName + ".");
                    _position = open;

                    if (Consume("<!--"))
                    {
                        SkipUntil("-->");
                        continue;
                    }
                    if (Consume("<?"))
                    {
                        SkipUntil("?>");
                        continue;
                    }

                    bool closing = Consume("</");
                    if (!closing)
                        Expect('<');
                    string name = ParseName();

                    bool quoted = false;
                    char quote = '\0';
                    bool selfClosing = false;
                    while (_position < _xml.Length)
                    {
                        char character = _xml[_position++];
                        if (quoted)
                        {
                            if (character == quote)
                                quoted = false;
                            continue;
                        }
                        if (character == '\'' || character == '"')
                        {
                            quoted = true;
                            quote = character;
                            continue;
                        }
                        if (character == '>')
                        {
                            int before = _position - 2;
                            while (before >= 0 && char.IsWhiteSpace(_xml[before]))
                                before--;
                            selfClosing = before >= 0 && _xml[before] == '/';
                            break;
                        }
                    }

                    if (string.Equals(name, expectedName, StringComparison.Ordinal))
                    {
                        if (closing)
                            depth--;
                        else if (!selfClosing)
                            depth++;
                        if (depth == 0)
                            return _xml.Substring(start, _position - start);
                    }
                }
                Fail("Unterminated " + expectedName + ".");
                return null;
            }

            static float ParseBrushFloat(string value)
            {
                return float.Parse(value, CultureInfo.InvariantCulture);
            }

            static Color ParseColor(string value)
            {
                return (Color)new Acrux.Markup.PropertyConverter().Convert(
                    value, typeof(Color));
            }

            bool ParseStartTag(
                string expectedName,
                IDictionary<string, string> attributes,
                bool requireSelfClosing,
                bool allowSelfClosing = false)
            {
                Expect('<');
                string name = ParseName();
                if (!string.Equals(name, expectedName, StringComparison.Ordinal))
                    Fail("Expected " + expectedName + ".");
                while (true)
                {
                    SkipWhitespace();
                    if (Consume("/>"))
                    {
                        if (!requireSelfClosing && !allowSelfClosing)
                            Fail(expectedName + " must contain Setter elements.");
                        return true;
                    }
                    if (Consume(">"))
                    {
                        if (requireSelfClosing)
                            Fail(expectedName + " must be self-closing.");
                        return false;
                    }
                    string attributeName = ParseName();
                    SkipWhitespace();
                    Expect('=');
                    SkipWhitespace();
                    attributes[attributeName] = ParseQuotedValue();
                }
            }

            bool PeekElement(string name)
            {
                if (_position >= _xml.Length || _xml[_position] != '<' ||
                    _position + name.Length + 1 > _xml.Length ||
                    string.CompareOrdinal(_xml, _position + 1, name, 0, name.Length) != 0)
                    return false;
                int after = _position + name.Length + 1;
                return after < _xml.Length &&
                       (char.IsWhiteSpace(_xml[after]) || _xml[after] == '>' ||
                        _xml[after] == '/');
            }

            string ParseName()
            {
                int start = _position;
                while (_position < _xml.Length)
                {
                    char value = _xml[_position];
                    if (!(char.IsLetterOrDigit(value) || value == '_' || value == '-' ||
                          value == '.' || value == ':'))
                        break;
                    _position++;
                }
                if (_position == start)
                    Fail("Expected an XML name.");
                return _xml.Substring(start, _position - start);
            }

            string ParseQuotedValue()
            {
                if (_position >= _xml.Length ||
                    (_xml[_position] != '\'' && _xml[_position] != '"'))
                    Fail("Expected a quoted attribute value.");
                char quote = _xml[_position++];
                int start = _position;
                while (_position < _xml.Length && _xml[_position] != quote)
                    _position++;
                if (_position >= _xml.Length)
                    Fail("Unterminated attribute value.");
                string value = DecodeEntities(_xml.Substring(start, _position - start));
                _position++;
                return value;
            }

            void SkipTrivia()
            {
                while (true)
                {
                    SkipWhitespace();
                    if (Consume("<!--"))
                    {
                        SkipUntil("-->");
                        continue;
                    }
                    if (Consume("<?"))
                    {
                        SkipUntil("?>");
                        continue;
                    }
                    return;
                }
            }

            void SkipWhitespace()
            {
                while (_position < _xml.Length && char.IsWhiteSpace(_xml[_position]))
                    _position++;
            }

            void SkipUntil(string marker)
            {
                int found = _xml.IndexOf(marker, _position, StringComparison.Ordinal);
                if (found < 0)
                    Fail("Unterminated XML trivia.");
                _position = found + marker.Length;
            }

            bool Consume(string value)
            {
                if (_position + value.Length > _xml.Length ||
                    string.CompareOrdinal(_xml, _position, value, 0, value.Length) != 0)
                    return false;
                _position += value.Length;
                return true;
            }

            void Expect(char value)
            {
                if (_position >= _xml.Length || _xml[_position] != value)
                    Fail("Expected '" + value + "'.");
                _position++;
            }

            void Fail(string message)
            {
                throw new FormatException(message + " Position: " + _position + ".");
            }

            static string GetRequired(IDictionary<string, string> attributes, string name)
            {
                string value;
                if (!attributes.TryGetValue(name, out value) ||
                    string.IsNullOrWhiteSpace(value))
                    throw new FormatException("Element requires a '" + name + "' attribute.");
                return value.Trim();
            }

            static string DecodeEntities(string value)
            {
                if (value.IndexOf('&') < 0)
                    return value;
                var result = new StringBuilder(value);
                result.Replace("&quot;", "\"");
                result.Replace("&apos;", "'");
                result.Replace("&lt;", "<");
                result.Replace("&gt;", ">");
                result.Replace("&amp;", "&");
                return result.ToString();
            }
        }
    }
}
