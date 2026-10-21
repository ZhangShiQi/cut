using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cut.Services;

// Deliberately not a record: generated ToString must never expose credentials.
public sealed class TranslationSettings
{
    public const string DefaultEndpoint = "https://api.cognitive.microsofttranslator.com";
    public string Endpoint { get; init; } = DefaultEndpoint;
    public string Region { get; init; } = "";
    public string ApiKey { get; init; } = "";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

    public Uri GetTranslateUri(string source, string target)
    {
        Validate();
        if (source is not ("auto" or "zh-Hans" or "en") || target is not ("zh-Hans" or "en"))
            throw new TranslationException("当前支持自动检测、简体中文和英文，请重新选择语言。");
        var endpoint = new Uri(Endpoint.Trim().TrimEnd('/'));
        string path = endpoint.Host.EndsWith(".cognitiveservices.azure.com", StringComparison.OrdinalIgnoreCase)
            ? "/translator/text/v3.0/translate" : "/translate";
        var builder = new UriBuilder(endpoint) { Path = path, Query = "api-version=3.0&textType=plain&to=" + Uri.EscapeDataString(target) };
        if (source != "auto") builder.Query += "&from=" + Uri.EscapeDataString(source);
        return builder.Uri;
    }

    public void Validate(bool requireKey = true)
    {
        if (!Uri.TryCreate(Endpoint.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.IsDefaultPort || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new TranslationException("服务地址必须是 Azure 官方 HTTPS 地址，不含密钥、查询参数或片段。");
        string host = uri.IdnHost.ToLowerInvariant();
        bool custom = host.EndsWith(".cognitiveservices.azure.com", StringComparison.Ordinal);
        if (host is not ("api.cognitive.microsofttranslator.com" or "api-apc.cognitive.microsofttranslator.com" or
            "api-eur.cognitive.microsofttranslator.com" or "api-nam.cognitive.microsofttranslator.com") && !custom)
            throw new TranslationException("当前仅支持 Azure Translator 官方服务地址，请使用默认地址或 Azure 资源的自定义地址。");
        string path = uri.AbsolutePath.TrimEnd('/');
        if (path.Length != 0 && !(custom && path == "/translator/text/v3.0"))
            throw new TranslationException("请填写服务根地址；无需填写 /translate 或 api-version。");
        if (Region.Length > 64 || Region.AnyInvalidRegionCharacter())
            throw new TranslationException("区域应为 Azure 区域代码，例如 eastasia，不含空格。");
        if (requireKey && !IsConfigured) throw new TranslationException("尚未配置翻译服务，请点击“翻译设置”填写 Azure Translator 密钥和区域。");
        if (ApiKey.Length > 512 || ApiKey.AnyInvalidKeyCharacter())
            throw new TranslationException("密钥格式不正确，请重新粘贴 Azure Translator 密钥。");
    }
}

internal static class TranslationValidation
{
    public static bool AnyInvalidKeyCharacter(this string value)
    {
        foreach (char c in value) if (c < 33 || c > 126) return true;
        return false;
    }
    public static bool AnyInvalidRegionCharacter(this string value)
    {
        foreach (char c in value) if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')) return true;
        return false;
    }
}

public sealed class TranslationSettingsStore
{
    private readonly string path;
    public TranslationSettingsStore() : this(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cut", "translation.json")) { }
    internal TranslationSettingsStore(string path) => this.path = path;
    private sealed class StoredSettings
    {
        public int Version { get; set; } = 1;
        public string Endpoint { get; set; } = TranslationSettings.DefaultEndpoint;
        public string Region { get; set; } = "";
        public string EncryptedKey { get; set; } = "";
    }
    public TranslationSettings Load()
    {
        if (!File.Exists(path)) return new();
        try
        {
            var saved = JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(path)) ?? throw new JsonException();
            if (saved.Version != 1) throw new JsonException();
            string key = "";
            if (!string.IsNullOrEmpty(saved.EncryptedKey))
            {
                byte[] bytes = ProtectedData.Unprotect(Convert.FromBase64String(saved.EncryptedKey), null, DataProtectionScope.CurrentUser);
                try { key = Encoding.UTF8.GetString(bytes); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            var result = new TranslationSettings { Endpoint = saved.Endpoint ?? "", Region = saved.Region ?? "", ApiKey = key };
            result.Validate(false);
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or CryptographicException or FormatException or TranslationException)
        { throw new TranslationException("无法读取翻译配置或解密密钥。请在“翻译设置”重新填写并保存；密钥仅能由当前 Windows 用户解密。"); }
    }
    public void Save(TranslationSettings settings)
    {
        settings.Validate(false);
        byte[] bytes = Encoding.UTF8.GetBytes(settings.ApiKey);
        string encrypted;
        try { encrypted = bytes.Length == 0 ? "" : Convert.ToBase64String(ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        var stored = new StoredSettings { Endpoint = settings.Endpoint.Trim().TrimEnd('/'), Region = settings.Region, EncryptedKey = encrypted };
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(stored)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
