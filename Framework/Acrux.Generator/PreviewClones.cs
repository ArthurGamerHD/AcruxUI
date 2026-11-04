using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Adk.Analyzer;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Acrux.Generator;

/// <summary>
/// Which view models get a preview clone and what each clone holds, decided in one place:
/// <see cref="PreviewCloneGenerator"/> emits the clones, and <see cref="AcruxViewGenerator"/> binds compiled views to
/// them in design builds (both plan from here, since source generators never see each other's output). The clones'
/// base and collections are ADK's (Adk.Mvvm); what is design code is ADK's <see cref="DesignSources"/> rule.
///
/// A view model with a <c>[PreviewWith]</c> member is cloned, and so is every view model it reaches (a property of
/// a view model type, or a collection of them). A clone has every bindable member of the original (its public
/// readable properties and the properties its <c>[ObservableProperty]</c> fields generate) as a settable observable
/// property of the same name; a view model member becomes its clone, a collection of view models a list of clones.
/// </summary>
internal static class PreviewClones
{
    public const string AttributeName = "Adk.Mvvm.PreviewWithAttribute";
    const string ObservableObjectName = "Adk.Mvvm.ObservableObject";
    const string NotifyName = "System.ComponentModel.INotifyPropertyChanged";

    internal sealed class Clone
    {
        public INamedTypeSymbol Original;
        public string Namespace;
        // The static class with the design properties and Default: FooViewModelPreview.
        public string PreviewName;
        // The clone type: FooViewModelPreviewModel.
        public string ModelName;
        public readonly List<Member> Members = new List<Member>();
        // Discovery order from the [PreviewWith] roots: owners come before what they hold.
        public int Order;
        // Class-level values by member path ("Title", "Rows.Name"), most derived class first.
        public readonly Dictionary<string, ImmutableValues> Overrides = new Dictionary<string, ImmutableValues>(StringComparer.Ordinal);

        public string PreviewFullName => Qualify(Namespace, PreviewName);
        public string ModelFullName => Qualify(Namespace, ModelName);
    }

    internal enum MemberKind
    {
        // Same type as the original's member.
        Value,
        // A view model: the clone holds that view model's clone.
        Model,
        // A collection of view models: the clone holds a list of their clones.
        ModelList,
    }

    internal sealed class Member
    {
        public string Name;
        public ITypeSymbol Type;
        public MemberKind Kind;
        // For Model and ModelList: the clone of the (element) view model.
        public Clone Target;
        public ImmutableValues Values;

        // The clone's property type (fully qualified).
        public string CloneTypeName
        {
            get
            {
                switch (Kind)
                {
                    case MemberKind.Model:
                        return Target.ModelFullName;
                    case MemberKind.ModelList:
                        return IsObservableList(Type)
                            ? "global::Adk.Mvvm.ObservableList<" + Target.ModelFullName + ">"
                            : "global::System.Collections.Generic.List<" + Target.ModelFullName + ">";
                    default:
                        return Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                }
            }
        }

        // The clone's value converts implicitly to a binding's value type bound (the design bindings cast it so).
        public bool ConvertsTo(ITypeSymbol bound, Compilation compilation)
        {
            if (bound.SpecialType == SpecialType.System_Object)
                return true;
            switch (Kind)
            {
                case MemberKind.Model:
                    return bound.ToDisplayString() == NotifyName;
                case MemberKind.ModelList:
                    string name = bound.OriginalDefinition.ToDisplayString();
                    return name == "System.Collections.IEnumerable" || name == "System.Collections.ICollection" ||
                           name == "System.Collections.IList";
                default:
                    return compilation.ClassifyConversion(Type, bound).IsImplicit;
            }
        }
    }

    /// <summary>A member's preview values (none, one, or several cycling over list items).</summary>
    internal sealed class ImmutableValues
    {
        public readonly TypedConstant[] Items;

        public ImmutableValues(TypedConstant[] items)
        {
            Items = items;
        }

        public int Count => Items.Length;
    }

    static string Qualify(string ns, string name)
    {
        return "global::" + (string.IsNullOrEmpty(ns) ? name : ns + "." + name);
    }

