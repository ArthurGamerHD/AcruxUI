using Acrux.Controls;

namespace Acrux.Dialogs
{
    public sealed partial class FilePickerView : Window
    {
        public FilePickerView(FilePickerViewModel model)
        {
            DataContext = model;
            InitializeComponent();
        }
    }
}
