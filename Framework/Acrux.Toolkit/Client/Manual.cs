using System;
using System.Collections.Generic;
using Acrux.Animation;
using Acrux.Core.Invalidation;
using Acrux.Core.PropertySystem;
using Acrux.Toolkit;

namespace Acrux.Toolkit
{
    public static partial class Ui
    {
        // property bridge
        static Func<object, string, string, object, int, Func<object, bool>, string, object> _registerProperty;
        static Func<object, object[]> _propertyInfo;
        static Func<object, object, object> _getValue;
        static Action<object, object, object> _setValue;
        static Action<object, object> _clearValue;
        static Action<object, object, string> _setDynamicResource;
        static Func<object, string, object[]> _tryFindResource;
        static Func<object, string, object[]> _resourceTryGetValue;
        static Action<Func<string, string>> _registerSourceLoader;
        static Action<Action<string, Exception>> _registerSourceFailure;
        static Action<string, object, Func<object, bool>> _registerStyleType;
        static Action<string, Func<object>> _registerTemplateType;
        static Action<string, string, string, Action<object, object>> _registerTemplateProperty;

        static void LoadManual(IDictionary<string, Delegate> api)
        {
            _registerProperty = Get<Func<object, string, string, object, int, Func<object, bool>, string, object>>(api, "Property.Register");
            _propertyInfo = Get<Func<object, object[]>>(api, "Property.Info");
            _getValue = Get<Func<object, object, object>>(api, "Property.GetValue");
            _setValue = Get<Action<object, object, object>>(api, "Property.SetValue");
            _clearValue = Get<Action<object, object>>(api, "Property.ClearValue");
            _setDynamicResource = Get<Action<object, object, string>>(api, "Property.SetDynamicResource");
            _tryFindResource = Get<Func<object, string, object[]>>(api, "Control.TryFindResource");
            _resourceTryGetValue = Get<Func<object, string, object[]>>(api, "ResourceDictionary.TryGetValue");
            _registerSourceLoader = Get<Action<Func<string, string>>>(api, "StyleSheet.RegisterSourceLoader");
            _registerSourceFailure = Get<Action<Action<string, Exception>>>(api, "StyleSheet.RegisterSourceLoadFailed");
            _registerStyleType = Get<Action<string, object, Func<object, bool>>>(api, "Types.RegisterStyleType");
            _registerTemplateType = Get<Action<string, Func<object>>>(api, "Types.RegisterTemplateType");
            _registerTemplateProperty = Get<Action<string, string, string, Action<object, object>>>(api, "Types.RegisterTemplateProperty");
        }

        static void ResolvePendingProperties()
        {
            IPendingProperty[] pending;
            lock (PendingProperties)
            {
                pending = PendingProperties.ToArray();
                PendingProperties.Clear();
            }
            foreach (var property in pending)
                property.Resolve();
        }

        // Generated in the client compilation: announces its own control types and properties.
        static partial void RegisterClientCatalog();

        /// <summary>Forces a static styled-property field to initialize (GC.KeepAlive is not allowed in mods).</summary>
        public static void Touch(object value)
        {
        }

        internal static void RegisterClientTypes()
        {
            RegisterClientCatalog();
        }

        // ------------------------------------------------------------------ styled properties

        internal interface IPendingProperty
        {
            void Resolve();
        }

        internal static readonly List<IPendingProperty> PendingProperties = new List<IPendingProperty>();

        internal static object RegisterProperty(Type ownerType, string name, Type valueType, object defaultWire,
            UiInvalidation invalidation, Func<object, bool> isOwner, string ownerSelector)
        {
            object native = _registerProperty(ownerType, name, valueType.FullName, defaultWire, (int)invalidation, isOwner, ownerSelector);
            return native;
        }

        internal static object[] PropertyInfo(object native)
        {
            return _propertyInfo(native);
        }

        public static StyledProperty<T> WrapProperty<T>(object native)
        {
            if (native == null)
                return null;
            object peer;
            lock (Identities)
                Identities.TryGetValue(native, out peer);
            var typed = peer as StyledProperty<T>;
            if (typed != null)
                return typed;
            var created = StyledProperty<T>.FromNative(native);
            Pin(native, created);
            return created;
        }

        public static IStyledProperty WrapProperty(object native)
        {
            if (native == null)
                return null;
            object peer;
            lock (Identities)
                Identities.TryGetValue(native, out peer);
            var known = peer as IStyledProperty;
            if (known != null)
                return known;
            var created = StyledProperty<object>.FromNative(native);
            Pin(native, created);
            return created;
        }

        internal static object GetValue(object control, object property)
        {
            return _getValue(control, property);
        }

