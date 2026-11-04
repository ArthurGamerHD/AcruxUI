using System;
using System.Collections;
using System.Collections.Generic;
using Adk.Mvvm;
using Acrux.Core.Input;
using Acrux.Layout;
using Acrux.Rendering;
using Acrux.Styling;
using VRageMath;

namespace Acrux.Toolkit
{
    /// <summary>Marker passed down wrapper constructor chains: only the most derived wrapper creates the toolkit object.</summary>
    public struct Deferred
    {
        public static readonly Deferred Value = new Deferred();
    }

    /// <summary>A client-side handle to an object that lives in the Acrux UI Toolkit mod.</summary>
    public interface IToolkitObject
    {
        object Native { get; }
    }

    /// <summary>
    /// Base of every generated wrapper. The wrapped toolkit object is opaque: the client only
    /// passes it back to toolkit delegates.
    /// </summary>
    public abstract class ToolkitObject : IToolkitObject
    {
        object _native;

        protected ToolkitObject(Deferred deferred)
        {
        }

        /// <summary>The toolkit object. Null only while a wrapper is being constructed.</summary>
        public object Native
        {
            get { return _native; }
        }

        /// <summary>Holds a toolkit object without claiming it (interface views of an object wrapped elsewhere).</summary>
        protected internal void __AttachDetached(object native)
        {
            _native = native;
        }

        protected internal void __AttachNative(object native)
        {
            if (native == null)
                throw new InvalidOperationException("The Acrux UI Toolkit did not create an object for " + GetType().FullName + ".");
            _native = native;
            Ui.SetPeer(native, this);
        }

        /// <summary>
        /// Full name of the toolkit object's own type. It differs from this wrapper's type when the object is an
        /// internal or unmirrored toolkit type, which is wrapped as its nearest mirrored base.
        /// </summary>
        public string ToolkitTypeName
        {
            get { return Ui.ToolkitTypeName(_native); }
        }

        /// <summary>
        /// Styled properties the toolkit object's own type declares beyond this wrapper's type (empty when the wrapper is
        /// its exact type), read and written by name through generated per-type codecs.
        /// </summary>
        public IReadOnlyList<CustomProperty> CustomProperties
        {
            get { return Ui.CustomProperties(_native); }
        }

        /// <summary>The value of one of <see cref="CustomProperties"/>, converted to its client type.</summary>
        public object GetCustomProperty(string name)
        {
            return Ui.GetCustomProperty(_native, name);
        }

        /// <summary>The value of one of <see cref="CustomProperties"/> as <typeparamref name="T"/>.</summary>
        public T GetCustomProperty<T>(string name)
        {
            object value = Ui.GetCustomProperty(_native, name);
            return value == null ? default(T) : (T)value;
        }

        /// <summary>Sets one of <see cref="CustomProperties"/> from its client type.</summary>
        public void SetCustomProperty(string name, object value)
        {
            Ui.SetCustomProperty(_native, name, value);
        }
    }

    /// <summary>
    /// A styled property of a toolkit object's own, unmirrored type, described by the toolkit at run time and converted by
    /// the generated codec for its value type.
    /// </summary>
    public sealed class CustomProperty
    {
        internal CustomProperty(string name, string valueTypeName, Type valueType, object defaultValue)
        {
            Name = name;
            ValueTypeName = valueTypeName;
            ValueType = valueType;
            DefaultValue = defaultValue;
        }

        public string Name { get; private set; }

        /// <summary>The value type as the toolkit names it, or null when no client can name it.</summary>
        public string ValueTypeName { get; private set; }

        /// <summary>The client type values convert to, or null when the property cannot cross the bridge.</summary>
        public Type ValueType { get; private set; }

        /// <summary>False for a value type only the toolkit can name: such a property is listed but cannot be read or set.</summary>
        public bool CanTransfer
        {
            get { return ValueType != null; }
        }

        public object DefaultValue { get; private set; }

        public override string ToString()
        {
            return Name;
        }
    }

