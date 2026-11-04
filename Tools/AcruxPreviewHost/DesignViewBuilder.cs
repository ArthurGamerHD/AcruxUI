using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Acrux.Controls;
using Acrux.Core;
using Acrux.Markup;
using Acrux.Styling;
using VRageMath;

namespace AcruxPreview
{
    /// <summary>
    /// Builds the edited view as the compiled view would be (bindings, converters, templates, dynamic resources),
    /// through Acrux's runtime view builder, in Avalonia's design mode:
    /// <list type="bullet">
    /// <item><c>Design.DataContext</c> (property element or attribute) and <c>d:DataContext</c> give the view's data
    /// context: a mod type created with its parameterless constructor (typically a design-only view model in a
    /// <c>*.AcruxDesign.cs</c> file, which MDK does not pack) or <c>{x:Static prefix:Type.Member}</c>;</item>
    /// <item><c>Design.Width</c> / <c>Design.Height</c> and <c>d:DesignWidth</c> / <c>d:DesignHeight</c> size the view;</item>
    /// <item>other Blend (<c>d:</c>) and mc:Ignorable attributes are dropped, as outside design mode.</item>
    /// </list>
    /// The view's own class (x:Class) is created as well when it can be (with no arguments, or with the design data
    /// context), so resources its code-behind adds (converters) are available; its content is replaced by the edited XAML.
    /// Bindings read the data context by property name, so a design view model need not derive from x:DataType.
    /// </summary>
    sealed class DesignViewBuilder
    {
        const string Blend2008 = "http://schemas.microsoft.com/expression/blend/2008";
        const string MarkupCompatibility = "http://schemas.openxmlformats.org/markup-compatibility/2006";
        static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        readonly ModClient _mod;
        readonly List<ModType> _modTypes = new List<ModType>();
        readonly List<KeyValuePair<string, object>> _contexts = new List<KeyValuePair<string, object>>();
        int _generatedNames;

        /// <summary>A mod control used by the view, under the element name the runtime builder creates it by.</summary>
        sealed class ModType
        {
            public string Alias;
            public Type Type;
            public List<KeyValuePair<string, string>> Attributes;
            // Bound properties of the mod control: property name, then the binding's path.
            public List<KeyValuePair<string, string>> Bindings;
        }

        public DesignViewBuilder(ModClient mod)
        {
            _mod = mod;
        }

        // The design data context of the view's root (null when it declares none).
        public object RootContext { get; private set; }

        // The view's own class, created for its code-behind resources; null when it could not be.
        public Control CodeBehind { get; private set; }

        // The classes the view's design data comes from (their static properties are its design properties).
        public List<Type> DesignTypes { get; } = new List<Type>();

        // The view's x:Class (full name); null when it has none.
        public string ViewClass { get; private set; }

        public float DesignWidth { get; private set; } = float.NaN;
        public float DesignHeight { get; private set; } = float.NaN;

        public List<string> StyleSources { get; } = new List<string>();

        // The view's own Styles as edited, when they hold Style elements: one Styles document (its Style and
        // StyleInclude elements in order, with the view's namespace declarations), as the generator compiles it. The
        // preview applies it in place of the compiled view's, so inline styles follow every edit.
        public string InlineStyles { get; private set; }

        // The edited XAML's root signature (what only takes effect once compiled: Acrux.Generator.ViewRootSignature).
        public string RootSignature { get; private set; }

