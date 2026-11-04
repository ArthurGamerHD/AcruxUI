using System;
using System.Collections.Generic;
using Acrux.Core;
using Acrux.Core.Input;
using Acrux.Core.PropertySystem;

namespace Acrux.Markup
{
    public sealed class TypeRegistry
    {
        sealed class Entry
        {
            public Func<Control> Factory;
            public readonly Dictionary<string, Action<Control, string, TemplatedControl>> Setters =
                new Dictionary<string, Action<Control, string, TemplatedControl>>(StringComparer.Ordinal);
            public readonly Dictionary<string, Action<Control, object>> ValueSetters =
                new Dictionary<string, Action<Control, object>>(StringComparer.Ordinal);
            public readonly Dictionary<string, Type> PropertyTypes =
                new Dictionary<string, Type>(StringComparer.Ordinal);
        }

        readonly Dictionary<string, Entry> _entries =
            new Dictionary<string, Entry>(StringComparer.Ordinal);
        readonly Dictionary<string, Func<string, object>> _markupExtensions =
            new Dictionary<string, Func<string, object>>(StringComparer.Ordinal);
        readonly PropertyConverter _converter;

        public TypeRegistry(PropertyConverter converter = null)
        {
            _converter = converter ?? new PropertyConverter();
        }

        public void Register<TControl>(string xmlName, Func<TControl> factory)
            where TControl : Control
        {
            if (string.IsNullOrWhiteSpace(xmlName))
                throw new ArgumentException("A registered control requires an XML name.", nameof(xmlName));
            if (factory == null)
                throw new ArgumentNullException(nameof(factory));
            Entry entry;
            if (!_entries.TryGetValue(xmlName, out entry))
                _entries[xmlName] = entry = new Entry();
            entry.Factory = () => factory();
        }

        internal void EnsureType(string xmlName)
        {
            if (!_entries.ContainsKey(xmlName))
                _entries[xmlName] = new Entry();
        }

        public void RegisterAlias(string alias, string xmlName)
        {
            Entry entry;
            if (!_entries.TryGetValue(xmlName, out entry))
                throw new InvalidOperationException(xmlName + " must be registered first.");
            if (!_entries.ContainsKey(alias))
                _entries[alias] = entry;
        }

        public bool Contains(string xmlName)
        {
            return xmlName != null && _entries.ContainsKey(xmlName);
        }

        public void RegisterMarkupExtension(string name, Func<string, object> provideValue)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("A markup extension requires a name.", nameof(name));
            if (provideValue == null)
                throw new ArgumentNullException(nameof(provideValue));
            _markupExtensions[name] = provideValue;
        }

        internal bool TryProvideMarkupExtension(string value, out object result)
        {
            result = null;
            if (string.IsNullOrEmpty(value) || value[0] != '{' || value[value.Length - 1] != '}')
                return false;
            int separator = value.IndexOf(' ');
            if (separator < 2)
                return false;
            Func<string, object> provideValue;
            string name = value.Substring(1, separator - 1);
            if (!_markupExtensions.TryGetValue(name, out provideValue))
            {
                if (name.IndexOf('.') >= 0)
                    throw new FormatException("Unknown markup extension '" + name + "'.");
                return false;
            }
            string argument = value.Substring(separator + 1, value.Length - separator - 2).Trim();
            if (argument.Length == 0)
                throw new FormatException("Markup extension requires an argument: " + value);
            result = provideValue(argument);
            return true;
        }

