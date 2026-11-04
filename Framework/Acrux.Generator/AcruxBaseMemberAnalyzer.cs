using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using Adk.Analyzer;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Acrux.Generator;

/// <summary>
/// Warns when compiled views or runtime stylesheets use Acrux members or elements that are not
/// whitelisted as base members.
/// </summary>
[Generator]
public sealed class AcruxBaseMemberAnalyzer : IIncrementalGenerator
{
    const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
    const string XamlNamespaceHttps = "https://schemas.microsoft.com/winfx/2006/xaml";
    const string AcruxNamespace = "https://github.com/ArthurGamerHD/AcruxUI";

    static readonly DiagnosticDescriptor NonBaseMember = new DiagnosticDescriptor(
        "ACRUX007",
        "Member is not a whitelisted base member",
        "Member '{0}.{1}' is not whitelisted as a base member",
        "Acrux.Generator",
        DiagnosticSeverity.Warning,
        true);

    static readonly DiagnosticDescriptor NonBaseElement = new DiagnosticDescriptor(
        "ACRUX008",
        "Element is not a whitelisted base member",
        "Element '{0}' is not whitelisted as a base member",
        "Acrux.Generator",
        DiagnosticSeverity.Warning,
        true);

    static readonly HashSet<string> MarkupElements = new HashSet<string>(StringComparer.Ordinal)
    {
        "Styles", "Style", "Setter", "StyleInclude", "ControlTemplate", "DataTemplate", "ItemsPanelTemplate",
        "Animation", "KeyFrame"
    };

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var files = context.AdditionalTextsProvider
            .Where(static file => AcruxViewGenerator.IsXamlFile(file.Path))
            .Select(static (file, cancellationToken) =>
                new KeyValuePair<string, SourceText>(file.Path, file.GetText(cancellationToken)));

