using System;

namespace Adk.Mvvm
{
    public interface INotifyCollectionChanged
    {
        /// <summary>Occurs when the collection changes.</summary>
        event NotifyCollectionChangedEventHandler CollectionChanged;
    }
}