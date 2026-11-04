using System;
using Acrux.Core.Invalidation;

namespace Acrux.Core.PropertySystem
{
    public interface IStyledProperty
    {
        string Name { get; }
        Type OwnerType { get; }
        Type ValueType { get; }
        object DefaultValueObject { get; }
        UiInvalidation Invalidation { get; }
        bool Matches(Control control);
        object GetValueObject(Control control);
        void ApplyStyle(Control control, object value, IStyleValueSource source);
        void SetDynamicResource(Control control, string key);
        void AttachTwoWay(VisualElement target, Func<object, object> getter, Action<object, object> setter, string name);
        /// <summary>Untyped local-value assignment for bridges that only know the value at run time.</summary>
        void SetValueObject(Control control, object value);
        void ClearValueObject(Control control);
    }

    public interface IStyleValueSource
    {
        Func<T> ConvertProvider<T>(object value);
        bool TryGetDynamicResourceKey(object value, out string key);
    }

    public sealed class StyledProperty<T> : IStyledProperty
    {
        internal StyledProperty(
            string name,
            Type ownerType,
            T defaultValue,
            UiInvalidation invalidation,
            Func<Control, bool> matches)
        {
            Name = name;
            OwnerType = ownerType;
            DefaultValue = defaultValue;
            Invalidation = invalidation;
            _matches = matches;
        }

        readonly Func<Control, bool> _matches;

        public string Name { get; }
        public Type OwnerType { get; }
        public Type ValueType => typeof(T);
        public T DefaultValue { get; }
        public object DefaultValueObject => DefaultValue;
        public UiInvalidation Invalidation { get; }

        public bool Matches(Control control)
        {
            return control != null && _matches(control);
        }

        public object GetValueObject(Control control)
        {
            if (!Matches(control))
                throw new InvalidOperationException(
                    control.GetType().Name + " cannot use " + OwnerType.Name + "." + Name + ".");
            return control.GetValue(this);
        }

        public void ApplyStyle(Control control, object value, IStyleValueSource source)
        {
            if (control == null)
                throw new ArgumentNullException(nameof(control));
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            if (!Matches(control))
                throw new InvalidOperationException(
                    control.GetType().Name + " cannot use " + OwnerType.Name + "." + Name + ".");
            var dependent = value as Acrux.Styling.IResourceDependentValue;
            if (dependent != null)
            {
                control.SetStyleValue(this, () => (T)dependent.Resolve(control));
                control.WatchDynamicResource(dependent.ResourceKey, Invalidation);
                return;
            }
            string resourceKey;
            if (source.TryGetDynamicResourceKey(value, out resourceKey))
            {
                control.SetStyleValue(this, () => control.ResolveDynamicResource(resourceKey, DefaultValue));
                control.WatchDynamicResource(resourceKey, Invalidation);
            }
            else
                control.SetStyleValue(this, source.ConvertProvider<T>(value));
        }

        public void SetDynamicResource(Control control, string key)
        {
            control.SetDynamicResource(this, key);
        }

        public void SetValueObject(Control control, object value)
        {
            control.SetValue(this, value is T ? (T)value : value == null ? default(T) : Acrux.Markup.TypeRegistry.ConvertValue<T>(value));
        }

        public void ClearValueObject(Control control)
        {
            control.ClearValue(this);
        }

        public void AttachTwoWay(VisualElement target, Func<object, object> getter, Action<object, object> setter, string name)
        {
            Acrux.Core.Binding.CompiledTwoWayBinding.Attach<object, T>(
                target, this, source =>
                {
                    // BindingOperations.DoNothing leaves the target as it is.
                    object value = getter(source);
                    return ReferenceEquals(value, Acrux.Data.BindingOperations.DoNothing)
                        ? target.GetValue(this)
                        : Acrux.Markup.TypeRegistry.ConvertValue<T>(value);
                }, (source, value) => setter(source, value), name);
        }
    }

    public static class StyledProperty
    {
        static StyledProperty<T> Create<TOwner, T>(
            string name,
            T defaultValue,
            UiInvalidation invalidation)
            where TOwner : Control
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("A styled property requires a name.", nameof(name));
            return new StyledProperty<T>(
                name,
                typeof(TOwner),
                defaultValue,
                invalidation,
                control => control is TOwner);
        }

        public static StyledProperty<T> Register<TOwner, T>(
            string name,
            T defaultValue,
            UiInvalidation invalidation = UiInvalidation.Render)
            where TOwner : Control
        {
            StyledProperty<T> property = Create<TOwner, T>(name, defaultValue, invalidation);
            StyledPropertyCatalog.Register(property);
            return property;
        }

        internal static StyledProperty<T> RegisterInternal<TOwner, T>(
            string name,
            T defaultValue,
            UiInvalidation invalidation = UiInvalidation.Render)
            where TOwner : Control
        {
            return Create<TOwner, T>(name, defaultValue, invalidation);
        }

        /// <summary>
        /// Registers an owner-qualified property that can be attached to any control
        /// assignable to TTarget.  The qualified style name mirrors XAML
        /// attached-property syntax, e.g. TextOptions.DropShadow.
        /// </summary>
        public static StyledProperty<T> RegisterAttached<TTarget, T>(
            Type attachedOwnerType,
            string name,
            T defaultValue,
            UiInvalidation invalidation = UiInvalidation.Render)
            where TTarget : Control
        {
            if (attachedOwnerType == null)
                throw new ArgumentNullException(nameof(attachedOwnerType));
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("An attached styled property requires a name.", nameof(name));

            string qualifiedName = attachedOwnerType.Name + "." + name;
            var property = new StyledProperty<T>(
                qualifiedName,
                typeof(TTarget),
                defaultValue,
                invalidation,
                control => control is TTarget);
            StyledPropertyCatalog.Register(property);
            return property;
        }

        internal static StyledProperty<T> RegisterAttachedInternal<TTarget, T>(
            Type attachedOwnerType,
            string name,
            T defaultValue,
            UiInvalidation invalidation = UiInvalidation.Render)
            where TTarget : Control
        {
            if (attachedOwnerType == null)
                throw new ArgumentNullException(nameof(attachedOwnerType));
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("An attached styled property requires a name.", nameof(name));

            return new StyledProperty<T>(
                attachedOwnerType.Name + "." + name,
                typeof(TTarget),
                defaultValue,
                invalidation,
                control => control is TTarget);
        }
    }
}
