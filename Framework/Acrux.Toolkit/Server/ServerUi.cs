using System;
using System.Collections;
using System.Collections.Generic;
using Adk.Mvvm;
using Acrux.Core;
using Acrux.Core.Input;
using Acrux.Core.Invalidation;
using Acrux.Core.PropertySystem;
using Acrux.Layout;
using Acrux.Markup;
using Acrux.Rendering;
using Acrux.Styling;
using Acrux.Toolkit;
using VRageMath;

namespace Acrux.Toolkit.Server
{
    /// <summary>Toolkit side of the client bridge: value codecs, peers and hand-written endpoints.</summary>
    public static class ServerUi
    {
        const string CommandTag = "acrux.command";
        const string ConverterTag = "acrux.converter";
        const string ItemsTag = "acrux.items";

        // Styled properties are a finite set; clients need them to keep a stable identity.
        static readonly Dictionary<object, object> PinnedPeers = new Dictionary<object, object>();

        public static object GetPeer(object native)
        {
            if (native == null)
                return null;
            var remote = native as IRemotePeer;
            if (remote != null && remote.RemotePeer != null)
                return remote.RemotePeer;
            var visual = native as Visual;
            if (visual != null)
                return visual.ExternalPeer;
            var host = native as IExternalPeerHost;
            if (host != null)
                return host.ExternalPeer;
            if (native is IStyledProperty)
                lock (PinnedPeers)
                {
                    object peer;
                    return PinnedPeers.TryGetValue(native, out peer) ? peer : null;
                }
            return null;
        }

        public static void SetPeer(object native, object peer)
        {
            var visual = native as Visual;
            if (visual != null)
            {
                visual.ExternalPeer = peer;
                return;
            }
            var host = native as IExternalPeerHost;
            if (host != null)
            {
                host.ExternalPeer = peer;
                return;
            }
            if (native is IStyledProperty)
                lock (PinnedPeers)
                    PinnedPeers[native] = peer;
        }

        // ------------------------------------------------------------------ untyped values

        static bool IsToolkitEnum(Type type)
        {
            // Type.Namespace is not whitelisted; FullName is.
            string name = type.FullName;
            return name != null && name.StartsWith("Acrux.", StringComparison.Ordinal);
        }

        public static object ToWire(object value)
        {
            if (value == null)
                return null;
            var command = value as RemoteCommand;
            if (command != null)
                return command.Wire;
            var items = value as RemoteItems;
            if (items != null)
                return items.Wire;
            var impl = value as IClientImpl;
            if (impl != null)
                return impl.Bundle;
            var converter = value as RemoteConverter;
            if (converter != null)
                return converter.Wire;
            // Only the toolkit's own enums cross as numbers (each client has a mirror type of the same name). A client's
            // enum held in an object slot (Tag, items, bindings) goes back as the same object, as do game enums.
            if (value is Enum && IsToolkitEnum(value.GetType()))
                return Convert.ToInt32(value);
            if (value is GridLength)
            {
                var length = (GridLength)value;
                return new Vector2(length.Value, (int)length.GridUnitType);
            }
            if (value is TextureSource)
                return TextureToWire((TextureSource)value);
            if (value is ICommand)
                return CommandToWire((ICommand)value);
            return value;
        }

        public static object FromWire(object value)
        {
            object[] bundle = value as object[];
            if (bundle != null && bundle.Length > 0)
            {
                if (ReferenceEquals(bundle[0], CommandTag) || CommandTag.Equals(bundle[0]))
                    return CommandFromWire(value);
                if (ReferenceEquals(bundle[0], ItemsTag) || ItemsTag.Equals(bundle[0]))
                    return ItemsFromWire(value);
                if (ConverterTag.Equals(bundle[0]) && bundle.Length == 4)
                    return new RemoteConverter(value, bundle[2] as Func<object, Type, object, object, object, object>,
                        bundle[3] as Func<object, Type, object, object, object>);
            }
            return value;
        }

        const string InterfaceTag = "acrux.iface";

        /// <summary>A toolkit interface value from a client: either a toolkit object, or a client implementation bundle.</summary>
        public static T ImportInterface<T>(object wire, Func<object[], T> importer) where T : class
        {
            if (wire == null)
                return null;
            object[] bundle = wire as object[];
            if (bundle != null && bundle.Length > 1 && InterfaceTag.Equals(bundle[0]))
            {
                if (importer == null)
                    throw new NotSupportedException("A client cannot implement " + typeof(T).FullName + ".");
                return importer(bundle);
            }
            return (T)wire;
        }

