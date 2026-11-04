using System.Linq;
using System.Xml.Linq;

namespace Acrux.Generator;

/// <summary>
/// Design-time markup, stripped outside design mode: the
/// Blend "d:" directives (d:DataContext, d:DesignWidth, d:DesignHeight, d:PreviewWith) and every <c>Design.*</c>
/// attached property (attribute or property element) are removed before the view is compiled. Only the IDE preview
/// applies them, so a view may name design-only types (e.g. view models in design files mdk.ini ignores, which MDK does not pack).
/// Other attributes of namespaces listed in mc:Ignorable are ignored as well, as XAML's markup compatibility asks.
/// </summary>
static class DesignTimeMarkup
{
    public const string Blend2008 = "http://schemas.microsoft.com/expression/blend/2008";
    public const string MarkupCompatibility = "http://schemas.openxmlformats.org/markup-compatibility/2006";

    public static void Strip(XDocument document)
    {
        XElement root = document.Root;
        if (root == null)
            return;
        // Decided on the whole document first: removing mc:Ignorable early would change the answer for descendants.
        var elements = root.Descendants().Where(IsDesignPropertyElement).ToList();
        var attributes = root.DescendantsAndSelf()
            .SelectMany(element => element.Attributes().Where(a => !a.IsNamespaceDeclaration && IsIgnored(element, a)))
            .ToList();
        foreach (XAttribute attribute in attributes)
            attribute.Remove();
        foreach (XElement element in elements)
            if (element.Parent != null)
                element.Remove();
    }

    static bool IsDesignPropertyElement(XElement element)
    {
        return element.Name.LocalName.StartsWith("Design.", System.StringComparison.Ordinal);
    }

    static bool IsIgnored(XElement element, XAttribute attribute)
    {
        string ns = attribute.Name.NamespaceName;
        if (ns == Blend2008 || ns == MarkupCompatibility)
            return true;
        if (ns.Length == 0)
            return attribute.Name.LocalName.StartsWith("Design.", System.StringComparison.Ordinal);
        return IsIgnorableNamespace(element, ns);
    }

    // mc:Ignorable="d other" on this element or an ancestor names prefixes whose attributes a consumer may skip.
    static bool IsIgnorableNamespace(XElement element, string ns)
    {
        for (XElement current = element; current != null; current = current.Parent)
        {
            XAttribute ignorable = current.Attribute(XName.Get("Ignorable", MarkupCompatibility));
            if (ignorable == null)
                continue;
            foreach (string prefix in ignorable.Value.Split(new[] { ' ', '\t', '\r', '\n' }, System.StringSplitOptions.RemoveEmptyEntries))
                if (current.GetNamespaceOfPrefix(prefix)?.NamespaceName == ns)
                    return true;
        }
        return false;
    }
}
