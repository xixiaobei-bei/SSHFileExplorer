using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Dispatching;
using System.IO;
using System.Diagnostics;
using Windows.Storage;
using Windows.Storage.Streams;
using System.Runtime.InteropServices.WindowsRuntime;

namespace SSHFileExplorer
{
    public static class IconHelper
    {
        private static DispatcherQueue? dispatcherQueue;
        
        // 图标缓存，参考NanaZip的CExtToIconMap
        private static readonly ConcurrentDictionary<string, BitmapImage> _iconCache = new ConcurrentDictionary<string, BitmapImage>();
        
        // 目录图标的缓存键
        private const string DirectoryCacheKey = "__DIRECTORY__";

        public static void Initialize(DispatcherQueue dispatcher)
        {
            dispatcherQueue = dispatcher;
        }

        // 获取系统图标，带缓存机制
        public static async Task<BitmapImage?> GetSystemIconAsync(bool isDirectory, string? fileName)
        {
            try
            {
                string cacheKey = isDirectory ? DirectoryCacheKey : GetExtensionCacheKey(fileName);
                
                // 先检查缓存
                if (_iconCache.TryGetValue(cacheKey, out var cachedIcon))
                {
                    return cachedIcon;
                }

                // 获取新图标
                var icon = await GetSystemIconFromAPI(isDirectory, fileName);
                if (icon != null)
                {
                    // 缓存图标
                    _iconCache[cacheKey] = icon;
                }
                return icon;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"获取系统图标失败: {ex.Message}");
            }

            // 如果获取系统图标失败，返回默认图标
            return GetDefaultIcon(isDirectory);
        }

        // 获取扩展名缓存键
        private static string GetExtensionCacheKey(string? fileName)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                return "__DEFAULT_FILE__";
            }
            
            try
            {
                var extension = Path.GetExtension(fileName).ToLowerInvariant();
                if (string.IsNullOrEmpty(extension))
                {
                    return "__NO_EXTENSION__";
                }
                
                // 处理类似 .001, .002 这类扩展名，统一使用一个缓存键，参考NanaZip
                if (extension.Length > 1 && int.TryParse(extension.Substring(1), out _))
                {
                    return "__NUMBERED_EXT__";
                }
                
                return extension;
            }
            catch
            {
                return "__DEFAULT_FILE__";
            }
        }

        private static async Task<BitmapImage?> GetSystemIconFromAPI(bool isDirectory, string? fileName)
        {
            try
            {
                string extension = "";
                if (!isDirectory && !string.IsNullOrEmpty(fileName))
                {
                    try
                    {
                        extension = Path.GetExtension(fileName).ToLowerInvariant();
                    }
                    catch
                    {
                        extension = "";
                    }
                }

                // 使用预初始化的临时文件路径，对于无扩展名文件也创建无扩展名的临时文件
                string fullPath;
                if (isDirectory)
                {
                    fullPath = Path.Combine(Path.GetTempPath(), "_SSHExplorer_Dir_");
                }
                else
                {
                    // 确保临时文件有正确的扩展名，对于无扩展名文件，就创建无扩展名的文件
                    if (string.IsNullOrEmpty(extension))
                    {
                        fullPath = Path.Combine(Path.GetTempPath(), "_SSHExplorer_NoExtension");
                    }
                    else
                    {
                        fullPath = Path.Combine(Path.GetTempPath(), $"_SSHExplorer_Icon{extension}");
                    }
                }

                Debug.WriteLine($"获取图标: {(isDirectory ? "目录" : "文件")}, 扩展名: {extension}, 路径: {fullPath}");

                // 创建临时文件或目录来获取图标（只创建一次）
                if (isDirectory)
                {
                    if (!Directory.Exists(fullPath))
                    {
                        Directory.CreateDirectory(fullPath);
                    }
                }
                else
                {
                    if (!File.Exists(fullPath))
                    {
                        // 创建一个小的临时文件，而不是完全空的
                        using (var fs = File.Create(fullPath))
                        {
                            byte[] dummyData = new byte[10]; // 写一些数据
                            fs.Write(dummyData, 0, dummyData.Length);
                        }
                    }
                }

                // 使用 StorageFile/StorageFolder API 获取缩略图
                if (isDirectory)
                {
                    var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(fullPath);
                    // 尝试多种缩略图模式获取文件夹图标
                    var thumbnail = await folder.GetThumbnailAsync(
                        Windows.Storage.FileProperties.ThumbnailMode.ListView,
                        32,
                        Windows.Storage.FileProperties.ThumbnailOptions.UseCurrentScale);

                    if (thumbnail == null)
                    {
                        thumbnail = await folder.GetThumbnailAsync(
                            Windows.Storage.FileProperties.ThumbnailMode.SingleItem,
                            32,
                            Windows.Storage.FileProperties.ThumbnailOptions.ResizeThumbnail);
                    }

                    if (thumbnail != null)
                    {
                        var bitmap = new BitmapImage();
                        await bitmap.SetSourceAsync(thumbnail);
                        return bitmap;
                    }
                }
                else
                {
                    var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(fullPath);
                    // 尝试多种缩略图模式获取文件图标
                    var thumbnail = await file.GetThumbnailAsync(
                        Windows.Storage.FileProperties.ThumbnailMode.ListView,
                        32,
                        Windows.Storage.FileProperties.ThumbnailOptions.UseCurrentScale);

                    if (thumbnail == null)
                    {
                        thumbnail = await file.GetThumbnailAsync(
                            Windows.Storage.FileProperties.ThumbnailMode.SingleItem,
                            32,
                            Windows.Storage.FileProperties.ThumbnailOptions.ResizeThumbnail);
                    }

                    if (thumbnail == null)
                    {
                        thumbnail = await file.GetThumbnailAsync(
                            Windows.Storage.FileProperties.ThumbnailMode.DocumentsView,
                            32,
                            Windows.Storage.FileProperties.ThumbnailOptions.ResizeThumbnail);
                    }

                    if (thumbnail != null)
                    {
                        var bitmap = new BitmapImage();
                        await bitmap.SetSourceAsync(thumbnail);
                        return bitmap;
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"从API获取图标失败: {ex.Message}");
                return null;
            }
        }

        private static BitmapImage GetDefaultIcon(bool isDirectory)
        {
            try
            {
                if (isDirectory)
                {
                    return new BitmapImage(new Uri("ms-appx:///Assets/Square44x44Logo.scale-200.png"));
                }
                else
                {
                    return new BitmapImage(new Uri("ms-appx:///Assets/Square44x44Logo.scale-200.png"));
                }
            }
            catch
            {
                // 如果加载默认图标也失败，创建一个空的BitmapImage
                return new BitmapImage();
            }
        }

        // 更新方法以使用系统图标
        public static async Task<BitmapImage?> GetIconBitmapAsync(bool isDirectory, string fileName)
        {
            return await GetSystemIconAsync(isDirectory, fileName);
        }
        
        // 清空图标缓存（可选）
        public static void ClearCache()
        {
            _iconCache.Clear();
        }
    }
}