        // The view's content, built from xaml; an empty Border when it has none.
        public VisualElement Build(string xaml)
        {
            XDocument document = XDocument.Parse(xaml);
            XElement root = document.Root;
            if (root == null)
                throw new FormatException("Empty XAML.");
            RootSignature = Acrux.Generator.ViewRootSignature.Of(root);

            foreach (XElement styles in root.Elements().Where(e => e.Name.LocalName.EndsWith(".Styles", StringComparison.Ordinal)).ToList())
            {
                List<XElement> values = styles.Elements().ToList();
                if (values.Any(e => e.Name.LocalName == "Style"))
                {
                    var inline = new XElement(values[0].Name.Namespace + "Styles",
                        root.Attributes().Where(a => a.IsNamespaceDeclaration).Select(a => new XAttribute(a)),
                        values.Select(e => new XElement(e)));
                    InlineStyles = inline.ToString(SaveOptions.DisableFormatting);
                }
                StyleSources.AddRange(styles.Descendants()
                    .Where(e => e.Name.LocalName == "StyleInclude")
                    .Select(e => (string)e.Attribute("Source"))
                    .Where(s => !string.IsNullOrEmpty(s)));
                styles.Remove();
            }

            // Design mode first (it reads the d: and Design.* markup the clean-up below removes).
            RootContext = ReadDesign(root, true);
            if (RootContext == null && !HasDesignContext(root))
                RootContext = FindDesignContext(root);
            foreach (XElement element in root.Descendants().ToList())
                if (element.Parent != null && !IsDesignPropertyElement(element))
                    ReadDesign(element, false);
            CodeBehind = CreateCodeBehind(root);

            XElement content = root.Elements().FirstOrDefault(e => !e.Name.LocalName.Contains("."));
            if (content == null)
                return new Border();
            Clean(root);
            // Only the root and its content: the root's own attributes and property elements are the view class's.
            var shell = new XElement(root.Name, root.Attributes().Where(a => a.IsNamespaceDeclaration || a.Name == Xaml + "DataType"),
                root.Elements().FirstOrDefault(e => !e.Name.LocalName.Contains(".")));

            var context = new HotReloadContext(ControlTemplate.CreateTypeRegistry());
            string text = shell.ToString(SaveOptions.DisableFormatting);
            UiXmlLoader.Node node = UiXmlLoader.ParseNode(text);
            RegisterBindings(context, node);
            foreach (ModType modType in _modTypes)
            {
                ModType current = modType;
                context.Type<Control>(current.Alias, () => CreateModControl(current));
            }
            var builder = new HotReloadViewBuilder(context);
            VisualElement built = builder.BuildRoot(node, null);
            if (built == null)
                return new Border();
            foreach (var pair in _contexts)
            {
                var element = Find(built, pair.Key);
                if (element != null)
                    element.DataContext = pair.Value;
            }
            ConnectNames(built);
            return built;
        }

        // ------------------------------------------------------------------ design mode

        object ReadDesign(XElement element, bool isRoot)
        {
            object context = null;
            bool hasContext = false;
            foreach (XAttribute attribute in element.Attributes().ToList())
            {
                string name = DesignProperty(attribute);
                if (name == null)
                    continue;
                if (name == "DataContext")
                {
                    context = StaticValue(element, attribute.Value);
                    hasContext = true;
                }
                else if (isRoot && (name == "Width" || name == "Height"))
                {
                    float size;
                    if (float.TryParse(attribute.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out size))
                    {
                        if (name == "Width") DesignWidth = size;
                        else DesignHeight = size;
                    }
                }
            }
            XElement property = element.Elements().FirstOrDefault(e => e.Name.LocalName == "Design.DataContext");
            if (property != null)
            {
                XElement value = property.Elements().FirstOrDefault();
                if (value != null)
                {
                    context = CreateDesignObject(value);
                    hasContext = true;
                }
            }
            if (!hasContext || isRoot)
                return context;
            // A nested element's design data context: set on it once the tree is built.
            string elementName = (string)element.Attribute(Xaml + "Name") ?? (string)element.Attribute("Name");
            if (elementName == null)
            {
                elementName = "__acrux_design_" + _generatedNames++;
                element.SetAttributeValue("Name", elementName);
            }
            _contexts.Add(new KeyValuePair<string, object>(elementName, context));
            return context;
        }

        static bool HasDesignContext(XElement element)
        {
            return element.Attributes().Any(a => DesignProperty(a) == "DataContext") ||
                   element.Elements().Any(e => e.Name.LocalName == "Design.DataContext");
        }