        public static object ExportInterface(object value)
        {
            var impl = value as IClientImpl;
            return impl != null ? impl.Bundle : value;
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

        public static ICommand CommandFromWire(object wire)
        {
            if (wire == null)
                return null;
            var command = wire as ICommand;
            if (command != null)
                return command;
            object[] bundle = wire as object[];
            if (bundle == null || bundle.Length != 4)
                return null;
            return new RemoteCommand(wire, bundle[1] as Action<object>, bundle[2] as Func<object, bool>);
        }

        public static object CommandToWire(ICommand command)
        {
            if (command == null)
                return null;
            var remote = command as RemoteCommand;
            if (remote != null)
                return remote.Wire;
            return new object[]
            {
                CommandTag,
                new Action<object>(parameter => command.Execute(FromWire(parameter))),
                new Func<object, bool>(parameter => command.CanExecute(FromWire(parameter))),
                null
            };
        }

        public static IEnumerable ItemsFromWire(object wire)
        {
            if (wire == null)
                return null;
            object[] bundle = wire as object[];
            if (bundle != null && bundle.Length == 4 && ItemsTag.Equals(bundle[0]))
                return new RemoteItems(wire, bundle[1] as IEnumerable,
                    bundle[2] as Func<Action<int, IList, IList, int, int>, Action>,
                    bundle[3] as Func<object, object>);
            return wire as IEnumerable;
        }

        public static object ItemsToWire(IEnumerable items)
        {
            var remote = items as RemoteItems;
            return remote != null ? remote.Wire : items;
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

        /// <summary>
        /// A mutable toolkit list, handed to a client as a live view: element operations run on this list, so a
        /// client's <c>Items.Add(...)</c> changes the toolkit object instead of a copy.
        /// </summary>
        public static object ExportList<T>(IList<T> list, Func<T, object> toWire, Func<object, T> fromWire)
        {
            if (list == null)
                return null;
            return new object[]
            {
                ListTag, list,
                new Func<int>(() => list.Count),
                new Func<int, object>(index => toWire(list[index])),
                new Action<int, object>((index, value) => list[index] = fromWire(value)),
                new Action<int, object>((index, value) => list.Insert(index, fromWire(value))),
                new Action<int>(index => list.RemoveAt(index)),
                new Action(() => list.Clear()),
            };
        }

        /// <summary>A list from a client: the toolkit's own list when the client passed a live view back, else a copy.</summary>
        public static IList<T> LiveListFromWire<T>(object wire, Func<object, T> convert)
        {
            var bundle = wire as object[];
            if (bundle != null && bundle.Length > 1 && ListTag.Equals(bundle[0]))
            {
                var own = bundle[1] as IList<T>;
                if (own != null)
                    return own;
            }
            return ListFromWire(wire, convert);
        }

        // ------------------------------------------------------------------ hand-written endpoints

        // Style sources come from the clients' loaders, then the toolkit's content. Each client registers one
        // loader and one failure reporter (bound to it in RegisterClient); a failure goes only to the client whose
        // content it is. Clients keep their styles under their own folder (Data/Gui/Styles/<Mod>/...), so a source
        // belongs to the client that served the most specific folder above it.
        sealed class StyleClient
        {
            public object Key;
            public string Name;
            public Func<string, string> Loader;
            public Action<string, Exception> Reporter;
        }

        static readonly StyleClient ToolkitStyles = new StyleClient { Name = "Acrux UI" };
        static readonly List<StyleClient> StyleClients = new List<StyleClient>();
        // Older client runtimes register through the shared API, without a name: they keep receiving every failure.
        static readonly List<Func<string, string>> SourceLoaders = new List<Func<string, string>>();
        static readonly List<Action<string, Exception>> SourceFailureReporters = new List<Action<string, Exception>>();
        // Served source (normalized) -> the client (or the toolkit) that served it.
        static readonly Dictionary<string, StyleClient> SourceOwners = new Dictionary<string, StyleClient>(StringComparer.OrdinalIgnoreCase);
        static Func<string, string> _toolkitSourceLoader;

