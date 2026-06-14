using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SSHFileExplorer
{
    // Connection dialog for SSH server connection settings
    // SSH服务器连接设置的对话框
    public sealed partial class ConnectDialog : ContentDialog
    {
        // Get host/IP from input field
        // 从输入字段获取主机/IP
        public string Host => HostTextBox.Text.Trim();
        
        // Get username from input field
        // 从输入字段获取用户名
        public string User => UserTextBox.Text.Trim();
        
        // Get password from input field
        // 从输入字段获取密码
        public string Password => PasswordBox.Password;
        
        // Get private key path from input field (nullable)
        // 从输入字段获取私钥路径（可为空）
        public string? PrivateKeyPath => PrivateKeyPathTextBox?.Text?.Trim();  // May be null
        
        // Get display name from input field
        // 从输入字段获取显示名称
        public string DisplayName => DisplayNameTextBox.Text.Trim();

        // Whether the user wants to save this connection
        // 用户是否要保存此连接
        public bool ShouldSave => SaveCheckBox.IsChecked == true;

        // Get port from input field, default to 22 if invalid
        // 从输入字段获取端口，默认为22（如果无效）
        public int Port => int.TryParse(PortTextBox.Text, out int p) ? p : 22;

        public ConnectDialog()
        {
            this.InitializeComponent();

            // 绑定保存复选框的状态变化事件
            // Bind save checkbox state change events
            SaveCheckBox.Checked += SaveCheckBox_Checked;
            SaveCheckBox.Unchecked += SaveCheckBox_Unchecked;
        }

        // 用户勾选保存复选框时显示名称输入框
        // Show display name text box when user checks the save checkbox
        private void SaveCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (DisplayNameTextBox != null)
                DisplayNameTextBox.Visibility = Visibility.Visible;
        }

        // 用户取消勾选保存复选框时隐藏名称输入框
        // Hide display name text box when user unchecks the save checkbox
        private void SaveCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (DisplayNameTextBox != null)
                DisplayNameTextBox.Visibility = Visibility.Collapsed;
        }

        // 用已保存的连接预填对话框（例如用户点左侧栏里的已保存项）
        // Pre-fill from an already-saved connection (e.g. user clicks saved item in left panel)
        public ConnectDialog(SavedConnection existing)
            : this()
        {
            HostTextBox.Text = existing.Host;
            UserTextBox.Text = existing.User;
            PasswordBox.Password = existing.Password;
            PrivateKeyPathTextBox.Text = existing.PrivateKeyPath ?? "";
            PortTextBox.Text = existing.Port.ToString();
            DisplayNameTextBox.Text = existing.DisplayName;
            SaveCheckBox.IsChecked = true;

            // 由于已勾选保存，确保名称输入框可见
            // Ensure display name text box is visible since save is checked
            if (DisplayNameTextBox != null)
                DisplayNameTextBox.Visibility = Visibility.Visible;
        }
    }
}