        // A view with an x:DataType but no design data context (a part of a larger view, previewed on its own) still
        // gets one, as it would inside its parent: a static member of that type on a mod class (design data classes,
        // *DesignData, and their Default member first), else a design copy of the view model on its design data class (
        // FooViewModel: FooDesignData; read-only members, the editable design properties left out), else an instance
        // its parameterless constructor makes. Without it every binding of the view would have no source.
        object FindDesignContext(XElement root)
        {
            string dataTypeName = (string)root.Attribute(Xaml + "DataType");
            if (_mod == null || string.IsNullOrWhiteSpace(dataTypeName))
                return null;
            XName qualified = QualifiedName(root, dataTypeName.Trim());
            Type dataType = qualified == null ? null : ResolveType(root, qualified);
            if (dataType == null)
            {
                Program.Log("x:DataType " + dataTypeName + ": type not found in the mod (build the mod)");
                return null;
            }

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var candidates = new List<KeyValuePair<Type, MemberInfo>>();
            foreach (Type type in _mod.Types)
            {
                if (type.IsGenericTypeDefinition)
                    continue;
                foreach (PropertyInfo property in type.GetProperties(flags))
                    if (property.GetIndexParameters().Length == 0 && property.CanRead && dataType.IsAssignableFrom(property.PropertyType))
                        candidates.Add(new KeyValuePair<Type, MemberInfo>(type, property));
                foreach (FieldInfo field in type.GetFields(flags))
                    if (dataType.IsAssignableFrom(field.FieldType))
                        candidates.Add(new KeyValuePair<Type, MemberInfo>(type, field));
            }
            // A design copy of the view model (another type with the same properties) on the view model's design data
            // class, by name: FooViewModel's is FooDesignData. The compiled views bind it through their design binds.
            if (candidates.Count == 0)
            {
                string stem = dataType.Name.EndsWith("ViewModel", StringComparison.Ordinal)
                    ? dataType.Name.Substring(0, dataType.Name.Length - "ViewModel".Length)
                    : dataType.Name;
                // ADK's generated preview clone (FooViewModelPreview.Default) first, then a hand-written FooDesignData.
                foreach (string name in new[] { dataType.Name + "Preview", stem + "DesignData" })
                {
                    Type designData = _mod.Types.FirstOrDefault(t => t.Name == name && t.Namespace == dataType.Namespace);
                    if (designData == null)
                        continue;
                    foreach (PropertyInfo property in designData.GetProperties(flags))
                        if (property.GetIndexParameters().Length == 0 && property.CanRead && !property.CanWrite &&
                            !property.PropertyType.IsPrimitive && property.PropertyType != typeof(string))
                            candidates.Add(new KeyValuePair<Type, MemberInfo>(designData, property));
                    if (candidates.Count > 0)
                        break;
                }
            }
            foreach (var candidate in candidates
                         .OrderBy(c => c.Key.Name.EndsWith("DesignData", StringComparison.Ordinal) ? 0 : 1)
                         .ThenBy(c => c.Value.Name == "Default" ? 0 : 1)
                         .ThenBy(c => c.Key.FullName, StringComparer.Ordinal)
                         .ThenBy(c => c.Value.Name, StringComparer.Ordinal))
            {
                Type owner = candidate.Key;
                DesignProperties.Apply(owner);
                object value;
                try
                {
                    var property = candidate.Value as PropertyInfo;
                    value = property != null ? property.GetValue(null) : ((FieldInfo)candidate.Value).GetValue(null);
                }
                catch (Exception error)
                {
                    Program.Log("design data " + owner.Name + "." + candidate.Value.Name + " failed: " +
                                ((error as TargetInvocationException)?.InnerException ?? error).Message);
                    continue;
                }
                if (value == null)
                    continue;
                if (!DesignTypes.Contains(owner))
                    DesignTypes.Add(owner);
                Program.Log("design data context for " + dataType.Name + ": " + owner.Name + "." + candidate.Value.Name);
                return value;
            }

            if (!dataType.IsAbstract && dataType.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null) != null)
            {
                string error;
                object instance = ModClient.Create(dataType, Enumerable.Empty<KeyValuePair<string, string>>(), out error);
                if (instance != null)
                {
                    Program.Log("design data context for " + dataType.Name + ": a new instance");
                    return instance;
                }
                Program.Log("design data context " + dataType.FullName + " not created: " + error);
            }
            Program.Log("no design data context for " + dataType.Name + " (add Design.DataContext or a *DesignData member of that type)");
            return null;
        }

        // Avalonia maps d:DataContext / d:DesignWidth / d:DesignHeight to Design.DataContext / Width / Height.
        static string DesignProperty(XAttribute attribute)
        {
            if (attribute.Name.NamespaceName == Blend2008)
            {
                switch (attribute.Name.LocalName)
                {
                    case "DataContext": return "DataContext";
                    case "DesignWidth": return "Width";
                    case "DesignHeight": return "Height";
                }
                return null;
            }
            if (attribute.Name.Namespace == XNamespace.None && attribute.Name.LocalName.StartsWith("Design.", StringComparison.Ordinal))
                return attribute.Name.LocalName.Substring("Design.".Length);
            return null;
        }