        static StyleClient FindStyleClient(object key)
        {
            for (int i = 0; i < StyleClients.Count; i++)
                if (ReferenceEquals(StyleClients[i].Key, key))
                    return StyleClients[i];
            return null;
        }

        static void SetClientSourceLoader(object key, string name, Func<string, string> loader)
        {
            lock (StyleClients)
            {
                StyleClient client = FindStyleClient(key);
                if (client == null)
                {
                    if (loader == null)
                        return;
                    client = new StyleClient { Key = key, Name = name };
                    StyleClients.Add(client);
                }
                client.Loader = loader;
            }
            StyleSheet.ClearSourceCache();
        }

        static void SetClientSourceFailureReporter(object key, string name, Action<string, Exception> reporter)
        {
            lock (StyleClients)
            {
                StyleClient client = FindStyleClient(key);
                if (client == null)
                {
                    if (reporter == null)
                        return;
                    client = new StyleClient { Key = key, Name = name };
                    StyleClients.Add(client);
                }
                client.Reporter = reporter;
            }
        }

        static void RemoveStyleClient(object key)
        {
            lock (StyleClients)
            {
                StyleClient client = FindStyleClient(key);
                if (client != null)
                    StyleClients.Remove(client);
            }
            lock (SourceOwners)
            {
                var stale = new List<string>();
                foreach (KeyValuePair<string, StyleClient> served in SourceOwners)
                    if (ReferenceEquals(served.Value.Key, key))
                        stale.Add(served.Key);
                foreach (string source in stale)
                    SourceOwners.Remove(source);
            }
            StyleSheet.ClearSourceCache();
        }

        static string NormalizeSource(string source)
        {
            return source == null ? string.Empty : source.Replace('\\', '/').TrimStart('/');
        }

        static void RecordSourceOwner(string source, StyleClient owner)
        {
            lock (SourceOwners)
                SourceOwners[NormalizeSource(source)] = owner;
        }

        // The served source sharing the longest folder with this one names its owner; null when none shares one.
        static StyleClient FindSourceOwner(string source)
        {
            string normalized = NormalizeSource(source);
            StyleClient owner = null;
            int best = 0;
            lock (SourceOwners)
            {
                if (SourceOwners.TryGetValue(normalized, out owner))
                    return owner;
                foreach (KeyValuePair<string, StyleClient> served in SourceOwners)
                {
                    int shared = SharedFolderLength(normalized, served.Key);
                    if (shared <= best)
                        continue;
                    best = shared;
                    owner = served.Value;
                }
            }
            return owner;
        }

        static int SharedFolderLength(string a, string b)
        {
            int length = Math.Min(a.Length, b.Length);
            int folder = 0;
            for (int i = 0; i < length; i++)
            {
                if (char.ToLowerInvariant(a[i]) != char.ToLowerInvariant(b[i]))
                    break;
                if (a[i] == '/')
                    folder = i + 1;
            }
            return folder;
        }

        /// <summary>Installs the toolkit style source loader: client loaders first, then the toolkit's own content.</summary>
        public static void InstallSourceLoader(Func<string, string> toolkitLoader)
        {
            _toolkitSourceLoader = toolkitLoader;
            StyleSheet.SourceLoader = LoadSource;
            StyleSheet.SourceLoadFailed = ReportSourceFailure;
        }

        static string LoadSource(string source)
        {
            StyleClient[] clients;
            lock (StyleClients)
                clients = StyleClients.ToArray();
            for (int i = 0; i < clients.Length; i++)
            {
                Func<string, string> loader = clients[i].Loader;
                string text = loader == null ? null : loader(source);
                if (text == null)
                    continue;
                RecordSourceOwner(source, clients[i]);
                return text;
            }
            Func<string, string>[] loaders;
            lock (SourceLoaders)
                loaders = SourceLoaders.ToArray();
            for (int i = 0; i < loaders.Length; i++)
            {
                string text = loaders[i](source);
                if (text != null)
                    return text;
            }
            if (_toolkitSourceLoader == null)
                throw new System.IO.FileNotFoundException("No style source loader resolved '" + source + "'.");
            string toolkitText = _toolkitSourceLoader(source);
            RecordSourceOwner(source, ToolkitStyles);
            return toolkitText;
        }