    /// <summary>
    /// Client side of the Acrux UI Toolkit bridge: loads the delegate table published by the toolkit mod
    /// and converts values between client wrappers and toolkit objects.
    /// </summary>
    public static partial class Ui
    {
        static IDictionary<string, Delegate> _api;
        static Func<object, object> _getPeer;
        static Action<object, object> _setPeer;
        static Func<object, string> _typeName;
        static Func<object, string> _toolkitTypeName;
        static Func<object, object[]> _customPropertySchema;
        static Func<object, string, object> _getCustomProperty;
        static Action<object, string, object> _setCustomProperty;
        static readonly CustomProperty[] NoCustomProperties = new CustomProperty[0];
        static readonly Dictionary<string, CustomProperty[]> CustomPropertySchemas =
            new Dictionary<string, CustomProperty[]>(StringComparer.Ordinal);

        sealed class PropertyCodec
        {
            public Type ClientType;
            public Func<object, object> ToWire;
            public Func<object, object> FromWire;
        }

        static readonly Dictionary<string, PropertyCodec> PropertyCodecs =
            new Dictionary<string, PropertyCodec>(StringComparer.Ordinal);
        static readonly Dictionary<string, Func<object, object>> Factories =
            new Dictionary<string, Func<object, object>>(StringComparer.Ordinal);
        static readonly List<string> MissingMembers = new List<string>();

        /// <summary>True once the toolkit delegate table is loaded.</summary>
        public static bool IsLoaded { get; private set; }

        /// <summary>Toolkit members this client expected but the loaded toolkit does not publish.</summary>
        public static IList<string> Missing
        {
            get { return MissingMembers; }
        }

        public static void Load(IDictionary<string, Delegate> api)
        {
            if (api == null)
                throw new ArgumentNullException("api");
            api = RegisterClient(api);
            _api = api;
            MissingMembers.Clear();
            _getPeer = Get<Func<object, object>>(api, "Toolkit.GetPeer");
            _setPeer = Get<Action<object, object>>(api, "Toolkit.SetPeer");
            _typeName = Get<Func<object, string>>(api, "Toolkit.TypeName");
            _toolkitTypeName = Get<Func<object, string>>(api, "Toolkit.ToolkitTypeName");
            _customPropertySchema = Get<Func<object, object[]>>(api, "Toolkit.CustomPropertySchema");
            lock (CustomPropertySchemas)
                CustomPropertySchemas.Clear();
            _getCustomProperty = Get<Func<object, string, object>>(api, "Toolkit.GetCustomProperty");
            _setCustomProperty = Get<Action<object, string, object>>(api, "Toolkit.SetCustomProperty");
            _dispose = Get<Action<object>>(api, "Toolkit.Dispose");
            _warn = Get<Action<string>>(api, "Toolkit.Warn");
            if (_getPeer == null || _setPeer == null || _typeName == null)
                throw new InvalidOperationException("The Acrux UI Toolkit API is incomplete.");
            LoadManual(api);
            UiMirror.LoadGenerated(api);
            var registerProviders = Get<Action<Func<object, object[]>>>(api, "Interop.RegisterResourceProviderAdapter");
            if (registerProviders != null)
                registerProviders(ResourceProviderToWire);
            ResolvePendingProperties();
            IsLoaded = true;
            RegisterClientTypes();
        }

        /// <summary>Name the toolkit uses for this mod in its log and texture diagnostics.</summary>
        public static string ClientName { get; set; }

        static Action _unregisterClient;

        // Hands the toolkit this mod's own generated-texture delegate (textures count against the budget of the
        // mod that creates them) and returns the API with the members the toolkit bound to this client.
        static IDictionary<string, Delegate> RegisterClient(IDictionary<string, Delegate> api)
        {
            Delegate entry;
            var register = api.TryGetValue("Toolkit.RegisterClient", out entry)
                ? entry as Func<IDictionary<string, object>, IDictionary<string, Delegate>>
                : null;
            if (register == null)
                return api;
            var info = new Dictionary<string, object>
            {
                { "name", ClientName ?? "client" },
                { "owns", new Func<object, bool>(peer => peer is ToolkitObject) },
                { "textures.create", new Func<string, Vector2I, bool, object>(CreateGeneratedTexture) },
                { "textures.supported", new Func<bool>(() => Sandbox.ModAPI.MyAPIGateway.GeneratedTextures != null && Sandbox.ModAPI.MyAPIGateway.GeneratedTextures.IsSupported) },
                { "textures.maxSide", new Func<int>(() => Sandbox.ModAPI.MyAPIGateway.GeneratedTextures == null ? 0 : Sandbox.ModAPI.MyAPIGateway.GeneratedTextures.MaxTextureSide) },
            };
            IDictionary<string, Delegate> overrides = register(info);
            if (overrides == null || overrides.Count == 0)
                return api;
            var merged = new Dictionary<string, Delegate>(api, StringComparer.Ordinal);
            foreach (var pair in overrides)
                merged[pair.Key] = pair.Value;
            Delegate unregister;
            _unregisterClient = overrides.TryGetValue("Toolkit.UnregisterClient", out unregister) ? unregister as Action : null;
            return merged;
        }