        public void RegisterProperty<TControl, TValue>(
            string xmlName,
            string propertyName,
            Action<TControl, TValue> setter)
            where TControl : Control
        {
            Entry entry;
            if (!_entries.TryGetValue(xmlName, out entry))
                throw new InvalidOperationException(xmlName + " must be registered first.");
            entry.PropertyTypes[propertyName] = typeof(TValue);
            entry.ValueSetters[propertyName] = (control, value) =>
                setter((TControl)control, ConvertTemplateBindingValue<TValue>(value));
            entry.Setters[propertyName] = (control, text, templatedParent) =>
            {
                TemplateBindingExtension binding;
                if (TemplateBindingExtension.TryParse(text, out binding))
                {
                    AttachTemplateBinding(
                        control,
                        templatedParent,
                        binding.Property,
                        value =>
                        {
                            object converted = binding.ConvertValue(templatedParent, value, typeof(TValue));
                            if (!ReferenceEquals(converted, Acrux.Data.BindingOperations.DoNothing))
                                setter((TControl)control, ConvertTemplateBindingValue<TValue>(converted));
                        });
                    return;
                }
                setter((TControl)control, (TValue)_converter.Convert(text, typeof(TValue)));
            };
        }

        static TValue ConvertTemplateBindingValue<TValue>(object value)
        {
            return ConvertValue<TValue>(value);
        }

        internal static TValue ConvertValue<TValue>(object value)
        {
            if (value == null)
                return default(TValue);
            if (value is TValue)
                return (TValue)value;
            if (typeof(TValue) == typeof(Acrux.Styling.BackgroundBrush) &&
                value is VRageMath.Color)
            {
                return (TValue)(object)new Acrux.Styling.SolidColorBrush(
                    (VRageMath.Color)value);
            }
            object number;
            if (TryConvertNumber(value, typeof(TValue), out number))
                return (TValue)number;
            // A single number is a uniform Vector4, so float CornerRadius values keep working.
            if (typeof(TValue) == typeof(VRageMath.Vector4) && TryConvertNumber(value, typeof(float), out number))
                return (TValue)(object)new VRageMath.Vector4((float)number);
            // Enums cross mod boundaries as integers; unboxing an int as an enum is not allowed.
            object fallback = default(TValue);
            if (fallback is Enum && (value is int || value is long || value is short || value is byte || value is Enum))
                return (TValue)Enum.ToObject(typeof(TValue), Convert.ToInt64(value));
            return (TValue)value;
        }

        static bool TryConvertNumber(object value, Type target, out object result)
        {
            result = null;
            double number;
            if (value is int) number = (int)value;
            else if (value is float) number = (float)value;
            else if (value is double) number = (double)value;
            else if (value is long) number = (long)value;
            else if (value is short) number = (short)value;
            else if (value is byte) number = (byte)value;
            else return false;
            if (target == typeof(float)) result = (float)number;
            else if (target == typeof(double)) result = number;
            else if (target == typeof(int)) result = (int)number;
            else if (target == typeof(long)) result = (long)number;
            else return false;
            return true;
        }

        internal bool TryGetValueSetter(
            Control control, string xmlName, string propertyName, out Action<object> setter, out Type valueType)
        {
            setter = GetCommonSetter(control, propertyName);
            valueType = GetCommonType(propertyName);
            if (setter != null)
            {
                Action<object> common = setter;
                Type commonType = valueType;
                setter = value => common(ConvertCommon(value, commonType));
                return true;
            }
            Entry entry;
            Action<Control, object> valueSetter;
            if (!_entries.TryGetValue(xmlName, out entry) ||
                !entry.ValueSetters.TryGetValue(propertyName, out valueSetter))
                return false;
            entry.PropertyTypes.TryGetValue(propertyName, out valueType);
            setter = value => valueSetter(control, value);
            return true;
        }

        static object ConvertCommon(object value, Type type)
        {
            // A binding without a source gives default(T), as the compiled view's typed binding does.
            if (value == null)
                return DefaultOfCommon(type);
            object number;
            return TryConvertNumber(value, type, out number) ? number : value;
        }