        static void ReportSourceFailure(string source, Exception error)
        {
            StyleClient owner = FindSourceOwner(source);
            ExternalInterop.LogClient(owner == null ? null : owner.Name, "Error", "Skipped style source '" + source + "': " +
                (error == null ? "unknown error" : error.GetType().Name + ": " + error.Message));

            Action<string, Exception> clientReporter = owner == null ? null : owner.Reporter;
            if (clientReporter != null)
                clientReporter(source, error);

            Action<string, Exception>[] reporters;
            lock (SourceFailureReporters)
                reporters = SourceFailureReporters.ToArray();
            foreach (var reporter in reporters)
                reporter(source, error);
        }

        // Called by each client once it has the API. The client hands over its own generated-texture delegate (each mod
        // has its own texture budget) and an ownership test for its wrappers; the reply holds entries that replace
        // shared API members with ones bound to that client.
        static IDictionary<string, Delegate> RegisterClient(IDictionary<string, object> info)
        {
            var overrides = new Dictionary<string, Delegate>();
            if (info == null)
                return overrides;
            object value;
            string owner = info.TryGetValue("name", out value) ? value as string : null;
            var create = info.TryGetValue("textures.create", out value) ? value as Func<string, Vector2I, bool, object> : null;
            var supported = info.TryGetValue("textures.supported", out value) ? value as Func<bool> : null;
            var maxSide = info.TryGetValue("textures.maxSide", out value) ? value as Func<int> : null;
            var owns = info.TryGetValue("owns", out value) ? value as Func<object, bool> : null;
            if (create == null || supported == null || maxSide == null)
                return overrides;

            var pool = new GeneratedTexturePool(owner, create, supported, maxSide, owns);
            GeneratedTexturePool.Register(pool);
            // Override hooks are routed by peer ownership: every client's handler casts to its own wrapper types.
            if (owns != null)
                AcruxUiToolkitBridge.BindClientHooks(pool, overrides);
            // Style sources and their failures are this client's: a missing file is reported to it alone, by name.
            string styleOwner = pool.Owner;
            overrides["StyleSheet.RegisterSourceLoader"] = new Action<Func<string, string>>(loader =>
                SetClientSourceLoader(pool, styleOwner, loader));
            overrides["StyleSheet.RegisterSourceLoadFailed"] = new Action<Action<string, Exception>>(report =>
                SetClientSourceFailureReporter(pool, styleOwner, report));
            overrides["Toolkit.UnregisterClient"] = new Action(() =>
            {
                AcruxUiToolkitBridge.UnbindClientHooks(pool);
                RemoveStyleClient(pool);
                GeneratedTexturePool.Unregister(pool);
            });
            overrides["Acrux.Lcd.LcdDynamicTexture.ctor(string,global::VRageMath.Vector2I)"] =
                new Func<string, Vector2I, object>((name, size) => new Acrux.Lcd.LcdDynamicTexture(name, size, pool));
            overrides["Acrux.ScreenSpace.DynamicMapTexture.ctor(string,global::VRageMath.Vector2I)"] =
                new Func<string, Vector2I, object>((name, size) => new Acrux.ScreenSpace.DynamicMapTexture(name, size, pool));
            overrides["Acrux.Lcd.LcdDynamicTexture.FitToRenderer`0(global::VRageMath.Vector2I)s"] =
                new Func<Vector2I, Vector2I>(size => Acrux.Lcd.LcdDynamicTexture.FitToRenderer(size, pool));
            overrides["Acrux.ScreenSpace.DynamicMapTexture.FitToRenderer`0(global::VRageMath.Vector2I)s"] =
                new Func<Vector2I, Vector2I>(size => Acrux.ScreenSpace.DynamicMapTexture.FitToRenderer(size, pool));
            overrides["Acrux.Lcd.LcdDynamicTextureFactory.ctor()"] =
                new Func<object>(() => new Acrux.Lcd.LcdDynamicTextureFactory(() => pool));
            overrides["Acrux.ScreenSpace.ScreenSpaceDynamicTextureFactory.ctor()"] =
                new Func<object>(() => new Acrux.ScreenSpace.ScreenSpaceDynamicTextureFactory(() => pool));
            foreach (var bind in ClientBindings)
                bind(pool, overrides);
            return overrides;
        }