    sealed class Planned
    {
        public DesignSources Design;
        public Dictionary<INamedTypeSymbol, Clone> Clones;
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Compilation, Planned> Plans =
        new System.Runtime.CompilerServices.ConditionalWeakTable<Compilation, Planned>();

    // Every clone of the compilation, keyed by the original view model (planned once per compilation).
    public static Dictionary<INamedTypeSymbol, Clone> Plan(Compilation compilation, DesignSources design)
    {
        Planned planned;
        lock (Plans)
        {
            if (Plans.TryGetValue(compilation, out planned) && planned.Design.Equals(design))
                return planned.Clones;
        }
        var clones = Build(compilation, design);
        lock (Plans)
        {
            Plans.Remove(compilation);
            Plans.Add(compilation, new Planned { Design = design, Clones = clones });
        }
        return clones;
    }

    static Dictionary<INamedTypeSymbol, Clone> Build(Compilation compilation, DesignSources design)
    {
        var clones = new Dictionary<INamedTypeSymbol, Clone>(SymbolEqualityComparer.Default);
        if (compilation.GetTypeByMetadataName(AttributeName) == null)
            return clones;

        var queue = new Queue<INamedTypeSymbol>();
        foreach (INamedTypeSymbol type in SourceTypes(compilation.Assembly.GlobalNamespace))
            if (IsCandidate(type, compilation, design) && HasPreviewValues(type))
                Enqueue(type, clones, queue);

        while (queue.Count > 0)
        {
            Clone clone = clones[queue.Dequeue()];
            ReadOverrides(clone);
            foreach (var bindable in Bindable(clone.Original))
            {
                ImmutableValues overridden;
                var member = new Member
                {
                    Name = bindable.Key,
                    Type = bindable.Value.Type,
                    Values = clone.Overrides.TryGetValue(bindable.Key, out overridden) ? overridden : Values(bindable.Value.Symbol),
                };
                INamedTypeSymbol element = ElementType(member.Type);
                INamedTypeSymbol model = member.Type as INamedTypeSymbol;
                if (element != null && IsCandidate(element, compilation, design))
                {
                    member.Kind = MemberKind.ModelList;
                    member.Target = Enqueue(element, clones, queue);
                }
                else if (model != null && IsCandidate(model, compilation, design))
                {
                    member.Kind = MemberKind.Model;
                    member.Target = Enqueue(model, clones, queue);
                }
                clone.Members.Add(member);
            }
        }
        return clones;
    }

    // The member of owner leads back up to it (an item's link to its owner, found before it from the roots): creating
    // it would never end, so the generated clone leaves it empty. Links down (to clones found later) are created.
    public static bool IsBackLink(Clone owner, Member member)
    {
        if (member.Kind == MemberKind.Value || !Reaches(member.Target, owner))
            return false;
        // Lists are containment: a link to a view model that lists this one is the link up to its owner, and a list
        // is a link down. Only cycles without that signal fall back to the discovery order.
        bool targetLists = member.Target.Members.Any(m => m.Kind == MemberKind.ModelList && m.Target == owner);
        bool isList = member.Kind == MemberKind.ModelList;
        if (targetLists != isList)
            return targetLists;
        return member.Target.Order <= owner.Order;
    }

    // The clone of from leads to to through nested clones or lists. listsOnly: Follow lists only: a list's items are
    // created eagerly, while a nested clone that leads back is left null (which already ends every cycle through it).
    public static bool Reaches(Clone from, Clone to, bool listsOnly = false)
    {
        var seen = new HashSet<Clone>();
        var stack = new Stack<Clone>();
        stack.Push(from);
        while (stack.Count > 0)
        {
            Clone current = stack.Pop();
            if (current == to)
                return true;
            if (!seen.Add(current))
                continue;
            foreach (Member member in current.Members)
                if (member.Kind == MemberKind.ModelList || (!listsOnly && member.Kind == MemberKind.Model))
                    stack.Push(member.Target);
        }
        return false;
    }