        object CreateDesignObject(XElement value)
        {
            if (value.Name.LocalName == "Static" && value.Name.Namespace == Xaml)
                return ResolveStatic(value, (string)value.Attribute("Member"));
            Type type = ResolveType(value, value.Name);
            if (type == null)
            {
                Program.Log("design data context " + value.Name + " not found in the mod (build the mod)");
                return null;
            }
            string error;
            object instance = ModClient.Create(type, value.Attributes()
                .Where(a => !a.IsNamespaceDeclaration && a.Name.Namespace == XNamespace.None)
                .Select(a => new KeyValuePair<string, string>(a.Name.LocalName, a.Value)), out error);
            if (instance == null)
                Program.Log("design data context " + type.FullName + " not created: " + error);
            return instance;
        }

        // {x:Static prefix:Type.Member}; other values are plain text.
        object StaticValue(XElement scope, string text)
        {
            string v = (text ?? string.Empty).Trim();
            if (!v.StartsWith("{", StringComparison.Ordinal) || !v.EndsWith("}", StringComparison.Ordinal))
                return text;
            string inner = v.Substring(1, v.Length - 2).Trim();
            int space = inner.IndexOf(' ');
            string extension = space < 0 ? inner : inner.Substring(0, space);
            string argument = space < 0 ? string.Empty : inner.Substring(space + 1).Trim();
            if (argument.StartsWith("Member=", StringComparison.Ordinal))
                argument = argument.Substring("Member=".Length).Trim();
            XName name = QualifiedName(scope, extension);
            if (name == null || name.Namespace != Xaml || name.LocalName != "Static")
            {
                Program.Log("design data context '" + text + "' is not supported (use a type or {x:Static})");
                return null;
            }
            return ResolveStatic(scope, argument);
        }

        object ResolveStatic(XElement scope, string member)
        {
            member = (member ?? string.Empty).Trim();
            int dot = member.LastIndexOf('.');
            XName typeName = dot <= 0 ? null : QualifiedName(scope, member.Substring(0, dot));
            Type type = typeName == null ? null : ResolveType(scope, typeName);
            if (type == null)
            {
                Program.Log("x:Static " + member + ": type not found in the mod (build the mod)");
                return null;
            }
            string memberName = member.Substring(dot + 1);
            // The class's design properties first (defaults, then the plugin's values), as the member may read them.
            DesignProperties.Apply(type);
            if (!DesignTypes.Contains(type))
                DesignTypes.Add(type);
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy;
            try
            {
                PropertyInfo property = type.GetProperty(memberName, flags);
                if (property != null)
                    return property.GetValue(null);
                FieldInfo field = type.GetField(memberName, flags);
                if (field != null)
                    return field.GetValue(null);
            }
            catch (TargetInvocationException error)
            {
                Program.Log("x:Static " + member + " failed: " + (error.InnerException ?? error));
                return null;
            }
            Program.Log("x:Static " + member + ": no static member " + memberName);
            return null;
        }

        static XName QualifiedName(XElement scope, string qualified)
        {
            int colon = qualified.IndexOf(':');
            XNamespace ns = colon < 0 ? scope.GetDefaultNamespace() : scope.GetNamespaceOfPrefix(qualified.Substring(0, colon));
            return ns == null ? null : ns + qualified.Substring(colon + 1);
        }

        Type ResolveType(XElement scope, XName name)
        {
            string ns = name.NamespaceName;
            string clr = null;
            if (ns.StartsWith("using:", StringComparison.Ordinal))
                clr = ns.Substring("using:".Length);
            else if (ns.StartsWith("clr-namespace:", StringComparison.Ordinal))
            {
                clr = ns.Substring("clr-namespace:".Length);
                int assembly = clr.IndexOf(';');
                if (assembly >= 0)
                    clr = clr.Substring(0, assembly);
            }
            return clr == null || _mod == null ? null : _mod.FindType(clr.Trim(), name.LocalName);
        }

