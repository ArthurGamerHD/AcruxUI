using System;
using System.ComponentModel;
using Acrux.Controls;
using Acrux.Core;
using Acrux.Core.Input;
using Acrux.Core.Invalidation;
using Acrux.Core.PropertySystem;
using Acrux.Rendering;
using VRageMath;

namespace Sample
{
    /// <summary>Its Label comes from ADK's [ObservableProperty]; the view generator binds it without seeing that output.</summary>
    public sealed partial class CounterItem : Adk.Mvvm.ObservableObject
    {
        [Adk.Mvvm.ObservableProperty] string _label;
    }

    public enum GaugeMode { Off, Low, High }

    /// <summary>A view model on ADK's ObservableObject, which the client compiles from ClientApi/Shared.</summary>
    public sealed class TitleViewModel : Adk.Mvvm.ObservableObject
    {
        string _title;

        public string Title
        {
            get { return _title; }
            set { RaiseAndSetIfChanged(ref _title, value, nameof(Title)); }
        }
    }

    /// <summary>
    /// A <c>{Binding Title, Mode=TwoWay}</c> as generated XAML builds it: the binding is attached first, then the view
    /// model arrives as an inherited DataContext, so the toolkit calls back with its own object for the source.
    /// </summary>
    public static class TwoWaySample
    {
        public static Acrux.Controls.TextBox Bind(TitleViewModel model, out Acrux.Controls.Border host)
        {
            var box = new Acrux.Controls.TextBox();
            Acrux.Core.Binding.CompiledTwoWayBinding.Attach<TitleViewModel, string>(box, Acrux.Controls.TextBox.TextProperty,
                source => source.Title, (source, value) => source.Title = value, "Title");
            host = new Acrux.Controls.Border { DataContext = model };
            host.Child = box;
            return box;
        }
    }

    public sealed class DoublerConverter : Acrux.Data.Converters.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return "x2=" + ((int)value * 2);
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return value;
        }
    }

    public sealed class CounterViewModel : INotifyPropertyChanged, Acrux.Styling.IResourceProvider
    {
        public event Action<string> ResourceChanged;
        public GaugeMode Mode => _count > 0 ? GaugeMode.High : GaugeMode.Low;

        public bool TryGetResource<T>(string key, out T value)
        {
            if (key == "accent" && typeof(T) == typeof(Color))
            {
                value = (T)(object)Color.Orange;
                return true;
            }
            value = default(T);
            return false;
        }

        public System.Collections.Generic.List<CounterItem> Items { get; } =
            new System.Collections.Generic.List<CounterItem> { new CounterItem { Label = "item A" }, new CounterItem { Label = "item B" } };

        int _count;
        public event PropertyChangedEventHandler PropertyChanged;

        public CounterViewModel()
        {
            IncrementCommand = new DelegateCommand(_ => Count++, _ => true);
        }

        public ICommand IncrementCommand { get; private set; }

        public int Count
        {
            get { return _count; }
            set
            {
                _count = value;
                Raise("Count");
                Raise("Title");
                Raise("Fraction");
                Raise("Mode");
            }
        }

        public string Title => "Clicks: " + _count;
        public float Fraction => Math.Min(1f, _count / 10f);

        void Raise(string name)
        {
            var handler = PropertyChanged;
            if (handler != null)
                handler(this, new PropertyChangedEventArgs(name));
        }
    }

    public partial class CounterView : UserControl
    {
        public CounterView()
        {
            Resources.Add("Doubler", new DoublerConverter());
            InitializeComponent();
        }

        public Acrux.Controls.Border AccentBorder => Accent;

        public Button IncrementButton => Increment;
        public TextBlock TitleBlock => Title;
        public Gauge MeterControl => Meter;
        public Acrux.Controls.Border InlineBoxBorder => InlineBox;
    }

    /// <summary>A client templated control whose look comes from a toolkit-parsed style with TemplateBindings.</summary>
    public sealed class Badge : TemplatedControl
    {
        public static readonly StyledProperty<string> LabelProperty =
            StyledProperty.Register<Badge, string>(nameof(Label), string.Empty, UiInvalidation.Measure);

        public static readonly StyledProperty<Color> FillProperty =
            StyledProperty.Register<Badge, Color>(nameof(Fill), Color.Transparent, UiInvalidation.Render);

        public string Label
        {
            get { return GetValue(LabelProperty); }
            set { SetValue(LabelProperty, value); }
        }

        public Color Fill
        {
            get { return GetValue(FillProperty); }
            set { SetValue(FillProperty, value); }
        }

        public int Presses { get; private set; }

        protected override bool HitTestSelf(Vector2 point)
        {
            return true;
        }

        internal override void PointerPressed(Vector2 point)
        {
            Presses++;
        }
    }

    /// <summary>A client panel (like a mod's custom layout) arranging toolkit children side by side.</summary>
    public sealed class SidePanel : Acrux.Layout.Panel
    {
        public int Arranges { get; private set; }

        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            var children = LogicalChildren;
            for (int i = 0; i < children.Count; i++)
                children[i].Measure(new Vector2(availableSize.X / 2f, 20f));
            return new Vector2(availableSize.X, 20f);
        }

        protected override void ArrangeChildren(RectangleF bounds)
        {
            Arranges++;
            var children = LogicalChildren;
            for (int i = 0; i < children.Count; i++)
                children[i].Arrange(new RectangleF(bounds.X + i * bounds.Width / 2f, bounds.Y, bounds.Width / 2f, 20f));
        }
    }

    /// <summary>A client control with its own styled property, measure and render override.</summary>
    public sealed class Gauge : VisualElement
    {
        public static readonly StyledProperty<float> ValueProperty =
            StyledProperty.Register<Gauge, float>(nameof(Value), 0f, UiInvalidation.Render);

        public static readonly StyledProperty<GaugeMode> ModeProperty =
            StyledProperty.Register<Gauge, GaugeMode>(nameof(Mode), GaugeMode.Off, UiInvalidation.Render);

        public GaugeMode Mode
        {
            get { return GetValue(ModeProperty); }
            set { SetValue(ModeProperty, value); }
        }

        public float Value
        {
            get { return GetValue(ValueProperty); }
            set { SetValue(ValueProperty, value); }
        }

        public int Draws { get; private set; }
        public bool SameFactory { get; private set; }
        public bool SameContext { get; private set; }
        object _lastFactory, _lastContext;
        public int PropertyChanges { get; private set; }

        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            return new Vector2(50, 20);
        }

        protected override void DrawSelf(RenderContext context)
        {
            Draws++;
            var factory = context.DynamicTextures;
            if (_lastFactory != null)
                SameFactory = ReferenceEquals(_lastFactory, factory) && ReferenceEquals(factory, context.DynamicTextures);
            _lastFactory = factory;
            if (_lastContext != null)
                SameContext = ReferenceEquals(_lastContext, context);
            _lastContext = context;
            RectangleF bounds = Bounds;
            context.Fill(bounds, Color.DarkGray);
            context.Fill(new RectangleF(bounds.X, bounds.Y, bounds.Width * Value, bounds.Height), Color.Lime);
        }

        protected override void OnPropertyChanged(IStyledProperty property)
        {
            base.OnPropertyChanged(property);
            if (ReferenceEquals(property, ValueProperty))
                PropertyChanges++;
        }
    }
}
