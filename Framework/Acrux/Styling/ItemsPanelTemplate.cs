using System;
using Acrux.Core;
using Acrux.Layout;
using Acrux.Markup;

namespace Acrux.Styling
{
    public interface ITemplate<out T>
    {
        T Build();
    }

    /// <summary>A fresh layout panel for each items presenter.</summary>
    public sealed class ItemsPanelTemplate : ITemplate<Panel>
    {
        readonly Func<Panel> _factory;
        readonly UiXmlLoader.Node _root;

        public ItemsPanelTemplate(Func<Panel> factory)
        {
            if(factory == null)
                throw new ArgumentNullException(nameof(factory));
            _factory = factory;
        }

        ItemsPanelTemplate(UiXmlLoader.Node root) { _root = root; }

        public static ItemsPanelTemplate Parse(string xml)
        {
            return Parse(xml, XamlNamespaceScope.Default);
        }

        internal static ItemsPanelTemplate Parse(string xml, XamlNamespaceScope namespaces)
        {
            var wrapper = UiXmlLoader.ParseNode(xml, namespaces);
            if (wrapper.Name != "ItemsPanelTemplate" || wrapper.Attributes.Count != 0 || wrapper.Children.Count != 1)
                throw new FormatException("ItemsPanelTemplate requires exactly one panel root.");
            return new ItemsPanelTemplate(wrapper.Children[0]);
        }

        public Panel Build() { return Build(null); }

        internal Panel Build(TemplatedControl owner)
        {
            var panel = _factory != null ? _factory() : new UiXmlLoader(ControlTemplate.TemplateTypes)
                .Build(_root, owner, null) as Panel;
            if (panel == null)
                throw new InvalidOperationException("ItemsPanelTemplate must build a layout panel.");
            return panel;
        }
    }
}