        // The view's class, for the resources its constructor adds: no arguments, or the design data context.
        Control CreateCodeBehind(XElement root)
        {
            string className = (string)root.Attribute(Xaml + "Class");
            ViewClass = className;
            if (_mod == null || string.IsNullOrEmpty(className))
                return null;
            int dot = className.LastIndexOf('.');
            Type type = dot < 0 ? null : _mod.FindType(className.Substring(0, dot), className.Substring(dot + 1));
            if (type == null)
                return null;
            foreach (ConstructorInfo constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                         .OrderBy(c => c.GetParameters().Length))
            {
                ParameterInfo[] parameters = constructor.GetParameters();
                object[] arguments;
                if (parameters.Length == 0)
                    arguments = new object[0];
                else if (parameters.Length == 1 && RootContext != null && parameters[0].ParameterType.IsInstanceOfType(RootContext))
                    arguments = new[] { RootContext };
                else
                    continue;
                try
                {
                    object view = constructor.Invoke(arguments);
                    _codeBehindWrapper = view;
                    return _mod.NativeOf(view) as Control;
                }
                catch (Exception error)
                {
                    Program.Log("view class " + type.FullName + " not created: " + ((error as TargetInvocationException)?.InnerException ?? error).Message);
                    return null;
                }
            }
            return null;
        }

        object _codeBehindWrapper;

        // As the view's InitializeComponent would with the edited XAML: its x:Name fields refer to the edited content
        // (which the preview hosts in the view class), not to the content the class built from its compiled XAML.
        void ConnectNames(VisualElement built)
        {
            if (_codeBehindWrapper == null)
                return;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            Type type = _codeBehindWrapper.GetType();
            foreach (FieldInfo field in type.GetFields(flags))
            {
                if (field.IsInitOnly && field.IsStatic)
                    continue;
                VisualElement element = Find(built, field.Name);
                if (element == null)
                    continue;
                object wrapper = _mod.WrapperOf(element);
                if (wrapper != null && field.FieldType.IsInstanceOfType(wrapper))
                {
                    try
                    {
                        field.SetValue(_codeBehindWrapper, wrapper);
                    }
                    catch (Exception error)
                    {
                        Program.Log("view class field " + field.Name + " not connected: " + error.Message);
                    }
                }
                else
                    Program.Log("view class field " + field.Name + " not connected (" + (wrapper == null ? "no wrapper" : wrapper.GetType().Name) + ")");
            }
            // As after a hot reload of the view: its code-behind re-attaches what it wires to named elements.
            MethodInfo reloaded = type.GetMethod("OnHotReloaded", flags, null, Type.EmptyTypes, null);
            if (reloaded == null)
                return;
            try
            {
                reloaded.Invoke(_codeBehindWrapper, null);
            }
            catch (TargetInvocationException error)
            {
                Program.Log("view class OnHotReloaded failed: " + (error.InnerException ?? error).Message);
            }
        }

        // ------------------------------------------------------------------ clean-up for the runtime builder

        // Removes design-time markup, keeps what the runtime builder knows: Acrux elements, x:Name / x:DataType, and the
        // mod's controls as slots filled with the real control after the build.
        void Clean(XElement element)
        {
            foreach (XElement child in element.Elements().ToList())
            {
                if (IsDesignPropertyElement(child))
                {
                    child.Remove();
                    continue;
                }
                if (child.Name.LocalName.Contains(".") && !IsBuildableProperty(child))
                {
                    Program.Log("preview skips property element " + child.Name.LocalName);
                    child.Remove();
                    continue;
                }
                if (IsModControl(child))
                {
                    XElement replacement = ModElementFor(child);
                    child.ReplaceWith(replacement);
                    Clean(replacement);
                    continue;
                }
                Clean(child);
            }
            foreach (XAttribute attribute in element.Attributes().ToList())
            {
                if (attribute.IsNamespaceDeclaration)
                    continue;
                XNamespace ns = attribute.Name.Namespace;
                if (ns == Xaml)
                {
                    if (attribute.Name.LocalName != "Name" && attribute.Name.LocalName != "DataType")
                        attribute.Remove();
                    continue;
                }
                if (ns != XNamespace.None || DesignProperty(attribute) != null)
                {
                    attribute.Remove();
                    continue;
                }
                string value = attribute.Value;
                string replaced;
                if (TryRewriteValue(attribute.Name.LocalName, value, out replaced))
                {
                    if (replaced == null)
                        attribute.Remove();
                    else
                        attribute.Value = replaced;
                }
            }
        }

