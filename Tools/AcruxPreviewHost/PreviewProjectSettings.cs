using System;
using System.Collections.Generic;
using System.IO;

namespace AcruxPreview
{
    /// <summary>
    /// The previewed project's obj/acrux-preview.properties (written by AcruxPreview.props on each build): the project's
    /// preview options, such as its design-time resource provider ("designResources").
    /// </summary>
    public static class PreviewProjectSettings
    {
        static string _path;
        static DateTime _stamp;
        static Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.Ordinal);

        public static string Get(string assemblyPath, string key)
        {
            string project = string.IsNullOrEmpty(assemblyPath) ? null : ModClient.FindProjectDirectory(assemblyPath);
            string path = project == null ? null : Path.Combine(project, "obj", "acrux-preview.properties");
            if (path == null || !File.Exists(path))
                return null;
            DateTime stamp = File.GetLastWriteTimeUtc(path);
            if (path != _path || stamp != _stamp)
            {
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (string line in File.ReadAllLines(path))
                {
                    int equals = line.IndexOf('=');
                    if (line.StartsWith("#", StringComparison.Ordinal) || equals <= 0)
                        continue;
                    values[line.Substring(0, equals).Trim()] = line.Substring(equals + 1).Trim();
                }
                _values = values;
                _path = path;
                _stamp = stamp;
            }
            string value;
            return _values.TryGetValue(key, out value) && value.Length > 0 ? value : null;
        }
    }
}