        static object DefaultOfCommon(Type type)
        {
            if (type == typeof(bool)) return false;
            if (type == typeof(float)) return 0f;
            if (type == typeof(VRageMath.Vector4)) return VRageMath.Vector4.Zero;
            if (type == typeof(PointerCursor)) return PointerCursor.Inherit;
            if (type == typeof(HorizontalAlignment)) return default(HorizontalAlignment);
            if (type == typeof(VerticalAlignment)) return default(VerticalAlignment);
            if (type == typeof(Acrux.Controls.Primitives.PlacementMode)) return default(Acrux.Controls.Primitives.PlacementMode);
            return null;
        }

        static Type GetCommonType(string propertyName)
        {
            switch (propertyName)
            {
                case "Classes": return typeof(string);
                case "IsVisible":
                case "IsEnabled":
                case "ClipToBounds": return typeof(bool);
                case "Cursor": return typeof(PointerCursor);
                case "Width":
                case "Height":
                case "MinWidth":
                case "MinHeight":
                case "MaxWidth":
                case "MaxHeight": return typeof(float);
                case "Margin": return typeof(VRageMath.Vector4);
                case "OpacityMask": return typeof(Acrux.Styling.BackgroundBrush);
                case "RenderTransform": return typeof(Acrux.Styling.Transform);
                case "ToolTip.Tip": return typeof(object);
                case "ToolTip.Placement": return typeof(Acrux.Controls.Primitives.PlacementMode);
                case "HorizontalAlignment": return typeof(HorizontalAlignment);
                case "VerticalAlignment": return typeof(VerticalAlignment);
                default: return null;
            }
        }

        internal void SetPropertyValue(Control control, string xmlName, string propertyName, object value)
        {
            Action<object> common = GetCommonSetter(control, propertyName);
            if (common != null)
            {
                common(value);
                return;
            }
            Entry entry;
            Action<Control, object> setter;
            if (!_entries.TryGetValue(xmlName, out entry) || !entry.ValueSetters.TryGetValue(propertyName, out setter))
                throw new InvalidOperationException("Unknown property '" + xmlName + "." + propertyName + "'.");
            setter(control, value);
        }

        public Control Create(string xmlName)
        {
            Entry entry;
            if (!_entries.TryGetValue(xmlName, out entry) || entry.Factory == null)
                throw new InvalidOperationException("Unknown UI element '" + xmlName + "'.");
            return entry.Factory();
        }

        public void SetProperty(Control control, string xmlName, string propertyName, string value)
        {
            SetProperty(control, xmlName, propertyName, value, control.TemplatedParent);
        }

        internal void SetProperty(
            Control control,
            string xmlName,
            string propertyName,
            string value,
            TemplatedControl templatedParent)
        {
            object provided;
            if (TryProvideMarkupExtension(value, out provided))
            {
                Action<object> valueSetter;
                Type valueType;
                if (!TryGetValueSetter(control, xmlName, propertyName, out valueSetter, out valueType))
                    throw new InvalidOperationException("Unknown property '" + xmlName + "." + propertyName + "'.");
                valueSetter(provided);
                return;
            }
            if (SetCommonProperty(control, propertyName, value, templatedParent))
                return;
            Entry entry;
            Action<Control, string, TemplatedControl> setter;
            if (!_entries.TryGetValue(xmlName, out entry) ||
                !entry.Setters.TryGetValue(propertyName, out setter))
                throw new InvalidOperationException(
                    "Unknown property '" + xmlName + "." + propertyName + "'.");
            setter(control, value, templatedParent);
        }

