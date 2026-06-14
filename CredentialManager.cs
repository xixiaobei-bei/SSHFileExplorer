using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Windows.Security.Credentials;

namespace SSHFileExplorer
{
    // PBKDF2 + AES 本地加密凭据管理器 / PBKDF2 + AES local encrypted credential manager
    // 阶段一：首次启动 → 创建主密码 → 派生主密钥 → 存到 PasswordVault 作为自动密钥
    // 阶段二：正常启动 → 从 PasswordVault 读取自动密钥 → 自动解密
    // 阶段三：自动密钥丢失 → 提示输入主密码 → 重新派生 → 存回 PasswordVault
    public static class CredentialManager
    {
        // PasswordVault 资源名（固定，用户指定）
        private const string VaultResource = "SSHFileExplorer_Password";

        // 本地加密数据文件路径（ApplicationData.Current.LocalFolder）
        private static string DataFilePath =>
            Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "credentials.enc");

        // PBKDF2 参数
        private const int KeySizeBytes = 32;       // 256-bit AES key
        private const int IvSizeBytes = 16;        // 128-bit IV for AES-CBC
        private const int SaltSizeBytes = 16;      // 128-bit salt
        private const int Iterations = 10000;      // PBKDF2 迭代次数

        // 内存中缓存的自动密钥（AES key，base64 字符串）——只在单个操作期间缓存，用完立即清除
        // Auto-key cached in memory (AES key as base64 string).
        // Only cached during a single operation; cleared immediately after use.
        private static string? _cachedAutoKey;

        // = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = =
        // 内存清理工具 / Memory sanitation helpers
        // = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = =

        // 安全清零字节数组中的敏感数据 / Safely zero out sensitive data in a byte array
        private static void ZeroMemory(byte[] data)
        {
            if (data == null) return;
            Array.Clear(data, 0, data.Length);
        }

        // 清除缓存的自动密钥 / Clear the cached auto-key
        public static void ClearAutoKeyCache()
        {
            _cachedAutoKey = null;
        }

        // 清除 SavedConnection 对象中的密码字段（调用者使用完后调用）
        // Clear password fields in a SavedConnection object (call after use)
        public static void ClearConnectionPassword(SavedConnection conn)
        {
            if (conn == null) return;
            // C# string 是不可变的，无法真正清零；这里置为 null 让引用可被 GC 回收
            // C# strings are immutable and cannot be zeroed in place;
            // setting to null allows the reference to be collected by GC.
            conn.Password = null;
        }

        // = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = =
        // 状态检测
        // = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = =

        // 本地是否已经有加密凭据文件（即是否初始化过主密码）
        public static bool HasDataFile() => File.Exists(DataFilePath);

        // = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = =
        // PasswordVault 读写（自动密钥）
        // = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = =

        // 从 PasswordVault 读取自动密钥 / Read auto-key from PasswordVault
        public static string? TryGetAutoKeyFromVault()
        {
            try
            {
                var vault = new PasswordVault();
                var entries = vault.FindAllByResource(VaultResource).ToList();
                if (entries.Count == 0) return null;

                var cred = entries[0];
                cred.RetrievePassword();
                return cred.Password;  // 里面存的是 base64 的 AES key / Stores a base64 AES key
            }
            catch
            {
                return null;
            }
        }

