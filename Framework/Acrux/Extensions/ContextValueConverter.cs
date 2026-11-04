using System;
using System.Globalization;
using Acrux.Core;
using Acrux.Data.Converters;

namespace Acrux.Extensions
{
    /// <summary>
    /// Acrux extension of IValueConverter that also receives the binding target, so a converter can
    /// read inherited resources or ancestor state (ConverterParameter cannot be bound).
    /// </summary>
    public interface IContextValueConverter : IValueConverter
    {
        object Convert(object value, Type targetType, object parameter, CultureInfo culture, Control target);
    }

    public static class ValueConverters
    {
        // Counts contextual conversions, so bindings can tell they depend on their target's ancestry.
        internal static int ContextualConversions;

        /// <summary>
        /// Resolves a converter resource, or returns null while the target cannot reach it yet (a template built
        /// before it is attached); the binding is then refreshed when the target is attached.
        /// </summary>
        public static IValueConverter Resolve(Control target, string key)
        {
            IValueConverter converter;
            return target.TryFindResource(key, out converter) ? converter : null;
        }

        public static object Convert(IValueConverter converter, object value, Type targetType, object parameter,
            CultureInfo culture, Control target)
        {
            if (converter == null)
            {
                ContextualConversions++;
                return Acrux.Data.BindingOperations.DoNothing;
            }
            var contextual = converter as IContextValueConverter;
            if (contextual != null)
                ContextualConversions++;
            return contextual != null
                ? contextual.Convert(value, targetType, parameter, culture, target)
                : converter.Convert(value, targetType, parameter, culture);
        }
    }
}
