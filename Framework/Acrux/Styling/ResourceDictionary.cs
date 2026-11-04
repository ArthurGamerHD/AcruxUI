using System;
using System.Collections.Generic;

namespace Acrux.Styling
{
    public sealed class ResourceDictionary
    {
        readonly Dictionary<string, object> _values =
            new Dictionary<string, object>(StringComparer.Ordinal);
        ResourceDictionary _parent;

        public ResourceDictionary Parent
        {
            get { return _parent; }
            set
            {
                if (ReferenceEquals(_parent, value))
                    return;
                if (_parent != null)
                    _parent.Changed -= OnParentChanged;
                _parent = value;
                if (_parent != null)
                    _parent.Changed += OnParentChanged;
                RaiseChanged(null);
            }
        }
        public event Action<string> Changed;

        public void Add(string key, object value)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("A resource requires a key.", nameof(key));
            _values[key] = value;
            RaiseChanged(key);
        }

        public bool TryGetValue<T>(string key, out T value)
        {
            object candidate;
            if (_values.TryGetValue(key, out candidate) && candidate is T)
            {
                value = (T)candidate;
                return true;
            }
            if (Parent != null)
                return Parent.TryGetValue(key, out value);
            value = default(T);
            return false;
        }

        void OnParentChanged(string key)
        {
            RaiseChanged(key);
        }

        void RaiseChanged(string key)
        {
            Action<string> changed = Changed;
            if (changed != null)
                changed(key);
        }
    }
}
