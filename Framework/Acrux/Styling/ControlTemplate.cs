using System;
using System.Collections.Generic;
using System.Globalization;
using Acrux.Controls;
using Acrux.Controls.Primitives;
using PathShape = Acrux.Controls.Shapes.Path;
using Acrux.Core;
using Acrux.Layout;
using Acrux.Markup;
using Acrux.Rendering;
using VRageMath;

namespace Acrux.Styling
{
    public sealed class ControlTemplateInstance
    {
        internal ControlTemplateInstance(
            VisualElement root,
            IDictionary<string, Control> names)
        {
            Root = root;
            Names = names;
        }

        public VisualElement Root { get; }
        public IDictionary<string, Control> Names { get; }
    }

    /// <summary>An immutable factory for one control's style-owned visual tree.</summary>
    public sealed class ControlTemplate
    {
        internal static readonly TypeRegistry TemplateTypes = CreateTypeRegistry();
        readonly UiXmlLoader.Node _root;
        readonly Func<TemplatedControl, VisualElement> _factory;

        public ControlTemplate(Func<TemplatedControl, VisualElement> factory)
        {
            if (factory == null)
                throw new ArgumentNullException(nameof(factory));
            _factory = factory;
        }

        internal ControlTemplate(UiXmlLoader.Node root)
        {
            if (root == null)
                throw new ArgumentNullException(nameof(root));
            _root = root;
        }

        public static ControlTemplate Parse(string xml)
        {
            return Parse(xml, XamlNamespaceScope.Default);
        }

        internal static ControlTemplate Parse(string xml, XamlNamespaceScope namespaces)
        {
            UiXmlLoader.Node wrapper = UiXmlLoader.ParseNode(xml, namespaces);
            if (!string.Equals(wrapper.Name, "ControlTemplate", StringComparison.Ordinal))
                throw new FormatException("Expected ControlTemplate.");
            if (wrapper.Attributes.Count != 0)
                throw new FormatException("ControlTemplate does not accept attributes.");
            if (wrapper.Children.Count != 1)
                throw new FormatException("ControlTemplate requires exactly one visual root.");
            return new ControlTemplate(wrapper.Children[0]);
        }

        internal ControlTemplateInstance BuildInstance(TemplatedControl owner)
        {
            if (owner == null)
                throw new ArgumentNullException(nameof(owner));
            var names = new Dictionary<string, Control>(StringComparer.Ordinal);
            VisualElement root;
            if (_factory != null)
            {
                root = _factory(owner);
                RegisterTree(root, owner, names);
            }
            else
            {
                root = new UiXmlLoader(TemplateTypes).Build(
                    _root, owner, names) as VisualElement;
            }
            if (root == null)
                throw new InvalidOperationException(
                    "A ControlTemplate root must be a VisualElement.");
            return new ControlTemplateInstance(root, names);
        }

        static void RegisterTree(
            VisualElement element,
            TemplatedControl owner,
            IDictionary<string, Control> names)
        {
            if (element == null)
                return;
            element.TemplatedParent = owner;
            if (!string.IsNullOrWhiteSpace(element.Name))
            {
                if (names.ContainsKey(element.Name))
                    throw new InvalidOperationException(
                        "Duplicate template name '" + element.Name + "'.");
                names.Add(element.Name, element);
            }
            IReadOnlyList<VisualElement> children = element.LogicalChildren;
            for (int i = 0; i < children.Count; i++)
                RegisterTree(children[i], owner, names);
        }

