using System;
using System.Globalization;

namespace Acrux.Data.Converters
{
    public interface IValueConverter
    {
        object Convert(object value, Type targetType, object parameter, CultureInfo culture);
        object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture);
    }
}

namespace Acrux.Data
{
    public static class BindingOperations
    {
        public static readonly object DoNothing = new object();
    }
}
