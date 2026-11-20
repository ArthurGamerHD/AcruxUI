using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using Adk.Analyzer;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Acrux.Generator;

[Generator]
public sealed class AcruxViewGenerator : IIncrementalGenerator
{
    const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
    const string XamlNamespaceHttps = "https://schemas.microsoft.com/winfx/2006/xaml";
    const string AcruxNamespace = "https://github.com/ArthurGamerHD/AcruxUI";
    static bool IsAcruxNamespace(string ns)
    {
        return ns == AcruxNamespace;
    }

    static readonly DiagnosticDescriptor InvalidView = CreateDiagnostic(
        "ACRUX001", "Invalid Acrux view", "{0}");
    static readonly DiagnosticDescriptor UnknownType = CreateDiagnostic(
        "ACRUX002", "Unknown Acrux view type", "{0}");
    static readonly DiagnosticDescriptor UnknownProperty = CreateDiagnostic(
        "ACRUX003", "Unknown Acrux view property", "{0}");
    static readonly DiagnosticDescriptor InvalidBinding = CreateDiagnostic(
        "ACRUX004", "Invalid compiled binding", "{0}");
    static readonly DiagnosticDescriptor UnsupportedMarkup = CreateDiagnostic(
        "ACRUX005", "Unsupported Acrux markup", "{0}");
    static readonly DiagnosticDescriptor UnobservableDataType = CreateDiagnostic(
        "ACRUX010", "View models must be observable", "{0}");
    static readonly DiagnosticDescriptor UnknownEventHandler = CreateDiagnostic(
        "ACRUX012", "Unknown Acrux event handler", "{0}");
    static readonly DiagnosticDescriptor UnresolvedStyleInclude = new DiagnosticDescriptor(
        "ACRUX006",
        "Unresolved Acrux StyleInclude",
        "StyleInclude source '{0}' does not point to an included XAML asset",
        "Acrux.Generator",
        DiagnosticSeverity.Warning,
        true);