        internal static TypeRegistry CreateTypeRegistry()
        {
            var registry = new TypeRegistry();
            GeneratedTypeCatalog.Register(registry);

            registry.Register<Panel>("Panel", () => new Panel());

            registry.Register<Grid>("Grid", () => new Grid());
            registry.RegisterProperty<Grid, string>("Grid", "ColumnDefinitions",
                (grid, value) => grid.SetColumnDefinitions(ParseGridLengths(value)));
            registry.RegisterProperty<Grid, string>("Grid", "RowDefinitions",
                (grid, value) => grid.SetRowDefinitions(ParseGridLengths(value)));
            registry.Register<StackPanel>("StackPanel", () => new StackPanel());
            registry.RegisterProperty<StackPanel, StackOrientation>(
                "StackPanel", "Orientation", (panel, value) => panel.Orientation = value);
            registry.RegisterProperty<StackPanel, float>(
                "StackPanel", "Spacing", (panel, value) => panel.Spacing = value);
            registry.Register<Viewbox>("Viewbox", () => new Viewbox());
            registry.RegisterProperty<Viewbox, Stretch>(
                "Viewbox", "Stretch", (viewbox, value) => viewbox.Stretch = value);
            registry.RegisterProperty<Viewbox, StretchDirection>(
                "Viewbox", "StretchDirection", (viewbox, value) => viewbox.StretchDirection = value);
            registry.Register<LayoutTransformControl>("LayoutTransformControl", () => new LayoutTransformControl());
            registry.RegisterProperty<LayoutTransformControl, string>("LayoutTransformControl", "LayoutTransform",
                (control, value) => control.LayoutTransform = Transform.Parse(value));
            registry.Register<VirtualizingStackPanel>("VirtualizingStackPanel", () => new VirtualizingStackPanel());

            registry.Register<WrapPanel>("WrapPanel", () => new WrapPanel());
            registry.Register<UniformGrid>("UniformGrid", () => new UniformGrid());
            registry.Register<VirtualizingCarouselPanel>("VirtualizingCarouselPanel", () => new VirtualizingCarouselPanel());
            registry.RegisterProperty<UniformGrid, int>("UniformGrid", "Rows", (panel, value) => panel.Rows = value);
            registry.RegisterProperty<UniformGrid, int>("UniformGrid", "Columns", (panel, value) => panel.Columns = value);
            registry.RegisterProperty<UniformGrid, int>("UniformGrid", "FirstColumn", (panel, value) => panel.FirstColumn = value);
            registry.RegisterProperty<UniformGrid, float>("UniformGrid", "RowSpacing", (panel, value) => panel.RowSpacing = value);
            registry.RegisterProperty<UniformGrid, float>("UniformGrid", "ColumnSpacing", (panel, value) => panel.ColumnSpacing = value);
            registry.RegisterProperty<WrapPanel, Orientation>(
                "WrapPanel", "Orientation", (panel, value) => panel.Orientation = value);
            registry.RegisterProperty<WrapPanel, float>(
                "WrapPanel", "ItemWidth", (panel, value) => panel.ItemWidth = value);
            registry.RegisterProperty<WrapPanel, float>(
                "WrapPanel", "ItemHeight", (panel, value) => panel.ItemHeight = value);
            registry.RegisterProperty<WrapPanel, float>(
                "WrapPanel", "ItemSpacing", (panel, value) => panel.ItemSpacing = value);
            registry.RegisterProperty<WrapPanel, float>(
                "WrapPanel", "LineSpacing", (panel, value) => panel.LineSpacing = value);
            registry.RegisterProperty<WrapPanel, WrapPanelItemsAlignment>(
                "WrapPanel", "ItemsAlignment", (panel, value) => panel.ItemsAlignment = value);
            registry.Register<Border>("Border", () => new Border());
            registry.RegisterProperty<Border, BackgroundBrush>("Border", "Background",
                (border, value) => border.Background = value);
            registry.RegisterProperty<Border, Color>("Border", "BorderBrush",
                (border, value) => border.BorderBrush = value);
            registry.RegisterProperty<Border, Vector4>("Border", "BorderThickness",
                (border, value) => border.BorderThickness = value);
            registry.RegisterProperty<Border, Vector4>("Border", "Padding",
                (border, value) => border.Padding = value);
            registry.RegisterProperty<Border, Vector4>("Border", "CornerRadius",
                (border, value) => border.CornerRadius = value);
            registry.Register<TextBlock>("TextBlock", () => new TextBlock());
            registry.RegisterProperty<TextBlock, string>("TextBlock", "Text",
                (text, value) => text.Text = value);
            registry.RegisterProperty<TextBlock, float>("TextBlock", "FontSize",
                (text, value) => text.FontSize = value);
            registry.RegisterProperty<TextBlock, string>("TextBlock", "FontFamily",
                (text, value) => text.FontFamily = value);
            registry.RegisterProperty<TextBlock, Color>("TextBlock", "Foreground",
                (text, value) => text.Foreground = value);
            registry.RegisterProperty<TextBlock, HorizontalAlignment>(
                "TextBlock", "TextAlignment",
                (text, value) => text.TextAlignment = value);
            registry.RegisterProperty<TextBlock, VerticalAlignment>(
                "TextBlock", "VerticalTextAlignment",
                (text, value) => text.VerticalTextAlignment = value);
            registry.RegisterProperty<TextBlock, TextWrapping>(
                "TextBlock", "TextWrapping",
                (text, value) => text.TextWrapping = value);
            registry.RegisterProperty<TextBlock, TextTrimming>(
                "TextBlock", "TextTrimming",
                (text, value) => text.TextTrimming = value);

            registry.Register<TextPresenter>("TextPresenter", () => new TextPresenter());

            registry.Register<Acrux.Controls.Image>("Image", () => new Acrux.Controls.Image());
            registry.RegisterProperty<Acrux.Controls.Image, string>("Image", "Source",
                (image, value) => image.Source = value);
            registry.RegisterProperty<Acrux.Controls.Image, Stretch>("Image", "Stretch",
                (image, value) => image.Stretch = value);

            registry.Register<Button>("Button", () => new Button());
            registry.RegisterProperty<Button, ClickMode>("Button", "ClickMode",
                (button, value) => button.ClickMode = value);

            registry.Register<ToggleButton>("ToggleButton", () => new ToggleButton());
            registry.RegisterProperty<ToggleButton, bool?>("ToggleButton", "IsChecked",
                (button, value) => button.IsChecked = value);
            registry.RegisterProperty<ToggleButton, bool>("ToggleButton", "IsThreeState",
                (button, value) => button.IsThreeState = value);

            registry.Register<CheckBox>("CheckBox", () => new CheckBox());
            registry.RegisterProperty<CheckBox, bool?>("CheckBox", "IsChecked",
                (button, value) => button.IsChecked = value);
            registry.RegisterProperty<CheckBox, bool>("CheckBox", "IsThreeState",
                (button, value) => button.IsThreeState = value);

            registry.Register<PathShape>("Path", () => new PathShape());
            registry.RegisterProperty<PathShape, string>("Path", "Data",
                (path, value) => path.Data = Acrux.Media.Geometry.Parse(value));
            registry.RegisterProperty<PathShape, BackgroundBrush>("Path", "Stroke",
                (path, value) => path.Stroke = value);
            registry.RegisterProperty<PathShape, float>("Path", "StrokeThickness",
                (path, value) => path.StrokeThickness = value);
            registry.RegisterProperty<PathShape, Acrux.Controls.Shapes.PenLineCap>("Path", "StrokeLineCap",
                (path, value) => path.StrokeLineCap = value);

            registry.Register<DropDownButton>("DropDownButton", () => new DropDownButton());
            registry.Register<RepeatButton>("RepeatButton", () => new RepeatButton());
            registry.RegisterProperty<RepeatButton, int>("RepeatButton", "Delay",
                (button, value) => button.Delay = value);
            registry.RegisterProperty<RepeatButton, int>("RepeatButton", "Interval",
                (button, value) => button.Interval = value);
            registry.RegisterProperty<RepeatButton, ClickMode>("RepeatButton", "ClickMode",
                (button, value) => button.ClickMode = value);

            registry.Register<Slider>("Slider", () => new Slider());
            registry.RegisterProperty<Slider, float>("Slider", "Minimum",
                (slider, value) => slider.Minimum = value);
            registry.RegisterProperty<Slider, float>("Slider", "Maximum",
                (slider, value) => slider.Maximum = value);
            registry.RegisterProperty<Slider, float>("Slider", "Value",
                (slider, value) => slider.Value = value);
            registry.RegisterProperty<Slider, Orientation>("Slider", "Orientation",
                (slider, value) => slider.Orientation = value);

            registry.Register<TextBox>("TextBox", () => new TextBox());
            registry.RegisterProperty<TextBox, string>("TextBox", "Text",
                (textBox, value) => textBox.Text = value);
            registry.RegisterProperty<TextBox, string>("TextBox", "PlaceholderText",
                (textBox, value) => textBox.PlaceholderText = value);

            registry.Register<ProgressBar>("ProgressBar", () => new ProgressBar());
            registry.RegisterProperty<ProgressBar, float>("ProgressBar", "Minimum",
                (progress, value) => progress.Minimum = value);
            registry.RegisterProperty<ProgressBar, float>("ProgressBar", "Maximum",
                (progress, value) => progress.Maximum = value);
            registry.RegisterProperty<ProgressBar, float>("ProgressBar", "Value",
                (progress, value) => progress.Value = value);
            registry.RegisterProperty<ProgressBar, Orientation>("ProgressBar", "Orientation",
                (progress, value) => progress.Orientation = value);
            registry.RegisterProperty<ProgressBar, bool>("ProgressBar", "IsIndeterminate",
                (progress, value) => progress.IsIndeterminate = value);
            registry.RegisterProperty<ProgressBar, Vector4>("ProgressBar", "CornerRadius",
                (progress, value) => progress.CornerRadius = value);
            registry.RegisterProperty<ProgressBar, BackgroundBrush>("ProgressBar", "Background",
                (progress, value) => progress.Background = value);
            registry.RegisterProperty<ProgressBar, BackgroundBrush>("ProgressBar", "Foreground",
                (progress, value) => progress.Foreground = value);
            registry.Register<ItemsControl>("ItemsControl", () => new ItemsControl());
            registry.Register<ListBox>("ListBox", () => new ListBox());

            registry.Register<ScrollViewer>("ScrollViewer", () => new ScrollViewer());
            registry.RegisterProperty<ScrollViewer, ScrollBarVisibility>(
                "ScrollViewer", "HorizontalScrollBarVisibility",
                (viewer, value) => viewer.HorizontalScrollBarVisibility = value);
            registry.RegisterProperty<ScrollViewer, ScrollBarVisibility>(
                "ScrollViewer", "VerticalScrollBarVisibility",
                (viewer, value) => viewer.VerticalScrollBarVisibility = value);

            registry.Register<ScrollBar>("ScrollBar", () => new ScrollBar());
            registry.RegisterProperty<ScrollBar, float>("ScrollBar", "Minimum",
                (bar, value) => bar.Minimum = value);
            registry.RegisterProperty<ScrollBar, float>("ScrollBar", "Maximum",
                (bar, value) => bar.Maximum = value);
            registry.RegisterProperty<ScrollBar, float>("ScrollBar", "Value",
                (bar, value) => bar.Value = value);
            registry.RegisterProperty<ScrollBar, float>("ScrollBar", "ViewportSize",
                (bar, value) => bar.ViewportSize = value);
            registry.RegisterProperty<ScrollBar, Orientation>("ScrollBar", "Orientation",
                (bar, value) => bar.Orientation = value);
            registry.RegisterProperty<ScrollBar, bool>("ScrollBar", "AllowAutoHide",
                (bar, value) => bar.AllowAutoHide = value);
            registry.RegisterProperty<ScrollBar, float>("ScrollBar", "SmallChange",
                (bar, value) => bar.SmallChange = value);
            registry.RegisterProperty<ScrollBar, float>("ScrollBar", "LargeChange",
                (bar, value) => bar.LargeChange = value);

            registry.Register<Track>("Track", () => new Track());
            registry.RegisterProperty<Track, float>("Track", "Minimum",
                (track, value) => track.Minimum = value);
            registry.RegisterProperty<Track, float>("Track", "Maximum",
                (track, value) => track.Maximum = value);
            registry.RegisterProperty<Track, float>("Track", "Value",
                (track, value) => track.Value = value);
            registry.RegisterProperty<Track, float>("Track", "ViewportSize",
                (track, value) => track.ViewportSize = value);
            registry.RegisterProperty<Track, Orientation>("Track", "Orientation",
                (track, value) => track.Orientation = value);

            registry.Register<Thumb>("Thumb", () => new Thumb());
            registry.RegisterProperty<Thumb, BackgroundBrush>("Thumb", "Background",
                (thumb, value) => thumb.Background = value);

            registry.Register<ContentPresenter>("ContentPresenter", () => new ContentPresenter());
            registry.Register<ScrollViewer.ScrollContentPresenter>(
                "ScrollContentPresenter", () => new ScrollViewer.ScrollContentPresenter());
            return registry;
        }

