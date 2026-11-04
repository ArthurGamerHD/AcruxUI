using System;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace Acrux.Generator
{
    /// <summary>
    /// What of a view's XAML only takes effect once compiled: its root element's own attributes (x:Class, Classes,
    /// bindings on the view itself...) and property elements (Resources...). The IDE preview builds the view's content
    /// from the edited XAML but hosts it in the compiled view class, so a change here shows only after a build. The view
    /// generator records the signature of the compiled XAML (design builds only, as assembly metadata named
    /// <see cref="MetadataPrefix"/> + x:Class) and the preview host compares the edited XAML's with it. Left out: what the
    /// preview reads live (Styles, Design.* and d: markup) and namespace declarations. Linked into the preview host.
    /// </summary>
    internal static class ViewRootSignature
    {
        public const string MetadataPrefix = "AcruxView.Root:";

        const string Blend = "http://schemas.microsoft.com/expression/blend/2008";
        const string MarkupCompatibility = "http://schemas.openxmlformats.org/markup-compatibility/2006";

        public static string Of(XElement root)
        {
            var builder = new StringBuilder();
            builder.Append(root.Name.ToString()).Append('\n');
            foreach (XAttribute attribute in root.Attributes()
                         .Where(a => !a.IsNamespaceDeclaration && !IsDesign(a.Name))
                         .OrderBy(a => a.Name.ToString(), StringComparer.Ordinal))
                builder.Append(attribute.Name.ToString()).Append('=').Append(attribute.Value).Append('\n');
            foreach (XElement property in root.Elements().Where(e => e.Name.LocalName.IndexOf('.') > 0))
            {
                string name = property.Name.LocalName;
                if (IsDesign(property.Name) || name.EndsWith(".Styles", StringComparison.Ordinal))
                    continue;
                var copy = new XElement(property);
                foreach (XText blank in copy.DescendantNodes().OfType<XText>().Where(t => string.IsNullOrWhiteSpace(t.Value)).ToList())
                    blank.Remove();
                foreach (XComment comment in copy.DescendantNodes().OfType<XComment>().ToList())
                    comment.Remove();
                builder.Append(copy.ToString(SaveOptions.DisableFormatting)).Append('\n');
            }
            return builder.ToString();
        }

        static bool IsDesign(XName name)
        {
            return name.NamespaceName == Blend || name.NamespaceName == MarkupCompatibility ||
                   name.LocalName.StartsWith("Design.", StringComparison.Ordinal);
        }
    }
}
