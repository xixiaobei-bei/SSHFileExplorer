using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace SSHFileExplorer
{
    // SSH/SFTP client wrapper class
    // SSH/SFTP客户端包装类
    public class SSHFileExplorer
    {
        private readonly SshClient sshClient;
        public readonly SftpClient sftpClient;

        // Constructor to initialize SSH/SFTP clients
        // 构造函数，初始化SSH/SFTP客户端
        public SSHFileExplorer(string host, string username, string? password = null, string? privateKeyPath = null, int port = 22)
        {
            if (!string.IsNullOrEmpty(privateKeyPath))
            {
                var keyFile = new PrivateKeyFile(privateKeyPath);
                var keyFiles = new[] { keyFile };
                sshClient = new SshClient(host, port, username, keyFiles);
                sftpClient = new SftpClient(host, port, username, keyFiles);
            }
            else if (!string.IsNullOrEmpty(password))
            {
                sshClient = new SshClient(host, port, username, password);
                sftpClient = new SftpClient(host, port, username, password);
            }
            else
            {
                throw new ArgumentException("Either password or private key path must be provided.");
            }
        }

        // Connect to SSH/SFTP server
        // 连接到SSH/SFTP服务器
        public void Connect()
        {
            sshClient.Connect();
            sftpClient.Connect();
        }

        // Disconnect from SSH/SFTP server
        // 断开与SSH/SFTP服务器的连接
        public void Disconnect()
        {
            sshClient.Disconnect();
            sftpClient.Disconnect();
        }

        // List directory contents
        // 列出目录内容
        public IEnumerable<SftpFile> ListDirectory(string? path)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException($"'{nameof(path)}' cannot be null or empty", nameof(path));
                
            return sftpClient.ListDirectory(path).Cast<SftpFile>();
        }

        // Upload local file to remote server
        // 上传本地文件到远程服务器
        public void UploadFile(string? localPath, string? remotePath)
        {
            if (string.IsNullOrEmpty(localPath))
                throw new ArgumentException($"'{nameof(localPath)}' cannot be null or empty", nameof(localPath));
                
            if (string.IsNullOrEmpty(remotePath))
                throw new ArgumentException($"'{nameof(remotePath)}' cannot be null or empty", nameof(remotePath));
                
            using (var file = File.OpenRead(localPath))
            {
                sftpClient.UploadFile(file, remotePath);
            }
        }

        // Upload single file with cancellation support
        // 上传单个文件（支持取消）
        public void UploadFile(string? localPath, string? remotePath, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(localPath))
                throw new ArgumentException($"'{nameof(localPath)}' cannot be null or empty", nameof(localPath));
            if (string.IsNullOrEmpty(remotePath))
                throw new ArgumentException($"'{nameof(remotePath)}' cannot be null or empty", nameof(remotePath));

            // 取消标志 — 从回调里设置，从调用线程检查并抛出
            // cancellation flag — set from callback, check and throw from calling thread
            bool cancelled = false;
            FileStream? file = null;

            try
            {
                file = File.OpenRead(localPath);
                sftpClient.UploadFile(file, remotePath, uploaded =>
                {
                    // ⚠ Do NOT throw in this callback — SSH.NET runs it on an internal thread pool thread.
                    // Exceptions thrown here may become unobserved and crash the process.
                    // ⚠ 不要在此回调里抛异常 — SSH.NET 在内部线程池线程上执行回调。
                    // 从这里抛出的异常可能成为未观察异常导致程序崩溃。
                    if (cancellationToken.IsCancellationRequested)
                    {
                        cancelled = true;
                        try { file?.Dispose(); } catch { }
                    }
                });
            }
            catch (ObjectDisposedException) when (cancelled)
            {
                // Expected: stream disposed by cancellation callback
                // 预期行为：取消回调关闭了流
            }
            catch (IOException) when (cancelled)
            {
                // Expected: stream closed by cancellation callback
                // 预期行为：取消回调关闭了流
            }
            catch
            {
                if (file != null)
                {
                    try { file.Dispose(); } catch { }
                }
                throw;
            }

            if (file != null)
            {
                try { file.Dispose(); } catch { }
            }

            // Safe to throw from the calling thread
            // 从调用线程抛出是安全的
            if (cancelled)
                throw new OperationCanceledException(cancellationToken);
        }

        // Recursively upload local folder to remote server
        // 递归上传本地文件夹到远程服务器
        public void UploadFolder(string? localFolderPath, string? remoteFolderPath)
        {
            if (string.IsNullOrEmpty(localFolderPath))
                throw new ArgumentException($"'{nameof(localFolderPath)}' cannot be null or empty", nameof(localFolderPath));
                
            if (string.IsNullOrEmpty(remoteFolderPath))
                throw new ArgumentException($"'{nameof(remoteFolderPath)}' cannot be null or empty", nameof(remoteFolderPath));
            
            // Create remote directory if it doesn't exist
            // 如果远程目录不存在则创建
            try
            {
                if (!DirectoryExists(remoteFolderPath))
                {
                    CreateDirectory(remoteFolderPath);
                }
            }
            catch
            {
                // Directory may already exist, continue
                // 目录可能已存在，继续
            }
            
            // Get all files and subdirectories in local folder
            // 获取本地文件夹中的所有文件和子目录
            var files = Directory.GetFiles(localFolderPath);
            var directories = Directory.GetDirectories(localFolderPath);
            
            // Upload all files
            // 上传所有文件
            foreach (var file in files)
            {
                var fileName = Path.GetFileName(file);
                var remoteFilePath = $"{remoteFolderPath}/{fileName}".Replace("//", "/");
                UploadFile(file, remoteFilePath);
            }
            
            // Recursively upload all subdirectories
            // 递归上传所有子目录
            foreach (var directory in directories)
            {
                var dirName = Path.GetFileName(directory);
                var remoteSubFolderPath = $"{remoteFolderPath}/{dirName}".Replace("//", "/");
                UploadFolder(directory, remoteSubFolderPath);
            }
        }

        // Recursively upload local folder with cancellation support
        // 递归上传本地文件夹（支持取消）
        public void UploadFolder(string? localFolderPath, string? remoteFolderPath, CancellationToken cancellationToken)
        {
            // 这是在调用线程上执行的 ThrowIfCancellationRequested — 安全
            // This ThrowIfCancellationRequested runs on the calling thread — safe
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(localFolderPath))
                throw new ArgumentException($"'{nameof(localFolderPath)}' cannot be null or empty", nameof(localFolderPath));
            if (string.IsNullOrEmpty(remoteFolderPath))
                throw new ArgumentException($"'{nameof(remoteFolderPath)}' cannot be null or empty", nameof(remoteFolderPath));

            try
            {
                if (!DirectoryExists(remoteFolderPath))
                    CreateDirectory(remoteFolderPath);
            }
            catch { }

            var files = Directory.GetFiles(localFolderPath);
            var directories = Directory.GetDirectories(localFolderPath);

            foreach (var file in files)
            {
                // 两个文件之间检查取消（在调用线程上 — 安全）
                // Check cancellation between files (on calling thread — safe)
                cancellationToken.ThrowIfCancellationRequested();
                var fileName = Path.GetFileName(file);
                var remoteFilePath = $"{remoteFolderPath}/{fileName}".Replace("//", "/");
                UploadFile(file, remoteFilePath, cancellationToken);
            }

            foreach (var directory in directories)
            {
                // 两个子文件夹之间检查取消（在调用线程上 — 安全）
                // Check cancellation between subfolders (on calling thread — safe)
                cancellationToken.ThrowIfCancellationRequested();
                var dirName = Path.GetFileName(directory);
                var remoteSubFolderPath = $"{remoteFolderPath}/{dirName}".Replace("//", "/");
                UploadFolder(directory, remoteSubFolderPath, cancellationToken);
            }
        }

        // Download remote file to local
        // 下载远程文件到本地
        public void DownloadFile(string? remotePath, string? localPath)
        {
            if (string.IsNullOrEmpty(remotePath))
                throw new ArgumentException($"'{nameof(remotePath)}' cannot be null or empty", nameof(remotePath));
                
            if (string.IsNullOrEmpty(localPath))
                throw new ArgumentException($"'{nameof(localPath)}' cannot be null or empty", nameof(localPath));
                
            using (var file = File.OpenWrite(localPath))
            {
                sftpClient.DownloadFile(remotePath, file);
            }
        }

        // Download remote file with cancellation support
        // 下载远程文件（支持取消）
        public void DownloadFile(string? remotePath, string? localPath, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(remotePath))
                throw new ArgumentException($"'{nameof(remotePath)}' cannot be null or empty", nameof(remotePath));
            if (string.IsNullOrEmpty(localPath))
                throw new ArgumentException($"'{nameof(localPath)}' cannot be null or empty", nameof(localPath));

            bool cancelled = false;
            FileStream? file = null;

            try
            {
                file = File.OpenWrite(localPath);
                sftpClient.DownloadFile(remotePath, file, downloaded =>
                {
                    // ⚠ Do NOT throw in this callback — SSH.NET runs it on an internal thread pool thread.
                    // Exceptions thrown here may become unobserved and crash the process.
                    // ⚠ 不要在此回调里抛异常 — SSH.NET 在内部线程池线程上执行回调。
                    // 从这里抛出的异常可能成为未观察异常导致程序崩溃。
                    if (cancellationToken.IsCancellationRequested)
                    {
                        cancelled = true;
                        try { file?.Dispose(); } catch { }
                    }
                });
            }
            catch (ObjectDisposedException) when (cancelled)
            {
                // Expected: stream disposed by cancellation callback
                // 预期行为：取消回调关闭了流
            }
            catch (IOException) when (cancelled)
            {
                // Expected: stream closed by cancellation callback
                // 预期行为：取消回调关闭了流
            }
            catch
            {
                if (file != null)
                {
                    try { file.Dispose(); } catch { }
                }
                throw;
            }

            if (file != null)
            {
                try { file.Dispose(); } catch { }
            }

            // Safe to throw from the calling thread
            // 从调用线程抛出是安全的
            if (cancelled)
                throw new OperationCanceledException(cancellationToken);
        }

        // Delete file on remote server
        // 删除远程服务器上的文件
        public void DeleteFile(string? remotePath)
        {
            if (string.IsNullOrEmpty(remotePath))
                throw new ArgumentException($"'{nameof(remotePath)}' cannot be null or empty", nameof(remotePath));
                
            sftpClient.DeleteFile(remotePath);
        }

        // Recursively delete directory and all its contents
        // 递归删除目录及其所有内容
        public void DeleteDirectory(string? remotePath)
        {
            if (string.IsNullOrEmpty(remotePath))
                throw new ArgumentException($"'{nameof(remotePath)}' cannot be null or empty", nameof(remotePath));

            // Get all items in the directory
            // 获取目录中的所有项目
            var items = sftpClient.ListDirectory(remotePath).ToList();
            
            foreach (var item in items)
            {
                // Skip current directory (.) and parent directory (..)
                // 跳过当前目录(.)和父目录(..)
                if (item.Name == "." || item.Name == "..")
                    continue;
                    
                var itemPath = $"{remotePath}/{item.Name}".Replace("//", "/");
                
                if (item.IsDirectory)
                {
                    // Recursively delete subdirectory
                    // 递归删除子目录
                    DeleteDirectory(itemPath);
                }
                else
                {
                    // Delete file
                    // 删除文件
                    sftpClient.DeleteFile(itemPath);
                }
            }
            
            // Finally delete the empty directory
            // 最后删除空目录
            sftpClient.DeleteDirectory(remotePath);
        }

        // Recursively delete directory with cancellation support
        // 递归删除目录（支持取消）
        public void DeleteDirectory(string? remotePath, CancellationToken cancellationToken)
        {
            // 在调用线程上检查取消 — 安全
            // Check cancellation on calling thread — safe
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(remotePath))
                throw new ArgumentException($"'{nameof(remotePath)}' cannot be null or empty", nameof(remotePath));

            var items = sftpClient.ListDirectory(remotePath).ToList();
            foreach (var item in items)
            {
                // 两个项目之间检查取消（在调用线程上 — 安全）
                // Check cancellation between items (on calling thread — safe)
                cancellationToken.ThrowIfCancellationRequested();
                if (item.Name == "." || item.Name == "..") continue;

                var itemPath = $"{remotePath}/{item.Name}".Replace("//", "/");
                if (item.IsDirectory)
                    DeleteDirectory(itemPath, cancellationToken);
                else
                    sftpClient.DeleteFile(itemPath);
            }

            // 最后检查一次取消
            // Final check before deleting the directory itself
            cancellationToken.ThrowIfCancellationRequested();
            sftpClient.DeleteDirectory(remotePath);
        }

        // Create directory on remote server
        // 在远程服务器上创建目录
        public void CreateDirectory(string? remotePath)
        {
            if (string.IsNullOrEmpty(remotePath))
                throw new ArgumentException($"'{nameof(remotePath)}' cannot be null or empty", nameof(remotePath));
                
            sftpClient.CreateDirectory(remotePath);
        }

        // Check if a file exists on the remote server
        // 检查远程服务器上是否存在同名文件
        public bool FileExists(string? remotePath)
        {
            if (string.IsNullOrEmpty(remotePath))
                return false;

            try
            {
                var attributes = sftpClient.GetAttributes(remotePath);
                return !attributes.IsDirectory;
            }
            catch
            {
                return false;
            }
        }

        // Check if directory exists on remote server
        // 检查远程服务器上目录是否存在
        public bool DirectoryExists(string? remotePath)
        {
            if (string.IsNullOrEmpty(remotePath))
                throw new ArgumentException($"'{nameof(remotePath)}' cannot be null or empty", nameof(remotePath));

            try
            {
                var attributes = sftpClient.GetAttributes(remotePath);
                return attributes.IsDirectory;
            }
            catch
            {
                return false;
            }
        }
    }
}