        bool SetCommonProperty(
            Control control,
            string propertyName,
            string value,
            TemplatedControl templatedParent)
        {
            TemplateBindingExtension binding;
            if (TemplateBindingExtension.TryParse(value, out binding))
            {
                Action<object> setter = GetCommonSetter(control, propertyName);
                if (setter == null)
                    return false;
                Type targetType = null;
                AttachTemplateBinding(
                    control, templatedParent, binding.Property, source =>
                    {
                        if (targetType == null)
                            targetType = propertyName == "IsVisible" || propertyName == "IsEnabled" ||
                                         propertyName == "ClipToBounds"
                                ? typeof(bool)
                                : FindStyledProperty(control, propertyName).ValueType;
                        object converted = binding.ConvertValue(templatedParent, source, targetType);
                        if (!ReferenceEquals(converted, Acrux.Data.BindingOperations.DoNothing))
                            setter(converted);
                    });
                return true;
            }
            switch (propertyName)
            {
                case "Name": control.Name = value; return true;
                case "Classes": control.StyleClass = value; return true;
                case "IsVisible": control.Visible = (bool)_converter.Convert(value, typeof(bool)); return true;
                case "IsEnabled": control.Enabled = (bool)_converter.Convert(value, typeof(bool)); return true;
                case "Cursor":
                    control.Cursor = (PointerCursor)_converter.Convert(
                        value, typeof(PointerCursor));
                    return true;
                case "Width": control.Width = (float)_converter.Convert(value, typeof(float)); return true;
                case "Height": control.Height = (float)_converter.Convert(value, typeof(float)); return true;
                case "MinWidth": control.MinWidth = (float)_converter.Convert(value, typeof(float)); return true;
                case "MinHeight": control.MinHeight = (float)_converter.Convert(value, typeof(float)); return true;
                case "MaxWidth": control.MaxWidth = (float)_converter.Convert(value, typeof(float)); return true;
                case "MaxHeight": control.MaxHeight = (float)_converter.Convert(value, typeof(float)); return true;
                case "Margin": control.Margin = (VRageMath.Vector4)_converter.Convert(value, typeof(VRageMath.Vector4)); return true;
                case "ClipToBounds": control.ClipToBounds = (bool)_converter.Convert(value, typeof(bool)); return true;
                case "OpacityMask": control.OpacityMask = (Acrux.Styling.BackgroundBrush)_converter.Convert(value, typeof(Acrux.Styling.BackgroundBrush)); return true;
                case "RenderTransform": control.RenderTransform = (Acrux.Styling.Transform)_converter.Convert(value, typeof(Acrux.Styling.Transform)); return true;
                case "ToolTip.Tip": Acrux.Controls.ToolTip.SetTip(control, value); return true;
                case "ToolTip.Placement":
                    Acrux.Controls.ToolTip.SetPlacement(control,
                        (Acrux.Controls.Primitives.PlacementMode)_converter.Convert(
                            value, typeof(Acrux.Controls.Primitives.PlacementMode)));
                    return true;
                case "HorizontalAlignment":
                    control.HorizontalAlignment = (HorizontalAlignment)_converter.Convert(
                        value, typeof(HorizontalAlignment));
                    return true;
                case "VerticalAlignment":
                    control.VerticalAlignment = (VerticalAlignment)_converter.Convert(
                        value, typeof(VerticalAlignment));
                    return true;
                default: return false;
            }
        }

        static Action<object> GetCommonSetter(Control control, string propertyName)
        {
            switch (propertyName)
            {
                // As the compiled view: a bound Classes sets the style classes (a space-separated list).
                case "Classes": return value => control.StyleClass = value as string;
                case "IsVisible": return value => control.Visible = (bool)value;
                case "IsEnabled": return value => control.Enabled = (bool)value;
                case "Cursor": return value => control.Cursor = (PointerCursor)value;
                case "Width": return value => control.Width = (float)value;
                case "Height": return value => control.Height = (float)value;
                case "MinWidth": return value => control.MinWidth = (float)value;
                case "MinHeight": return value => control.MinHeight = (float)value;
                case "MaxWidth": return value => control.MaxWidth = (float)value;
                case "MaxHeight": return value => control.MaxHeight = (float)value;
                case "Margin": return value => control.Margin = (VRageMath.Vector4)value;
                case "ClipToBounds": return value => control.ClipToBounds = (bool)value;
                case "OpacityMask": return value => control.OpacityMask = value as Acrux.Styling.BackgroundBrush;
                case "RenderTransform": return value => control.RenderTransform = value as Acrux.Styling.Transform;
                case "ToolTip.Tip": return value => Acrux.Controls.ToolTip.SetTip(control, value);
                case "ToolTip.Placement": return value => Acrux.Controls.ToolTip.SetPlacement(control,
                    (Acrux.Controls.Primitives.PlacementMode)value);
                case "HorizontalAlignment":
                    return value => control.HorizontalAlignment = (HorizontalAlignment)value;
                case "VerticalAlignment":
                    return value => control.VerticalAlignment = (VerticalAlignment)value;
                default: return null;
            }
        }