        internal static void SetValue(object control, object property, object value)
        {
            _setValue(control, property, value);
        }

        internal static void ClearValue(object control, object property)
        {
            _clearValue(control, property);
        }

        internal static void SetDynamicResource(object control, object property, string key)
        {
            _setDynamicResource(control, property, key);
        }

        internal static bool TryFindResource<T>(object control, string key, out T value)
        {
            object[] found = _tryFindResource(control, key);
            if (found == null)
            {
                value = default(T);
                return false;
            }
            try
            {
                value = FromWireValue<T>(found[0]);
                return true;
            }
            catch (InvalidCastException)
            {
                value = default(T);
                return false;
            }
        }

        internal static bool ResourceTryGetValue<T>(object dictionary, string key, out T value)
        {
            object[] found = _resourceTryGetValue(dictionary, key);
            if (found == null)
            {
                value = default(T);
                return false;
            }
            try
            {
                value = FromWireValue<T>(found[0]);
                return true;
            }
            catch (InvalidCastException)
            {
                value = default(T);
                return false;
            }
        }

        // ------------------------------------------------------------------ client types for toolkit XAML

        /// <summary>Lets toolkit styles select a client control type (Selector="prefix|Name").</summary>
        public static void RegisterStyleType<TControl>(string selectorName) where TControl : class
        {
            _registerStyleType(selectorName, typeof(TControl), peer => peer is TControl);
        }

        /// <summary>Lets toolkit-parsed templates instantiate a client control.</summary>
        public static void RegisterTemplateType<TControl>(string xmlName, Func<TControl> factory) where TControl : class
        {
            _registerTemplateType(xmlName, () => Native(factory()));
        }

        public static void RegisterTemplateProperty<TControl, TValue>(string xmlName, string propertyName, Action<TControl, TValue> setter)
            where TControl : class
        {
            _registerTemplateProperty(xmlName, propertyName, typeof(TValue).FullName,
                (native, value) => setter(Wrap<TControl>(native), FromWireValue<TValue>(value)));
        }

        internal static void RegisterSourceLoader(Func<string, string> loader)
        {
            _registerSourceLoader(loader);
        }

        internal static void RegisterSourceLoadFailed(Action<string, Exception> report)
        {
            _registerSourceFailure(report);
        }
    }
}

namespace Acrux.Core.PropertySystem
{
    /// <summary>Client view of a toolkit styled property.</summary>
    public interface IStyledProperty : IToolkitObject
    {
        string Name { get; }
        Type OwnerType { get; }
        Type ValueType { get; }
        object DefaultValueObject { get; }
        UiInvalidation Invalidation { get; }
    }

    /// <summary>
    /// Client handle of a styled property. Values live in the toolkit property store so toolkit styles,
    /// template bindings and animations see client-registered properties exactly like built-in ones.
    /// </summary>
    public sealed class StyledProperty<T> : IStyledProperty, Ui.IPendingProperty
    {
        object _native;
        Func<object> _register;

        StyledProperty(string name, Type ownerType, T defaultValue, UiInvalidation invalidation)
        {
            Name = name;
            OwnerType = ownerType;
            DefaultValue = defaultValue;
            Invalidation = invalidation;
        }

        /// <summary>A toolkit-defined property handle that resolves when first used (after the toolkit is loaded).</summary>
        public static StyledProperty<T> Lazy(Func<object> resolve)
        {
            var property = new StyledProperty<T>(null, null, default(T), UiInvalidation.Render);
            property._resolveNative = resolve;
            // Resolve as early as possible so the handle is the identity every later lookup returns.
            if (Ui.IsLoaded)
                property.Resolve();
            else
                lock (Ui.PendingProperties)
                    Ui.PendingProperties.Add(property);
            return property;
        }

        Func<object> _resolveNative;
        bool _infoLoaded = true;

        void EnsureInfo()
        {
            if (_infoLoaded)
                return;
            object native = Native;
            object[] info = Ui.PropertyInfo(native);
            _name = (string)info[0];
            _ownerType = info[1] as Type;
            _defaultValue = Ui.FromWireValue<T>(info[2]);
            _invalidation = (UiInvalidation)(int)info[3];
            _infoLoaded = true;
        }

        internal static StyledProperty<T> FromNative(object native)
        {
            object[] info = Ui.PropertyInfo(native);
            var property = new StyledProperty<T>(
                (string)info[0],
                info[1] as Type,
                Ui.FromWireValue<T>(info[2]),
                (UiInvalidation)(int)info[3]);
            property._native = native;
            return property;
        }