        static object CreateGeneratedTexture(string name, Vector2I size, bool linear)
        {
            var textures = Sandbox.ModAPI.MyAPIGateway.GeneratedTextures;
            return textures == null
                ? null
                : textures.Create(name, size, linear ? VRage.Game.ModAPI.MyGeneratedTextureFormat.Linear : VRage.Game.ModAPI.MyGeneratedTextureFormat.Srgb);
        }

        public static void Unload()
        {
            Action unregister = _unregisterClient;
            _unregisterClient = null;
            if (unregister != null)
                unregister();
            IsLoaded = false;
            _api = null;
            lock (Identities)
                Identities.Clear();
        }

        public static T Get<T>(IDictionary<string, Delegate> api, string id) where T : class
        {
            Delegate value;
            if (api.TryGetValue(id, out value))
            {
                T typed = value as T;
                if (typed != null)
                    return typed;
            }
            MissingMembers.Add(id);
            return null;
        }

        internal static T Api<T>(string id) where T : class
        {
            if (_api == null)
                throw new InvalidOperationException("The Acrux UI Toolkit is not loaded.");
            return Get<T>(_api, id);
        }

        public static void RegisterFactory(string toolkitTypeName, Func<object, object> factory)
        {
            Factories[toolkitTypeName] = factory;
        }

        // ------------------------------------------------------------------ identity

        // Peers of toolkit objects that cannot store one themselves (styled properties, brushes kept by the client).
        static readonly Dictionary<object, object> Identities = new Dictionary<object, object>();

        internal static void SetPeer(object native, object peer)
        {
            _setPeer(native, peer);
        }

        internal static void Pin(object native, object peer)
        {
            lock (Identities)
                Identities[native] = peer;
        }

        static object FindPeer(object native)
        {
            object peer = _getPeer(native);
            if (peer != null)
                return peer;
            lock (Identities)
                Identities.TryGetValue(native, out peer);
            return peer;
        }

        public static object Native(object wrapper)
        {
            if (wrapper == null)
                return null;
            var toolkit = wrapper as IToolkitObject;
            if (toolkit == null)
                throw new InvalidCastException(wrapper.GetType().FullName + " is not an Acrux UI Toolkit object.");
            return toolkit.Native;
        }

        public static T Wrap<T>(object native) where T : class
        {
            if (native == null)
                return null;
            object[] exported = native as object[];
            if (exported != null && exported.Length > 1 && InterfaceTag.Equals(exported[0]))
                return exported[1] as T;
            object peer = FindPeer(native);
            T typed = peer as T;
            if (typed != null)
                return typed;
            object created = Create(native);
            typed = created as T;
            if (typed == null && created != null)
                throw new InvalidCastException("Toolkit object " + _typeName(native) + " is not a " + typeof(T).FullName + ".");
            return typed;
        }

        /// <summary>Wraps with a caller-supplied factory when no wrapper exists yet (generic wrappers).</summary>
        public static T Wrap<T>(object native, Func<object, T> factory) where T : class
        {
            if (native == null)
                return null;
            T typed = FindPeer(native) as T;
            return typed ?? factory(native);
        }

        internal static string ToolkitTypeName(object native)
        {
            return native == null || _toolkitTypeName == null ? null : _toolkitTypeName(native);
        }