        // {StaticResource k} resolves like {DynamicResource k} here (the preview's resources are all dynamic); a mod's
        // own markup extension (e.g. {ext:loc Key}) shows its localized text, or is dropped where it is not text.
        static bool TryRewriteValue(string property, string value, out string replaced)
        {
            replaced = null;
            string v = (value ?? string.Empty).Trim();
            if (!v.StartsWith("{", StringComparison.Ordinal) || !v.EndsWith("}", StringComparison.Ordinal) || v.StartsWith("{}", StringComparison.Ordinal))
                return false;
            string inner = v.Substring(1, v.Length - 2).Trim();
            int space = inner.IndexOf(' ');
            string name = space < 0 ? inner : inner.Substring(0, space);
            string rest = space < 0 ? string.Empty : inner.Substring(space + 1).Trim();
            if (name == "StaticResource")
            {
                replaced = "{DynamicResource " + rest + "}";
                return true;
            }
            if (name.IndexOf(':') <= 0 || name.StartsWith("x:", StringComparison.Ordinal))
                return false;
            if (!IsTextProperty(property))
                return true;
            string localized;
            string key = rest.Length == 0 ? name : rest;
            replaced = PreviewLocalization.TryGet(key, out localized) ? localized : key;
            return true;
        }

        static bool IsTextProperty(string name)
        {
            return name == "Text" || name == "Title" || name == "PlaceholderText" || name == "Header" || name == "ToolTip.Tip";
        }

        static bool IsDesignPropertyElement(XElement element)
        {
            return element.Name.LocalName.StartsWith("Design.", StringComparison.Ordinal);
        }

        // Property elements the runtime builder can assign (as the compiled view does).
        static bool IsBuildableProperty(XElement element)
        {
            string property = element.Name.LocalName.Substring(element.Name.LocalName.LastIndexOf('.') + 1);
            XElement value = element.Elements().FirstOrDefault();
            if (value == null)
                return false;
            string kind = value.Name.LocalName;
            return (property == "ItemsPanel" && kind == "ItemsPanelTemplate") ||
                   kind == "DataTemplate" ||
                   kind == "ImageBrush" || kind == "SolidColorBrush";
        }

        bool IsModControl(XElement element)
        {
            string ns = element.Name.NamespaceName;
            return ns.StartsWith("using:", StringComparison.Ordinal) || ns.StartsWith("clr-namespace:", StringComparison.Ordinal);
        }

        // The mod's control: created by the mod (literal values of its own properties set on it), under an alias the
        // runtime builder knows; the builder still applies what every control has (layout, classes, bindings of those).
        XElement ModElementFor(XElement element)
        {
            Type type = ResolveType(element, element.Name);
            var common = element.Attributes().Where(a => a.IsNamespaceDeclaration || a.Name.Namespace != XNamespace.None || IsCommonAttribute(a.Name.LocalName)).ToList();
            XElement result;
            if (type == null)
            {
                result = new XElement("Border",
                    new XAttribute("Background", "#2A3440"), new XAttribute("BorderBrush", "#5A7088"),
                    new XAttribute("BorderThickness", "1"), new XAttribute("MinHeight", "32"),
                    new XElement("TextBlock", new XAttribute("Text", element.Name.LocalName + " (mod control; build the mod)"),
                        new XAttribute("Margin", "6"), new XAttribute("Foreground", "#A0B4C8")));
                foreach (XAttribute attribute in common.Where(a => a.Name.Namespace == XNamespace.None && !a.IsNamespaceDeclaration))
                    result.SetAttributeValue(attribute.Name, attribute.Value);
                return result;
            }
            var modType = new ModType
            {
                Alias = "AcruxPreviewMod" + _modTypes.Count,
                Type = type,
                Attributes = element.Attributes()
                    .Where(a => a.Name.Namespace == XNamespace.None && !IsCommonAttribute(a.Name.LocalName) &&
                                !a.Value.TrimStart().StartsWith("{", StringComparison.Ordinal))
                    .Select(a => new KeyValuePair<string, string>(a.Name.LocalName, a.Value)).ToList(),
                Bindings = new List<KeyValuePair<string, string>>(),
            };
            _modTypes.Add(modType);
            result = new XElement(modType.Alias, element.Nodes());
            foreach (XAttribute attribute in common)
                result.Add(new XAttribute(attribute));
            foreach (XAttribute attribute in element.Attributes()
                         .Where(a => a.Name.Namespace == XNamespace.None && !IsCommonAttribute(a.Name.LocalName) &&
                                     a.Value.TrimStart().StartsWith("{", StringComparison.Ordinal)))
            {
                // A plain {Binding Path} on the mod's own property: bound here (one way) as the compiled view binds it.
                BindingMarkup binding;
                if (BindingMarkup.TryParse(attribute.Value, "Binding", out binding) && string.IsNullOrEmpty(binding.ConverterKey) &&
                    string.IsNullOrEmpty(binding.StringFormat) && (binding.Path ?? string.Empty).IndexOf('.') < 0)
                    modType.Bindings.Add(new KeyValuePair<string, string>(attribute.Name.LocalName, binding.Path ?? string.Empty));
                else
                    Program.Log("preview leaves " + type.Name + "." + attribute.Name.LocalName + " unset (" + attribute.Value + ")");
            }
            return result;
        }

