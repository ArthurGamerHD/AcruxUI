using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Acrux.Generator;

/// <summary>
/// Mirrors the Acrux object model of an Acrux UI Toolkit mod as thin client wrappers.
///
/// The toolkit mod compiles the real Acrux UI. Every other mod (a "client") compiles the
/// generated wrapper classes instead: same namespaces, type names and members, but each
/// wrapper only holds the toolkit object (opaque) and calls a typed delegate obtained
/// through a mod message. Client subclasses of toolkit controls are backed by generated
/// <c>Remote_*</c> toolkit subclasses that forward every overridable member to the client.
///
/// Activated only in a compilation that declares a class named AcruxUiToolkitExportAttribute and
/// applies it to the assembly. Files listed in the additional file <c>acrux-ui-toolkit-shared.txt</c>
/// are compiled verbatim by clients; their types cross the boundary by value (enums as int,
/// known structs through codecs).
/// </summary>
[Generator]
public sealed class AcruxUiToolkitMirrorGenerator : IIncrementalGenerator
{
    const string ClientRuntime = "global::Acrux.Toolkit";
    const string ServerRuntime = "global::Acrux.Toolkit.Server";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var shared = context.AdditionalTextsProvider
            .Where(static f => string.Equals(Path.GetFileName(f.Path), "acrux-ui-toolkit-shared.txt", StringComparison.OrdinalIgnoreCase))
            .Select(static (f, ct) => f.GetText(ct)?.ToString() ?? string.Empty)
            .Collect();