        /// <summary>Registers the conversion for one styled-property value type (generated, one per type).</summary>
        public static void RegisterPropertyCodec(string valueTypeName, Type clientType, Func<object, object> toWire,
            Func<object, object> fromWire)
        {
            PropertyCodecs[valueTypeName] = new PropertyCodec { ClientType = clientType, ToWire = toWire, FromWire = fromWire };
        }

        internal static IReadOnlyList<CustomProperty> CustomProperties(object native)
        {
            if (native == null || _customPropertySchema == null)
                return NoCustomProperties;
            // The schema depends only on the toolkit type, so it is fetched once per type.
            string typeName = ToolkitTypeName(native) ?? string.Empty;
            CustomProperty[] cached;
            lock (CustomPropertySchemas)
                if (CustomPropertySchemas.TryGetValue(typeName, out cached))
                    return cached;
            object[] schema = _customPropertySchema(native) ?? new object[0];
            var properties = new CustomProperty[schema.Length];
            for (int i = 0; i < schema.Length; i++)
            {
                var entry = (object[])schema[i];
                string valueTypeName = entry[1] as string;
                PropertyCodec codec = null;
                if (valueTypeName != null)
                    PropertyCodecs.TryGetValue(valueTypeName, out codec);
                properties[i] = new CustomProperty((string)entry[0], valueTypeName, codec == null ? null : codec.ClientType,
                    codec == null || entry[2] == null ? null : codec.FromWire(entry[2]));
            }
            lock (CustomPropertySchemas)
                CustomPropertySchemas[typeName] = properties;
            return properties;
        }

        static PropertyCodec FindCodec(object native, string name)
        {
            foreach (CustomProperty property in CustomProperties(native))
            {
                if (property.Name != name)
                    continue;
                PropertyCodec codec;
                if (property.ValueTypeName == null || !PropertyCodecs.TryGetValue(property.ValueTypeName, out codec))
                    throw new NotSupportedException("Custom property '" + name + "' cannot cross the bridge.");
                return codec;
            }
            throw new ArgumentException("No custom property '" + name + "' on " + ToolkitTypeName(native) + ".");
        }

        internal static object GetCustomProperty(object native, string name)
        {
            if (_getCustomProperty == null)
                throw new InvalidOperationException("The loaded Acrux UI Toolkit has no custom properties.");
            PropertyCodec codec = FindCodec(native, name);
            object wire = _getCustomProperty(native, name);
            return wire == null ? null : codec.FromWire(wire);
        }

        internal static void SetCustomProperty(object native, string name, object value)
        {
            if (_setCustomProperty == null)
                throw new InvalidOperationException("The loaded Acrux UI Toolkit has no custom properties.");
            PropertyCodec codec = FindCodec(native, name);
            _setCustomProperty(native, name, value == null ? null : codec.ToWire(value));
        }

        static object Create(object native)
        {
            string name = _typeName(native);
            Func<object, object> factory;
            if (name == null || !Factories.TryGetValue(name, out factory))
                return null;
            return factory(native);
        }

        // ------------------------------------------------------------------ untyped values

        const string CommandTag = "acrux.command";
        const string InterfaceTag = "acrux.iface";
        const string ConverterTag = "acrux.converter";
        static readonly Dictionary<string, Type> ClientTypes = new Dictionary<string, Type>(StringComparer.Ordinal);
        static Action<string> _warn;

        /// <summary>Maps a toolkit type to the client type of the same name (converters compare target types).</summary>
        public static void RegisterClientType(string fullName, Type type)
        {
            ClientTypes[fullName] = type;
        }

        public static Type ClientType(Type toolkitType)
        {
            Type mapped;
            return toolkitType != null && ClientTypes.TryGetValue(toolkitType.FullName, out mapped) ? mapped : toolkitType;
        }

        /// <summary>Logs once in the toolkit log (missing resources and similar soft failures).</summary>
        public static void WarnOnce(string message)
        {
            if (_warn != null)
                _warn(message);
        }

