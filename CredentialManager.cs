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

        // 内存中缓存的自动密钥（AES key，base64 字符串）
        private static string? _cachedAutoKey;

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

                // 找到第一个条目（只存一个）
                var cred = entries[0];
                cred.RetrievePassword();
                return cred.Password;  // 里面存的是 base64 的 AES key
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
            ms.Write(aes.IV, 0, aes.IV.Length);

            using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
            {
                cs.Write(plainData, 0, plainData.Length);
            }
            return ms.ToArray();
        }

        private static byte[] DecryptBytes(byte[] cipherData, byte[] aesKey)
        {
            using var aes = Aes.Create();
            aes.Key = aesKey;

            // 前 16 字节是 IV
            var iv = new byte[IvSizeBytes];
            Buffer.BlockCopy(cipherData, 0, iv, 0, IvSizeBytes);
            aes.IV = iv;

            using var decryptor = aes.CreateDecryptor();
            using var ms = new MemoryStream();
            using (var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Write))
            {
                cs.Write(cipherData, IvSizeBytes, cipherData.Length - IvSizeBytes);
            }
            return ms.ToArray();
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

            // 2) 派生主密钥
            var masterKey = DeriveKeyFromMasterPassword(masterPassword, salt, Iterations);

            // 3) 主密钥 = 自动密钥（初始化时二者相同）
            string base64Key = Convert.ToBase64String(masterKey);

            // 4) 把自动密钥存到 PasswordVault
            SaveAutoKeyToVault(base64Key);

            // 5) 用主密钥加密一个空的凭据列表保存到本地文件
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

            _cachedAutoKey = base64Key;
        }

        // 用自动密钥解密并加载保存的连接列表 / Load saved connections with the auto-key
        public static List<SavedConnection> LoadConnections()
        {
            string? autoKey = _cachedAutoKey ?? TryGetAutoKeyFromVault();
            if (string.IsNullOrEmpty(autoKey))
                throw new InvalidOperationException("没有找到自动解锁密钥");

            _cachedAutoKey = autoKey;
            var aesKey = Convert.FromBase64String(autoKey);

            if (!File.Exists(DataFilePath))
                return new List<SavedConnection>();

            var file = JsonSerializer.Deserialize<EncryptedDataFile>(File.ReadAllText(DataFilePath));
            if (file == null) return new List<SavedConnection>();

            var cipherBytes = Convert.FromBase64String(file.EncryptedBase64);
            var plainBytes = DecryptBytes(cipherBytes, aesKey);
            var json = Encoding.UTF8.GetString(plainBytes);
            return JsonSerializer.Deserialize<List<SavedConnection>>(json) ?? new List<SavedConnection>();
        }

        // 用用户输入的主密码尝试解密（阶段三：自动密钥丢失时）
        // 返回 null = 密码错误；非 null = 成功（同时把派生 key 写入 PasswordVault 作为自动密钥）
        public static List<SavedConnection>? TryRecoverWithMasterPassword(string masterPassword)
        {
            if (!File.Exists(DataFilePath)) return new List<SavedConnection>();

            var file = JsonSerializer.Deserialize<EncryptedDataFile>(File.ReadAllText(DataFilePath));
            if (file == null) return new List<SavedConnection>();

            var salt = Convert.FromBase64String(file.SaltBase64);
            var iterations = file.Iterations;

            // 用用户输入的密码派生 key
            var candidateKey = DeriveKeyFromMasterPassword(masterPassword, salt, iterations);

            try
            {
                var cipherBytes = Convert.FromBase64String(file.EncryptedBase64);
                var plainBytes = DecryptBytes(cipherBytes, candidateKey);
                var json = Encoding.UTF8.GetString(plainBytes);
                var list = JsonSerializer.Deserialize<List<SavedConnection>>(json);

                // 解密 + JSON 解析都成功 → 密码正确
                string base64Key = Convert.ToBase64String(candidateKey);
                SaveAutoKeyToVault(base64Key);
                _cachedAutoKey = base64Key;
                return list ?? new List<SavedConnection>();
            }
            catch (CryptographicException)
            {
                // AES padding 校验失败 → 密码错误
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
            var list = LoadConnections();

            // 按 host+user 去重：如果已经存在就更新
            bool found = false;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Host == conn.Host && list[i].User == conn.User && list[i].Port == conn.Port)
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
            var list = LoadConnections();
            list.RemoveAll(c =>
                c.Host == conn.Host && c.User == conn.User && c.Port == conn.Port && c.DisplayName == conn.DisplayName);
            SaveAllConnections(list);
        }

        // 加密并写回整个列表
        private static void SaveAllConnections(List<SavedConnection> list)
        {
            string? autoKey = _cachedAutoKey ?? TryGetAutoKeyFromVault();
            if (string.IsNullOrEmpty(autoKey))
                throw new InvalidOperationException("没有找到自动解锁密钥，无法保存");

            _cachedAutoKey = autoKey;
            var aesKey = Convert.FromBase64String(autoKey);

            var plainJson = JsonSerializer.Serialize(list);
            var cipherBytes = EncryptBytes(Encoding.UTF8.GetBytes(plainJson), aesKey);

            // 为了简单：从文件读回原来的 salt/iterations，保持不变
            var file = File.Exists(DataFilePath)
                ? (JsonSerializer.Deserialize<EncryptedDataFile>(File.ReadAllText(DataFilePath)) ?? new EncryptedDataFile())
                : new EncryptedDataFile();

            // 如果文件没有 salt（首次）就生成
            if (string.IsNullOrEmpty(file.SaltBase64))
            {
                var salt = new byte[SaltSizeBytes];
                using (var rng = RandomNumberGenerator.Create())
                    rng.GetBytes(salt);
                file.SaltBase64 = Convert.ToBase64String(salt);
                file.Iterations = Iterations;
            }

            file.EncryptedBase64 = Convert.ToBase64String(cipherBytes);
            File.WriteAllText(DataFilePath, JsonSerializer.Serialize(file));
        }
    }
}