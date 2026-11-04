using System.Collections.Generic;

namespace Adk.Mvvm
{
    public interface IObservableDictionary<TKey, TValue> : INotifyCollectionChanged, IDictionary<TKey, TValue>
    {
    }
}