        static object ConverterToWire(Acrux.Data.Converters.IValueConverter converter)
        {
            var contextual = converter as Acrux.Extensions.IContextValueConverter;
            return new object[]
            {
                ConverterTag,
                converter,
                new Func<object, Type, object, object, object, object>((value, targetType, parameter, culture, target) =>
                {
                    var clientTarget = Wrap<Acrux.Core.Control>(target);
                    object result = contextual != null && clientTarget != null
                        ? contextual.Convert(FromWire(value), ClientType(targetType), FromWire(parameter), culture as System.Globalization.CultureInfo, clientTarget)
                        : converter.Convert(FromWire(value), ClientType(targetType), FromWire(parameter), culture as System.Globalization.CultureInfo);
                    return ToWire(result);
                }),
                new Func<object, Type, object, object, object>((value, targetType, parameter, culture) =>
                    ToWire(converter.ConvertBack(FromWire(value), ClientType(targetType), FromWire(parameter), culture as System.Globalization.CultureInfo)))
            };
        }
        static Action<object> _dispose;

        public static void DisposeNative(object native)
        {
            if (native != null && _dispose != null)
                _dispose(native);
        }
        const string ItemsTag = "acrux.items";

        /// <summary>Converts a client value passed where the toolkit expects object.</summary>
        public static object ToWire(object value)
        {
            if (value == null)
                return null;
            var toolkit = value as IToolkitObject;
            if (toolkit != null)
                return toolkit.Native;
            var command = value as ICommand;
            if (command != null)
                return CommandToWire(command);
            var converter = value as Acrux.Data.Converters.IValueConverter;
            if (converter != null)
                return ConverterToWire(converter);
            // A mirrored toolkit enum crosses as its number (the toolkit has its own type of that name). Any other enum
            // (this mod's own, or a game type both sides share) stays the object it is: the toolkit only holds it,
            // and handing it back unchanged is what lets object slots (Tag, items, bindings) round-trip it.
            if (value is Enum)
                return ClientTypes.ContainsKey(value.GetType().FullName) ? (object)Convert.ToInt32(value) : value;
            if (value is GridLength)
                return GridLengthToWire((GridLength)value);
            if (value is TextureSource)
                return TextureToWire((TextureSource)value);
            return value;
        }

        /// <summary>Converts a toolkit value received as object.</summary>
        public static object FromWire(object value)
        {
            if (value == null || value is string || value is ValueType)
                return value;
            object peer = FindPeer(value);
            if (peer != null)
                return peer;
            object[] bundle = value as object[];
            if (bundle != null && bundle.Length > 0 && ReferenceEquals(bundle[0], CommandTag))
                return CommandFromWire(value);
            if (bundle != null && bundle.Length > 1 && (InterfaceTag.Equals(bundle[0]) || ConverterTag.Equals(bundle[0])))
                return bundle[1];
            object created = Create(value);
            return created ?? value;
        }

        public static T FromWire<T>(object value)
        {
            return FromWireValue<T>(value);
        }

        public static object ToWireValue<T>(T value)
        {
            return ToWire(value);
        }

        public static T FromWireValue<T>(object value)
        {
            if (value == null)
                return default(T);
            if (value is T)
                return (T)value;
            if (typeof(T) == typeof(GridLength) && value is Vector2)
                return (T)(object)GridLengthFromWire((Vector2)value);
            if (typeof(T) == typeof(TextureSource))
                return (T)(object)TextureFromWire(value);
            if (typeof(T) == typeof(ICommand))
                return (T)CommandFromWire(value);
            object converted = FromWire(value);
            if (converted is T)
                return (T)converted;
            // Enums cross the bridge as integers; an int cannot be unboxed as an enum.
            object fallback = default(T);
            if (fallback is Enum && (converted is int || converted is long || converted is short || converted is byte || converted is Enum))
                return (T)Enum.ToObject(typeof(T), Convert.ToInt64(converted));
            return (T)converted;
        }

        // ------------------------------------------------------------------ codecs

        public static object GridLengthToWire(GridLength value)
        {
            return new Vector2(value.Value, (int)value.GridUnitType);
        }

        public static GridLength GridLengthFromWire(Vector2 value)
        {
            return new GridLength(value.X, (GridUnitType)(int)value.Y);
        }

        public static object TextureToWire(TextureSource texture)
        {
            if (texture.IsEmpty)
                return null;
            return texture.Key != null ? (object)texture.Key : texture.NativeHandle;
        }

