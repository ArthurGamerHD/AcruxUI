using System;
using System.Collections.Generic;
using System.Globalization;
using Acrux.Controls;
using Acrux.Core;
using Acrux.Core.PropertySystem;
using Acrux.Data;
using Acrux.Data.Converters;
using Acrux.Layout;
using Acrux.Styling;

namespace Acrux.Markup
{
    /// <summary>
    /// Implemented by generated views in debug builds. Exposes what a runtime rebuild of the view's
    /// XAML needs without reflection: element factories and setters, data-type accessors, and the
    /// x:Name fields the code-behind uses.
    /// </summary>
    internal interface IHotReloadableView
    {
        string HotReloadSource { get; }
        void RegisterHotReloadTypes(HotReloadContext context);
        void ApplyHotReloadNames(IDictionary<string, Control> names);
        void CompleteHotReload();
    }

    internal sealed class HotReloadContext
    {
        readonly Dictionary<string, Func<object, object>> _getters =
            new Dictionary<string, Func<object, object>>(StringComparer.Ordinal);
        readonly Dictionary<string, Action<object, object>> _setters =
            new Dictionary<string, Action<object, object>>(StringComparer.Ordinal);
        readonly Dictionary<string, Delegate> _handlers = new Dictionary<string, Delegate>(StringComparer.Ordinal);

        public HotReloadContext(TypeRegistry types)
        {
            Types = types;
        }

        public TypeRegistry Types { get; }

        public void Type<TControl>(string name, Func<TControl> factory) where TControl : Control
        {
            if (factory != null)
                Types.Register(name, factory);
            else
                Types.EnsureType(name);
        }

        public void Property<TControl, TValue>(string name, string property, Action<TControl, TValue> setter)
            where TControl : Control
        {
            Types.RegisterProperty(name, property, setter);
        }

        public void Alias(string alias, string name)
        {
            if (!Types.Contains(alias))
                Types.RegisterAlias(alias, name);
        }

        // The IDE preview builds views without their code-behind, so it leaves event attributes unsubscribed.
        public bool IgnoreMissingHandlers { get; set; }

        public void Handler(string eventName, string method, Delegate handler)
        {
            _handlers[eventName + "|" + method] = handler;
        }

        internal Delegate GetHandler(string eventName, string method)
        {
            Delegate handler;
            if (_handlers.TryGetValue(eventName + "|" + method, out handler) || IgnoreMissingHandlers)
                return handler;
            throw new InvalidOperationException("Event handler '" + method + "' for '" + eventName +
                                                "' is not available to hot reload; rebuild the mod.");
        }

        public void Getter(string dataType, string property, Func<object, object> getter)
        {
            _getters[dataType + "|" + property] = getter;
        }

        public void Setter(string dataType, string property, Action<object, object> setter)
        {
            _setters[dataType + "|" + property] = setter;
        }

        internal Func<object, object> GetGetter(string dataType, string property)
        {
            Func<object, object> getter;
            if (!_getters.TryGetValue(dataType + "|" + property, out getter))
                throw new InvalidOperationException("Binding '" + dataType + "." + property +
                                                    "' is not available to hot reload; rebuild the mod.");
            return getter;
        }

        internal Action<object, object> GetSetter(string dataType, string property)
        {
            Action<object, object> setter;
            if (!_setters.TryGetValue(dataType + "|" + property, out setter))
                throw new InvalidOperationException("TwoWay binding '" + dataType + "." + property +
                                                    "' requires a writable source property.");
            return setter;
        }
    }

    /// <summary>
    /// Rebuilds generated views from their XAML at runtime. While enabled, view and style sources are
    /// polled through <see cref="StyleSheet.SourceLoader"/>; a changed view is rebuilt in place, and a
    /// changed stylesheet rebuilds every tracked view.
    /// </summary>
    internal static class HotReload
    {
        const int PollTicks = 60;

        static readonly List<IHotReloadableView> Views = new List<IHotReloadableView>();
        static readonly HashSet<string> StyleSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, string> Texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static bool _enabled;
        static bool _previousStyleCache = true;
        static int _ticks;