        Control CreateModControl(ModType modType)
        {
            string error;
            object wrapper;
            var control = _mod.CreateControl(modType.Type, modType.Attributes, out wrapper, out error) as Control;
            if (control != null)
            {
                var element = control as VisualElement;
                if (element != null)
                    foreach (var binding in modType.Bindings)
                    {
                        string property = binding.Key;
                        string path = binding.Value;
                        if (property == "DataContext")
                        {
                            // As the compiled view: the element's own data context, read from the inherited one.
                            element.BindDataContext<object, object>(
                                source => path.Length == 0 ? source : _mod.ReadModProperty(source, path),
                                path.Length == 0 ? property : path);
                            continue;
                        }
                        element.Bind<object, object>(
                            source => path.Length == 0 ? source : _mod.ReadModProperty(source, path),
                            value =>
                            {
                                if (!ReferenceEquals(value, Acrux.Data.BindingOperations.DoNothing))
                                    _mod.WriteProperty(wrapper, property, value);
                            },
                            path.Length == 0 ? property : path);
                    }
                return control;
            }
            Program.Log("mod control " + modType.Type.FullName + " not created: " + error);
            return new TextBlock { Text = modType.Type.Name + " (mod control: " + (error ?? "not a control") + ")", Margin = new Vector4(6f) };
        }

        // What the runtime builder sets on any control (TypeRegistry's common properties), plus attached layout.
        static bool IsCommonAttribute(string name)
        {
            switch (name)
            {
                case "Name": case "Classes": case "IsVisible": case "IsEnabled": case "Cursor":
                case "Width": case "Height": case "MinWidth": case "MinHeight": case "MaxWidth": case "MaxHeight":
                case "Margin": case "ClipToBounds": case "OpacityMask": case "RenderTransform":
                case "HorizontalAlignment": case "VerticalAlignment":
                    return true;
            }
            return name.StartsWith("Grid.", StringComparison.Ordinal) || name.StartsWith("ToolTip.", StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------ bindings

        // The compiled view reads bound properties through generated accessors; here every binding path under any
        // x:DataType reads the data context's property of that name (by reflection), whatever its type.
        void RegisterBindings(HotReloadContext context, UiXmlLoader.Node root)
        {
            var dataTypes = new HashSet<string>(StringComparer.Ordinal);
            var paths = new HashSet<string>(StringComparer.Ordinal);
            Collect(root, dataTypes, paths);
            foreach (string dataType in dataTypes)
                foreach (string path in paths)
                {
                    string property = path;
                    context.Getter(dataType, property, source => _mod.ReadProperty(source, property));
                    context.Setter(dataType, property, (source, value) => _mod.WriteProperty(source, property, value));
                }
        }

        static void Collect(UiXmlLoader.Node node, HashSet<string> dataTypes, HashSet<string> paths)
        {
            foreach (UiXmlLoader.Attribute attribute in node.Attributes)
            {
                if (attribute.Name == "x:DataType")
                {
                    dataTypes.Add(attribute.Value);
                    continue;
                }
                BindingMarkup binding;
                if (BindingMarkup.TryParse(attribute.Value, "Binding", out binding) && !string.IsNullOrEmpty(binding.Path))
                    paths.Add(binding.Path);
            }
            foreach (UiXmlLoader.Node child in node.Children)
                Collect(child, dataTypes, paths);
        }

        static VisualElement Find(VisualElement element, string name)
        {
            if (element == null)
                return null;
            if (element.Name == name)
                return element;
            foreach (VisualElement child in element.LogicalChildren)
            {
                VisualElement found = Find(child, name);
                if (found != null)
                    return found;
            }
            return null;
        }
    }
}
