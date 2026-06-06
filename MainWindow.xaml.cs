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
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;
using Microsoft.UI.Text;

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

        private void Window_Activated(object sender, WindowActivatedEventArgs args)
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated)
            {
                TopCommandBarBorder.Background = new SolidColorBrush(Colors.White);
            }
            else
            {
                TopCommandBarBorder.Background = new SolidColorBrush(Color.FromArgb(255, 0, 120, 215));
            }
        }

        public MainWindow()
        {
            this.InitializeComponent();
            
            // Set initial welcome title with current system username
            // 设置初始欢迎标题为当前系统用户名
            if (WelcomeTitle != null)
            {
                string currentUsername = Environment.UserName;
                WelcomeTitle.Text = $"Hello！{currentUsername}";
            }
            
            // Initialize other components
            // 初始化其他组件
            pathOperationSemaphore = new SemaphoreSlim(1, 1);
            currentPath = "/";
            
            // Use standard Windows title bar to show title and icon automatically
            // 使用标准Windows标题栏以自动显示标题和图标
            this.AppWindow.TitleBar.ExtendsContentIntoTitleBar = false;
            
            // Initialize title bar colors
            InitializeTitleBarColors();
            
            // Subscribe to window activation events for color switching
            // 订阅窗口激活事件以实现颜色切换
            this.Activated += Window_Activated;
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
            
            // Ensure window is fully loaded and XamlRoot is set
            // 确保窗口已完全加载并设置了XamlRoot
            if (this.Content.XamlRoot != null)
            {
                dialog.XamlRoot = this.Content.XamlRoot;
            }
            else
            {
                // If XamlRoot is unavailable, use current window's XamlRoot
                // 如果XamlRoot不可用，使用当前窗口的XamlRoot
                var mainWindow = (Window.Current.Content as FrameworkElement)?.XamlRoot;
                if (mainWindow != null)
                {
                    dialog.XamlRoot = mainWindow;
                }
            }
            
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
                        Title = "错误",
                        Content = "主机和用户名不能为空！",
                        CloseButtonText = "确定",
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
                    if (WelcomeGrid != null)
                    {
                        WelcomeGrid.Visibility = Visibility.Collapsed;
                    }
                    // Show file browser panel
                    // 显示文件浏览器界面
                    if (MainGrid != null)
                    {
                        MainGrid.Visibility = Visibility.Visible;
                    }

                    // Load root directory
                    // 加载根目录
                    LoadFileList("/");

                    // Load directory tree
                    // 加载目录树
                    await LoadDirectoryTree();

                }
                catch (Exception ex)
                {
                    var errorDialog = new ContentDialog
                    {
                        Title = "连接失败",
                        Content = $"无法连接到SSH服务器：{ex.Message}",
                        CloseButtonText = "确定",
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
                        Title = "正在上传...",
                        Content = $"正在上传 {file.Name} 到 {currentPath}",
                        CloseButtonText = "取消",
                        XamlRoot = this.Content.XamlRoot
                    };

                    // Run upload in background thread
                    // 在后台线程运行上传
                    var uploadTask = Task.Run(() =>
                    {
                        var safeCurrentPath = currentPath ?? "/";
                        var safeFileName = file.Name ?? "";
                        var combinedPath = Path.Combine(safeCurrentPath, safeFileName).Replace('\\', '/');
                        SSHFileExplorer.UploadFile(file.Path, combinedPath);
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
                        Title = "上传失败",
                        Content = $"文件上传失败：{ex.Message}",
                        CloseButtonText = "确定",
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
                    Title = "未选择",
                    Content = "请选择要下载的文件。",
                    CloseButtonText = "确定",
                    XamlRoot = this.Content.XamlRoot
                };
                await errorDialog.ShowAsync();
                return;
            }

            if (selectedItem.IsDirectory)
            {
                var errorDialog = new ContentDialog
                {
                    Title = "无效选择",
                    Content = "无法下载目录。",
                    CloseButtonText = "确定",
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
                        Title = "正在下载...",
                        Content = $"正在下载 {selectedItem.Name} 到 {folder.Path}",
                        CloseButtonText = "取消",
                        XamlRoot = this.Content.XamlRoot
                    };

                    // Run download in background thread
                    // 在后台线程运行下载
                    var downloadTask = Task.Run(() =>
                    {
                        var safeLocalPath = localPath ?? Path.Combine(folder.Path, selectedItem.Name);
                        SSHFileExplorer.DownloadFile(selectedItem.Path, safeLocalPath);
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
                        Title = "下载失败",
                        Content = $"文件下载失败：{ex.Message}",
                        CloseButtonText = "确定",
                        XamlRoot = this.Content.XamlRoot
                    };
                    await errorDialog.ShowAsync();
                }
            }
        }

        private async void RenameButton_Click(object sender, RoutedEventArgs e)
        {
            if (SSHFileExplorer == null) return;

            var selectedItems = FileListView.SelectedItems;
            if (selectedItems.Count == 0)
            {
                var dialog = new ContentDialog
                {
                    Title = "提示",
                    Content = "请先选择要重命名的文件或文件夹",
                    CloseButtonText = "确定",
                    XamlRoot = this.Content.XamlRoot,
                    RequestedTheme = ElementTheme.Default
                };
                await dialog.ShowAsync();
                return;
            }

            foreach (FileItem item in selectedItems)
            {
                var textBox = new TextBox
                {
                    Text = item.Name,
                    PlaceholderText = "输入新名称",
                    Margin = new Thickness(0, 0, 0, 10)
                };
                
                var stackPanel = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = $"重命名: {item.Name}", Margin = new Thickness(0, 0, 0, 10) },
                        textBox
                    }
                };

                var renameDialog = new ContentDialog
                {
                    Title = "重命名",
                    Content = stackPanel,
                    PrimaryButtonText = "确定",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = this.Content.XamlRoot,
                    RequestedTheme = ElementTheme.Default
                };

                var result = await renameDialog.ShowAsync();
                if (result == ContentDialogResult.Primary)
                {
                    string newName = textBox.Text?.Trim();
                    
                    if (string.IsNullOrEmpty(newName) || newName == item.Name)
                    {
                        continue;
                    }
                    
                    string newFullName = System.IO.Path.Combine(currentPath, newName).Replace('\\', '/');
                    if (!newFullName.StartsWith("/"))
                    {
                        newFullName = "/" + newFullName;
                    }
                    newFullName = newFullName.Replace("//", "/");
                    
                    try
                    {
                        var attrs = SSHFileExplorer.sftpClient.GetAttributes(newFullName);
                        var dialogExists = new ContentDialog
                        {
                            Title = "名称冲突",
                            Content = $"名为 '{newName}' 的文件或文件夹已存在，无法重命名",
                            CloseButtonText = "确定",
                            XamlRoot = this.Content.XamlRoot,
                            RequestedTheme = ElementTheme.Default
                        };
                        await dialogExists.ShowAsync();
                        continue;
                    }
                    catch
                    {
                    }

                    try
                    {
                        string oldFullName = System.IO.Path.Combine(currentPath, item.Name).Replace('\\', '/');
                        if (!oldFullName.StartsWith("/"))
                        {
                            oldFullName = "/" + oldFullName;
                        }
                        oldFullName = oldFullName.Replace("//", "/");
                        SSHFileExplorer.sftpClient.RenameFile(oldFullName, newFullName);
                    }
                    catch (Exception ex)
                    {
                        var errorDialog = new ContentDialog
                        {
                            Title = "重命名失败",
                            Content = $"重命名 '{item.Name}' 为 '{newName}' 时发生错误:\n{ex.Message}",
                            CloseButtonText = "确定",
                            XamlRoot = this.Content.XamlRoot,
                            RequestedTheme = ElementTheme.Default
                        };
                        await errorDialog.ShowAsync();
                        continue;
                    }
                }
            }

            LoadFileList(currentPath);
        }

        // Create new folder on SSH server
        // 在SSH服务器上创建新文件夹
        private async void NewFolderButton_Click(object sender, RoutedEventArgs e)
        {
            if (SSHFileExplorer == null) return;

            var textBox = new TextBox
            {
                Text = "新建文件夹",
                PlaceholderText = "输入文件夹名称",
                Margin = new Thickness(0, 0, 0, 10)
            };
            
            var stackPanel = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = "请输入新文件夹的名称:", Margin = new Thickness(0, 0, 0, 10) },
                    textBox
                }
            };

            var newFolderDialog = new ContentDialog
            {
                Title = "新建文件夹",
                Content = stackPanel,
                PrimaryButtonText = "确定",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.Content.XamlRoot,
                RequestedTheme = ElementTheme.Default
            };

            var result = await newFolderDialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                string folderName = textBox.Text?.Trim();
                
                if (string.IsNullOrEmpty(folderName))
                {
                    var errorDialog = new ContentDialog
                    {
                        Title = "文件夹名称无效",
                        Content = "请输入有效的文件夹名称",
                        CloseButtonText = "确定",
                        XamlRoot = this.Content.XamlRoot,
                        RequestedTheme = ElementTheme.Default
                    };
                    await errorDialog.ShowAsync();
                    return;
                }
                
                string newFolderPath = System.IO.Path.Combine(currentPath, folderName).Replace('\\', '/');
                if (!newFolderPath.StartsWith("/"))
                {
                    newFolderPath = "/" + newFolderPath;
                }
                newFolderPath = newFolderPath.Replace("//", "/");
                
                try
                {
                    if (SSHFileExplorer.DirectoryExists(newFolderPath))
                    {
                        var existsDialog = new ContentDialog
                        {
                            Title = "文件夹已存在",
                            Content = $"名为 '{folderName}' 的文件夹已存在，无法创建",
                            CloseButtonText = "确定",
                            XamlRoot = this.Content.XamlRoot,
                            RequestedTheme = ElementTheme.Default
                        };
                        await existsDialog.ShowAsync();
                        return;
                    }
                    
                    SSHFileExplorer.CreateDirectory(newFolderPath);
                    LoadFileList(currentPath);
                }
                catch (Exception ex)
                {
                    var errorDialog = new ContentDialog
                    {
                        Title = "创建文件夹失败",
                        Content = $"创建文件夹 '{folderName}' 时发生错误:\n{ex.Message}",
                        CloseButtonText = "确定",
                        XamlRoot = this.Content.XamlRoot,
                        RequestedTheme = ElementTheme.Default
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

            // Get all selected items instead of just the first one
            // 获取所有选中的项目，而不仅仅是第一个
            var allSelectedItems = FileListView.SelectedItems.Cast<FileItem>().ToList();
            
            // Filter out ".." and "." folders to prevent accidental deletion of parent directory
            // 过滤掉".."和"."文件夹，防止误删上级目录
            var selectedItems = allSelectedItems
                .Where(item => item.Name != ".." && item.Name != ".")
                .ToList();
            
            if (selectedItems.Count == 0)
            {
                var errorDialog = new ContentDialog
                {
                    Title = "未选择",
                    Content = "请选择要删除的文件或目录。",
                    CloseButtonText = "确定",
                    XamlRoot = this.Content.XamlRoot
                };
                await errorDialog.ShowAsync();
                return;
            }

            // Confirm deletion for all selected files and directories
            // 确认删除所有选中的文件和目录
            var fileListText = string.Join("\n", selectedItems.Select(item => item.Name ?? "unknown"));
            
            // Create scrollable content for file list
            // 为文件列表创建可滚动的内容
            var scrollViewer = new ScrollViewer
            {
                MaxHeight = 200, // 限制最大高度，超出则显示滚动条
                VerticalScrollMode = ScrollMode.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollMode = ScrollMode.Disabled,
                Content = new TextBlock
                {
                    Text = fileListText,
                    TextWrapping = TextWrapping.NoWrap,
                    FontFamily = new FontFamily("Consolas, Courier New, monospace"),
                    FontSize = 12
                }
            };
            
            var confirmDialog = new ContentDialog
            {
                Title = "确认删除",
                Content = new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = $"确定要删除 {selectedItems.Count} 个项目吗？此操作无法撤销！",
                            TextWrapping = TextWrapping.Wrap
                        },
                        new TextBlock
                        {
                            Text = "以下项目将被删除：",
                            FontWeight = FontWeights.SemiBold
                        },
                        scrollViewer
                    }
                },
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.Content.XamlRoot
            };

            var result = await confirmDialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                try
                {
                    // Create progress dialog immediately with indeterminate progress for scanning
                    // 立即创建进度对话框，使用不确定进度条表示扫描状态
                    var progressTextBlock = new TextBlock
                    {
                        Text = "正在计算文件数量",
                        Margin = new Thickness(0, 0, 0, 8)
                    };
                    
                    var currentTaskTextBlock = new TextBlock
                    {
                        Text = "当前任务：正在计算文件数量",
                        Margin = new Thickness(0, 0, 0, 8)
                    };
                    
                    var progressBar = new ProgressBar
                    {
                        IsIndeterminate = true,
                        Height = 20,
                        Margin = new Thickness(0, 0, 0, 8),
                        HorizontalAlignment = HorizontalAlignment.Stretch
                    };
                    
                    var totalProgressTextBlock = new TextBlock
                    {
                        Text = "总进度：正在计算文件数量",
                        Margin = new Thickness(0, 0, 0, 0)
                    };
                    
                    var progressStackPanel = new StackPanel
                    {
                        Spacing = 4,
                        MinWidth = 500,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Children =
                        {
                            progressTextBlock,
                            currentTaskTextBlock,
                            progressBar,
                            totalProgressTextBlock
                        }
                    };
                    
                    var progressDialog = new ContentDialog
                    {
                        Title = "正在删除",
                        Content = progressStackPanel,
                        IsPrimaryButtonEnabled = false,
                        CloseButtonText = "取消",
                        XamlRoot = this.Content.XamlRoot,
                        // Set specific width for consistent appearance
                        // 设置特定宽度以保持一致的外观
                        Width = 600,
                        Height = 250
                    };
                    
                    // Show progress dialog immediately
                    // 立即显示进度对话框
                    var dialogTask = progressDialog.ShowAsync();
                    
                    // Create progress state object to track deletion progress
                    // 创建进度状态对象以跟踪删除进度
                    var progressState = new ProgressState
                    {
                        DeletedCount = 0,
                        TotalFiles = 0,
                        CancelRequested = false,
                        ProgressTextBlock = progressTextBlock,
                        CurrentTaskTextBlock = currentTaskTextBlock,
                        ProgressBar = progressBar,
                        TotalProgressTextBlock = totalProgressTextBlock
                    };
                    
                    // Handle cancel button click
                    // 处理取消按钮点击
                    bool userCancelled = false;
                    progressDialog.CloseButtonClick += (s, args) =>
                    {
                        progressState.CancelRequested = true;
                        userCancelled = true;
                        // Hide dialog immediately when user cancels
                        // 用户取消时立即隐藏对话框
                        progressDialog.Hide();
                    };
                    
                    // Start the actual work in background
                    // 在后台开始实际工作
                    var workTask = Task.Run(() =>
                    {
                        try
                        {
                            // Count total files to be deleted
                            // 计算要删除的总文件数
                            int totalFiles = CountTotalFilesForSelection(selectedItems);
                            
                            // Check if user cancelled during counting
                            // 检查用户是否在计数期间取消
                            if (progressState.CancelRequested)
                            {
                                // Close dialog when cancelled during counting
                                // 计算期间被取消时关闭对话框
                                DispatcherQueue.TryEnqueue(() => progressDialog.Hide());
                                return;
                            }
                            
                            // Update UI to show actual progress after counting
                            // 计数完成后更新UI显示实际进度
                            DispatcherQueue.TryEnqueue(() =>
                            {
                                progressState.TotalFiles = totalFiles;
                                progressTextBlock.Text = $"删除进度(0/{totalFiles})";
                                currentTaskTextBlock.Text = "当前任务：";
                                progressBar.IsIndeterminate = false;
                                progressBar.Maximum = totalFiles;
                                progressBar.Value = 0;
                                totalProgressTextBlock.Text = "总进度：0%";
                            });
                            
                            // Handle simple case (no directories or only empty directories)
                            // 处理简单情况（无目录或仅空目录）
                            if (totalFiles <= selectedItems.Count(x => !x.IsDirectory))
                            {
                                foreach (var item in selectedItems)
                                {
                                    if (progressState.CancelRequested) break;
                                    
                                    DispatcherQueue.TryEnqueue(() =>
                                    {
                                        currentTaskTextBlock.Text = $"当前任务：{item.Name}";
                                    });
                                    
                                    if (item.IsDirectory)
                                    {
                                        SSHFileExplorer.sftpClient.DeleteDirectory(item.Path);
                                    }
                                    else
                                    {
                                        SSHFileExplorer.DeleteFile(item.Path);
                                    }
                                    
                                    progressState.IncrementDeletedCount();
                                    
                                    DispatcherQueue.TryEnqueue(() =>
                                    {
                                        if (progressState.TotalFiles > 0)
                                        {
                                            progressTextBlock.Text = $"删除进度({progressState.DeletedCount}/{progressState.TotalFiles})";
                                            progressBar.Value = progressState.DeletedCount;
                                            double percentage = (double)progressState.DeletedCount / progressState.TotalFiles * 100;
                                            totalProgressTextBlock.Text = $"总进度：{percentage:F0}%";
                                        }
                                    });
                                }
                            }
                            else
                            {
                                // Delete items with progress tracking
                                // 带进度跟踪的删除项目
                                DeleteItemsWithProgress(selectedItems, progressState).Wait();
                            }
                            
                            // Work completed successfully, close dialog and refresh
                            // 工作成功完成，关闭对话框并刷新
                            DispatcherQueue.TryEnqueue(() =>
                            {
                                progressDialog.Hide();
                                LoadFileList(currentPath);
                            });
                        }
                        catch (Exception ex)
                        {
                            DispatcherQueue.TryEnqueue(async () =>
                            {
                                var errorDialog = new ContentDialog
                                {
                                    Title = "删除错误",
                                    Content = $"删除过程中发生错误: {ex.Message}",
                                    CloseButtonText = "确定",
                                    XamlRoot = this.Content.XamlRoot
                                };
                                await errorDialog.ShowAsync();
                                
                                // Also close the progress dialog on error
                                // 错误时也要关闭进度对话框
                                progressDialog.Hide();
                            });
                        }
                    });
                    
                    // Don't wait synchronously to avoid blocking UI thread
                    // 不要同步等待以避免阻塞UI线程
                    _ = workTask.ContinueWith(task =>
                    {
                        // This will run after workTask completes, regardless of success or failure
                        // 无论成功还是失败，工作完成后都会运行
                        if (task.IsFaulted && !userCancelled)
                        {
                            // If there was an exception and user didn't cancel, ensure dialog is closed
                            // 如果发生异常且用户未取消，则确保对话框关闭
                            DispatcherQueue.TryEnqueue(() => progressDialog.Hide());
                        }
                    });
                }
                catch (Exception ex)
                {
                    var errorDialog = new ContentDialog
                    {
                        Title = "删除错误",
                        Content = $"删除过程中发生错误: {ex.Message}",
                        CloseButtonText = "确定",
                        XamlRoot = this.Content.XamlRoot
                    };
                    await errorDialog.ShowAsync();
                }
            }
        }

        // Helper class to hold progress state
        // 辅助类用于持有进度状态
        private class ProgressState
        {
            // Fields for thread-safe operations
            // 用于线程安全操作的字段
            private int _deletedCount = 0;
            private int _totalFiles = 0;
            private bool _cancelRequested = false;
            
            // Lock object for thread-safe operations
            // 线程安全操作的锁对象
            private readonly object _lock = new object();
            
            public int DeletedCount 
            { 
                get { lock(_lock) { return _deletedCount; } }
                set { lock(_lock) { _deletedCount = value; } }
            }
            public int TotalFiles 
            { 
                get { lock(_lock) { return _totalFiles; } }
                set { lock(_lock) { _totalFiles = value; } }
            }
            public bool CancelRequested 
            { 
                get { lock(_lock) { return _cancelRequested; } }
                set { lock(_lock) { _cancelRequested = value; } }
            }
            public TextBlock ProgressTextBlock { get; set; }
            public TextBlock CurrentTaskTextBlock { get; set; }
            public ProgressBar ProgressBar { get; set; }
            public TextBlock TotalProgressTextBlock { get; set; }
            
            // Thread-safe method to increment deleted count
            // 线程安全地增加删除计数的方法
            public int IncrementDeletedCount()
            {
                lock (_lock)
                {
                    _deletedCount++;
                    return _deletedCount;
                }
            }
        }

        // Delete items with progress tracking
        // 带进度跟踪的删除项目方法
        private async Task DeleteItemsWithProgress(List<FileItem> selectedItems, ProgressState progressState)
        {
            foreach (var item in selectedItems)
            {
                if (progressState.CancelRequested)
                    break;
                    
                if (item.IsDirectory)
                {
                    // Delete directory recursively with progress tracking
                    // 递归删除目录并跟踪进度
                    DeleteDirectoryWithProgress(item.Path, progressState);
                }
                else
                {
                    // Delete single file
                    // 删除单个文件
                    SSHFileExplorer.DeleteFile(item.Path);
                    progressState.DeletedCount++;
                    
                    // Update UI on main thread
                    // 在主线程上更新UI
                    DispatcherQueue.TryEnqueue(() =>
                    {
                    if (progressState.TotalFiles > 0)
                    {
                        progressState.ProgressTextBlock.Text = $"删除进度({progressState.DeletedCount}/{progressState.TotalFiles})";
                        progressState.CurrentTaskTextBlock.Text = $"当前任务：{GetRelativePath(item.Path, currentPath)}";
                        progressState.ProgressBar.Value = progressState.DeletedCount;
                        double percentage = (double)progressState.DeletedCount / progressState.TotalFiles * 100;
                        progressState.TotalProgressTextBlock.Text = $"总进度：{percentage:F0}%";
                    }
                });
                }
            }
        }

        // Load file list from SSH server
        // 从SSH服务器加载文件列表
        private async void LoadFileList(string? path)
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
                        Title = "错误",
                        Content = $"目录不存在或无法访问：{ex.Message}",
                        CloseButtonText = "确定",
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
            if (DirectoryTree == null) return; // 添加空值检查

            try
            {
                DirectoryTree.RootNodes.Clear();

                // 直接加载根目录的子目录到根节点，而不是创建/节点
                // Load root directory children directly to root nodes
                var rootDirectories = SSHFileExplorer.ListDirectory("/")
                    .Where(f => f.IsDirectory && f.Name != "." && f.Name != "..")
                    .OrderBy(f => f.Name);

                foreach (var dir in rootDirectories)
                {
                    var node = await CreateTreeNode(dir);
                    if (node != null)
                    {
                        DirectoryTree.RootNodes.Add(node);
                    }
                }
            }
            catch (Exception ex)
            {
                var errorDialog = new ContentDialog
                {
                    Title = "错误",
                    Content = $"加载目录树失败：{ex.Message}",
                    CloseButtonText = "确定",
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
            // Check for null or empty path to prevent errors
            // 检查路径是否为null或空，以防止错误
            if (string.IsNullOrEmpty(parentItem.Path)) return;
            
            if (parentItem.Path != "/" && !SSHFileExplorer.DirectoryExists(parentItem.Path)) return;

            var directories = SSHFileExplorer.ListDirectory(parentItem.Path)
                .Where(f => f.IsDirectory && f.Name != "." && f.Name != "..")
                .OrderBy(f => f.Name);

            // Mark as no more unrealized children
            // 标记为没有更多未实现的子节点
            parentNode.HasUnrealizedChildren = false;
            
            if (directories.Any())
            {
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
            var safePath = file.FullName ?? string.Empty;
            item.Icon = await IconHelper.GetSystemIconAsync(item.IsDirectory, safePath);
            var node = new TreeViewNode 
            { 
                Content = item,
                // Set HasUnrealizedChildren to true to show expand arrow
                // 设置HasUnrealizedChildren为true以显示展开箭头
                HasUnrealizedChildren = true
            };

            return node;
        }

        // Handle tree view node expanding event
        // 处理树视图节点展开事件
        private async void DirectoryTree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
        {
            var node = args.Node;
            if (node == null || !node.HasUnrealizedChildren) return;
            
            // Load children when node is expanding
            // 节点展开时加载子节点
            await LoadDirectoryTreeChildren(node);
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
        private void UpdateAddressBar(string? path)
        {
            if (string.IsNullOrEmpty(path) || AddressBarTextBox == null || BreadcrumbPanel == null) return;
            AddressBarTextBox.Text = path!;
            UpdateBreadcrumbBar(path);
        }

        // Update breadcrumb navigation bar
        // 更新面包屑导航栏
        private void UpdateBreadcrumbBar(string? path)
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
                                        var safeTargetPath = targetItem.Path ?? "/";
                                        var combinedPath = Path.Combine(safeTargetPath, item.Name).Replace('\\', '/');
                                        SSHFileExplorer.UploadFile(item.Path, combinedPath);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    var errorDialog = new ContentDialog
                                    {
                                        Title = "上传失败",
                                        Content = $"上传 {item.Name} 失败：{ex.Message}",
                                        CloseButtonText = "确定",
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

        // Count total files in a directory (including subdirectories)
        // 计算目录中的总文件数（包括子目录）
        private int CountTotalFilesInDirectory(string remotePath)
        {
            if (SSHFileExplorer == null || string.IsNullOrEmpty(remotePath))
                return 0;

            int totalCount = 0;
            try
            {
                var items = SSHFileExplorer.sftpClient.ListDirectory(remotePath).ToList();
                foreach (var item in items)
                {
                    // Skip current directory (.) and parent directory (..)
                    // 跳过当前目录(.)和父目录(..)
                    if (item.Name == "." || item.Name == "..")
                        continue;
                        
                    var itemPath = $"{remotePath}/{item.Name}".Replace("//", "/");
                    
                    if (item.IsDirectory)
                    {
                        // Recursively count files in subdirectory
                        // 递归计算子目录中的文件数
                        totalCount += CountTotalFilesInDirectory(itemPath);
                    }
                    else
                    {
                        // Count file
                        // 计算文件
                        totalCount++;
                    }
                }
            }
            catch (Exception)
            {
                // Ignore errors during counting
                // 忽略计数过程中的错误
            }
            
            return totalCount;
        }

        // Count total files for all selected items
        // 计算所有选中项目的总文件数
        private int CountTotalFilesForSelection(List<FileItem> selectedItems)
        {
            int totalCount = 0;
            foreach (var item in selectedItems)
            {
                if (item.IsDirectory)
                {
                    totalCount += CountTotalFilesInDirectory(item.Path);
                }
                else
                {
                    totalCount++;
                }
            }
            return totalCount;
        }

        // Helper method to get relative path for display
        // 辅助方法获取用于显示的相对路径
        private string GetRelativePath(string fullPath, string basePath)
        {
            if (string.IsNullOrEmpty(fullPath))
                return "";
                
            if (string.IsNullOrEmpty(basePath) || !fullPath.StartsWith(basePath))
                return fullPath;
                
            var relativePath = fullPath.Substring(basePath.Length).TrimStart('/');
            return relativePath == "" ? fullPath : relativePath;
        }

        // Delete directory recursively with progress tracking
        // 递归删除目录并跟踪进度
        private void DeleteDirectoryWithProgress(string remotePath, ProgressState progressState)
        {
            if (string.IsNullOrEmpty(remotePath) || progressState.CancelRequested)
                return;

            try
            {
                var items = SSHFileExplorer.sftpClient.ListDirectory(remotePath).ToList();
                foreach (var item in items)
                {
                    if (progressState.CancelRequested)
                        break;
                        
                    // Skip current directory (.) and parent directory (..)
                    // 跳过当前目录(.)和父目录(..)
                    if (item.Name == "." || item.Name == "..")
                        continue;
                        
                    var itemPath = $"{remotePath}/{item.Name}".Replace("//", "/");
                    
                    if (item.IsDirectory)
                    {
                        // Recursively delete subdirectory
                        // 递归删除子目录
                        DeleteDirectoryWithProgress(itemPath, progressState);
                    }
                    else
                    {
                        // Delete file and update progress
                        // 删除文件并更新进度
                        SSHFileExplorer.DeleteFile(itemPath);
                        progressState.IncrementDeletedCount();
                        
                        // Update UI on main thread
                        // 在主线程上更新UI
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            progressState.ProgressTextBlock.Text = $"删除进度({progressState.DeletedCount}/{progressState.TotalFiles})";
                            progressState.CurrentTaskTextBlock.Text = $"当前任务：{GetRelativePath(itemPath, currentPath)}";
                            progressState.ProgressBar.Value = progressState.DeletedCount;
                            progressState.TotalProgressTextBlock.Text = $"总进度：{(int)((double)progressState.DeletedCount / progressState.TotalFiles * 100)}%";
                        });
                    }
                }
                
                // Finally delete the empty directory
                // 最后删除空目录
                if (!progressState.CancelRequested)
                {
                    SSHFileExplorer.sftpClient.DeleteDirectory(remotePath);
                }
            }
            catch (Exception)
            {
                // Ignore errors during deletion to continue with other items
                // 忽略删除过程中的错误以继续处理其他项目
            }
        }
    }
}