        internal static StyledProperty<T> Create(string name, Type ownerType, T defaultValue, UiInvalidation invalidation,
            Func<object, bool> isOwner, string ownerSelector)
        {
            var property = new StyledProperty<T>(name, ownerType, defaultValue, invalidation);
            property._register = () => Ui.RegisterProperty(ownerType, name, typeof(T), Ui.ToWireValue(defaultValue), invalidation, isOwner, ownerSelector);
            if (Ui.IsLoaded)
                property.Resolve();
            else
                lock (Ui.PendingProperties)
                    Ui.PendingProperties.Add(property);
            return property;
        }

        void Ui.IPendingProperty.Resolve()
        {
            Resolve();
        }

        void Resolve()
        {
            if (_native != null)
                return;
            if (_resolveNative != null)
            {
                _native = _resolveNative();
                if (_native != null)
                {
                    _infoLoaded = false;
                    Ui.Pin(_native, this);
                }
                return;
            }
            if (_register == null)
                return;
            _native = _register();
            Ui.Pin(_native, this);
        }

        public object Native
        {
            get
            {
                if (_native == null)
                    Resolve();
                return _native;
            }
        }

        string _name;
        Type _ownerType;
        T _defaultValue;
        UiInvalidation _invalidation;

        public string Name { get { EnsureInfo(); return _name; } private set { _name = value; } }
        public Type OwnerType { get { EnsureInfo(); return _ownerType; } private set { _ownerType = value; } }
        public Type ValueType => typeof(T);
        public T DefaultValue { get { EnsureInfo(); return _defaultValue; } private set { _defaultValue = value; } }
        public object DefaultValueObject => DefaultValue;
        public UiInvalidation Invalidation { get { EnsureInfo(); return _invalidation; } private set { _invalidation = value; } }

        public override string ToString()
        {
            return (OwnerType == null ? "?" : OwnerType.Name) + "." + Name;
        }
    }

    public static class StyledProperty
    {
        public static StyledProperty<T> Register<TOwner, T>(
            string name,
            T defaultValue,
            UiInvalidation invalidation = UiInvalidation.Render)
            where TOwner : Acrux.Core.Control
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("A styled property requires a name.", nameof(name));
            return StyledProperty<T>.Create(name, typeof(TOwner), defaultValue, invalidation, peer => peer is TOwner, null);
        }

        internal static StyledProperty<T> RegisterInternal<TOwner, T>(
            string name,
            T defaultValue,
            UiInvalidation invalidation = UiInvalidation.Render)
            where TOwner : Acrux.Core.Control
        {
            return Register<TOwner, T>(name, defaultValue, invalidation);
        }

        public static StyledProperty<T> RegisterAttached<TTarget, T>(
            Type attachedOwnerType,
            string name,
            T defaultValue,
            UiInvalidation invalidation = UiInvalidation.Render)
            where TTarget : Acrux.Core.Control
        {
            if (attachedOwnerType == null)
                throw new ArgumentNullException(nameof(attachedOwnerType));
            string qualifiedName = attachedOwnerType.Name + "." + name;
            // Attached properties may target toolkit controls: the toolkit matches typeof(TTarget) by name
            // when it is one of its own types, and asks the client otherwise.
            return StyledProperty<T>.Create(qualifiedName, typeof(TTarget), defaultValue, invalidation, peer => peer is TTarget, typeof(TTarget).FullName);
        }

        internal static StyledProperty<T> RegisterAttachedInternal<TTarget, T>(
            Type attachedOwnerType,
            string name,
            T defaultValue,
            UiInvalidation invalidation = UiInvalidation.Render)
            where TTarget : Acrux.Core.Control
        {
            return RegisterAttached<TTarget, T>(attachedOwnerType, name, defaultValue, invalidation);
        }
    }
}

namespace Acrux.Core
{
    public abstract partial class Control
    {
        public T GetValue<T>(StyledProperty<T> property)
        {
            if (property == null)
                throw new ArgumentNullException(nameof(property));
            return Ui.FromWireValue<T>(Ui.GetValue(Native, property.Native));
        }

        public void SetValue<T>(StyledProperty<T> property, T value)
        {
            if (property == null)
                throw new ArgumentNullException(nameof(property));
            Ui.SetValue(Native, property.Native, Ui.ToWireValue(value));
        }

        public void ClearValue<T>(StyledProperty<T> property)
        {
            if (property == null)
                throw new ArgumentNullException(nameof(property));
            Ui.ClearValue(Native, property.Native);
        }

        public void SetDynamicResource<T>(StyledProperty<T> property, string key)
        {
            if (property == null)
                throw new ArgumentNullException(nameof(property));
            Ui.SetDynamicResource(Native, property.Native, key);
        }

