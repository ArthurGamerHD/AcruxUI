using System.Collections.Generic;
using System.Linq;
using Adk.Analyzer;
using Microsoft.CodeAnalysis;

namespace Acrux.Generator;

/// <summary>
/// A member of a view model that a view binds: a property the type declares, or one ADK's
/// <c>[ObservableProperty]</c> generates from a field. Source generators never see each other's output, so the
/// generated properties are known from their fields, by the rules ADK's generator follows
/// (<see cref="ObservablePropertyConventions"/>): public, read and write, named after the field.
/// </summary>
sealed class ViewModelMember
{
    ViewModelMember(string name, ITypeSymbol type, bool readableByView, bool writableByView, bool canWrite)
    {
        Name = name;
        Type = type;
        ReadableByView = readableByView;
        WritableByView = writableByView;
        CanWrite = canWrite;
    }

    public string Name { get; }
    public ITypeSymbol Type { get; }

    // The view can read it: a getter the view's assembly can access.
    public bool ReadableByView { get; }

    // The view can write it (two-way bindings): a setter the view's assembly can access.
    public bool WritableByView { get; }

    // It has a setter, accessible or not.
    public bool CanWrite { get; }

    // The bindable member name of type or its bases; null when none.
    public static ViewModelMember Find(INamedTypeSymbol type, string name, Compilation compilation)
    {
        for (INamedTypeSymbol current = type; current != null; current = current.BaseType)
        {
            IPropertySymbol property = current.GetMembers(name).OfType<IPropertySymbol>().FirstOrDefault(p => !p.IsStatic);
            if (property != null)
                return FromProperty(property, compilation);
            foreach (IFieldSymbol field in current.GetMembers().OfType<IFieldSymbol>())
                if (ObservablePropertyConventions.GeneratesProperty(field) &&
                    ObservablePropertyConventions.PropertyName(field.Name) == name)
                    return FromField(field, compilation);
        }
        return null;
    }

    // Why an [ObservableProperty] field named for name generates no property (its type is not partial, or the field is
    // static or read-only); empty when there is no such field.
    public static string WhyNotGenerated(INamedTypeSymbol type, string name)
    {
        for (INamedTypeSymbol current = type; current != null; current = current.BaseType)
            foreach (IFieldSymbol field in current.GetMembers().OfType<IFieldSymbol>())
            {
                if (!ObservablePropertyConventions.HasAttribute(field) || ObservablePropertyConventions.PropertyName(field.Name) != name)
                    continue;
                if (!ObservablePropertyConventions.IsPartial(field.ContainingType))
                    return " [ObservableProperty] field '" + field.Name + "' generates it only when '" + field.ContainingType.Name + "' is partial.";
                if (!ObservablePropertyConventions.IsSupportedField(field))
                    return " [ObservableProperty] field '" + field.Name + "' must be a writable instance field.";
            }
        return string.Empty;
    }

    // The members of type the view can read (or write), most derived first, one per name.
    public static IEnumerable<ViewModelMember> All(INamedTypeSymbol type, Compilation compilation, bool writable)
    {
        var seen = new HashSet<string>(System.StringComparer.Ordinal);
        for (INamedTypeSymbol current = type; current != null; current = current.BaseType)
            foreach (ISymbol symbol in current.GetMembers())
            {
                var field = symbol as IFieldSymbol;
                if (field == null || !ObservablePropertyConventions.GeneratesProperty(field))
                    continue;
                ViewModelMember member = FromField(field, compilation);
                if (seen.Add(member.Name) && (writable ? member.WritableByView : member.ReadableByView))
                    yield return member;
            }
    }

    static ViewModelMember FromProperty(IPropertySymbol property, Compilation compilation)
    {
        IMethodSymbol getter = property.GetMethod;
        IMethodSymbol setter = property.SetMethod;
        return new ViewModelMember(property.Name, property.Type,
            getter != null && compilation.IsSymbolAccessibleWithin(getter, compilation.Assembly),
            setter != null && !setter.IsInitOnly && compilation.IsSymbolAccessibleWithin(setter, compilation.Assembly),
            setter != null);
    }

    // ADK generates `public T Name { get; set; }` on the field's (partial) type.
    static ViewModelMember FromField(IFieldSymbol field, Compilation compilation)
    {
        bool accessible = compilation.IsSymbolAccessibleWithin(field.ContainingType, compilation.Assembly);
        return new ViewModelMember(ObservablePropertyConventions.PropertyName(field.Name), field.Type, accessible, accessible, true);
    }
}