        // 将自动密钥存入 PasswordVault（覆盖之前的）
        public static void SaveAutoKeyToVault(string base64AesKey)
        {
            try
            {
                var vault = new PasswordVault();
                // 先清掉旧的
                try
                {
                    var oldEntries = vault.FindAllByResource(VaultResource).ToList();
                    foreach (var old in oldEntries)
                        vault.Remove(old);
                }
                catch { /* 没有旧的就不管 */ }

                vault.Add(new PasswordCredential(VaultResource, "auto_key", base64AesKey));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"无法写入系统凭据管理器：{ex.Message}", ex);
            }
        }

        // = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = =
        // 密钥派生（PBKDF2）
        // = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = =

        // 从用户主密码派生 AES key / Derive AES key from master password
        public static byte[] DeriveKeyFromMasterPassword(string masterPassword, byte[] salt, int iterations)
        {
            byte[] pbkdf2 = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(masterPassword),
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                KeySizeBytes);
            return pbkdf2;
        }

        // = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = =
        // 加密 / 解密（AES-CBC + PKCS7 padding）
        // = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = =

        private static byte[] EncryptBytes(byte[] plainData, byte[] aesKey)
        {
            using var aes = Aes.Create();
            aes.Key = aesKey;
            aes.GenerateIV();

            using var encryptor = aes.CreateEncryptor();
            using var ms = new MemoryStream();

            // 先写 IV（16 字节），再写密文
            // Write IV first (16 bytes), then ciphertext
            ms.Write(aes.IV, 0, aes.IV.Length);

            using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
            {
                cs.Write(plainData, 0, plainData.Length);
            }
            var result = ms.ToArray();

            // 清除 AES 实例的密钥副本 / Zero out the key copy in the AES instance
            try
            {
                aes.Key = new byte[KeySizeBytes];
            }
            catch { }

            return result;
        }

        private static byte[] DecryptBytes(byte[] cipherData, byte[] aesKey)
        {
            using var aes = Aes.Create();
            aes.Key = aesKey;

            // 前 16 字节是 IV / First 16 bytes are the IV
            var iv = new byte[IvSizeBytes];
            Buffer.BlockCopy(cipherData, 0, iv, 0, IvSizeBytes);
            aes.IV = iv;

            using var decryptor = aes.CreateDecryptor();
            using var ms = new MemoryStream();
            using (var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Write))
            {
                cs.Write(cipherData, IvSizeBytes, cipherData.Length - IvSizeBytes);
            }
            var result = ms.ToArray();

            // 清除 AES 实例的密钥副本 / Zero out the key copy in the AES instance
            try
            {
                aes.Key = new byte[KeySizeBytes];
            }
            catch { }

            ZeroMemory(iv);
            return result;
        }

        // = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = =
        // 本地文件读写
        // 数据格式（JSON）：{ salt, iterations, encryptedCredentials }
        // = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = =

        private class EncryptedDataFile
        {
            public string SaltBase64 { get; set; } = "";
            public int Iterations { get; set; }
            public string EncryptedBase64 { get; set; } = "";
        }

        // 首次初始化：创建主密码 → 保存空凭据 / First-time init: create master password
        public static void InitializeWithMasterPassword(string masterPassword)
        {
            // 1) 生成随机 salt
            var salt = new byte[SaltSizeBytes];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(salt);

            // 2) 派生主密钥 / Derive master key
            var masterKey = DeriveKeyFromMasterPassword(masterPassword, salt, Iterations);

            // 3) 主密钥 = 自动密钥（初始化时二者相同）
            // Master key = auto-key (they are the same at initialization time)
            string base64Key = Convert.ToBase64String(masterKey);

            // 4) 把自动密钥存到 PasswordVault / Save auto-key to PasswordVault
            SaveAutoKeyToVault(base64Key);

            // 5) 用主密钥加密一个空的凭据列表保存到本地文件
            // Encrypt an empty credential list with the master key and save to local file
            var emptyList = new List<SavedConnection>();
            var plainJson = JsonSerializer.Serialize(emptyList);
            var cipherBytes = EncryptBytes(Encoding.UTF8.GetBytes(plainJson), masterKey);

            var file = new EncryptedDataFile
            {
                SaltBase64 = Convert.ToBase64String(salt),
                Iterations = Iterations,
                EncryptedBase64 = Convert.ToBase64String(cipherBytes)
            };
            File.WriteAllText(DataFilePath, JsonSerializer.Serialize(file));

            // 用完立即清除敏感数据 / Sanitize sensitive data immediately after use
            ZeroMemory(masterKey);
            ZeroMemory(salt);
            ZeroMemory(cipherBytes);
            // 注意：字符串无法被清零，只能让引用可被 GC 回收
            // Note: strings cannot be zeroed in place; we just drop references for GC
            masterPassword = null!;
            _cachedAutoKey = null;
        }

        // 用自动密钥解密并加载保存的连接列表（列表里的 Password 字段将被清空，仅供显示用）
        // Load saved connections with the auto-key. The Password field in each item is cleared before return.
        // Use GetConnectionPassword() to retrieve individual password on demand.
        public static List<SavedConnection> LoadConnectionsForDisplay()
        {
            string? autoKey = _cachedAutoKey ?? TryGetAutoKeyFromVault();
            if (string.IsNullOrEmpty(autoKey))
                throw new InvalidOperationException("没有找到自动解锁密钥 / No auto-unlock key found");

            var aesKey = Convert.FromBase64String(autoKey);

            if (!File.Exists(DataFilePath))
            {
                ZeroMemory(aesKey);
                _cachedAutoKey = null;
                return new List<SavedConnection>();
            }

            var file = JsonSerializer.Deserialize<EncryptedDataFile>(File.ReadAllText(DataFilePath));
            if (file == null)
            {
                ZeroMemory(aesKey);
                _cachedAutoKey = null;
                return new List<SavedConnection>();
            }

            var cipherBytes = Convert.FromBase64String(file.EncryptedBase64);
            var plainBytes = DecryptBytes(cipherBytes, aesKey);
            var json = Encoding.UTF8.GetString(plainBytes);
            var list = JsonSerializer.Deserialize<List<SavedConnection>>(json) ?? new List<SavedConnection>();

            // ↓ 关键：清空每个连接的密码字段，只保留显示需要的信息
            // ↓ Critical: clear the Password field from every connection — only metadata is needed for display
            foreach (var c in list)
            {
                c.Password = null;
            }

            // 用完立即清除敏感数据 / Sanitize all sensitive data immediately after use
            ZeroMemory(aesKey);
            ZeroMemory(plainBytes);
            ZeroMemory(cipherBytes);
            _cachedAutoKey = null;

            return list;
        }

        // 按需解密单个连接的密码（点击连接时调用，用完立即清理）
        // Decrypt an individual connection's password on demand (called when user clicks to connect).
        // Returns the password string; caller should null it out after use.
        public static string? GetConnectionPassword(string host, string user, int port, string? displayName)
        {
            string? autoKey = _cachedAutoKey ?? TryGetAutoKeyFromVault();
            if (string.IsNullOrEmpty(autoKey))
                return null;

            var aesKey = Convert.FromBase64String(autoKey);

            if (!File.Exists(DataFilePath))
            {
                ZeroMemory(aesKey);
                _cachedAutoKey = null;
                return null;
            }

            var file = JsonSerializer.Deserialize<EncryptedDataFile>(File.ReadAllText(DataFilePath));
            if (file == null)
            {
                ZeroMemory(aesKey);
                _cachedAutoKey = null;
                return null;
            }

            var cipherBytes = Convert.FromBase64String(file.EncryptedBase64);
            var plainBytes = DecryptBytes(cipherBytes, aesKey);
            var json = Encoding.UTF8.GetString(plainBytes);
            var list = JsonSerializer.Deserialize<List<SavedConnection>>(json) ?? new List<SavedConnection>();

            // 按 host+user+port+displayName 查找 / Lookup by host+user+port+displayName
            string? foundPassword = null;
            foreach (var c in list)
            {
                if (c.Host == host && c.User == user && c.Port == port && c.DisplayName == displayName)
                {
                    foundPassword = c.Password;
                    c.Password = null;
                    break;
                }
            }

            // 清理所有列表项中的密码字段（虽然本来就不该在显示列表里保留）
            // Clear password fields in the list (they should never stay around anyway)
            foreach (var c in list)
            {
                c.Password = null;
            }

            // 用完立即清除敏感数据 / Sanitize all sensitive data immediately after use
            ZeroMemory(aesKey);
            ZeroMemory(plainBytes);
            ZeroMemory(cipherBytes);
            _cachedAutoKey = null;

            return foundPassword;
        }

        // 用用户输入的主密码尝试解密（阶段三：自动密钥丢失时）
        // Return null = password wrong; non-null = success (derived key is also saved to PasswordVault as auto-key).
        // The returned list has Password fields cleared — use GetConnectionPassword() for individual lookup.
        public static List<SavedConnection>? TryRecoverWithMasterPassword(string masterPassword)
        {
            if (!File.Exists(DataFilePath)) return new List<SavedConnection>();

            var file = JsonSerializer.Deserialize<EncryptedDataFile>(File.ReadAllText(DataFilePath));
            if (file == null) return new List<SavedConnection>();

            var salt = Convert.FromBase64String(file.SaltBase64);
            var iterations = file.Iterations;

            // 用用户输入的密码派生 key / Derive key from user-entered password
            var candidateKey = DeriveKeyFromMasterPassword(masterPassword, salt, iterations);

            try
            {
                var cipherBytes = Convert.FromBase64String(file.EncryptedBase64);
                var plainBytes = DecryptBytes(cipherBytes, candidateKey);
                var json = Encoding.UTF8.GetString(plainBytes);
                var list = JsonSerializer.Deserialize<List<SavedConnection>>(json);

                // 解密 + JSON 解析都成功 → 密码正确
                // Decryption + JSON parsing both succeeded → password is correct
                string base64Key = Convert.ToBase64String(candidateKey);
                SaveAutoKeyToVault(base64Key);

                // 显示用的列表：清掉所有密码 / For-display list: clear all passwords
                if (list != null)
                {
                    foreach (var c in list)
                        c.Password = null;
                }

                // 用完立即清除敏感数据 / Sanitize sensitive data immediately after use
                ZeroMemory(candidateKey);
                ZeroMemory(salt);
                ZeroMemory(plainBytes);
                ZeroMemory(cipherBytes);
                _cachedAutoKey = null;
                masterPassword = null!;

                return list ?? new List<SavedConnection>();
            }
            catch (CryptographicException)
            {
                // AES padding 校验失败 → 密码错误 / AES padding check failed → wrong password
                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // 保存一个连接到列表（用自动密钥加密并写文件） / Save one connection (encrypted with auto-key)
        public static void SaveConnection(SavedConnection conn)
        {
            var list = LoadAllConnectionsWithPasswords();

            // 按 host+user+port+displayName 去重：如果已经存在就更新
            // De-duplicate by host+user+port+displayName: update if it already exists
            bool found = false;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Host == conn.Host && list[i].User == conn.User && list[i].Port == conn.Port && list[i].DisplayName == conn.DisplayName)
                {
                    list[i] = conn;
                    found = true;
                    break;
                }
            }
            if (!found) list.Add(conn);

            SaveAllConnections(list);
        }

        // 从列表删除一个连接 / Delete one connection
        public static void DeleteConnection(SavedConnection conn)
        {
            var list = LoadAllConnectionsWithPasswords();
            list.RemoveAll(c =>
                c.Host == conn.Host && c.User == conn.User && c.Port == conn.Port && c.DisplayName == conn.DisplayName);
            SaveAllConnections(list);
        }

        // 内部：加载完整列表（带密码），只在 SaveConnection / DeleteConnection 内部使用
        // Internal: load full list (with passwords), only used inside SaveConnection / DeleteConnection
        private static List<SavedConnection> LoadAllConnectionsWithPasswords()
        {
            string? autoKey = _cachedAutoKey ?? TryGetAutoKeyFromVault();
            if (string.IsNullOrEmpty(autoKey))
                throw new InvalidOperationException("没有找到自动解锁密钥，无法保存 / No auto-unlock key found, cannot save");

            var aesKey = Convert.FromBase64String(autoKey);

            if (!File.Exists(DataFilePath))
            {
                ZeroMemory(aesKey);
                _cachedAutoKey = null;
                return new List<SavedConnection>();
            }

            var file = JsonSerializer.Deserialize<EncryptedDataFile>(File.ReadAllText(DataFilePath));
            if (file == null)
            {
                ZeroMemory(aesKey);
                _cachedAutoKey = null;
                return new List<SavedConnection>();
            }

            var cipherBytes = Convert.FromBase64String(file.EncryptedBase64);
            var plainBytes = DecryptBytes(cipherBytes, aesKey);
            var json = Encoding.UTF8.GetString(plainBytes);
            var list = JsonSerializer.Deserialize<List<SavedConnection>>(json) ?? new List<SavedConnection>();

            // 保留密码（调用方 SaveAllConnections 需要），其他敏感数据立即清除
            // Keep passwords (needed by the caller SaveAllConnections); sanitize everything else immediately.
            ZeroMemory(aesKey);
            ZeroMemory(plainBytes);
            ZeroMemory(cipherBytes);
            // 注意：这里不清除 _cachedAutoKey，因为紧接着的 SaveAllConnections 还需要用它
            // Note: we do NOT clear _cachedAutoKey here because SaveAllConnections (called right after) still needs it
            return list;
        }

        // 加密并写回整个列表
        // Encrypt and write back the full list
        private static void SaveAllConnections(List<SavedConnection> list)
        {
            string? autoKey = _cachedAutoKey ?? TryGetAutoKeyFromVault();
            if (string.IsNullOrEmpty(autoKey))
                throw new InvalidOperationException("没有找到自动解锁密钥，无法保存 / No auto-unlock key found, cannot save");

            var aesKey = Convert.FromBase64String(autoKey);

            var plainJson = JsonSerializer.Serialize(list);
            var cipherBytes = EncryptBytes(Encoding.UTF8.GetBytes(plainJson), aesKey);

            // 从文件读回原来的 salt/iterations，保持不变
            // Read back the original salt/iterations from the file and keep them unchanged
            var file = File.Exists(DataFilePath)
                ? (JsonSerializer.Deserialize<EncryptedDataFile>(File.ReadAllText(DataFilePath)) ?? new EncryptedDataFile())
                : new EncryptedDataFile();

            // 如果文件没有 salt（首次）就生成 / Generate a salt if the file doesn't have one (first time)
            if (string.IsNullOrEmpty(file.SaltBase64))
            {
                var salt = new byte[SaltSizeBytes];
                using (var rng = RandomNumberGenerator.Create())
                    rng.GetBytes(salt);
                file.SaltBase64 = Convert.ToBase64String(salt);
                file.Iterations = Iterations;
                ZeroMemory(salt);
            }

            file.EncryptedBase64 = Convert.ToBase64String(cipherBytes);
            File.WriteAllText(DataFilePath, JsonSerializer.Serialize(file));

            // 用完立即清除敏感数据 / Sanitize sensitive data immediately after use
            ZeroMemory(aesKey);
            ZeroMemory(cipherBytes);
            foreach (var c in list)
                c.Password = null;
            _cachedAutoKey = null;
        }
    }
}