    static Clone Enqueue(INamedTypeSymbol type, Dictionary<INamedTypeSymbol, Clone> clones, Queue<INamedTypeSymbol> queue)
    {
        Clone clone;
        if (clones.TryGetValue(type, out clone))
            return clone;
        clone = new Clone
        {
            Order = clones.Count,
            Original = type,
            Namespace = type.ContainingNamespace.IsGlobalNamespace ? string.Empty : type.ContainingNamespace.ToDisplayString(),
            PreviewName = type.Name + "Preview",
            ModelName = type.Name + "PreviewModel",
        };
        clones.Add(type, clone);
        queue.Enqueue(type);
        return clone;
    }

    // A view model that can be cloned: a top-level, non-generic source class of this mod, observable.
    static bool IsCandidate(INamedTypeSymbol type, Compilation compilation, DesignSources design)
    {
        if (type == null || type.TypeKind != TypeKind.Class || type.IsStatic || type.IsGenericType || type.ContainingType != null ||
            !SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, compilation.Assembly) ||
            !type.Locations.Any(location => location.IsInSource) || design.IsDesignOnly(type))
            return false;
        string ns = type.ContainingNamespace.ToDisplayString();
        // The framework's own namespaces only: a mod's "AcruxSomething" namespace is not Acrux.
        if (ns == "Adk.Mvvm" || ns == "Acrux" || ns.StartsWith("Acrux.", StringComparison.Ordinal))
            return false;
        return type.AllInterfaces.Any(i => i.ToDisplayString() == NotifyName);
    }

    // Class-level [PreviewWith(path, values...)], most derived first (a base's value for the same path is ignored).
    static void ReadOverrides(Clone clone)
    {
        for (INamedTypeSymbol current = clone.Original; current != null && !IsFramework(current); current = current.BaseType)
            foreach (AttributeData attribute in current.GetAttributes())
            {
                if (attribute.AttributeClass?.ToDisplayString() != AttributeName || attribute.ConstructorArguments.Length == 0)
                    continue;
                TypedConstant argument = attribute.ConstructorArguments[0];
                TypedConstant[] values = argument.Kind == TypedConstantKind.Array ? argument.Values.ToArray() : new[] { argument };
                string path = values.Length > 0 ? values[0].Value as string : null;
                if (string.IsNullOrEmpty(path) || clone.Overrides.ContainsKey(path))
                    continue;
                clone.Overrides.Add(path, new ImmutableValues(values.Skip(1).ToArray()));
            }
    }

