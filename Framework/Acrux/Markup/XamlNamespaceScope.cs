using System;
using System.Collections.Generic;

namespace Acrux.Markup
{
    /// <summary>
    /// XML namespace prefixes for runtime XAML. Unprefixed names use the
    /// default Acrux namespace; prefixed names resolve through "using:" or "clr-namespace:".
    /// </summary>
    internal sealed class XamlNamespaceScope
    {
        public static readonly XamlNamespaceScope Default = new XamlNamespaceScope(null);

        readonly XamlNamespaceScope _parent;
        readonly Dictionary<string, string> _namespaces = new Dictionary<string, string>(StringComparer.Ordinal);

        XamlNamespaceScope(XamlNamespaceScope parent)
        {
            _parent = parent;
        }

        public static bool IsDeclaration(string attributeName)
        {
            return string.Equals(attributeName, "xmlns", StringComparison.Ordinal) ||
                   attributeName.StartsWith("xmlns:", StringComparison.Ordinal);
        }

        public XamlNamespaceScope With(IEnumerable<KeyValuePair<string, string>> attributes)
        {
            XamlNamespaceScope scope = null;
            foreach (KeyValuePair<string, string> attribute in attributes)
            {
                if (!attribute.Key.StartsWith("xmlns:", StringComparison.Ordinal))
                    continue;
                string clrNamespace = ParseClrNamespace(attribute.Value);
                if (clrNamespace == null)
                    continue;
                if (scope == null)
                    scope = new XamlNamespaceScope(this);
                scope._namespaces[attribute.Key.Substring("xmlns:".Length)] = clrNamespace;
            }
            return scope ?? this;
        }

        public string ResolveTypeName(string qualifiedName)
        {
            int separator = qualifiedName.IndexOf(':');
            if (separator < 0)
                return qualifiedName;
            return Resolve(qualifiedName.Substring(0, separator), qualifiedName) + "." +
                   qualifiedName.Substring(separator + 1);
        }

        public string ResolveSelectorType(string prefix, string typeName)
        {
            return Resolve(prefix, prefix + "|" + typeName) + "." + typeName;
        }

        string Resolve(string prefix, string qualifiedName)
        {
            for (XamlNamespaceScope scope = this; scope != null; scope = scope._parent)
            {
                string clrNamespace;
                if (scope._namespaces.TryGetValue(prefix, out clrNamespace))
                    return clrNamespace;
            }
            throw new FormatException(
                "Undeclared XML namespace prefix '" + prefix + "' in '" + qualifiedName + "'.");
        }

        static string ParseClrNamespace(string value)
        {
            value = (value ?? string.Empty).Trim();
            const string UsingPrefix = "using:";
            const string ClrPrefix = "clr-namespace:";
            if (value.StartsWith(UsingPrefix, StringComparison.Ordinal))
                return value.Substring(UsingPrefix.Length).Trim();
            if (!value.StartsWith(ClrPrefix, StringComparison.Ordinal))
                return null;
            value = value.Substring(ClrPrefix.Length);
            int assembly = value.IndexOf(';');
            return (assembly < 0 ? value : value.Substring(0, assembly)).Trim();
        }
    }
}
