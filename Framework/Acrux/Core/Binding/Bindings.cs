using System;
using System.Collections.Generic;
using System.ComponentModel;
using Acrux.Core.PropertySystem;

namespace Acrux.Core.Binding
{
    // BindingExpression normalizes bare numeric formats, applies the
    // converter first, and formats only targets that can accept a string.
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

    internal static class CompiledTwoWayBinding
    {
        internal static void Attach<TSource, TValue>(VisualElement target, StyledProperty<TValue> property,
            Func<TSource, TValue> getter, Action<TSource, TValue> setter, string name) where TSource : class
        {
            bool applying = false;
            target.Bind<TSource, TValue>(getter, value =>
            {
                applying = true;
                try { target.SetValue(property, value); }
                finally { applying = false; }
            }, name);
            Action<Control, IStyledProperty> changed = (control, changedProperty) =>
            {
                if (applying || target.IsAttachingBindings || !ReferenceEquals(property, changedProperty)) return;
                var source = target.DataContext as TSource;
                if (source != null) setter(source, target.GetValue(property));
            };
            target.StyledPropertyChanged += changed;
            target.RegisterLifetime(new Subscription(() => target.StyledPropertyChanged -= changed));
        }

        sealed class Subscription : IDisposable
        {
            Action _dispose;
            internal Subscription(Action dispose) { _dispose = dispose; }
            public void Dispose() { if (_dispose != null) _dispose(); _dispose = null; }
        }
    }

    internal interface IBind : IDisposable
    {
        void Attach(object source);

        // True when the last update used a contextual converter (see IContextValueConverter).
        bool UsesContext { get; }

        void Refresh();
    }

    /// <summary>
    /// A build-time-resolved binding. The generated getter and setter contain all
    /// member access, so applying the binding does not require reflection.
    /// </summary>
    public sealed class CompiledBinding<TSource, TValue> : IBind
        where TSource : class
    {
        readonly Func<TSource, TValue> _getter;
        readonly Action<TValue> _setter;
        readonly string _propertyName;
        TSource _source;
        INotifyPropertyChanged _notifier;

        public CompiledBinding(
            Func<TSource, TValue> getter,
            Action<TValue> setter,
            string propertyName)
        {
            if (getter == null)
                throw new ArgumentNullException(nameof(getter));
            if (setter == null)
                throw new ArgumentNullException(nameof(setter));
            if (string.IsNullOrWhiteSpace(propertyName))
                throw new ArgumentException("A source property name is required.", nameof(propertyName));
            _getter = getter;
            _setter = setter;
            _propertyName = propertyName;
        }

        void IBind.Attach(object source)
        {
            Attach(source);
        }

        internal void Attach(object source)
        {
            TSource typedSource = source as TSource;
            if (source != null && typedSource == null)
            {
                // A data context of another type (an inherited one, before the element's own
                // DataContext binding resolves) leaves the binding without a value instead of failing.
                Detach();
                return;
            }
            if (ReferenceEquals(_source, typedSource))
                return;

            if (_notifier != null)
                _notifier.PropertyChanged -= OnPropertyChanged;

            _source = typedSource;
            _notifier = typedSource as INotifyPropertyChanged;
            if (_notifier != null)
                _notifier.PropertyChanged += OnPropertyChanged;

            Apply();
        }

        void Detach()
        {
            if (_notifier != null)
                _notifier.PropertyChanged -= OnPropertyChanged;
            _source = null;
            _notifier = null;
        }

        void OnPropertyChanged(object sender, PropertyChangedEventArgs args)
        {
            if (!ReferenceEquals(sender, _notifier))
                return;
            if (!string.IsNullOrEmpty(args.PropertyName) && args.PropertyName != _propertyName)
                return;
            Apply();
        }

        bool _usesContext;

        bool IBind.UsesContext => _usesContext;

        void IBind.Refresh()
        {
            if (_source != null)
                Apply();
        }

        void Apply()
        {
            int contextual = Acrux.Extensions.ValueConverters.ContextualConversions;
            _setter(_source == null ? default(TValue) : _getter(_source));
            _usesContext = contextual != Acrux.Extensions.ValueConverters.ContextualConversions;
        }

        public void Dispose()
        {
            if (_notifier != null)
                _notifier.PropertyChanged -= OnPropertyChanged;
            _source = null;
            _notifier = null;
        }
    }
}
