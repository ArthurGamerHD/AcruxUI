using System;

namespace Acrux.Styling
{
    /// <summary>
    /// Supplies named resources to a visual tree through its data context.
    /// This keeps dynamic-resource lookup typed and reflection-free.
    /// </summary>
    public interface IResourceProvider
    {
        event Action<string> ResourceChanged;
        bool TryGetResource<T>(string key, out T value);
    }
}
