using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Cut.Services;

public sealed class TranslationException : Exception
{
    public TranslationException(string message) : base(message) { }
}
public sealed record TranslationResult(string Text, string? DetectedLanguage);
public interface ITranslationService
{
    Task<TranslationResult> TranslateAsync(string text, string source, string target, TranslationSettings settings, CancellationToken cancellationToken);
}

public sealed class AzureTranslationService : ITranslationService
{
    public const int MaxCharacters = 5000;
    // Do not follow redirects with a subscription key or silently retry billable requests.
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient client;
    private readonly TimeSpan timeout;
    public AzureTranslationService() : this(Client, TimeSpan.FromSeconds(30)) { }
    internal AzureTranslationService(HttpClient client, TimeSpan? timeout = null) { this.client = client; this.timeout = timeout ?? TimeSpan.FromSeconds(30); }

    public async Task<TranslationResult> TranslateAsync(string text, string source, string target, TranslationSettings settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(text)) throw new TranslationException("请先输入或选中需要翻译的文字。");
        if (text.Length > MaxCharacters) throw new TranslationException("单次最多翻译 5000 个字符，请选中部分文字分段翻译。");
        if (source == target) throw new TranslationException("原文语言与目标语言相同，请调整语言选项。");
        var uri = settings.GetTranslateUri(source, target);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Headers.Add("Ocp-Apim-Subscription-Key", settings.ApiKey);
        if (settings.Region.Length > 0) request.Headers.Add("Ocp-Apim-Subscription-Region", settings.Region);
        request.Content = new StringContent(JsonSerializer.Serialize(new[] { new { Text = text } }), Encoding.UTF8, "application/json");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new TranslationException(ErrorMessage(response.StatusCode));
            // Bound response size and never display provider error bodies/headers (may
            // contain submitted text, resource details or other sensitive data).
            const int maxBytes = 1_048_576;
            if (response.Content.Headers.ContentLength > maxBytes) throw new TranslationException("翻译服务响应过大，请缩小文本范围后重试。");
            using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[8192]; int read;
            while ((read = await stream.ReadAsync(chunk, deadline.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > maxBytes) throw new TranslationException("翻译服务响应过大，请缩小文本范围后重试。");
                buffer.Write(chunk, 0, read);
            }
            using var document = JsonDocument.Parse(buffer.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != 1) throw new JsonException();
            var entry = root[0];
            if (!entry.TryGetProperty("translations", out var translations) || translations.ValueKind != JsonValueKind.Array) throw new JsonException();
            string? translated = null;
            foreach (var item in translations.EnumerateArray())
                if (item.TryGetProperty("to", out var language) && language.GetString() == target && item.TryGetProperty("text", out var value)) translated = value.GetString();
            if (string.IsNullOrWhiteSpace(translated)) throw new JsonException();
            string? detected = null;
            if (entry.TryGetProperty("detectedLanguage", out var detection) && detection.TryGetProperty("language", out var detectedValue)) detected = detectedValue.GetString();
            cancellationToken.ThrowIfCancellationRequested();
            return new(translated, detected);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TranslationException("翻译请求超时，请检查网络后重试。原文和已有译文已保留。"); }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        { throw new TranslationException("无法连接翻译服务，请检查网络和服务地址后重试。"); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        { throw new TranslationException("翻译服务返回了无法识别的结果，请检查服务地址或稍后重试。"); }
    }

    private static string ErrorMessage(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => "翻译认证失败，请检查服务密钥和区域是否与 Azure 资源一致。",
        HttpStatusCode.Forbidden => "翻译服务拒绝访问，请检查资源权限、区域和订阅状态。",
        HttpStatusCode.TooManyRequests => "翻译额度已用尽或请求过于频繁，请检查 Azure 配额并稍后重试。",
        HttpStatusCode.BadRequest => "翻译请求被拒绝，请检查语言选项和服务配置。",
        HttpStatusCode.NotFound => "翻译接口不存在，请检查服务地址。",
        _ when (int)status >= 500 => "翻译服务暂时不可用，请稍后重试。",
        _ when (int)status is >= 300 and < 400 => "翻译服务地址发生重定向，请在设置中核对官方地址。",
        _ => $"翻译失败（HTTP {(int)status}），请检查配置后重试。"
    };
}