        static void AttachTemplateBinding(
            Control target,
            TemplatedControl owner,
            string propertyName,
            Action<object> setter)
        {
            if (owner == null)
                throw new InvalidOperationException(
                    "TemplateBinding can only be used inside a ControlTemplate.");
            IStyledProperty source = FindStyledProperty(owner, propertyName);
            var subscription = new TemplateBindingSubscription(
                owner, source, setter);
            target.RegisterLifetime(subscription);
        }

        static readonly Dictionary<object, Dictionary<string, IStyledProperty>> StyledPropertyLookups = CreateLookupCache();

        static Dictionary<object, Dictionary<string, IStyledProperty>> CreateLookupCache()
        {
            var cache = new Dictionary<object, Dictionary<string, IStyledProperty>>();
            StyledPropertyCatalog.Registered += property => { lock (cache) cache.Clear(); };
            return cache;
        }

        static IStyledProperty FindStyledProperty(Control owner, string propertyName)
        {
            object type = owner.StyleKey;
            lock (StyledPropertyLookups)
            {
                Dictionary<string, IStyledProperty> byName;
                if (!StyledPropertyLookups.TryGetValue(type, out byName))
                    StyledPropertyLookups[type] = byName = new Dictionary<string, IStyledProperty>(StringComparer.Ordinal);
                IStyledProperty cached;
                if (byName.TryGetValue(propertyName, out cached))
                    return cached;
                cached = ScanStyledProperty(owner, propertyName);
                byName[propertyName] = cached;
                return cached;
            }
        }

        static IStyledProperty ScanStyledProperty(Control owner, string propertyName)
        {
            IList<IStyledProperty> properties = StyledPropertyCatalog.Snapshot();
            IStyledProperty match = null;
            for (int i = 0; i < properties.Count; i++)
            {
                IStyledProperty property = properties[i];
                if (string.Equals(property.Name, propertyName, StringComparison.Ordinal) &&
                    property.Matches(owner))
                    match = property;
            }
            if (match == null)
                throw new InvalidOperationException(
                    "Unknown template property '" + owner.GetType().Name + "." +
                    propertyName + "'.");
            return match;
        }

        sealed class TemplateBindingSubscription : IDisposable
        {
            readonly TemplatedControl _owner;
            readonly IStyledProperty _property;
            readonly Action<object> _setter;

            public TemplateBindingSubscription(
                TemplatedControl owner,
                IStyledProperty property,
                Action<object> setter)
            {
                _owner = owner;
                _property = property;
                _setter = setter;
                _owner.StyledPropertyChanged += OnPropertyChanged;
                _owner.ResourcesChanged += OnResourcesChanged;
                Apply();
            }

            void OnPropertyChanged(Control control, IStyledProperty property)
            {
                if (ReferenceEquals(property, _property))
                    Apply();
            }

            void Apply()
            {
                _setter(_property.GetValueObject(_owner));
            }

            void OnResourcesChanged(string key) { Apply(); }

            public void Dispose()
            {
                _owner.StyledPropertyChanged -= OnPropertyChanged;
                _owner.ResourcesChanged -= OnResourcesChanged;
            }
        }
    }
}