        public static TextureSource TextureFromWire(object wire)
        {
            if (wire == null)
                return default(TextureSource);
            string key = wire as string;
            return key != null ? TextureSource.FromKey(key) : TextureSource.FromNative(wire);
        }

        sealed class WireCommand : ICommand
        {
            readonly Action<object> _execute;
            readonly Func<object, bool> _canExecute;
            public readonly object Wire;

            public WireCommand(object wire, Action<object> execute, Func<object, bool> canExecute)
            {
                Wire = wire;
                _execute = execute;
                _canExecute = canExecute;
            }

            public bool CanExecute(object parameter)
            {
                return _canExecute == null || _canExecute(ToWire(parameter));
            }

            public void Execute(object parameter)
            {
                if (_execute != null)
                    _execute(ToWire(parameter));
            }
        }

        public static object CommandToWire(ICommand command)
        {
            if (command == null)
                return null;
            var wire = command as WireCommand;
            if (wire != null)
                return wire.Wire;
            return new object[]
            {
                CommandTag,
                new Action<object>(parameter => command.Execute(FromWire(parameter))),
                new Func<object, bool>(parameter => command.CanExecute(FromWire(parameter))),
                command
            };
        }

        public static ICommand CommandFromWire(object wire)
        {
            if (wire == null)
                return null;
            var command = wire as ICommand;
            if (command != null)
                return command;
            object[] bundle = wire as object[];
            if (bundle != null && bundle.Length == 4 && ReferenceEquals(bundle[0], CommandTag))
            {
                command = bundle[3] as ICommand;
                if (command != null)
                    return command;
                return new WireCommand(wire, bundle[1] as Action<object>, bundle[2] as Func<object, bool>);
            }
            return null;
        }

        /// <summary>
        /// Items sources stay client objects. Observable sources are announced with a subscription
        /// delegate so the toolkit can follow changes; wrapper items are unwrapped lazily.
        /// </summary>
        public static object ItemsToWire(IEnumerable items)
        {
            if (items == null)
                return null;
            var observable = items as INotifyCollectionChanged;
            Func<Action<int, IList, IList, int, int>, Action> subscribe = null;
            if (observable != null)
            {
                subscribe = callback =>
                {
                    NotifyCollectionChangedEventHandler handler = (sender, args) =>
                        callback((int)args.Action, UnwrapList(args.NewItems), UnwrapList(args.OldItems),
                            args.NewStartingIndex, args.OldStartingIndex);
                    observable.CollectionChanged += handler;
                    return () => observable.CollectionChanged -= handler;
                };
            }
            return new object[] { ItemsTag, items, subscribe, new Func<object, object>(ToWire) };
        }

        public static IEnumerable ItemsFromWire(object wire)
        {
            if (wire == null)
                return null;
            object[] bundle = wire as object[];
            if (bundle != null && bundle.Length == 4 && ReferenceEquals(bundle[0], ItemsTag))
                return bundle[1] as IEnumerable;
            var enumerable = wire as IEnumerable;
            if (enumerable == null)
                return null;
            var list = new List<object>();
            foreach (object item in enumerable)
                list.Add(FromWire(item));
            return list;
        }

        static IList UnwrapList(IList items)
        {
            if (items == null)
                return null;
            var result = new List<object>(items.Count);
            foreach (object item in items)
                result.Add(ToWire(item));
            return result;
        }

        public static object[] ArrayToWire<T>(T[] values, Func<T, object> convert)
        {
            if (values == null)
                return null;
            var result = new object[values.Length];
            for (int i = 0; i < values.Length; i++)
                result[i] = convert(values[i]);
            return result;
        }

        public static T[] ArrayFromWire<T>(object[] values, Func<object, T> convert)
        {
            if (values == null)
                return null;
            var result = new T[values.Length];
            for (int i = 0; i < values.Length; i++)
                result[i] = convert(values[i]);
            return result;
        }

        public static object ListToWire<T>(IEnumerable<T> values, Func<T, object> convert)
        {
            if (values == null)
                return null;
            var result = new List<object>();
            foreach (T value in values)
                result.Add(convert(value));
            return result;
        }

