using System.Text.Json.Serialization;

namespace SSHFileExplorer
{
    // 保存的 SSH 连接信息（会被加密后写到本地文件）
    // Saved SSH connection info (written to local file after encryption)
    public class SavedConnection
    {
        [JsonPropertyName("name")]
        public string DisplayName { get; set; } = "";

        [JsonPropertyName("host")]
        public string Host { get; set; } = "";

        [JsonPropertyName("user")]
        public string User { get; set; } = "";

        [JsonPropertyName("password")]
        public string Password { get; set; } = "";

        [JsonPropertyName("privateKeyPath")]
        public string? PrivateKeyPath { get; set; }

        [JsonPropertyName("port")]
        public int Port { get; set; } = 22;

        // 用于在 UI 里显示的友好名称 / Friendly label shown in UI
        public string Label =>
            string.IsNullOrEmpty(DisplayName)
                ? $"{User}@{Host}:{Port}"
                : $"{DisplayName} ({User}@{Host}:{Port})";
    }
}