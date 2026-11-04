using System;
using System.Collections;

namespace Adk.Mvvm
{
    internal class ReadOnlyList : IList
    {
        private IList _list;
        public ReadOnlyList(IList list)
        {
            _list = list;
        }
        
        public IEnumerator GetEnumerator() => _list.GetEnumerator();

        public void CopyTo(Array array, int index) => _list.CopyTo(array, index);

        public int Count => _list.Count;
        public object SyncRoot => _list.SyncRoot;
        public bool IsSynchronized => _list.IsSynchronized;
        public int Add(object value)
        {
            throw new InvalidOperationException("Items cannot be added to a Read Only list");
        }

        public bool Contains(object value)
            => _list.Contains(value);

        public void Clear() => ThrowModified();

        public int IndexOf(object value) => _list.IndexOf(value);

        public void Insert(int index, object value) => ThrowModified();

        public void Remove(object value) => ThrowModified();

        public void RemoveAt(int index) => ThrowModified();

        public object this[int index]
        {
            get {  return _list[index]; }
            set
            {
                if (value == null) throw new ArgumentNullException(nameof(value));
                ThrowModified();
            }
        }

        public bool IsReadOnly => true;
        public bool IsFixedSize => _list.IsFixedSize;
        
        private static void ThrowModified()
        {
            throw new InvalidOperationException("Read Only list cannot be modified");
        }
    }
}