        public static Action<string> Log { get; set; }

        public static bool Enabled
        {
            get { return _enabled; }
            set
            {
                if (_enabled == value)
                    return;
                _enabled = value;
                _ticks = 0;
                if (value)
                {
                    _previousStyleCache = StyleSheet.CacheSources;
                    StyleSheet.CacheSources = false;
                    Texts.Clear();
                    foreach (string source in Sources())
                    {
                        string text;
                        if (TryRead(source, out text))
                            Texts[source] = text;
                    }
                }
                else
                {
                    StyleSheet.CacheSources = _previousStyleCache;
                    Texts.Clear();
                }
            }
        }

        public static int TrackedViewCount => Views.Count;

        internal static void Track(IHotReloadableView view)
        {
            if (view == null)
                return;
            lock (Views)
                if (!Views.Contains(view))
                    Views.Add(view);
        }

        internal static void Untrack(IHotReloadableView view)
        {
            lock (Views)
                Views.Remove(view);
        }

        internal static void NoteStyleSource(string source)
        {
            lock (StyleSources)
                StyleSources.Add(source);
        }

        public static void Update()
        {
            if (!_enabled || ++_ticks < PollTicks)
                return;
            _ticks = 0;
            Poll();
        }

        public static void Reset()
        {
            Enabled = false;
            Views.Clear();
            StyleSources.Clear();
        }

        static void Poll()
        {
            bool stylesChanged = false;
            foreach (string source in new List<string>(StyleSources))
                stylesChanged |= Changed(source);

            var changedViews = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string source in ViewSources())
                if (Changed(source))
                    changedViews.Add(source);

            if (!stylesChanged && changedViews.Count == 0)
                return;
            int reloaded = 0, failed = 0;
            foreach (IHotReloadableView view in Views.ToArray())
            {
                if (!stylesChanged && !changedViews.Contains(view.HotReloadSource))
                    continue;
                if (Reload(view))
                    reloaded++;
                else
                    failed++;
            }
            Report("Hot reload: " + reloaded + " view(s) rebuilt" + (failed > 0 ? ", " + failed + " failed." : "."));
        }

        // Rebuilds every tracked view from its current source; returns how many succeeded.
        public static int ReloadAll()
        {
            int reloaded = 0;
            foreach (IHotReloadableView view in Views.ToArray())
                if (Reload(view))
                    reloaded++;
            return reloaded;
        }

        internal static bool Reload(IHotReloadableView view)
        {
            string text;
            if (!TryRead(view.HotReloadSource, out text))
                return false;
            try
            {
                Apply(view, text);
                return true;
            }
            catch (Exception error)
            {
                Report("Hot reload of '" + view.HotReloadSource + "' failed: " + error.Message);
                return false;
            }
        }

        internal static void Apply(IHotReloadableView view, string xml)
        {
            var target = view as VisualElement;
            if (target == null)
                throw new InvalidOperationException("Hot reload requires a visual view.");

            var context = new HotReloadContext(ControlTemplate.CreateTypeRegistry());
            view.RegisterHotReloadTypes(context);
            UiXmlLoader.Node root = UiXmlLoader.ParseNode(xml);
            var builder = new HotReloadViewBuilder(context);
            VisualElement content = builder.BuildRoot(root, target);
            view.ApplyHotReloadNames(builder.Names);

            view.CompleteHotReload();

            VisualElement previous = GetContent(target);
            builder.ApplyRoot(target);
            SetContent(target, content);
            if (previous != null && !ReferenceEquals(previous, content))
            {
                ReleaseHostedContent(previous);
                previous.Dispose();
            }
        }

        public static T Require<T>(IDictionary<string, Control> names, string name) where T : Control
        {
            Control control;
            if (!names.TryGetValue(name, out control))
                throw new InvalidOperationException("The code-behind requires an element named '" + name + "'.");
            var typed = control as T;
            if (typed == null)
                throw new InvalidOperationException("Element '" + name + "' has an incompatible type.");
            return typed;
        }

