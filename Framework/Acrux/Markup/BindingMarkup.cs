using System;
using System.Collections.Generic;

namespace Acrux.Markup
{
    // Shared by the runtime template loader and compiled view generator.
    internal sealed class BindingMarkup
    {
        public string Path;
        public string ConverterKey;
        public string Parameter;
        public string Culture;
        public string Mode;
        public string StringFormat;

        public static bool TryParse(string text, string extension, out BindingMarkup result)
        {
            result = null;
            if (text == null) return false;
            text = text.Trim();
            string prefix = "{" + extension + " ";
            if (!text.StartsWith(prefix, StringComparison.Ordinal) || !text.EndsWith("}", StringComparison.Ordinal))
                return false;
            string body = text.Substring(prefix.Length, text.Length - prefix.Length - 1);
            var parts = new List<string>();
            int start = 0, depth = 0;
            char quote = '\0';
            for (int i = 0; i < body.Length; i++)
            {
                char c = body[i];
                if (c == '\\' && i + 1 < body.Length) { i++; continue; }
                if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
                if (c == '\'' || c == '"') quote = c;
                else if (c == '{') depth++;
                else if (c == '}') { if (--depth < 0) return false; }
                else if (c == ',' && depth == 0) { parts.Add(body.Substring(start, i - start).Trim()); start = i + 1; }
            }
            if (depth != 0 || quote != '\0') return false;
            parts.Add(body.Substring(start).Trim());
            var binding = new BindingMarkup();
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < parts.Count; i++)
            {
                string part = parts[i];
                int equals = part.IndexOf('=');
                string name = equals < 0 ? (extension == "Binding" ? "Path" : "Property") : part.Substring(0, equals).Trim();
                string value = equals < 0 ? part : part.Substring(equals + 1).Trim();
                if (value.Length == 0 || !names.Add(name) || (equals < 0 && i != 0)) return false;
                if (value.Length >= 2 && (value[0] == '\'' || value[0] == '"') && value[0] == value[value.Length - 1])
                    value = value.Substring(1, value.Length - 2);
                if (name == (extension == "Binding" ? "Path" : "Property")) binding.Path = value;
                else if (name == "Converter")
                {
                    const string resource = "{StaticResource ";
                    if (!value.StartsWith(resource, StringComparison.Ordinal) || !value.EndsWith("}", StringComparison.Ordinal)) return false;
                    binding.ConverterKey = value.Substring(resource.Length, value.Length - resource.Length - 1).Trim();
                    if (binding.ConverterKey.Length == 0) return false;
                }
                else if (name == "ConverterParameter") binding.Parameter = value;
                else if (name == "ConverterCulture") binding.Culture = value;
                else if (name == "StringFormat" && extension == "Binding")
                    binding.StringFormat = Unescape(value);
                else if (name == "Mode" && extension == "Binding" && (value == "OneWay" || value == "TwoWay")) binding.Mode = value;
                else return false;
            }
            if (string.IsNullOrWhiteSpace(binding.Path)) return false;
            result = binding;
            return true;
        }

        static string Unescape(string value)
        {
            var result = new System.Text.StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] == '\\' && i + 1 < value.Length) i++;
                result.Append(value[i]);
            }
            return result.ToString();
        }
    }
}
