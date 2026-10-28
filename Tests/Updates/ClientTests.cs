using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Cut.UpdateEngine;
using Cut.Updates;

internal static partial class Program
{
    private sealed class MockHttp(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(send(request)); }
    }
    private static string ReleaseJson(string version, byte[] bytes, bool draft = false, bool prerelease = false) => JsonSerializer.Serialize(new
    {
        tag_name = "v" + version, draft, prerelease, body = "测试更新说明 中文",
        assets = new[] { new { name = $"Cut-{version}-win-x64-portable.zip", browser_download_url = $"https://github.com/ZhangShiQi/cut/releases/download/v{version}/Cut-{version}-win-x64-portable.zip", url = "https://api.github.com/repos/ZhangShiQi/cut/releases/assets/123", digest = "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(), size = bytes.Length } }
    });
    private static void Reject(Action action, string name)
    {
        try { action(); } catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or KeyNotFoundException or ArgumentException or OperationCanceledException) { return; }
        throw new InvalidOperationException("Unsafe operation accepted: " + name);
    }
    private static void ClientAndPackageTests()
    {
        var bytes = Encoding.UTF8.GetBytes("test package 中文");
        var json = ReleaseJson("1.2.3", bytes);
        var release = UpdateRelease.Parse(Encoding.UTF8.GetBytes(json), "1.2.2")!;
        Check(release.Version == "1.2.3" && release.Notes.Contains("中文"), "New version/release notes");
        Check(UpdateRelease.Parse(Encoding.UTF8.GetBytes(json), "1.2.3") is null, "Same version");
        Check(UpdateRelease.Parse(Encoding.UTF8.GetBytes(json), "1.3.0") is null, "Downgrade");
        foreach (var ignored in new[] { ReleaseJson("1.2.3", bytes, draft: true), ReleaseJson("1.2.3", bytes, prerelease: true) })
            Check(UpdateRelease.Parse(Encoding.UTF8.GetBytes(ignored), "1.0.0") is null, "Draft/prerelease");
        foreach (var invalid in new[] { json.Replace("ZhangShiQi/cut", "attacker/cut"), json.Replace("sha256:", "sha512:"), json.Replace("v1.2.3", "v01.2.3"), json.Replace("win-x64", "win-arm64") })
            Reject(() => UpdateRelease.Parse(Encoding.UTF8.GetBytes(invalid), "1.0.0"), "release metadata");
        var folder = Path.Combine(_root, "client"); Directory.CreateDirectory(folder);
        var download = Path.Combine(folder, "package.zip");
        var calls = 0;
        using (var client = new UpdateClient(new MockHttp(request =>
        {
            calls++;
            if (request.RequestUri!.Host == "api.github.com")
            {
                Check(request.Headers.Authorization is null, "Anonymous download unexpectedly contains credentials");
                return new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://release-assets.githubusercontent.com/asset") } };
            }
            Check(request.Headers.Authorization is null, "Token leaked to redirected download host");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }), (_, _) => Task.FromResult<string?>("synthetic-token")))
        {
            client.DownloadAsync(release, download, null, CancellationToken.None).GetAwaiter().GetResult();
        }
        // The initial client has not checked credentials: download must work anonymously too.
        Check(calls == 2 && File.ReadAllBytes(download).SequenceEqual(bytes), "Redirected complete download");
        File.Delete(download);
        var apiCalls = 0;
        using (var client = new UpdateClient(new MockHttp(request =>
        {
            apiCalls++;
            if (request.Headers.Authorization is null) return new HttpResponseMessage(HttpStatusCode.NotFound);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        }), (_, _) => Task.FromResult<string?>("synthetic-token")))
            Check(client.CheckAsync("1.0.0", CancellationToken.None).GetAwaiter().GetResult()?.Version == "1.2.3" && apiCalls == 2, "Private repository login/retry");
        foreach (var failure in new[] { "hash", "short", "redirect", "cancel" })
        {
            using var client = new UpdateClient(new MockHttp(_ => failure switch
            {
                "redirect" => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://evil.example/download") } },
                "short" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes[..^1]) },
                _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
            }));
            Reject(() => client.DownloadAsync(failure == "hash" ? release with { Sha256 = new string('0', 64) } : release,
                download, null, failure == "cancel" ? new CancellationToken(true) : CancellationToken.None).GetAwaiter().GetResult(), failure);
            Check(!File.Exists(download) && !File.Exists(download + ".partial"), "Invalid/partial download retained");
        }
        using (var missing = new UpdateClient(new MockHttp(r => new HttpResponseMessage(r.RequestUri!.AbsolutePath.EndsWith("/latest") ? HttpStatusCode.NotFound : HttpStatusCode.OK)), (_, _) => Task.FromResult<string?>(null)))
            Check(missing.CheckAsync("1.0.0", CancellationToken.None).GetAwaiter().GetResult() is null, "Accessible repository without releases");
        using (var denied = new UpdateClient(new MockHttp(_ => new HttpResponseMessage(HttpStatusCode.NotFound)), (_, _) => Task.FromResult<string?>(null)))
            Reject(() => denied.CheckAsync("1.0.0", CancellationToken.None).GetAwaiter().GetResult(), "Inaccessible repository reported as up to date");
        foreach (var unsafePath in new[] { "../escape.txt", "tools/../../escape.txt", "tools/CON.txt", "tools/a.", "tools/a ", "tools\\a", "Data/settings.json", "tools/x:stream" })
        {
            var zipPath = Path.Combine(folder, Guid.NewGuid() + ".zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("Cut-1.0.0-win-x64-portable/" + unsafePath).Open())) writer.Write("bad");
            Reject(() => UpdatePackage.Extract(zipPath, Path.Combine(folder, Guid.NewGuid().ToString()), "1.0.0"), "ZIP path " + unsafePath);
        }
        foreach (var kind in new[] { "duplicate", "symlink" })
        {
            var zipPath = Path.Combine(folder, kind + ".zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("Cut-1.0.0-win-x64-portable/tools/file");
                if (kind == "duplicate") zip.CreateEntry("Cut-1.0.0-win-x64-portable/tools/FILE");
                else entry.ExternalAttributes = unchecked((int)0xA1FF0000);
            }
            Reject(() => UpdatePackage.Extract(zipPath, Path.Combine(folder, kind), "1.0.0"), kind);
        }
        var layout = Path.Combine(folder, "layout"); ModernPayload(layout, "1.0.0", "good");
        Check(VersionLayout.Validate(layout) == "1.0.0", "Complete payload validation");
        File.WriteAllText(Path.Combine(layout, "tools", "unlisted.dll"), "not hashed");
        Reject(() => VersionLayout.Validate(layout), "Unlisted program component");
        var state = Path.Combine(folder, "atomic.json");
        DurableFiles.Write(state, new LauncherState("bootstrap", null)); DurableFiles.Write(state, new LauncherState("bootstrap", null, 1));
        File.WriteAllText(state, "{broken"); Check(DurableFiles.Read<LauncherState>(state).Revision == 0, "Atomic state backup");
    }
}