        // Toolkit features outside Acrux add API members bound to one client's texture pool here (for example UI the
        // toolkit builds itself and draws on that client's surface).
        internal static readonly List<Action<GeneratedTexturePool, Dictionary<string, Delegate>>> ClientBindings =
            new List<Action<GeneratedTexturePool, Dictionary<string, Delegate>>>();

        public static void Register(Dictionary<string, Delegate> api)
        {
            api["Toolkit.GetPeer"] = new Func<object, object>(GetPeer);
            api["Toolkit.SetPeer"] = new Action<object, object>(SetPeer);
            api["Binding.AttachTwoWay"] = new Action<object, object, Func<object, object>, Action<object, object>, string>((target, property, getter, setter, name) =>
                ((IStyledProperty)property).AttachTwoWay((VisualElement)target, source => FromWire(getter(source)), (source, value) => setter(source, ToWire(value)), name));
            api["Toolkit.Warn"] = new Action<string>(ExternalInterop.WarnOnce);
            api["Toolkit.RegisterClient"] = new Func<IDictionary<string, object>, IDictionary<string, Delegate>>(RegisterClient);
            api["Interop.RegisterResourceProviderAdapter"] = new Action<Func<object, object[]>>(adapter =>
            {
                if (adapter == null)
                    return;
                lock (ResourceAdapters)
                    ResourceAdapters.Add(adapter);
                ExternalInterop.ResourceProviderAdapter = AdaptResourceProvider;
            });
            api["Toolkit.Dispose"] = new Action<object>(native =>
            {
                var disposable = native as IDisposable;
                if (disposable != null)
                    disposable.Dispose();
            });

            api["Property.Register"] = new Func<object, string, string, object, int, Func<object, bool>, string, object>(RegisterProperty);
            api["Property.Info"] = new Func<object, object[]>(property =>
            {
                var p = (IStyledProperty)property;
                return new object[] { p.Name, p.OwnerType, ToWire(p.DefaultValueObject), (int)p.Invalidation };
            });
            api["Property.GetValue"] = new Func<object, object, object>((control, property) =>
                ToWire(((IStyledProperty)property).GetValueObject((Control)control)));
            api["Property.SetValue"] = new Action<object, object, object>((control, property, value) =>
                ((IStyledProperty)property).SetValueObject((Control)control, FromWire(value)));
            api["Property.ClearValue"] = new Action<object, object>((control, property) =>
                ((IStyledProperty)property).ClearValueObject((Control)control));
            api["Property.SetDynamicResource"] = new Action<object, object, string>((control, property, key) =>
                ((IStyledProperty)property).SetDynamicResource((Control)control, key));
            api["Control.TryFindResource"] = new Func<object, string, object[]>((control, key) =>
            {
                object value;
                return ((Control)control).TryFindResource(key, out value) ? new[] { ToWire(value) } : null;
            });
            api["ResourceDictionary.TryGetValue"] = new Func<object, string, object[]>((dictionary, key) =>
            {
                object value;
                return ((ResourceDictionary)dictionary).TryGetValue(key, out value) ? new[] { ToWire(value) } : null;
            });

            api["StyleSheet.RegisterSourceLoader"] = new Action<Func<string, string>>(loader =>
            {
                if (loader == null)
                    return;
                lock (SourceLoaders)
                    if (!SourceLoaders.Contains(loader))
                        SourceLoaders.Add(loader);
                StyleSheet.ClearSourceCache();
            });
            api["StyleSheet.RegisterSourceLoadFailed"] = new Action<Action<string, Exception>>(report =>
            {
                if (report == null)
                    return;
                lock (SourceFailureReporters)
                    SourceFailureReporters.Add(report);
            });

            api["Types.RegisterStyleType"] = new Action<string, object, Func<object, bool>>((selector, ownerType, isType) =>
            {
                try
                {
                    StylePropertyRegistry.Global.RegisterExternalType(selector, (Type)ownerType, control =>
                    {
                        var remote = control as IRemotePeer;
                        return remote != null && remote.RemotePeer != null && isType(remote.RemotePeer);
                    });
                }
                catch (InvalidOperationException)
                {
                    // The selector already names another type (for example a toolkit control); the full name still works.
                }
            });
            api["Types.RegisterTemplateType"] = new Action<string, Func<object>>((xmlName, factory) =>
                Acrux.Styling.ControlTemplate.TemplateTypes.Register<Control>(xmlName, () => (Control)factory()));
            api["Types.RegisterTemplateProperty"] = new Action<string, string, string, Action<object, object>>((xmlName, property, valueType, setter) =>
            {
                var visitor = new TemplatePropertyVisitor { XmlName = xmlName, Property = property, Setter = setter };
                if (!ServerTypes.Visit(valueType, visitor))
                    visitor.Visit<object>();
            });
        }

