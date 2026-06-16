using System;
using System.Collections.Generic;
using System.Diagnostics;
using Windows.Foundation;
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

        // In-memory list of saved connections (decrypted at startup)
        // 内存中的已保存连接列表（启动时解密加载）
        private List<SavedConnection> _savedConnections = new();

        // Track the currently highlighted border during drag
        // 跟踪拖拽时当前高亮的Border
        private Border? _currentDragHighlightBorder;

        // Track the ListViewItem currently under the pointer (set by PointerEntered)
        // 跟踪当前指针下的ListViewItem（由PointerEntered设置）
        private ListViewItem? _currentHoverListViewItem;

        // True once credential manager initialization has finished
        // 凭据管理器初始化完成标记
        private bool _credentialInitDone = false;

        // 后台读取的已保存连接缓存（供多机制共享结果）
        // Cached saved connections read on background thread (shared across multiple mechanisms)
        private List<SavedConnection>? _cachedSavedConnections;

        // Whether a drag operation is currently in progress over the list
        // 当前列表上是否有拖拽操作
        private bool _isDraggingOver = false;

        // 高亮当前悬停项（如果正在拖拽中）
        // Highlight the currently hovered item (when a drag operation is in progress)
        private void HighlightCurrentHoverItem()
        {
            if (_currentHoverListViewItem == null) return;
            var border = FindDragHighlightBorder(_currentHoverListViewItem);
            if (border != null)
            {
                _currentDragHighlightBorder = border;
                border.Background = new SolidColorBrush(Color.FromArgb(0x25, 0x80, 0x80, 0x80));
            }
        }

        // 清除拖拽高亮
        // Clear drag highlight
        private void ClearDragHighlight()
        {
            if (_currentDragHighlightBorder != null)
            {
                _currentDragHighlightBorder.Background = new SolidColorBrush(Colors.Transparent);
                _currentDragHighlightBorder = null;
            }
        }

        // 每个ListViewItem创建时被调用 - 挂上PointerEntered/PointerExited以跟踪悬停项
        // Called when each ListViewItem is created - hook PointerEntered/PointerExited to track hovered items
        private void FileListView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            var container = args.ItemContainer as ListViewItem;
            if (container == null) return;
            if (args.Phase == 0)
            {
                container.PointerEntered -= FileListViewItem_PointerEntered;
                container.PointerExited -= FileListViewItem_PointerExited;
                container.PointerEntered += FileListViewItem_PointerEntered;
                container.PointerExited += FileListViewItem_PointerExited;
            }
        }

        // 指针进入某一项时被调用 - 记录当前悬停项，如果正在拖拽则立即高亮
        // Called when pointer enters an item - record current hovered item, highlight immediately if dragging
        private void FileListViewItem_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            ClearDragHighlight();
            _currentHoverListViewItem = sender as ListViewItem;
            if (_isDraggingOver) HighlightCurrentHoverItem();
        }

        // 指针离开某一项时被调用 - 清除高亮和悬停跟踪
        // Called when pointer leaves an item - clear highlight and hover tracking
        private void FileListViewItem_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (_currentHoverListViewItem == sender as ListViewItem)
            {
                ClearDragHighlight();
                _currentHoverListViewItem = null;
            }
        }

        // Add a lock to ensure sequential path operations
        // 添加一个锁来确保路径操作是顺序执行的
        private readonly SemaphoreSlim pathOperationSemaphore = new SemaphoreSlim(1, 1);

        // Window_Activated: 只处理窗口激活状态变化时的颜色切换（首次激活时 XamlRoot 可能还没就绪，不在这里初始化凭据）
        // Window_Activated: only handle color switching when window activation state changes
        // (XamlRoot may not be ready on first activation, do not init credentials here)
        private void Window_Activated(object sender, WindowActivatedEventArgs args)
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated)
            {
                if (TopCommandBarBorder != null)
                    TopCommandBarBorder.Background = new SolidColorBrush(Colors.White);
            }
            else
            {
                if (TopCommandBarBorder != null)
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
            
            // 订阅窗口激活事件：只用于颜色切换，不再用于凭据初始化
            // Subscribe to window activation events: for color switching only, credential init handled below.
            this.Activated += Window_Activated;

            // 启动时直接在后台初始化凭据并加载已保存连接（不依赖 XamlRoot）
            // Kick off credential init and saved-connections loading on a background thread at startup.
            // 注意：不依赖 XamlRoot 来判断是否可以开始，避免第一次激活时 XamlRoot 为 null 导致加载
            // 流程根本没启动（表现为一直"正在加载..."，切窗口后才显示）。
            // Note: do not rely on XamlRoot to decide when to start loading — during the first activation
            // XamlRoot may still be null, causing the load to never start (appears as "loading..." forever
            // until the user switches windows).
            _credentialInitDone = true;
            var dq = this.DispatcherQueue;

            _ = System.Threading.Tasks.Task.Run(() =>
            {
                bool autoKeyReady = false;
                bool needUserInteraction = false;
                try
                {
                    if (CredentialManager.HasDataFile())
                    {
                        string? autoKey = CredentialManager.TryGetAutoKeyFromVault();
                        autoKeyReady = !string.IsNullOrEmpty(autoKey);
                        if (!autoKeyReady) needUserInteraction = true;
                    }
                    else
                    {
                        needUserInteraction = true; // 首次启动，需要创建主密码
                    }
                }
                catch
                {
                    needUserInteraction = true;
                }

                if (needUserInteraction && dq != null)
                {
                    // 需要用户交互（弹对话框）时调度回 UI 线程
                    // Marshal back to UI thread when user interaction (dialog) is needed.
                    dq.TryEnqueue(async () =>
                    {
                        try
                        {
                            await InitializeCredentialManager(skipFlagCheck: true);
                            LoadSavedConnectionsToUI();
                        }
                        catch { /* 窗口已关闭 / Window has been closed. */ }
                    });
                }
                else if (dq != null)
                {
                    // 自动密钥可用：后台读取连接列表 → 回 UI 线程更新 ListView
                    // Auto-key available: load connection list on background thread → update ListView on UI thread.
                    List<SavedConnection>? list;
                    try
                    {
                        list = CredentialManager.LoadConnectionsForDisplay();
                    }
                    catch
                    {
                        list = new List<SavedConnection>();
                    }

                    dq.TryEnqueue(() =>
                    {
                        try
                        {
                            if (SavedConnectionsLoadingText != null)
                                SavedConnectionsLoadingText.Visibility = Visibility.Collapsed;
                            if (SavedConnectionsListView != null)
                                SavedConnectionsListView.ItemsSource = list;
                        }
                        catch { /* 窗口已关闭 / Window has been closed. */ }
                    });
                }
            });
        }

        // = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = =
        // Credential manager lifecycle
        // 凭据管理器生命周期
        // = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = =

        // Run the correct phase based on local state:
        // Phase 1 - No data file → show master password creation
        // Phase 2 - Data file + auto-key in vault → auto-decrypt
        // Phase 3 - Data file but vault key missing → ask for master password
        private async System.Threading.Tasks.Task InitializeCredentialManager(bool skipFlagCheck = false)
        {
            // 正常路径：如果已经初始化过就直接返回
            // Normal path: return immediately if already initialized.
            if (!skipFlagCheck && _credentialInitDone) return;
            if (!skipFlagCheck)
                _credentialInitDone = true;

            // Phase 2: fast path - auto-key exists and the file exists
            // 阶段二快捷路径：自动密钥存在
            if (CredentialManager.HasDataFile())
            {
                string? autoKey = CredentialManager.TryGetAutoKeyFromVault();
                if (!string.IsNullOrEmpty(autoKey))
                    return; // 已经能自动解密，无需弹窗

                // Phase 3: data file exists but auto-key missing
                // 用户可能换系统/清空凭据管理器
                // 阶段三：文件存在但自动密钥丢失
                // 弹提示用户输入主密码恢复
                await ShowMasterPasswordRecovery();
                return;
            }

            // Phase 1: first run - create master password
            // 阶段一：首次启动 → 创建主密码
            await ShowMasterPasswordCreation();
        }

        // 阶段一：让用户输入主密码并确认
        private async System.Threading.Tasks.Task ShowMasterPasswordCreation()
        {
            var pwGrid = new Grid { Margin = new Thickness(0, 8, 0, 8) };
            pwGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
            pwGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
            pwGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
            pwGrid.Children.Add(new TextBlock { Text = "请创建主密码（用于加密本地凭据）", Margin = new Thickness(0, 0, 0, 8) });
            var pwBox1 = new PasswordBox { PlaceholderText = "主密码", Margin = new Thickness(0, 4, 0, 4), Width = 260 };
            pwGrid.Children.Add(pwBox1);
            Grid.SetRow(pwBox1, 1);
            var pwBox2 = new PasswordBox { PlaceholderText = "再次输入", Margin = new Thickness(0, 4, 0, 4), Width = 260 };
            pwGrid.Children.Add(pwBox2);
            Grid.SetRow(pwBox2, 2);

            var dlg = new ContentDialog
            {
                Title = "创建主密码",
                Content = pwGrid,
                PrimaryButtonText = "确定",
                CloseButtonText = "取消",
                XamlRoot = this.Content.XamlRoot
            };

            while (true)
            {
                var result = await dlg.ShowAsync();
                if (result != ContentDialogResult.Primary) return;
                string p1 = pwBox1.Password;
                string p2 = pwBox2.Password;
                if (string.IsNullOrEmpty(p1) || p1 != p2)
                {
                    var err = new ContentDialog
                    {
                        Title = "错误",
                        Content = "两次输入的密码不一致或为空",
                        CloseButtonText = "确定",
                        XamlRoot = this.Content.XamlRoot
                    };
                    await err.ShowAsync();
                    continue;
                }
                try
                {
                    CredentialManager.InitializeWithMasterPassword(p1);
                    return;
                }
                catch (Exception ex)
                {
                    var err = new ContentDialog
                    {
                        Title = "初始化失败",
                        Content = ex.Message,
                        CloseButtonText = "确定",
                        XamlRoot = this.Content.XamlRoot
                    };
                    await err.ShowAsync();
                }
            }
        }

        // 阶段三：自动密钥丢失，输入主密码恢复
        private async System.Threading.Tasks.Task ShowMasterPasswordRecovery()
        {
            var pwGrid = new Grid { Margin = new Thickness(0, 8, 0, 8) };
            pwGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
            pwGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
            pwGrid.Children.Add(new TextBlock { Text = "检测到自动解锁密钥已丢失，请输入主密码恢复数据", TextWrapping = TextWrapping.WrapWholeWords, Margin = new Thickness(0, 0, 0, 8) });
            var pwBox = new PasswordBox { PlaceholderText = "主密码", Margin = new Thickness(0, 4, 0, 4), Width = 260 };
            pwGrid.Children.Add(pwBox);
            Grid.SetRow(pwBox, 1);

            var dlg = new ContentDialog
            {
                Title = "恢复已保存的连接",
                Content = pwGrid,
                PrimaryButtonText = "恢复",
                CloseButtonText = "取消",
                XamlRoot = this.Content.XamlRoot
            };

            while (true)
            {
                var result = await dlg.ShowAsync();
                if (result != ContentDialogResult.Primary) return;
                var list = CredentialManager.TryRecoverWithMasterPassword(pwBox.Password);
                if (list != null)
                    return; // 成功
                var err = new ContentDialog
                {
                    Title = "主密码错误",
                    Content = "主密码错误，请重试",
                    CloseButtonText = "确定",
                    XamlRoot = this.Content.XamlRoot
                };
                await err.ShowAsync();
            }
        }

        // 把已保存的连接加载到左侧栏 ListView
        // Load saved connections into the sidebar ListView.
        // 注意：不用 await 回 UI 线程 —— 用 DispatcherQueue 手动调度，
        // 避免 WinUI3 启动时 SynchronizationContext 没准备好导致续体卡住。
        // Note: do not use await to marshal back to the UI thread — use DispatcherQueue instead
        // to avoid getting stuck when the WinUI3 SynchronizationContext is not yet ready at startup.
        private void LoadSavedConnectionsToUI()
        {
            try
            {
                if (SavedConnectionsLoadingText != null)
                    SavedConnectionsLoadingText.Visibility = Visibility.Visible;
            }
            catch { }

            // 后台线程读取（PasswordVault 可能很慢）
            // 注意：不用 await 回 UI 线程——用 DispatcherQueue 手动调度，
            // 避免 WinUI 3 启动时 SynchronizationContext 没准备好导致 continuation 卡住
            var dq = this.DispatcherQueue;

            _ = System.Threading.Tasks.Task.Run(() =>
            {
                List<SavedConnection>? list;
                try
                {
                    list = CredentialManager.LoadConnectionsForDisplay();
                }
                catch
                {
                    list = new List<SavedConnection>();
                }

                // 手动调度回 UI 线程更新列表
                if (dq != null)
                {
                    dq.TryEnqueue(() =>
                    {
                        try
                        {
                            if (SavedConnectionsLoadingText != null)
                                SavedConnectionsLoadingText.Visibility = Visibility.Collapsed;
                            if (SavedConnectionsListView != null)
                                SavedConnectionsListView.ItemsSource = list;
                        }
                        catch { /* 窗口已关闭 */ }
                    });
                }
            });
        }

        // 用户点击已保存的连接项 → 用已保存凭据尝试连接
        // User tapped a saved connection item → try connecting with saved credentials
        private async void SavedConnectionItem_Tapped(object sender, TappedRoutedEventArgs e)
        {
            var fe = sender as FrameworkElement;
            var conn = fe?.DataContext as SavedConnection;
            if (conn == null) return;
            await ConnectWithSavedConnection(conn);
        }

        // 点击已保存连接项中的 "删除" 按钮
        // Click the "Delete" button in a saved connection item
        private void DeleteSavedConnection_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as FrameworkElement;
            var conn = btn?.DataContext as SavedConnection;
            if (conn == null) return;
            CredentialManager.DeleteConnection(conn);
            LoadSavedConnectionsToUI();
        }

        // SavedConnectionsListView_SelectionChanged (暂未用，只是占位)
        // SavedConnectionsListView_SelectionChanged (not used yet, placeholder only)
        private void SavedConnectionsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // SelectionChanged 只是为了让 ListView 可选择；真正的连接发生在 SavedConnectionItem_Tapped
            // SelectionChanged only serves to make ListView selectable; actual connection happens in SavedConnectionItem_Tapped
            // 如果用户点击删除按钮，SelectionChanged 会触发，没关系
            // If user clicks delete button, SelectionChanged will fire — that's fine
        }

        // 用已保存的连接直接连接 / Connect directly using saved connection credentials.
        // 密码不从列表对象中读取：按需从加密文件解密获取，连接后立即清理敏感数据。
        // Password is NOT read from the list object. It is decrypted on-demand from the encrypted file,
        // and all sensitive data is sanitized immediately after the SSH connection is established.
        private async System.Threading.Tasks.Task ConnectWithSavedConnection(SavedConnection conn)
        {
            if (conn == null) return;
            try
            {
                // ↓ 按需解密单个连接的密码（只有这一步把密码读到内存）
                // ↓ Decrypt the individual connection password on demand (the only place password enters memory)
                string? password = CredentialManager.GetConnectionPassword(conn.Host, conn.User, conn.Port, conn.DisplayName);
                if (string.IsNullOrEmpty(password))
                {
                    var err = new ContentDialog
                    {
                        Title = "连接失败",
                        Content = "无法读取已保存的密码 / Could not retrieve saved password",
                        CloseButtonText = "确定",
                        XamlRoot = this.Content.XamlRoot
                    };
                    await err.ShowAsync();
                    return;
                }

                SSHFileExplorer = new SSHFileExplorer(conn.Host, conn.User, password, conn.PrivateKeyPath, conn.Port);
                SSHFileExplorer.Connect();

                // ↓ 连接成功后立即清除密码字段（字符串无法被真正清零，但让 GC 回收引用）
                // ↓ Immediately clear the password after connection. C# strings are immutable
                //   and cannot be zeroed in place; dropping references allows the GC to reclaim them.
                password = null;
                CredentialManager.ClearConnectionPassword(conn);
                CredentialManager.ClearAutoKeyCache();

                if (WelcomeGrid != null) WelcomeGrid.Visibility = Visibility.Collapsed;
                if (MainGrid != null) MainGrid.Visibility = Visibility.Visible;
                currentPath = "/";
                LoadFileList("/");
                await LoadDirectoryTree();
            }
            catch (Exception ex)
            {
                CredentialManager.ClearAutoKeyCache();
                CredentialManager.ClearConnectionPassword(conn);
                var err = new ContentDialog
                {
                    Title = "连接失败",
                    Content = ex.Message,
                    CloseButtonText = "确定",
                    XamlRoot = this.Content.XamlRoot
                };
                await err.ShowAsync();
            }
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
                var displayName = dialog.DisplayName;
                var shouldSave = dialog.ShouldSave;

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

                    // 保存到加密文件（如果用户勾选）
                    // Save to encrypted file if user checked the option
                    if (shouldSave)
                    {
                        try
                        {
                            CredentialManager.SaveConnection(new SavedConnection
                            {
                                Host = host,
                                User = user,
                                Password = password,
                                PrivateKeyPath = privateKeyPath,
                                Port = port,
                                DisplayName = displayName
                            });
                            LoadSavedConnectionsToUI();
                        }
                        catch (Exception ex)
                        {
                            var warn = new ContentDialog
                            {
                                Title = "保存失败",
                                Content = ex.Message,
                                CloseButtonText = "确定",
                                XamlRoot = this.Content.XamlRoot
                            };
                            await warn.ShowAsync();
                        }
                    }
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

            // Show dialog to select upload type: files or folder
            // 显示对话框选择上传类型：文件或文件夹
            var selectDialog = new ContentDialog
            {
                Title = "选择上传类型",
                Content = "请选择要上传的内容类型：",
                PrimaryButtonText = "上传文件",
                SecondaryButtonText = "上传文件夹",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.Content.XamlRoot
            };

            var selectResult = await selectDialog.ShowAsync();
            if (selectResult == ContentDialogResult.Primary)
            {
                await UploadFilesAsync();
            }
            else if (selectResult == ContentDialogResult.Secondary)
            {
                await UploadFolderAsync();
            }
        }

        // Upload multiple files to server
        // 上传多个文件到服务器
        private async Task UploadFilesAsync()
        {
            var picker = new FileOpenPicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.ViewMode = PickerViewMode.Thumbnail;
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeFilter.Add("*");

            var files = await picker.PickMultipleFilesAsync();
            if (files != null && files.Count > 0)
            {
                try
                {
                    var explorer = SSHFileExplorer;
                    var safeCurrentPath = currentPath ?? "/";

                    // Step 1: 扫描冲突 — 检查远程是否已有同名文件
                    // Step 1: Scan for conflicts — check if files with the same names already exist remotely
                    var conflictNames = new List<string>();
                    var filePaths = new List<(string Path, string Name)>();
                    foreach (var f in files)
                    {
                        var safeName = f.Name ?? "";
                        filePaths.Add((f.Path, safeName));
                        var combinedPath = Path.Combine(safeCurrentPath, safeName).Replace('\\', '/');
                        try
                        {
                            if (explorer != null && explorer.FileExists(combinedPath))
                                conflictNames.Add(safeName);
                        }
                        catch { }
                    }

                    // Step 2: 有冲突则询问用户是否覆盖
                    // Step 2: If there are conflicts, ask the user whether to overwrite
                    HashSet<string> skipSet = new HashSet<string>();
                    if (conflictNames.Count > 0)
                    {
                        var result = await ShowConflictDialogAsync(safeCurrentPath, conflictNames, showSkipButton: true);

                        if (result == ContentDialogResult.Secondary)
                        {
                            foreach (var n in conflictNames) skipSet.Add(n);
                        }
                        else if (result != ContentDialogResult.Primary)
                        {
                            return;
                        }
                    }

                    // Step 3: 过滤掉要跳过的文件
                    // Step 3: Filter out files to skip
                    var uploadList = filePaths
                        .Where(fp => !skipSet.Contains(fp.Name))
                        .ToList();

                    if (uploadList.Count == 0) return;

                    // Show progress dialog
                    // 显示进度对话框
                    var progressDialog = new ContentDialog
                    {
                        Title = "正在上传...",
                        Content = $"正在上传 {uploadList.Count} 个文件到 {safeCurrentPath}",
                        CloseButtonText = "取消",
                        XamlRoot = this.Content.XamlRoot
                    };

                    // Create cancellation token source so Cancel button actually aborts the transfer
                    // 创建取消令牌源，点取消时真正中止传输
                    var cts = new CancellationTokenSource();
                    progressDialog.CloseButtonClick += (s, args) =>
                    {
                        cts.Cancel();
                        progressDialog.Hide();
                    };

                    // Run upload in background thread (with cancellation support)
                    // 在后台线程运行上传（支持取消）
                    var uploadTask = Task.Run(() =>
                    {
                        foreach (var file in uploadList)
                        {
                            cts.Token.ThrowIfCancellationRequested();
                            var combinedPath = Path.Combine(safeCurrentPath, file.Name).Replace('\\', '/');
                            if (explorer != null)
                                explorer.UploadFile(file.Path, combinedPath, cts.Token);
                        }
                    }, cts.Token);

                    // Show progress dialog and wait for upload to complete
                    // 显示进度对话框并等待上传完成
                    var dialogTask = progressDialog.ShowAsync();
                    try
                    {
                        await uploadTask;
                    }
                    catch (OperationCanceledException)
                    {
                        return; // 用户取消，静默退出
                    }

                    // Close dialog after upload completes
                    // 上传完成后关闭对话框
                    progressDialog.Hide();

                    // Refresh file list
                    // 刷新文件列表
                    LoadFileList(currentPath);
                }
                catch (OperationCanceledException)
                {
                    // User cancelled, swallow silently
                    // 用户已取消，静默处理
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

        // Upload folder to server recursively
        // 递归上传文件夹到服务器
        private async Task UploadFolderAsync()
        {
            var picker = new FolderPicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeFilter.Add("*");

            var folder = await picker.PickSingleFolderAsync();
            if (folder != null)
            {
                try
                {
                    var explorer = SSHFileExplorer;
                    var safeLocalFolderPath = folder.Path;
                    var safeFolderName = folder.Name ?? "";
                    var safeCurrentPath = currentPath ?? "/";
                    var combinedPath = Path.Combine(safeCurrentPath, safeFolderName).Replace('\\', '/');

                    // Step 1: 扫描冲突 — 枚举本地所有文件，检查远程对应位置是否已存在
                    // Step 1: Scan for conflicts — enumerate all local files, check if remote counterparts exist
                    var conflictFiles = new List<string>();
                    try
                    {
                        var localFiles = Directory.GetFiles(safeLocalFolderPath, "*", SearchOption.AllDirectories);
                        int rootLen = safeLocalFolderPath.Length;
                        foreach (var localFile in localFiles)
                        {
                            string relPath = localFile.Substring(rootLen).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                            string remoteFile = Path.Combine(combinedPath, relPath).Replace('\\', '/');
                            try
                            {
                                if (explorer != null && explorer.FileExists(remoteFile))
                                    conflictFiles.Add(relPath);
                            }
                            catch { }
                        }
                    }
                    catch { }

                    // Step 2: 有冲突则询问用户
                    // Step 2: Ask user if conflicts exist
                    HashSet<string>? skipFiles = null;
                    if (conflictFiles.Count > 0)
                    {
                        var result = await ShowConflictDialogAsync(combinedPath, conflictFiles, showSkipButton: true);

                        if (result == ContentDialogResult.Secondary)
                        {
                            // 跳过重名文件 / Skip conflict files
                            skipFiles = new HashSet<string>(conflictFiles);
                        }
                        else if (result != ContentDialogResult.Primary)
                        {
                            return; // 取消整个上传 / Cancel the entire upload
                        }
                        // Primary: 全部覆盖，直接上传即可（SftpClient.UploadFile 默认行为就是覆盖）
                    }

                    // Show progress dialog
                    // 显示进度对话框
                    var progressDialog = new ContentDialog
                    {
                        Title = "正在上传...",
                        Content = $"正在上传文件夹 {safeFolderName} 到 {safeCurrentPath}",
                        CloseButtonText = "取消",
                        XamlRoot = this.Content.XamlRoot
                    };

                    // Create cancellation token source so Cancel button actually aborts the transfer
                    // 创建取消令牌源，点取消时真正中止传输
                    var cts = new CancellationTokenSource();
                    progressDialog.CloseButtonClick += (s, args) =>
                    {
                        cts.Cancel();
                        progressDialog.Hide();
                    };

                    // Run upload in background thread (with cancellation support)
                    // 在后台线程运行上传（支持取消）
                    // skipFiles 不为空：手动扫描并跳过冲突文件；为空：直接 UploadFolder 覆盖
                    var uploadTask = Task.Run(() =>
                    {
                        if (explorer == null) return;
                        if (skipFiles == null)
                        {
                            // 全部覆盖，直接上传让 SFTP 覆盖
                            explorer.UploadFolder(safeLocalFolderPath, combinedPath, cts.Token);
                        }
                        else
                        {
                            // 跳过重名文件：手动扫描所有文件，跳过冲突项
                            explorer.CreateDirectory(combinedPath);
                            var localFiles = Directory.GetFiles(safeLocalFolderPath, "*", SearchOption.AllDirectories);
                            int rootLen = safeLocalFolderPath.Length;
                            foreach (var localFile in localFiles)
                            {
                                cts.Token.ThrowIfCancellationRequested();
                                string relPath = localFile.Substring(rootLen).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                                if (skipFiles.Contains(relPath)) continue;
                                string remoteFile = Path.Combine(combinedPath, relPath).Replace('\\', '/');
                                string? parentDir = Path.GetDirectoryName(remoteFile)?.Replace('\\', '/');
                                if (!string.IsNullOrEmpty(parentDir) && parentDir != combinedPath)
                                    explorer.CreateDirectory(parentDir);
                                explorer.UploadFile(localFile, remoteFile, cts.Token);
                            }
                        }
                    }, cts.Token);

                    // Show progress dialog and wait for upload to complete
                    // 显示进度对话框并等待上传完成
                    var dialogTask = progressDialog.ShowAsync();
                    try
                    {
                        await uploadTask;
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    // Close dialog after upload completes
                    // 上传完成后关闭对话框
                    progressDialog.Hide();

                    // Refresh file list
                    // 刷新文件列表
                    LoadFileList(currentPath);
                }
                catch (OperationCanceledException)
                {
                    // User cancelled, swallow silently
                    // 用户已取消，静默处理
                }
                catch (Exception ex)
                {
                    var errorDialog = new ContentDialog
                    {
                        Title = "上传失败",
                        Content = $"文件夹上传失败：{ex.Message}",
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

                    // Create cancellation token source so Cancel button actually aborts the transfer
                    // 创建取消令牌源，点取消时真正中止传输
                    var cts = new CancellationTokenSource();
                    progressDialog.CloseButtonClick += (s, args) =>
                    {
                        cts.Cancel();
                        progressDialog.Hide();
                    };

                    // Capture values on UI thread before Task.Run
                    // 在Task.Run之前于UI线程捕获值
                    var explorer = SSHFileExplorer;
                    var safeLocalPath = localPath ?? Path.Combine(folder.Path, selectedItem.Name);
                    var remoteItemPath = selectedItem.Path;

                    // Run download in background thread (with cancellation support)
                    // 在后台线程运行下载（支持取消）
                    var downloadTask = Task.Run(() =>
                    {
                        explorer.DownloadFile(remoteItemPath, safeLocalPath, cts.Token);
                    }, cts.Token);

                    // Show progress dialog and wait for download to complete
                    // 显示进度对话框并等待下载完成
                    var dialogTask = progressDialog.ShowAsync();
                    try
                    {
                        await downloadTask;
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    // Close dialog after download completes
                    // 下载完成后关闭对话框
                    progressDialog.Hide();
                }
                catch (OperationCanceledException)
                {
                    // User cancelled, swallow silently
                    // 用户已取消，静默处理
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

        // 重命名按钮点击事件 - 重命名选中的文件或文件夹
        // Rename button click event - rename selected files or folders
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



        // Handle drag over event - highlight the item under pointer with a subtle gray effect
        // 处理拖拽经过事件 - 用柔和的灰色高亮鼠标指针下的项
        private void FileListView_DragOver(object sender, DragEventArgs e)
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            _isDraggingOver = true;

            // PointerEntered is suppressed during external drag-drop - use manual container hit-test
            // 拖外部文件时 PointerEntered 被抑制，手动遍历容器做命中测试
            var point = e.GetPosition(FileListView);
            ListViewItem? itemAtPoint = null;
            int count = FileListView.Items.Count;

            for (int i = 0; i < count; i++)
            {
                var container = FileListView.ContainerFromIndex(i) as ListViewItem;
                if (container == null) continue;
                try
                {
                    var origin = container.TransformToVisual(FileListView).TransformPoint(new Point(0, 0));
                    if (point.X >= origin.X && point.X < origin.X + container.ActualWidth &&
                        point.Y >= origin.Y && point.Y < origin.Y + container.ActualHeight)
                    {
                        itemAtPoint = container;
                        break;
                    }
                }
                catch { }
            }

            // Fallback: visual tree enumeration if ContainerFromIndex returned nothing (virtualization)
            // 回退：如果 ContainerFromIndex 因为虚拟化返回空，枚举可视化树
            if (itemAtPoint == null)
            {
                static void Collect(DependencyObject parent, List<ListViewItem> results)
                {
                    for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                    {
                        var child = VisualTreeHelper.GetChild(parent, i);
                        if (child is ListViewItem lvi) results.Add(lvi);
                        Collect(child, results);
                    }
                }
                var allContainers = new List<ListViewItem>();
                Collect(FileListView, allContainers);
                foreach (var lvi in allContainers)
                {
                    try
                    {
                        var origin = lvi.TransformToVisual(FileListView).TransformPoint(new Point(0, 0));
                        if (point.X >= origin.X && point.X < origin.X + lvi.ActualWidth &&
                            point.Y >= origin.Y && point.Y < origin.Y + lvi.ActualHeight)
                        {
                            itemAtPoint = lvi;
                            break;
                        }
                    }
                    catch { }
                }
            }

            // Clear highlight if the hovered item changed
            // 鼠标下的项变了就清除旧高亮
            if (_currentHoverListViewItem != itemAtPoint)
            {
                ClearDragHighlight();
                _currentHoverListViewItem = itemAtPoint;
            }

            // Apply highlight to the item currently under the pointer
            // 高亮当前鼠标下的项
            if (_currentDragHighlightBorder == null && itemAtPoint != null)
            {
                var border = FindDragHighlightBorder(itemAtPoint);
                if (border != null)
                {
                    _currentDragHighlightBorder = border;
                    border.Background = new SolidColorBrush(Color.FromArgb(0x25, 0x80, 0x80, 0x80));
                }
            }
        }

        // Recursive helper to find the DragHighlightBorder inside a container
        // 在容器中递归查找DragHighlightBorder
        private static Border? FindDragHighlightBorder(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is Border border)
                {
                    return border;
                }
                var found = FindDragHighlightBorder(child);
                if (found != null) return found;
            }
            return null;
        }

        // Handle drag leave event - clear highlight
        // 处理拖拽离开事件 - 清除高亮
        private void FileListView_DragLeave(object sender, DragEventArgs e)
        {
            _isDraggingOver = false;
            ClearDragHighlight();
        }

        // Show conflict dialog with unified buttons, returns user choice
        // 显示统一按钮布局的冲突对话框，返回用户选择
        // Returns: Primary=覆盖全部, Secondary=跳过重名文件, Close/None=取消
        private async Task<ContentDialogResult> ShowConflictDialogAsync(
            string targetPath,
            List<string> conflictNames,
            bool showSkipButton = true)
        {
            if (conflictNames.Count == 0) return ContentDialogResult.Primary;

            string conflictList = string.Join(", ", conflictNames.Take(5));
            if (conflictNames.Count > 5) conflictList += ", ...";

            var dialog = new ContentDialog
            {
                Title = "文件已存在",
                Content = $"在 {targetPath} 下有 {conflictNames.Count} 个同名文件：{conflictList}\n\n是否覆盖？",
                PrimaryButtonText = "全部覆盖",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.Content.XamlRoot
            };
            if (showSkipButton)
                dialog.SecondaryButtonText = "跳过重名文件";

            return await dialog.ShowAsync();
        }

        // Handle drop event on file list view
        // 处理文件列表拖拽放置事件
        private async void FileListView_Drop(object sender, DragEventArgs e)
        {
            // Clear highlight when dropping
            // 放置时清除高亮
            _isDraggingOver = false;
            ClearDragHighlight();

            if (SSHFileExplorer == null) return;

            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var items = await e.DataView.GetStorageItemsAsync();
                if (items.Count > 0)
                {
                    // Determine target directory - reuse the item tracked by DragOver
                    // (FindElementsInHostCoordinates is unreliable during external drag-drop)
                    // 确定目标目录 - 复用 DragOver 已经检测到的项（拖外部文件时 FindElementsInHostCoordinates 不可靠）
                    string targetPath = currentPath ?? "/";

                    var container = _currentHoverListViewItem;
                    if (container != null && container.Content is FileItem fi && fi.IsDirectory)
                    {
                        targetPath = fi.Path ?? "/";
                    }

                    var uploadList = new List<(bool IsFolder, string Name, string Path)>();
                    foreach (var item in items)
                    {
                        try
                        {
                            if (item.IsOfType(StorageItemTypes.File))
                            {
                                uploadList.Add((false, item.Name ?? "", item.Path));
                            }
                            else if (item.IsOfType(StorageItemTypes.Folder))
                            {
                                uploadList.Add((true, item.Name ?? "", item.Path));
                            }
                        }
                        catch { }
                    }

                    if (uploadList.Count == 0) return;

                    // Defer to next dispatcher cycle - the drag-drop manager sends follow-up
                    // messages that would otherwise be interpreted as "close" by ContentDialog
                    // 推迟到下一消息循环，否则拖放管理器的后续消息会被ContentDialog当成"取消"
                    var explorerForUpload = SSHFileExplorer;
                    var capturedTargetPath = targetPath ?? "/";
                    var capturedUploadList = uploadList.ToList();
                    var capturedCurrentPath = currentPath ?? "/";
                    var xamlRoot = this.Content.XamlRoot;

                    // 推迟到下一消息循环，否则拖放管理器的后续消息会被 ContentDialog 当成"取消"
                    // Defer to next dispatcher cycle — the drag-drop manager sends follow-up
                    // messages that would otherwise be interpreted as "close" by ContentDialog
                    DispatcherQueue.TryEnqueue(async () =>
                    {
                        try
                        {
                            // Step 1: 扫描冲突 — 检查远程是否已有同名文件
                            // Step 1: Scan for conflicts — check if files with same names already exist remotely
                            var conflictNames = new List<string>();
                            try
                            {
                                foreach (var item in capturedUploadList)
                                {
                                    if (item.IsFolder)
                                    {
                                        // 文件夹：递归扫描内部所有文件
                                        // Folder: recursively scan all inner files
                                        try
                                        {
                                            var innerFiles = Directory.GetFiles(item.Path, "*", SearchOption.AllDirectories);
                                            int rootLen = item.Path.Length;
                                            foreach (var innerFile in innerFiles)
                                            {
                                                string relPath = innerFile.Substring(rootLen).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                                                string remoteFile = Path.Combine(capturedTargetPath, item.Name, relPath).Replace('\\', '/');
                                                if (explorerForUpload != null && explorerForUpload.FileExists(remoteFile))
                                                {
                                                    conflictNames.Add(Path.Combine(item.Name, relPath));
                                                    if (conflictNames.Count >= 20) break;
                                                }
                                            }
                                        }
                                        catch { }
                                    }
                                    else
                                    {
                                        // 单个文件：直接检查目标位置
                                        // Single file: check directly at target location
                                        string remoteFile = Path.Combine(capturedTargetPath, item.Name).Replace('\\', '/');
                                        if (explorerForUpload != null && explorerForUpload.FileExists(remoteFile))
                                            conflictNames.Add(item.Name);
                                    }
                                    if (conflictNames.Count >= 20) break;
                                }
                            }
                            catch { }

                            // Step 2: 有冲突则询问用户是否覆盖
                            // Step 2: If conflicts exist, ask user whether to overwrite
                            HashSet<string>? conflictSet = null;
                            if (conflictNames.Count > 0)
                            {
                                var conflictResult = await ShowConflictDialogAsync(capturedTargetPath, conflictNames, showSkipButton: true);
                                if (conflictResult == ContentDialogResult.Secondary)
                                    conflictSet = new HashSet<string>(conflictNames);
                                else if (conflictResult != ContentDialogResult.Primary)
                                    return; // 取消上传 / Cancel the upload
                            }

                            // Step 3: 执行上传（根据选项可能跳过冲突项）
                            // Step 3: Perform the actual upload (may skip conflict items based on user choice)
                            var progressDialog = new ContentDialog
                            {
                                Title = "正在上传...",
                                Content = $"正在上传 {capturedUploadList.Count} 个项目到 {capturedTargetPath}",
                                CloseButtonText = "取消",
                                XamlRoot = xamlRoot
                            };

                            var cts = new CancellationTokenSource();
                            progressDialog.CloseButtonClick += (s, args) => cts.Cancel();

                            var uploadTask = Task.Run(() =>
                            {
                                foreach (var uploadItem in capturedUploadList)
                                {
                                    cts.Token.ThrowIfCancellationRequested();

                                    // 如果选择了"跳过重名文件"，用已扫描的 conflictSet 判断是否跳过
                                    // 对文件夹：检查 conflictSet 中是否有任何项属于此文件夹（前缀匹配）
                                    // 对文件：直接检查是否在冲突集合中
                                    if (conflictSet != null)
                                    {
                                        bool shouldSkip = false;
                                        if (uploadItem.IsFolder)
                                        {
                                            string prefixWin = uploadItem.Name + "\\";
                                            string prefixNix = uploadItem.Name + "/";
                                            foreach (var c in conflictSet)
                                            {
                                                if (c.StartsWith(prefixWin) || c.StartsWith(prefixNix) || c == uploadItem.Name)
                                                { shouldSkip = true; break; }
                                            }
                                        }
                                        else
                                        {
                                            shouldSkip = conflictSet.Contains(uploadItem.Name);
                                        }
                                        if (shouldSkip) continue;
                                    }

                                    try
                                    {
                                        var combinedPath = Path.Combine(capturedTargetPath, uploadItem.Name).Replace('\\', '/');
                                        if (uploadItem.IsFolder)
                                        {
                                            explorerForUpload.UploadFolder(uploadItem.Path, combinedPath, cts.Token);
                                        }
                                        else
                                        {
                                            explorerForUpload.UploadFile(uploadItem.Path, combinedPath, cts.Token);
                                        }
                                    }
                                    catch (OperationCanceledException) { throw; }
                                    catch (Exception ex) { Debug.WriteLine($"上传 {uploadItem.Name} 失败: {ex.Message}"); }
                                }
                            }, cts.Token);

                            var dialogTask = progressDialog.ShowAsync();
                            try { await uploadTask; } catch (OperationCanceledException) { }

                            progressDialog.Hide();
                            LoadFileList(capturedCurrentPath);
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception ex)
                        {
                            var errorDialog = new ContentDialog
                            {
                                Title = "上传失败",
                                Content = $"文件上传失败：{ex.Message}",
                                CloseButtonText = "确定",
                                XamlRoot = xamlRoot
                            };
                            await errorDialog.ShowAsync();
                        }
                    });
                }
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