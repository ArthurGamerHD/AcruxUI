using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using VRageMath;

namespace AcruxPreview
{
    /// <summary>
    /// The previewed view's design properties: the public static settable properties (string, bool, int, float,
    /// double, enum or Color) of the classes its design data comes from (the type of each {x:Static} Design.DataContext
    /// member). The preview plugin edits them; before the view's design data is read again, every one is put back to
    /// the value it had when its class was first seen in this build of the mod, then the plugin's values are applied.
    /// </summary>
    public static class DesignProperties
    {
        static readonly Dictionary<string, object> Defaults = new Dictionary<string, object>(StringComparer.Ordinal);
        static Assembly _defaultsAssembly;
        static Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>The plugin's values, by "Type.Property" (replaces the previous set).</summary>
        public static void SetValues(string[] names, string[] values)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (names != null && values != null)
                for (int i = 0; i < names.Length && i < values.Length; i++)
                    if (!string.IsNullOrEmpty(names[i]))
                        map[names[i]] = values[i];
            _values = map;
        }

        /// <summary>Puts a design data class's properties to their defaults, then to the plugin's values.</summary>
        public static void Apply(Type type)
        {
            if (type.Assembly != _defaultsAssembly)
            {
                // A rebuilt mod: its classes start from their own defaults.
                Defaults.Clear();
                _defaultsAssembly = type.Assembly;
            }
            foreach (PropertyInfo property in Editable(type))
            {
                string key = Key(property);
                object current;
                if (!Defaults.TryGetValue(key, out current))
                    Defaults[key] = current = Read(property);
                object value = current;
                string text;
                object parsed;
                if (_values.TryGetValue(key, out text) && Parse(text, property.PropertyType, out parsed))
                    value = parsed;
                try
                {
                    property.SetValue(null, value);
                }
                catch (Exception error)
                {
                    Program.Log("design property " + key + " not set: " + ((error as TargetInvocationException)?.InnerException ?? error).Message);
                }
            }
        }

        /// <summary>The plugin's description of the design properties of <paramref name="types"/>.</summary>
        public static PreviewDesignPropertiesMessage Describe(IEnumerable<Type> types)
        {
            var names = new List<string>();
            var kinds = new List<string>();
            var values = new List<string>();
            var options = new List<string>();
            foreach (Type type in types.Distinct())
                foreach (PropertyInfo property in Editable(type))
                {
                    Type valueType = property.PropertyType;
                    names.Add(Key(property));
                    kinds.Add(Kind(valueType));
                    values.Add(Format(Read(property)));
                    options.Add(valueType.IsEnum ? string.Join("\n", Enum.GetNames(valueType)) : string.Empty);
                }
            return new PreviewDesignPropertiesMessage
            {
                Names = names.ToArray(), Kinds = kinds.ToArray(), Values = values.ToArray(), Options = options.ToArray(),
            };
        }

        static IEnumerable<PropertyInfo> Editable(Type type)
        {
            return type.GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0 && p.GetSetMethod() != null &&
                            Kind(p.PropertyType) != null)
                .OrderBy(p => p.MetadataToken);
        }

        static string Key(PropertyInfo property)
        {
            return property.DeclaringType.Name + "." + property.Name;
        }

        static string Kind(Type type)
        {
            if (type == typeof(string)) return "string";
            if (type == typeof(bool)) return "bool";
            if (type == typeof(int) || type == typeof(long)) return "int";
            if (type == typeof(float) || type == typeof(double)) return "float";
            if (type.IsEnum) return "enum";
            if (type == typeof(Color)) return "color";
            return null;
        }

        static object Read(PropertyInfo property)
        {
            try
            {
                return property.GetValue(null);
            }
            catch (Exception)
            {
                return null;
            }
        }

        static bool Parse(string text, Type type, out object value)
        {
            if (type == typeof(long))
            {
                long number;
                bool ok = long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out number);
                value = number;
                return ok;
            }
            return ModClient.TryConvert(text ?? string.Empty, type, out value);
        }

        static string Format(object value)
        {
            if (value == null)
                return string.Empty;
            if (value is Color)
            {
                var color = (Color)value;
                return color.A == 255
                    ? string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", color.R, color.G, color.B)
                    : string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}{3:X2}", color.A, color.R, color.G, color.B);
            }
            if (value is bool)
                return (bool)value ? "true" : "false";
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }
    }
}