        context.RegisterSourceOutput(
            "gui",
            files.Combine(context.CompilationProvider),
            static (output, input) => Analyze(input.Left.Key, input.Left.Value, input.Right, output));
    }

    static void Analyze(string path, SourceText text, Compilation compilation, SourceProductionContext context)
    {
        if (text == null || text.Length == 0)
            return;

        XDocument document;
        try
        {
            document = XDocument.Parse(text.ToString(), LoadOptions.SetLineInfo);
            DesignTimeMarkup.Strip(document);
        }
        catch (Exception error) when (error is XmlException || error is InvalidOperationException)
        {
            return;
        }

        if (document.Root == null)
            return;
        string rootNamespace = document.Root.Name.NamespaceName;
        if (document.Root.Name.LocalName != "Styles" && rootNamespace != AcruxNamespace &&
            !rootNamespace.StartsWith("using:", StringComparison.Ordinal))
            return;

        new Walker(path, text, new AcruxTypes(compilation), compilation, context).Visit(document.Root, null);
    }

    sealed class Walker
    {
        readonly string _path;
        readonly SourceText _text;
        readonly AcruxTypes _acruxTypes;
        readonly Compilation _compilation;
        readonly SourceProductionContext _context;

        public Walker(string path, SourceText text, AcruxTypes acruxTypes, Compilation compilation,
            SourceProductionContext context)
        {
            _path = path;
            _text = text;
            _acruxTypes = acruxTypes;
            _compilation = compilation;
            _context = context;
        }

        public void Visit(XElement element, INamedTypeSymbol parentType)
        {
            string localName = element.Name.LocalName;
            int separator = localName.IndexOf('.');
            if (separator > 0)
            {
                if (parentType != null)
                    CheckMember(parentType, localName.Substring(separator + 1), element);
                VisitChildren(element, parentType);
                return;
            }

            if (localName == "Style")
            {
                VisitStyle(element);
                return;
            }

            if (MarkupElements.Contains(localName) || IsXaml(element.Name.NamespaceName))
            {
                VisitChildren(element, null);
                return;
            }

            INamedTypeSymbol type = ResolveElement(element);
            if (type != null)
            {
                if (BaseMemberWhitelist.IsUnsupportedType(type))
                    Report(NonBaseElement, element, type.Name);
                // The root's attributes set the view's own class (x:Class, which derives from the root element), as the
                // view generator emits them: members the class adds are the app's, checked no further.
                CheckAttributes(element, element.Parent == null ? ViewClass(element) ?? type : type);
            }
            VisitChildren(element, type);
        }

        void VisitChildren(XElement element, INamedTypeSymbol type)
        {
            foreach (XElement child in element.Elements())
                Visit(child, type);
        }

        void VisitStyle(XElement style)
        {
            XAttribute selector = style.Attribute("Selector");
            INamedTypeSymbol type = selector == null ? null : ResolveSelector(style, selector.Value);
            if (type != null && BaseMemberWhitelist.IsUnsupportedType(type))
                Report(NonBaseElement, selector, type.Name);

            foreach (XElement child in style.Elements())
            {
                if (child.Name.LocalName == "Setter")
                {
                    XAttribute property = child.Attribute("Property");
                    if (type != null && property != null)
                        CheckMemberName(type, property.Value.Trim(), property);
                }
                else if (child.Name.LocalName == "Style.Animations" && type != null)
                {
                    foreach (XElement setter in child.Descendants())
                    {
                        XAttribute property = setter.Name.LocalName == "Setter" ? setter.Attribute("Property") : null;
                        if (property != null)
                            CheckMemberName(type, property.Value.Trim(), property);
                    }
                }
                Visit(child, null);
            }
        }

        INamedTypeSymbol ViewClass(XElement root)
        {
            string className = (string)root.Attribute(XName.Get("Class", "http://schemas.microsoft.com/winfx/2006/xaml"));
            return string.IsNullOrWhiteSpace(className) ? null : _compilation.GetTypeByMetadataName(className.Trim());
        }

        void CheckAttributes(XElement element, INamedTypeSymbol type)
        {
            foreach (XAttribute attribute in element.Attributes())
            {
                if (attribute.IsNamespaceDeclaration || IsXaml(attribute.Name.NamespaceName))
                    continue;
                CheckMemberName(type, attribute.Name.LocalName, attribute);
            }
        }

        void CheckMemberName(INamedTypeSymbol type, string name, XObject node)
        {
            int separator = name.IndexOf('.');
            if (separator < 0)
            {
                CheckMember(type, name, node);
                return;
            }

            INamedTypeSymbol owner = _acruxTypes.Find(name.Substring(0, separator));
            string member = name.Substring(separator + 1);
            if (owner != null && !BaseMemberWhitelist.IsAllowedAttached(owner, member))
                Report(NonBaseMember, node, owner.Name, member);
        }

        void CheckMember(INamedTypeSymbol type, string member, XObject node)
        {
            if (!BaseMemberWhitelist.IsAllowedMember(type, member))
                Report(NonBaseMember, node, type.Name, member);
        }

        INamedTypeSymbol ResolveElement(XElement element)
        {
            string ns = element.Name.NamespaceName;
            if (ns.Length == 0 || ns == AcruxNamespace)
                return _acruxTypes.Find(element.Name.LocalName);
            string clrNamespace = ClrNamespace(ns);
            return clrNamespace == null
                ? null
                : _compilation.GetTypeByMetadataName(clrNamespace + "." + element.Name.LocalName);
        }

        INamedTypeSymbol ResolveSelector(XElement style, string selector)
        {
            string text = selector.Trim();
            int descendant = text.LastIndexOfAny(new[] { ' ', '\t', '\r', '\n' });
            if (descendant >= 0)
                text = text.Substring(descendant + 1);
            int bar = text.IndexOf('|');
            int start = bar + 1;
            int end = start;
            while (end < text.Length && text[end] != '.' && text[end] != ':' && text[end] != '[' && !char.IsWhiteSpace(text[end]))
                end++;
            string local = text.Substring(start, end - start);
            if (bar < 0)
                return _acruxTypes.Find(local);
            XNamespace ns = style.GetNamespaceOfPrefix(text.Substring(0, bar).Trim());
            string clrNamespace = ns == null ? null : ClrNamespace(ns.NamespaceName);
            return clrNamespace == null ? null : _compilation.GetTypeByMetadataName(clrNamespace + "." + local);
        }

        void Report(DiagnosticDescriptor descriptor, XObject node, params object[] arguments)
        {
            _context.ReportDiagnostic(Diagnostic.Create(descriptor, GetLocation(node), arguments));
        }

        Location GetLocation(XObject node)
        {
            IXmlLineInfo lineInfo = node;
            if (!lineInfo.HasLineInfo() || lineInfo.LineNumber - 1 >= _text.Lines.Count)
                return Location.None;
            TextLine line = _text.Lines[lineInfo.LineNumber - 1];
            int start = Math.Min(line.End, line.Start + Math.Max(0, lineInfo.LinePosition - 1));
            string label = node is XAttribute attribute ? attribute.ToString() : ((XElement)node).Name.LocalName;
            int length = Math.Min(Math.Max(1, label.Length), Math.Max(0, line.End - start));
            var span = new TextSpan(start, length);
            return Location.Create(_path, span, _text.Lines.GetLinePositionSpan(span));
        }

        static bool IsXaml(string ns)
        {
            return ns == XamlNamespace || ns == XamlNamespaceHttps;
        }

        static string ClrNamespace(string ns)
        {
            if (ns.StartsWith("using:", StringComparison.Ordinal))
                return ns.Substring("using:".Length).Trim();
            if (!ns.StartsWith("clr-namespace:", StringComparison.Ordinal))
                return null;
            string value = ns.Substring("clr-namespace:".Length);
            int assembly = value.IndexOf(';');
            return (assembly < 0 ? value : value.Substring(0, assembly)).Trim();
        }
    }

    sealed class AcruxTypes
    {
        readonly Dictionary<string, INamedTypeSymbol> _types = new Dictionary<string, INamedTypeSymbol>(StringComparer.Ordinal);

        public AcruxTypes(Compilation compilation)
        {
            INamespaceSymbol acrux = compilation.GlobalNamespace.GetNamespaceMembers()
                .FirstOrDefault(ns => ns.Name == "Acrux");
            if (acrux != null)
                Collect(acrux);
        }

        public INamedTypeSymbol Find(string name)
        {
            INamedTypeSymbol type;
            return _types.TryGetValue(name, out type) ? type : null;
        }

        void Collect(INamespaceSymbol ns)
        {
            foreach (INamedTypeSymbol type in ns.GetTypeMembers())
                if (!_types.ContainsKey(type.Name))
                    _types[type.Name] = type;
            foreach (INamespaceSymbol child in ns.GetNamespaceMembers())
                Collect(child);
            foreach (INamedTypeSymbol type in ns.GetTypeMembers())
                CollectNested(type);
        }

        void CollectNested(INamedTypeSymbol type)
        {
            foreach (INamedTypeSymbol nested in type.GetTypeMembers())
            {
                if (nested.DeclaredAccessibility == Accessibility.Public && !_types.ContainsKey(nested.Name))
                    _types[nested.Name] = nested;
                CollectNested(nested);
            }
        }
    }
}