        context.RegisterSourceOutput(context.CompilationProvider.Combine(shared), static (output, input) =>
        {
            var compilation = input.Left;
            if (!compilation.Assembly.GetAttributes().Any(a => a.AttributeClass?.Name == "AcruxUiToolkitExportAttribute"))
                return;
            var sharedFiles = new List<string>();
            var mirrorRoots = new List<string> { "Acrux" };
            foreach (var text in input.Right)
                foreach (var raw in text.Split('\n'))
                {
                    var line = raw.Trim();
                    // "mirror: Namespace" adds a toolkit-owned namespace (outside Acrux) to the client API.
                    if (line.StartsWith("mirror:", StringComparison.Ordinal))
                        mirrorRoots.Add(line.Substring("mirror:".Length).Trim());
                    else if (line.Length > 0 && !line.StartsWith("#", StringComparison.Ordinal))
                        // "./" marks a file of the toolkit project itself; others are relative to Framework/.
                        sharedFiles.Add(line.Replace('\\', '/').TrimStart('.', '/'));
                }
            new Emitter(compilation, sharedFiles, mirrorRoots, output).Run();
        });
    }

    // ------------------------------------------------------------------------------------

    sealed class TypeMap
    {
        public string Client;
        public string Wire;
        public string Server;
        public Func<string, string> C2W = e => e;
        public Func<string, string> W2C = e => e;
        public Func<string, string> SFrom = e => e;
        public Func<string, string> STo = e => e;
        public bool IsIdentity;
    }

    sealed class Codec
    {
        public string Wire;
        public string C2W, W2C, SFrom, STo; // string.Format templates with {0}
    }

    sealed class Emitter
    {
        readonly Compilation _compilation;
        readonly List<string> _sharedFiles;
        readonly List<string> _mirrorRoots;

        bool InMirroredNamespace(string ns)
        {
            foreach (string root in _mirrorRoots)
                if (ns == root || ns.StartsWith(root + ".", StringComparison.Ordinal))
                    return true;
            return false;
        }
        readonly SourceProductionContext _output;
        readonly HashSet<INamedTypeSymbol> _mirrored = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        readonly HashSet<INamedTypeSymbol> _shared = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        readonly Dictionary<string, Codec> _codecs = new Dictionary<string, Codec>(StringComparer.Ordinal);
        readonly HashSet<string> _manual = new HashSet<string>(StringComparer.Ordinal);
        readonly StringBuilder _report = new StringBuilder();
        readonly List<string> _serverRegistrations = new List<string>();
        readonly List<string> _clientLoads = new List<string>();
        readonly List<string> _factories = new List<string>();
        readonly List<string> _propertyCodecRegistrations = new List<string>();
        // Toolkit-only enums with an explicit underlying type, published under that integer's codec key.
        readonly List<KeyValuePair<string, string>> _enumCodecAliases = new List<KeyValuePair<string, string>>();

        static readonly DiagnosticDescriptor EnumWithoutUnderlyingType = new DiagnosticDescriptor(
            "ACRUX009",
            "Styled-property enum needs an explicit underlying type",
            "Enum '{0}' is the type of styled property '{1}', but clients cannot name it. Declare its underlying type " +
            "explicitly (for example 'enum {2} : byte' or ': int') so the property crosses the bridge as that integer.",
            "Acrux.Generator",
            DiagnosticSeverity.Warning,
            true);

        sealed class PropertyCodec
        {
            public string Key;
            public ITypeSymbol Type;
            public TypeMap Map;
        }
        readonly List<string> _hookRegistrations = new List<string>();
        readonly StringBuilder _serverHooks = new StringBuilder();
        readonly StringBuilder _serverHookBindings = new StringBuilder();
        readonly StringBuilder _serverHookUnbindings = new StringBuilder();
        readonly StringBuilder _serverInterfaces = new StringBuilder();
        readonly HashSet<string> _declaredInterfaces = new HashSet<string>(StringComparer.Ordinal);
        int _lambda;

        public Emitter(Compilation compilation, List<string> sharedFiles, List<string> mirrorRoots, SourceProductionContext output)
        {
            _compilation = compilation;
            _sharedFiles = sharedFiles;
            _mirrorRoots = mirrorRoots;
            _output = output;

            _codecs["Acrux.Layout.GridLength"] = new Codec
            {
                Wire = "global::VRageMath.Vector2",
                C2W = "new global::VRageMath.Vector2(({0}).Value, (float)(int)({0}).GridUnitType)",
                W2C = "new global::Acrux.Layout.GridLength(({0}).X, (global::Acrux.Layout.GridUnitType)(int)({0}).Y)",
                SFrom = "new global::Acrux.Layout.GridLength(({0}).X, (global::Acrux.Layout.GridUnitType)(int)({0}).Y)",
                STo = "new global::VRageMath.Vector2(({0}).Value, (float)(int)({0}).GridUnitType)",
            };
            _codecs["Acrux.Rendering.TextureSource"] = new Codec
            {
                Wire = "object",
                C2W = ClientRuntime + ".Ui.TextureToWire({0})",
                W2C = ClientRuntime + ".Ui.TextureFromWire({0})",
                SFrom = ServerRuntime + ".ServerUi.TextureFromWire({0})",
                STo = ServerRuntime + ".ServerUi.TextureToWire({0})",
            };
            _codecs["Acrux.Styling.RelativePoint"] = new Codec
            {
                Wire = "global::VRageMath.Vector3",
                C2W = "new global::VRageMath.Vector3(({0}).Point, (float)(int)({0}).Unit)",
                W2C = "new global::Acrux.Styling.RelativePoint(({0}).X, ({0}).Y, (global::Acrux.Styling.RelativeUnit)(int)({0}).Z)",
                SFrom = "new global::Acrux.Styling.RelativePoint(({0}).X, ({0}).Y, (global::Acrux.Styling.RelativeUnit)(int)({0}).Z)",
                STo = "new global::VRageMath.Vector3(({0}).Point, (float)(int)({0}).Unit)",
            };
            _codecs["Acrux.Styling.RelativeRect"] = new Codec
            {
                Wire = "global::VRage.MyTuple<global::VRageMath.Vector4, int>",
                C2W = "new global::VRage.MyTuple<global::VRageMath.Vector4, int>(new global::VRageMath.Vector4(({0}).Rect.X, ({0}).Rect.Y, ({0}).Rect.Width, ({0}).Rect.Height), (int)({0}).Unit)",
                W2C = "new global::Acrux.Styling.RelativeRect(({0}).Item1.X, ({0}).Item1.Y, ({0}).Item1.Z, ({0}).Item1.W, (global::Acrux.Styling.RelativeUnit)({0}).Item2)",
                SFrom = "new global::Acrux.Styling.RelativeRect(({0}).Item1.X, ({0}).Item1.Y, ({0}).Item1.Z, ({0}).Item1.W, (global::Acrux.Styling.RelativeUnit)({0}).Item2)",
                STo = "new global::VRage.MyTuple<global::VRageMath.Vector4, int>(new global::VRageMath.Vector4(({0}).Rect.X, ({0}).Rect.Y, ({0}).Rect.Width, ({0}).Rect.Height), (int)({0}).Unit)",
            };
            _codecs["Acrux.Core.Input.ICommand"] = new Codec
            {
                Wire = "object",
                C2W = ClientRuntime + ".Ui.CommandToWire({0})",
                W2C = ClientRuntime + ".Ui.CommandFromWire({0})",
                SFrom = ServerRuntime + ".ServerUi.CommandFromWire({0})",
                STo = ServerRuntime + ".ServerUi.CommandToWire({0})",
            };
            _codecs["System.Collections.IEnumerable"] = new Codec
            {
                Wire = "object",
                C2W = ClientRuntime + ".Ui.ItemsToWire({0})",
                W2C = ClientRuntime + ".Ui.ItemsFromWire({0})",
                SFrom = ServerRuntime + ".ServerUi.ItemsFromWire({0})",
                STo = ServerRuntime + ".ServerUi.ItemsToWire({0})",
            };

            // Members whose semantics need hand-written wrappers on both sides.
            foreach (var m in new[]
                     {
                         "Acrux.Core.Control.GetValue", "Acrux.Core.Control.SetValue", "Acrux.Core.Control.ClearValue",
                         "Acrux.Core.Control.SetDynamicResource", "Acrux.Core.Control.ResolveDynamicResource",
                         "Acrux.Core.Control.ResolveResource", "Acrux.Core.Control.TryFindResource",
                         "Acrux.Animation.VisualElementAnimationExtensions.Animate",
                         "Acrux.Styling.StyleSheet.SourceLoader", "Acrux.Styling.StyleSheet.SourceLoadFailed",
                         "Acrux.Styling.ResourceDictionary.TryGetValue",
                         "Acrux.Core.Visual.ExternalPeer", "Acrux.Core.Control.StyleKey",
                     })
                _manual.Add(m);
        }

        public void Run()
        {
            Collect(_compilation.GlobalNamespace);
            foreach (var type in _mirrored.OrderBy(t => t.ToDisplayString(), StringComparer.Ordinal))
            {
                try
                {
                    EmitType(type);
                }
                catch (Exception e)
                {
                    _report.AppendLine("// FAILED " + type.ToDisplayString() + ": " + e.Message.Replace("\n", " "));
                }
            }
            EmitRoots();
        }

        // ---------------------------------------------------------------- classification

        void Collect(INamespaceSymbol ns)
        {
            foreach (var member in ns.GetMembers())
            {
                if (member is INamespaceSymbol child)
                {
                    Collect(child);
                    continue;
                }
                var type = member as INamedTypeSymbol;
                if (type == null || (type.DeclaredAccessibility != Accessibility.Public && !InternalMirrors.Contains(type.ToDisplayString())))
                    continue;
                string name = type.ContainingNamespace.ToDisplayString();
                if (!InMirroredNamespace(name))
                    continue;
                if ((name.StartsWith("Acrux.Markup", StringComparison.Ordinal) && !MarkupMirrors.Contains(type.ToDisplayString())) ||
                    false ||
                    name.StartsWith("Acrux.Toolkit", StringComparison.Ordinal))
                    continue;
                if (!type.Locations.Any(l => l.IsInSource))
                    continue;
                if (ManualTypes.Contains(type.OriginalDefinition.ToDisplayString()))
                    continue;
                if (IsSharedSource(type))
                {
                    _shared.Add(type);
                    continue;
                }
                if (type.TypeKind == TypeKind.Struct && !_codecs.ContainsKey(type.ToDisplayString()))
                {
                    _report.AppendLine("// skipped struct without codec: " + type.ToDisplayString());
                    continue;
                }
                if (type.TypeKind == TypeKind.Struct)
                    continue;
                _mirrored.Add(type);
            }
        }

        bool IsSharedSource(INamedTypeSymbol type)
        {
            foreach (var location in type.Locations)
            {
                if (!location.IsInSource)
                    continue;
                string path = location.SourceTree.FilePath.Replace('\\', '/');
                foreach (var shared in _sharedFiles)
                    if (path.EndsWith("/" + shared, StringComparison.Ordinal))
                        return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- type mapping

        int _lambdaVar;
        string NextLambdaVar() => "__x" + (_lambdaVar++);

        static string Fq(ITypeSymbol t) => t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        // Internal Acrux types that clients used when they compiled Acrux themselves.
        static readonly HashSet<string> InternalMirrors = new HashSet<string>(StringComparer.Ordinal)
        {
            "Acrux.Controls.Chrome.CaptionGlyph", "Acrux.Controls.Chrome.CaptionGlyphs",
        };

        // The markup runtime stays in the toolkit; only these helpers are useful to clients.
        static readonly HashSet<string> MarkupMirrors = new HashSet<string>(StringComparer.Ordinal)
        {
            "Acrux.Markup.PropertyConverter",
        };

        static readonly HashSet<string> ManualTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "Acrux.Core.PropertySystem.StyledProperty", "Acrux.Core.PropertySystem.StyledProperty<T>",
            "Acrux.Core.PropertySystem.IStyledProperty", "Acrux.Core.PropertySystem.IStyleValueSource",
            "Acrux.Core.PropertySystem.StyledPropertyCatalog",
        };

        static readonly HashSet<string> PassNamespaces = new HashSet<string>(StringComparer.Ordinal)
        {
            "System", "VRageMath", "VRage.Game.GUI.TextPanel", "Sandbox.ModAPI", "Sandbox.ModAPI.Ingame",
            "VRage.Game.ModAPI", "VRage.Game.ModAPI.Ingame", "VRage.ModAPI", "VRage", "VRage.Utils",
            "System.Text", "System.Globalization", "System.ComponentModel",
        };

        sealed class GenericContext
        {
            public readonly Dictionary<ITypeParameterSymbol, ITypeSymbol> ServerArgs =
                new Dictionary<ITypeParameterSymbol, ITypeSymbol>(SymbolEqualityComparer.Default);
        }

        TypeMap Map(ITypeSymbol type, GenericContext generics)
        {
            if (type == null)
                return null;
            if (type.SpecialType == SpecialType.System_Void)
                return new TypeMap { Client = "void", Wire = "void", Server = "void", IsIdentity = true };
            if (type.SpecialType == SpecialType.System_Object)
                return new TypeMap
                {
                    Client = "object", Wire = "object", Server = "object",
                    C2W = e => ClientRuntime + ".Ui.ToWire(" + e + ")",
                    W2C = e => ClientRuntime + ".Ui.FromWire(" + e + ")",
                    SFrom = e => ServerRuntime + ".ServerUi.FromWire(" + e + ")",
                    STo = e => ServerRuntime + ".ServerUi.ToWire(" + e + ")",
                };

            if (type is ITypeParameterSymbol tp)
                return MapTypeParameter(tp, generics);

            if (type is IArrayTypeSymbol array)
            {
                var element = Map(array.ElementType, generics);
                if (element == null || array.Rank != 1)
                    return null;
                if (element.IsIdentity)
                    return Identity(Fq(type));
                return new TypeMap
                {
                    Client = element.Client + "[]", Wire = "object[]", Server = element.Server + "[]",
                    C2W = e => { string v = NextLambdaVar(); return ClientRuntime + ".Ui.ArrayToWire<" + element.Client + ">(" + e + ", " + v + " => (object)" + element.C2W(v) + ")"; },
                    W2C = e => { string v = NextLambdaVar(); return ClientRuntime + ".Ui.ArrayFromWire<" + element.Client + ">(" + e + ", " + v + " => " + element.W2C("((" + element.Wire + ")" + v + ")") + ")"; },
                    SFrom = e => { string v = NextLambdaVar(); return ServerRuntime + ".ServerUi.ArrayFromWire<" + element.Server + ">(" + e + ", " + v + " => " + element.SFrom("((" + element.Wire + ")" + v + ")") + ")"; },
                    STo = e => { string v = NextLambdaVar(); return ServerRuntime + ".ServerUi.ArrayToWire<" + element.Server + ">(" + e + ", " + v + " => (object)" + element.STo(v) + ")"; },
                };
            }

            var named = type as INamedTypeSymbol;
            if (named == null)
                return null;

            string full = named.OriginalDefinition.ToDisplayString();
            Codec codec;
            if (_codecs.TryGetValue(full, out codec))
            {
                string fq = Fq(named);
                return new TypeMap
                {
                    Client = fq, Wire = codec.Wire, Server = fq,
                    C2W = e => string.Format(codec.C2W, e), W2C = e => string.Format(codec.W2C, e),
                    SFrom = e => string.Format(codec.SFrom, e), STo = e => string.Format(codec.STo, e),
                };
            }

            // Value structs both sides compile from the same shared source are still two distinct runtime types
            // (one per mod assembly), so they cross the bridge field by field.
            if (named.TypeKind == TypeKind.Struct && named.TypeArguments.Length == 0 && IsAcrux(named) && _shared.Contains(named))
                return MapSharedStruct(named, generics);

            if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            {
                var inner = Map(named.TypeArguments[0], generics);
                if (inner == null)
                    return null;
                if (inner.IsIdentity)
                    return Identity(Fq(named));
                if (named.TypeArguments[0].TypeKind == TypeKind.Enum)
                    return new TypeMap
                    {
                        Client = inner.Client + "?", Wire = inner.Wire + "?", Server = inner.Server + "?",
                        C2W = e => "(" + inner.Wire + "?)(" + e + ")", W2C = e => "(" + inner.Client + "?)(" + e + ")",
                        SFrom = e => "(" + inner.Server + "?)(" + e + ")", STo = e => "(" + inner.Wire + "?)(" + e + ")",
                    };
                return null;
            }

            if (named.TypeKind == TypeKind.Enum && (IsAcrux(named)))
            {
                string fq = Fq(named);
                string wire = Fq(named.EnumUnderlyingType);
                return new TypeMap
                {
                    Client = fq, Wire = wire, Server = fq,
                    C2W = e => "(" + wire + ")(" + e + ")", W2C = e => "(" + fq + ")(" + e + ")",
                    SFrom = e => "(" + fq + ")(" + e + ")", STo = e => "(" + wire + ")(" + e + ")",
                };
            }

            if (full == "Acrux.Core.PropertySystem.StyledProperty<T>")
            {
                var arg = Map(named.TypeArguments[0], generics);
                if (arg == null)
                    return null;
                string client = "global::Acrux.Core.PropertySystem.StyledProperty<" + arg.Client + ">";
                string server = "global::Acrux.Core.PropertySystem.StyledProperty<" + arg.Server + ">";
                return new TypeMap
                {
                    Client = client, Wire = "object", Server = server,
                    C2W = e => ClientRuntime + ".Ui.Native(" + e + ")",
                    W2C = e => ClientRuntime + ".Ui.WrapProperty<" + arg.Client + ">(" + e + ")",
                    SFrom = e => "((" + server + ")(" + e + "))",
                    STo = e => "((object)(" + e + "))",
                };
            }
            if (full == "Acrux.Core.PropertySystem.IStyledProperty")
                return new TypeMap
                {
                    Client = "global::Acrux.Core.PropertySystem.IStyledProperty", Wire = "object",
                    Server = "global::Acrux.Core.PropertySystem.IStyledProperty",
                    C2W = e => ClientRuntime + ".Ui.Native(" + e + ")",
                    W2C = e => ClientRuntime + ".Ui.WrapProperty(" + e + ")",
                    SFrom = e => "((global::Acrux.Core.PropertySystem.IStyledProperty)(" + e + "))",
                    STo = e => "((object)(" + e + "))",
                };

            if (named.TypeKind == TypeKind.Delegate && named.ContainingNamespace.ToDisplayString() == "System" &&
                (named.Name == "Action" || named.Name == "Func"))
                return MapDelegate(named, generics);
            if (named.TypeKind == TypeKind.Delegate && IsAcrux(named) && _mirrored.Contains(named.OriginalDefinition))
                return MapAcruxDelegate(named, generics);

            if (IsAcrux(named))
            {
                if (!_mirrored.Contains(named.OriginalDefinition))
                    return null;
                var clientArgs = new List<string>();
                var serverArgs = new List<string>();
                foreach (var arg in named.TypeArguments)
                {
                    var m = Map(arg, generics);
                    if (m == null)
                        return null;
                    clientArgs.Add(m.Client);
                    serverArgs.Add(m.Server);
                }
                string client = GenericName(named, clientArgs);
                string server = GenericName(named, serverArgs);
                if (named.TypeKind == TypeKind.Interface)
                {
                    string key = Sanitize(named.OriginalDefinition.ToDisplayString());
                    // Bridge classes are never generic: static delegate fields must exist once, not per closed type.
                    string bridgeType = "global::" + named.ContainingNamespace.ToDisplayString() + ".__UiBridge_" + key;
                    string exportArgs = clientArgs.Count > 0 ? "<" + string.Join(", ", clientArgs) + ">" : "";
                    bool defaultConstruction = named.TypeArguments.Length == 0 ||
                                               SymbolEqualityComparer.Default.Equals(named, named.OriginalDefinition);
                    string importer = named.TypeArguments.Length == 0 ? "b => new " + ServerRuntime + ".ClientImpl_" + key + "(b)" : "null";
                    return new TypeMap
                    {
                        Client = client, Wire = "object", Server = server,
                        C2W = e => bridgeType + ".Export" + exportArgs + "(" + e + ")",
                        W2C = e => ClientRuntime + ".Ui.Wrap<" + client + ">(" + e + ")",
                        SFrom = e => ServerRuntime + ".ServerUi.ImportInterface<" + server + ">(" + e + ", " + importer + ")",
                        STo = e => ServerRuntime + ".ServerUi.ExportInterface(" + e + ")",
                    };
                }
                bool genericConcrete = named.TypeArguments.Length > 0 && named.TypeKind == TypeKind.Class && !named.IsAbstract && !named.IsStatic;
                return new TypeMap
                {
                    Client = client, Wire = "object", Server = server,
                    C2W = e => ClientRuntime + ".Ui.Native(" + e + ")",
                    // A generic toolkit object is created with the client's exact type arguments.
                    W2C = genericConcrete
                        ? (Func<string, string>)(e => ClientRuntime + ".Ui.Wrap<" + client + ">(" + e + ", __n => { var __w = new " + client + "(" + ClientRuntime +
                                                       ".Deferred.Value); __w.__AttachNative(__n); return __w; })")
                        : (e => ClientRuntime + ".Ui.Wrap<" + client + ">(" + e + ")"),
                    SFrom = e => "((" + server + ")(" + e + "))",
                    STo = e => "((object)(" + e + "))",
                };
            }

            // System collections of mirrored/pass element types.
            if (named.IsGenericType && named.ContainingNamespace.ToDisplayString() == "System.Collections.Generic" &&
                named.TypeArguments.Length == 1 &&
                (named.Name == "IReadOnlyList" || named.Name == "IList" || named.Name == "IEnumerable" ||
                 named.Name == "List" || named.Name == "IReadOnlyCollection" || named.Name == "ICollection"))
            {
                var element = Map(named.TypeArguments[0], generics);
                if (element == null)
                    return null;
                if (element.IsIdentity)
                    return Identity(Fq(named));
                string listName = named.Name;
                string client = "global::System.Collections.Generic." + listName + "<" + element.Client + ">";
                string server = "global::System.Collections.Generic." + listName + "<" + element.Server + ">";
                string clientMake = listName == "List" ? "ListFromWire" : "ReadOnlyListFromWire";
                // A mutable IList<T> stays the toolkit's list: the client gets a live view, not a copy.
                if (listName == "IList")
                    return new TypeMap
                    {
                        Client = client, Wire = "object", Server = server,
                        C2W = e => { string v = NextLambdaVar(); return ClientRuntime + ".Ui.LiveListToWire<" + element.Client + ">(" + e + ", " + v + " => (object)" + element.C2W(v) + ")"; },
                        W2C = e => { string v = NextLambdaVar(); string w = NextLambdaVar(); return ClientRuntime + ".Ui.LiveListFromWire<" + element.Client + ">(" + e + ", " + v + " => " + element.W2C("((" + element.Wire + ")" + v + ")") + ", " + w + " => (object)" + element.C2W(w) + ")"; },
                        SFrom = e => { string v = NextLambdaVar(); return ServerRuntime + ".ServerUi.LiveListFromWire<" + element.Server + ">(" + e + ", " + v + " => " + element.SFrom("((" + element.Wire + ")" + v + ")") + ")"; },
                        STo = e => { string v = NextLambdaVar(); string w = NextLambdaVar(); return ServerRuntime + ".ServerUi.ExportList<" + element.Server + ">(" + e + ", " + v + " => (object)" + element.STo(v) + ", " + w + " => " + element.SFrom("((" + element.Wire + ")" + w + ")") + ")"; },
                    };
                return new TypeMap
                {
                    Client = client, Wire = "object", Server = server,
                    C2W = e => { string v = NextLambdaVar(); return ClientRuntime + ".Ui.ListToWire<" + element.Client + ">(" + e + ", " + v + " => (object)" + element.C2W(v) + ")"; },
                    W2C = e => { string v = NextLambdaVar(); return ClientRuntime + ".Ui." + clientMake + "<" + element.Client + ">(" + e + ", " + v + " => " + element.W2C("((" + element.Wire + ")" + v + ")") + ")"; },
                    SFrom = e => { string v = NextLambdaVar(); return ServerRuntime + ".ServerUi.ListFromWire<" + element.Server + ">(" + e + ", " + v + " => " + element.SFrom("((" + element.Wire + ")" + v + ")") + ")"; },
                    STo = e => { string v = NextLambdaVar(); return ServerRuntime + ".ServerUi.ListToWire<" + element.Server + ">(" + e + ", " + v + " => (object)" + element.STo(v) + ")"; },
                };
            }

            if (IsPass(named))
            {
                foreach (var arg in named.TypeArguments)
                {
                    var m = Map(arg, generics);
                    if (m == null || !m.IsIdentity)
                        return null;
                }
                return Identity(Fq(named));
            }
            return null;
        }

        static TypeMap Identity(string name) => new TypeMap { Client = name, Wire = name, Server = name, IsIdentity = true };

        static readonly System.Text.RegularExpressions.Regex SimpleName =
            new System.Text.RegularExpressions.Regex(@"^@?[A-Za-z_][A-Za-z0-9_]*$");

        // A shared struct as an object[] of its public instance fields. Every field must itself cross unchanged
        // (primitives, VRageMath values,...); otherwise the struct is not mappable and members using it are skipped.
        TypeMap MapSharedStruct(INamedTypeSymbol named, GenericContext generics)
        {
            var fields = new List<IFieldSymbol>();
            foreach (var member in named.GetMembers())
            {
                var field = member as IFieldSymbol;
                if (field == null || field.IsStatic || field.IsConst)
                    continue;
                if (field.IsImplicitlyDeclared || field.IsReadOnly || field.DeclaredAccessibility != Accessibility.Public)
                {
                    _report.AppendLine("// skipped shared struct with non-public, readonly or auto-property state: " + named.ToDisplayString());
                    return null;
                }
                var map = Map(field.Type, generics);
                if (map == null || !map.IsIdentity)
                {
                    _report.AppendLine("// skipped shared struct with a field that does not cross unchanged: " + named.ToDisplayString() + "." + field.Name);
                    return null;
                }
                fields.Add(field);
            }

            string fq = Fq(named);
            Func<string, string> toWire = e =>
            {
                Func<string, string> build = v => "new object[] { " + string.Join(", ", fields.Select(f => "(object)(" + v + ")." + f.Name)) + " }";
                if (SimpleName.IsMatch(e))
                    return build(e);
                string v2 = NextLambdaVar();
                return "((global::System.Func<" + fq + ", object[]>)(" + v2 + " => " + build(v2) + "))(" + e + ")";
            };
            Func<string, string> fromWire = e =>
            {
                Func<string, string> build = w => "new " + fq + " { " +
                    string.Join(", ", fields.Select((f, i) => f.Name + " = (" + Fq(f.Type) + ")(" + w + ")[" + i + "]")) + " }";
                if (SimpleName.IsMatch(e))
                    return build(e);
                string w2 = NextLambdaVar();
                return "((global::System.Func<object[], " + fq + ">)(" + w2 + " => " + build(w2) + "))(" + e + ")";
            };
            return new TypeMap
            {
                Client = fq, Wire = "object[]", Server = fq,
                C2W = toWire, W2C = fromWire, SFrom = fromWire, STo = toWire,
            };
        }

        bool IsAcrux(INamedTypeSymbol type)
        {
            string ns = type.ContainingNamespace?.ToDisplayString() ?? string.Empty;
            return InMirroredNamespace(ns);
        }

        bool IsPass(INamedTypeSymbol type)
        {
            if (type.SpecialType != SpecialType.None)
                return true;
            string ns = type.ContainingNamespace?.ToDisplayString() ?? string.Empty;
            if (ns.StartsWith("Adk.", StringComparison.Ordinal) || InMirroredNamespace(ns))
                return false;
            if (ns == "System.Collections.Generic" || ns == "System.Collections")
                return true;
            if (PassNamespaces.Contains(ns))
                return type.TypeKind != TypeKind.Delegate || true;
            return ns.StartsWith("VRage", StringComparison.Ordinal) || ns.StartsWith("Sandbox", StringComparison.Ordinal);
        }

        TypeMap MapTypeParameter(ITypeParameterSymbol tp, GenericContext generics)
        {
            ITypeSymbol constraint = tp.ConstraintTypes.FirstOrDefault(c => c.TypeKind == TypeKind.Class);
            if (tp.HasValueTypeConstraint || tp.HasUnmanagedTypeConstraint)
                return null;
            string name = tp.Name;
            if (constraint != null)
            {
                var cm = Map(constraint, generics);
                if (cm == null)
                    return null;
                generics.ServerArgs[tp] = constraint;
                string server = cm.Server;
                if (cm.Wire == "object" && !cm.IsIdentity)
                    return new TypeMap
                    {
                        Client = name, Wire = "object", Server = server,
                        C2W = e => ClientRuntime + ".Ui.Native(" + e + ")",
                        W2C = e => ClientRuntime + ".Ui.Wrap<" + name + ">(" + e + ")",
                        SFrom = e => "((" + server + ")(" + e + "))",
                        STo = e => "((object)(" + e + "))",
                    };
                return null;
            }
            generics.ServerArgs[tp] = _compilation.GetSpecialType(SpecialType.System_Object);
            return new TypeMap
            {
                Client = name, Wire = "object", Server = "object",
                C2W = e => ClientRuntime + ".Ui.ToWire(" + e + ")",
                W2C = e => ClientRuntime + ".Ui.FromWire<" + name + ">(" + e + ")",
                SFrom = e => ServerRuntime + ".ServerUi.FromWire(" + e + ")",
                STo = e => ServerRuntime + ".ServerUi.ToWire(" + e + ")",
            };
        }

        TypeMap MapDelegate(INamedTypeSymbol named, GenericContext generics)
        {
            bool isFunc = named.Name == "Func";
            var args = new List<TypeMap>();
            foreach (var a in named.TypeArguments)
            {
                var m = Map(a, generics);
                if (m == null)
                    return null;
                args.Add(m);
            }
            if (args.All(a => a.IsIdentity))
                return Identity(Fq(named));

            string Join(Func<TypeMap, string> f) => string.Join(", ", args.Select(f));
            string client = "global::System." + named.Name + (args.Count > 0 ? "<" + Join(a => a.Client) + ">" : "");
            string wire = "global::System." + named.Name + (args.Count > 0 ? "<" + Join(a => a.Wire) + ">" : "");
            string server = "global::System." + named.Name + (args.Count > 0 ? "<" + Join(a => a.Server) + ">" : "");
            int parameterCount = isFunc ? args.Count - 1 : args.Count;

            // Build an adapter: given expression of delegate type 'from', produce delegate of type 'to'.
            string Adapt(string expr, string fromType, string toType, Func<TypeMap, Func<string, string>> argConv,
                Func<TypeMap, Func<string, string>> retConv)
            {
                int id = ++_lambda;
                string d = "__d" + id;
                var ps = Enumerable.Range(0, parameterCount).Select(i => "__p" + id + "_" + i).ToList();
                var callArgs = Enumerable.Range(0, parameterCount).Select(i => argConv(args[i])(ps[i]));
                string call = d + "(" + string.Join(", ", callArgs) + ")";
                string body = isFunc ? retConv(args[args.Count - 1])(call) : call;
                return "((global::System.Func<" + fromType + ", " + toType + ">)(" + d + " => " + d + " == null ? null : new " +
                       toType + "((" + string.Join(", ", ps) + ") => " + body + ")))(" + expr + ")";
            }

            return new TypeMap
            {
                Client = client, Wire = wire, Server = server,
                // client delegate -> wire delegate: wire params are converted to client, client result to wire.
                C2W = e => Adapt(e, client, wire, a => a.W2C, a => a.C2W),
                W2C = e => Adapt(e, wire, client, a => a.C2W, a => a.W2C),
                SFrom = e => Adapt(e, wire, server, a => a.STo, a => a.SFrom),
                STo = e => Adapt(e, server, wire, a => a.SFrom, a => a.STo),
            };
        }

        TypeMap MapAcruxDelegate(INamedTypeSymbol named, GenericContext generics)
        {
            var invoke = named.DelegateInvokeMethod;
            if (invoke == null || invoke.Parameters.Any(p => p.RefKind != RefKind.None))
                return null;
            var ps = new List<TypeMap>();
            foreach (var p in invoke.Parameters)
            {
                var m = Map(p.Type, generics);
                if (m == null)
                    return null;
                ps.Add(m);
            }
            var ret = Map(invoke.ReturnType, generics);
            if (ret == null)
                return null;
            bool isVoid = ret.Wire == "void";
            var clientArgs = new List<string>();
            var serverArgs = new List<string>();
            foreach (var arg in named.TypeArguments)
            {
                var m = Map(arg, generics);
                if (m == null)
                    return null;
                clientArgs.Add(m.Client);
                serverArgs.Add(m.Server);
            }
            string client = GenericName(named, clientArgs);
            string server = GenericName(named, serverArgs);
            var wireArgs = ps.Select(p => p.Wire).ToList();
            if (!isVoid) wireArgs.Add(ret.Wire);
            string wire = isVoid
                ? (wireArgs.Count == 0 ? "global::System.Action" : "global::System.Action<" + string.Join(", ", wireArgs) + ">")
                : "global::System.Func<" + string.Join(", ", wireArgs) + ">";

            string Adapt(string expr, string fromType, string toType, Func<TypeMap, Func<string, string>> argConv, Func<TypeMap, Func<string, string>> retConv)
            {
                int id = ++_lambda;
                string d = "__d" + id;
                var names = Enumerable.Range(0, ps.Count).Select(i => "__p" + id + "_" + i).ToList();
                string call = d + "(" + string.Join(", ", Enumerable.Range(0, ps.Count).Select(i => argConv(ps[i])(names[i]))) + ")";
                string body = isVoid ? call : retConv(ret)(call);
                return "((global::System.Func<" + fromType + ", " + toType + ">)(" + d + " => " + d + " == null ? null : new " +
                       toType + "((" + string.Join(", ", names) + ") => " + body + ")))(" + expr + ")";
            }

            return new TypeMap
            {
                Client = client, Wire = wire, Server = server,
                C2W = e => Adapt(e, client, wire, a => a.W2C, a => a.C2W),
                W2C = e => Adapt(e, wire, client, a => a.C2W, a => a.W2C),
                SFrom = e => Adapt(e, wire, server, a => a.STo, a => a.SFrom),
                STo = e => Adapt(e, server, wire, a => a.SFrom, a => a.STo),
            };
        }

        static string GenericName(INamedTypeSymbol named, List<string> args)
        {
            string baseName = "global::" + named.OriginalDefinition.ContainingNamespace.ToDisplayString() + "." + named.Name;
            return args.Count == 0 ? baseName : baseName + "<" + string.Join(", ", args) + ">";
        }

        // ---------------------------------------------------------------- per type emission

        static string Sanitize(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s)
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            return sb.ToString();
        }

        static bool IsAccessible(ISymbol s) =>
            s.DeclaredAccessibility == Accessibility.Public ||
            s.DeclaredAccessibility == Accessibility.Protected ||
            s.DeclaredAccessibility == Accessibility.Internal ||
            s.DeclaredAccessibility == Accessibility.ProtectedOrInternal ||
            s.DeclaredAccessibility == Accessibility.ProtectedAndInternal;

        static bool IsProtected(ISymbol s) =>
            s.DeclaredAccessibility == Accessibility.Protected ||
            s.DeclaredAccessibility == Accessibility.ProtectedAndInternal;

        static string AccessText(ISymbol s)
        {
            switch (s.DeclaredAccessibility)
            {
                case Accessibility.Public: return "public";
                case Accessibility.Protected: return "protected";
                case Accessibility.Internal: return "internal";
                case Accessibility.ProtectedOrInternal: return "protected internal";
                case Accessibility.ProtectedAndInternal: return "private protected";
                default: return "private";
            }
        }

        bool IsManual(ISymbol member) =>
            _manual.Contains(member.ContainingType.OriginalDefinition.ToDisplayString().Split('<')[0] + "." + member.Name);

        static bool IsRootVirtualAbstract(IMethodSymbol m)
        {
            var root = m;
            while (root.OverriddenMethod != null)
                root = root.OverriddenMethod;
            return root.IsAbstract;
        }

        static bool IsRootVirtualAbstract(IPropertySymbol p)
        {
            var root = p;
            while (root.OverriddenProperty != null)
                root = root.OverriddenProperty;
            return root.IsAbstract;
        }

        INamedTypeSymbol MirroredBase(INamedTypeSymbol type)
        {
            var b = type.BaseType;
            if (b != null && _mirrored.Contains(b.OriginalDefinition))
                return b;
            return null;
        }

        bool CanRemote(INamedTypeSymbol type) =>
            type.TypeKind == TypeKind.Class && !type.IsSealed && !type.IsStatic &&
            type.InstanceConstructors.Any(c => IsAccessible(c) && c.DeclaredAccessibility != Accessibility.Private) &&
            !HasUnimplementedAbstract(type);

        static bool HasUnimplementedAbstract(INamedTypeSymbol type)
        {
            var implemented = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            for (var t = type; t != null; t = t.BaseType)
            {
                foreach (var member in t.GetMembers())
                {
                    if (member.IsAbstract && !implemented.Contains(member.OriginalDefinition))
                        return true;
                    ISymbol overridden = (member as IMethodSymbol)?.OverriddenMethod ?? (ISymbol)(member as IPropertySymbol)?.OverriddenProperty ?? (member as IEventSymbol)?.OverriddenEvent;
                    for (; overridden != null; overridden = (overridden as IMethodSymbol)?.OverriddenMethod ?? (ISymbol)(overridden as IPropertySymbol)?.OverriddenProperty ?? (overridden as IEventSymbol)?.OverriddenEvent)
                        if (!member.IsAbstract)
                            implemented.Add(overridden.OriginalDefinition);
                }
            }
            return false;
        }

        void EmitType(INamedTypeSymbol type)
        {
            if (type.TypeKind == TypeKind.Enum)
            {
                EmitEnum(type);
                return;
            }
            if (type.TypeKind == TypeKind.Delegate)
            {
                EmitDelegate(type);
                return;
            }

            string fullName = type.ToDisplayString();
            string key = Sanitize(fullName);
            string bridge = "__UiBridge_" + key;
            string ns = type.ContainingNamespace.ToDisplayString();
            var generics = new GenericContext();
            foreach (var tp in type.TypeParameters)
                MapTypeParameter(tp, generics);

            var typeParamNames = type.TypeParameters.Select(t => t.Name).ToList();
            var placeholderArgs = type.TypeParameters.Select(t =>
            {
                var c = t.ConstraintTypes.FirstOrDefault(x => x.TypeKind == TypeKind.Class);
                var cm = c == null ? null : Map(c, generics);
                return cm == null ? "object" : cm.Client;
            }).ToList();
            string clientTypeName = type.Name + (typeParamNames.Count > 0 ? "<" + string.Join(", ", typeParamNames) + ">" : "");
            string serverTypeArgs = type.TypeParameters.Length == 0
                ? string.Empty
                : "<" + string.Join(", ", type.TypeParameters.Select(t => Fq(generics.ServerArgs[t]))) + ">";
            string serverType = "global::" + fullName.Split('<')[0] + serverTypeArgs;
            string clientSelf = "global::" + ns + "." + clientTypeName;
            bool isInterface = type.TypeKind == TypeKind.Interface;
            bool isStatic = type.IsStatic;
            bool canRemote = CanRemote(type);
            string remoteName = "Remote_" + key;
            string remoteType = ServerRuntime + "." + remoteName;

            var client = new StringBuilder();
            var clientBridgeFields = new StringBuilder();
            var clientBridgeLoad = new StringBuilder();
            var server = new StringBuilder();
            var remote = new StringBuilder();
            var remoteInterfaces = new List<string>();
            var clientHooks = new StringBuilder();
            var exportDelegates = new List<string>();
            var implBody = new StringBuilder();
            var implemented = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            int index = 0;

            string Field(string wireDelegate, string id)
            {
                string name = "m" + (index++);
                clientBridgeFields.Append("        internal static ").Append(wireDelegate).Append(' ').Append(name).AppendLine(";");
                clientBridgeLoad.Append("            ").Append(name).Append(" = ").Append(ClientRuntime)
                    .Append(".Ui.Get<").Append(wireDelegate).Append(">(api, ").Append(Literal(id)).AppendLine(");");
                return bridge + "." + name;
            }

            void Server(string id, string wireDelegate, string lambda)
            {
                server.Append("            api[").Append(Literal(id)).Append("] = new ").Append(wireDelegate).Append('(')
                    .Append(lambda).AppendLine(");");
            }

            // ---- base type and header
            var mirroredBase = MirroredBase(type);
            var listBase = type.BaseType != null && type.BaseType.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.List<T>"
                ? Map(type.BaseType.TypeArguments[0], generics)
                : null;
            string baseClause;
            if (isInterface)
                baseClause = "";
            else if (mirroredBase != null)
            {
                var bm = Map(mirroredBase, generics);
                baseClause = " : " + (bm != null ? bm.Client : ClientRuntime + ".ToolkitObject");
            }
            else
                baseClause = isStatic ? "" : " : " + ClientRuntime + ".ToolkitObject";

            var interfaces = new List<string>();
            if (!isStatic)
            {
                foreach (var iface in type.Interfaces)
                {
                    var im = Map(iface, generics);
                    if (im != null && _mirrored.Contains(iface.OriginalDefinition))
                        interfaces.Add(im.Client);
                    else if (iface.ToDisplayString() == "System.IDisposable")
                        interfaces.Add("global::System.IDisposable");
                }
            }
            if (listBase != null)
                interfaces.Add("global::System.Collections.Generic.IEnumerable<" + listBase.Client + ">");
            if (interfaces.Count > 0)
                baseClause += (baseClause.Length == 0 ? " : " : ", ") + string.Join(", ", interfaces);
            if (listBase != null)
                EmitListMembers(listBase);

            string constraints = string.Join(" ", type.TypeParameters.Select(ConstraintClause).Where(c => c.Length > 0));

            // ---- members
            var emittedSignatures = new HashSet<string>(StringComparer.Ordinal);
            bool hasDeferredCtor = false;

            foreach (var member in type.GetMembers())
            {
                if (!IsAccessible(member) || (member.IsImplicitlyDeclared && !(member is IMethodSymbol ctorSymbol && ctorSymbol.MethodKind == MethodKind.Constructor)))
                    continue;
                if (member is INamedTypeSymbol)
                    continue;
                if (IsManual(member))
                {
                    _report.AppendLine("// manual: " + member.ToDisplayString());
                    continue;
                }
                if (member.GetAttributes().Any(a => a.AttributeClass?.Name == "ObsoleteAttribute"))
                    continue;
                try
                {
                    switch (member)
                    {
                        case IMethodSymbol m when m.MethodKind == MethodKind.Constructor:
                            EmitConstructor(m);
                            break;
                        case IMethodSymbol m when m.MethodKind == MethodKind.Ordinary:
                            EmitMethod(m);
                            break;
                        case IMethodSymbol m when m.MethodKind == MethodKind.Conversion:
                            EmitConversion(m);
                            break;
                        case IPropertySymbol p when !p.IsIndexer:
                            EmitProperty(p);
                            break;
                        case IPropertySymbol p when p.IsIndexer:
                            EmitIndexer(p);
                            break;
                        case IFieldSymbol f:
                            EmitField(f);
                            break;
                        case IEventSymbol e:
                            EmitEvent(e);
                            break;
                    }
                }
                catch (SkipException skip)
                {
                    _report.AppendLine("// skipped " + member.ToDisplayString() + ": " + skip.Message);
                }
            }

            if (!isInterface && !isStatic && !hasDeferredCtor)
            {
                // Wrapping constructor chain used by factories and derived wrappers.
            }

            // ---- assemble client
            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated/>");
            sb.Append("namespace ").AppendLine(ns);
            sb.AppendLine("{");
            if (isInterface)
            {
                sb.Append("    public partial interface ").Append(clientTypeName).Append(baseClause).Append(' ').AppendLine(constraints);
                sb.AppendLine("    {");
                sb.Append(client);
                sb.AppendLine("    }");
                // proxy for toolkit objects that only expose the interface
                sb.Append("    internal sealed class __Proxy_").Append(key).Append(typeParamNames.Count > 0 ? "<" + string.Join(", ", typeParamNames) + ">" : "")
                    .Append(" : ").Append(ClientRuntime).Append(".ToolkitObject, ").Append(clientSelf).Append(' ').AppendLine(constraints);
                sb.AppendLine("    {");
                sb.Append("        internal __Proxy_").Append(key).Append("(object native) : base(").Append(ClientRuntime).AppendLine(".Deferred.Value) { __AttachNative(native); }");
                sb.Append("        internal __Proxy_").Append(key).Append("(object native, bool detached) : base(").Append(ClientRuntime).AppendLine(".Deferred.Value) { __AttachDetached(native); }");
                sb.Append(_proxyBody);
                sb.Append(ExplicitInterfaceImpls(type, type.AllInterfaces, generics));
                sb.AppendLine("    }");
            }
            else
            {
                string modifiers = isStatic ? "static " : type.IsAbstract ? "abstract " : type.IsSealed ? "sealed " : "";
                sb.Append("    public ").Append(modifiers).Append("partial class ").Append(clientTypeName).Append(baseClause).Append(' ').AppendLine(constraints);
                sb.AppendLine("    {");
                if (!isStatic)
                {
                    sb.Append("        ").Append(type.IsSealed ? "internal" : "protected internal").Append(' ').Append(type.Name)
                        .Append('(').Append(ClientRuntime).Append(".Deferred deferred) : base(deferred) { }").AppendLine();
                }
                sb.Append(client);
                sb.Append(clientHooks);
                if (!isStatic)
                {
                    var inherited = mirroredBase == null ? new List<INamedTypeSymbol>() : mirroredBase.AllInterfaces.ToList();
                    var own = type.AllInterfaces.Where(i => !inherited.Any(b => SymbolEqualityComparer.Default.Equals(b, i))).ToImmutableArray();
                    sb.Append(ExplicitInterfaceImpls(type, own, generics));
                }
                sb.AppendLine("    }");
            }

            sb.Append("    internal static class ").AppendLine(bridge);
            sb.AppendLine("    {");
            sb.Append(clientBridgeFields);
            if (isInterface)
            {
                bool disposable = type.AllInterfaces.Any(i => i.ToDisplayString() == "System.IDisposable");
                sb.Append("        internal static object Export").Append(typeParamNames.Count > 0 ? "<" + string.Join(", ", typeParamNames) + ">" : "")
                    .Append('(').Append(clientSelf).Append(" __impl) ").AppendLine(constraints);
                sb.AppendLine("        {");
                sb.AppendLine("            if (__impl == null) return null;");
                sb.Append("            var __toolkit = __impl as ").Append(ClientRuntime).AppendLine(".IToolkitObject;");
                sb.AppendLine("            if (__toolkit != null) return __toolkit.Native;");
                sb.Append("            return new object[] { \"acrux.iface\", __impl, ")
                    .Append(disposable ? "new global::System.Action(__impl.Dispose)" : "null");
                foreach (var d in exportDelegates)
                    sb.Append(", ").Append(d);
                sb.AppendLine(" };");
                sb.AppendLine("        }");
            }
            sb.AppendLine("        internal static void Load(global::System.Collections.Generic.IDictionary<string, global::System.Delegate> api)");
            sb.AppendLine("        {");
            sb.Append(clientBridgeLoad);
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            _output.AddSource("Client." + fullName.Replace('<', '_').Replace('>', '_').Replace(", ", "_") + ".cs", ClientGuard(sb.ToString()));

            _clientLoads.Add("global::" + ns + "." + bridge + ".Load(api);");

            // factory for toolkit objects whose nearest mirrored type is this one
            string factoryClient = typeParamNames.Count == 0 ? clientSelf : "global::" + ns + "." + type.Name + "<" + string.Join(", ", placeholderArgs) + ">";
            if (isInterface)
                _factories.Add(ClientRuntime + ".Ui.RegisterFactory(" + Literal(fullName) + ", native => new global::" + ns + ".__Proxy_" + key +
                               (typeParamNames.Count > 0 ? "<" + string.Join(", ", placeholderArgs) + ">" : "") + "(native));");
            else if (!isStatic && !type.IsAbstract)
                _factories.Add(ClientRuntime + ".Ui.RegisterFactory(" + Literal(fullName) + ", native => { var w = new " + factoryClient + "(" + ClientRuntime +
                               ".Deferred.Value); w.__AttachNative(native); return w; });");

            // ---- assemble server
            var ss = new StringBuilder();
            ss.AppendLine("// <auto-generated/>");
            ss.AppendLine("namespace Acrux.Toolkit.Server");
            ss.AppendLine("{");
            ss.AppendLine("    static partial class AcruxUiToolkitBridge");
            ss.AppendLine("    {");
            ss.Append("        static void Register_").Append(key).AppendLine("(global::System.Collections.Generic.Dictionary<string, global::System.Delegate> api)");
            ss.AppendLine("        {");
            ss.Append(server);
            ss.AppendLine("        }");
            ss.AppendLine("    }");
            if (isInterface)
            {
                ss.Append("    public sealed class ClientImpl_").Append(key).Append(" : ").Append(serverType).Append(", ").Append(ServerRuntime).AppendLine(".IClientImpl");
                ss.AppendLine("    {");
                ss.AppendLine("        readonly object[] _b;");
                ss.Append("        public ClientImpl_").Append(key).AppendLine("(object[] bundle) { _b = bundle; }");
                ss.AppendLine("        public object Bundle { get { return _b; } }");
                ss.Append(implBody);
                ss.Append(ThrowingImpls(type, implemented, generics));
                ss.AppendLine("    }");
            }
            if (canRemote)
            {
                ss.Append("    public sealed partial class ").Append(remoteName).Append(" : ").Append(serverType).Append(", ").Append(ServerRuntime).AppendLine(".IRemotePeer");
                ss.AppendLine("    {");
                ss.AppendLine("        public object RemotePeer { get; private set; }");
                ss.Append(remote);
                ss.AppendLine("    }");
            }
            ss.AppendLine("}");
            _output.AddSource("Server." + key + ".g.cs", ss.ToString());
            _serverRegistrations.Add("Register_" + key + "(api);");
            return;

            // ------------------------------------------------------------ local emitters

            void EmitConstructor(IMethodSymbol ctor)
            {
                if (isStatic || isInterface)
                    return;
                if (ctor.Parameters.Length == 1 && ctor.Parameters[0].Type.Name == "Deferred")
                    return;
                var ps = MapParameters(ctor.Parameters, generics);
                string sig = "ctor(" + string.Join(",", ps.Select(p => p.Map.Wire)) + ")";
                if (!emittedSignatures.Add(sig))
                    throw new SkipException("duplicate wire signature");
                string id = fullName + "." + sig;
                bool publicCreatable = !type.IsAbstract && (ctor.DeclaredAccessibility == Accessibility.Public || ctor.DeclaredAccessibility == Accessibility.Internal || ctor.DeclaredAccessibility == Accessibility.ProtectedOrInternal);
                string wireArgs = string.Join(", ", ps.Select(p => p.Map.Wire));
                string createField = null, remoteField = null;
                if (publicCreatable)
                {
                    string del = ps.Count == 0 ? "global::System.Func<object>" : "global::System.Func<" + wireArgs + ", object>";
                    createField = Field(del, id);
                    Server(id, del, "(" + string.Join(", ", ps.Select(p => p.WireName)) + ") => new " + serverType + "(" +
                                    string.Join(", ", ps.Select(p => p.Map.SFrom(p.WireName))) + ")");
                }
                if (canRemote)
                {
                    if (ps.Count + 1 > 16)
                        throw new SkipException("too many parameters");
                    string del = "global::System.Func<object" + (ps.Count > 0 ? ", " + wireArgs : "") + ", object>";
                    remoteField = Field(del, id + "#remote");
                    Server(id + "#remote", del, "(__peer" + (ps.Count > 0 ? ", " : "") + string.Join(", ", ps.Select(p => p.WireName)) + ") => new " + remoteType + "(__peer" +
                                                (ps.Count > 0 ? ", " : "") + string.Join(", ", ps.Select(p => p.Map.SFrom(p.WireName))) + ")");
                    remote.Append("        public ").Append(remoteName).Append("(object peer").Append(ps.Count > 0 ? ", " : "")
                        .Append(string.Join(", ", ps.Select(p => p.ServerDecl))).Append(") : base(")
                        .Append(string.Join(", ", ps.Select(p => p.Name))).AppendLine(") { RemotePeer = peer; }");
                }
                if (createField == null && remoteField == null)
                    throw new SkipException("not constructible");
                string args = string.Join(", ", ps.Select(p => p.Map.C2W(p.Name)));
                client.Append("        ").Append(AccessText(ctor)).Append(' ').Append(type.Name).Append('(')
                    .Append(string.Join(", ", ps.Select(p => p.ClientDecl))).Append(") : base(").Append(ClientRuntime).AppendLine(".Deferred.Value)");
                client.AppendLine("        {");
                if (createField != null && remoteField != null)
                    client.Append("            __AttachNative(GetType() == typeof(").Append(clientSelf).Append(") ? ").Append(createField).Append('(').Append(args)
                        .Append(") : ").Append(remoteField).Append("(this").Append(args.Length > 0 ? ", " + args : "").AppendLine("));");
                else if (createField != null)
                    client.Append("            __AttachNative(").Append(createField).Append('(').Append(args).AppendLine("));");
                else
                    client.Append("            __AttachNative(").Append(remoteField).Append("(this").Append(args.Length > 0 ? ", " + args : "").AppendLine("));");
                client.AppendLine("        }");
            }

            void EmitMethod(IMethodSymbol m)
            {
                if (m.IsOverride && !(IsRootVirtualAbstract(m) && !type.IsAbstract && !isInterface))
                    return;
                if (m.Name == "ToString" || m.Name == "GetHashCode" || m.Name == "Equals" && m.Parameters.Length == 1 && m.Parameters[0].Type.SpecialType == SpecialType.System_Object)
                    return;
                if (m.ReturnsByRef || m.ReturnsByRefReadonly)
                    throw new SkipException("ref return");
                var methodGenerics = new GenericContext();
                foreach (var kv in generics.ServerArgs)
                    methodGenerics.ServerArgs[kv.Key] = kv.Value;
                foreach (var tp in m.TypeParameters)
                    if (MapTypeParameter(tp, methodGenerics) == null)
                        throw new SkipException("unsupported type parameter");
                var ps = MapParameters(m.Parameters, methodGenerics);
                var ret = Map(m.ReturnType, methodGenerics);
                if (ret == null)
                    throw new SkipException("return type " + m.ReturnType.ToDisplayString());
                if (ps.Any(p => p.RefKind == RefKind.Out))
                {
                    EmitOutMethod(m, ps, ret, methodGenerics);
                    return;
                }
                bool isVoid = ret.Wire == "void";
                bool instance = !m.IsStatic;
                int arity = ps.Count + (instance ? 1 : 0);
                if (arity > 16)
                    throw new SkipException("too many parameters");
                string sig = m.Name + "`" + m.TypeParameters.Length + "(" + string.Join(",", ps.Select(p => p.Map.Wire)) + ")" + (m.IsStatic ? "s" : "");
                if (!emittedSignatures.Add(sig))
                    throw new SkipException("duplicate wire signature");
                string id = fullName + "." + sig;

                var wireTypes = new List<string>();
                if (instance) wireTypes.Add("object");
                wireTypes.AddRange(ps.Select(p => p.Map.Wire));
                string del = isVoid
                    ? (wireTypes.Count == 0 ? "global::System.Action" : "global::System.Action<" + string.Join(", ", wireTypes) + ">")
                    : "global::System.Func<" + string.Join(", ", wireTypes.Concat(new[] { ret.Wire })) + ">";

                bool isProtected = IsProtected(m);
                // Hooks exist for every overridable member of an unsealed class: a remotable subclass overrides it.
                bool hookable = type.TypeKind == TypeKind.Class && !type.IsSealed && !type.IsStatic &&
                                (m.IsVirtual || m.IsAbstract || m.IsOverride) && !m.IsSealed && instance && !isInterface;
                string serverTypeArgsM = m.TypeParameters.Length == 0 ? "" : "<" + string.Join(", ", m.TypeParameters.Select(t => Fq(methodGenerics.ServerArgs[t]))) + ">";
                string serverArgs = string.Join(", ", ps.Select(p => p.ServerArg));
                string lambdaParams = string.Join(", ", (instance ? new[] { "__o" } : new string[0]).Concat(ps.Select(p => p.WireName)));
                string declaringServer = isInterface ? serverType : serverType;

                string invoke;
                string baseIface = null;
                if (isProtected || hookable || (m.IsAbstract && !isInterface))
                {
                    baseIface = DeclareBaseInterface(key, m, ps, ret, serverTypeArgsM, methodGenerics);
                    remoteInterfaces.Add(baseIface);
                    string viaBase = "((" + baseIface + ")__o).Base_" + m.Name + serverTypeArgsM + "(" + serverArgs + ")";
                    string direct = "((" + declaringServer + ")__o)." + m.Name + serverTypeArgsM + "(" + serverArgs + ")";
                    if (isProtected)
                        invoke = viaBase;
                    else if (isVoid)
                        invoke = "{ if (__o is " + baseIface + ") " + viaBase + "; else " + direct + "; }";
                    else
                        invoke = "(__o is " + baseIface + " ? " + viaBase + " : " + direct + ")";
                }
                else if (instance)
                    invoke = "((" + declaringServer + ")__o)." + m.Name + serverTypeArgsM + "(" + serverArgs + ")";
                else
                    invoke = serverType + "." + m.Name + serverTypeArgsM + "(" + serverArgs + ")";

                string serverBody = PrepareOutParams(ps) + (isVoid ? invoke : ret.STo(invoke));
                Server(id, del, "(" + lambdaParams + ") => " + serverBody);

                string field = Field(del, id);
                string clientArgs = string.Join(", ", (instance ? new[] { "Native" } : new string[0]).Concat(ps.Select(p => p.Map.C2W(p.Name))));
                string callExpr = field + "(" + clientArgs + ")";
                string body = isVoid ? callExpr + ";" : "return " + ret.W2C(callExpr) + ";";
                string typeParams = m.TypeParameters.Length == 0 ? "" : "<" + string.Join(", ", m.TypeParameters.Select(t => t.Name)) + ">";
                string mConstraints = string.Join(" ", m.TypeParameters.Select(ConstraintClause).Where(c => c.Length > 0));
                string paramDecl = string.Join(", ", ps.Select((p, i) => (i == 0 && m.IsExtensionMethod ? "this " : "") + p.ClientDecl));

                if (isInterface)
                {
                    client.Append("        ").Append(ret.Client).Append(' ').Append(m.Name).Append(typeParams).Append('(').Append(paramDecl).Append(") ")
                        .Append(mConstraints).AppendLine(";");
                    _proxyBodyBuilder.Append("        public ").Append(ret.Client).Append(' ').Append(m.Name).Append(typeParams).Append('(').Append(paramDecl).Append(") ")
                        .Append(OverrideConstraints(m)).Append("{ ").Append(body).AppendLine(" }");
                    if (m.TypeParameters.Length == 0)
                    {
                        var exportTypes = ps.Select(p => p.Map.Wire).ToList();
                        string exportDel = isVoid
                            ? (exportTypes.Count == 0 ? "global::System.Action" : "global::System.Action<" + string.Join(", ", exportTypes) + ">")
                            : "global::System.Func<" + string.Join(", ", exportTypes.Concat(new[] { ret.Wire })) + ">";
                        string implCall = "__impl." + m.Name + "(" + string.Join(", ", ps.Select(p => p.Map.W2C(p.WireName))) + ")";
                        exportDelegates.Add("new " + exportDel + "((" + string.Join(", ", ps.Select(p => p.WireName)) + ") => " + (isVoid ? implCall : ret.C2W(implCall)) + ")");
                        string slot = "((" + exportDel + ")_b[" + (exportDelegates.Count + 2) + "])(" + string.Join(", ", ps.Select(p => p.Map.STo(p.Name))) + ")";
                        implBody.Append("        public ").Append(ret.Server).Append(' ').Append(m.Name).Append('(').Append(string.Join(", ", ps.Select(p => p.ServerDecl)))
                            .Append(") { ").Append(isVoid ? slot + ";" : "return " + ret.SFrom(slot) + ";").AppendLine(" }");
                        implemented.Add(m);
                    }
                    return;
                }

                string modifiers = HidesBase(m) + (m.IsStatic ? "static " :
                    m.IsOverride ? "override " :
                    m.IsAbstract ? (type.IsAbstract ? "abstract " : "virtual ") :
                    (m.IsVirtual && !type.IsSealed) ? "virtual " : "");
                client.Append("        ").Append(AccessText(m)).Append(' ').Append(modifiers).Append(ret.Client).Append(' ').Append(m.Name).Append(typeParams)
                    .Append('(').Append(paramDecl).Append(')');
                if (!m.IsOverride)
                    client.Append(' ').Append(mConstraints);
                if (modifiers.EndsWith("abstract ", StringComparison.Ordinal))
                {
                    _clientAbstractMembers.Add(type.OriginalDefinition);
                    client.AppendLine(";");
                }
                else
                    client.Append(" { ").Append(body).AppendLine(" }");

                if (hookable && m.TypeParameters.Length == 0)
                    EmitHook(m, ps, ret, baseIface);
            }

            void EmitListMembers(TypeMap item)
            {
                string list = "((global::System.Collections.IList)__o)";
                string add = Field("global::System.Action<object, " + item.Wire + ">", fullName + ".List.Add");
                Server(fullName + ".List.Add", "global::System.Action<object, " + item.Wire + ">", "(__o, __v) => " + list + ".Add(" + item.SFrom("__v") + ")");
                string count = Field("global::System.Func<object, int>", fullName + ".List.Count");
                Server(fullName + ".List.Count", "global::System.Func<object, int>", "__o => " + list + ".Count");
                string get = Field("global::System.Func<object, int, " + item.Wire + ">", fullName + ".List.Get");
                Server(fullName + ".List.Get", "global::System.Func<object, int, " + item.Wire + ">", "(__o, __i) => " + item.STo("(" + item.Server + ")" + list + "[__i]"));
                string set = Field("global::System.Action<object, int, " + item.Wire + ">", fullName + ".List.Set");
                Server(fullName + ".List.Set", "global::System.Action<object, int, " + item.Wire + ">", "(__o, __i, __v) => " + list + "[__i] = " + item.SFrom("__v"));
                string insert = Field("global::System.Action<object, int, " + item.Wire + ">", fullName + ".List.Insert");
                Server(fullName + ".List.Insert", "global::System.Action<object, int, " + item.Wire + ">", "(__o, __i, __v) => " + list + ".Insert(__i, " + item.SFrom("__v") + ")");
                string removeAt = Field("global::System.Action<object, int>", fullName + ".List.RemoveAt");
                Server(fullName + ".List.RemoveAt", "global::System.Action<object, int>", "(__o, __i) => " + list + ".RemoveAt(__i)");
                string clear = Field("global::System.Action<object>", fullName + ".List.Clear");
                Server(fullName + ".List.Clear", "global::System.Action<object>", "__o => " + list + ".Clear()");
                string c = item.Client;
                client.Append("        public void Add(").Append(c).Append(" item) { ").Append(add).Append("(Native, ").Append(item.C2W("item")).AppendLine("); }");
                client.Append("        public int Count { get { return ").Append(count).AppendLine("(Native); } }");
                client.Append("        public ").Append(c).Append(" this[int index] { get { return ").Append(item.W2C(get + "(Native, index)")).Append("; } set { ")
                    .Append(set).Append("(Native, index, ").Append(item.C2W("value")).AppendLine("); } }");
                client.Append("        public void Insert(int index, ").Append(c).Append(" item) { ").Append(insert).Append("(Native, index, ").Append(item.C2W("item")).AppendLine("); }");
                client.Append("        public void RemoveAt(int index) { ").Append(removeAt).AppendLine("(Native, index); }");
                client.Append("        public void Clear() { ").Append(clear).AppendLine("(Native); }");
                client.Append("        public int IndexOf(").Append(c).AppendLine(" item) { for (int i = 0; i < Count; i++) if (global::System.Collections.Generic.EqualityComparer<" + c + ">.Default.Equals(this[i], item)) return i; return -1; }");
                client.Append("        public bool Contains(").Append(c).AppendLine(" item) { return IndexOf(item) >= 0; }");
                client.Append("        public bool Remove(").Append(c).AppendLine(" item) { int i = IndexOf(item); if (i < 0) return false; RemoveAt(i); return true; }");
                client.Append("        public void AddRange(global::System.Collections.Generic.IEnumerable<").Append(c).AppendLine("> items) { foreach (var item in items) Add(item); }");
                client.Append("        public global::System.Collections.Generic.IEnumerator<").Append(c).AppendLine("> GetEnumerator() { int n = Count; for (int i = 0; i < n; i++) yield return this[i]; }");
                client.AppendLine("        global::System.Collections.IEnumerator global::System.Collections.IEnumerable.GetEnumerator() { return GetEnumerator(); }");
            }

            void EmitOutMethod(IMethodSymbol m, List<Param> ps, TypeMap ret, GenericContext methodGenerics)
            {
                // Interface members are abstract by nature; out parameters cross as an object[] { result, outs... } both
                // ways (toolkit implementation called by a client proxy, client implementation called by the toolkit).
                if (isInterface
                        ? m.TypeParameters.Length > 0
                        : IsProtected(m) || m.IsVirtual || m.IsAbstract || m.IsOverride || m.TypeParameters.Length > 0)
                    throw new SkipException("out parameter on a virtual, protected or generic member");
                bool instance = !m.IsStatic;
                var ins = ps.Where(p => p.RefKind != RefKind.Out).ToList();
                var outs = ps.Where(p => p.RefKind == RefKind.Out).ToList();
                var wireTypes = new List<string>();
                if (instance) wireTypes.Add("object");
                wireTypes.AddRange(ins.Select(p => p.Map.Wire));
                wireTypes.Add("object[]");
                string del = "global::System.Func<" + string.Join(", ", wireTypes) + ">";
                string id = fullName + "." + m.Name + "#out(" + string.Join(",", ps.Select(p => (p.RefKind == RefKind.Out ? "out " : "") + p.Map.Wire)) + ")" + (m.IsStatic ? "s" : "");
                if (!emittedSignatures.Add(id))
                    throw new SkipException("duplicate wire signature");
                bool isVoid = ret.Wire == "void";
                var serverArgs = ps.Select(p => p.RefKind == RefKind.Out ? "out __out_" + p.Name.TrimStart('@') : p.ServerArg);
                string target = instance ? "((" + serverType + ")__o)." + m.Name : serverType + "." + m.Name;
                var body = new StringBuilder("{ ");
                foreach (var o in outs)
                    body.Append(o.Map.Server).Append(" __out_").Append(o.Name.TrimStart('@')).Append("; ");
                string call = target + "(" + string.Join(", ", serverArgs) + ")";
                body.Append(isVoid ? call + "; " : "var __r = " + call + "; ");
                body.Append("return new object[] { ").Append(isVoid ? "null" : "(object)" + ret.STo("__r"));
                foreach (var o in outs)
                    body.Append(", (object)").Append(o.Map.STo("__out_" + o.Name.TrimStart('@')));
                body.Append(" }; }");
                string lambdaParams = string.Join(", ", (instance ? new[] { "__o" } : new string[0]).Concat(ins.Select(p => p.WireName)));
                Server(id, del, "(" + lambdaParams + ") => " + body);
                string field = Field(del, id);
                string clientArgs = string.Join(", ", (instance ? new[] { "Native" } : new string[0]).Concat(ins.Select(p => p.Map.C2W(p.Name))));
                var cb = new StringBuilder("{ var __res = " + field + "(" + clientArgs + "); ");
                for (int i = 0; i < outs.Count; i++)
                    cb.Append(outs[i].Name).Append(" = ").Append(outs[i].Map.W2C("((" + outs[i].Map.Wire + ")__res[" + (i + 1) + "])")).Append("; ");
                if (!isVoid)
                    cb.Append("return ").Append(ret.W2C("((" + ret.Wire + ")__res[0])")).Append("; ");
                cb.Append("}");
                string clientDecl = string.Join(", ", ps.Select(p => p.ClientDecl));
                if (!isInterface)
                {
                    client.Append("        ").Append(AccessText(m)).Append(m.IsStatic ? " static " : " ").Append(ret.Client).Append(' ').Append(m.Name).Append('(')
                        .Append(clientDecl).Append(") ").AppendLine(cb.ToString());
                    return;
                }

                client.Append("        ").Append(ret.Client).Append(' ').Append(m.Name).Append('(').Append(clientDecl).AppendLine(");");
                _proxyBodyBuilder.Append("        public ").Append(ret.Client).Append(' ').Append(m.Name).Append('(').Append(clientDecl).Append(") ")
                    .AppendLine(cb.ToString());

                // Client implementation, exported: the delegate runs the client's method and packs the results.
                string exportDel = "global::System.Func<" + string.Join(", ", ins.Select(p => p.Map.Wire).Concat(new[] { "object[]" })) + ">";
                var eb = new StringBuilder("{ ");
                foreach (var o in outs)
                    eb.Append(o.Map.Client).Append(" __out_").Append(o.Name.TrimStart('@')).Append("; ");
                string implCall = "__impl." + m.Name + "(" + string.Join(", ", ps.Select(p => p.RefKind == RefKind.Out
                    ? "out __out_" + p.Name.TrimStart('@')
                    : p.Map.W2C(p.WireName))) + ")";
                eb.Append(isVoid ? implCall + "; " : "var __r = " + implCall + "; ");
                eb.Append("return new object[] { ").Append(isVoid ? "null" : "(object)" + ret.C2W("__r"));
                foreach (var o in outs)
                    eb.Append(", (object)").Append(o.Map.C2W("__out_" + o.Name.TrimStart('@')));
                eb.Append(" }; }");
                exportDelegates.Add("new " + exportDel + "((" + string.Join(", ", ins.Select(p => p.WireName)) + ") => " + eb + ")");

                // Toolkit side of a client implementation: call the exported delegate and unpack the results.
                string slot = "((" + exportDel + ")_b[" + (exportDelegates.Count + 2) + "])(" + string.Join(", ", ins.Select(p => p.Map.STo(p.Name))) + ")";
                var ib = new StringBuilder("{ var __res = " + slot + "; ");
                for (int i = 0; i < outs.Count; i++)
                    ib.Append(outs[i].Name).Append(" = ").Append(outs[i].Map.SFrom("((" + outs[i].Map.Wire + ")__res[" + (i + 1) + "])")).Append("; ");
                if (!isVoid)
                    ib.Append("return ").Append(ret.SFrom("((" + ret.Wire + ")__res[0])")).Append("; ");
                ib.Append("}");
                implBody.Append("        public ").Append(ret.Server).Append(' ').Append(m.Name).Append('(')
                    .Append(string.Join(", ", ps.Select(p => (p.RefKind == RefKind.Out ? "out " : "") + p.ServerDecl))).Append(") ").AppendLine(ib.ToString());
                implemented.Add(m);
            }

            void EmitConversion(IMethodSymbol m)
            {
                var ps = MapParameters(m.Parameters, generics);
                var ret = Map(m.ReturnType, generics);
                if (ret == null || ps.Count != 1)
                    throw new SkipException("conversion types");
                string del = "global::System.Func<" + ps[0].Map.Wire + ", " + ret.Wire + ">";
                string id = fullName + "." + m.Name + "(" + ps[0].Map.Wire + ")";
                if (!emittedSignatures.Add(id))
                    throw new SkipException("duplicate conversion");
                Server(id, del, ps[0].WireName + " => " + ret.STo("(" + ret.Server + ")" + ps[0].Map.SFrom(ps[0].WireName)));
                string field = Field(del, id);
                client.Append("        public static ").Append(m.Name == "op_Implicit" ? "implicit" : "explicit").Append(" operator ").Append(ret.Client)
                    .Append('(').Append(ps[0].ClientDecl).Append(") { return ").Append(ret.W2C(field + "(" + ps[0].Map.C2W(ps[0].Name) + ")")).AppendLine("; }");
            }

            void EmitHook(IMethodSymbol m, List<Param> ps, TypeMap ret, string baseIface)
            {
                bool isVoid = ret.Wire == "void";
                var wireTypes = new List<string> { "object" };
                wireTypes.AddRange(ps.Select(p => p.Map.Wire));
                if (ps.Any(p => p.RefKind != RefKind.None))
                    return;
                string del = isVoid
                    ? "global::System.Action<" + string.Join(", ", wireTypes) + ">"
                    : "global::System.Func<" + string.Join(", ", wireTypes.Concat(new[] { ret.Wire })) + ">";
                string hookName = key + "_" + m.Name + "_" + (_lambda++);
                string hookId = fullName + ".hook." + m.Name + "(" + string.Join(",", ps.Select(p => p.Map.Wire)) + ")";

                // server: one hook slot per member holding each client's handler (each casts the peer to its own
                // mod's wrapper types), plus the override in this remote class. The shared entry serves a client that
                // connected without an ownership test; RegisterClient binds the entry to the client's own pool.
                string slotType = ServerRuntime + ".RemoteHookSlot<" + del + ">";
                _serverHooks.Append("        public static readonly ").Append(slotType).Append(' ').Append(hookName).Append(" = new ").Append(slotType).AppendLine("();");
                server.Append("            api[").Append(Literal(hookId)).Append("] = new global::System.Action<").Append(del).Append(">(h => RemoteHooks.")
                    .Append(hookName).AppendLine(".Set(null, h));");
                _serverHookBindings.Append("            overrides[").Append(Literal(hookId)).Append("] = new global::System.Action<").Append(del).Append(">(h => RemoteHooks.")
                    .Append(hookName).AppendLine(".Set(owner, h));");
                _serverHookUnbindings.Append("            RemoteHooks.").Append(hookName).AppendLine(".Remove(owner);");
                _overrideSlots[m] = hookName;

                // client: hook entry calling the virtual
                string clientHookMethod = "__Hook_" + hookName;
                string clientParams = string.Join(", ", ps.Select(p => p.Map.Wire + " " + p.WireName));
                string virtualCall = m.Name + "(" + string.Join(", ", ps.Select(p => p.Map.W2C(p.WireName))) + ")";
                clientHooks.Append("        internal ").Append(ret.Wire).Append(' ').Append(clientHookMethod).Append('(').Append(clientParams).Append(") { ")
                    .Append(isVoid ? virtualCall + ";" : "return " + ret.C2W(virtualCall) + ";").AppendLine(" }");
                string lambdaParams = string.Join(", ", new[] { "__peer" }.Concat(ps.Select(p => p.WireName)));
                // Null-safe: a toolkit build without this type (older toolkit, test host) simply has no hook.
                _hookRegistrations.Add("{ var __hook = " + ClientRuntime + ".Ui.Get<global::System.Action<" + del + ">>(api, " + Literal(hookId) + "); if (__hook != null) __hook(new " + del + "((" + lambdaParams +
                                       ") => " + "((" + clientSelf.Split('<')[0] + (typeParamNames.Count > 0 ? "<" + string.Join(", ", placeholderArgs) + ">" : "") +
                                       ")__peer)." + clientHookMethod + "(" + string.Join(", ", ps.Select(p => p.WireName)) + "))); }");
            }

            void EmitProperty(IPropertySymbol p)
            {
                if (p.IsOverride)
                    return;
                var map = Map(p.Type, generics);
                if (map == null)
                    throw new SkipException("type " + p.Type.ToDisplayString());
                bool instance = !p.IsStatic;
                string getterField = null, setterField = null;
                bool protectedGet = p.GetMethod != null && IsProtected(p.GetMethod) || IsProtected(p);
                if (p.GetMethod != null && IsAccessible(p.GetMethod))
                {
                    string del = instance ? "global::System.Func<object, " + map.Wire + ">" : "global::System.Func<" + map.Wire + ">";
                    string id = fullName + ".get_" + p.Name;
                    getterField = Field(del, id);
                    string access;
                    if (protectedGet)
                    {
                        string iface = DeclareAccessorInterface(key, p, map, true);
                        remoteInterfaces.Add(iface);
                        access = "((" + iface + ")__o).Get_" + p.Name + "()";
                    }
                    else
                        access = instance ? "((" + serverType + ")__o)." + p.Name : serverType + "." + p.Name;
                    Server(id, del, (instance ? "__o" : "()") + " => " + map.STo(access));
                }
                if (p.SetMethod != null && IsAccessible(p.SetMethod) && !p.SetMethod.IsInitOnly)
                {
                    string del = instance ? "global::System.Action<object, " + map.Wire + ">" : "global::System.Action<" + map.Wire + ">";
                    string id = fullName + ".set_" + p.Name;
                    setterField = Field(del, id);
                    bool protectedSet = IsProtected(p.SetMethod) || IsProtected(p);
                    string access;
                    if (protectedSet)
                    {
                        string iface = DeclareAccessorInterface(key, p, map, false);
                        remoteInterfaces.Add(iface);
                        access = "((" + iface + ")__o).Set_" + p.Name + "(" + map.SFrom("__v") + ")";
                    }
                    else
                        access = (instance ? "((" + serverType + ")__o)." + p.Name : serverType + "." + p.Name) + " = " + map.SFrom("__v");
                    Server(id, del, (instance ? "(__o, __v)" : "__v") + " => " + access);
                }
                if (getterField == null && setterField == null)
                    return;

                string native = instance ? "Native" : "";
                string getBody = getterField == null ? null : "return " + map.W2C(getterField + "(" + native + ")") + ";";
                string setBody = setterField == null ? null : setterField + "(" + (instance ? "Native, " : "") + map.C2W("value") + ");";

                if (isInterface)
                {
                    client.Append("        ").Append(map.Client).Append(' ').Append(p.Name).Append(" { ")
                        .Append(getBody != null ? "get; " : "").Append(setBody != null ? "set; " : "").AppendLine("}");
                    _proxyBodyBuilder.Append("        public ").Append(map.Client).Append(' ').Append(p.Name).Append(" { ")
                        .Append(getBody != null ? "get { " + getBody + " } " : "").Append(setBody != null ? "set { " + setBody + " } " : "").AppendLine("}");
                    implBody.Append("        public ").Append(map.Server).Append(' ').Append(p.Name).Append(" { ");
                    if (getBody != null)
                    {
                        exportDelegates.Add("new global::System.Func<" + map.Wire + ">(() => " + map.C2W("__impl." + p.Name) + ")");
                        implBody.Append("get { return ").Append(map.SFrom("((global::System.Func<" + map.Wire + ">)_b[" + (exportDelegates.Count + 2) + "])()")).Append("; } ");
                    }
                    if (setBody != null)
                    {
                        exportDelegates.Add("new global::System.Action<" + map.Wire + ">(__v => __impl." + p.Name + " = " + map.W2C("__v") + ")");
                        implBody.Append("set { ((global::System.Action<").Append(map.Wire).Append(">)_b[").Append(exportDelegates.Count + 2).Append("])(").Append(map.STo("value")).Append("); } ");
                    }
                    implBody.AppendLine("}");
                    if (getBody != null && (p.SetMethod == null || setBody != null))
                        implemented.Add(p);
                    return;
                }
                string modifiers = HidesBase(p) + (p.IsStatic ? "static " : p.IsOverride ? "override " : "");
                client.Append("        ").Append(AccessText(p)).Append(' ').Append(modifiers).Append(map.Client).Append(' ').Append(p.Name).Append(" { ");
                if (getBody != null)
                    client.Append(AccessorAccess(p, p.GetMethod)).Append("get { ").Append(getBody).Append(" } ");
                if (setBody != null)
                    client.Append(AccessorAccess(p, p.SetMethod)).Append("set { ").Append(setBody).Append(" } ");
                client.AppendLine("}");
            }

            void EmitIndexer(IPropertySymbol p)
            {
                if (p.IsOverride || p.Parameters.Length != 1)
                    return;
                var map = Map(p.Type, generics);
                var pm = Map(p.Parameters[0].Type, generics);
                if (map == null || pm == null || p.IsStatic)
                    throw new SkipException("indexer types");
                string getterField = null, setterField = null;
                if (p.GetMethod != null && p.GetMethod.DeclaredAccessibility == Accessibility.Public)
                {
                    string del = "global::System.Func<object, " + pm.Wire + ", " + map.Wire + ">";
                    string id = fullName + ".get_Item(" + pm.Wire + ")";
                    getterField = Field(del, id);
                    Server(id, del, "(__o, __i) => " + map.STo("((" + serverType + ")__o)[" + pm.SFrom("__i") + "]"));
                }
                if (p.SetMethod != null && p.SetMethod.DeclaredAccessibility == Accessibility.Public)
                {
                    string del = "global::System.Action<object, " + pm.Wire + ", " + map.Wire + ">";
                    string id = fullName + ".set_Item(" + pm.Wire + ")";
                    setterField = Field(del, id);
                    Server(id, del, "(__o, __i, __v) => ((" + serverType + ")__o)[" + pm.SFrom("__i") + "] = " + map.SFrom("__v"));
                }
                if (getterField == null && setterField == null)
                    return;
                string head = (isInterface ? "        " : "        public ") + map.Client + " this[" + pm.Client + " index] { ";
                var body = new StringBuilder(head);
                if (getterField != null) body.Append("get { return ").Append(map.W2C(getterField + "(Native, " + pm.C2W("index") + ")")).Append("; } ");
                if (setterField != null) body.Append("set { ").Append(setterField).Append("(Native, ").Append(pm.C2W("index")).Append(", ").Append(map.C2W("value")).Append("); } ");
                body.AppendLine("}");
                if (isInterface)
                {
                    client.Append("        ").Append(map.Client).Append(" this[").Append(pm.Client).Append(" index] { ")
                        .Append(getterField != null ? "get; " : "").Append(setterField != null ? "set; " : "").AppendLine("}");
                    _proxyBodyBuilder.Append(body.ToString().Replace(head, "        public " + map.Client + " this[" + pm.Client + " index] { "));
                }
                else
                    client.Append(body);
            }

            void EmitField(IFieldSymbol f)
            {
                var map = Map(f.Type, generics);
                if (map == null)
                    throw new SkipException("type " + f.Type.ToDisplayString());
                if (f.IsConst)
                {
                    if (!map.IsIdentity && f.Type.TypeKind != TypeKind.Enum)
                        throw new SkipException("const of mapped type");
                    client.Append("        ").Append(AccessText(f)).Append(" const ").Append(map.Client).Append(' ').Append(f.Name).Append(" = ")
                        .Append(f.Type.TypeKind == TypeKind.Enum ? "(" + map.Client + ")(" + Convert.ToString(f.ConstantValue) + ")" : ConstantLiteral(f)).AppendLine(";");
                    return;
                }
                if (IsProtected(f))
                    throw new SkipException("protected field");
                bool instance = !f.IsStatic;
                string del = instance ? "global::System.Func<object, " + map.Wire + ">" : "global::System.Func<" + map.Wire + ">";
                string id = fullName + ".field_" + f.Name;
                string getterField = Field(del, id);
                string access = instance ? "((" + serverType + ")__o)." + f.Name : serverType + "." + f.Name;
                Server(id, del, (instance ? "__o" : "()") + " => " + map.STo(access));
                string setterField = null;
                if (!f.IsReadOnly)
                {
                    string sdel = instance ? "global::System.Action<object, " + map.Wire + ">" : "global::System.Action<" + map.Wire + ">";
                    string sid = fullName + ".setfield_" + f.Name;
                    setterField = Field(sdel, sid);
                    Server(sid, sdel, (instance ? "(__o, __v)" : "__v") + " => " + access + " = " + map.SFrom("__v"));
                }
                if (isInterface)
                    return;
                var fieldType = f.Type as INamedTypeSymbol;
                if (f.IsStatic && f.IsReadOnly && fieldType != null && fieldType.OriginalDefinition.ToDisplayString() == "Acrux.Core.PropertySystem.StyledProperty<T>")
                {
                    // A real field, so the view generator recognizes styled properties; it resolves on first use.
                    client.Append("        ").Append(AccessText(f)).Append(' ').Append(HidesBase(f)).Append("static readonly ").Append(map.Client).Append(' ').Append(f.Name).Append(" = ")
                        .Append(map.Client).Append(".Lazy(() => ").Append(getterField).AppendLine("());");
                    return;
                }
                bool cache = f.IsStatic && f.IsReadOnly && !map.IsIdentity && map.Wire == "object";
                string getExpr = map.W2C(getterField + "(" + (instance ? "Native" : "") + ")");
                if (cache)
                {
                    string cacheName = "__field_" + f.Name;
                    client.Append("        static ").Append(map.Client).Append(' ').Append(cacheName).AppendLine(";");
                    getExpr = cacheName + " ?? (" + cacheName + " = " + getExpr + ")";
                }
                client.Append("        ").Append(AccessText(f)).Append(' ').Append(HidesBase(f)).Append(f.IsStatic ? "static " : "").Append(map.Client).Append(' ').Append(f.Name)
                    .Append(" { get { return ").Append(getExpr).Append("; } ");
                if (setterField != null)
                    client.Append("set { ").Append(setterField).Append('(').Append(instance ? "Native, " : "").Append(map.C2W("value")).Append("); } ");
                client.AppendLine("}");
            }

            void EmitEvent(IEventSymbol e)
            {
                if (e.IsOverride || isInterface)
                    return;
                var dt = e.Type as INamedTypeSymbol;
                if (dt == null)
                    throw new SkipException("event type");
                var map = Map(dt, generics);
                if (map == null)
                    throw new SkipException("event type " + dt.ToDisplayString());
                bool instance = !e.IsStatic;
                string del = instance ? "global::System.Action<object, " + map.Wire + ">" : "global::System.Action<" + map.Wire + ">";
                string id = fullName + ".add_" + e.Name;
                string addField = Field(del, id);
                string target = instance ? "((" + serverType + ")__o)." + e.Name : serverType + "." + e.Name;
                Server(id, del, (instance ? "(__o, __h)" : "__h") + " => " + target + " += " + map.SFrom("__h"));
                string handlers = "__event_" + e.Name;
                string subscribed = "__event_" + e.Name + "_subscribed";
                var invoke = dt.DelegateInvokeMethod;
                var ips = invoke.Parameters.Select((p, i) => "__a" + i).ToList();
                string st = e.IsStatic ? "static " : "";
                client.Append("        ").Append(st).Append(map.Client).Append(' ').Append(handlers).AppendLine(";");
                client.Append("        ").Append(st).Append("bool ").Append(subscribed).AppendLine(";");
                client.Append("        ").Append(AccessText(e)).Append(' ').Append(HidesBase(e)).Append(st).Append("event ").Append(map.Client).Append(' ').Append(e.Name).AppendLine();
                client.AppendLine("        {");
                client.Append("            add { ").Append(handlers).Append(" += value; if (!").Append(subscribed).Append(") { ").Append(subscribed).Append(" = true; ")
                    .Append(addField).Append('(').Append(instance ? "Native, " : "")
                    .Append(map.C2W("new " + map.Client + "((" + string.Join(", ", ips) + ") => { var __h = " + handlers + "; if (__h != null) __h(" + string.Join(", ", ips) + "); })"))
                    .AppendLine("); } }");
                client.Append("            remove { ").Append(handlers).AppendLine(" -= value; }");
                client.AppendLine("        }");
            }

            // The wrapper repeats the source's `new`, so a member hiding an inherited one does not warn (CS0108) in clients.
            static string HidesBase(ISymbol symbol)
            {
                foreach (var reference in symbol.DeclaringSyntaxReferences)
                {
                    SyntaxNode node = reference.GetSyntax();
                    while (node != null && !(node is MemberDeclarationSyntax))
                        node = node.Parent;
                    if (node is MemberDeclarationSyntax member && member.Modifiers.Any(SyntaxKind.NewKeyword))
                        return "new ";
                }
                return "";
            }

            string AccessorAccess(IPropertySymbol p, IMethodSymbol accessor)
            {
                if (accessor == null || accessor.DeclaredAccessibility == p.DeclaredAccessibility)
                    return "";
                return AccessText(accessor) + " ";
            }
        }

        // Client explicit implementations of mirrored interfaces, forwarding through a detached interface proxy.
        string ExplicitInterfaceImpls(INamedTypeSymbol owner, ImmutableArray<INamedTypeSymbol> interfaces, GenericContext generics)
        {
            var sb = new StringBuilder();
            foreach (var iface in interfaces)
            {
                if (iface.ToDisplayString() == "System.IDisposable")
                {
                    if (owner.TypeKind == TypeKind.Interface)
                        sb.Append("        public void Dispose() { ").Append(ClientRuntime).AppendLine(".Ui.DisposeNative(Native); }");
                    continue;
                }
                if (!_mirrored.Contains(iface.OriginalDefinition))
                    continue;
                var im = Map(iface, generics);
                if (im == null)
                    continue;
                string key = Sanitize(iface.OriginalDefinition.ToDisplayString());
                string proxy = "new global::" + iface.ContainingNamespace.ToDisplayString() + ".__Proxy_" + key +
                               (iface.TypeArguments.Length > 0 ? "<" + string.Join(", ", iface.TypeArguments.Select(a => Map(a, generics)?.Client ?? "object")) + ">" : "") +
                               "(Native, true)";
                var ifaceGenerics = new GenericContext();
                foreach (var kv in generics.ServerArgs)
                    ifaceGenerics.ServerArgs[kv.Key] = kv.Value;
                foreach (var member in iface.GetMembers())
                {
                    if (IsManual(member.OriginalDefinition))
                        continue;
                    try
                    {
                        if (member is IMethodSymbol m && m.MethodKind == MethodKind.Ordinary)
                        {
                            if (m.TypeParameters.Length > 0 || m.Parameters.Any(x => x.RefKind != RefKind.None))
                                continue;
                            var ps = MapParameters(m.Parameters, ifaceGenerics);
                            var ret = Map(m.ReturnType, ifaceGenerics);
                            if (ret == null)
                                continue;
                            string call = proxy + "." + m.Name + "(" + string.Join(", ", ps.Select(p => p.Name)) + ")";
                            sb.Append("        ").Append(ret.Client).Append(' ').Append(im.Client).Append('.').Append(m.Name).Append('(')
                                .Append(string.Join(", ", ps.Select(p => p.Map.Client + " " + p.Name))).Append(") { ")
                                .Append(ret.Wire == "void" ? call + ";" : "return " + call + ";").AppendLine(" }");
                        }
                        else if (member is IPropertySymbol p && !p.IsIndexer)
                        {
                            var map = Map(p.Type, ifaceGenerics);
                            if (map == null)
                                continue;
                            bool get = p.GetMethod != null, set = p.SetMethod != null;
                            sb.Append("        ").Append(map.Client).Append(' ').Append(im.Client).Append('.').Append(p.Name).Append(" { ");
                            if (get) sb.Append("get { return ").Append(proxy).Append('.').Append(p.Name).Append("; } ");
                            if (set) sb.Append("set { ").Append(proxy).Append('.').Append(p.Name).Append(" = value; } ");
                            sb.AppendLine("}");
                        }
                        else if (member is IPropertySymbol ix && ix.IsIndexer && ix.Parameters.Length == 1)
                        {
                            var map = Map(ix.Type, ifaceGenerics);
                            var pm = Map(ix.Parameters[0].Type, ifaceGenerics);
                            if (map == null || pm == null)
                                continue;
                            sb.Append("        ").Append(map.Client).Append(' ').Append(im.Client).Append(".this[").Append(pm.Client).Append(" index] { ");
                            if (ix.GetMethod != null) sb.Append("get { return ").Append(proxy).Append("[index]; } ");
                            if (ix.SetMethod != null) sb.Append("set { ").Append(proxy).Append("[index] = value; } ");
                            sb.AppendLine("}");
                        }
                    }
                    catch (SkipException)
                    {
                    }
                }
            }
            return sb.ToString();
        }

        // Server implementations for interface members a client implementation cannot provide.
        string ThrowingImpls(INamedTypeSymbol iface, HashSet<ISymbol> implemented, GenericContext generics)
        {
            var sb = new StringBuilder();
            var all = new List<INamedTypeSymbol> { iface };
            all.AddRange(iface.AllInterfaces);
            foreach (var t in all)
            {
                string owner = Fq(SubstituteServer(t, generics));
                foreach (var member in t.GetMembers())
                {
                    if (implemented.Contains(member))
                        continue;
                    if (t.ToDisplayString() == "System.IDisposable")
                    {
                        sb.AppendLine("        public void Dispose() { var d = _b[2] as global::System.Action; if (d != null) d(); }");
                        continue;
                    }
                    string fail = "throw new global::System.NotSupportedException(\"" + member.Name + " is not provided by the client implementation.\");";
                    if (member is IMethodSymbol m && m.MethodKind == MethodKind.Ordinary)
                    {
                        string tps = m.TypeParameters.Length == 0 ? "" : "<" + string.Join(", ", m.TypeParameters.Select(x => x.Name)) + ">";
                        sb.Append("        ").Append(Fq(SubstituteServer(m.ReturnType, generics))).Append(' ').Append(owner).Append('.').Append(m.Name).Append(tps).Append('(')
                            .Append(string.Join(", ", m.Parameters.Select(p => (p.RefKind == RefKind.Out ? "out " : p.RefKind == RefKind.Ref ? "ref " : "") + Fq(SubstituteServer(p.Type, generics)) + " @" + p.Name)))
                            .Append(") { ").Append(fail).AppendLine(" }");
                    }
                    else if (member is IPropertySymbol p)
                    {
                        string head = p.IsIndexer
                            ? "this[" + string.Join(", ", p.Parameters.Select(x => Fq(SubstituteServer(x.Type, generics)) + " @" + x.Name)) + "]"
                            : p.Name;
                        sb.Append("        ").Append(Fq(SubstituteServer(p.Type, generics))).Append(' ').Append(owner).Append('.').Append(head).Append(" { ");
                        if (p.GetMethod != null) sb.Append("get { ").Append(fail).Append(" } ");
                        if (p.SetMethod != null) sb.Append("set { ").Append(fail).Append(" } ");
                        sb.AppendLine("}");
                    }
                    else if (member is IEventSymbol e)
                        sb.Append("        event ").Append(Fq(SubstituteServer(e.Type, generics))).Append(' ').Append(owner).Append('.').Append(e.Name).AppendLine(" { add { } remove { } }");
                }
            }
            return sb.ToString();
        }

        ITypeSymbol SubstituteServer(ITypeSymbol type, GenericContext generics)
        {
            if (type is ITypeParameterSymbol tp)
            {
                ITypeSymbol arg;
                return generics.ServerArgs.TryGetValue(tp, out arg) ? arg : type;
            }
            if (type is INamedTypeSymbol named && named.IsGenericType)
            {
                var args = named.TypeArguments.Select(a => SubstituteServer(a, generics)).ToArray();
                return named.OriginalDefinition.Construct(args);
            }
            if (type is IArrayTypeSymbol array)
                return _compilation.CreateArrayTypeSymbol(SubstituteServer(array.ElementType, generics), array.Rank);
            return type;
        }

        // remote overrides are emitted after all hooks are known (per type)
        readonly Dictionary<IMethodSymbol, string> _overrideSlots = new Dictionary<IMethodSymbol, string>(SymbolEqualityComparer.Default);
        readonly StringBuilder _proxyBodyBuilder = new StringBuilder();
        // Mirrored types whose client wrapper declares abstract members: no stand-in can be made for them.
        readonly HashSet<INamedTypeSymbol> _clientAbstractMembers = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        string _proxyBody
        {
            get
            {
                string s = _proxyBodyBuilder.ToString();
                _proxyBodyBuilder.Clear();
                return s;
            }
        }

        static string OverrideConstraints(IMethodSymbol m) => string.Join(" ", m.TypeParameters.Select(ConstraintClause).Where(c => c.Length > 0)) + " ";

        static string ConstraintClause(ITypeParameterSymbol tp)
        {
            var parts = new List<string>();
            if (tp.HasReferenceTypeConstraint) parts.Add("class");
            if (tp.HasValueTypeConstraint) parts.Add("struct");
            foreach (var c in tp.ConstraintTypes)
                parts.Add(Fq(c));
            if (tp.HasConstructorConstraint) parts.Add("new()");
            return parts.Count == 0 ? "" : "where " + tp.Name + " : " + string.Join(", ", parts);
        }

        const string ClientGuardOpen = "#if ACRUX_UI_TOOLKIT_CLIENT // stripped when exported to ClientApi";
        const string ClientGuardClose = "#endif // ACRUX_UI_TOOLKIT_CLIENT";

        // Client sources must not compile into the toolkit itself; the export step strips the guard.
        static string ClientGuard(string source) => ClientGuardOpen + "\n" + source + "\n" + ClientGuardClose + "\n";

        static string Literal(string s) => SymbolDisplay.FormatLiteral(s, true);

        static string ConstantLiteral(IFieldSymbol f)
        {
            object v = f.ConstantValue;
            switch (v)
            {
                case null: return "null";
                case string s: return Literal(s);
                case bool b: return b ? "true" : "false";
                case float fl: return fl.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "f";
                case double d: return d.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "d";
                case long l: return l + "L";
                case char c: return SymbolDisplay.FormatLiteral(c, true);
                default: return Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        sealed class Param
        {
            public string Name;
            public string WireName;
            public TypeMap Map;
            public string ClientDecl;
            public string ServerDecl;
            public string ServerArg;
            public RefKind RefKind;
        }

        sealed class SkipException : Exception
        {
            public SkipException(string message) : base(message) { }
        }

        List<Param> MapParameters(ImmutableArray<IParameterSymbol> parameters, GenericContext generics)
        {
            var result = new List<Param>();
            foreach (var p in parameters)
            {
                if (p.RefKind != RefKind.None && p.RefKind != RefKind.Out)
                    throw new SkipException("ref parameter " + p.Name);
                var map = Map(p.Type, generics);
                if (map == null)
                    throw new SkipException("parameter " + p.Name + " of type " + p.Type.ToDisplayString());
                string name = "@" + p.Name;
                string decl = (p.RefKind == RefKind.Out ? "out " : "") + (p.IsParams ? "params " : "") + map.Client + " " + name;
                if (p.HasExplicitDefaultValue)
                    decl += " = " + DefaultLiteral(p, map);
                result.Add(new Param
                {
                    Name = name,
                    WireName = "__w_" + p.Name,
                    Map = map,
                    ClientDecl = decl,
                    ServerDecl = map.Server + " " + name,
                    ServerArg = map.SFrom("__w_" + p.Name),
                    RefKind = p.RefKind,
                });
            }
            return result;
        }

        static string PrepareOutParams(List<Param> ps) => string.Empty;

        static string DefaultLiteral(IParameterSymbol p, TypeMap map)
        {
            object v = p.ExplicitDefaultValue;
            if (v == null)
                return p.Type.IsValueType && p.Type.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T ? "default(" + map.Client + ")" : "null";
            if (p.Type.TypeKind == TypeKind.Enum)
                return "(" + map.Client + ")(" + Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) + ")";
            switch (v)
            {
                case string s: return Literal(s);
                case bool b: return b ? "true" : "false";
                case float f: return f.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "f";
                case double d: return d.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "d";
                case long l: return l + "L";
                default: return Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        // ------------------------------------------------------- server-side base/protected access

        string DeclareBaseInterface(string key, IMethodSymbol m, List<Param> ps, TypeMap ret, string typeArgs, GenericContext generics)
        {
            // Declared once per member at its declaring type; every remote subclass implements it.
            var declaring = m;
            while (declaring.OverriddenMethod != null)
                declaring = declaring.OverriddenMethod;
            string declKey = Sanitize(declaring.ContainingType.OriginalDefinition.ToDisplayString()) + "_" + m.Name + "_" +
                             Sanitize(string.Join("_", ps.Select(p => p.Map.Server)));
            string name = "IBase_" + declKey;
            string typeParams = m.TypeParameters.Length == 0 ? "" : "<" + string.Join(", ", m.TypeParameters.Select(t => "T" + t.Ordinal)) + ">";
            string sig = ret.Server + " Base_" + m.Name + typeParams + "(" + string.Join(", ", ps.Select(p => p.ServerDecl)) + ")";
            if (_declaredInterfaces.Add(name))
            {
                if (m.TypeParameters.Length > 0)
                    throw new SkipException("generic protected/virtual member");
                _serverInterfaces.Append("    public interface ").Append(name).Append(" { ").Append(sig).AppendLine("; }");
            }
            _pendingRemoteMembers.Add(new PendingRemote { Interface = name, Method = m, Params = ps, Return = ret });
            return ServerRuntime + "." + name;
        }

        string DeclareAccessorInterface(string key, IPropertySymbol p, TypeMap map, bool getter)
        {
            var declaring = p;
            while (declaring.OverriddenProperty != null)
                declaring = declaring.OverriddenProperty;
            string name = "IProp_" + Sanitize(declaring.ContainingType.OriginalDefinition.ToDisplayString()) + "_" + p.Name + (getter ? "_get" : "_set");
            if (_declaredInterfaces.Add(name))
            {
                _serverInterfaces.Append("    public interface ").Append(name).Append(" { ")
                    .Append(getter ? map.Server + " Get_" + p.Name + "()" : "void Set_" + p.Name + "(" + map.Server + " value)")
                    .AppendLine("; }");
            }
            _pendingRemoteProperties.Add(new PendingProperty { Interface = name, Property = p, Map = map, Getter = getter });
            return ServerRuntime + "." + name;
        }

        sealed class PendingRemote
        {
            public string Interface;
            public IMethodSymbol Method;
            public List<Param> Params;
            public TypeMap Return;
        }

        sealed class PendingProperty
        {
            public string Interface;
            public IPropertySymbol Property;
            public TypeMap Map;
            public bool Getter;
        }

        readonly List<PendingRemote> _pendingRemoteMembers = new List<PendingRemote>();
        readonly List<PendingProperty> _pendingRemoteProperties = new List<PendingProperty>();

        // ---------------------------------------------------------------- roots

        // ---------------------------------------------------------------- styled-property codecs

        // One codec per value type of every StyledProperty<T> declared in the toolkit's source, mirrored types or not,
        // so a property reached by name (a custom property) converts like a generated member would. Types a client
        // cannot name (toolkit-only types) get no codec: such properties are listed as not transferable.
        List<PropertyCodec> CollectPropertyCodecs()
        {
            var codecs = new List<PropertyCodec>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            void WalkType(INamedTypeSymbol t)
            {
                if (!t.Locations.Any(l => l.IsInSource))
                    return;
                foreach (var nested in t.GetTypeMembers())
                    WalkType(nested);
                foreach (var field in t.GetMembers().OfType<IFieldSymbol>())
                {
                    var fieldType = field.Type as INamedTypeSymbol;
                    if (fieldType == null || fieldType.Name != "StyledProperty" || fieldType.TypeArguments.Length != 1 ||
                        fieldType.ContainingNamespace == null ||
                        fieldType.ContainingNamespace.ToDisplayString() != "Acrux.Core.PropertySystem")
                        continue;
                    ITypeSymbol valueType = fieldType.TypeArguments[0];
                    string key = Fq(valueType);
                    if (!IsClientNameable(valueType))
                    {
                        // A toolkit-only enum crosses as its integer when the declaration pins that integer type.
                        if (valueType.TypeKind == TypeKind.Enum && seen.Add(key))
                        {
                            var enumType = (INamedTypeSymbol)valueType;
                            // Published as its integer codec, which every client has (the integer codecs are always
                            // generated); the CLR unboxes an enum as its underlying integer and back.
                            if (HasExplicitUnderlyingType(enumType))
                                _enumCodecAliases.Add(new KeyValuePair<string, string>(key, Fq(enumType.EnumUnderlyingType)));
                            else
                                _output.ReportDiagnostic(Diagnostic.Create(EnumWithoutUnderlyingType,
                                    enumType.Locations.FirstOrDefault(l => l.IsInSource) ?? field.Locations.FirstOrDefault(),
                                    enumType.ToDisplayString(), t.ToDisplayString() + "." + field.Name, enumType.Name));
                        }
                        continue;
                    }
                    if (!seen.Add(key))
                        continue;
                    TypeMap map;
                    try
                    {
                        map = Map(valueType, new GenericContext());
                    }
                    catch (SkipException)
                    {
                        map = null;
                    }
                    if (map == null || map.Wire == "void")
                        continue;
                    codecs.Add(new PropertyCodec { Key = key, Type = valueType, Map = map });
                }
            }
            void WalkNamespace(INamespaceSymbol ns)
            {
                foreach (var member in ns.GetMembers())
                {
                    if (member is INamespaceSymbol child)
                        WalkNamespace(child);
                    else if (member is INamedTypeSymbol type)
                        WalkType(type);
                }
            }
            WalkNamespace(_compilation.Assembly.GlobalNamespace);
            // Every integer type, used or not: they carry toolkit-only enums, whose aliases a client cannot know ahead.
            foreach (SpecialType integer in new[]
                     {
                         SpecialType.System_SByte, SpecialType.System_Byte, SpecialType.System_Int16, SpecialType.System_UInt16,
                         SpecialType.System_Int32, SpecialType.System_UInt32, SpecialType.System_Int64, SpecialType.System_UInt64
                     })
            {
                ITypeSymbol integerType = _compilation.GetSpecialType(integer);
                string key = Fq(integerType);
                if (seen.Add(key))
                    codecs.Add(new PropertyCodec { Key = key, Type = integerType, Map = Identity(key) });
            }
            return codecs.OrderBy(c => c.Key, StringComparer.Ordinal).ToList();
        }

        // Whether the enum's declaration states its underlying type (': byte'), rather than the implicit int.
        static bool HasExplicitUnderlyingType(INamedTypeSymbol enumType)
        {
            foreach (var reference in enumType.DeclaringSyntaxReferences)
            {
                var declaration = reference.GetSyntax() as Microsoft.CodeAnalysis.CSharp.Syntax.EnumDeclarationSyntax;
                if (declaration != null && declaration.BaseList != null && declaration.BaseList.Types.Count > 0)
                    return true;
            }
            return false;
        }

        // Whether a client can name the type: game/BCL types, mirrored, shared or codec types, recursively.
        bool IsClientNameable(ITypeSymbol type)
        {
            if (type is ITypeParameterSymbol)
                return false;
            if (type is IArrayTypeSymbol array)
                return IsClientNameable(array.ElementType);
            var named = type as INamedTypeSymbol;
            if (named == null)
                return false;
            foreach (var arg in named.TypeArguments)
                if (!IsClientNameable(arg))
                    return false;
            if (named.SpecialType != SpecialType.None || !named.Locations.Any(l => l.IsInSource))
                return true;
            var definition = named.OriginalDefinition;
            return _mirrored.Contains(definition) || _shared.Contains(definition) ||
                   _codecs.ContainsKey(definition.ToDisplayString());
        }

        void EmitRoots()
        {
            var propertyCodecs = CollectPropertyCodecs();
            foreach (var codec in propertyCodecs)
                _propertyCodecRegistrations.Add(ClientRuntime + ".Ui.RegisterPropertyCodec(" + Literal(codec.Key) + ", typeof(" + codec.Map.Client +
                    "), __v => (object)(" + codec.Map.C2W("((" + codec.Map.Client + ")__v)") + "), __w => (object)(" +
                    codec.Map.W2C("((" + codec.Map.Wire + ")__w)") + "));");

            // Remote classes: implement every base/protected interface of the class and its ancestors,
            // and override every hookable member.
            var remoteMembers = new StringBuilder();
            remoteMembers.AppendLine("// <auto-generated/>");
            remoteMembers.AppendLine("namespace Acrux.Toolkit.Server");
            remoteMembers.AppendLine("{");
            foreach (var type in _mirrored.Where(CanRemote).OrderBy(t => t.ToDisplayString(), StringComparer.Ordinal))
            {
                string key = Sanitize(type.ToDisplayString());
                var interfaces = new List<string>();
                var body = new StringBuilder();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (var t = type; t != null; t = t.BaseType)
                {
                    foreach (var pending in _pendingRemoteMembers.Where(p => SymbolEqualityComparer.Default.Equals(p.Method.ContainingType.OriginalDefinition, t.OriginalDefinition)))
                    {
                        if (!seen.Add(pending.Interface))
                            continue;
                        var m = pending.Method;
                        // Resolve the member as seen from 'type' (sealed overrides cannot be re-overridden).
                        if (IsSealedBelow(type, m))
                            continue;
                        interfaces.Add(pending.Interface);
                        string ps = string.Join(", ", pending.Params.Select(p => p.ServerDecl));
                        string args = string.Join(", ", pending.Params.Select(p => p.Name));
                        bool isVoid = pending.Return.Server == "void";
                        string baseCall = m.IsAbstract && !HasConcreteImplementationBelow(type, m)
                            ? "throw new global::System.NotSupportedException(\"" + m.Name + " is abstract\")"
                            : "base." + m.Name + "(" + args + ")";
                        body.Append("        public ").Append(pending.Return.Server).Append(" Base_").Append(m.Name).Append('(').Append(ps).Append(") { ")
                            .Append(isVoid || baseCall.StartsWith("throw", StringComparison.Ordinal) ? baseCall + ";" : "return " + baseCall + ";").AppendLine(" }");

                        string slot;
                        if ((m.IsVirtual || m.IsAbstract || m.IsOverride) && _overrideSlots.TryGetValue(m, out slot))
                        {
                            var rootMethod = m;
                            while (rootMethod.OverriddenMethod != null) rootMethod = rootMethod.OverriddenMethod;
                            string access = AccessText(rootMethod);
                            if (access == "protected internal" && !SymbolEqualityComparer.Default.Equals(rootMethod.ContainingAssembly, _compilation.Assembly))
                                access = "protected";
                            string wireArgs = string.Join(", ", new[] { "RemotePeer" }.Concat(pending.Params.Select(p => p.Map.STo(p.Name))));
                            string hookCall = "__hook(" + wireArgs + ")";
                            body.Append("        ").Append(access).Append(" override ").Append(pending.Return.Server).Append(' ').Append(m.Name).Append('(').Append(ps).Append(") { ");
                            body.Append("var __hook = RemoteHooks.").Append(slot).Append(".For(RemotePeer); ");
                            body.Append("if (__hook != null) ")
                                .Append(isVoid ? hookCall + ";" : "return " + pending.Return.SFrom(hookCall) + ";");
                            if (isVoid)
                                body.Append(" else ").Append(baseCall).Append(';');
                            else
                                body.Append(baseCall.StartsWith("throw", StringComparison.Ordinal) ? " " + baseCall + ";" : " return " + baseCall + ";");
                            body.AppendLine(" }");
                        }
                    }
                    foreach (var pending in _pendingRemoteProperties.Where(p => SymbolEqualityComparer.Default.Equals(p.Property.ContainingType.OriginalDefinition, t.OriginalDefinition)))
                    {
                        if (!seen.Add(pending.Interface))
                            continue;
                        interfaces.Add(pending.Interface);
                        if (pending.Getter)
                            body.Append("        public ").Append(pending.Map.Server).Append(" Get_").Append(pending.Property.Name).Append("() { return ").Append(pending.Property.Name).AppendLine("; }");
                        else
                            body.Append("        public void Set_").Append(pending.Property.Name).Append('(').Append(pending.Map.Server).Append(" value) { ").Append(pending.Property.Name).AppendLine(" = value; }");
                    }
                }
                remoteMembers.Append("    partial class Remote_").Append(key);
                if (interfaces.Count > 0)
                    remoteMembers.Append(" : ").Append(string.Join(", ", interfaces.Select(i => ServerRuntime + "." + i)));
                remoteMembers.AppendLine();
                remoteMembers.AppendLine("    {");
                remoteMembers.Append(body);
                remoteMembers.AppendLine("    }");
            }
            remoteMembers.AppendLine("}");
            _output.AddSource("Server.RemoteMembers.g.cs", remoteMembers.ToString());

            var root = new StringBuilder();
            root.AppendLine("// <auto-generated/>");
            root.AppendLine("namespace Acrux.Toolkit.Server");
            root.AppendLine("{");
            root.Append(_serverInterfaces);
            root.AppendLine("    public static class RemoteHooks");
            root.AppendLine("    {");
            root.Append(_serverHooks);
            root.AppendLine("    }");
            root.AppendLine("    public static partial class AcruxUiToolkitBridge");
            root.AppendLine("    {");
            root.AppendLine("        /// <summary>Binds the override hooks a client registers to that client's pool (its ownership test).</summary>");
            root.AppendLine("        internal static void BindClientHooks(global::Acrux.Toolkit.GeneratedTexturePool owner, global::System.Collections.Generic.IDictionary<string, global::System.Delegate> overrides)");
            root.AppendLine("        {");
            root.Append(_serverHookBindings);
            root.AppendLine("        }");
            root.AppendLine();
            root.AppendLine("        internal static void UnbindClientHooks(global::Acrux.Toolkit.GeneratedTexturePool owner)");
            root.AppendLine("        {");
            root.Append(_serverHookUnbindings);
            root.AppendLine("        }");
            root.AppendLine();
            root.AppendLine("        public static void RegisterGenerated(global::System.Collections.Generic.Dictionary<string, global::System.Delegate> api)");
            root.AppendLine("        {");
            foreach (var r in _serverRegistrations)
                root.Append("            ").AppendLine(r);
            root.AppendLine("            api[\"Toolkit.TypeName\"] = new global::System.Func<object, string>(MirrorTypeName);");
            root.AppendLine("            api[\"Toolkit.ToolkitTypeName\"] = new global::System.Func<object, string>(o => o == null ? null : o.GetType().FullName);");
            root.AppendLine("            api[\"Toolkit.CustomPropertySchema\"] = new global::System.Func<object, object[]>(CustomPropertySchema);");
            root.AppendLine("            api[\"Toolkit.GetCustomProperty\"] = new global::System.Func<object, string, object>(GetCustomProperty);");
            root.AppendLine("            api[\"Toolkit.SetCustomProperty\"] = new global::System.Action<object, string, object>(SetCustomProperty);");
            root.AppendLine("        }");
            root.AppendLine();
            root.AppendLine("        // A client wraps a toolkit object as its most derived mirrored type; the styled properties declared by the");
            root.AppendLine("        // object's own, unmirrored types (an internal control, or a toolkit type not mirrored to clients) are its");
            root.AppendLine("        // custom properties, read and written by name through generated per-type codecs. Attached properties");
            root.AppendLine("        // (qualified names) are not included.");
            root.AppendLine("        static readonly global::System.Collections.Generic.Dictionary<global::System.Type, global::Acrux.Core.PropertySystem.IStyledProperty[]> __customProperties =");
            root.AppendLine("            new global::System.Collections.Generic.Dictionary<global::System.Type, global::Acrux.Core.PropertySystem.IStyledProperty[]>();");
            root.AppendLine("        static global::Acrux.Core.PropertySystem.IStyledProperty[] CustomPropertiesOf(object o)");
            root.AppendLine("        {");
            root.AppendLine("            var control = o as global::Acrux.Core.Control;");
            root.AppendLine("            if (control == null) return new global::Acrux.Core.PropertySystem.IStyledProperty[0];");
            root.AppendLine("            var type = o.GetType();");
            root.AppendLine("            global::Acrux.Core.PropertySystem.IStyledProperty[] cached;");
            root.AppendLine("            lock (__customProperties)");
            root.AppendLine("                if (__customProperties.TryGetValue(type, out cached)) return cached;");
            root.AppendLine("            var mirrored = new global::System.Collections.Generic.HashSet<string>(MirrorTypeChain(o));");
            root.AppendLine("            var names = new global::System.Collections.Generic.HashSet<string>();");
            root.AppendLine("            var found = new global::System.Collections.Generic.List<global::Acrux.Core.PropertySystem.IStyledProperty>();");
            root.AppendLine("            foreach (var p in global::Acrux.Core.PropertySystem.StyledPropertyCatalog.Snapshot())");
            root.AppendLine("                if (p.Name.IndexOf('.') < 0 && p.Matches(control) && !mirrored.Contains(OpenTypeName(p.OwnerType.FullName)) && names.Add(p.Name))");
            root.AppendLine("                    found.Add(p);");
            root.AppendLine("            cached = found.ToArray();");
            root.AppendLine("            lock (__customProperties) __customProperties[type] = cached;");
            root.AppendLine("            return cached;");
            root.AppendLine("        }");
            root.AppendLine("        static string OpenTypeName(string name)");
            root.AppendLine("        {");
            root.AppendLine("            if (name == null) return null;");
            root.AppendLine("            int cut = name.IndexOfAny(new[] { '`', '<' });");
            root.AppendLine("            return cut < 0 ? name : name.Substring(0, cut);");
            root.AppendLine("        }");
            root.AppendLine("        /// <summary>Each custom property as { name, codec key (null: not transferable), default value on the wire }.</summary>");
            root.AppendLine("        public static object[] CustomPropertySchema(object o)");
            root.AppendLine("        {");
            root.AppendLine("            var properties = CustomPropertiesOf(o);");
            root.AppendLine("            var schema = new object[properties.Length];");
            root.AppendLine("            for (int i = 0; i < properties.Length; i++)");
            root.AppendLine("            {");
            root.AppendLine("                string key = PropertyCodecKey(properties[i].ValueType);");
            root.AppendLine("                schema[i] = new object[] { properties[i].Name, key, key == null ? null : PropertyToWire(key, properties[i].DefaultValueObject) };");
            root.AppendLine("            }");
            root.AppendLine("            return schema;");
            root.AppendLine("        }");
            root.AppendLine("        static global::Acrux.Core.PropertySystem.IStyledProperty FindCustomProperty(object o, string name, out string key)");
            root.AppendLine("        {");
            root.AppendLine("            foreach (var p in CustomPropertiesOf(o))");
            root.AppendLine("            {");
            root.AppendLine("                if (p.Name != name) continue;");
            root.AppendLine("                key = PropertyCodecKey(p.ValueType);");
            root.AppendLine("                if (key == null) throw new global::System.NotSupportedException(\"Custom property '\" + name + \"' (\" + p.ValueType.FullName + \") cannot cross the bridge.\");");
            root.AppendLine("                return p;");
            root.AppendLine("            }");
            root.AppendLine("            throw new global::System.ArgumentException(\"No custom property '\" + name + \"' on \" + (o == null ? \"null\" : o.GetType().FullName) + \".\");");
            root.AppendLine("        }");
            root.AppendLine("        static object GetCustomProperty(object o, string name)");
            root.AppendLine("        {");
            root.AppendLine("            string key;");
            root.AppendLine("            var p = FindCustomProperty(o, name, out key);");
            root.AppendLine("            return PropertyToWire(key, p.GetValueObject((global::Acrux.Core.Control)o));");
            root.AppendLine("        }");
            root.AppendLine("        static void SetCustomProperty(object o, string name, object value)");
            root.AppendLine("        {");
            root.AppendLine("            string key;");
            root.AppendLine("            var p = FindCustomProperty(o, name, out key);");
            root.AppendLine("            p.SetValueObject((global::Acrux.Core.Control)o, PropertyFromWire(key, value));");
            root.AppendLine("        }");
            root.AppendLine();
            root.AppendLine("        // Generated codecs: one per styled-property value type a client can name.");
            root.AppendLine("        static readonly global::System.Collections.Generic.Dictionary<global::System.Type, string> __propertyCodecKeys =");
            root.AppendLine("            new global::System.Collections.Generic.Dictionary<global::System.Type, string>");
            root.AppendLine("            {");
            foreach (var codec in propertyCodecs)
                root.Append("                { typeof(").Append(codec.Map.Server).Append("), ").Append(Literal(codec.Key)).AppendLine(" },");
            foreach (var alias in _enumCodecAliases)
                root.Append("                { typeof(").Append(alias.Key).Append("), ").Append(Literal(alias.Value)).AppendLine(" },");
            root.AppendLine("            };");
            root.AppendLine("        static string PropertyCodecKey(global::System.Type type)");
            root.AppendLine("        {");
            root.AppendLine("            string key;");
            root.AppendLine("            return type != null && __propertyCodecKeys.TryGetValue(type, out key) ? key : null;");
            root.AppendLine("        }");
            root.AppendLine("        static object PropertyToWire(string key, object value)");
            root.AppendLine("        {");
            root.AppendLine("            if (value == null) return null;");
            root.AppendLine("            switch (key)");
            root.AppendLine("            {");
            foreach (var codec in propertyCodecs)
                root.Append("                case ").Append(Literal(codec.Key)).Append(": return (object)(").Append(codec.Map.STo("((" + codec.Map.Server + ")value)")).AppendLine(");");
            root.AppendLine("                default: throw new global::System.NotSupportedException(\"No codec for \" + key + \".\");");
            root.AppendLine("            }");
            root.AppendLine("        }");
            root.AppendLine("        static object PropertyFromWire(string key, object wire)");
            root.AppendLine("        {");
            root.AppendLine("            if (wire == null) return null;");
            root.AppendLine("            switch (key)");
            root.AppendLine("            {");
            foreach (var codec in propertyCodecs)
                root.Append("                case ").Append(Literal(codec.Key)).Append(": return (object)(").Append(codec.Map.SFrom("((" + codec.Map.Wire + ")wire)")).AppendLine(");");
            root.AppendLine("                default: throw new global::System.NotSupportedException(\"No codec for \" + key + \".\");");
            root.AppendLine("            }");
            root.AppendLine("        }");
            root.AppendLine();
            root.AppendLine("        static readonly global::System.Collections.Generic.Dictionary<global::System.Type, string> __typeNames = new global::System.Collections.Generic.Dictionary<global::System.Type, string>();");
            root.AppendLine("        public static string MirrorTypeName(object o)");
            root.AppendLine("        {");
            root.AppendLine("            if (o == null) return null;");
            root.AppendLine("            var type = o.GetType();");
            root.AppendLine("            string name;");
            root.AppendLine("            lock (__typeNames)");
            root.AppendLine("                if (__typeNames.TryGetValue(type, out name)) return name;");
            root.AppendLine("            name = ResolveMirrorTypeName(o);");
            root.AppendLine("            lock (__typeNames) __typeNames[type] = name;");
            root.AppendLine("            return name;");
            root.AppendLine("        }");
            root.AppendLine();
            root.AppendLine("        static string ResolveMirrorTypeName(object o)");
            root.AppendLine("        {");
            var ordered = _mirrored.Where(t => t.TypeKind != TypeKind.Enum && !t.IsStatic)
                .OrderByDescending(Depth).ThenBy(t => t.TypeKind == TypeKind.Interface ? 1 : 0).ThenBy(t => t.ToDisplayString(), StringComparer.Ordinal);
            foreach (var t in ordered)
            {
                string serverType = "global::" + t.ToDisplayString().Split('<')[0] +
                                    (t.TypeParameters.Length > 0 ? "<" + string.Join(", ", t.TypeParameters.Select(tp => Fq(tp.ConstraintTypes.FirstOrDefault(c => c.TypeKind == TypeKind.Class) ?? _compilation.GetSpecialType(SpecialType.System_Object)))) + ">" : "");
                root.Append("            if (o is ").Append(serverType).Append(") return ").Append(Literal(t.ToDisplayString())).AppendLine(";");
            }
            root.AppendLine("            return null;");
            root.AppendLine("        }");
            root.AppendLine();
            root.AppendLine("        /// <summary>Every mirrored class the object is, most derived first, as open type names.</summary>");
            root.AppendLine("        static global::System.Collections.Generic.List<string> MirrorTypeChain(object o)");
            root.AppendLine("        {");
            root.AppendLine("            var chain = new global::System.Collections.Generic.List<string>();");
            foreach (var t in ordered.Where(x => x.TypeKind == TypeKind.Class))
            {
                string serverType = "global::" + t.ToDisplayString().Split('<')[0] +
                                    (t.TypeParameters.Length > 0 ? "<" + string.Join(", ", t.TypeParameters.Select(tp => Fq(tp.ConstraintTypes.FirstOrDefault(c => c.TypeKind == TypeKind.Class) ?? _compilation.GetSpecialType(SpecialType.System_Object)))) + ">" : "");
                root.Append("            if (o is ").Append(serverType).Append(") chain.Add(").Append(Literal(t.ToDisplayString().Split('<')[0])).AppendLine(");");
            }
            root.AppendLine("            return chain;");
            root.AppendLine("        }");
            root.AppendLine("    }");
            root.AppendLine();
            root.AppendLine("    public interface ITypeVisitor { void Visit<T>(); }");
            root.AppendLine("    public static class ServerTypes");
            root.AppendLine("    {");
            root.AppendLine("        /// <summary>Calls visitor.Visit&lt;T&gt;() for a toolkit or shared type given by full name.</summary>");
            root.AppendLine("        public static bool Visit(string fullName, ITypeVisitor visitor)");
            root.AppendLine("        {");
            root.AppendLine("            switch (fullName)");
            root.AppendLine("            {");
            var visited = new HashSet<string>(StringComparer.Ordinal);
            void Case(string name, string type)
            {
                if (visited.Add(name))
                    root.Append("                case ").Append(Literal(name)).Append(": visitor.Visit<").Append(type).AppendLine(">(); return true;");
            }
            foreach (var p in new[] { "System.Boolean", "System.Byte", "System.Int16", "System.Int32", "System.Int64", "System.Single", "System.Double", "System.String", "System.Object",
                         "VRageMath.Color", "VRageMath.Vector2", "VRageMath.Vector3", "VRageMath.Vector4", "VRageMath.Vector2I", "VRageMath.RectangleF", "VRageMath.Vector3D",
                         "VRage.Game.GUI.TextPanel.TextAlignment" })
                Case(p, "global::" + p);
            Case("System.Nullable`1[[System.Boolean, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089]]", "bool?");
            Case("System.Nullable`1[[System.Single, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089]]", "float?");
            foreach (var t in _mirrored.Concat(_shared).Where(t => t.TypeParameters.Length == 0 && !t.IsStatic && t.TypeKind != TypeKind.Delegate)
                         .OrderBy(t => t.ToDisplayString(), StringComparer.Ordinal))
                Case(t.ToDisplayString(), Fq(t));
            root.AppendLine("                default: return false;");
            root.AppendLine("            }");
            root.AppendLine("        }");
            root.AppendLine();
            root.AppendLine("        /// <summary>Instance test for a toolkit type given by full name, or null when it is not one.</summary>");
            root.AppendLine("        public static global::System.Func<global::Acrux.Core.Control, bool> Matcher(string fullName)");
            root.AppendLine("        {");
            root.AppendLine("            switch (fullName)");
            root.AppendLine("            {");
            var control = _compilation.GetTypeByMetadataName("Acrux.Core.Control");
            foreach (var t in _mirrored.Where(t => t.TypeParameters.Length == 0 && t.TypeKind == TypeKind.Class && control != null && InheritsFrom(t, control))
                         .OrderBy(t => t.ToDisplayString(), StringComparer.Ordinal))
                root.Append("                case ").Append(Literal(t.ToDisplayString())).Append(": return c => c is ").Append(Fq(t)).AppendLine(";");
            root.AppendLine("                default: return null;");
            root.AppendLine("            }");
            root.AppendLine("        }");
            root.AppendLine("    }");
            root.AppendLine("}");
            root.AppendLine(_report.ToString());
            _output.AddSource("Server.AcruxUiToolkitBridge.g.cs", root.ToString());

            // A toolkit object whose most derived mirrored type is abstract (an internal or unmirrored control deriving
            // from VisualElement, Decorator, ...) is wrapped as that base through a concrete stand-in, instead of null.
            var standIns = new StringBuilder();
            foreach (var t in _mirrored.Where(x => x.TypeKind == TypeKind.Class && x.IsAbstract && !x.IsStatic && x.TypeParameters.Length == 0)
                         .OrderBy(x => x.ToDisplayString(), StringComparer.Ordinal))
            {
                bool hasAbstract = false;
                for (var b = t; b != null && !hasAbstract; b = b.BaseType)
                    hasAbstract = _clientAbstractMembers.Contains(b.OriginalDefinition);
                if (hasAbstract)
                    continue;
                string standIn = "__Base_" + Sanitize(t.ToDisplayString());
                standIns.Append("    /// <summary>Stands for a toolkit object whose nearest mirrored type is the abstract ").Append(t.Name).AppendLine(".</summary>");
                standIns.Append("    internal sealed class ").Append(standIn).Append(" : ").AppendLine(Fq(t));
                standIns.AppendLine("    {");
                standIns.Append("        internal ").Append(standIn).Append("(").Append(ClientRuntime).AppendLine(".Deferred deferred) : base(deferred) { }");
                standIns.AppendLine("    }");
                _factories.Add(ClientRuntime + ".Ui.RegisterFactory(" + Literal(t.ToDisplayString()) + ", native => { var w = new " + ClientRuntime + "." + standIn +
                               "(" + ClientRuntime + ".Deferred.Value); w.__AttachNative(native); return w; });");
            }

            var client = new StringBuilder();
            client.AppendLine("// <auto-generated/>");
            client.AppendLine("namespace Acrux.Toolkit");
            client.AppendLine("{");
            client.AppendLine("    static partial class UiMirror");
            client.AppendLine("    {");
            client.AppendLine("        internal static void LoadGenerated(global::System.Collections.Generic.IDictionary<string, global::System.Delegate> api)");
            client.AppendLine("        {");
            foreach (var l in _clientLoads)
                client.Append("            ").AppendLine(l);
            foreach (var f in _factories)
                client.Append("            ").AppendLine(f);
            foreach (var c in _propertyCodecRegistrations)
                client.Append("            ").AppendLine(c);
            foreach (var h in _hookRegistrations)
                client.Append("            ").AppendLine(h);
            var valueTypes = _mirrored.Concat(_shared)
                .Where(t => t.TypeParameters.Length == 0 && !t.IsStatic && t.TypeKind != TypeKind.Delegate &&
                            (t.DeclaredAccessibility == Accessibility.Public || InternalMirrors.Contains(t.ToDisplayString())))
                .OrderBy(t => t.ToDisplayString(), StringComparer.Ordinal).ToList();
            foreach (var t in valueTypes)
                client.Append("            ").Append(ClientRuntime).Append(".Ui.RegisterClientType(").Append(Literal(t.ToDisplayString())).Append(", typeof(").Append(Fq(t)).AppendLine("));");
            client.AppendLine("        }");
            client.AppendLine();
            client.AppendLine("        /// <summary>Typed lookup on a client resource provider: the toolkit asks by type name.</summary>");
            client.AppendLine("        internal static object[] TryGetTypedResource(global::Acrux.Styling.IResourceProvider p, string typeName, string key)");
            client.AppendLine("        {");
            client.AppendLine("            switch (typeName)");
            client.AppendLine("            {");
            var caseTypes = new List<string> { "System.Boolean:bool", "System.Int32:int", "System.Int64:long", "System.Single:float", "System.Double:double", "System.String:string",
                "VRageMath.Color:global::VRageMath.Color", "VRageMath.Vector2:global::VRageMath.Vector2", "VRageMath.Vector4:global::VRageMath.Vector4" };
            caseTypes.AddRange(valueTypes.Select(t => t.ToDisplayString() + ":" + Fq(t)));
            var seenCases = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in caseTypes)
            {
                int split = entry.IndexOf(':');
                string name = entry.Substring(0, split), type = entry.Substring(split + 1);
                if (!seenCases.Add(name))
                    continue;
                client.Append("                case ").Append(Literal(name)).Append(": { ").Append(type).Append(" v; return p.TryGetResource<").Append(type)
                    .Append(">(key, out v) ? new object[] { ").Append(ClientRuntime).AppendLine(".Ui.ToWire(v) } : null; }");
            }
            client.Append("                default: { object v; return p.TryGetResource<object>(key, out v) ? new object[] { ").Append(ClientRuntime).AppendLine(".Ui.ToWire(v) } : null; }");
            client.AppendLine("            }");
            client.AppendLine("        }");
            client.AppendLine("    }");
            client.Append(standIns);
            client.AppendLine("}");
            _output.AddSource("Client.Acrux.Toolkit.UiMirror.cs", ClientGuard(client.ToString()));
        }

        static bool InheritsFrom(INamedTypeSymbol t, INamedTypeSymbol baseType)
        {
            for (var b = t; b != null; b = b.BaseType)
                if (SymbolEqualityComparer.Default.Equals(b.OriginalDefinition, baseType))
                    return true;
            return false;
        }

        static int Depth(INamedTypeSymbol t)
        {
            int d = 0;
            for (var b = t.BaseType; b != null; b = b.BaseType) d++;
            return d;
        }

        static bool IsSealedBelow(INamedTypeSymbol type, IMethodSymbol m)
        {
            for (var t = type; t != null && !SymbolEqualityComparer.Default.Equals(t.OriginalDefinition, m.ContainingType.OriginalDefinition); t = t.BaseType)
                foreach (var member in t.GetMembers(m.Name).OfType<IMethodSymbol>())
                    if (member.IsSealed && member.IsOverride)
                    {
                        for (var o = member.OverriddenMethod; o != null; o = o.OverriddenMethod)
                            if (SymbolEqualityComparer.Default.Equals(o.OriginalDefinition, m.OriginalDefinition))
                                return true;
                    }
            return false;
        }

        static bool HasConcreteImplementationBelow(INamedTypeSymbol type, IMethodSymbol m)
        {
            for (var t = type; t != null && !SymbolEqualityComparer.Default.Equals(t.OriginalDefinition, m.ContainingType.OriginalDefinition); t = t.BaseType)
                foreach (var member in t.GetMembers(m.Name).OfType<IMethodSymbol>())
                    if (member.IsOverride && !member.IsAbstract)
                        for (var o = member.OverriddenMethod; o != null; o = o.OverriddenMethod)
                            if (SymbolEqualityComparer.Default.Equals(o.OriginalDefinition, m.OriginalDefinition))
                                return true;
            return false;
        }

        void EmitDelegate(INamedTypeSymbol type)
        {
            var generics = new GenericContext();
            foreach (var tp in type.TypeParameters)
                MapTypeParameter(tp, generics);
            var invoke = type.DelegateInvokeMethod;
            var ret = Map(invoke.ReturnType, generics);
            var ps = new List<string>();
            foreach (var p in invoke.Parameters)
            {
                var m = Map(p.Type, generics);
                if (m == null || ret == null)
                {
                    _report.AppendLine("// skipped delegate " + type.ToDisplayString());
                    return;
                }
                ps.Add(m.Client + " @" + p.Name);
            }
            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated/>");
            sb.Append("namespace ").AppendLine(type.ContainingNamespace.ToDisplayString());
            sb.AppendLine("{");
            sb.Append("    public delegate ").Append(ret.Client).Append(' ').Append(type.Name)
                .Append(type.TypeParameters.Length > 0 ? "<" + string.Join(", ", type.TypeParameters.Select(t => t.Name)) + ">" : "")
                .Append('(').Append(string.Join(", ", ps)).AppendLine(");");
            sb.AppendLine("}");
            _output.AddSource("Client." + type.ToDisplayString().Replace('<', '_').Replace('>', '_') + ".cs", ClientGuard(sb.ToString()));
        }

        void EmitEnum(INamedTypeSymbol type)
        {
            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated/>");
            sb.Append("namespace ").AppendLine(type.ContainingNamespace.ToDisplayString());
            sb.AppendLine("{");
            if (type.GetAttributes().Any(a => a.AttributeClass?.Name == "FlagsAttribute"))
                sb.AppendLine("    [global::System.Flags]");
            sb.Append("    public enum ").Append(type.Name).Append(" : ").AppendLine(type.EnumUnderlyingType.ToDisplayString());
            sb.AppendLine("    {");
            foreach (var f in type.GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue))
                sb.Append("        ").Append(f.Name).Append(" = ").Append(Convert.ToString(f.ConstantValue, System.Globalization.CultureInfo.InvariantCulture)).AppendLine(",");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            _output.AddSource("Client." + type.ToDisplayString() + ".cs", ClientGuard(sb.ToString()));
        }
    }
}