        static VisualElement GetContent(VisualElement view)
        {
            var decorator = view as Decorator;
            return decorator != null ? decorator.Child : null;
        }

        static void SetContent(VisualElement view, VisualElement content)
        {
            var decorator = view as Decorator;
            if (decorator == null)
                throw new InvalidOperationException("Hot reload requires a content view.");
            decorator.Child = content;
        }

        // Content hosted by a view-declared ContentPresenter (an app's content, a bound child view) belongs to
        // someone else; detach it so disposing the old tree does not dispose it.
        static void ReleaseHostedContent(VisualElement element)
        {
            var presenter = element as ContentPresenter;
            if (presenter != null && presenter.TemplatedParent == null)
            {
                presenter.Content = null;
                return;
            }
            var children = new List<VisualElement>(element.LogicalChildren);
            for (int i = 0; i < children.Count; i++)
                ReleaseHostedContent(children[i]);
        }

        static bool Changed(string source)
        {
            string text;
            if (!TryRead(source, out text))
                return false;
            string previous;
            if (Texts.TryGetValue(source, out previous) && string.Equals(previous, text, StringComparison.Ordinal))
                return false;
            bool known = previous != null;
            Texts[source] = text;
            return known;
        }

        static bool TryRead(string source, out string text)
        {
            text = null;
            Func<string, string> loader = StyleSheet.SourceLoader;
            if (loader == null || string.IsNullOrWhiteSpace(source))
                return false;
            try
            {
                text = loader(source);
                return text != null;
            }
            catch (Exception error)
            {
                Report("Hot reload could not read '" + source + "': " + error.Message);
                return false;
            }
        }

        static IEnumerable<string> Sources()
        {
            foreach (string source in StyleSources)
                yield return source;
            foreach (string source in ViewSources())
                yield return source;
        }

