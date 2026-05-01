using Microsoft.UI.Xaml.Media.Imaging;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml.Controls;
using System.Threading.Tasks;

namespace SSHFileExplorer
{
    public class FileItem : INotifyPropertyChanged
    {
        private string? name;
        private string? path;
        private bool isDirectory;
        private BitmapImage? icon;

        public string? Name 
        { 
            get => name;
            set
            {
                if (name != value)
                {
                    name = value;
                    OnPropertyChanged(nameof(Name));
                }
            }
        }
        
        public string? Path 
        { 
            get => path;
            set
            {
                if (path != value)
                {
                    path = value;
                    OnPropertyChanged(nameof(Path));
                }
            }
        }
        
        public bool IsDirectory 
        { 
            get => isDirectory;
            set
            {
                if (isDirectory != value)
                {
                    isDirectory = value;
                    OnPropertyChanged(nameof(IsDirectory));
                }
            }
        }
        
        public BitmapImage? Icon 
        { 
            get => icon;
            set
            {
                if (icon != value)
                {
                    icon = value;
                    OnPropertyChanged(nameof(Icon));
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        // 调用此方法以触发 PropertyChanged 事件
        public void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
