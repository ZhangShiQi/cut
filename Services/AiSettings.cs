using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cut.Services;

public sealed class AiSettings
{
    public string Endpoint { get; init; } = "https://api.openai.com/v1/chat/completions";
    public string Model { get; init; } = "";
    public string ApiKey { get; init; } = "";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Model);
    public void Validate(bool forRequest = true)
    {
        if (!Uri.TryCreate(Endpoint.Trim(), UriKind.Absolute, out var uri) ||
            !(uri.Scheme == "https" || (uri.Scheme == "http" && uri.IsLoopback)) ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new AiException("请填写完整 HTTPS 接口地址，不含查询参数、密钥或片段；本机服务允许 HTTP。");
        if (Endpoint.Length > 2048 || Model.Length > 200 || Model.Contains('\r') || Model.Contains('\n'))
            throw new AiException("服务地址或模型名称格式不正确。");
        if (ApiKey.Length > 4096 || ApiKey.AnyInvalidKeyCharacter()) throw new AiException("API Key 格式不正确，请重新粘贴。");
        if (forRequest && !IsConfigured) throw new AiException("请先点击“模型设置”，填写接口地址和模型名称。");
        if (forRequest && !uri.IsLoopback && string.IsNullOrEmpty(ApiKey)) throw new AiException("尚未填写 API Key，请在模型设置中填写。");
    }
}

public sealed class AiSettingsStore
{
    private readonly string path;
    public AiSettingsStore() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cut", "ai.json")) { }
    internal AiSettingsStore(string path) => this.path = path;
    private sealed class Stored
    {
        public int Version { get; set; } = 1;
        public string Endpoint { get; set; } = "";
        public string Model { get; set; } = "";
        public string EncryptedKey { get; set; } = "";
    }
    public AiSettings Load()
    {
        if (!File.Exists(path)) return new();
        try
        {
            var saved = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path)) ?? throw new JsonException();
            if (saved.Version != 1) throw new JsonException();
            string key = "";
            if (!string.IsNullOrEmpty(saved.EncryptedKey))
            {
                byte[] bytes = ProtectedData.Unprotect(Convert.FromBase64String(saved.EncryptedKey), null, DataProtectionScope.CurrentUser);
                try { key = Encoding.UTF8.GetString(bytes); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            var result = new AiSettings { Endpoint = saved.Endpoint ?? "", Model = saved.Model ?? "", ApiKey = key };
            result.Validate(false); return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or CryptographicException or FormatException or AiException)
        { throw new AiException("无法读取 AI 配置或解密密钥，请重新填写并保存。密钥仅能由当前 Windows 用户解密。"); }
    }
    public void Save(AiSettings settings)
    {
        settings.Validate(false);
        byte[] bytes = Encoding.UTF8.GetBytes(settings.ApiKey);
        string encrypted;
        try { encrypted = bytes.Length == 0 ? "" : Convert.ToBase64String(ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        var saved = new Stored { Endpoint = settings.Endpoint.Trim(), Model = settings.Model.Trim(), EncryptedKey = encrypted };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(saved)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
