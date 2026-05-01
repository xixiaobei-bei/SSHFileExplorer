using Microsoft.UI.Xaml.Controls;

namespace SSHFileExplorer
{
    public sealed partial class UploadDialog : ContentDialog
    {
        public bool IsCurrentLocation => CurrentLocationRadio.IsChecked == true;
        public string TargetPath => TargetPathTextBox.Text;

        public UploadDialog(string currentPath)
        {
            InitializeComponent();
            TargetPathTextBox.Text = currentPath;
        }
    }
}