        static IEnumerable<string> ViewSources()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (IHotReloadableView view in Views)
                if (!string.IsNullOrWhiteSpace(view.HotReloadSource) && seen.Add(view.HotReloadSource))
                    yield return view.HotReloadSource;
        }

        static void Report(string message)
        {
            Action<string> log = Log;
            if (log != null)
                log(message);
        }
    }

    /// <summary>Builds a view tree from XAML with the same semantics as the compiled view generator.</summary>
    internal sealed class HotReloadViewBuilder
    {
        readonly HotReloadContext _context;
        readonly List<Action<VisualElement>> _rootActions = new List<Action<VisualElement>>();

        public HotReloadViewBuilder(HotReloadContext context)
        {
            _context = context;
            Names = new Dictionary<string, Control>(StringComparer.Ordinal);
        }

        public Dictionary<string, Control> Names { get; }

        public VisualElement BuildRoot(UiXmlLoader.Node root, VisualElement view)
        {
            string dataType = GetAttribute(root, "x:DataType");
            string xmlName = root.Name;
            for (int i = 0; i < root.Attributes.Count; i++)
            {
                UiXmlLoader.Attribute attribute = root.Attributes[i];
                if (IsDirective(attribute.Name))
                    continue;
                _rootActions.Add(target => ApplyAttribute(target, xmlName, attribute.Name, attribute.Value, dataType, false));
            }

            VisualElement content = null;
            for (int i = 0; i < root.Children.Count; i++)
            {
                UiXmlLoader.Node child = root.Children[i];
                if (child.IsPropertyElement)
                {
                    if (PropertyName(child) == "Styles")
                    {
                        StyleSheet styles = LoadStyles(child);
                        _rootActions.Add(target => target.Styles = styles);
                    }
                    else
                    {
                        UiXmlLoader.Node propertyNode = child;
                        _rootActions.Add(target => ApplyPropertyElement(target, xmlName, propertyNode, dataType));
                    }
                    continue;
                }
                if (content != null)
                    throw new FormatException("A view accepts at most one content root.");
                content = Build(child, dataType, false);
            }
            if (!HasStyles(root))
                _rootActions.Add(target => target.Styles = null);
            return content;
        }

        public void ApplyRoot(VisualElement view)
        {
            for (int i = 0; i < _rootActions.Count; i++)
                _rootActions[i](view);
        }

        VisualElement Build(UiXmlLoader.Node node, string dataType, bool insideTemplate)
        {
            var element = _context.Types.Create(node.Name) as VisualElement;
            if (element == null)
                throw new FormatException("'" + node.Name + "' is not a visual element.");

            for (int i = 0; i < node.Attributes.Count; i++)
            {
                UiXmlLoader.Attribute attribute = node.Attributes[i];
                if (IsDirective(attribute.Name))
                    continue;
                ApplyAttribute(element, node.Name, attribute.Name, attribute.Value, dataType, insideTemplate);
            }

            for (int i = 0; i < node.Children.Count; i++)
            {
                UiXmlLoader.Node child = node.Children[i];
                if (child.IsPropertyElement)
                {
                    ApplyPropertyElement(element, node.Name, child, dataType);
                    continue;
                }
                VisualElement built = Build(child, dataType, insideTemplate);
                var grid = element as Grid;
                var flyout = element as Acrux.Controls.Flyout;
                if (grid != null)
                    grid.Set(built, built.GridColumn, built.GridRow, built.GridColumnSpan, built.GridRowSpan);
                else if (flyout != null)
                    // A flyout's one child is its content, as the compiled view sets it.
                    flyout.Content = built;
                else
                    element.AddChild(built);
            }
            return element;
        }

        void ApplyAttribute(VisualElement target, string xmlName, string name, string value, string dataType,
            bool insideTemplate)
        {
            if (name == "Name")
            {
                target.Name = value;
                if (insideTemplate)
                    return;
                if (Names.ContainsKey(value))
                    throw new FormatException("Duplicate x:Name '" + value + "'.");
                Names.Add(value, target);
                return;
            }

            if (TypeRegistry.IsEvent(target, name))
            {
                if (insideTemplate)
                    throw new FormatException("Event handlers inside DataTemplate are not supported yet ('" + name + "').");
                Delegate handler = _context.GetHandler(name, value.Trim());
                if (handler != null)
                    TypeRegistry.Subscribe(target, name, handler);
                return;
            }

            BindingMarkup binding;
            bool bound = BindingMarkup.TryParse(value, "Binding", out binding);
            string gridProperty = GridProperty(name);
            if (gridProperty != null)
            {
                Action<object> setGrid = v => SetGrid(target, gridProperty, TypeRegistry.ConvertValue<int>(v));
                if (bound)
                    Bind(target, binding, dataType, setGrid, typeof(int));
                else
                    setGrid(int.Parse(value.Trim(), CultureInfo.InvariantCulture));
                return;
            }

            if (bound)
            {
                ApplyBinding(target, xmlName, name, binding, dataType);
                return;
            }

            string resourceKey;
            if (TryParseDynamicResource(value, out resourceKey))
            {
                FindStyledProperty(target, name).SetDynamicResource(target, resourceKey);
                return;
            }

            _context.Types.SetProperty(target, xmlName, name, value);
        }

        void ApplyBinding(VisualElement target, string xmlName, string name, BindingMarkup binding, string dataType)
        {
            if (binding.Mode == "TwoWay")
            {
                if (binding.ConverterKey != null || !string.IsNullOrWhiteSpace(binding.StringFormat))
                    throw new FormatException("TwoWay bindings with converters or string formats are not supported.");
                RequireDataType(binding, dataType);
                FindStyledProperty(target, name).AttachTwoWay(target, _context.GetGetter(dataType, binding.Path),
                    _context.GetSetter(dataType, binding.Path), binding.Path);
                return;
            }

            if (name == "DataContext" && binding.ConverterKey == null && string.IsNullOrWhiteSpace(binding.StringFormat))
            {
                // As the compiled view: the element's own data context, read from the inherited one.
                RequireDataType(binding, dataType);
                target.BindDataContext<object, object>(_context.GetGetter(dataType, binding.Path), binding.Path);
                return;
            }

            Action<object> setter;
            Type valueType;
            if (!_context.Types.TryGetValueSetter(target, xmlName, name, out setter, out valueType))
                throw new FormatException("Unknown property '" + xmlName + "." + name + "'.");
            Bind(target, binding, dataType, setter, valueType);
        }

        void Bind(VisualElement target, BindingMarkup binding, string dataType, Action<object> setter, Type valueType)
        {
            RequireDataType(binding, dataType);
            Func<object, object> getter = _context.GetGetter(dataType, binding.Path);
            IValueConverter converter = null;
            CultureInfo culture = binding.Culture == null
                ? null
                : CultureInfo.GetCultureInfo(binding.Culture);
            target.Bind<object, object>(getter, value =>
            {
                if (ReferenceEquals(value, BindingOperations.DoNothing))
                    return;
                if (binding.ConverterKey != null)
                {
                    if (converter == null)
                        converter = Acrux.Extensions.ValueConverters.Resolve(target, binding.ConverterKey);
                    value = Acrux.Extensions.ValueConverters.Convert(converter, value, valueType, binding.Parameter,
                        culture ?? CultureInfo.CurrentCulture, target);
                    if (ReferenceEquals(value, BindingOperations.DoNothing))
                        return;
                }
                setter(Acrux.Core.Binding.BindingStringFormat.Apply(value, valueType, binding.StringFormat, culture));
            }, binding.Path);
        }

        void ApplyPropertyElement(VisualElement target, string xmlName, UiXmlLoader.Node node, string dataType)
        {
            string property = PropertyName(node);
            if (property == "Styles")
            {
                target.Styles = LoadStyles(node);
                return;
            }
            if (node.Children.Count != 1)
                throw new FormatException(node.Name + " requires exactly one value.");

            UiXmlLoader.Node value = node.Children[0];
            object built;
            if (property == "ItemsPanel" && value.Name == "ItemsPanelTemplate")
            {
                UiXmlLoader.Node panel = SingleChild(value, "ItemsPanelTemplate requires exactly one panel root.");
                built = new ItemsPanelTemplate(() =>
                {
                    var result = Build(panel, dataType, true) as Panel;
                    if (result == null)
                        throw new FormatException("ItemsPanelTemplate requires a Panel-derived root.");
                    return result;
                });
            }
            // Any template property (ItemTemplate, ContentTemplate, ...), as DataTemplate content.
            else if (value.Name == "DataTemplate")
            {
                UiXmlLoader.Node root = SingleChild(value, "DataTemplate requires exactly one visual root.");
                string templateDataType = GetAttribute(value, "x:DataType") ?? dataType;
                built = new DataTemplate(item =>
                {
                    VisualElement element = Build(root, templateDataType, true);
                    element.DataContext = item;
                    return element;
                });
            }
            else if (value.Name == "ImageBrush" || value.Name == "SolidColorBrush" ||
                     GradientBrushMarkup.IsGradientBrush(value.Name))
                built = UiXmlLoader.BuildValue(value);
            else
                // A single visual, as the compiled view assigns one (Button.Flyout holding a Flyout).
                built = Build(value, dataType, false);

            Action<object> setter;
            Type valueType;
            if (!_context.Types.TryGetValueSetter(target, xmlName, property, out setter, out valueType))
                throw new FormatException("Unknown property '" + xmlName + "." + property + "'.");
            setter(built);
        }

        static StyleSheet LoadStyles(UiXmlLoader.Node node)
        {
            // Style elements (mixed with includes, in document order): one Styles document, as the generator emits.
            bool inline = false;
            for (int i = 0; i < node.Children.Count; i++)
                if (node.Children[i].Xml != null && !node.Children[i].Name.EndsWith("StyleInclude", StringComparison.Ordinal))
                    inline = true;
            if (inline)
            {
                var document = new System.Text.StringBuilder("<Styles>");
                for (int i = 0; i < node.Children.Count; i++)
                {
                    UiXmlLoader.Node child = node.Children[i];
                    if (child.Xml == null)
                        throw new FormatException("Styles requires one or more Style or StyleInclude values.");
                    document.Append(child.Xml);
                }
                document.Append("</Styles>");
                return StyleSheet.LoadInline(document.ToString(), node.Children[0].Namespaces);
            }
            var sources = new List<string>();
            for (int i = 0; i < node.Children.Count; i++)
            {
                UiXmlLoader.Node include = node.Children[i];
                if (!include.Name.EndsWith("StyleInclude", StringComparison.Ordinal))
                    throw new FormatException("Styles requires one or more StyleInclude values.");
                string source = GetAttribute(include, "Source");
                if (string.IsNullOrWhiteSpace(source))
                    throw new FormatException("StyleInclude requires a Source attribute.");
                sources.Add(source);
            }
            if (sources.Count == 0)
                throw new FormatException("Styles requires one or more StyleInclude values.");
            return sources.Count == 1 ? StyleSheet.LoadFromSource(sources[0]) : StyleSheet.LoadFromSources(sources);
        }

        static bool HasStyles(UiXmlLoader.Node root)
        {
            for (int i = 0; i < root.Children.Count; i++)
                if (root.Children[i].IsPropertyElement && PropertyName(root.Children[i]) == "Styles")
                    return true;
            return false;
        }

        static IStyledProperty FindStyledProperty(Control target, string name)
        {
            IList<IStyledProperty> properties = StyledPropertyCatalog.Snapshot();
            IStyledProperty match = null;
            for (int i = 0; i < properties.Count; i++)
                if (string.Equals(properties[i].Name, name, StringComparison.Ordinal) && properties[i].Matches(target))
                    match = properties[i];
            if (match == null)
                throw new FormatException("'" + name + "' is not a styled property of this element.");
            return match;
        }

        static void SetGrid(VisualElement target, string property, int value)
        {
            switch (property)
            {
                case "GridColumn": target.GridColumn = value; break;
                case "GridRow": target.GridRow = value; break;
                case "GridColumnSpan": target.GridColumnSpan = value; break;
                default: target.GridRowSpan = value; break;
            }
        }

        static string GridProperty(string attribute)
        {
            switch (attribute)
            {
                case "Grid.Column": return "GridColumn";
                case "Grid.Row": return "GridRow";
                case "Grid.ColumnSpan": return "GridColumnSpan";
                case "Grid.RowSpan": return "GridRowSpan";
                default: return null;
            }
        }

        static void RequireDataType(BindingMarkup binding, string dataType)
        {
            if (dataType == null)
                throw new FormatException("Binding '" + binding.Path + "' requires x:DataType.");
            if (binding.Path.IndexOf('.') >= 0)
                throw new FormatException("Only one-segment binding paths are supported: '" + binding.Path + "'.");
        }

        static bool TryParseDynamicResource(string value, out string key)
        {
            key = null;
            string text = value == null ? string.Empty : value.Trim();
            const string prefix = "{DynamicResource ";
            if (!text.StartsWith(prefix, StringComparison.Ordinal) || !text.EndsWith("}", StringComparison.Ordinal))
                return false;
            key = text.Substring(prefix.Length, text.Length - prefix.Length - 1).Trim();
            return key.Length > 0;
        }

        static bool IsDirective(string name)
        {
            return name.StartsWith("x:", StringComparison.Ordinal);
        }

        static string PropertyName(UiXmlLoader.Node node)
        {
            int separator = node.Name.LastIndexOf('.');
            return separator < 0 ? node.Name : node.Name.Substring(separator + 1);
        }

        static UiXmlLoader.Node SingleChild(UiXmlLoader.Node node, string error)
        {
            if (node.Children.Count != 1 || node.Children[0].IsPropertyElement)
                throw new FormatException(error);
            return node.Children[0];
        }

        static string GetAttribute(UiXmlLoader.Node node, string name)
        {
            for (int i = 0; i < node.Attributes.Count; i++)
                if (string.Equals(node.Attributes[i].Name, name, StringComparison.Ordinal))
                    return node.Attributes[i].Value;
            return null;
        }
    }
}
