using System;
using System.Globalization;
using Acrux.Styling;
using VRageMath;

namespace Acrux.Markup
{
    // As Avalonia: GradientStops is the brush's content property, so stops are direct children or inside
    // <Brush.GradientStops>, optionally wrapped in <GradientStops>.
    internal static class GradientBrushMarkup
    {
        internal static bool IsGradientBrush(string name)
        {
            return name == "LinearGradientBrush" || name == "RadialGradientBrush" || name == "ConicGradientBrush";
        }

        internal static GradientBrush Build(UiXmlLoader.Node node)
        {
            GradientBrush brush;
            switch (node.Name)
            {
                case "LinearGradientBrush": brush = new LinearGradientBrush(); break;
                case "RadialGradientBrush": brush = new RadialGradientBrush(); break;
                case "ConicGradientBrush": brush = new ConicGradientBrush(); break;
                default: throw new FormatException("'" + node.Name + "' is not a gradient brush.");
            }

            foreach (UiXmlLoader.Attribute attribute in node.Attributes)
            {
                if (attribute.Name.StartsWith("xmlns", StringComparison.Ordinal) || attribute.Name.StartsWith("x:", StringComparison.Ordinal))
                    continue;
                if (!TrySet(brush, attribute.Name, attribute.Value.Trim()))
                    throw new FormatException("Unknown property '" + node.Name + "." + attribute.Name + "'.");
            }

            foreach (UiXmlLoader.Node child in node.Children)
                AddStops(brush, node.Name, child);
            return brush;
        }

        static void AddStops(GradientBrush brush, string brushName, UiXmlLoader.Node node)
        {
            if (node.Name == "GradientStop")
            {
                brush.GradientStops.Add(BuildStop(node));
                return;
            }
            if (node.Name == "GradientStops" || node.Name == brushName + ".GradientStops")
            {
                foreach (UiXmlLoader.Node child in node.Children)
                    AddStops(brush, brushName, child);
                return;
            }
            throw new FormatException("Unexpected '" + node.Name + "' in " + brushName + "; expected GradientStop elements.");
        }

        static GradientStop BuildStop(UiXmlLoader.Node node)
        {
            var stop = new GradientStop();
            foreach (UiXmlLoader.Attribute attribute in node.Attributes)
            {
                if (attribute.Name == "Color")
                    stop.Color = (Color)new PropertyConverter().Convert(attribute.Value.Trim(), typeof(Color));
                else if (attribute.Name == "Offset")
                    stop.Offset = ParseFloat(attribute.Value);
                else
                    throw new FormatException("Unknown property 'GradientStop." + attribute.Name + "'.");
            }
            return stop;
        }

        static bool TrySet(GradientBrush brush, string name, string value)
        {
            switch (name)
            {
                case "SpreadMethod":
                    brush.SpreadMethod = (GradientSpreadMethod)Enum.Parse(typeof(GradientSpreadMethod), value, true);
                    return true;
                case "Opacity":
                    brush.Opacity = ParseFloat(value);
                    return true;
                case "Transform":
                    brush.Transform = Transform.Parse(value);
                    return true;
                case "TransformOrigin":
                    brush.TransformOrigin = RelativePoint.Parse(value);
                    return true;
            }

            var linear = brush as LinearGradientBrush;
            if (linear != null)
            {
                if (name == "StartPoint") { linear.StartPoint = RelativePoint.Parse(value); return true; }
                if (name == "EndPoint") { linear.EndPoint = RelativePoint.Parse(value); return true; }
                return false;
            }
            var radial = brush as RadialGradientBrush;
            if (radial != null)
            {
                switch (name)
                {
                    case "Center": radial.Center = RelativePoint.Parse(value); return true;
                    case "GradientOrigin": radial.GradientOrigin = RelativePoint.Parse(value); return true;
                    case "RadiusX": radial.RadiusX = RelativeScalar.Parse(value); return true;
                    case "RadiusY": radial.RadiusY = RelativeScalar.Parse(value); return true;
                }
                return false;
            }
            var conic = brush as ConicGradientBrush;
            if (conic != null)
            {
                if (name == "Center") { conic.Center = RelativePoint.Parse(value); return true; }
                if (name == "Angle") { conic.Angle = ParseFloat(value); return true; }
            }
            return false;
        }

        static float ParseFloat(string value)
        {
            return float.Parse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
        }
    }
}