    static bool HasPreviewValues(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol current = type; current != null && !IsFramework(current); current = current.BaseType)
            if (current.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == AttributeName))
                return true;
        for (INamedTypeSymbol current = type; current != null && !IsFramework(current); current = current.BaseType)
            foreach (ISymbol member in current.GetMembers())
                if ((member is IPropertySymbol || member is IFieldSymbol) && PreviewAttribute(member) != null)
                    return true;
        return false;
    }

    static bool IsFramework(INamedTypeSymbol type)
    {
        string name = type.ToDisplayString();
        return name == ObservableObjectName || type.SpecialType == SpecialType.System_Object;
    }

    // The original's bindable members by name, most derived first: public readable instance properties and the
    // properties [ObservableProperty] fields generate.
    static List<KeyValuePair<string, (ITypeSymbol Type, ISymbol Symbol)>> Bindable(INamedTypeSymbol type)
    {
        var result = new List<KeyValuePair<string, (ITypeSymbol, ISymbol)>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (INamedTypeSymbol current = type; current != null && !IsFramework(current); current = current.BaseType)
            foreach (ISymbol symbol in current.GetMembers())
            {
                if (symbol is IPropertySymbol property)
                {
                    if (property.IsStatic || property.IsIndexer || property.DeclaredAccessibility != Accessibility.Public ||
                        property.GetMethod == null || property.GetMethod.DeclaredAccessibility != Accessibility.Public ||
                        property.ReturnsByRef || property.ReturnsByRefReadonly || !seen.Add(property.Name))
                        continue;
                    result.Add(new KeyValuePair<string, (ITypeSymbol, ISymbol)>(property.Name, (property.Type, property)));
                }
                else if (symbol is IFieldSymbol field && ObservablePropertyConventions.GeneratesProperty(field))
                {
                    string name = ObservablePropertyConventions.PropertyName(field.Name);
                    if (seen.Add(name))
                        result.Add(new KeyValuePair<string, (ITypeSymbol, ISymbol)>(name, (field.Type, field)));
                }
            }
        return result;
    }

    static AttributeData PreviewAttribute(ISymbol symbol)
    {
        return symbol.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == AttributeName);
    }

    static ImmutableValues Values(ISymbol symbol)
    {
        AttributeData attribute = PreviewAttribute(symbol);
        if (attribute == null || attribute.ConstructorArguments.Length == 0)
            return new ImmutableValues(new TypedConstant[0]);
        TypedConstant argument = attribute.ConstructorArguments[0];
        return argument.Kind == TypedConstantKind.Array
            ? new ImmutableValues(argument.Values.ToArray())
            : new ImmutableValues(new[] { argument });
    }

    public static INamedTypeSymbol ElementType(ITypeSymbol type)
    {
        if (type == null || type.SpecialType == SpecialType.System_String)
            return null;
        if (type is IArrayTypeSymbol array)
            return array.Rank == 1 ? array.ElementType as INamedTypeSymbol : null;
        var named = type as INamedTypeSymbol;
        if (named != null && named.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T)
            return named.TypeArguments[0] as INamedTypeSymbol;
        foreach (INamedTypeSymbol face in type.AllInterfaces)
            if (face.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T)
                return face.TypeArguments[0] as INamedTypeSymbol;
        return null;
    }

    static bool IsObservableList(ITypeSymbol type)
    {
        return type is INamedTypeSymbol named && named.OriginalDefinition.ToDisplayString() == "Adk.Mvvm.ObservableList<T>";
    }

    static IEnumerable<INamedTypeSymbol> SourceTypes(INamespaceSymbol ns)
    {
        foreach (INamespaceSymbol child in ns.GetNamespaceMembers())
            foreach (INamedTypeSymbol type in SourceTypes(child))
                yield return type;
        foreach (INamedTypeSymbol type in ns.GetTypeMembers())
            yield return type;
    }

    // ------------------------------------------------------------------ values

    // C# for preview value value as type; null when it does not fit (the reason in error).
    public static string Expression(TypedConstant value, ITypeSymbol type, out string error)
    {
        error = null;
        if (value.IsNull)
            return type.IsValueType ? "default(" + type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + ")" : "null";
        var named = type as INamedTypeSymbol;
        if (named != null && named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            return Expression(value, named.TypeArguments[0], out error);
        string typeName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        object raw = value.Value;

        if (type.SpecialType == SpecialType.System_String)
            return SymbolDisplay.FormatLiteral(Convert.ToString(raw, CultureInfo.InvariantCulture), true);
        if (type.SpecialType == SpecialType.System_Object)
            return raw is string text0 ? SymbolDisplay.FormatLiteral(text0, true)
                : raw is bool flag ? (flag ? "true" : "false")
                : "((object)(" + SymbolDisplay.FormatPrimitive(raw, true, false) + Suffix(raw) + "))";
        if (type.SpecialType == SpecialType.System_Boolean)
        {
            if (raw is bool b)
                return b ? "true" : "false";
            error = "expects true or false";
            return null;
        }
        if (IsNumber(type.SpecialType))
        {
            if (raw is string || raw is bool || raw == null)
            {
                error = "expects a number";
                return null;
            }
            return "((" + typeName + ")(" + SymbolDisplay.FormatPrimitive(raw, true, false) + Suffix(raw) + "))";
        }
        if (type.TypeKind == TypeKind.Enum)
        {
            if (raw is string text)
            {
                if (type.GetMembers(text).OfType<IFieldSymbol>().Any())
                    return typeName + "." + text;
                error = "is not a member of " + type.Name;
                return null;
            }
            return "((" + typeName + ")(" + SymbolDisplay.FormatPrimitive(raw, true, false) + "))";
        }
        string full = type.ToDisplayString();
        if (full == "VRageMath.Color")
            return ColorExpression(raw as string, out error);
        if (full == "Acrux.Styling.BackgroundBrush" || full == "Acrux.Styling.ImageBrush" || full == "Acrux.Styling.SolidColorBrush")
            return BrushExpression(raw as string, full, out error);
        if (full == "VRageMath.Vector2" || full == "VRageMath.Vector3" || full == "VRageMath.Vector4")
            return VectorExpression(raw, full == "VRageMath.Vector2" ? 2 : full == "VRageMath.Vector3" ? 3 : 4, typeName, out error);
        error = "has no preview value form for " + type.Name;
        return null;
    }

    static bool IsNumber(SpecialType type)
    {
        switch (type)
        {
            case SpecialType.System_Byte: case SpecialType.System_SByte: case SpecialType.System_Int16:
            case SpecialType.System_UInt16: case SpecialType.System_Int32: case SpecialType.System_UInt32:
            case SpecialType.System_Int64: case SpecialType.System_UInt64: case SpecialType.System_Single:
            case SpecialType.System_Double: case SpecialType.System_Decimal:
                return true;
        }
        return false;
    }

    static string Suffix(object raw)
    {
        return raw is float ? "f" : raw is double ? "d" : raw is decimal ? "m" : string.Empty;
    }

    static string ColorExpression(string text, out string error)
    {
        error = null;
        string hex = (text ?? string.Empty).Trim().TrimStart('#');
        uint value;
        if ((hex.Length != 6 && hex.Length != 8) ||
            !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
        {
            error = "expects a color as \"#RRGGBB\" or \"#AARRGGBB\"";
            return null;
        }
        uint a = hex.Length == 8 ? (value >> 24) & 0xFF : 0xFF;
        uint r = (value >> 16) & 0xFF, g = (value >> 8) & 0xFF, b = value & 0xFF;
        return "new global::VRageMath.Color(" + r + ", " + g + ", " + b + ", " + a + ")";
    }

    // A brush as XAML writes one: "#RRGGBB" / "#AARRGGBB" a solid colour, anything else an image (a sprite name),
    // optionally ending in a Stretch member: "Lock Uniform".
    static string BrushExpression(string text, string type, out string error)
    {
        error = null;
        text = (text ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            error = "expects a colour or a sprite name";
            return null;
        }
        if (text[0] == '#' && type != "Acrux.Styling.ImageBrush")
        {
            string color = ColorExpression(text, out error);
            return color == null ? null : "new global::Acrux.Styling.SolidColorBrush(" + color + ")";
        }
        if (type == "Acrux.Styling.SolidColorBrush")
        {
            error = "expects a colour as \"#RRGGBB\" or \"#AARRGGBB\"";
            return null;
        }
        string source = text, stretch = null;
        int space = text.LastIndexOf(' ');
        if (space > 0)
        {
            string last = text.Substring(space + 1);
            if (last == "None" || last == "Fill" || last == "Uniform" || last == "UniformToFill")
            {
                source = text.Substring(0, space).TrimEnd();
                stretch = last;
            }
        }
        return "new global::Acrux.Styling.ImageBrush(" + SymbolDisplay.FormatLiteral(source, true) + ")" +
               (stretch == null ? string.Empty : " { Stretch = global::Acrux.Styling.Stretch." + stretch + " }");
    }

    static string VectorExpression(object raw, int size, string typeName, out string error)
    {
        error = null;
        var parts = new List<float>();
        if (raw is string text)
        {
            foreach (string part in text.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                float number;
                if (!float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                {
                    error = "expects numbers as \"x y z w\"";
                    return null;
                }
                parts.Add(number);
            }
        }
        else if (raw != null && !(raw is bool))
            parts.Add(Convert.ToSingle(raw, CultureInfo.InvariantCulture));
        if (parts.Count == 1)
            while (parts.Count < size) parts.Add(parts[0]);
        else if (size == 4 && parts.Count == 2)
            parts = new List<float> { parts[0], parts[1], parts[0], parts[1] };
        if (parts.Count != size)
        {
            error = "expects " + size + " numbers (or one for all" + (size == 4 ? ", or two as a thickness" : string.Empty) + ")";
            return null;
        }
        return "new " + typeName + "(" + string.Join(", ", parts.Select(p => p.ToString("R", CultureInfo.InvariantCulture) + "f")) + ")";
    }
}
