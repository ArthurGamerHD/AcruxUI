using System;
using Acrux.Core;

namespace Acrux.Styling
{
    public interface IDataTemplate
    {
        VisualElement Build(object value);
    }

    public sealed class DataTemplate : IDataTemplate
    {
        readonly Func<object, VisualElement> _factory;

        public DataTemplate(Func<object, VisualElement> factory)
        {
            if (factory == null)
                throw new ArgumentNullException(nameof(factory));
            _factory = factory;
        }

        public VisualElement Build(object value)
        {
            return _factory(value);
        }
    }

    public sealed class DataTemplate<TData> : IDataTemplate
    {
        readonly Func<TData, VisualElement> _factory;

        public DataTemplate(Func<TData, VisualElement> factory)
        {
            if (factory == null)
                throw new ArgumentNullException(nameof(factory));
            _factory = factory;
        }

        public VisualElement Build(TData value)
        {
            return _factory(value);
        }

        VisualElement IDataTemplate.Build(object value)
        {
            return Build((TData)value);
        }
    }
}
