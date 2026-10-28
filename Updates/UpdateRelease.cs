using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using Cut.UpdateEngine;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cut.Updates;

public sealed record UpdateRelease(string Version, string PackageName, Uri DownloadUrl, string Sha256, long Size, string Notes, Uri? ApiDownloadUrl = null)
{
    public const string Repository = "ZhangShiQi/cut";
    public static string CurrentVersion => typeof(UpdateRelease).Assembly.GetName().Version!.ToString(3);
    public static Version ParseVersion(string value)
    {
        if (!Regex.IsMatch(value, @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$", RegexOptions.CultureInvariant)
            || !System.Version.TryParse(value, out var version)) throw new InvalidDataException("无效的正式版本号。");
        return version;
    }

    public static UpdateRelease? Parse(ReadOnlySpan<byte> json, string currentVersion)
    {
        using var doc = JsonDocument.Parse(json.ToArray());
        var root = doc.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!tag.StartsWith('v')) throw new InvalidDataException("更新标签必须为 v主版本.次版本.修订号。");
        var version = tag[1..];
        if (ParseVersion(version) <= ParseVersion(currentVersion)) return null;
        var name = $"Cut-{version}-win-x64-portable.zip";
        var candidates = root.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString() == name).ToArray();
        if (candidates.Length != 1) throw new InvalidDataException("此版本缺少唯一的 Windows x64 便携更新包。");
        var asset = candidates[0];
        var url = new Uri(asset.GetProperty("browser_download_url").GetString()!);
        var expected = $"https://github.com/{Repository}/releases/download/{tag}/{name}";
        if (url.AbsoluteUri != expected) throw new InvalidDataException("更新包地址不属于官方发布仓库。");
        var apiUrl = new Uri(asset.GetProperty("url").GetString()!);
        if (!Regex.IsMatch(apiUrl.AbsoluteUri, "^https://api\\.github\\.com/repos/" + Repository + "/releases/assets/[0-9]+$"))
            throw new InvalidDataException("更新资源 API 地址不属于官方仓库。");
        var digest = asset.TryGetProperty("digest", out var hash) ? hash.GetString() : null;
        if (digest is null || !Regex.IsMatch(digest, "^sha256:[a-fA-F0-9]{64}$")) throw new InvalidDataException("发布包缺少 GitHub SHA256 校验，暂不自动更新。");
        var size = asset.GetProperty("size").GetInt64();
        if (size <= 0 || size > 768L * 1024 * 1024) throw new InvalidDataException("更新包大小超出限制。");
        return new(version, name, url, digest[7..].ToLowerInvariant(), size,
            root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "", apiUrl);
    }
}

public sealed class UpdateClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Func<bool, CancellationToken, Task<string?>>? _credentials;
    private string? _accessToken;
    public UpdateClient(HttpMessageHandler? handler = null, Func<bool, CancellationToken, Task<string?>>? credentials = null)
    {
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Cut/" + UpdateRelease.CurrentVersion);
        _credentials = credentials ?? (handler is null ? UpdateCredentials.ReadAsync : null);
    }
    public async Task<UpdateRelease?> CheckAsync(string currentVersion, CancellationToken token, bool interactive = false)
    {
        var url = new Uri($"https://api.github.com/repos/{UpdateRelease.Repository}/releases/latest");
        using var first = await GetAsync(url, token);
        if (first.IsSuccessStatusCode) return await ReadRelease(first, currentVersion, token);
        if (_credentials is null) { if (first.StatusCode == HttpStatusCode.NotFound) return null; first.EnsureSuccessStatusCode(); }
        if (first.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            _accessToken = await _credentials!(interactive, token);
            if (string.IsNullOrWhiteSpace(_accessToken)) _accessToken = null;
            if (_accessToken is not null)
            {
                using var retry = await GetAsync(url, token);
                if (retry.IsSuccessStatusCode) return await ReadRelease(retry, currentVersion, token);
                if (retry.StatusCode != HttpStatusCode.NotFound) retry.EnsureSuccessStatusCode();
            }
            // A private/missing repository also returns 404. Do not report it as "up to date".
            using var repository = await GetAsync(new Uri($"https://api.github.com/repos/{UpdateRelease.Repository}"), token);
            if (repository.IsSuccessStatusCode && first.StatusCode == HttpStatusCode.NotFound) return null;
            throw new IOException("更新仓库不可访问。若为私有仓库，请在“私有更新仓库访问”中保存具有 Contents 读取权限的令牌后重试。");
        }
        first.EnsureSuccessStatusCode();
        return null;
    }
    private static async Task<UpdateRelease?> ReadRelease(HttpResponseMessage response, string currentVersion, CancellationToken token)
    {
        using var memory = new MemoryStream();
        await CopyLimitedAsync(await response.Content.ReadAsStreamAsync(token), memory, 2 * 1024 * 1024, null, token);
        return UpdateRelease.Parse(memory.ToArray(), currentVersion);
    }
    public async Task DownloadAsync(UpdateRelease release, string destination, IProgress<double>? progress, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var partial = destination + ".partial";
        try
        {
            Cut.UpdateEngine.UpdatePackage.CheckSpace(destination, release.Size + 16L * 1024 * 1024);
            using var response = await GetAsync(release.ApiDownloadUrl ?? release.DownloadUrl, token, binary: true);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length != release.Size) throw new InvalidDataException("下载大小与发布信息不一致。");
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                await CopyLimitedAsync(await response.Content.ReadAsStreamAsync(token), output, release.Size,
                    progress is null ? null : new Progress<long>(bytes => progress.Report(bytes * 100.0 / release.Size)), token);
                if (output.Length != release.Size) throw new InvalidDataException("更新包下载不完整。");
            }
            await UpdatePackage.VerifyHashAsync(partial, release.Sha256, token);
            File.Move(partial, destination, true);
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }
    private async Task<HttpResponseMessage> GetAsync(Uri url, CancellationToken token, bool binary = false)
    {
        for (var redirects = 0; redirects < 6; redirects++)
        {
            if (url.Scheme != "https" || !string.IsNullOrEmpty(url.UserInfo) || !url.IsDefaultPort
                || !(url.Host is "api.github.com" or "github.com" || url.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("更新下载重定向到了不受信任的地址。");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd(!binary && url.Host == "api.github.com" ? "application/vnd.github+json" : "application/octet-stream");
            if (_accessToken is not null && url.Host == "api.github.com"
                && (url.AbsolutePath == $"/repos/{UpdateRelease.Repository}" || url.AbsolutePath.StartsWith($"/repos/{UpdateRelease.Repository}/", StringComparison.Ordinal)))
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null) throw new InvalidDataException("下载重定向地址为空。");
            url = new Uri(url, location);
        }
        throw new InvalidDataException("更新下载重定向次数过多。");
    }
    private static async Task CopyLimitedAsync(Stream input, Stream output, long limit, IProgress<long>? progress, CancellationToken token)
    {
        var buffer = new byte[81920]; long total = 0;
        while (true)
        {
            var count = await input.ReadAsync(buffer, token);
            if (count == 0) break;
            total += count;
            if (total > limit) throw new InvalidDataException("下载内容超出大小限制。");
            await output.WriteAsync(buffer.AsMemory(0, count), token);
            progress?.Report(total);
        }
    }
    public void Dispose() { _accessToken = null; _http.Dispose(); }
}
