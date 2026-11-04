using System;
using Acrux.Core.Input;
using Acrux.Styling;
using AcruxAppTemplate.Client.Resources;
using AcruxAppTemplate.Model;
using Adk.Mvvm;

namespace AcruxAppTemplate.Client.ViewModel
{
    /// <summary>
    /// The view model of <see cref="Client.View.CounterView"/>. Keep one per surface and mutate it; never rebuild it per frame.
    /// [ObservableProperty] fields become notifying properties (Adk.Analyzer); [PreviewWith] values feed the
    /// generated CounterViewModelPreview the IDE preview binds to (Acrux.Generator, design-only: MDK does not pack it).
    /// It also hands the theme's resources to the view, since {DynamicResource} resolves through the data context.
    /// </summary>
    [PreviewWith("Title", "Acrux App Template")]
    public sealed partial class CounterViewModel : ObservableObject, IResourceProvider
    {
        [PreviewWith(3)] CountModel _countModel;
        [PreviewWith("3")] [ObservableProperty] string _countText = "0";
        [PreviewWith("Clicked 3 times\n(not functional on preview)")] [ObservableProperty] string _status = "Not clicked yet";

        int Count
        {
            get
            {
                return _countModel.Number;
            } 
            set
            {
                _countModel.Number = value;
                CountText = Count.ToString();
                Status = Count == 1 ? "Clicked once" : "Clicked " + Count + " times";
            }
        }
        
        
        
        readonly SurfaceTheme _theme;

        public CounterViewModel(SurfaceTheme theme)
        {
            _theme = theme;
            _countModel = new CountModel();
            Title = "Acrux App Template";
            IncrementCommand = new DelegateCommand(_ => Increment());
            ResetCommand = new DelegateCommand(_ => Reset());
        }

        public string Title { get; }
        public ICommand IncrementCommand { get; }
        public ICommand ResetCommand { get; }

        public event Action<string> ResourceChanged
        {
            add { _theme.ResourceChanged += value; }
            remove { _theme.ResourceChanged -= value; }
        }

        public bool TryGetResource<T>(string key, out T value)
        {
            return _theme.TryGetResource(key, out value);
        }

        void Increment()
        {
            Count++;
        }

        void Reset()
        {
            _countModel = new CountModel();
            Count = 0;
        }
    }
}
