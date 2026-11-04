using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Acrux.Generator;

/// <summary>
/// Emits the debug-only tables a generated view gives Acrux.Markup.HotReload: factories and setters for
/// every element type the view uses, and accessors for every property of its x:DataType types.
/// </summary>
static class HotReloadTables
{
    static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.FullyQualifiedFormat;

    public static void Emit(
        StringBuilder builder,
        IReadOnlyList<INamedTypeSymbol> elementTypes,
        IReadOnlyDictionary<string, string> aliases,
        IReadOnlyDictionary<string, ITypeSymbol> dataTypes,
        Compilation compilation)
    {
        foreach (INamedTypeSymbol type in elementTypes)
        {
            string typeName = type.ToDisplayString(TypeFormat);
            string name = Literal(MetadataName(type));
            builder.Append("            context.Type<").Append(typeName).Append(">(").Append(name).Append(", ");
            builder.Append(CanCreate(type, compilation) ? "() => new " + typeName + "()" : "null").AppendLine(");");
            foreach (IPropertySymbol property in Properties(type, compilation, writable: true))
                builder.Append("            context.Property<").Append(typeName).Append(", ")
                    .Append(property.Type.ToDisplayString(TypeFormat)).Append(">(").Append(name).Append(", ")
                    .Append(Literal(property.Name)).Append(", (control, value) => control.")
                    .Append(Escape(property.Name)).AppendLine(" = value);");
        }

        foreach (KeyValuePair<string, string> alias in aliases.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            builder.Append("            context.Alias(").Append(Literal(alias.Key)).Append(", ")
                .Append(Literal(alias.Value)).AppendLine(");");

        foreach (KeyValuePair<string, ITypeSymbol> dataType in dataTypes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var type = dataType.Value as INamedTypeSymbol;
            if (type == null)
                continue;
            string typeName = type.ToDisplayString(TypeFormat);
            string key = Literal(dataType.Key);
            foreach (IPropertySymbol property in Properties(type, compilation, writable: false))
                builder.Append("            context.Getter(").Append(key).Append(", ").Append(Literal(property.Name))
                    .Append(", source => ((").Append(typeName).Append(")source).").Append(Escape(property.Name))
                    .AppendLine(");");
            // And the properties ADK's [ObservableProperty] generates from fields (not visible to this generator).
            foreach (ViewModelMember member in ViewModelMember.All(type, compilation, writable: false).Where(m => IsAccessible(m.Type, compilation)))
                builder.Append("            context.Getter(").Append(key).Append(", ").Append(Literal(member.Name))
                    .Append(", source => ((").Append(typeName).Append(")source).").Append(Escape(member.Name))
                    .AppendLine(");");
            foreach (IPropertySymbol property in Properties(type, compilation, writable: true))
                AppendSetter(builder, key, typeName, property.Name, property.Type);
            foreach (ViewModelMember member in ViewModelMember.All(type, compilation, writable: true).Where(m => IsAccessible(m.Type, compilation)))
                AppendSetter(builder, key, typeName, member.Name, member.Type);
        }
    }

    static void AppendSetter(StringBuilder builder, string key, string typeName, string name, ITypeSymbol type)
    {
        string valueType = type.ToDisplayString(TypeFormat);
        var nullable = type as INamedTypeSymbol;
        string test = nullable != null && nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
            ? "value == null || value is " + nullable.TypeArguments[0].ToDisplayString(TypeFormat)
            : type.IsReferenceType
                ? "value == null || value is " + valueType
                : "value is " + valueType;
        builder.Append("            context.Setter(").Append(key).Append(", ").Append(Literal(name))
            .Append(", (source, value) => { if (").Append(test).Append(") ((")
            .Append(typeName).Append(")source).").Append(Escape(name)).Append(" = (")
            .Append(valueType).AppendLine(")value; });");
    }

    public static string MetadataName(INamedTypeSymbol type)
    {
        return type.ContainingNamespace.IsGlobalNamespace
            ? type.Name
            : type.ContainingNamespace.ToDisplayString() + "." + type.Name;
    }

    static IEnumerable<IPropertySymbol> Properties(INamedTypeSymbol type, Compilation compilation, bool writable)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (INamedTypeSymbol current = type; current != null; current = current.BaseType)
        {
            foreach (IPropertySymbol property in current.GetMembers().OfType<IPropertySymbol>())
            {
                if (property.IsStatic || property.IsIndexer || property.ReturnsByRef || property.ReturnsByRefReadonly ||
                    property.DeclaredAccessibility != Accessibility.Public || !seen.Add(property.Name))
                    continue;
                IMethodSymbol accessor = writable ? FindSetter(property) : FindGetter(property);
                if (accessor == null || accessor.IsInitOnly ||
                    accessor.DeclaredAccessibility != Accessibility.Public && accessor.DeclaredAccessibility != Accessibility.NotApplicable ||
                    !compilation.IsSymbolAccessibleWithin(accessor, compilation.Assembly) ||
                    !IsAccessible(property.Type, compilation) ||
                    property.GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString() == "System.ObsoleteAttribute"))
                    continue;
                yield return property;
            }
        }
    }

    static IMethodSymbol FindSetter(IPropertySymbol property)
    {
        for (IPropertySymbol current = property; current != null; current = current.OverriddenProperty)
            if (current.SetMethod != null)
                return current.SetMethod;
        return null;
    }

    static IMethodSymbol FindGetter(IPropertySymbol property)
    {
        for (IPropertySymbol current = property; current != null; current = current.OverriddenProperty)
            if (current.GetMethod != null)
                return current.GetMethod;
        return null;
    }

    static bool CanCreate(INamedTypeSymbol type, Compilation compilation)
    {
        return !type.IsAbstract && type.InstanceConstructors.Any(constructor =>
            constructor.Parameters.Length == 0 &&
            compilation.IsSymbolAccessibleWithin(constructor, compilation.Assembly));
    }

    static bool IsAccessible(ITypeSymbol type, Compilation compilation)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
                return IsAccessible(array.ElementType, compilation);
            case INamedTypeSymbol named:
                if (named.IsRefLikeType || !compilation.IsSymbolAccessibleWithin(named, compilation.Assembly))
                    return false;
                return named.TypeArguments.All(argument => IsAccessible(argument, compilation));
            default:
                return false;
        }
    }

    static string Literal(string value)
    {
        return SymbolDisplay.FormatLiteral(value, true);
    }

    static string Escape(string name)
    {
        return SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;
    }
}