    static readonly IReadOnlyDictionary<string, string> BuiltInTypes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["UserControl"] = "Acrux.Controls.UserControl",
            ["Window"] = "Acrux.Controls.Window",
            ["Grid"] = "Acrux.Layout.Grid",
            ["StackPanel"] = "Acrux.Layout.StackPanel",
            ["WrapPanel"] = "Acrux.Layout.WrapPanel",
            ["UniformGrid"] = "Acrux.Layout.UniformGrid",
            ["VirtualizingCarouselPanel"] = "Acrux.Layout.VirtualizingCarouselPanel",
            ["VirtualizingStackPanel"] = "Acrux.Layout.VirtualizingStackPanel",
            ["Carousel"] = "Acrux.Controls.Carousel",
            ["Arc"] = "Acrux.Controls.Shapes.Arc",
            ["ScrollViewer"] = "Acrux.Layout.ScrollViewer",
            ["ScrollBar"] = "Acrux.Controls.Primitives.ScrollBar",
            ["Track"] = "Acrux.Controls.Primitives.Track",
            ["Thumb"] = "Acrux.Controls.Primitives.Thumb",
            ["ToggleButton"] = "Acrux.Controls.Primitives.ToggleButton",
            ["ToolTip"] = "Acrux.Controls.ToolTip",
            ["Menu"] = "Acrux.Controls.Menu",
            ["ContextMenu"] = "Acrux.Controls.ContextMenu",
            ["Flyout"] = "Acrux.Controls.Flyout",
            ["MenuItem"] = "Acrux.Controls.MenuItem",
            ["Border"] = "Acrux.Controls.Border",
            ["Button"] = "Acrux.Controls.Button",
            ["CheckBox"] = "Acrux.Controls.CheckBox",
            ["ComboBox"] = "Acrux.Controls.ComboBox",
            ["ContentPresenter"] = "Acrux.Controls.ContentPresenter",
            ["DropDownButton"] = "Acrux.Controls.DropDownButton",
            ["ItemsControl"] = "Acrux.Controls.ItemsControl",
            ["Label"] = "Acrux.Controls.Label",
            ["ListBox"] = "Acrux.Controls.ListBox",
            ["NumericUpDown"] = "Acrux.Controls.NumericUpDown",
            ["ProgressBar"] = "Acrux.Controls.ProgressBar",
            ["RepeatButton"] = "Acrux.Controls.RepeatButton",
            ["Slider"] = "Acrux.Controls.Slider",
            ["TabControl"] = "Acrux.Controls.TabControl",
            ["TabItem"] = "Acrux.Controls.TabItem",
            ["TextBlock"] = "Acrux.Controls.TextBlock",
            ["TextBox"] = "Acrux.Controls.TextBox",
            ["Image"] = "Acrux.Controls.Image",
            ["Viewbox"] = "Acrux.Layout.Viewbox",
            ["ToggleSwitch"] = "Acrux.Controls.ToggleSwitch"
        };

    // Acrux markup files: ".xaml", or ".axaml" as tooling (e.g. the IDE previewer) expects.
    internal static bool IsXamlFile(string path)
    {
        return path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase);
    }

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var xamlAssets = context.AdditionalTextsProvider
            .Where(static file => IsXamlFile(file.Path))
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Select(static (input, cancellationToken) =>
                ReadXamlAsset(input.Left, input.Right, cancellationToken))
            .Collect();

        var views = context.AdditionalTextsProvider
            .Where(static file =>
                file.Path.EndsWith(".acrux.xaml", StringComparison.OrdinalIgnoreCase) ||
                file.Path.EndsWith("View.xaml", StringComparison.OrdinalIgnoreCase) ||
                file.Path.EndsWith(".acrux.axaml", StringComparison.OrdinalIgnoreCase) ||
                file.Path.EndsWith("View.axaml", StringComparison.OrdinalIgnoreCase))
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Select(static (input, cancellationToken) => ReadView(input.Left, input.Right, cancellationToken));

        context.RegisterSourceOutput(
            "gui",
            views.Combine(context.CompilationProvider.Combine(DesignSources.GetProvider(context))),
            static (productionContext, input) =>
                Generate(input.Left, input.Right.Left, input.Right.Right, productionContext));

        context.RegisterSourceOutput(
            "gui-style-includes",
            xamlAssets,
            static (productionContext, assets) =>
                ValidateStyleIncludes(assets, productionContext));
    }

    static XamlAsset ReadXamlAsset(
        AdditionalText file,
        AnalyzerConfigOptionsProvider optionsProvider,
        CancellationToken cancellationToken)
    {
        SourceText text = file.GetText(cancellationToken) ?? SourceText.From(string.Empty);
        string link;
        optionsProvider.GetOptions(file).TryGetValue(
            "build_metadata.AdditionalFiles.Link", out link);
        return new XamlAsset(file.Path, link, text);
    }

    static void ValidateStyleIncludes(
        IReadOnlyList<XamlAsset> assets,
        SourceProductionContext context)
    {
        for (int assetIndex = 0; assetIndex < assets.Count; assetIndex++)
        {
            XamlAsset asset = assets[assetIndex];
            if (asset.Text.Length == 0)
                continue;

            XDocument document;
            try
            {
                document = XDocument.Parse(
                    asset.Text.ToString(),
                    LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
                DesignTimeMarkup.Strip(document);
            }
            catch (Exception error) when (error is XmlException || error is InvalidOperationException)
            {
                continue;
            }

            foreach (XElement include in document.Descendants()
                         .Where(element => element.Name.LocalName == "StyleInclude"))
            {
                XAttribute sourceAttribute = include.Attribute("Source");
                if (sourceAttribute == null || string.IsNullOrWhiteSpace(sourceAttribute.Value))
                    continue;
                if (StyleSourceExists(asset, sourceAttribute.Value, assets))
                    continue;

                context.ReportDiagnostic(Diagnostic.Create(
                    UnresolvedStyleInclude,
                    GetAttributeLocation(asset, sourceAttribute),
                    sourceAttribute.Value));
            }
        }
    }

    static bool StyleSourceExists(
        XamlAsset declaringAsset,
        string source,
        IReadOnlyList<XamlAsset> assets)
    {
        string trimmed = source.Trim().Replace('\\', '/');
        bool rooted = trimmed.StartsWith("/", StringComparison.Ordinal);
        string runtimeTarget = null;
        string physicalTarget = null;

        if (rooted)
        {
            runtimeTarget = NormalizeAssetPath(trimmed.Substring(1));
            // Acrux and toolkit styles ship with the toolkit mod, not with its clients.
            if (runtimeTarget.StartsWith("Data/Gui/Styles/Acrux/", StringComparison.OrdinalIgnoreCase) ||
                runtimeTarget.StartsWith("Data/Gui/Styles/Toolkit/", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        else
        {
            if (!string.IsNullOrEmpty(declaringAsset.RuntimePath))
                runtimeTarget = ResolveAssetPath(declaringAsset.RuntimePath, trimmed);
            string directory = Path.GetDirectoryName(declaringAsset.PhysicalPath);
            if (!string.IsNullOrEmpty(directory))
                physicalTarget = NormalizePhysicalPath(Path.Combine(directory, trimmed));
        }

        for (int i = 0; i < assets.Count; i++)
        {
            XamlAsset candidate = assets[i];
            if (!string.IsNullOrEmpty(runtimeTarget) &&
                string.Equals(candidate.RuntimePath, runtimeTarget, StringComparison.OrdinalIgnoreCase))
                return true;
            if (!string.IsNullOrEmpty(physicalTarget) &&
                string.Equals(candidate.NormalizedPhysicalPath, physicalTarget, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    static string ResolveAssetPath(string declaringPath, string relativeSource)
    {
        int separator = declaringPath.LastIndexOf('/');
        return NormalizeAssetPath(separator < 0
            ? relativeSource
            : declaringPath.Substring(0, separator + 1) + relativeSource);
    }

    static string NormalizeAssetPath(string path)
    {
        string[] parts = (path ?? string.Empty).Replace('\\', '/').Split('/');
        var normalized = new List<string>(parts.Length);
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            if (part.Length == 0 || part == ".")
                continue;
            if (part == "..")
            {
                if (normalized.Count > 0)
                    normalized.RemoveAt(normalized.Count - 1);
                continue;
            }
            normalized.Add(part);
        }
        return string.Join("/", normalized);
    }

    static string NormalizePhysicalPath(string path)
    {
        try
        {
            return Path.GetFullPath(path).Replace('\\', '/');
        }
        catch
        {
            return (path ?? string.Empty).Replace('\\', '/');
        }
    }

    static Location GetAttributeLocation(XamlAsset asset, XAttribute attribute)
    {
        IXmlLineInfo lineInfo = attribute;
        if (!lineInfo.HasLineInfo())
            return Location.None;
        int lineIndex = lineInfo.LineNumber - 1;
        if (lineIndex < 0 || lineIndex >= asset.Text.Lines.Count)
            return Location.None;

        TextLine line = asset.Text.Lines[lineIndex];
        int column = Math.Max(0, lineInfo.LinePosition - 1);
        int start = Math.Min(line.End, line.Start + column);
        int length = Math.Min(
            Math.Max(1, attribute.ToString().Length),
            Math.Max(0, line.End - start));
        var span = new TextSpan(start, length);
        return Location.Create(asset.PhysicalPath, span, asset.Text.Lines.GetLinePositionSpan(span));
    }

    static ViewFile ReadView(
        AdditionalText file,
        AnalyzerConfigOptionsProvider optionsProvider,
        CancellationToken cancellationToken)
    {
        var text = file.GetText(cancellationToken);
        string link, projectDirectory;
        optionsProvider.GetOptions(file).TryGetValue("build_metadata.AdditionalFiles.Link", out link);
        optionsProvider.GlobalOptions.TryGetValue("build_property.ProjectDir", out projectDirectory);
        return new ViewFile(file.Path, text == null ? string.Empty : text.ToString(),
            GetRuntimeViewPath(file.Path, link, projectDirectory));
    }

    static string GetRuntimeViewPath(string path, string link, string projectDirectory)
    {
        string physical = path.Replace('\\', '/');
        string relative;
        if (!string.IsNullOrWhiteSpace(link))
            relative = link.Replace('\\', '/');
        else if (!string.IsNullOrWhiteSpace(projectDirectory) &&
                 physical.StartsWith(projectDirectory.Replace('\\', '/').TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase))
            relative = physical.Substring(projectDirectory.Replace('\\', '/').TrimEnd('/').Length + 1);
        else
            relative = physical.Substring(physical.LastIndexOf('/') + 1);
        relative = relative.TrimStart('/');
        if (relative.StartsWith("Content/", StringComparison.OrdinalIgnoreCase))
            relative = relative.Substring("Content/".Length);
        return relative;
    }

    static void Generate(
        ViewFile file,
        Compilation compilation,
        DesignSources design,
        SourceProductionContext context)
    {
        if (string.IsNullOrWhiteSpace(file.Text))
        {
            Report(context, InvalidView, file.Path + " is empty.");
            return;
        }

        XDocument document;
        string rootSignature = null;
        try
        {
            document = XDocument.Parse(
                file.Text,
                LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
            if (document.Root != null)
                rootSignature = ViewRootSignature.Of(document.Root);
            DesignTimeMarkup.Strip(document);
        }
        catch (Exception error) when (error is XmlException || error is InvalidOperationException)
        {
            Report(context, InvalidView, file.Path + ": " + error.Message);
            return;
        }

        XElement root = document.Root;
        if (root == null)
        {
            Report(context, InvalidView, file.Path + " requires a root element.");
            return;
        }
        if (!IsAcruxNamespace(root.Name.NamespaceName) &&
            !root.Name.NamespaceName.StartsWith("using:", StringComparison.Ordinal))
        {
            Report(context, InvalidView,
                file.Path + " must use the Acrux namespace or a using: namespace.");
            return;
        }

        string className = GetDirective(root, "Class");
        if (string.IsNullOrWhiteSpace(className))
        {
            Report(context, InvalidView, file.Path + " requires x:Class.");
            return;
        }

        INamedTypeSymbol viewType = compilation.GetTypeByMetadataName(className.Trim());
        if (viewType == null)
        {
            Report(context, UnknownType,
                file.Path + " references missing backing class '" + className + "'.");
            return;
        }
        if (viewType.ContainingType != null || viewType.TypeParameters.Length != 0)
        {
            Report(context, UnsupportedMarkup,
                file.Path + " backing classes must be non-generic top-level types.");
            return;
        }
        if (!IsPartial(viewType))
        {
            Report(context, InvalidView,
                "Backing class '" + className + "' must be partial.");
            return;
        }

        INamedTypeSymbol userControlType =
            compilation.GetTypeByMetadataName("Acrux.Controls.UserControl");
        if (userControlType == null || !InheritsFrom(viewType, userControlType))
        {
            Report(context, InvalidView,
                "Backing class '" + className + "' must derive from Acrux.Controls.UserControl.");
            return;
        }

        INamedTypeSymbol rootType = ResolveMarkupType(root, compilation);
        if (rootType == null ||
            !SymbolEqualityComparer.Default.Equals(viewType, rootType) &&
            !InheritsFrom(viewType, rootType))
        {
            Report(context, InvalidView,
                file.Path + " root '" + root.Name.LocalName +
                "' must be a base type of backing class '" + className + "'.");
            return;
        }

        ITypeSymbol dataType = ResolveDataType(root, root, compilation, context, file.Path);
        if (!string.IsNullOrWhiteSpace(GetDirective(root, "DataType")) && dataType == null)
            return;

        var emitter = new ViewEmitter(file.Path, compilation, context, root);
        string body = emitter.EmitInitializeBody(root, viewType, dataType);
        if (emitter.HasErrors)
            return;

        string dataTypeKey = ResolveDataTypeKey(root, GetDirective(root, "DataType"));
        if (dataType != null && dataTypeKey != null)
            emitter.AddDataType(dataTypeKey, dataType);
        string source = BuildSource(viewType, emitter, body, file.RuntimePath, compilation);
        // A view that names a design-only type (its class, its x:DataType, a control) is design code itself.
        bool designView = design.Touches(viewType) ||
                          (dataType != null && design.Touches(dataType)) ||
                          emitter.ElementTypes.Any(design.Touches) ||
                          emitter.DataTypes.Values.Any(design.Touches);
        context.AddSource(design.HintName(GetHintName(viewType), designView), source);
        // The IDE preview's check that the compiled root matches the edited XAML (design code: MDK does not pack it).
        if (rootSignature != null)
            context.AddSource(design.HintName(GetHintName(viewType).Replace(".AcruxView.g.cs", ".AcruxViewRoot.g.cs"), true),
                "// <auto-generated/>\n[assembly: global::System.Reflection.AssemblyMetadata(" +
                SymbolDisplay.FormatLiteral(ViewRootSignature.MetadataPrefix + viewType.ToDisplayString(), true) + ", " +
                SymbolDisplay.FormatLiteral(rootSignature, true) + ")]\n");
        if (emitter.DesignAccessors.Count > 0)
        {
            List<INamedTypeSymbol> designTypes = CollectDesignTypes(compilation.Assembly.GlobalNamespace, design, new List<INamedTypeSymbol>());
            designTypes.Sort((a, b) => string.CompareOrdinal(a.ToDisplayString(), b.ToDisplayString()));
            var clones = PreviewClones.Plan(compilation, design).Values.OrderBy(c => c.ModelFullName, StringComparer.Ordinal).ToList();
            string designSource = BuildDesignSource(viewType, emitter.DesignAccessors, designTypes, clones, compilation);
            if (designSource != null)
                context.AddSource(design.HintName(GetHintName(viewType), true), designSource);
        }
    }

    static string BuildSource(
        INamedTypeSymbol viewType,
        ViewEmitter emitter,
        string initializeBody,
        string runtimePath,
        Compilation compilation)
    {
        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated/>");
        if (!viewType.ContainingNamespace.IsGlobalNamespace)
        {
            builder.Append("namespace ");
            builder.AppendLine(viewType.ContainingNamespace.ToDisplayString());
            builder.AppendLine("{");
        }

        builder.Append("    partial class ");
        builder.AppendLine(viewType.Name);
        builder.AppendLine("    {");
        builder.AppendLine("        private bool __acruxContentLoaded;");
        AppendDesignDeclarations(builder, emitter.DesignAccessors);
        foreach (NamedElement named in emitter.NamedElements)
        {
            builder.Append("        private ");
            builder.Append(named.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
            builder.Append(' ');
            builder.Append(named.Name);
            builder.AppendLine(";");
        }

        builder.AppendLine();
        builder.AppendLine("        partial void OnHotReloaded();");
        builder.AppendLine();
        builder.AppendLine("        private void InitializeComponent()");
        builder.AppendLine("        {");
        builder.AppendLine("            if (__acruxContentLoaded)");
        builder.AppendLine("                return;");
        builder.AppendLine("            __acruxContentLoaded = true;");
        builder.Append(initializeBody);
        // An Acrux UI Toolkit client compiles thin wrappers without the markup runtime: no hot reload there.
        bool hotReload = compilation.GetTypeByMetadataName("Acrux.Markup.HotReload") != null;
        if (hotReload)
        {
            builder.AppendLine("#if DEBUG");
            builder.AppendLine("            global::Acrux.Markup.HotReload.Track(this);");
            builder.AppendLine("#endif");
        }
        builder.AppendLine("        }");
        builder.AppendLine("    }");
        builder.AppendLine();
        if (!hotReload)
        {
            if (!viewType.ContainingNamespace.IsGlobalNamespace)
                builder.AppendLine("}");
            return builder.ToString();
        }
        builder.AppendLine("#if DEBUG");
        builder.Append("    partial class ").Append(viewType.Name)
            .AppendLine(" : global::Acrux.Markup.IHotReloadableView");
        builder.AppendLine("    {");
        builder.Append("        string global::Acrux.Markup.IHotReloadableView.HotReloadSource { get { return ")
            .Append(SymbolDisplay.FormatLiteral(runtimePath ?? string.Empty, true)).AppendLine("; } }");
        builder.AppendLine();
        builder.AppendLine("        void global::Acrux.Markup.IHotReloadableView.RegisterHotReloadTypes(global::Acrux.Markup.HotReloadContext context)");
        builder.AppendLine("        {");
        HotReloadTables.Emit(builder, emitter.ElementTypes, emitter.ElementAliases, emitter.DataTypes, compilation);
        foreach (EventHandlerUse handler in emitter.EventHandlers)
        {
            builder.Append("            context.Handler(").Append(SymbolDisplay.FormatLiteral(handler.Event, true)).Append(", ")
                .Append(SymbolDisplay.FormatLiteral(handler.Method, true)).Append(", new ")
                .Append(handler.DelegateType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
                .Append("(this.").Append(handler.Method).AppendLine("));");
        }
        foreach (KeyValuePair<string, INamedTypeSymbol> extension in emitter.MarkupExtensions)
        {
            builder.Append("            context.Types.RegisterMarkupExtension(")
                .Append(SymbolDisplay.FormatLiteral(extension.Key, true))
                .Append(", key => new ")
                .Append(extension.Value.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
                .AppendLine("(key).ProvideValue());");
        }
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        void global::Acrux.Markup.IHotReloadableView.ApplyHotReloadNames(global::System.Collections.Generic.IDictionary<string, global::Acrux.Core.Control> names)");
        builder.AppendLine("        {");
        for (int i = 0; i < emitter.NamedElements.Count; i++)
        {
            NamedElement named = emitter.NamedElements[i];
            builder.Append("            var __named").Append(i).Append(" = global::Acrux.Markup.HotReload.Require<")
                .Append(named.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).Append(">(names, ")
                .Append(SymbolDisplay.FormatLiteral(named.Name, true)).AppendLine(");");
        }
        for (int i = 0; i < emitter.NamedElements.Count; i++)
            builder.Append("            this.").Append(emitter.NamedElements[i].Name).Append(" = __named").Append(i).AppendLine(";");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        void global::Acrux.Markup.IHotReloadableView.CompleteHotReload()");
        builder.AppendLine("        {");
        builder.AppendLine("            OnHotReloaded();");
        builder.AppendLine("        }");
        builder.AppendLine("    }");
        builder.AppendLine("#endif");

        if (!viewType.ContainingNamespace.IsGlobalNamespace)
            builder.AppendLine("}");
        return builder.ToString();
    }

    static string ResolveDataTypeKey(XElement element, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        value = value.Trim();
        int separator = value.IndexOf(':');
        if (separator <= 0)
            return value;
        XNamespace xmlNamespace = element.GetNamespaceOfPrefix(value.Substring(0, separator));
        if (xmlNamespace == null)
            return null;
        string namespaceName = xmlNamespace.NamespaceName;
        if (namespaceName.StartsWith("using:", StringComparison.Ordinal))
            namespaceName = namespaceName.Substring("using:".Length);
        else if (namespaceName.StartsWith("clr-namespace:", StringComparison.Ordinal))
        {
            namespaceName = namespaceName.Substring("clr-namespace:".Length);
            int assembly = namespaceName.IndexOf(';');
            if (assembly >= 0)
                namespaceName = namespaceName.Substring(0, assembly);
        }
        else
            return null;
        return namespaceName.Trim() + "." + value.Substring(separator + 1);
    }

    sealed class ViewEmitter
    {
        readonly string _filePath;
        readonly Compilation _compilation;
        readonly SourceProductionContext _context;
        readonly XElement _documentRoot;
        int _dataTemplateCount;
        readonly List<NamedElement> _namedElements = new();
        readonly HashSet<string> _names = new(StringComparer.Ordinal);
        int _nextVariable;

        public ViewEmitter(
            string filePath,
            Compilation compilation,
            SourceProductionContext context,
            XElement documentRoot)
        {
            _filePath = filePath;
            _compilation = compilation;
            _context = context;
            _documentRoot = documentRoot;
        }

        readonly List<INamedTypeSymbol> _elementTypes = new();
        readonly Dictionary<string, string> _elementAliases = new(StringComparer.Ordinal);
        readonly Dictionary<string, ITypeSymbol> _dataTypes = new(StringComparer.Ordinal);
        readonly Dictionary<string, INamedTypeSymbol> _markupExtensions = new(StringComparer.Ordinal);
        readonly List<EventHandlerUse> _eventHandlers = new();
        INamedTypeSymbol _viewType;

        public bool HasErrors { get; private set; }
        public IReadOnlyList<EventHandlerUse> EventHandlers => _eventHandlers;

        // The bound properties a design view model may supply instead of the x:DataType: each one a bodiless static
        // partial method in the shipped source (the compiler drops its calls), implemented only in the design-build
        // source (design code, which MDK does not pack) with typed casts to the mod's design-only types.
        public List<DesignAccessor> DesignAccessors { get; } = new List<DesignAccessor>();

        readonly Dictionary<string, DesignAccessor> _designAccessorKeys = new(StringComparer.Ordinal);

        DesignAccessor Design(ITypeSymbol dataType, ViewModelMember property, bool write, ITypeSymbol boundType = null)
        {
            boundType = boundType ?? property.Type;
            string key = dataType.ToDisplayString() + "." + property.Name + ":" + boundType.ToDisplayString();
            DesignAccessor accessor;
            if (!_designAccessorKeys.TryGetValue(key, out accessor))
            {
                accessor = new DesignAccessor("__AcruxDesign" + DesignAccessors.Count, dataType, property, boundType);
                _designAccessorKeys.Add(key, accessor);
                DesignAccessors.Add(accessor);
            }
            accessor.Write |= write;
            return accessor;
        }

        // A getter of any source: the x:DataType's property, else what a design view model supplies, else DoNothing
        // (a data context of another type leaves the target as it is).
        string DesignGetter(ITypeSymbol dataType, ViewModelMember property)
        {
            DesignAccessor accessor = Design(dataType, property, false);
            return "delegate(object __source) { var source = __source as " +
                   dataType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + "; if (source != null) return source." +
                   property.Name + "; object __design = global::Acrux.Data.BindingOperations.DoNothing; " +
                   accessor.ReadName + "(__source, ref __design); return __design; }";
        }

        // A typed getter (the value keeps its type, so the client decodes it as before: commands, textures, enums):
        // on another source type it raises <paramref name="missVariable"/>, which the paired setter checks first.
        string DesignGetter(ITypeSymbol dataType, ViewModelMember property, ITypeSymbol boundType, string missVariable)
        {
            DesignAccessor accessor = Design(dataType, property, false, boundType);
            string valueType = boundType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            return "delegate(object __source) { " + missVariable + " = false; var source = __source as " +
                   dataType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + "; if (source != null) return source." +
                   property.Name + "; object __design = global::Acrux.Data.BindingOperations.DoNothing; " +
                   accessor.ReadName + "(__source, ref __design); if (global::System.Object.ReferenceEquals(__design, global::Acrux.Data.BindingOperations.DoNothing)) { " +
                   missVariable + " = true; return default(" + valueType + "); } return (" + valueType + ")__design; }";
        }

        string DeclareMiss(StringBuilder builder, int indent)
        {
            string variable = "__miss" + _nextVariable++;
            AppendIndent(builder, indent);
            builder.Append("bool ").Append(variable).AppendLine(" = false;");
            return variable;
        }

        public IReadOnlyList<NamedElement> NamedElements => _namedElements;
        public IReadOnlyList<INamedTypeSymbol> ElementTypes => _elementTypes;
        public IReadOnlyDictionary<string, string> ElementAliases => _elementAliases;
        public IReadOnlyDictionary<string, ITypeSymbol> DataTypes => _dataTypes;
        public IReadOnlyDictionary<string, INamedTypeSymbol> MarkupExtensions => _markupExtensions;

        public void AddDataType(string key, ITypeSymbol type)
        {
            if (!_dataTypes.ContainsKey(key))
                _dataTypes.Add(key, type);
        }

        void AddElementType(XElement element, INamedTypeSymbol type)
        {
            if (!_elementTypes.Any(existing => SymbolEqualityComparer.Default.Equals(existing, type)))
                _elementTypes.Add(type);
            if (!element.Name.NamespaceName.StartsWith("using:", StringComparison.Ordinal))
                _elementAliases[element.Name.LocalName] = HotReloadTables.MetadataName(type);
        }

        public string EmitInitializeBody(
            XElement root,
            INamedTypeSymbol viewType,
            ITypeSymbol dataType)
        {
            var builder = new StringBuilder();
            _viewType = viewType;
            EmitAttributes(builder, root, "this", viewType, dataType, false, 3);

            foreach (XElement propertyElement in GetPropertyElements(root))
                EmitPropertyElement(builder, "this", viewType, propertyElement, dataType, 3);

            XElement[] children = GetVisualChildren(root).ToArray();
            if (children.Length > 1)
            {
                Error(InvalidView, _filePath + " UserControl accepts at most one content root.");
                return string.Empty;
            }

            if (children.Length == 0)
                return builder.ToString();

            EmittedNode content = EmitVisual(builder, children[0], dataType, false, 3);
            if (content != null)
            {
                AppendIndent(builder, 3);
                builder.Append("this.Content = ");
                builder.Append(content.Variable);
                builder.AppendLine(";");
            }
            return builder.ToString();
        }

        EmittedNode EmitVisual(
            StringBuilder builder,
            XElement element,
            ITypeSymbol dataType,
            bool insideTemplate,
            int indent)
        {
            INamedTypeSymbol type = ResolveElementType(element);
            if (type == null)
                return null;
            AddElementType(element, type);
            if (!HasParameterlessConstructor(type))
            {
                Error(UnsupportedMarkup,
                    "Element '" + element.Name.LocalName + "' requires an accessible parameterless constructor.");
                return null;
            }

            string variable = "__element" + _nextVariable++;
            AppendIndent(builder, indent);
            builder.Append("var ");
            builder.Append(variable);
            builder.Append(" = new ");
            builder.Append(type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
            builder.AppendLine("();");

            string name = GetName(element);
            if (!string.IsNullOrWhiteSpace(name))
            {
                if (!SyntaxFacts.IsValidIdentifier(name))
                    Error(InvalidView, "'" + name + "' is not a valid x:Name identifier.");
                else if (insideTemplate)
                    Error(UnsupportedMarkup, "x:Name inside DataTemplate is not supported yet.");
                else if (!_names.Add(name))
                    Error(InvalidView, "Duplicate x:Name '" + name + "'.");
                else
                {
                    _namedElements.Add(new NamedElement(name, type));
                    AppendIndent(builder, indent);
                    builder.Append("this.");
                    builder.Append(name);
                    builder.Append(" = ");
                    builder.Append(variable);
                    builder.AppendLine(";");
                    if (GetDirective(element, "Name") != null)
                    {
                        AppendIndent(builder, indent);
                        builder.Append(variable);
                        builder.Append(".Name = ");
                        builder.Append(SymbolDisplay.FormatLiteral(name, true));
                        builder.AppendLine(";");
                    }
                }
            }

            if (GetDirective(element, "DataType") != null)
                dataType = ResolveDataType(element, _documentRoot, _compilation, _context, _filePath) ?? dataType;

            EmitAttributes(builder, element, variable, type, dataType, insideTemplate, indent);

            foreach (XElement propertyElement in GetPropertyElements(element))
                EmitPropertyElement(builder, variable, type, propertyElement, dataType, indent);

            foreach (XElement childElement in GetVisualChildren(element))
            {
                EmittedNode child = EmitVisual(
                    builder, childElement, dataType, insideTemplate, indent);
                if (child == null)
                    continue;
                EmitAddChild(builder, variable, type, child, childElement, indent);
            }

            return new EmittedNode(variable, type);
        }

        // Avalonia's event attribute: the value names an instance method of the x:Class whose parameters accept the
        // event's (sender, args), subscribed when the view is built.
        void EmitEventHandler(StringBuilder builder, string target, IEventSymbol routed, string value, bool insideTemplate, int indent)
        {
            string handler = (value ?? string.Empty).Trim();
            if (insideTemplate)
            {
                Error(UnsupportedMarkup, "Event handlers inside DataTemplate are not supported yet ('" + routed.Name + "').");
                return;
            }
            IMethodSymbol invoke = (routed.Type as INamedTypeSymbol)?.DelegateInvokeMethod;
            string signature = invoke == null
                ? routed.Type.ToDisplayString()
                : "void " + handler + "(" + string.Join(", ", invoke.Parameters.Select((p, i) => p.Type.ToDisplayString() + " " + (i == 0 ? "sender" : i == 1 ? "e" : p.Name))) + ")";
            if (!SyntaxFacts.IsValidIdentifier(handler))
            {
                Error(UnknownEventHandler, "'" + handler + "' is not a valid handler name for event '" + routed.Name + "'.");
                return;
            }
            if (invoke == null || FindEventHandler(_viewType, handler, invoke) == null)
            {
                Error(UnknownEventHandler, "Event '" + routed.Name + "' needs a method '" + signature + "' on '" +
                                           _viewType.ToDisplayString() + "'.");
                return;
            }
            AppendIndent(builder, indent);
            builder.Append(target).Append('.').Append(routed.Name).Append(" += this.").Append(handler).AppendLine(";");
            if (!_eventHandlers.Any(use => use.Event == routed.Name && use.Method == handler))
                _eventHandlers.Add(new EventHandlerUse(routed.Name, handler, routed.Type));
        }

        IMethodSymbol FindEventHandler(INamedTypeSymbol type, string name, IMethodSymbol invoke)
        {
            for (INamedTypeSymbol current = type; current != null; current = current.BaseType)
            {
                foreach (IMethodSymbol method in current.GetMembers(name).OfType<IMethodSymbol>())
                {
                    if (method.IsStatic || !method.ReturnsVoid || method.IsGenericMethod ||
                        method.Parameters.Length != invoke.Parameters.Length)
                        continue;
                    if (!SymbolEqualityComparer.Default.Equals(current, type) &&
                        method.DeclaredAccessibility == Accessibility.Private)
                        continue;
                    bool accepts = true;
                    for (int i = 0; i < method.Parameters.Length && accepts; i++)
                    {
                        IParameterSymbol parameter = method.Parameters[i];
                        Conversion conversion = _compilation.ClassifyConversion(invoke.Parameters[i].Type, parameter.Type);
                        accepts = parameter.RefKind == RefKind.None && (conversion.IsIdentity || conversion.IsImplicit && conversion.IsReference);
                    }
                    if (accepts)
                        return method;
                }
            }
            return null;
        }

        void EmitAttributes(
            StringBuilder builder,
            XElement element,
            string target,
            INamedTypeSymbol targetType,
            ITypeSymbol dataType,
            bool insideTemplate,
            int indent)
        {
            foreach (XAttribute attribute in element.Attributes())
            {
                if (attribute.IsNamespaceDeclaration ||
                    IsXamlNamespace(attribute.Name.NamespaceName))
                {
                    continue;
                }

                string propertyName = attribute.Name.LocalName;
                if (propertyName == "name")
                    propertyName = "Name";
                else if (propertyName == "Classes")
                    propertyName = "StyleClass";
                else if (propertyName == "IsEnabled")
                    propertyName = "Enabled";
                else if (IsGridPlacement(propertyName))
                    propertyName = GetGridPlacementProperty(propertyName);

                if (IsType(targetType, "Acrux.Layout.Grid") &&
                    (propertyName == "Columns" || propertyName == "Rows"))
                {
                    string segments = ConvertFloatArray(attribute.Value);
                    if (segments != null)
                    {
                        AppendIndent(builder, indent);
                        builder.Append(target);
                        builder.Append(propertyName == "Columns" ? ".SetColumns(" : ".SetRows(");
                        builder.Append(segments);
                        builder.AppendLine(");");
                    }
                    continue;
                }
                if (IsType(targetType, "Acrux.Layout.Grid") &&
                    (propertyName == "ColumnDefinitions" ||
                     propertyName == "RowDefinitions" ||
                     propertyName == "RowLayout"))
                {
                    string definitions = ConvertGridLengths(attribute.Value);
                    if (definitions != null)
                    {
                        AppendIndent(builder, indent);
                        builder.Append(target);
                        builder.Append(propertyName == "ColumnDefinitions"
                            ? ".SetColumnDefinitions("
                            : ".SetRowDefinitions(");
                        builder.Append(definitions);
                        builder.AppendLine(");");
                    }
                    continue;
                }

                IEventSymbol routed = propertyName.IndexOf('.') < 0 ? FindEvent(targetType, propertyName) : null;
                if (routed != null)
                {
                    EmitEventHandler(builder, target, routed, attribute.Value, insideTemplate, indent);
                    continue;
                }

                if (propertyName.IndexOf('.') > 0)
                {
                    EmitAttachedAttribute(builder, element, target, targetType, propertyName, attribute.Value, dataType, indent);
                    continue;
                }

                IPropertySymbol property = FindProperty(targetType, propertyName);
                if (property == null || property.SetMethod == null)
                {
                    Error(UnknownProperty,
                        "Unknown writable property '" + targetType.Name + "." + propertyName + "'.");
                    continue;
                }

                string resourceKey;
                if (TryParseDynamicResource(attribute.Value, out resourceKey))
                {
                    EmitDynamicResource(
                        builder, target, targetType, property, resourceKey, indent);
                    continue;
                }

                string bindingPath;
                if (TryParseBinding(attribute.Value, out bindingPath))
                {
                    EmitBinding(
                        builder, target, property, dataType, bindingPath, indent);
                    continue;
                }

                string extensionValue;
                if (TryEmitMarkupExtension(element, attribute.Value, property.Type, out extensionValue))
                {
                    if (extensionValue != null)
                    {
                        AppendIndent(builder, indent);
                        builder.Append(target).Append('.').Append(property.Name).Append(" = ")
                            .Append(extensionValue).AppendLine(";");
                    }
                    continue;
                }

                string valueExpression = ConvertLiteral(attribute.Value, property.Type);
                if (valueExpression == null)
                    continue;
                AppendIndent(builder, indent);
                builder.Append(target);
                builder.Append('.');
                builder.Append(property.Name);
                builder.Append(" = ");
                builder.Append(valueExpression);
                builder.AppendLine(";");
            }
        }

        // attached property syntax, e.g. ToolTip.Tip: calls the owner's static SetTip(target, value).
        void EmitAttachedAttribute(
            StringBuilder builder,
            XElement element,
            string target,
            INamedTypeSymbol targetType,
            string qualifiedName,
            string value,
            ITypeSymbol dataType,
            int indent)
        {
            int separator = qualifiedName.IndexOf('.');
            string ownerName = qualifiedName.Substring(0, separator);
            string name = qualifiedName.Substring(separator + 1);
            string ownerMetadata;
            INamedTypeSymbol owner = BuiltInTypes.TryGetValue(ownerName, out ownerMetadata)
                ? _compilation.GetTypeByMetadataName(ownerMetadata)
                : null;
            IMethodSymbol setter = owner == null ? null : owner.GetMembers("Set" + name).OfType<IMethodSymbol>()
                .FirstOrDefault(m => m.IsStatic && m.Parameters.Length == 2 &&
                                     _compilation.ClassifyConversion(targetType, m.Parameters[0].Type).IsImplicit);
            if (setter == null)
            {
                Error(UnknownProperty, "Unknown attached property '" + qualifiedName + "' for '" + targetType.Name + "'.");
                return;
            }
            string ownerTypeName = owner.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            ITypeSymbol valueType = setter.Parameters[1].Type;

            string bindingPath;
            if (TryParseBinding(value, out bindingPath))
            {
                Acrux.Markup.BindingMarkup binding;
                if (!Acrux.Markup.BindingMarkup.TryParse("{Binding " + bindingPath + "}", "Binding", out binding) ||
                    binding.ConverterKey != null || !string.IsNullOrWhiteSpace(binding.StringFormat) ||
                    binding.Path.IndexOf('.') >= 0 || dataType == null)
                {
                    Error(UnsupportedMarkup, "Attached property bindings support one-segment paths only: '" + bindingPath + "'.");
                    return;
                }
                ViewModelMember source = ViewModelMember.Find(dataType as INamedTypeSymbol, binding.Path, _compilation);
                if (source == null || !source.ReadableByView ||
                    !_compilation.ClassifyConversion(source.Type, valueType).IsImplicit)
                {
                    Error(InvalidBinding, "Cannot bind '" + dataType.Name + "." + binding.Path + "' to '" + qualifiedName + "'.");
                    return;
                }
                string sourceTypeName = dataType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                string sourceValueType = source.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                string attachedMiss = DeclareMiss(builder, indent);
                AppendIndent(builder, indent);
                builder.Append(target).Append(".Bind<object, ").Append(sourceValueType).Append(">(")
                    .Append(DesignGetter(dataType, source, source.Type, attachedMiss));
                builder.Append(", delegate(").Append(sourceValueType).Append(" value) { if (").Append(attachedMiss).Append(") return; ");
                builder.Append(ownerTypeName).Append('.');
                builder.Append(setter.Name).Append('(').Append(target).Append(", value); }, ");
                builder.Append(SymbolDisplay.FormatLiteral(source.Name, true)).AppendLine(");");
                return;
            }

            string extensionValue;
            if (TryEmitMarkupExtension(element, value, valueType, out extensionValue))
            {
                if (extensionValue != null)
                {
                    AppendIndent(builder, indent);
                    builder.Append(ownerTypeName).Append('.').Append(setter.Name).Append('(')
                        .Append(target).Append(", ").Append(extensionValue).AppendLine(");");
                }
                return;
            }

            string literal = valueType.SpecialType == SpecialType.System_Object
                ? SymbolDisplay.FormatLiteral(value, true)
                : ConvertLiteral(value, valueType);
            if (literal == null)
                return;
            AppendIndent(builder, indent);
            builder.Append(ownerTypeName).Append('.').Append(setter.Name).Append('(').Append(target).Append(", ");
            builder.Append(literal).AppendLine(");");
        }

        bool TryEmitMarkupExtension(XElement element, string value, ITypeSymbol targetType, out string expression)
        {
            expression = null;
            if (string.IsNullOrEmpty(value) || value[0] != '{')
                return false;
            int separator = value.IndexOf(' ');
            if (separator < 2 || value.Substring(1, separator - 1).IndexOf(':') < 1)
                return false;
            string qualifiedName = value.Substring(1, separator - 1);
            int prefixEnd = qualifiedName.IndexOf(':');
            XNamespace xmlNamespace = element.GetNamespaceOfPrefix(qualifiedName.Substring(0, prefixEnd));
            if (xmlNamespace == null)
            {
                Error(UnsupportedMarkup, "Unknown markup extension namespace in '" + value + "'.");
                return true;
            }
            string namespaceName = xmlNamespace.NamespaceName;
            if (namespaceName.StartsWith("using:", StringComparison.Ordinal))
                namespaceName = namespaceName.Substring("using:".Length);
            else if (namespaceName.StartsWith("clr-namespace:", StringComparison.Ordinal))
            {
                namespaceName = namespaceName.Substring("clr-namespace:".Length);
                int assembly = namespaceName.IndexOf(';');
                if (assembly >= 0)
                    namespaceName = namespaceName.Substring(0, assembly);
            }
            else
            {
                Error(UnsupportedMarkup, "Unsupported markup extension namespace in '" + value + "'.");
                return true;
            }
            if (!value.EndsWith("}", StringComparison.Ordinal))
            {
                Error(UnsupportedMarkup, "Unclosed markup extension '" + value + "'.");
                return true;
            }
            string argument = value.Substring(separator + 1, value.Length - separator - 2).Trim();
            if (argument.Length == 0)
            {
                Error(UnsupportedMarkup, "Markup extension requires a positional argument: '" + value + "'.");
                return true;
            }
            string typeName = namespaceName.Trim() + "." + qualifiedName.Substring(prefixEnd + 1);
            INamedTypeSymbol type = _compilation.GetTypeByMetadataName(typeName + "Extension") ??
                                    _compilation.GetTypeByMetadataName(typeName);
            IMethodSymbol constructor = type == null ? null : type.InstanceConstructors.FirstOrDefault(method =>
                method.DeclaredAccessibility == Accessibility.Public && method.Parameters.Length == 1 &&
                method.Parameters[0].Type.SpecialType == SpecialType.System_String);
            IMethodSymbol provideValue = type == null ? null : type.GetMembers("ProvideValue").OfType<IMethodSymbol>()
                .FirstOrDefault(method => method.DeclaredAccessibility == Accessibility.Public &&
                                          !method.IsStatic && method.Parameters.Length == 0);
            if (constructor == null || provideValue == null ||
                !_compilation.ClassifyConversion(provideValue.ReturnType, targetType).IsImplicit)
            {
                Error(UnsupportedMarkup, "Markup extension '" + qualifiedName +
                    "' needs a public string constructor and parameterless ProvideValue() compatible with '" +
                    targetType.ToDisplayString() + "'.");
                return true;
            }
            string key = namespaceName.Trim() + "." + qualifiedName.Substring(prefixEnd + 1);
            _markupExtensions[key] = type;
            expression = "new " + type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + "(" +
                         SymbolDisplay.FormatLiteral(argument, true) + ").ProvideValue()";
            return true;
        }

        void EmitDynamicResource(
            StringBuilder builder,
            string target,
            INamedTypeSymbol targetType,
            IPropertySymbol targetProperty,
            string resourceKey,
            int indent)
        {
            IFieldSymbol styledProperty = FindStyledProperty(
                targetType, targetProperty.Name, targetProperty.Type);
            if (styledProperty != null)
            {
                AppendIndent(builder, indent);
                builder.Append(target);
                builder.Append(".SetDynamicResource(");
                builder.Append(styledProperty.ContainingType.ToDisplayString(
                    SymbolDisplayFormat.FullyQualifiedFormat));
                builder.Append('.');
                builder.Append(styledProperty.Name);
                builder.Append(", ");
                builder.Append(SymbolDisplay.FormatLiteral(resourceKey, true));
                builder.AppendLine(");");
                return;
            }

            IPropertySymbol provider = FindProperty(
                targetType, targetProperty.Name + "Provider");
            INamedTypeSymbol providerType = provider == null
                ? null
                : provider.Type as INamedTypeSymbol;
            IMethodSymbol invoke = providerType == null
                ? null
                : providerType.DelegateInvokeMethod;
            if (provider != null && provider.SetMethod != null && invoke != null &&
                invoke.Parameters.Length == 0 &&
                SymbolEqualityComparer.Default.Equals(
                    invoke.ReturnType, targetProperty.Type))
            {
                string valueType = targetProperty.Type.ToDisplayString(
                    SymbolDisplayFormat.FullyQualifiedFormat);
                AppendIndent(builder, indent);
                builder.Append(target);
                builder.Append(".WatchDynamicResource(");
                builder.Append(SymbolDisplay.FormatLiteral(resourceKey, true));
                builder.Append(", global::Acrux.Core.Invalidation.UiInvalidation.Measure | ");
                builder.AppendLine("global::Acrux.Core.Invalidation.UiInvalidation.Render);");
                AppendIndent(builder, indent);
                builder.Append(target);
                builder.Append('.');
                builder.Append(provider.Name);
                builder.Append(" = delegate { return ");
                builder.Append(target);
                builder.Append(".ResolveResource<");
                builder.Append(valueType);
                builder.Append(">(");
                builder.Append(SymbolDisplay.FormatLiteral(resourceKey, true));
                builder.AppendLine("); };");
                return;
            }

            Error(UnsupportedMarkup,
                "Property '" + targetType.Name + "." + targetProperty.Name +
                "' does not support dynamic resources.");
        }

        void EmitBinding(
            StringBuilder builder,
            string target,
            IPropertySymbol targetProperty,
            ITypeSymbol dataType,
            string bindingPath,
            int indent)
        {
            Acrux.Markup.BindingMarkup binding;
            if (!Acrux.Markup.BindingMarkup.TryParse("{Binding " + bindingPath + "}", "Binding", out binding))
            {
                Error(UnsupportedMarkup, "Unsupported binding: " + bindingPath);
                return;
            }
            bindingPath = binding.Path;
            if (dataType == null)
            {
                Error(InvalidBinding,
                    "Binding '" + bindingPath + "' requires x:DataType.");
                return;
            }
            if (bindingPath.IndexOf('.') >= 0)
            {
                Error(UnsupportedMarkup,
                    "Only one-segment binding paths are currently supported: '" + bindingPath + "'.");
                return;
            }

            ViewModelMember sourceProperty = ViewModelMember.Find(dataType as INamedTypeSymbol, bindingPath, _compilation);
            if (sourceProperty == null)
            {
                Error(InvalidBinding,
                    "Cannot resolve binding property '" + dataType.Name + "." + bindingPath + "'." +
                    ViewModelMember.WhyNotGenerated(dataType as INamedTypeSymbol, bindingPath));
                return;
            }
            if (!sourceProperty.ReadableByView)
            {
                Error(InvalidBinding,
                    "Binding property '" + dataType.Name + "." + bindingPath + "' must have a public or internal getter the view can read.");
                return;
            }
            Conversion conversion = _compilation.ClassifyConversion(sourceProperty.Type, targetProperty.Type);
            bool formatsString = !string.IsNullOrWhiteSpace(binding.StringFormat) &&
                (targetProperty.Type.SpecialType == SpecialType.System_String ||
                 targetProperty.Type.SpecialType == SpecialType.System_Object);
            // A number binds to a Vector4 as a uniform value, so float CornerRadius sources keep working.
            bool uniformVector = targetProperty.Type.ToDisplayString() == "VRageMath.Vector4" &&
                (sourceProperty.Type.SpecialType == SpecialType.System_Single ||
                 sourceProperty.Type.SpecialType == SpecialType.System_Int32 ||
                 sourceProperty.Type.SpecialType == SpecialType.System_Double);
            if (binding.ConverterKey == null && !formatsString && !conversion.IsImplicit && !uniformVector)
            {
                Error(InvalidBinding,
                    "Binding '" + dataType.Name + "." + bindingPath + "' is not assignable to '" +
                    targetProperty.ContainingType.Name + "." + targetProperty.Name + "'.");
                return;
            }

            string sourceTypeName = dataType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            string valueTypeName = sourceProperty.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            if (binding.Mode == "TwoWay")
            {
                var styled = FindStyledProperty(targetProperty.ContainingType, targetProperty.Name, targetProperty.Type);
                bool nullableBoolean = sourceProperty.Type.SpecialType == SpecialType.System_Boolean &&
                    targetProperty.Type is INamedTypeSymbol nullable &&
                    nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T &&
                    nullable.TypeArguments[0].SpecialType == SpecialType.System_Boolean;
                if (!sourceProperty.WritableByView || styled == null || binding.ConverterKey != null ||
                    !string.IsNullOrWhiteSpace(binding.StringFormat) ||
                    (!nullableBoolean && !_compilation.ClassifyConversion(targetProperty.Type, sourceProperty.Type).IsImplicit))
                {
                    Error(InvalidBinding, "TwoWay requires a writable source, a styled target, and compatible types without a converter or string format.");
                    return;
                }
                AppendIndent(builder, indent);
                string targetValueType = targetProperty.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                string styledField = styled.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + "." + styled.Name;
                DesignAccessor twoWay = Design(dataType, sourceProperty, true);
                builder.Append("global::Acrux.Core.Binding.CompiledTwoWayBinding.Attach<object, ");
                builder.Append(targetValueType).Append(">(").Append(target).Append(", ").Append(styledField);
                // Another source type keeps the control's value unless a design view model supplies one.
                builder.Append(", __source => { var source = __source as ").Append(sourceTypeName).Append("; if (source != null) return source.")
                    .Append(sourceProperty.Name).Append("; object __design = global::Acrux.Data.BindingOperations.DoNothing; ")
                    .Append(twoWay.ReadName).Append("(__source, ref __design); return global::System.Object.ReferenceEquals(__design, global::Acrux.Data.BindingOperations.DoNothing) ? ")
                    .Append(target).Append(".GetValue(").Append(styledField).Append(") : (").Append(targetValueType).Append(")(").Append(valueTypeName).Append(")__design; }");
                builder.Append(", (__source, value) => { var source = __source as ").Append(sourceTypeName).Append("; if (source != null) source.")
                    .Append(sourceProperty.Name).Append(" = value");
                if (nullableBoolean) builder.Append(" == true");
                builder.Append("; else ").Append(twoWay.WriteName).Append("(__source, (object)(").Append(valueTypeName).Append(")(value");
                if (nullableBoolean) builder.Append(" == true");
                builder.Append(")); }");
                builder.Append(", ").Append(SymbolDisplay.FormatLiteral(sourceProperty.Name, true)).AppendLine(");");
                return;
            }
            if (targetProperty.Name == "DataContext" && binding.ConverterKey == null && !formatsString)
            {
                AppendIndent(builder, indent);
                builder.Append(target).Append(".BindDataContext<object, object>(").Append(DesignGetter(dataType, sourceProperty));
                builder.Append(", ").Append(SymbolDisplay.FormatLiteral(sourceProperty.Name, true)).AppendLine(");");
                return;
            }
            string converterVariable = null;
            if (binding.ConverterKey != null)
            {
                converterVariable = "__converter" + _nextVariable++;
                AppendIndent(builder, indent);
                builder.Append("global::Acrux.Data.Converters.IValueConverter ");
                builder.Append(converterVariable);
                builder.AppendLine(" = null;");
            }
            // A reference (or identity) conversion binds at the target's type: a design view model's value then only has
            // to fit the target (a list of preview clones as an ItemsSource). Value conversions keep the source's type,
            // so the client decodes the value exactly as the source has it.
            ITypeSymbol boundType = sourceProperty.Type;
            if (binding.ConverterKey == null && !formatsString && !uniformVector && (conversion.IsIdentity || conversion.IsReference))
                boundType = targetProperty.Type;
            valueTypeName = boundType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            string miss = DeclareMiss(builder, indent);
            AppendIndent(builder, indent);
            builder.Append(target);
            builder.Append(".Bind<object, ").Append(valueTypeName).Append(">(");
            builder.Append(DesignGetter(dataType, sourceProperty, boundType, miss));
            builder.Append(", delegate(").Append(valueTypeName).Append(" value) { if (").Append(miss).Append(") return; ");
            if (binding.ConverterKey != null || formatsString)
                builder.Append("if (global::System.Object.ReferenceEquals(value, global::Acrux.Data.BindingOperations.DoNothing)) return; ");
            if (binding.ConverterKey != null)
            {
                builder.Append("object converted = global::Acrux.Extensions.ValueConverters.Convert(");
                builder.Append(converterVariable);
                builder.Append(" ?? (");
                builder.Append(converterVariable);
                builder.Append(" = global::Acrux.Extensions.ValueConverters.Resolve(");
                builder.Append(target);
                builder.Append(", ");
                builder.Append(SymbolDisplay.FormatLiteral(binding.ConverterKey, true));
                builder.Append(")), value, typeof(");
                builder.Append(targetProperty.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                builder.Append("), ");
                builder.Append(binding.Parameter == null ? "null" : SymbolDisplay.FormatLiteral(binding.Parameter, true));
                builder.Append(", ");
                builder.Append(binding.Culture == null ? "global::System.Globalization.CultureInfo.CurrentCulture" :
                    "global::System.Globalization.CultureInfo.GetCultureInfo(" + SymbolDisplay.FormatLiteral(binding.Culture, true) + ")");
                builder.Append(", ").Append(target);
                builder.Append("); if (global::System.Object.ReferenceEquals(converted, global::Acrux.Data.BindingOperations.DoNothing)) return; ");
            }
            builder.Append(target);
            builder.Append('.');
            builder.Append(targetProperty.Name);
            if (formatsString)
            {
                builder.Append(" = (").Append(targetProperty.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                builder.Append(")global::Acrux.Core.Binding.BindingStringFormat.Apply(");
                builder.Append(binding.ConverterKey == null ? "value" : "converted");
                builder.Append(", typeof(").Append(targetProperty.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                builder.Append("), ").Append(SymbolDisplay.FormatLiteral(binding.StringFormat, true)).Append(", ");
                builder.Append(binding.Culture == null ? "global::System.Globalization.CultureInfo.CurrentCulture" :
                    "global::System.Globalization.CultureInfo.GetCultureInfo(" + SymbolDisplay.FormatLiteral(binding.Culture, true) + ")");
                builder.Append("); }, ");
            }
            else if (binding.ConverterKey == null && uniformVector)
                builder.Append(" = new global::VRageMath.Vector4((float)value); }, ");
            else
                builder.Append(binding.ConverterKey == null ? " = value; }, " :
                    " = (" + targetProperty.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + ")converted; }, ");
            builder.Append(SymbolDisplay.FormatLiteral(sourceProperty.Name, true));
            builder.AppendLine(");");
        }

        void EmitPropertyElement(
            StringBuilder builder,
            string target,
            INamedTypeSymbol targetType,
            XElement propertyElement,
            ITypeSymbol dataType,
            int indent)
        {
            string localName = propertyElement.Name.LocalName;
            int separator = localName.IndexOf('.');
            string propertyName = separator < 0 ? localName : localName.Substring(separator + 1);
            IPropertySymbol property = FindProperty(targetType, propertyName);
            if (property == null || property.SetMethod == null)
            {
                Error(UnknownProperty,
                    "Unknown writable property element '" + targetType.Name + "." + propertyName + "'.");
                return;
            }

            XElement[] values = propertyElement.Elements().ToArray();
            if (propertyName == "Styles")
            {
                EmitStyles(builder, target, property, values, indent);
                return;
            }
            if (propertyName == "ItemsPanel" && values.Length == 1 && values[0].Name.LocalName == "ItemsPanelTemplate")
            {
                EmitItemsPanelTemplate(builder, target, property, values[0], dataType, indent);
                return;
            }
            if (values.Length == 1 &&
                (values[0].Name.LocalName == "ImageBrush" || values[0].Name.LocalName == "SolidColorBrush"))
            {
                EmitBrush(builder, target, property, values[0], indent);
                return;
            }
            if (values.Length == 1 && IsGradientBrush(values[0].Name.LocalName))
            {
                EmitGradientBrush(builder, target, property, values[0], indent);
                return;
            }
            if (values.Length == 1 && values[0].Name.LocalName != "DataTemplate")
            {
                INamedTypeSymbol valueType = ResolveElementType(values[0]);
                if (valueType != null && IsAssignableTo(valueType, property.Type))
                {
                    EmittedNode value = EmitVisual(builder, values[0], dataType, false, indent);
                    if (value == null)
                        return;
                    AppendIndent(builder, indent);
                    builder.Append(target);
                    builder.Append('.');
                    builder.Append(property.Name);
                    builder.Append(" = ");
                    builder.Append(value.Variable);
                    builder.AppendLine(";");
                    return;
                }
            }
            if (values.Length != 1 || values[0].Name.LocalName != "DataTemplate" ||
                property.Type.ToDisplayString() != "Acrux.Styling.IDataTemplate")
            {
                Error(UnsupportedMarkup,
                    "Expected a DataTemplate for an IDataTemplate property, ItemsPanel/ItemsPanelTemplate, a literal brush, or a single visual.");
                return;
            }
            EmitDataTemplate(builder, target, property, values[0], dataType, indent);
        }

        static bool IsGradientBrush(string name)
        {
            return name == "LinearGradientBrush" || name == "RadialGradientBrush" || name == "ConicGradientBrush";
        }

        // A literal gradient brush, as Avalonia writes one: its attributes, then its GradientStop children (directly
        // or inside <Brush.GradientStops>, optionally wrapped in <GradientStops>), added one by one.
        void EmitGradientBrush(
            StringBuilder builder,
            string target,
            IPropertySymbol property,
            XElement brush,
            int indent)
        {
            string name = brush.Name.LocalName;
            var setters = new List<string>();
            foreach (XAttribute attribute in brush.Attributes())
            {
                if (attribute.IsNamespaceDeclaration || IsXamlNamespace(attribute.Name.NamespaceName))
                    continue;
                string member = attribute.Name.LocalName;
                string value = attribute.Value.Trim();
                if (TryParseBinding(value, out _) || TryParseDynamicResource(value, out _))
                {
                    Error(UnsupportedMarkup, name + " values must be literal; bind the brush property itself instead.");
                    return;
                }
                string literal = null;
                switch (member)
                {
                    case "SpreadMethod":
                        if (value == "Pad" || value == "Reflect" || value == "Repeat")
                            literal = "global::Acrux.Styling.GradientSpreadMethod." + value;
                        break;
                    case "Opacity":
                        literal = FloatLiteral(value);
                        break;
                    case "Transform":
                        literal = "global::Acrux.Styling.Transform.Parse(" + SymbolDisplay.FormatLiteral(value, true) + ")";
                        break;
                    case "TransformOrigin":
                        literal = RelativePointLiteral(value);
                        break;
                    case "StartPoint":
                    case "EndPoint":
                        if (name == "LinearGradientBrush")
                            literal = RelativePointLiteral(value);
                        break;
                    case "Center":
                        if (name != "LinearGradientBrush")
                            literal = RelativePointLiteral(value);
                        break;
                    case "GradientOrigin":
                        if (name == "RadialGradientBrush")
                            literal = RelativePointLiteral(value);
                        break;
                    case "RadiusX":
                    case "RadiusY":
                        if (name == "RadialGradientBrush")
                            literal = RelativeScalarLiteral(value);
                        break;
                    case "Angle":
                        if (name == "ConicGradientBrush")
                            literal = FloatLiteral(value);
                        break;
                    default:
                        Error(UnknownProperty, "Unknown property '" + name + "." + member + "'.");
                        return;
                }
                if (literal == null)
                {
                    Error(UnsupportedMarkup, "Invalid or unsupported value '" + value + "' for " + name + "." + member + ".");
                    return;
                }
                setters.Add(member + " = " + literal);
            }

            var stops = new List<XElement>();
            if (!CollectGradientStops(brush, name, stops))
                return;
            ITypeSymbol colorType = _compilation.GetTypeByMetadataName("VRageMath.Color");
            var adds = new List<string>();
            foreach (XElement stop in stops)
            {
                string color = "default(global::VRageMath.Color)";
                string offset = "0f";
                foreach (XAttribute attribute in stop.Attributes())
                {
                    if (attribute.IsNamespaceDeclaration || IsXamlNamespace(attribute.Name.NamespaceName))
                        continue;
                    string value = attribute.Value.Trim();
                    if (attribute.Name.LocalName == "Color")
                        color = ConvertLiteral(value, colorType);
                    else if (attribute.Name.LocalName == "Offset")
                        offset = FloatLiteral(value);
                    else
                    {
                        Error(UnknownProperty, "Unknown property 'GradientStop." + attribute.Name.LocalName + "'.");
                        return;
                    }
                    if (color == null || offset == null)
                    {
                        Error(UnsupportedMarkup, "GradientStop." + attribute.Name.LocalName + " must be a literal; got '" + value + "'.");
                        return;
                    }
                }
                adds.Add("new global::Acrux.Styling.GradientStop(" + color + ", " + offset + ")");
            }

            string variable = "__gradient" + _nextVariable++;
            AppendIndent(builder, indent);
            builder.Append("var ").Append(variable).Append(" = new global::Acrux.Styling.").Append(name)
                .Append(" { ").Append(string.Join(", ", setters)).AppendLine(" };");
            foreach (string add in adds)
            {
                AppendIndent(builder, indent);
                builder.Append(variable).Append(".GradientStops.Add(").Append(add).AppendLine(");");
            }
            AppendIndent(builder, indent);
            builder.Append(target).Append('.').Append(property.Name).Append(" = ").Append(variable).AppendLine(";");
        }

        bool CollectGradientStops(XElement element, string brushName, List<XElement> stops)
        {
            foreach (XElement child in element.Elements())
            {
                string local = child.Name.LocalName;
                if (local == "GradientStop")
                    stops.Add(child);
                else if (local == "GradientStops" || local == brushName + ".GradientStops")
                {
                    if (!CollectGradientStops(child, brushName, stops))
                        return false;
                }
                else
                {
                    Error(UnsupportedMarkup, "Unexpected '" + local + "' in " + brushName + "; expected GradientStop elements.");
                    return false;
                }
            }
            return true;
        }

        static string FloatLiteral(string value)
        {
            float parsed;
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)
                ? parsed.ToString("R", CultureInfo.InvariantCulture) + "f"
                : null;
        }

        // "x,y" in layout units or "x%,y%" as fractions, as RelativePoint.Parse reads it.
        static string RelativePointLiteral(string value)
        {
            string[] parts = value.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || parts[0].EndsWith("%", StringComparison.Ordinal) != parts[1].EndsWith("%", StringComparison.Ordinal))
                return null;
            bool relative = parts[0].EndsWith("%", StringComparison.Ordinal);
            float x, y;
            if (!float.TryParse(parts[0].TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out x) ||
                !float.TryParse(parts[1].TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out y))
                return null;
            if (relative)
            {
                x /= 100f;
                y /= 100f;
            }
            return "new global::Acrux.Styling.RelativePoint(" + x.ToString("R", CultureInfo.InvariantCulture) + "f, " +
                   y.ToString("R", CultureInfo.InvariantCulture) + "f, global::Acrux.Styling.RelativeUnit." +
                   (relative ? "Relative" : "Absolute") + ")";
        }

        // "x" in layout units or "x%" as a fraction, as RelativeScalar.Parse reads it.
        static string RelativeScalarLiteral(string value)
        {
            bool relative = value.EndsWith("%", StringComparison.Ordinal);
            float scalar;
            if (!float.TryParse(value.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out scalar))
                return null;
            if (relative)
                scalar /= 100f;
            return "new global::Acrux.Styling.RelativeScalar(" + scalar.ToString("R", CultureInfo.InvariantCulture) +
                   "f, global::Acrux.Styling.RelativeUnit." + (relative ? "Relative" : "Absolute") + ")";
        }

        void EmitBrush(
            StringBuilder builder,
            string target,
            IPropertySymbol property,
            XElement brush,
            int indent)
        {
            string expression;
            if (brush.Name.LocalName == "ImageBrush")
            {
                var setters = new List<string>();
                foreach (XAttribute attribute in brush.Attributes())
                {
                    if (attribute.IsNamespaceDeclaration || IsXamlNamespace(attribute.Name.NamespaceName))
                        continue;
                    if (TryParseBinding(attribute.Value, out _) || TryParseDynamicResource(attribute.Value, out _))
                    {
                        Error(UnsupportedMarkup,
                            "ImageBrush values must be literal; bind the brush property itself instead.");
                        return;
                    }
                    if (attribute.Name.LocalName == "Source")
                        setters.Add("Source = " + SymbolDisplay.FormatLiteral(attribute.Value, true));
                    else if (attribute.Name.LocalName == "Stretch")
                        setters.Add("Stretch = global::Acrux.Styling.Stretch." + attribute.Value.Trim());
                    else if (attribute.Name.LocalName == "Opacity")
                    {
                        float opacity;
                        if (!float.TryParse(attribute.Value.Trim(), System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out opacity))
                        {
                            Error(UnsupportedMarkup, "ImageBrush.Opacity must be a number.");
                            return;
                        }
                        setters.Add("Opacity = " + opacity.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "f");
                    }
                    else
                    {
                        Error(UnknownProperty, "Unknown property 'ImageBrush." + attribute.Name.LocalName + "'.");
                        return;
                    }
                }
                expression = "new global::Acrux.Styling.ImageBrush { " + string.Join(", ", setters) + " }";
            }
            else
            {
                XAttribute color = brush.Attribute("Color");
                string literal = color == null ? null : ConvertLiteral(color.Value,
                    _compilation.GetTypeByMetadataName("VRageMath.Color"));
                if (literal == null)
                {
                    Error(UnsupportedMarkup, "SolidColorBrush requires a literal Color.");
                    return;
                }
                expression = "new global::Acrux.Styling.SolidColorBrush(" + literal + ")";
            }

            AppendIndent(builder, indent);
            builder.Append(target).Append('.').Append(property.Name).Append(" = ").Append(expression).AppendLine(";");
        }

        void EmitStyles(
            StringBuilder builder,
            string target,
            IPropertySymbol property,
            XElement[] values,
            int indent)
        {
            if (values.Length == 0 ||
                values.Any(value => value.Name.LocalName != "StyleInclude" && value.Name.LocalName != "Style"))
            {
                Error(UnsupportedMarkup,
                    "Styles requires one or more Style or StyleInclude values.");
                return;
            }
            if (values.Any(value => value.Name.LocalName == "Style"))
            {
                EmitInlineStyles(builder, target, property, values, indent);
                return;
            }

            var sources = new List<string>(values.Length);
            for (int i = 0; i < values.Length; i++)
            {
                XAttribute source = values[i].Attribute("Source");
                if (source == null || string.IsNullOrWhiteSpace(source.Value))
                {
                    Error(InvalidView, "StyleInclude requires a Source attribute.");
                    return;
                }
                sources.Add(source.Value);
            }

            AppendIndent(builder, indent);
            builder.Append(target);
            builder.Append('.');
            builder.Append(property.Name);
            if (sources.Count == 1)
            {
                builder.Append(" = global::Acrux.Styling.StyleSheet.LoadFromSource(");
                builder.Append(SymbolDisplay.FormatLiteral(sources[0], true));
                builder.AppendLine(");");
                return;
            }

            builder.Append(" = global::Acrux.Styling.StyleSheet.LoadFromSources(new string[] { ");
            for (int i = 0; i < sources.Count; i++)
            {
                if (i > 0)
                    builder.Append(", ");
                builder.Append(SymbolDisplay.FormatLiteral(sources[i], true));
            }
            builder.AppendLine(" });");
        }

        // Styles with Style elements (mixed with StyleIncludes, applied in document order, as in Avalonia): the
        // runtime parses them as one Styles document, which carries every namespace declaration in scope so
        // prefixed selectors ("local|Control") and setter values resolve as in the view.
        void EmitInlineStyles(
            StringBuilder builder,
            string target,
            IPropertySymbol property,
            XElement[] values,
            int indent)
        {
            foreach (XElement value in values)
            {
                if (value.Name.LocalName == "Style" && string.IsNullOrWhiteSpace((string)value.Attribute("Selector")))
                {
                    Error(InvalidView, "Style requires a Selector attribute.");
                    return;
                }
                if (value.Name.LocalName == "StyleInclude" && string.IsNullOrWhiteSpace((string)value.Attribute("Source")))
                {
                    Error(InvalidView, "StyleInclude requires a Source attribute.");
                    return;
                }
                if (value.Name.LocalName == "Style" && value.Elements().Any(child => child.Name.LocalName == "Style"))
                {
                    Error(UnsupportedMarkup, "Nested Style elements are not supported.");
                    return;
                }
            }

            XElement scope = values[0].Parent ?? _documentRoot;
            var document = new XElement(values[0].Name.Namespace + "Styles");
            var declared = new HashSet<string>(StringComparer.Ordinal);
            for (XElement current = scope; current != null; current = current.Parent)
                foreach (XAttribute attribute in current.Attributes())
                    if (attribute.IsNamespaceDeclaration && declared.Add(attribute.Name.ToString()))
                        document.Add(new XAttribute(attribute));
            foreach (XElement value in values)
                document.Add(new XElement(value));

            AppendIndent(builder, indent);
            builder.Append(target).Append('.').Append(property.Name)
                .Append(" = global::Acrux.Styling.StyleSheet.LoadInline(")
                .Append(SymbolDisplay.FormatLiteral(document.ToString(SaveOptions.DisableFormatting), true))
                .AppendLine(");");
        }

        void EmitDataTemplate(
            StringBuilder builder,
            string target,
            IPropertySymbol property,
            XElement template,
            ITypeSymbol inheritedDataType,
            int indent)
        {
            ITypeSymbol templateDataType = ResolveDataType(
                template, _documentRoot, _compilation, _context, _filePath) ?? inheritedDataType;
            if (templateDataType == null)
            {
                Error(InvalidBinding, "DataTemplate requires x:DataType.");
                return;
            }
            XElement[] children = GetVisualChildren(template).ToArray();
            if (children.Length != 1)
            {
                Error(InvalidView, "DataTemplate requires exactly one visual root.");
                return;
            }

            string templateKey = ResolveDataTypeKey(template, GetDirective(template, "DataType"));
            if (templateKey != null)
                AddDataType(templateKey, templateDataType);
            string parameter = "dataContext" + _dataTemplateCount++;
            AppendIndent(builder, indent);
            builder.Append(target);
            builder.Append('.');
            builder.Append(property.Name);
            // object, not the x:DataType: the factory only hands the item on as the DataContext, and an item of another
            // type (a design view model) then gets its design bindings instead of a cast failure.
            builder.Append(" = new global::Acrux.Styling.DataTemplate<object>(delegate(object ");
            builder.Append(parameter);
            builder.AppendLine(")");
            AppendIndent(builder, indent);
            builder.AppendLine("{");
            EmittedNode root = EmitVisual(
                builder, children[0], templateDataType, true, indent + 1);
            if (root != null)
            {
                if (children[0].Attribute("DataContext") == null)
                {
                    AppendIndent(builder, indent + 1);
                    builder.Append(root.Variable);
                    builder.Append(".DataContext = ");
                    builder.Append(parameter);
                    builder.AppendLine(";");
                }
                AppendIndent(builder, indent + 1);
                builder.Append("return ");
                builder.Append(root.Variable);
                builder.AppendLine(";");
            }
            AppendIndent(builder, indent);
            builder.AppendLine("});");
        }

        void EmitItemsPanelTemplate(StringBuilder builder, string target, IPropertySymbol property,
            XElement template, ITypeSymbol dataType, int indent)
        {
            XElement[] children = GetVisualChildren(template).ToArray();
            if (children.Length != 1)
            {
                Error(InvalidView, "ItemsPanelTemplate requires exactly one panel root.");
                return;
            }
            AppendIndent(builder, indent);
            builder.Append(target).Append('.').Append(property.Name);
            builder.AppendLine(" = new global::Acrux.Styling.ItemsPanelTemplate(delegate()");
            AppendIndent(builder, indent);
            builder.AppendLine("{");
            EmittedNode root = EmitVisual(builder, children[0], dataType, true, indent + 1);
            if (root != null)
            {
                if (!InheritsFrom(root.Type, _compilation.GetTypeByMetadataName("Acrux.Layout.Panel")))
                    Error(InvalidView, "ItemsPanelTemplate requires a Panel-derived root.");
                AppendIndent(builder, indent + 1);
                builder.Append("return ").Append(root.Variable).AppendLine(";");
            }
            AppendIndent(builder, indent);
            builder.AppendLine("});");
        }

        void EmitAddChild(
            StringBuilder builder,
            string parent,
            INamedTypeSymbol parentType,
            EmittedNode child,
            XElement childElement,
            int indent)
        {
            AppendIndent(builder, indent);
            if (IsType(parentType, "Acrux.Layout.Grid"))
            {
                builder.Append(parent);
                builder.Append(".Set(");
                builder.Append(child.Variable);
                builder.Append(", ");
                builder.Append(child.Variable);
                builder.Append(".GridColumn, ");
                builder.Append(child.Variable);
                builder.Append(".GridRow, ");
                builder.Append(child.Variable);
                builder.Append(".GridColumnSpan, ");
                builder.Append(child.Variable);
                builder.Append(".GridRowSpan");
                builder.AppendLine(");");
            }
            else if (IsType(parentType, "Acrux.Controls.Flyout"))
            {
                // A flyout's one child is its content, as Avalonia's Flyout.Content.
                builder.Append(parent);
                builder.Append(".Content = ");
                builder.Append(child.Variable);
                builder.AppendLine(";");
            }
            else if (IsType(parentType, "Acrux.Controls.Menu") || IsType(parentType, "Acrux.Controls.ContextMenu") ||
                     IsType(parentType, "Acrux.Controls.MenuItem"))
            {
                builder.Append(parent);
                builder.Append(".Items.Add(");
                builder.Append(child.Variable);
                builder.AppendLine(");");
            }
            else
            {
                builder.Append(parent);
                builder.Append(".AddChild(");
                builder.Append(child.Variable);
                builder.AppendLine(");");
            }
        }

        INamedTypeSymbol ResolveElementType(XElement element)
        {
            string metadataName;
            string namespaceName = element.Name.NamespaceName;
            if (namespaceName.StartsWith("using:", StringComparison.Ordinal))
                metadataName = namespaceName.Substring("using:".Length) + "." + element.Name.LocalName;
            else if (namespaceName.Length != 0 && !IsAcruxNamespace(namespaceName))
            {
                Error(UnknownType,
                    "Unknown UI namespace '" + namespaceName + "' on element '" +
                    element.Name.LocalName + "'.");
                return null;
            }
            else if (!BuiltInTypes.TryGetValue(element.Name.LocalName, out metadataName))
            {
                Error(UnknownType, "Unknown UI element '" + element.Name.LocalName + "'.");
                return null;
            }

            INamedTypeSymbol type = _compilation.GetTypeByMetadataName(metadataName);
            if (type == null)
                Error(UnknownType, "Cannot resolve UI type '" + metadataName + "'.");
            return type;
        }

        string ConvertLiteral(string value, ITypeSymbol targetType)
        {
            var namedTargetType = targetType as INamedTypeSymbol;
            if (namedTargetType != null &&
                namedTargetType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            {
                if (string.Equals(value, "null", StringComparison.OrdinalIgnoreCase))
                    return "null";
                return ConvertLiteral(value, namedTargetType.TypeArguments[0]);
            }

            if (targetType.SpecialType == SpecialType.System_String ||
                targetType.SpecialType == SpecialType.System_Object)
                return SymbolDisplay.FormatLiteral(value, true);
            if (targetType.SpecialType == SpecialType.System_Boolean)
            {
                bool parsed;
                if (bool.TryParse(value, out parsed))
                    return parsed ? "true" : "false";
            }
            if (targetType.SpecialType == SpecialType.System_Int32)
            {
                int parsed;
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                    return parsed.ToString(CultureInfo.InvariantCulture);
            }
            if (targetType.SpecialType == SpecialType.System_Single)
            {
                float parsed;
                if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                    return parsed.ToString("R", CultureInfo.InvariantCulture) + "f";
            }
            if (targetType.SpecialType == SpecialType.System_Double)
            {
                double parsed;
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                    return parsed.ToString("R", CultureInfo.InvariantCulture) + "d";
            }
            if (targetType.TypeKind == TypeKind.Enum)
            {
                IFieldSymbol member = targetType.GetMembers()
                    .OfType<IFieldSymbol>()
                    .FirstOrDefault(field => field.HasConstantValue &&
                        string.Equals(field.Name, value, StringComparison.OrdinalIgnoreCase));
                if (member != null)
                {
                    return targetType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) +
                           "." + member.Name;
                }
            }

            string fullTypeName = targetType.ToDisplayString();
            if (fullTypeName == "VRageMath.Vector2")
            {
                float[] values = ParseFloats(value, 1, 2);
                if (values != null)
                {
                    if (values.Length == 1)
                        return "new global::VRageMath.Vector2(" + FormatFloat(values[0]) + ")";
                    return "new global::VRageMath.Vector2(" + FormatFloat(values[0]) + ", " +
                           FormatFloat(values[1]) + ")";
                }
            }
            if (fullTypeName == "VRageMath.Vector4")
            {
                float[] values = ParseFloats(value, 1, 4);
                if (values != null && (values.Length == 1 || values.Length == 2 || values.Length == 4))
                {
                    if (values.Length == 1)
                        return "new global::VRageMath.Vector4(" + FormatFloat(values[0]) + ")";
                    if (values.Length == 2)
                    {
                        return "new global::VRageMath.Vector4(" + FormatFloat(values[0]) + ", " +
                               FormatFloat(values[1]) + ", " + FormatFloat(values[0]) + ", " +
                               FormatFloat(values[1]) + ")";
                    }
                    return "new global::VRageMath.Vector4(" +
                           string.Join(", ", values.Select(FormatFloat)) + ")";
                }
            }
            if (fullTypeName == "VRageMath.Color")
            {
                string color = ConvertColorLiteral(value);
                if (color != null)
                    return color;
            }
            if (fullTypeName == "Acrux.Styling.BackgroundBrush")
            {
                string color = ConvertColorLiteral(value);
                if (color != null)
                    return "new global::Acrux.Styling.SolidColorBrush(" + color + ")";
            }

            if (fullTypeName == "Acrux.Styling.Transform")
                return "global::Acrux.Styling.Transform.Parse(" + SymbolDisplay.FormatLiteral(value, true) + ")";

            Error(UnsupportedMarkup,
                "Literal '" + value + "' cannot be compiled for property type '" +
                targetType.ToDisplayString() + "'.");
            return null;
        }

        static string ConvertColorLiteral(string value)
        {
            byte[] color = ParseColor(value);
            return color == null
                ? null
                : "new global::VRageMath.Color(" + color[0] + ", " + color[1] + ", " + color[2] + ", " + color[3] + ")";
        }

        string ConvertFloatArray(string value)
        {
            float[] values = ParseFloats(value, 1, int.MaxValue);
            if (values == null)
            {
                Error(UnsupportedMarkup, "Grid segments must be numeric values.");
                return null;
            }
            return string.Join(", ", values.Select(FormatFloat));
        }

        string ConvertGridLengths(string value)
        {
            string[] parts = (value ?? string.Empty).Split(
                new[] { ',', ' ', '\t', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                Error(UnsupportedMarkup, "Grid definitions require at least one segment.");
                return null;
            }

            var expressions = new string[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                if (string.Equals(part, "Auto", StringComparison.OrdinalIgnoreCase))
                {
                    expressions[i] = "global::Acrux.Layout.GridLength.Auto";
                    continue;
                }
                if (part.EndsWith("*", StringComparison.Ordinal))
                {
                    string weightText = part.Substring(0, part.Length - 1);
                    float weight = 1f;
                    if ((weightText.Length != 0 && !float.TryParse(
                             weightText,
                             NumberStyles.Float,
                             CultureInfo.InvariantCulture,
                             out weight)) || weight < 0f)
                    {
                        Error(UnsupportedMarkup, "Invalid star Grid row '" + part + "'.");
                        return null;
                    }
                    expressions[i] = "new global::Acrux.Layout.GridLength(" +
                                     FormatFloat(weight) + ", global::Acrux.Layout.GridUnitType.Star)";
                    continue;
                }

                float pixels;
                if (!float.TryParse(
                        part,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out pixels) || pixels < 0f)
                {
                    Error(UnsupportedMarkup, "Invalid Grid row '" + part + "'.");
                    return null;
                }
                expressions[i] = "new global::Acrux.Layout.GridLength(" +
                                 FormatFloat(pixels) + ")";
            }
            return string.Join(", ", expressions);
        }

        static float[] ParseFloats(string value, int minimum, int maximum)
        {
            string[] parts = (value ?? string.Empty).Split(
                new[] { ',', ' ', '\t', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < minimum || parts.Length > maximum)
                return null;
            var result = new float[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!float.TryParse(
                        parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out result[i]))
                    return null;
            }
            return result;
        }

        static byte[] ParseColor(string value)
        {
            uint argb;
            if (global::Acrux.Markup.KnownColors.TryGetArgb(value, out argb))
                return new[] { (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb, (byte)(argb >> 24) };
            if (!string.IsNullOrEmpty(value) && value[0] == '#')
            {
                string hex = value.Substring(1);
                uint parsed;
                if (hex.Length == 3 && uint.TryParse(
                        hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out parsed))
                {
                    return new[]
                    {
                        ExpandHexNibble((byte)(parsed >> 8)),
                        ExpandHexNibble((byte)(parsed >> 4)),
                        ExpandHexNibble((byte)parsed),
                        (byte)255
                    };
                }
                if (hex.Length == 4 && uint.TryParse(
                        hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out parsed))
                {
                    return new[]
                    {
                        ExpandHexNibble((byte)(parsed >> 8)),
                        ExpandHexNibble((byte)(parsed >> 4)),
                        ExpandHexNibble((byte)parsed),
                        ExpandHexNibble((byte)(parsed >> 12))
                    };
                }
                if (hex.Length == 6 && uint.TryParse(
                        hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out parsed))
                {
                    return new[]
                    {
                        (byte)(parsed >> 16),
                        (byte)(parsed >> 8),
                        (byte)parsed,
                        (byte)255
                    };
                }
                if (hex.Length == 8 && uint.TryParse(
                        hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out parsed))
                {
                    return new[]
                    {
                        (byte)(parsed >> 16),
                        (byte)(parsed >> 8),
                        (byte)parsed,
                        (byte)(parsed >> 24)
                    };
                }
                return null;
            }

            float[] components = ParseFloats(value, 3, 4);
            if (components == null || components.Any(component => component < 0f || component > 255f))
                return null;
            return new[]
            {
                (byte)components[0],
                (byte)components[1],
                (byte)components[2],
                components.Length == 4 ? (byte)components[3] : (byte)255
            };
        }

        static byte ExpandHexNibble(byte value)
        {
            return (byte)((value & 0x0F) * 17);
        }

        static string FormatFloat(float value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture) + "f";
        }

        void Error(DiagnosticDescriptor descriptor, string message)
        {
            HasErrors = true;
            Report(_context, descriptor, _filePath + ": " + message);
        }
    }

    static INamedTypeSymbol ResolveMarkupType(XElement element, Compilation compilation)
    {
        string metadataName;
        string namespaceName = element.Name.NamespaceName;
        if (namespaceName.StartsWith("using:", StringComparison.Ordinal))
        {
            metadataName = namespaceName.Substring("using:".Length) + "." +
                           element.Name.LocalName;
        }
        else if (IsAcruxNamespace(namespaceName) &&
                 BuiltInTypes.TryGetValue(element.Name.LocalName, out metadataName))
        {
        }
        else
        {
            return null;
        }
        return compilation.GetTypeByMetadataName(metadataName);
    }

    static ITypeSymbol ResolveDataType(
        XElement element,
        XElement namespaceRoot,
        Compilation compilation,
        SourceProductionContext context,
        string filePath)
    {
        string value = GetDirective(element, "DataType");
        if (string.IsNullOrWhiteSpace(value))
            return null;
        value = value.Trim();

        string metadataName = value;
        int separator = value.IndexOf(':');
        if (separator > 0)
        {
            string prefix = value.Substring(0, separator);
            XNamespace xmlNamespace = namespaceRoot.GetNamespaceOfPrefix(prefix);
            if (xmlNamespace == null ||
                !xmlNamespace.NamespaceName.StartsWith("using:", StringComparison.Ordinal))
            {
                Report(context, UnknownType,
                    filePath + ": x:DataType prefix '" + prefix + "' is not mapped to a using: namespace.");
                return null;
            }
            metadataName = xmlNamespace.NamespaceName.Substring("using:".Length) +
                           "." + value.Substring(separator + 1);
        }

        ITypeSymbol type = compilation.GetTypeByMetadataName(metadataName);
        if (type == null)
            Report(context, UnknownType,
                filePath + ": Cannot resolve x:DataType '" + value + "'.");
        else if (!IsObservable(type))
            // Bindings follow the view model through INotifyPropertyChanged, in game (where the view model may belong
            // to another mod) and in the preview alike; without it a binding reads its source once and never updates.
            Report(context, UnobservableDataType,
                filePath + ": x:DataType '" + value + "' must be an observable view model: derive it from " +
                "Adk.Mvvm.ObservableObject (or implement System.ComponentModel.INotifyPropertyChanged).");
        return type;
    }

    static bool IsObservable(ITypeSymbol type)
    {
        if (type.ToDisplayString() == "System.ComponentModel.INotifyPropertyChanged")
            return true;
        return type.AllInterfaces.Any(i => i.ToDisplayString() == "System.ComponentModel.INotifyPropertyChanged");
    }

    internal sealed class DesignAccessor
    {
        public DesignAccessor(string name, ITypeSymbol dataType, ViewModelMember property, ITypeSymbol boundType)
        {
            BoundType = boundType;
            ReadName = name + "Read";
            WriteName = name + "Write";
            DataType = dataType;
            Property = property;
        }

        public string ReadName { get; }
        public string WriteName { get; }
        public ITypeSymbol DataType { get; }
        public ViewModelMember Property { get; }
        // The binding's value type: what a design view model's value must convert to.
        public ITypeSymbol BoundType { get; }
        public bool Write { get; set; }
    }

    // In the shipped source: the bodiless declarations (no design type named, the calls compile away).
    static void AppendDesignDeclarations(StringBuilder builder, IReadOnlyList<DesignAccessor> accessors)
    {
        foreach (DesignAccessor accessor in accessors)
        {
            builder.Append("        static partial void ").Append(accessor.ReadName).AppendLine("(object source, ref object value);");
            if (accessor.Write)
                builder.Append("        static partial void ").Append(accessor.WriteName).AppendLine("(object source, object value);");
        }
    }

    // The design-build source of a view: its design accessors implemented over the mod's design-only types (declared in
    // design files) that have a property of the bound name and a compatible type. Named as design code (see ADK's
    // DesignSources: the design suffix mdk.ini ignores), so MDK leaves it out of the packed mod, as it does the design
    // types; null when no type fits.
    static string BuildDesignSource(INamedTypeSymbol viewType, IReadOnlyList<DesignAccessor> accessors,
        IReadOnlyList<INamedTypeSymbol> designTypes, IReadOnlyList<PreviewClones.Clone> clones, Compilation compilation)
    {
        var body = new StringBuilder();
        foreach (DesignAccessor accessor in accessors)
        {
            string boundType = accessor.BoundType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var readers = new StringBuilder();
            var writers = new StringBuilder();
            int index = 0;
            foreach (INamedTypeSymbol designType in designTypes)
            {
                if (SymbolEqualityComparer.Default.Equals(designType, accessor.DataType) ||
                    !compilation.IsSymbolAccessibleWithin(designType, viewType))
                    continue;
                ViewModelMember property = ViewModelMember.Find(designType, accessor.Property.Name, compilation);
                if (property == null || !property.ReadableByView)
                    continue;
                string name = designType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                string variable = "__d" + index++;
                if (compilation.ClassifyConversion(property.Type, accessor.BoundType).IsImplicit)
                    readers.Append("            { var ").Append(variable).Append(" = source as ").Append(name).Append("; if (")
                        .Append(variable).Append(" != null) { value = (").Append(boundType).Append(")").Append(variable).Append('.')
                        .Append(property.Name).AppendLine("; return; } }");
                if (accessor.Write && property.WritableByView &&
                    compilation.ClassifyConversion(accessor.BoundType, property.Type).IsImplicit)
                    writers.Append("            { var ").Append(variable).Append(" = source as ").Append(name).Append("; if (")
                        .Append(variable).Append(" != null) { ").Append(variable).Append('.').Append(property.Name).Append(" = (")
                        .Append(boundType).AppendLine(")value; return; } }");
            }
            // The generated preview clones (FooViewModelPreviewModel), known from the same plan PreviewCloneGenerator emits them by.
            foreach (PreviewClones.Clone clone in clones)
            {
                PreviewClones.Member member = clone.Members.FirstOrDefault(m => m.Name == accessor.Property.Name);
                if (member == null || !compilation.IsSymbolAccessibleWithin(clone.Original, viewType))
                    continue;
                string variable = "__d" + index++;
                if (member.ConvertsTo(accessor.BoundType, compilation))
                    readers.Append("            { var ").Append(variable).Append(" = source as ").Append(clone.ModelFullName).Append("; if (")
                        .Append(variable).Append(" != null) { value = (").Append(boundType).Append(")").Append(variable).Append('.')
                        .Append(member.Name).AppendLine("; return; } }");
                if (accessor.Write && member.Kind == PreviewClones.MemberKind.Value &&
                    compilation.ClassifyConversion(accessor.BoundType, member.Type).IsImplicit)
                    writers.Append("            { var ").Append(variable).Append(" = source as ").Append(clone.ModelFullName).Append("; if (")
                        .Append(variable).Append(" != null) { ").Append(variable).Append('.').Append(member.Name).Append(" = (")
                        .Append(boundType).AppendLine(")value; return; } }");
            }
            if (readers.Length > 0)
            {
                body.Append("        static partial void ").Append(accessor.ReadName).AppendLine("(object source, ref object value)");
                body.AppendLine("        {").Append(readers).AppendLine("        }");
            }
            if (writers.Length > 0)
            {
                body.Append("        static partial void ").Append(accessor.WriteName).AppendLine("(object source, object value)");
                body.AppendLine("        {").Append(writers).AppendLine("        }");
            }
        }
        if (body.Length == 0)
            return null;

        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated/>");
        builder.AppendLine("// Design build only (the IDE preview): binds the view to design view models of other types than its x:DataType.");
        if (!viewType.ContainingNamespace.IsGlobalNamespace)
            builder.Append("namespace ").AppendLine(viewType.ContainingNamespace.ToDisplayString()).AppendLine("{");
        builder.Append("    partial class ").AppendLine(viewType.Name);
        builder.AppendLine("    {");
        builder.Append(body);
        builder.AppendLine("    }");
        if (!viewType.ContainingNamespace.IsGlobalNamespace)
            builder.AppendLine("}");
        return builder.ToString();
    }

    static List<INamedTypeSymbol> CollectDesignTypes(INamespaceSymbol ns, DesignSources design, List<INamedTypeSymbol> types)
    {
        foreach (INamespaceSymbol child in ns.GetNamespaceMembers())
            CollectDesignTypes(child, design, types);
        foreach (INamedTypeSymbol type in ns.GetTypeMembers())
            CollectDesignType(type, design, types);
        return types;
    }

    static void CollectDesignType(INamedTypeSymbol type, DesignSources design, List<INamedTypeSymbol> types)
    {
        if (type.TypeKind == TypeKind.Class && !type.IsStatic && !type.IsGenericType && type.Locations.Any(l => l.IsInSource) &&
            design.IsDesignOnly(type))
            types.Add(type);
        foreach (INamedTypeSymbol nested in type.GetTypeMembers())
            CollectDesignType(nested, design, types);
    }

    static IEnumerable<XElement> GetVisualChildren(XElement element)
    {
        return element.Elements().Where(child => child.Name.LocalName.IndexOf('.') < 0);
    }

    static IEnumerable<XElement> GetPropertyElements(XElement element)
    {
        return element.Elements().Where(child => child.Name.LocalName.IndexOf('.') >= 0);
    }

    static string GetDirective(XElement element, string localName)
    {
        XAttribute attribute = element.Attribute(XName.Get(localName, XamlNamespace));
        if (attribute == null)
            attribute = element.Attribute(XName.Get(localName, XamlNamespaceHttps));
        return attribute == null ? null : attribute.Value;
    }

    static bool IsXamlNamespace(string namespaceName)
    {
        return namespaceName == XamlNamespace || namespaceName == XamlNamespaceHttps;
    }

    static string GetName(XElement element)
    {
        string value = GetDirective(element, "Name");
        if (!string.IsNullOrWhiteSpace(value))
            return value.Trim();
        XAttribute attribute = element.Attributes()
            .FirstOrDefault(item => item.Name.NamespaceName.Length == 0 &&
                (item.Name.LocalName == "Name" || item.Name.LocalName == "name"));
        return attribute == null ? null : attribute.Value.Trim();
    }

    static bool TryParseBinding(string text, out string path)
    {
        path = null;
        Acrux.Markup.BindingMarkup parsed;
        if (!Acrux.Markup.BindingMarkup.TryParse(text, "Binding", out parsed))
            return false;
        string value = text.Trim();
        value = value.Substring("{Binding".Length, value.Length - "{Binding".Length - 1).Trim();
        path = value;
        return path.Length > 0;
    }

    static bool TryParseDynamicResource(string text, out string key)
    {
        key = null;
        if (text == null)
            return false;
        string value = text.Trim();
        const string Prefix = "{DynamicResource ";
        if (!value.StartsWith(Prefix, StringComparison.Ordinal) ||
            !value.EndsWith("}", StringComparison.Ordinal))
            return false;
        key = value.Substring(Prefix.Length, value.Length - Prefix.Length - 1).Trim();
        return key.Length > 0;
    }

    static IEventSymbol FindEvent(INamedTypeSymbol type, string name)
    {
        for (INamedTypeSymbol current = type; current != null; current = current.BaseType)
        {
            IEventSymbol found = current.GetMembers(name)
                .OfType<IEventSymbol>()
                .FirstOrDefault(item => !item.IsStatic && item.DeclaredAccessibility == Accessibility.Public);
            if (found != null)
                return found;
        }
        return null;
    }

    static IPropertySymbol FindProperty(INamedTypeSymbol type, string name)
    {
        for (INamedTypeSymbol current = type; current != null; current = current.BaseType)
        {
            IPropertySymbol property = current.GetMembers(name)
                .OfType<IPropertySymbol>()
                .FirstOrDefault(item => !item.IsStatic);
            if (property != null)
                return property;
        }
        return null;
    }

    static IFieldSymbol FindStyledProperty(
        INamedTypeSymbol type,
        string propertyName,
        ITypeSymbol valueType)
    {
        string fieldName = propertyName + "Property";
        for (INamedTypeSymbol current = type; current != null; current = current.BaseType)
        {
            IFieldSymbol field = current.GetMembers(fieldName)
                .OfType<IFieldSymbol>()
                .FirstOrDefault(item => item.IsStatic &&
                    item.DeclaredAccessibility == Accessibility.Public);
            var fieldType = field == null ? null : field.Type as INamedTypeSymbol;
            if (fieldType == null || fieldType.TypeArguments.Length != 1 ||
                fieldType.ConstructedFrom.ToDisplayString() !=
                    "Acrux.Core.PropertySystem.StyledProperty<T>")
                continue;
            if (SymbolEqualityComparer.Default.Equals(fieldType.TypeArguments[0], valueType))
                return field;
        }
        return null;
    }

    static bool HasParameterlessConstructor(INamedTypeSymbol type)
    {
        if (type.IsAbstract)
            return false;
        // "new T()" also compiles against a constructor whose parameters are all optional (KeySelector's).
        return type.InstanceConstructors.Any(constructor =>
            constructor.Parameters.All(parameter => parameter.IsOptional) &&
            constructor.DeclaredAccessibility != Accessibility.Private);
    }

    static bool IsPartial(INamedTypeSymbol type)
    {
        return type.DeclaringSyntaxReferences.Any(reference =>
            reference.GetSyntax() is Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax declaration &&
            declaration.Modifiers.Any(SyntaxKind.PartialKeyword));
    }

    static bool InheritsFrom(INamedTypeSymbol type, INamedTypeSymbol baseType)
    {
        for (INamedTypeSymbol current = type; current != null; current = current.BaseType)
            if (SymbolEqualityComparer.Default.Equals(current, baseType))
                return true;
        return false;
    }

    static bool IsAssignableTo(INamedTypeSymbol type, ITypeSymbol target)
    {
        if (target.SpecialType == SpecialType.System_Object)
            return true;
        var named = target as INamedTypeSymbol;
        return named != null && InheritsFrom(type, named);
    }

    static bool IsType(INamedTypeSymbol type, string metadataName)
    {
        return type.ToDisplayString() == metadataName;
    }

    static bool IsGridPlacement(string name)
    {
        return name == "Grid.Column" || name == "Grid.Row" ||
               name == "Grid.ColumnSpan" || name == "Grid.RowSpan";
    }

    static string GetGridPlacementProperty(string name)
    {
        switch (name)
        {
            case "Grid.Column": return "GridColumn";
            case "Grid.Row": return "GridRow";
            case "Grid.ColumnSpan": return "GridColumnSpan";
            case "Grid.RowSpan": return "GridRowSpan";
            default: return name;
        }
    }

    static void AppendIndent(StringBuilder builder, int indent)
    {
        builder.Append(' ', indent * 4);
    }

    static DiagnosticDescriptor CreateDiagnostic(string id, string title, string message)
    {
        return new DiagnosticDescriptor(
            id, title, message, "Acrux.Generator", DiagnosticSeverity.Error, true);
    }

    static void Report(
        SourceProductionContext context,
        DiagnosticDescriptor descriptor,
        string message)
    {
        context.ReportDiagnostic(Diagnostic.Create(descriptor, Location.None, message));
    }

    static string GetHintName(INamedTypeSymbol type)
    {
        return type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            .Replace("global::", string.Empty)
            .Replace('.', '_') + ".AcruxView.g.cs";
    }

    sealed class ViewFile : IEquatable<ViewFile>
    {
        public ViewFile(string path, string text, string runtimePath)
        {
            Path = path;
            Text = text;
            RuntimePath = runtimePath;
        }

        public string Path { get; }
        public string Text { get; }
        public string RuntimePath { get; }

        public bool Equals(ViewFile other)
        {
            return other != null && Path == other.Path && Text == other.Text && RuntimePath == other.RuntimePath;
        }

        public override bool Equals(object obj) { return Equals(obj as ViewFile); }

        public override int GetHashCode()
        {
            return (Path ?? string.Empty).GetHashCode() ^ (Text ?? string.Empty).GetHashCode();
        }
    }

    sealed class XamlAsset
    {
        public XamlAsset(string physicalPath, string link, SourceText text)
        {
            PhysicalPath = physicalPath;
            NormalizedPhysicalPath = NormalizePhysicalPath(physicalPath);
            Text = text;

            string packagedPath = string.IsNullOrWhiteSpace(link)
                ? GetContentRelativePath(NormalizedPhysicalPath)
                : NormalizeAssetPath(link);
            if (packagedPath.StartsWith("Content/", StringComparison.OrdinalIgnoreCase))
                packagedPath = packagedPath.Substring("Content/".Length);
            RuntimePath = packagedPath;
        }

        public string PhysicalPath { get; }
        public string NormalizedPhysicalPath { get; }
        public string RuntimePath { get; }
        public SourceText Text { get; }

        static string GetContentRelativePath(string path)
        {
            const string marker = "/Content/";
            int content = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            return content < 0
                ? string.Empty
                : NormalizeAssetPath(path.Substring(content + marker.Length));
        }
    }

    sealed class EventHandlerUse
    {
        public EventHandlerUse(string eventName, string method, ITypeSymbol delegateType)
        {
            Event = eventName;
            Method = method;
            DelegateType = delegateType;
        }

        public string Event { get; }
        public string Method { get; }
        public ITypeSymbol DelegateType { get; }
    }

    sealed class NamedElement
    {
        public NamedElement(string name, INamedTypeSymbol type)
        {
            Name = name;
            Type = type;
        }

        public string Name { get; }
        public INamedTypeSymbol Type { get; }
    }

    sealed class EmittedNode
    {
        public EmittedNode(string variable, INamedTypeSymbol type)
        {
            Variable = variable;
            Type = type;
        }

        public string Variable { get; }
        public INamedTypeSymbol Type { get; }
    }
}
