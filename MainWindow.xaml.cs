using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using Windows.Storage;
using Renci.SshNet;
using Renci.SshNet.Sftp;
using System.Runtime.InteropServices;
using WinRT.Interop;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using System.Threading;

namespace SSHFileExplorer
{
    public sealed partial class MainWindow : Window
    {
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr LoadImage(IntPtr hInst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

        private const uint WM_SETICON = 0x0080;
        private const uint ICON_SMALL = 0;
        private const uint LR_LOADFROMFILE = 0x0010;
        private const uint IMAGE_ICON = 1;

        private SSHFileExplorer? SSHFileExplorer;
        private string currentPath = "/";

        // Add a lock to ensure sequential path operations
        // 添加一个锁来确保路径操作是顺序执行的
        private readonly SemaphoreSlim pathOperationSemaphore = new SemaphoreSlim(1, 1);

        public MainWindow()
        {
            this.InitializeComponent();

            // Set default welcome text, showing current Windows username
            // 设置默认的欢迎文本，显示当前Windows用户名
            string currentUserName = Environment.UserName;
            WelcomeTitle.Text = $"Hello！{currentUserName}";

            // Add window activation state change event handler
            // 添加窗口激活状态变化事件处理
            this.Activated += MainWindow_Activated;

            // Initialize title bar colors with theme color
            // 初始化标题栏颜色为主题色
            InitializeTitleBarColors();
        }

        // Initialize title bar colors with theme color
        // 初始化标题栏颜色为主题色
        private void InitializeTitleBarColors()
        {
            var titleBar = this.AppWindow.TitleBar;
            // Don't extend content into title bar, keep standard Windows title bar
            // 不扩展内容到标题栏，保留标准Windows标题栏
            titleBar.ExtendsContentIntoTitleBar = false;

            // Set title bar colors to theme color
            // 设置标题栏颜色为主题色
            titleBar.BackgroundColor = Microsoft.UI.Colors.Transparent; // Use system default background color
            titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent; // Button background transparent
        }

        // Handle window activation state change
        // 处理窗口激活状态变化
        private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated)
            {
                // Window loses focus, change button bar color to white
                // 窗口失去焦点，将按钮栏颜色改为白色
                ApplyInactiveTheme();
            }
            else // Other cases include Activated and PointerActivated
            {
                // Window gains focus, restore theme color
                // 窗口获得焦点，恢复主题色
                ApplyActiveTheme();
            }
        }

        // Apply inactive theme when window is not focused
        // 当窗口未获得焦点时应用非活动主题
        private void ApplyInactiveTheme()
        {
            var titleBar = this.AppWindow.TitleBar;
            // When window is unfocused, use lighter colors
            // 窗口失焦时使用较亮的颜色
            titleBar.ButtonForegroundColor = Microsoft.UI.Colors.LightGray;
            titleBar.ButtonHoverForegroundColor = Microsoft.UI.Colors.White;
            titleBar.ButtonPressedForegroundColor = Microsoft.UI.Colors.White;
            titleBar.ButtonInactiveForegroundColor = Microsoft.UI.Colors.DimGray;
        }

        // Apply active theme when window is focused
        // 当窗口获得焦点时应用活动主题
        private void ApplyActiveTheme()
        {
            var titleBar = this.AppWindow.TitleBar;
            // When window is focused, use white
            // 窗口聚焦时使用白色
            titleBar.ButtonForegroundColor = Microsoft.UI.Colors.White;
            titleBar.ButtonHoverForegroundColor = Microsoft.UI.Colors.LightGray;
            titleBar.ButtonPressedForegroundColor = Microsoft.UI.Colors.Gray;
            titleBar.ButtonInactiveForegroundColor = Microsoft.UI.Colors.DimGray;
        }

        // Show connection dialog to connect to SSH server
        // 显示连接对话框以连接到SSH服务器
        private async void ShowConnectDialog_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new ConnectDialog();
            var result = await dialog.ShowAsync();

            if (result == ContentDialogResult.Primary)
            {
                var host = dialog.Host;
                var user = dialog.User;  // Fixed: ConnectDialog class defines User property, not Username
                var password = dialog.Password;
                var privateKeyPath = dialog.PrivateKeyPath;
                var port = dialog.Port;

                if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(user))
                {
                    var errorDialog = new ContentDialog
                    {
                        Title = "Error",
                        Content = "Host and username cannot be empty!",
                        CloseButtonText = "OK",
                        XamlRoot = this.Content.XamlRoot
                    };
                    await errorDialog.ShowAsync();
                    return;
                }

