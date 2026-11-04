using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using VRageMath;

namespace AcruxPreview
{
    /// <summary>
    /// The previewed mod's assembly, loaded into the host and connected to it through the toolkit bridge, so the
    /// mod's own controls can be created in the preview. Reloaded when the mod is rebuilt.
    /// </summary>
    public sealed class ModClient
    {
        static ModClient _current;

        readonly Assembly _assembly;
        readonly string _path;
        readonly DateTime _stamp;

        ModClient(Assembly assembly, string path, DateTime stamp)
        {
            _assembly = assembly;
            _path = path;
            _stamp = stamp;
        }

        public string Error { get; private set; }

        /// <summary>
        /// The signature of <paramref name="viewClass"/>'s root as the mod was compiled (the view generator's design-build
        /// metadata); null when the build has none for it (no such view, or a build from before the check).
        /// </summary>
        public string CompiledRootSignature(string viewClass, out bool buildHasSignatures)
        {
            buildHasSignatures = false;
            string key = Acrux.Generator.ViewRootSignature.MetadataPrefix + viewClass;
            string found = null;
            foreach (AssemblyMetadataAttribute metadata in _assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
            {
                if (metadata.Key == null || !metadata.Key.StartsWith(Acrux.Generator.ViewRootSignature.MetadataPrefix, StringComparison.Ordinal))
                    continue;
                buildHasSignatures = true;
                if (metadata.Key == key)
                    found = metadata.Value;
            }
            return found;
        }

        /// <summary>The mod at <paramref name="assemblyPath"/>, connected; null when it cannot be loaded.</summary>
        public static ModClient For(string assemblyPath)
        {
            assemblyPath = ResolveAssembly(assemblyPath);
            if (assemblyPath == null)
                return null;
            DateTime stamp = File.GetLastWriteTimeUtc(assemblyPath);
            ModClient current = _current;
            if (current != null && current._path == assemblyPath && current._stamp == stamp)
                return current;
            _current = Load(assemblyPath, stamp);
            return _current;
        }

        /// <summary>
        /// The mod's built assembly: the recorded path when it exists, else the newest build of the same file under
        /// the project's bin folder (the IDE and the command line build to different configurations/platforms).
        /// </summary>
        public static string ResolveAssembly(string assemblyPath)
        {
            if (string.IsNullOrEmpty(assemblyPath))
                return null;
            if (File.Exists(assemblyPath))
                return assemblyPath;
            string project = FindProjectDirectory(assemblyPath);
            string bin = project == null ? null : Path.Combine(project, "bin");
            if (bin == null || !Directory.Exists(bin))
                return null;
            string found = Directory.EnumerateFiles(bin, Path.GetFileName(assemblyPath), SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (found != null)
                Program.Log("mod assembly " + assemblyPath + " is missing; using " + found);
            else
                Program.Log("mod assembly not found: " + assemblyPath + " (build the mod)");
            return found;
        }

        /// <summary>The folder holding the project file above <paramref name="path"/> (which may not exist yet).</summary>
        public static string FindProjectDirectory(string path)
        {
            for (string dir = Path.GetDirectoryName(Path.GetFullPath(path)); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
            {
                if (Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.csproj").Any())
                    return dir;
            }
            return null;
        }

        static ModClient Load(string path, DateTime stamp)
        {
            try
            {
                var context = new ModLoadContext(Path.GetDirectoryName(path));
                // From memory, so the IDE can rebuild the mod while the preview runs.
                Assembly assembly;
                using (var stream = new MemoryStream(File.ReadAllBytes(path)))
                    assembly = context.LoadFromStream(stream);
                var client = new ModClient(assembly, path, stamp);
                client.Connect();
                Program.Log("mod loaded: " + assembly.GetName().Name + (client.Error == null ? "" : " (" + client.Error + ")"));
                return client;
            }
            catch (Exception error)
            {
                Program.Log("mod not loaded: " + error);
                return null;
            }
        }

        void Connect()
        {
            Type ui = _assembly.GetType("Acrux.Toolkit.Ui");
            if (ui == null)
            {
                Error = "the mod does not use Acrux UI";
                return;
            }
            try
            {
                PropertyInfo name = ui.GetProperty("ClientName", BindingFlags.Public | BindingFlags.Static);
                if (name != null)
                    name.SetValue(null, _assembly.GetName().Name + " (preview)");
                ui.GetMethod("Load", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { ToolkitApi.Api });
            }
            catch (TargetInvocationException error)
            {
                Error = error.InnerException?.Message ?? error.Message;
            }
        }

        /// <summary>The mod's type named by an XAML "using:" namespace and element name.</summary>
        /// <summary>The mod's types that loaded (a type whose dependencies are missing is left out).</summary>
        public IEnumerable<Type> Types
        {
            get
            {
                try
                {
                    return _assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException error)
                {
                    return error.Types.Where(t => t != null);
                }
            }
        }

        public Type FindType(string clrNamespace, string name)
        {
            return _assembly.GetType(clrNamespace + "." + name, false);
        }

        /// <summary>
        /// Creates the mod's control and returns the toolkit object behind it (what the preview tree holds).
        /// </summary>
        public object CreateControl(Type type, IEnumerable<KeyValuePair<string, string>> attributes, out string error)
        {
            object wrapper;
            return CreateControl(type, attributes, out wrapper, out error);
        }

        /// <summary>As <see cref="CreateControl(Type, IEnumerable{KeyValuePair{string, string}}, out string)"/>, also giving the mod's wrapper.</summary>
        public object CreateControl(Type type, IEnumerable<KeyValuePair<string, string>> attributes, out object wrapper, out string error)
        {
            wrapper = Create(type, attributes, out error);
            if (wrapper == null)
                return null;
            PropertyInfo native = FindProperty(type, "Native");
            return native == null ? null : native.GetValue(wrapper);
        }

        /// <summary>The mod's object named <paramref name="typeName"/>, with the given properties set (by text).</summary>
        public object CreateObject(string typeName, IEnumerable<KeyValuePair<string, string>> properties, out string error)
        {
            Type type = _assembly.GetType(typeName, false);
            if (type == null)
            {
                error = "no type " + typeName + " in " + _assembly.GetName().Name;
                return null;
            }
            return Create(type, properties, out error);
        }

        /// <summary>The toolkit object behind one of the mod's UI wrappers (its "Native"); null when it is none.</summary>
        public object NativeOf(object wrapper)
        {
            if (wrapper == null)
                return null;
            PropertyInfo native = FindProperty(wrapper.GetType(), "Native");
            return native == null ? null : native.GetValue(wrapper);
        }

        /// <summary>
        /// A binding source's property by name, as the toolkit sees values: the mod's UI wrappers become the toolkit
        /// objects behind them, the mod's copies of Acrux enums the toolkit's. DoNothing when the source has no such property.
        /// </summary>
        public object ReadProperty(object source, string name)
        {
            if (source == null)
                return Acrux.Data.BindingOperations.DoNothing;
            PropertyInfo property = FindProperty(source.GetType(), name);
            if (property == null || !property.CanRead || property.GetIndexParameters().Length > 0)
                return Acrux.Data.BindingOperations.DoNothing;
            object value;
            try
            {
                value = property.GetValue(source);
            }
            catch (Exception)
            {
                return Acrux.Data.BindingOperations.DoNothing;
            }
            return ToToolkit(value);
        }

        /// <summary>The mod's wrapper for a toolkit object (as the bridge hands toolkit objects to the mod); null when none.</summary>
        public object WrapperOf(object native)
        {
            Type ui = _assembly.GetType("Acrux.Toolkit.Ui");
            MethodInfo fromWire = ui == null ? null : ui.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "FromWire" && !m.IsGenericMethodDefinition && m.GetParameters().Length == 1 &&
                                     m.GetParameters()[0].ParameterType == typeof(object));
            if (fromWire == null || native == null)
                return null;
            try
            {
                object wrapper = fromWire.Invoke(null, new[] { native });
                return ReferenceEquals(wrapper, native) ? null : wrapper;
            }
            catch (Exception error)
            {
                Program.Log("no mod wrapper for " + native.GetType().Name + ": " + ((error as TargetInvocationException)?.InnerException ?? error).Message);
                return null;
            }
        }

        /// <summary>
        /// A binding source's property by name as the mod sees it (no conversion to toolkit objects), for setting on
        /// the mod's own controls. DoNothing when the source has no such property.
        /// </summary>
        public object ReadModProperty(object source, string name)
        {
            PropertyInfo property = source == null ? null : FindProperty(source.GetType(), name);
            if (property == null || !property.CanRead || property.GetIndexParameters().Length > 0)
                return Acrux.Data.BindingOperations.DoNothing;
            try
            {
                return property.GetValue(source);
            }
            catch (Exception)
            {
                return Acrux.Data.BindingOperations.DoNothing;
            }
        }

        public void WriteProperty(object source, string name, object value)
        {
            PropertyInfo property = source == null ? null : FindProperty(source.GetType(), name);
            if (property == null || !property.CanWrite)
                return;
            try
            {
                if (value != null && !property.PropertyType.IsInstanceOfType(value))
                    value = property.PropertyType.IsEnum ? Enum.Parse(property.PropertyType, value.ToString(), true) : Convert.ChangeType(value, property.PropertyType, System.Globalization.CultureInfo.InvariantCulture);
                property.SetValue(source, value);
            }
            catch (Exception)
            {
            }
        }

        // Through the bridge's own encoding (the mod's Ui.ToWire, then the toolkit's FromWire), as values cross
        // between the mod and the toolkit in game: wrappers, commands, converters, textures, grid lengths.
        object ToToolkit(object value)
        {
            if (value == null || value is string)
                return value;
            Type type = value.GetType();
            if (type.Assembly != _assembly)
                return value;
            if (type.IsEnum && type.Namespace != null && type.Namespace.StartsWith("Acrux.", StringComparison.Ordinal))
            {
                Type own = typeof(Acrux.Core.Control).Assembly.GetType(type.FullName, false);
                if (own != null && own.IsEnum)
                    return Enum.ToObject(own, Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
            }
            MethodInfo toWire = ClientToWire;
            if (toWire == null)
                return NativeOf(value) ?? value;
            object wire = toWire.Invoke(null, new[] { value });
            if (type.FullName == "Acrux.Styling.TextureSource")
                return Acrux.Toolkit.Server.ServerUi.TextureFromWire(wire);
            if (type.FullName == "Acrux.Layout.GridLength" && wire is Vector2)
            {
                var length = (Vector2)wire;
                return new Acrux.Layout.GridLength(length.X, (Acrux.Layout.GridUnitType)(int)length.Y);
            }
            return Acrux.Toolkit.Server.ServerUi.FromWire(wire);
        }

        MethodInfo _clientToWire;
        bool _clientToWireLooked;

        MethodInfo ClientToWire
        {
            get
            {
                if (!_clientToWireLooked)
                {
                    _clientToWireLooked = true;
                    Type ui = _assembly.GetType("Acrux.Toolkit.Ui");
                    _clientToWire = ui == null ? null : ui.GetMethod("ToWire", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(object) }, null);
                }
                return _clientToWire;
            }
        }

        internal static object Create(Type type, IEnumerable<KeyValuePair<string, string>> properties, out string error)
        {
            error = null;
            object instance;
            try
            {
                instance = Activator.CreateInstance(type, true);
            }
            catch (Exception failure)
            {
                error = (failure.InnerException ?? failure).Message;
                return null;
            }
            foreach (var property in properties)
                TrySet(instance, property.Key, property.Value);
            return instance;
        }

        static void TrySet(object target, string name, string text)
        {
            PropertyInfo property = FindProperty(target.GetType(), name);
            if (property == null || !property.CanWrite)
                return;
            object value;
            if (!TryConvert(text, property.PropertyType, out value))
                return;
            try
            {
                property.SetValue(target, value);
            }
            catch (Exception)
            {
                // Design-time only: a property the control rejects simply keeps its default.
            }
        }

        internal static bool TryConvert(string text, Type type, out object value)
        {
            value = null;
            var invariant = System.Globalization.CultureInfo.InvariantCulture;
            try
            {
                if (type == typeof(string)) value = text;
                else if (type == typeof(float)) value = float.Parse(text, invariant);
                else if (type == typeof(double)) value = double.Parse(text, invariant);
                else if (type == typeof(int)) value = int.Parse(text, invariant);
                else if (type == typeof(bool)) value = bool.Parse(text);
                else if (type.IsEnum) value = Enum.Parse(type, text, true);
                else if (type == typeof(VRageMath.Color) && text.StartsWith("#", StringComparison.Ordinal))
                {
                    uint rgb = uint.Parse(text.Substring(1), System.Globalization.NumberStyles.HexNumber, invariant);
                    value = text.Length > 7
                        ? new VRageMath.Color((int)((rgb >> 16) & 0xFF), (int)((rgb >> 8) & 0xFF), (int)(rgb & 0xFF), (int)(rgb >> 24))
                        : new VRageMath.Color((int)((rgb >> 16) & 0xFF), (int)((rgb >> 8) & 0xFF), (int)(rgb & 0xFF));
                }
                else return false;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        static PropertyInfo FindProperty(Type type, string name)
        {
            for (Type current = type; current != null; current = current.BaseType)
            {
                PropertyInfo property = current.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (property != null)
                    return property;
            }
            return null;
        }

        /// <summary>Resolves the mod's own dependencies next to it; the game's and the host's come from the host.</summary>
        sealed class ModLoadContext : AssemblyLoadContext
        {
            readonly string _directory;

            public ModLoadContext(string directory) : base("Acrux preview mod", isCollectible: true)
            {
                _directory = directory;
            }

            protected override Assembly Load(AssemblyName name)
            {
                Assembly shared = Default.Assemblies.FirstOrDefault(a => string.Equals(a.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase));
                if (shared != null)
                    return shared;
                try
                {
                    return Default.LoadFromAssemblyName(name);
                }
                catch (Exception)
                {
                }
                string local = Path.Combine(_directory, name.Name + ".dll");
                return File.Exists(local) ? LoadFromAssemblyPath(local) : null;
            }
        }
    }
}