        static readonly List<Func<object, object[]>> ResourceAdapters = new List<Func<object, object[]>>();

        // A client data context that provides resources (the client's IResourceProvider).
        static IResourceProvider AdaptResourceProvider(object value)
        {
            Func<object, object[]>[] adapters;
            lock (ResourceAdapters)
                adapters = ResourceAdapters.ToArray();
            for (int i = 0; i < adapters.Length; i++)
            {
                object[] bundle = adapters[i](value);
                if (bundle != null)
                    return new RemoteResourceProvider(bundle[0] as Func<string, string, object[]>, bundle[1] as Func<Action<string>, Action>);
            }
            return null;
        }

        sealed class TemplatePropertyVisitor : ITypeVisitor
        {
            public string XmlName;
            public string Property;
            public Action<object, object> Setter;

            public void Visit<T>()
            {
                Action<object, object> setter = Setter;
                Acrux.Styling.ControlTemplate.TemplateTypes.RegisterProperty<Control, T>(XmlName, Property, (control, value) => setter(control, ToWire(value)));
            }
        }

        sealed class PropertyVisitor : ITypeVisitor
        {
            public string Name;
            public Type OwnerType;
            public object DefaultWire;
            public UiInvalidation Invalidation;
            public Func<Control, bool> Matches;
            public IStyledProperty Result;

            public void Visit<T>()
            {
                T defaultValue = DefaultWire == null ? default(T) : TypeRegistry.ConvertValue<T>(FromWire(DefaultWire));
                var property = new StyledProperty<T>(Name, OwnerType, defaultValue, Invalidation, Matches);
                StyledPropertyCatalog.Register(property);
                Result = property;
            }
        }

        static object RegisterProperty(object ownerType, string name, string valueTypeName, object defaultWire, int invalidation,
            Func<object, bool> isOwner, string ownerSelector)
        {
            Func<Control, bool> toolkitOwner = ownerSelector == null ? null : ServerTypes.Matcher(ownerSelector);
            Func<Control, bool> matches = control =>
            {
                if (control == null)
                    return false;
                if (toolkitOwner != null && toolkitOwner(control))
                    return true;
                var remote = control as IRemotePeer;
                return remote != null && remote.RemotePeer != null && isOwner(remote.RemotePeer);
            };
            var visitor = new PropertyVisitor
            {
                Name = name,
                OwnerType = (Type)ownerType,
                DefaultWire = defaultWire,
                Invalidation = (UiInvalidation)invalidation,
                Matches = matches,
            };
            if (!ServerTypes.Visit(valueTypeName, visitor))
                visitor.Visit<object>();
            return visitor.Result;
        }
    }

    /// <summary>A toolkit interface implemented by a client object.</summary>
    public interface IClientImpl
    {
        object Bundle { get; }
    }

    /// <summary>A client resource provider seen by the toolkit; lookups keep the requested type.</summary>
    public sealed class RemoteResourceProvider : IResourceProvider
    {
        readonly Func<string, string, object[]> _tryGet;
        readonly Func<Action<string>, Action> _subscribe;
        Action<string> _handlers;
        Action _unsubscribe;

        public RemoteResourceProvider(Func<string, string, object[]> tryGet, Func<Action<string>, Action> subscribe)
        {
            _tryGet = tryGet;
            _subscribe = subscribe;
        }

        public event Action<string> ResourceChanged
        {
            add
            {
                bool first = _handlers == null;
                _handlers += value;
                if (first && _subscribe != null && _unsubscribe == null)
                    _unsubscribe = _subscribe(key => { var h = _handlers; if (h != null) h(key); });
            }
            remove
            {
                _handlers -= value;
                if (_handlers == null && _unsubscribe != null)
                {
                    _unsubscribe();
                    _unsubscribe = null;
                }
            }
        }

