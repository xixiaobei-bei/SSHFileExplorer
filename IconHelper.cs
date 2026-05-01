using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Dispatching;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Drawing.Imaging;
// using Windows.UI.Core;
using System.Diagnostics;

namespace SSHFileExplorer
{
    public static class IconHelper
    {
        private static DispatcherQueue? dispatcherQueue;

        public static void Initialize(DispatcherQueue dispatcher)
        {
            dispatcherQueue = dispatcher;
        }

        // 获取系统图标
        public static async Task<BitmapImage?> GetSystemIconAsync(bool isDirectory, string? fileName)
        {
            try
            {
                // 使用系统API获取文件图标
                return await GetSystemIconFromAPI(isDirectory, fileName);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"获取系统图标失败: {ex.Message}");
            }

            // 如果获取系统图标失败，返回默认图标
            return GetDefaultIcon(isDirectory);
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
                        extension = Path.GetExtension(fileName).TrimStart('.');
                    }
                    catch
                    {
                        extension = "txt"; // 默认扩展名
                    }
                }

                // 创建临时文件来获取系统图标
                string tempPath = Path.GetTempPath();
                string tempFileName = isDirectory ? "temp_folder" : $"temp_file.{extension}";
                string fullPath = Path.Combine(tempPath, tempFileName);

                // 创建临时文件或目录来获取图标
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
                        using (var fs = File.Create(fullPath))
                        {
                            // 创建空文件
                        }
                    }
                }

                // 使用 StorageFile/StorageFolder API 获取缩略图
                if (isDirectory)
                {
                    var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(fullPath);
                    var thumbnail = await folder.GetThumbnailAsync(
                        Windows.Storage.FileProperties.ThumbnailMode.SingleItem,
                        32,
                        Windows.Storage.FileProperties.ThumbnailOptions.UseCurrentScale);

                    if (thumbnail != null)
                    {
                        var bitmap = new BitmapImage();
                        await bitmap.SetSourceAsync(thumbnail);
                        // 清理临时目录
                        try { Directory.Delete(fullPath); } catch { }
                        return bitmap;
                    }
                }
                else
                {
                    var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(fullPath);
                    var thumbnail = await file.GetThumbnailAsync(
                        Windows.Storage.FileProperties.ThumbnailMode.SingleItem,
                        32,
                        Windows.Storage.FileProperties.ThumbnailOptions.UseCurrentScale);

                    if (thumbnail != null)
                    {
                        var bitmap = new BitmapImage();
                        await bitmap.SetSourceAsync(thumbnail);
                        // 清理临时文件
                        try { File.Delete(fullPath); } catch { }
                        return bitmap;
                    }
                }

                // 清理临时文件或目录
                try
                {
                    if (isDirectory)
                        Directory.Delete(fullPath);
                    else
                        File.Delete(fullPath);
                }
                catch { }

                return null;
            }
            catch
            {
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
    }
}
