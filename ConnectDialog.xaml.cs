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
        
        // Get port from input field, default to 22 if invalid
        // 从输入字段获取端口，默认为22（如果无效）
        public int Port => int.TryParse(PortTextBox.Text, out int p) ? p : 22;

        public ConnectDialog()
        {
            this.InitializeComponent();
        }
    }
}