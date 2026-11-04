using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace Adk.Mvvm
{
    /// <summary>
    /// Base class for every view model: the one observable object shared by every ADK mod and the UI frameworks built on ADK.
    /// <see cref="PropertyChanged"/> is the allocation-free notification for code in the same mod; bindings use
    /// the standard <see cref="INotifyPropertyChanged"/> event, a BCL type, so they also see view models of another
    /// mod (each mod compiles its own copy of this class).
    /// </summary>
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        public event Action<ObservableObject, string> PropertyChanged;
        event PropertyChangedEventHandler INotifyPropertyChanged.PropertyChanged
        {
            add { StandardPropertyChanged += value; }
            remove { StandardPropertyChanged -= value; }
        }
        event PropertyChangedEventHandler StandardPropertyChanged;

        protected bool RaiseAndSetIfChanged<T>(ref T field, T value, string propertyName)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;

            field = value;
            RaisePropertyChangedCore(propertyName);
            return true;
        }

        public bool RaisePropertyChanged<T>(string propertyName)
        {
            RaisePropertyChangedCore(propertyName);
            return true;
        }

        void RaisePropertyChangedCore(string propertyName)
        {
            var handlers = PropertyChanged;
            Exception firstError = null;
            if (handlers != null)
            {
                foreach (Action<ObservableObject, string> handler in handlers.GetInvocationList())
                {
                    try
                    {
                        handler(this, propertyName);
                    }
                    catch (Exception e)
                    {
                        if (firstError == null)
                            firstError = e;
                    }
                }
            }

            var standardHandlers = StandardPropertyChanged;
            if (standardHandlers != null)
            {
                var args = new PropertyChangedEventArgs(propertyName);
                foreach (PropertyChangedEventHandler handler in standardHandlers.GetInvocationList())
                {
                    try
                    {
                        handler(this, args);
                    }
                    catch (Exception e)
                    {
                        if (firstError == null)
                            firstError = e;
                    }
                }
            }

            if (firstError != null)
                throw firstError;
        }
    }
}