        public static List<T> ListFromWire<T>(object wire, Func<object, T> convert)
        {
            var enumerable = wire as IEnumerable;
            if (enumerable == null)
                return null;
            var result = new List<T>();
            foreach (object value in enumerable)
                result.Add(convert(value));
            return result;
        }

        public static List<T> ReadOnlyListFromWire<T>(object wire, Func<object, T> convert)
        {
            return ListFromWire(wire, convert);
        }

        const string ListTag = "acrux.list";

        /// <summary>A live view of a toolkit list (see the toolkit's ExportList); a copy for anything else.</summary>
        public static IList<T> LiveListFromWire<T>(object wire, Func<object, T> fromWire, Func<T, object> toWire)
        {
            var bundle = wire as object[];
            if (bundle != null && bundle.Length == 8 && ListTag.Equals(bundle[0]))
                return new RemoteList<T>(bundle, fromWire, toWire);
            return ListFromWire(wire, fromWire);
        }

        public static object LiveListToWire<T>(IList<T> list, Func<T, object> toWire)
        {
            var remote = list as RemoteList<T>;
            return remote != null ? remote.Bundle : ListToWire(list, toWire);
        }
    }

    /// <summary>A client view of a list owned by the toolkit; every operation runs on the toolkit list.</summary>
    public sealed class RemoteList<T> : IList<T>
    {
        readonly Func<int> _count;
        readonly Func<int, object> _get;
        readonly Action<int, object> _set;
        readonly Action<int, object> _insert;
        readonly Action<int> _removeAt;
        readonly Action _clear;
        readonly Func<object, T> _fromWire;
        readonly Func<T, object> _toWire;

        internal RemoteList(object[] bundle, Func<object, T> fromWire, Func<T, object> toWire)
        {
            Bundle = bundle;
            _count = (Func<int>)bundle[2];
            _get = (Func<int, object>)bundle[3];
            _set = (Action<int, object>)bundle[4];
            _insert = (Action<int, object>)bundle[5];
            _removeAt = (Action<int>)bundle[6];
            _clear = (Action)bundle[7];
            _fromWire = fromWire;
            _toWire = toWire;
        }

        internal object[] Bundle { get; private set; }

        public int Count => _count();
        public bool IsReadOnly => false;

        public T this[int index]
        {
            get { return _fromWire(_get(index)); }
            set { _set(index, _toWire(value)); }
        }

        public void Add(T item) => _insert(_count(), _toWire(item));
        public void Insert(int index, T item) => _insert(index, _toWire(item));
        public void RemoveAt(int index) => _removeAt(index);
        public void Clear() => _clear();

        public int IndexOf(T item)
        {
            var comparer = EqualityComparer<T>.Default;
            int count = _count();
            for (int i = 0; i < count; i++)
                if (comparer.Equals(this[i], item))
                    return i;
            return -1;
        }

        public bool Contains(T item) => IndexOf(item) >= 0;

        public bool Remove(T item)
        {
            int index = IndexOf(item);
            if (index < 0)
                return false;
            _removeAt(index);
            return true;
        }

        public void CopyTo(T[] array, int arrayIndex)
        {
            int count = _count();
            for (int i = 0; i < count; i++)
                array[arrayIndex + i] = this[i];
        }

        public IEnumerator<T> GetEnumerator()
        {
            int count = _count();
            for (int i = 0; i < count; i++)
                yield return this[i];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public static partial class Ui
    {
        // Exposes a client data context that provides resources; lookups are typed on the client.
        static object[] ResourceProviderToWire(object value)
        {
            var provider = value as IResourceProvider;
            if (provider == null)
                return null;
            return new object[]
            {
                new Func<string, string, object[]>((typeName, key) => UiMirror.TryGetTypedResource(provider, typeName, key)),
                new Func<Action<string>, Action>(callback =>
                {
                    Action<string> handler = key => callback(key);
                    provider.ResourceChanged += handler;
                    return () => provider.ResourceChanged -= handler;
                })
            };
        }
    }

    /// <summary>Generated half fills LoadGenerated.</summary>
    static partial class UiMirror
    {
    }
}