        public bool TryGetResource<T>(string key, out T value)
        {
            value = default(T);
            object[] found = _tryGet == null ? null : _tryGet(typeof(T).FullName, key);
            if (found == null)
                return false;
            object wire = ServerUi.FromWire(found[0]);
            if (wire is T)
            {
                value = (T)wire;
                return true;
            }
            try
            {
                value = TypeRegistry.ConvertValue<T>(wire);
                return true;
            }
            catch (InvalidCastException)
            {
                return false;
            }
        }
    }

    /// <summary>A client value converter (IValueConverter / IContextValueConverter) seen by the toolkit.</summary>
    public sealed class RemoteConverter : Acrux.Extensions.IContextValueConverter
    {
        readonly Func<object, Type, object, object, object, object> _convert;
        readonly Func<object, Type, object, object, object> _convertBack;

        public RemoteConverter(object wire, Func<object, Type, object, object, object, object> convert, Func<object, Type, object, object, object> convertBack)
        {
            Wire = wire;
            _convert = convert;
            _convertBack = convertBack;
        }

        public object Wire { get; private set; }

        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture, Control target)
        {
            return ServerUi.FromWire(_convert(ServerUi.ToWire(value), targetType, ServerUi.ToWire(parameter), culture, target));
        }

        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return Convert(value, targetType, parameter, culture, null);
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return ServerUi.FromWire(_convertBack(ServerUi.ToWire(value), targetType, ServerUi.ToWire(parameter), culture));
        }
    }

    /// <summary>A client command seen by the toolkit.</summary>
    public sealed class RemoteCommand : ICommand
    {
        readonly Action<object> _execute;
        readonly Func<object, bool> _canExecute;

        public RemoteCommand(object wire, Action<object> execute, Func<object, bool> canExecute)
        {
            Wire = wire;
            _execute = execute;
            _canExecute = canExecute;
        }

        public object Wire { get; private set; }

        public bool CanExecute(object parameter)
        {
            return _canExecute == null || _canExecute(ServerUi.ToWire(parameter));
        }

        public void Execute(object parameter)
        {
            if (_execute != null)
                _execute(ServerUi.ToWire(parameter));
        }
    }

    /// <summary>A client items source; follows client collection changes while the toolkit listens.</summary>
    public sealed class RemoteItems : IEnumerable, INotifyCollectionChanged
    {
        readonly IEnumerable _source;
        readonly Func<Action<int, IList, IList, int, int>, Action> _subscribe;
        readonly Func<object, object> _unwrap;
        NotifyCollectionChangedEventHandler _handlers;
        Action _unsubscribe;

        public RemoteItems(object wire, IEnumerable source, Func<Action<int, IList, IList, int, int>, Action> subscribe, Func<object, object> unwrap)
        {
            Wire = wire;
            _source = source;
            _subscribe = subscribe;
            _unwrap = unwrap;
        }

        public object Wire { get; private set; }

        public IEnumerator GetEnumerator()
        {
            if (_source == null)
                yield break;
            foreach (object item in _source)
                yield return _unwrap == null ? item : _unwrap(item);
        }

        public event NotifyCollectionChangedEventHandler CollectionChanged
        {
            add
            {
                bool first = _handlers == null;
                _handlers += value;
                if (first && _subscribe != null && _unsubscribe == null)
                    _unsubscribe = _subscribe(OnChanged);
            }
            remove
            {
                _handlers -= value;
                if (_handlers == null && _unsubscribe != null)
                {
                    _unsubscribe();
                    _unsubscribe = null;
                }
            }
        }

        void OnChanged(int action, IList newItems, IList oldItems, int newIndex, int oldIndex)
        {
            var handlers = _handlers;
            if (handlers != null)
                handlers(this, new NotifyCollectionChangedEventArgs((NotifyCollectionChangedAction)action, newItems, oldItems, newIndex, oldIndex));
        }

        public override bool Equals(object obj)
        {
            var other = obj as RemoteItems;
            return other != null ? ReferenceEquals(other._source, _source) : ReferenceEquals(obj, _source);
        }

        public override int GetHashCode()
        {
            return _source == null ? 0 : _source.GetHashCode();
        }
    }
}
