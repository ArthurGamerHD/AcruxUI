using System.Collections.Generic;

namespace Adk.Mvvm
{
    public interface IObservableList<T> : INotifyCollectionChanged, IList<T>
    {
    }
}
