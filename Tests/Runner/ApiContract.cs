using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

/// <summary>
/// The bridge contract released clients were compiled against: every delegate-table key with its delegate type, the
/// handshake constants, and every enum value that crosses the bridge as an integer. Published mods keep their own
/// copy of ClientApi forever, so a toolkit update must keep serving everything in ClientApi/api.shipped.txt.
/// </summary>
static class ApiContract
{
    public const string ShippedFile = "api.shipped.txt";

    static readonly Regex Member = new Regex("(?:Get|Api)<(.+?)>\\((?:api, )?\"([^\"]+)\"\\)", RegexOptions.Compiled);
    static readonly Regex RawMember = new Regex("TryGetValue\\(\"([^\"]+)\"", RegexOptions.Compiled);
    static readonly Regex Constant = new Regex("public const (?:int|long) (RequestChannel|BroadcastChannel|ProtocolVersion) = ([0-9A-Fa-fxL]+);", RegexOptions.Compiled);
    static readonly Regex Namespace = new Regex("^\\s*namespace ([\\w.]+)", RegexOptions.Compiled | RegexOptions.Multiline);
    static readonly Regex EnumDecl = new Regex("\\benum (\\w+)(?:\\s*:\\s*(\\w+))?\\s*\\{([^}]*)\\}", RegexOptions.Compiled);
    static readonly Regex Comments = new Regex("//[^\\n]*|/\\*.*?\\*/", RegexOptions.Compiled | RegexOptions.Singleline);
    static readonly Regex Attributes = new Regex("\\[[^\\]]*\\]", RegexOptions.Compiled);

    public static SortedSet<string> Collect(string clientApiDir)
    {
        var lines = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string file in Directory.GetFiles(clientApiDir, "*.cs", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            string text = File.ReadAllText(file);
            foreach (Match m in Member.Matches(text))
            {
                if (m.Groups[1].Value != "T")
                    lines.Add("member " + m.Groups[2].Value + " : " + m.Groups[1].Value.Replace("global::", ""));
            }
            foreach (Match m in RawMember.Matches(text))
                lines.Add("member " + m.Groups[1].Value + " : (raw)");
            foreach (Match m in Constant.Matches(text))
                lines.Add("handshake " + m.Groups[1].Value + " = " + m.Groups[2].Value.TrimEnd('L'));
            CollectEnums(text, lines);
        }
        return lines;
    }

    static void CollectEnums(string text, SortedSet<string> lines)
    {
        string code = Comments.Replace(text, "");
        Match ns = Namespace.Match(code);
        string prefix = ns.Success ? ns.Groups[1].Value + "." : "";
        foreach (Match e in EnumDecl.Matches(code))
        {
            string name = prefix + e.Groups[1].Value;
            lines.Add("enum " + name + " : " + (e.Groups[2].Success ? e.Groups[2].Value : "int"));
            var values = new Dictionary<string, long>(StringComparer.Ordinal);
            long next = 0;
            foreach (string raw in Attributes.Replace(e.Groups[3].Value, "").Split(','))
            {
                string entry = raw.Trim();
                if (entry.Length == 0)
                    continue;
                string member = entry;
                long value = next;
                int eq = entry.IndexOf('=');
                if (eq >= 0)
                {
                    member = entry.Substring(0, eq).Trim();
                    value = ParseValue(entry.Substring(eq + 1).Trim(), values, name);
                }
                values[member] = value;
                next = value + 1;
                lines.Add("enum " + name + "." + member + " = " + value.ToString(CultureInfo.InvariantCulture));
            }
        }
    }

    static long ParseValue(string expr, Dictionary<string, long> known, string enumName)
    {
        long value;
        if (expr.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(expr.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
            return value;
        if (long.TryParse(expr, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            return value;
        if (known.TryGetValue(expr, out value))
            return value;
        Match shift = Regex.Match(expr, "^(\\d+)\\s*<<\\s*(\\d+)$");
        if (shift.Success)
            return long.Parse(shift.Groups[1].Value, CultureInfo.InvariantCulture) << int.Parse(shift.Groups[2].Value, CultureInfo.InvariantCulture);
        throw new FormatException("ApiContract cannot evaluate '" + expr + "' in enum " + enumName + "; extend ParseValue.");
    }

    /// <summary>Returns the shipped lines the current ClientApi no longer provides; prints the additions.</summary>
    public static List<string> Compare(string clientApiDir, bool accept)
    {
        string shippedPath = Path.Combine(clientApiDir, ShippedFile);
        SortedSet<string> current = Collect(clientApiDir);
        if (accept || !File.Exists(shippedPath))
        {
            File.WriteAllLines(shippedPath, current);
            Console.WriteLine("api contract: wrote " + current.Count + " lines to " + shippedPath);
            return new List<string>();
        }
        var shipped = new HashSet<string>(File.ReadAllLines(shippedPath).Where(l => l.Length > 0 && !l.StartsWith("#")), StringComparer.Ordinal);
        var broken = shipped.Where(l => !current.Contains(l)).OrderBy(l => l, StringComparer.Ordinal).ToList();
        var added = current.Where(l => !shipped.Contains(l)).ToList();
        Console.WriteLine("api contract: " + shipped.Count + " shipped, " + added.Count + " added since the last release");
        foreach (string line in added.Take(20))
            Console.WriteLine("    + " + line);
        if (added.Count > 20)
            Console.WriteLine("    + ... " + (added.Count - 20) + " more");
        foreach (string line in broken.Take(40))
            Console.WriteLine("    - " + line);
        return broken;
    }
}