        public bool TryFindResource<T>(string key, out T value)
        {
            return Ui.TryFindResource(Native, key, out value);
        }

        public T ResolveResource<T>(string key)
        {
            T value;
            if (TryFindResource(key, out value))
                return value;
            Ui.WarnOnce("Unknown resource '" + key + "' (" + typeof(T).FullName + ") requested by " + GetType().Name + ".");
            return default(T);
        }

        internal T ResolveDynamicResource<T>(string key, T fallback)
        {
            T value;
            return TryFindResource(key, out value) ? value : fallback;
        }
    }
}

namespace Acrux.Styling
{
    public sealed partial class ResourceDictionary
    {
        public bool TryGetValue<T>(string key, out T value)
        {
            return Ui.ResourceTryGetValue(Native, key, out value);
        }
    }

    public sealed partial class StyleSheet
    {
        static Func<string, string> _clientSourceLoader;
        static Action<string, Exception> _clientSourceLoadFailed;

        /// <summary>This mod's style asset loader. The toolkit tries every client loader before its own content.</summary>
        public static Func<string, string> SourceLoader
        {
            get { return _clientSourceLoader; }
            set
            {
                _clientSourceLoader = value;
                Func<string, string> loader = value;
                Ui.RegisterSourceLoader(loader == null ? null : new Func<string, string>(source =>
                {
                    try
                    {
                        return loader(source);
                    }
                    catch (Exception)
                    {
                        return null;
                    }
                }));
            }
        }

        public static Action<string, Exception> SourceLoadFailed
        {
            get { return _clientSourceLoadFailed; }
            set
            {
                _clientSourceLoadFailed = value;
                Ui.RegisterSourceLoadFailed(value);
            }
        }
    }
}

namespace Acrux.Animation
{
    public static partial class VisualElementAnimationExtensions
    {
        public static AnimationHandle Animate<T>(
            this Acrux.Core.VisualElement element,
            StyledProperty<T> property,
            T to,
            int durationFrames,
            AnimationInterpolator<T> interpolator,
            EasingMode easingMode = EasingMode.Linear)
        {
            if (property == null)
                throw new ArgumentNullException(nameof(property));
            string channel = property.OwnerType.FullName + "." + property.Name;
            return element.RunAnimation(
                channel,
                AnimationConflict.Replace,
                new Keyframe<T>(
                    () => element.GetValue(property),
                    value => element.SetValue(property, value),
                    to,
                    durationFrames,
                    easingMode,
                    interpolator));
        }
    }
}

namespace Acrux.Core.Binding
{
    // StringFormat normalizes bare numeric formats, applies the converter first,
    // and formats only targets that can accept a string. (Runs client-side: it only touches values.)
    internal static class BindingStringFormat
    {
        internal static object Apply(object value, Type targetType, string format, System.Globalization.CultureInfo culture)
        {
            if (ReferenceEquals(value, Acrux.Data.BindingOperations.DoNothing) ||
                string.IsNullOrWhiteSpace(format) ||
                (targetType != typeof(string) && targetType != typeof(object)))
                return value;
            if (format.IndexOf('{') < 0) format = "{0:" + format + "}";
            return string.Format(culture ?? System.Globalization.CultureInfo.CurrentCulture, format, value);
        }
    }

    /// <summary>Two-way compiled binding; the toolkit owns the target property and calls back into the source.</summary>
    internal static class CompiledTwoWayBinding
    {
        internal static void Attach<TSource, TValue>(VisualElement target, StyledProperty<TValue> property,
            Func<TSource, TValue> getter, Action<TSource, TValue> setter, string name) where TSource : class
        {
            // The toolkit hands back its own object for the source (a client view model crosses as a Remote_* proxy):
            // map it back to the client's instance, as the generated one-way Bind does, instead of casting it.
            Ui.AttachTwoWay(target.Native, property.Native,
                source => Ui.ToWireValue(getter(Ui.FromWire<TSource>(source))),
                (source, value) => setter(Ui.FromWire<TSource>(source), Ui.FromWireValue<TValue>(value)),
                name);
        }
    }
}

namespace Acrux.Toolkit
{
    public static partial class Ui
    {
        static Action<object, object, Func<object, object>, Action<object, object>, string> _attachTwoWay;
        internal static void AttachTwoWay(object target, object property, Func<object, object> getter, Action<object, object> setter, string name)
        {
            if (_attachTwoWay == null)
                _attachTwoWay = Api<Action<object, object, Func<object, object>, Action<object, object>, string>>("Binding.AttachTwoWay");
            _attachTwoWay(target, property, getter, setter, name);
        }
    }
}
