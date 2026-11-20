using System;
using System.Collections.Generic;
using System.Text;
using Acrux.Core;
using Acrux.Layout;

namespace Acrux.Markup
{
    public sealed class UiXmlLoader
    {
        readonly TypeRegistry _types;

        public UiXmlLoader(TypeRegistry types)
        {
            if (types == null)
                throw new ArgumentNullException(nameof(types));
            _types = types;
        }

        public Control Load(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml))
                throw new ArgumentException("UI XML cannot be empty.", nameof(xml));
            Node root = ParseNode(xml, XamlNamespaceScope.Default);
            return Build(root, null, null);
        }

        internal Control Build(
            Node node,
            TemplatedControl templatedParent,
            IDictionary<string, Control> names)
        {
            Control control = _types.Create(node.Name);
            control.TemplatedParent = templatedParent;
            for (int i = 0; i < node.Attributes.Count; i++)
            {
                Attribute attribute = node.Attributes[i];
                if (IsGridPlacement(attribute.Name))
                    continue;
                _types.SetProperty(
                    control, node.Name, attribute.Name, attribute.Value, templatedParent);
            }

            if (names != null && !string.IsNullOrWhiteSpace(control.Name))
            {
                if (names.ContainsKey(control.Name))
                    throw new FormatException(
                        "Duplicate template name '" + control.Name + "'.");
                names.Add(control.Name, control);
            }

            IControlContainer container = control as IControlContainer;
            for (int i = 0; i < node.Children.Count; i++)
            {
                Node propertyNode = node.Children[i];
                int separator = propertyNode.Name.IndexOf('.');
                if (propertyNode.IsPropertyElement)
                {
                    if (propertyNode.Children.Count != 1)
                        throw new FormatException(propertyNode.Name + " requires exactly one value.");
                    _types.SetPropertyValue(control, node.Name, propertyNode.Name.Substring(separator + 1),
                        BuildValue(propertyNode.Children[0]));
                    continue;
                }
                if (container == null)
                    throw new InvalidOperationException(node.Name + " does not accept visual children.");
                Control child = Build(node.Children[i], templatedParent, names);
                Grid grid = control as Grid;
                VisualElement visualChild = child as VisualElement;
                if (grid != null && visualChild != null)
                {
                    Node childNode = node.Children[i];
                    grid.Set(
                        visualChild,
                        GetIntAttribute(childNode, "Grid.Column", 0),
                        GetIntAttribute(childNode, "Grid.Row", 0),
                        GetIntAttribute(childNode, "Grid.ColumnSpan", 1),
                        GetIntAttribute(childNode, "Grid.RowSpan", 1));
                }
                else
                {
                    container.AddTemplateChild(child);
                }
            }
            return control;
        }

        internal static object BuildValue(Node node)
        {
            var converter = new PropertyConverter();
            switch (node.Name)
            {
                case "ImageBrush":
                    var image = new Acrux.Styling.ImageBrush();
                    foreach (Attribute attribute in node.Attributes)
                    {
                        if (attribute.Name == "Source")
                            image.Source = attribute.Value;
                        else if (attribute.Name == "Stretch")
                            image.Stretch = (Acrux.Styling.Stretch)converter.Convert(attribute.Value, typeof(Acrux.Styling.Stretch));
                        else
                            throw new FormatException("Unknown property 'ImageBrush." + attribute.Name + "'.");
                    }
                    return image;
                case "SolidColorBrush":
                    foreach (Attribute attribute in node.Attributes)
                        if (attribute.Name == "Color")
                            return new Acrux.Styling.SolidColorBrush((VRageMath.Color)converter.Convert(attribute.Value, typeof(VRageMath.Color)));
                    throw new FormatException("SolidColorBrush requires Color.");
                case "LinearGradientBrush":
                case "RadialGradientBrush":
                case "ConicGradientBrush":
                    return GradientBrushMarkup.Build(node);
                default:
                    throw new FormatException("Unsupported property value element '" + node.Name + "'.");
            }
        }

        internal static Node ParseNode(string xml)
        {
            return ParseNode(xml, XamlNamespaceScope.Default);
        }

        internal static Node ParseNode(string xml, XamlNamespaceScope namespaces)
        {
            if (string.IsNullOrWhiteSpace(xml))
                throw new ArgumentException("UI XML cannot be empty.", nameof(xml));
            return new Parser(xml).ParseDocument(namespaces ?? XamlNamespaceScope.Default);
        }

        static bool IsGridPlacement(string name)
        {
            return string.Equals(name, "Grid.Column", StringComparison.Ordinal) ||
                   string.Equals(name, "Grid.Row", StringComparison.Ordinal) ||
                   string.Equals(name, "Grid.ColumnSpan", StringComparison.Ordinal) ||
                   string.Equals(name, "Grid.RowSpan", StringComparison.Ordinal);
        }

        static int GetIntAttribute(Node node, string name, int fallback)
        {
            for (int i = 0; i < node.Attributes.Count; i++)
            {
                Attribute attribute = node.Attributes[i];
                if (string.Equals(attribute.Name, name, StringComparison.Ordinal))
                    return int.Parse(attribute.Value, System.Globalization.CultureInfo.InvariantCulture);
            }
            return fallback;
        }

        internal sealed class Node
        {
            public string Name;
            public bool IsPropertyElement;
            public readonly List<Attribute> Attributes = new List<Attribute>();
            public readonly List<Node> Children = new List<Node>();
            // For Style and StyleInclude elements (a control's own Styles): the element's markup and the namespaces in
            // scope, which the style parser reads.
            public string Xml;
            public XamlNamespaceScope Namespaces;
        }

        internal struct Attribute
        {
            public string Name;
            public string Value;
        }

        sealed class Parser
        {
            readonly string _text;
            int _position;

            public Parser(string text)
            {
                _text = text;
            }

            public Node ParseDocument(XamlNamespaceScope namespaces)
            {
                SkipTrivia();
                Node root = ParseElement(namespaces);
                SkipTrivia();
                if (_position != _text.Length)
                    Fail("Unexpected content after the root element.");
                return root;
            }

            Node ParseElement(XamlNamespaceScope namespaces)
            {
                int start = _position;
                Expect('<');
                if (Peek('/'))
                    Fail("Unexpected closing element.");

                string name = ParseName();
                var node = new Node();
                var declarations = new List<KeyValuePair<string, string>>();
                bool selfClosing = false;
                while (true)
                {
                    SkipWhitespace();
                    if (Consume("/>"))
                    {
                        selfClosing = true;
                        break;
                    }
                    if (Consume(">"))
                        break;

                    string attributeName = ParseName();
                    SkipWhitespace();
                    Expect('=');
                    SkipWhitespace();
                    string value = ParseQuotedValue();
                    if (XamlNamespaceScope.IsDeclaration(attributeName))
                    {
                        declarations.Add(new KeyValuePair<string, string>(attributeName, value));
                        continue;
                    }
                    node.Attributes.Add(new Attribute
                    {
                        Name = string.Equals(attributeName, "x:Name", StringComparison.Ordinal)
                            ? "Name"
                            : attributeName,
                        Value = value
                    });
                }

                namespaces = namespaces.With(declarations);
                for (int i = 0; i < node.Attributes.Count; i++)
                {
                    Attribute attribute = node.Attributes[i];
                    if (attribute.Value.Length > 2 && attribute.Value[0] == '{')
                    {
                        int argument = attribute.Value.IndexOf(' ');
                        if (argument > 1 && attribute.Value.Substring(1, argument - 1).IndexOf(':') > 0)
                        {
                            string extension = attribute.Value.Substring(1, argument - 1);
                            try
                            {
                                attribute.Value = "{" + namespaces.ResolveTypeName(extension) +
                                                  attribute.Value.Substring(argument);
                            }
                            catch (FormatException error)
                            {
                                Fail(error.Message);
                            }
                        }
                    }
                    if (!string.Equals(attribute.Name, "x:DataType", StringComparison.Ordinal))
                    {
                        node.Attributes[i] = attribute;
                        continue;
                    }
                    try
                    {
                        attribute.Value = namespaces.ResolveTypeName(attribute.Value.Trim());
                    }
                    catch (FormatException error)
                    {
                        Fail(error.Message);
                    }
                    node.Attributes[i] = attribute;
                }
                int prefix = name.IndexOf(':');
                node.IsPropertyElement = name.IndexOf('.', prefix + 1) > 0;
                try
                {
                    node.Name = node.IsPropertyElement ? name.Substring(prefix + 1) : namespaces.ResolveTypeName(name);
                }
                catch (FormatException error)
                {
                    Fail(error.Message);
                }
                string localName = name.Substring(prefix + 1);
                bool styleMarkup = !node.IsPropertyElement && (localName == "Style" || localName == "StyleInclude");
                if (styleMarkup)
                    node.Namespaces = namespaces;
                if (selfClosing)
                {
                    if (styleMarkup)
                        node.Xml = _text.Substring(start, _position - start);
                    return node;
                }

                while (true)
                {
                    SkipTrivia();
                    if (Consume("</"))
                    {
                        string closingName = ParseName();
                        if (!string.Equals(name, closingName, StringComparison.Ordinal))
                            Fail("Expected </" + name + "> but found </" + closingName + ">.");
                        SkipWhitespace();
                        Expect('>');
                        if (styleMarkup)
                            node.Xml = _text.Substring(start, _position - start);
                        return node;
                    }
                    if (!Peek('<'))
                        Fail("Text content is not supported; use a Text property.");
                    node.Children.Add(ParseElement(namespaces));
                }
            }

            string ParseName()
            {
                int start = _position;
                while (_position < _text.Length)
                {
                    char value = _text[_position];
                    if (!(char.IsLetterOrDigit(value) || value == '_' || value == '-' ||
                          value == '.' || value == ':'))
                        break;
                    _position++;
                }
                if (_position == start)
                    Fail("Expected an XML name.");
                return _text.Substring(start, _position - start);
            }

            string ParseQuotedValue()
            {
                if (_position >= _text.Length ||
                    (_text[_position] != '\'' && _text[_position] != '"'))
                    Fail("Expected a quoted attribute value.");
                char quote = _text[_position++];
                int start = _position;
                while (_position < _text.Length && _text[_position] != quote)
                    _position++;
                if (_position >= _text.Length)
                    Fail("Unterminated attribute value.");
                string value = DecodeEntities(_text.Substring(start, _position - start));
                _position++;
                return value;
            }

            void SkipTrivia()
            {
                while (true)
                {
                    SkipWhitespace();
                    if (Consume("<?"))
                    {
                        SkipUntil("?>");
                        continue;
                    }
                    if (Consume("<!--"))
                    {
                        SkipUntil("-->");
                        continue;
                    }
                    return;
                }
            }

            void SkipWhitespace()
            {
                while (_position < _text.Length && char.IsWhiteSpace(_text[_position]))
                    _position++;
            }

            void SkipUntil(string marker)
            {
                int found = _text.IndexOf(marker, _position, StringComparison.Ordinal);
                if (found < 0)
                    Fail("Unterminated XML trivia.");
                _position = found + marker.Length;
            }

            bool Peek(char value)
            {
                return _position < _text.Length && _text[_position] == value;
            }

            bool Consume(string value)
            {
                if (_position + value.Length > _text.Length ||
                    string.CompareOrdinal(_text, _position, value, 0, value.Length) != 0)
                    return false;
                _position += value.Length;
                return true;
            }

            void Expect(char value)
            {
                if (!Peek(value))
                    Fail("Expected '" + value + "'.");
                _position++;
            }

            void Fail(string message)
            {
                throw new FormatException(message + " Position: " + _position + ".");
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
