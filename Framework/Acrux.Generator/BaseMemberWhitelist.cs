using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Acrux.Generator;

/// <summary>
/// Decides which Acrux types and members are whitelisted as base members (see <see cref="BaseMembers"/>).
/// Acrux types in Acrux.Extensions and Acrux.Addons are explicit extensions and always allowed.
///
/// This list is to prevent addition of Mod-Specific Controls as base members of Acrux types,
/// Specially when autonomous AI agents may want to add then
///
/// Compiled based on Avalonia UI https://github.com/AvaloniaUI/Avalonia and WinUI https://github.com/microsoft/microsoft-ui-xaml
/// </summary>
internal static class BaseMemberWhitelist
{
    const string AcruxNamespace = "Acrux";
    static readonly string[] ExtensionNamespaces = { "Acrux.Extensions", "Acrux.Addons" };

    static readonly Dictionary<string, HashSet<string>> Members;
    static readonly Dictionary<string, HashSet<string>> Attached;

    static BaseMemberWhitelist()
    {
        var bases = new Dictionary<string, string>(StringComparer.Ordinal);
        var own = new Dictionary<string, string[]>(StringComparer.Ordinal);
        Attached = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (string row in BaseMembers.Table)
        {
            string[] parts = row.Split('|');
            bases[parts[0]] = parts[1];
            own[parts[0]] = Split(parts[2]);
            Attached[parts[0]] = new HashSet<string>(Split(parts[3]), StringComparer.Ordinal);
        }

        Members = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (string name in own.Keys)
        {
            var members = new HashSet<string>(StringComparer.Ordinal);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            for (string current = name; current.Length > 0 && own.ContainsKey(current) && visited.Add(current);
                 current = bases[current])
                members.UnionWith(own[current]);
            Members[name] = members;
        }
    }

    public static bool IsAcruxType(INamedTypeSymbol type)
    {
        string ns = type.ContainingNamespace.ToDisplayString();
        return ns == AcruxNamespace || ns.StartsWith(AcruxNamespace + ".", StringComparison.Ordinal);
    }

    public static bool IsExtension(INamedTypeSymbol type)
    {
        string ns = type.ContainingNamespace.ToDisplayString();
        return ExtensionNamespaces.Any(extension =>
            ns == extension || ns.StartsWith(extension + ".", StringComparison.Ordinal));
    }

    public static bool IsBaseType(INamedTypeSymbol type)
    {
        return Members.ContainsKey(type.Name);
    }

    public static INamedTypeSymbol NearestAcruxType(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol current = type; current != null; current = current.BaseType)
            if (IsAcruxType(current))
                return current;
        return null;
    }

    // True when an Acrux type itself is not whitelisted (and not an extension).
    public static bool IsUnsupportedType(INamedTypeSymbol type)
    {
        return IsAcruxType(type) && !IsExtension(type) && !IsBaseType(type);
    }

    // True when member may be used on type: app types may use what they declare themselves; anything provided by Acrux
    // must be a whitelisted base member.
    public static bool IsAllowedMember(INamedTypeSymbol type, string member)
    {
        for (INamedTypeSymbol current = type; current != null; current = current.BaseType)
        {
            if (IsAcruxType(current))
                break;
            if (current.GetMembers(member).Any(symbol => !symbol.IsStatic))
                return true;
        }

        INamedTypeSymbol acruxType = NearestAcruxType(type);
        if (acruxType == null || IsExtension(acruxType))
            return true;
        HashSet<string> members;
        return !Members.TryGetValue(acruxType.Name, out members) || members.Contains(member);
    }

    public static bool IsAllowedAttached(INamedTypeSymbol owner, string member)
    {
        if (!IsAcruxType(owner) || IsExtension(owner))
            return true;
        HashSet<string> attached;
        return Attached.TryGetValue(owner.Name, out attached) && attached.Contains(member);
    }

    static string[] Split(string value)
    {
        return value.Length == 0 ? [] : value.Split(',');
    }
}
