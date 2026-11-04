using System;
using System.Globalization;
using Acrux.Data.Converters;

namespace Acrux.Markup
{
    public sealed class BindingExtension
    {
        public BindingExtension(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("A binding requires a property path.", nameof(path));
            Path = path.Trim();
        }

        public string Path { get; }
        public string StringFormat { get; set; }
        public IValueConverter Converter { get; set; }
        public object ConverterParameter { get; set; }
        public CultureInfo ConverterCulture { get; set; }
        internal string ConverterResourceKey { get; set; }

        public static bool TryParse(string value, out BindingExtension binding)
        {
            binding = null;
            BindingMarkup parsed;
            if (!BindingMarkup.TryParse(value, "Binding", out parsed)) return false;
            // TwoWay is currently supported by compiled views, whose generated
            // setters can update the source without sandbox-prohibited reflection.
            if (parsed.Mode == "TwoWay") return false;
            binding = new BindingExtension(parsed.Path)
            {
                ConverterResourceKey = parsed.ConverterKey,
                ConverterParameter = parsed.Parameter,
                StringFormat = parsed.StringFormat,
                ConverterCulture = parsed.Culture == null ? null : CultureInfo.GetCultureInfo(parsed.Culture)
            };
            return true;
        }
    }

    public sealed class TemplateBindingExtension
    {
        public TemplateBindingExtension(string property)
        {
            if (string.IsNullOrWhiteSpace(property))
                throw new ArgumentException(
                    "A template binding requires a property name.", nameof(property));
            Property = property.Trim();
        }

        public string Property { get; }
        public IValueConverter Converter { get; set; }
        public object ConverterParameter { get; set; }
        public CultureInfo ConverterCulture { get; set; }
        internal string ConverterResourceKey { get; set; }

        internal object ConvertValue(Acrux.Core.Control owner, object value, Type targetType)
        {
            IValueConverter converter = Converter;
            if (converter == null && ConverterResourceKey != null)
                Converter = converter = owner.ResolveResource<IValueConverter>(ConverterResourceKey);
            return converter == null ? value : converter.Convert(value, targetType,
                ConverterParameter, ConverterCulture ?? CultureInfo.CurrentCulture);
        }

        public static bool TryParse(string value, out TemplateBindingExtension binding)
        {
            binding = null;
            BindingMarkup parsed;
            if (!BindingMarkup.TryParse(value, "TemplateBinding", out parsed)) return false;
            binding = new TemplateBindingExtension(parsed.Path)
            {
                ConverterResourceKey = parsed.ConverterKey,
                ConverterParameter = parsed.Parameter,
                ConverterCulture = parsed.Culture == null ? null : CultureInfo.GetCultureInfo(parsed.Culture)
            };
            return true;
        }
    }
}