                try
                {
                    SSHFileExplorer = new SSHFileExplorer(host, user, password, privateKeyPath, port);
                    SSHFileExplorer.Connect();

                    // Hide welcome panel
                    // 隐藏欢迎界面
                    WelcomePanel.Visibility = Visibility.Collapsed;
                    // Show file browser panel
                    // 显示文件浏览器界面
                    MainGrid.Visibility = Visibility.Visible;

                    // Load root directory
                    // 加载根目录
                    LoadFileList("/");

                    // Load directory tree
                    // 加载目录树
                    await LoadDirectoryTree();

                    // Set welcome title, display username
                    // 设置欢迎标题，显示用户名
                    WelcomeTitle.Text = $"Hello！{user}";
                }
                catch (Exception ex)
                {
                    var errorDialog = new ContentDialog
                    {
                        Title = "Connection failed",
                        Content = $"Cannot connect to SSH server: {ex.Message}",
                        CloseButtonText = "OK",
                        XamlRoot = this.Content.XamlRoot
                    };
                    await errorDialog.ShowAsync();
                }
            }
        }

        // Upload local file to SSH server
        // 上传本地文件到SSH服务器
        private async void UploadButton_Click(object sender, RoutedEventArgs e)
        {
            if (SSHFileExplorer == null) return;

            var picker = new FileOpenPicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.ViewMode = PickerViewMode.Thumbnail;
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeFilter.Add("*");

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                try
                {
                    // Show progress dialog
                    // 显示进度对话框
                    var progressDialog = new ContentDialog
                    {
                        Title = "Uploading...",
                        Content = $"Uploading {file.Name} to {currentPath}",
                        CloseButtonText = "Cancel",
                        XamlRoot = this.Content.XamlRoot
                    };

                    // Run upload in background thread
                    // 在后台线程运行上传
                    var uploadTask = Task.Run(() =>
                    {
                        SSHFileExplorer.UploadFile(file.Path, Path.Combine(currentPath, file.Name).Replace('\\', '/'));
                    });

                    // Show progress dialog and wait for upload to complete
                    // 显示进度对话框并等待上传完成
                    var dialogTask = progressDialog.ShowAsync();
                    await uploadTask;

                    // Close dialog after upload completes
                    // 上传完成后关闭对话框
                    progressDialog.Hide();

                    // Refresh file list
                    // 刷新文件列表
                    LoadFileList(currentPath);
                }
                catch (Exception ex)
                {
                    var errorDialog = new ContentDialog
                    {
                        Title = "Upload failed",
                        Content = $"Failed to upload file: {ex.Message}",
                        CloseButtonText = "OK",
                        XamlRoot = this.Content.XamlRoot
                    };
                    await errorDialog.ShowAsync();
                }
            }
        }

        // Download selected file from SSH server to local
        // 从SSH服务器下载选中文件到本地
        private async void DownloadButton_Click(object sender, RoutedEventArgs e)
        {
            if (SSHFileExplorer == null) return;

            var selectedItem = FileListView.SelectedItem as FileItem;
            if (selectedItem == null)
            {
                var errorDialog = new ContentDialog
                {
                    Title = "No selection",
                    Content = "Please select a file to download.",
                    CloseButtonText = "OK",
                    XamlRoot = this.Content.XamlRoot
                };
                await errorDialog.ShowAsync();
                return;
            }

            if (selectedItem.IsDirectory)
            {
                var errorDialog = new ContentDialog
                {
                    Title = "Invalid selection",
                    Content = "Cannot download a directory.",
                    CloseButtonText = "OK",
                    XamlRoot = this.Content.XamlRoot
                };
                await errorDialog.ShowAsync();
                return;
            }

            var picker = new FolderPicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.SuggestedStartLocation = PickerLocationId.Downloads;
            picker.FileTypeFilter.Add("*");

            var folder = await picker.PickSingleFolderAsync();
            if (folder != null)
            {
                try
                {
                    var localPath = Path.Combine(folder.Path, selectedItem.Name);
                    // Show progress dialog
                    // 显示进度对话框
                    var progressDialog = new ContentDialog
                    {
                        Title = "Downloading...",
                        Content = $"Downloading {selectedItem.Name} to {folder.Path}",
                        CloseButtonText = "Cancel",
                        XamlRoot = this.Content.XamlRoot
                    };

                    // Run download in background thread
                    // 在后台线程运行下载
                    var downloadTask = Task.Run(() =>
                    {
                        SSHFileExplorer.DownloadFile(selectedItem.Path, localPath);
                    });

                    // Show progress dialog and wait for download to complete
                    // 显示进度对话框并等待下载完成
                    var dialogTask = progressDialog.ShowAsync();
                    await downloadTask;

                    // Close dialog after download completes
                    // 下载完成后关闭对话框
                    progressDialog.Hide();
                }
                catch (Exception ex)
                {
                    var errorDialog = new ContentDialog
                    {
                        Title = "Download failed",
                        Content = $"Failed to download file: {ex.Message}",
                        CloseButtonText = "OK",
                        XamlRoot = this.Content.XamlRoot
                    };
                    await errorDialog.ShowAsync();
                }
            }
        }

        // Delete selected file or directory from SSH server
        // 从SSH服务器删除选中的文件或目录
        private async void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (SSHFileExplorer == null) return;

            var selectedItem = FileListView.SelectedItem as FileItem;
            if (selectedItem == null)
            {
                var errorDialog = new ContentDialog
                {
                    Title = "No selection",
                    Content = "Please select a file or directory to delete.",
                    CloseButtonText = "OK",
                    XamlRoot = this.Content.XamlRoot
                };
                await errorDialog.ShowAsync();
                return;
            }

            // Confirm deletion
            // 确认删除
            var confirmDialog = new ContentDialog
            {
                Title = "Confirm deletion",
                Content = $"Are you sure you want to delete {selectedItem.Name}? This cannot be undone.",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.Content.XamlRoot
            };

            var result = await confirmDialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                try
                {
                    if (selectedItem.IsDirectory)
                    {
                        // For now, only support deleting files, not directories
                        // 暂时只支持删除文件，不支持删除目录
                        var errorDialog = new ContentDialog
                        {
                            Title = "Not supported",
                            Content = "Deleting directories is not currently supported.",
                            CloseButtonText = "OK",
                            XamlRoot = this.Content.XamlRoot
                        };
                        await errorDialog.ShowAsync();
                    }
                    else
                    {
                        SSHFileExplorer.DeleteFile(selectedItem.Path);
                        // Refresh file list
                        // 刷新文件列表
                        LoadFileList(currentPath);
                    }
                }
                catch (Exception ex)
                {
                    var errorDialog = new ContentDialog
                    {
                        Title = "Delete failed",
                        Content = $"Failed to delete file: {ex.Message}",
                        CloseButtonText = "OK",
                        XamlRoot = this.Content.XamlRoot
                    };
                    await errorDialog.ShowAsync();
                }
            }
        }

        // Load file list from SSH server
        // 从SSH服务器加载文件列表
        private async void LoadFileList(string path)
        {
            await pathOperationSemaphore.WaitAsync();
            try
            {
                if (SSHFileExplorer == null) return;
                if (string.IsNullOrWhiteSpace(path))
                {
                    path = "/";
                }
                path = path.Trim();
                string previousPath = currentPath;
                currentPath = path;  // Update current path immediately
                UpdateAddressBar(path); // Update address bar early
                AddressBarTextBox.Visibility = Visibility.Collapsed;
                BreadcrumbPanel.Visibility = Visibility.Visible;
                FileListView.ItemsSource = null;
                try
                {
                    var files = SSHFileExplorer.ListDirectory(path)
                        .Where(file => file.Name != "." && file.Name != "..")
                        .OrderByDescending(file => file.IsDirectory)
                        .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    var list = new List<FileItem>();

                    // Add ".." to go to parent directory (if not root)
                    // 添加 ".." 返回上级目录（如果不是根目录）
                    if (path != "/" && !string.IsNullOrEmpty(path))
                    {
                        string parentPath = "/";
                        var trimmedPath = path.TrimEnd('/');
                        var lastSlashIndex = trimmedPath.LastIndexOf('/');

                        if (lastSlashIndex > 0)
                        {
                            parentPath = trimmedPath.Substring(0, lastSlashIndex);
                        }
                        else if (lastSlashIndex == 0)
                        {
                            // e.g. /home -> parent is /
                            // 例如 /home -> 父目录是 /
                            parentPath = "/";
                        }

                        list.Add(new FileItem
                        {
                            Name = "..",
                            Path = parentPath,
                            IsDirectory = true
                        });
                    }

                    foreach (var file in files)
                    {
                        var fileItem = new FileItem
                        {
                            Name = file.Name,
                            Path = file.FullName,
                            IsDirectory = file.IsDirectory
                        };
                        list.Add(fileItem);
                    }
                    FileListView.ItemsSource = list;
                    await UpdateFileListIconsAsync(list);
                    // No need to set currentPath again since it was set at the beginning of the method
                    // 不需要再次设置 currentPath，因为已经在方法开始时设置
                    UpdateAddressBar(path);
                }
                catch (Exception ex)
                {
                    // When exception occurs, revert to previous path
                    // 发生异常时，恢复到之前的路径
                    currentPath = previousPath;
                    var dialog = new ContentDialog
                    {
                        Title = "Error",
                        Content = $"Directory does not exist or is inaccessible: {ex.Message}",
                        CloseButtonText = "OK",
                        XamlRoot = this.Content.XamlRoot
                    };
                    await dialog.ShowAsync();
                    AddressBarTextBox.Visibility = Visibility.Collapsed;
                    BreadcrumbPanel.Visibility = Visibility.Visible;
                    UpdateAddressBar(previousPath);  // Show previous path
                }
            }
            finally
            {
                pathOperationSemaphore.Release();
            }
        }

        // Update icons for file list asynchronously
        // 异步更新文件列表图标
        private async Task UpdateFileListIconsAsync(List<FileItem> items)
        {
            foreach (var item in items)
            {
                // Ensure path is not null
                // 确保路径不为null
                var safePath = item.Path ?? string.Empty;
                item.Icon = await IconHelper.GetSystemIconAsync(item.IsDirectory, safePath);
            }
        }

        // Load directory tree from SSH server
        // 从SSH服务器加载目录树
        private async Task LoadDirectoryTree()
        {
            if (SSHFileExplorer == null) return;

            try
            {
                DirectoryTree.RootNodes.Clear();

                // Add root node
                // 添加根节点
                var rootNode = new TreeViewNode
                {
                    Content = new FileItem
                    {
                        Name = "/",
                        Path = "/",
                        IsDirectory = true
                    }
                };
                DirectoryTree.RootNodes.Add(rootNode);

                // Load children for root
                // 为根节点加载子节点
                await LoadDirectoryTreeChildren(rootNode);
            }
            catch (Exception ex)
            {
                var errorDialog = new ContentDialog
                {
                    Title = "Error",
                    Content = $"Failed to load directory tree: {ex.Message}",
                    CloseButtonText = "OK",
                    XamlRoot = this.Content.XamlRoot
                };
                await errorDialog.ShowAsync();
            }
        }

        // Load children nodes for directory tree
        // 为目录树加载子节点
        private async Task LoadDirectoryTreeChildren(TreeViewNode parentNode)
        {
            if (SSHFileExplorer == null) return;

            var parentItem = (FileItem)parentNode.Content;
            if (parentItem.Path != "/" && !SSHFileExplorer.DirectoryExists(parentItem.Path)) return;

            var directories = SSHFileExplorer.ListDirectory(parentItem.Path)
                .Where(f => f.IsDirectory && f.Name != "." && f.Name != "..")
                .OrderBy(f => f.Name);

            if (directories.Any())
            {
                // Mark as having children to enable expand icon
                // 标记为有子节点以启用展开图标
                parentNode.HasUnrealizedChildren = false;

                foreach (var dir in directories)
                {
                    var node = await CreateTreeNode(dir);
                    parentNode.Children.Add(node);
                }
            }
        }

        // Create tree node from directory
        // 从目录创建树节点
        private async Task<TreeViewNode> CreateTreeNode(ISftpFile file)
        {
            var item = new FileItem
            {
                Name = file.Name,
                Path = file.FullName,
                IsDirectory = file.IsDirectory
            };
            item.Icon = await IconHelper.GetSystemIconAsync(item.IsDirectory, item.Path);
            var node = new TreeViewNode { Content = item };

            return node;
        }

        // Handle tree view item click event
        // 处理树视图项点击事件
        private void DirectoryTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
        {
            // InvokedItem could be FileItem or TreeViewNode
            // InvokedItem 可能是 FileItem，也可能是 TreeViewNode
            var item = args.InvokedItem as FileItem ?? (args.InvokedItem as TreeViewNode)?.Content as FileItem;
            if (item != null && item.IsDirectory && !string.IsNullOrEmpty(item.Path))
            {
                LoadFileList(item.Path);
            }
        }

        // Update address bar with current path
        // 使用当前路径更新地址栏
        private void UpdateAddressBar(string path)
        {
            if (string.IsNullOrEmpty(path) || AddressBarTextBox == null || BreadcrumbPanel == null) return;
            AddressBarTextBox.Text = path;
            UpdateBreadcrumbBar(path);
        }

        // Update breadcrumb navigation bar
        // 更新面包屑导航栏
        private void UpdateBreadcrumbBar(string path)
        {
            if (BreadcrumbPanel == null) return;

            BreadcrumbPanel.Children.Clear();
            if (string.IsNullOrWhiteSpace(path))
            {
                path = "/";
            }
            path = path.Trim();
            var segments = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            string current = "/";
            var rootButton = new Button
            {
                Content = "/",
                Tag = "/",
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            rootButton.Click += (sender, e) =>
            {
                if (sender is Button btn && btn.Tag is string tag)
                {
                    LoadFileList(tag);
                }
            };
            BreadcrumbPanel.Children.Add(rootButton);

            foreach (var segment in segments)
            {
                if (!string.IsNullOrEmpty(segment))
                {
                    // Safely combine path to avoid null values
                    // 安全地组合路径，避免null值
                    if (string.IsNullOrEmpty(current))
                    {
                        current = segment;
                    }
                    else
                    {
                        current = $"{current}/{segment}";
                    }

                    var separator = new TextBlock
                    {
                        Text = ">",
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(8, 0, 8, 0)
                    };
                    BreadcrumbPanel.Children.Add(separator);

                    var button = new Button
                    {
                        Content = segment,
                        Tag = current,
                        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                        BorderThickness = new Thickness(0),
                        HorizontalAlignment = HorizontalAlignment.Left
                    };
                    button.Click += (sender, e) =>
                    {
                        if (sender is Button btn && btn.Tag is string tag)
                        {
                            LoadFileList(tag);
                        }
                    };
                    BreadcrumbPanel.Children.Add(button);
                }
            }
        }

        // Handle address bar tap event
        // 处理地址栏点击事件
        private void AddressBarBorder_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (AddressBarTextBox.Visibility == Visibility.Visible) return;
            if (e.OriginalSource is DependencyObject obj)
            {
                while (obj != null)
                {
                    if (obj == BreadcrumbPanel || obj is Button)
                    {
                        e.Handled = true;
                        return;
                    }
                    obj = VisualTreeHelper.GetParent(obj);
                }
            }
            AddressBarTextBox.Visibility = Visibility.Visible;
            BreadcrumbPanel.Visibility = Visibility.Collapsed;
            AddressBarTextBox.Focus(FocusState.Keyboard);
            AddressBarTextBox.SelectAll();
        }

        // Handle address bar text box key down event
        // 处理地址栏文本框按键按下事件
        private void AddressBarTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                if (SSHFileExplorer == null) return;
                string newPath = AddressBarTextBox.Text?.Trim();
                if (string.IsNullOrEmpty(newPath))
                {
                    LoadFileList("/");
                }
                else
                {
                    LoadFileList(newPath);
                }
                AddressBarTextBox.Visibility = Visibility.Collapsed;
                BreadcrumbPanel.Visibility = Visibility.Visible;
            }
        }

        // Handle double tap event on file list view
        // 处理文件列表视图双击事件
        private void FileListView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            var selectedItem = FileListView.SelectedItem as FileItem;
            if (selectedItem != null)
            {
                if (selectedItem.IsDirectory)
                {
                    LoadFileList(selectedItem.Path);
                }
            }
        }

        // Handle preview key down event on file list view
        // 处理文件列表视图预览按键事件
        private void FileListView_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Delete)
            {
                DeleteButton_Click(null, null);
            }
        }

        // Handle drag over event on directory tree
        // 处理目录树拖拽经过事件
        private void DirectoryTree_DragOver(object sender, DragEventArgs e)
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
        }

        // Handle drop event on directory tree
        // 处理目录树拖拽放置事件
        private async void DirectoryTree_Drop(object sender, DragEventArgs e)
        {
            if (SSHFileExplorer == null) return;

            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var items = await e.DataView.GetStorageItemsAsync();
                if (items.Count > 0)
                {
                    // Get target directory
                    // 获取目标目录
                    var targetNode = (TreeViewNode)DirectoryTree.SelectedNode;
                    if (targetNode != null)
                    {
                        var targetItem = (FileItem)targetNode.Content;
                        if (targetItem.IsDirectory)
                        {
                            foreach (var item in items)
                            {
                                try
                                {
                                    if (item.IsOfType(StorageItemTypes.File))
                                    {
                                        // Upload file to target directory
                                        // 上传文件到目标目录
                                        SSHFileExplorer.UploadFile(item.Path, Path.Combine(targetItem.Path, item.Name).Replace('\\', '/'));
                                    }
                                }
                                catch (Exception ex)
                                {
                                    var errorDialog = new ContentDialog
                                    {
                                        Title = "Upload failed",
                                        Content = $"Failed to upload {item.Name}: {ex.Message}",
                                        CloseButtonText = "OK",
                                        XamlRoot = this.Content.XamlRoot
                                    };
                                    await errorDialog.ShowAsync();
                                }
                            }

                            // Refresh the target directory
                            // 刷新目标目录
                            LoadFileList(targetItem.Path);
                        }
                    }
                }
            }
        }
    }
}