        static float[] ParseSegments(string value)
        {
            string[] parts = (value ?? string.Empty).Split(
                new[] { ',', ' ', '\t', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return new[] { 1f };
            var segments = new float[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                segments[i] = float.Parse(parts[i], CultureInfo.InvariantCulture);
            return segments;
        }

        static GridLength[] ParseGridLengths(string value)
        {
            string[] parts = (value ?? string.Empty).Split(
                new[] { ',', ' ', '\t', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return new[] { GridLength.Star };
            var definitions = new GridLength[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                if (string.Equals(part, "Auto", StringComparison.OrdinalIgnoreCase))
                {
                    definitions[i] = GridLength.Auto;
                    continue;
                }
                if (part.EndsWith("*", StringComparison.Ordinal))
                {
                    string weight = part.Substring(0, part.Length - 1);
                    definitions[i] = new GridLength(weight.Length == 0
                        ? 1f
                        : float.Parse(weight, CultureInfo.InvariantCulture), GridUnitType.Star);
                    continue;
                }
                definitions[i] = new GridLength(
                    float.Parse(part, CultureInfo.InvariantCulture));
            }
            return definitions;
        }
    }

    /// <summary>Strongly typed code-template compatibility wrapper.</summary>
    public sealed class ControlTemplate<TControl> where TControl : TemplatedControl
    {
        readonly Func<TControl, VisualElement> _factory;

        public ControlTemplate(Func<TControl, VisualElement> factory)
        {
            if (factory == null)
                throw new ArgumentNullException(nameof(factory));
            _factory = factory;
        }

        public VisualElement Build(TControl owner)
        {
            if (owner == null)
                throw new ArgumentNullException(nameof(owner));
            return _factory(owner);
        }

        public ControlTemplate ToUntyped()
        {
            return new ControlTemplate(owner => _factory((TControl)owner));
        }
    }
}
