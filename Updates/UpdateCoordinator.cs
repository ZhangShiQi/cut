using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Cut.UpdateEngine;

namespace Cut.Updates;

public sealed record UpdatePreferences(bool Automatic = true);

public sealed class UpdateCoordinator : INotifyPropertyChanged, IDisposable
{
    private readonly UpdateClient client;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly Func<bool> canRestart;
    private readonly Action exit;
    private CancellationTokenSource? active;
    private string? package;
    private bool disposed;
    private DateTimeOffset nextCheck = DateTimeOffset.UtcNow.AddSeconds(30 + Random.Shared.Next(15));
    private string SettingsPath => Path.Combine(UpdatePaths.Data, "device", "update-settings.json");
    private string CacheRoot => Path.Combine(UpdatePaths.Data, "updates", new LauncherStore(LauncherContext.Root!).MutexKey.Split('.').Last());
    private string ReadyPath => Path.Combine(CacheRoot, "ready.json");
    private sealed record CachedPackage(string Version, string Sha256, string Package);
    public string CurrentVersion => "Cut " + UpdateRelease.CurrentVersion;
    public bool Automatic { get; private set; } = true;
    public bool IsBusy { get; private set; }
    public bool CanCheck => !IsBusy;
    public bool CanCancel => IsBusy;
    public bool CanInstall => !IsBusy && Available is not null && package is not null && SupportsInstall;
    public bool SupportsInstall => LauncherContext.Root is not null;
    public UpdateRelease? Available { get; private set; }
    public string Status { get; private set; } = "等待检查更新";
    public string Details { get; private set; } = "后台检查和下载正式版本；由你决定何时重启安装。";
    public string ReleaseNotes => Available?.Notes ?? "";
    public event PropertyChangedEventHandler? PropertyChanged;
    public UpdateCoordinator(Func<bool> canRestart, Action exit, UpdateClient? client = null)
    {
        this.canRestart = canRestart; this.exit = exit; this.client = client ?? new UpdateClient();
        try
        {
            if (DurableFiles.Exists(SettingsPath)) Automatic = DurableFiles.Read<UpdatePreferences>(SettingsPath).Automatic;
            var result = Path.Combine(UpdatePaths.Data, "device", "update-result.json");
            if (DurableFiles.Exists(result))
            {
                var saved = DurableFiles.Read<UpdateResult>(result);
                Status = saved.Success ? "上次更新成功" : "上次更新已回退或未完成";
                Details = saved.Message;
            }
        }
        catch (Exception ex) { Automatic = false; Status = "更新设置无法读取"; Details = ex.Message; }
        timer.Tick += async (_, _) => { if (Automatic && !IsBusy && DateTimeOffset.UtcNow >= nextCheck) await CheckAsync(); };
    }
    private sealed record UpdateResult(bool Success, string Message);
    public void Start() { if (!disposed) timer.Start(); }
    public void SetAutomatic(bool value)
    {
        DurableFiles.Write(SettingsPath, new UpdatePreferences(value)); Automatic = value;
        if (!value) active?.Cancel();
        nextCheck = DateTimeOffset.UtcNow.AddSeconds(30); Notify();
    }
    public void Cancel() => active?.Cancel();
    public async Task CheckAsync()
    {
        if (disposed || IsBusy) return;
        IsBusy = true; active = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        var token = active.Token;
        Status = "正在检查更新…"; Notify();
        var failed = false;
        try
        {
            var release = await client.CheckAsync(UpdateRelease.CurrentVersion, token);
            if (release is null) { Available = null; package = null; Status = "当前没有更新的正式版本"; Details = "仅检查官方正式版；尚无发布时也显示此状态。"; return; }
            if (Available == release && package is not null && File.Exists(package)) { Status = "新版本已就绪"; return; }
            Available = release; package = null;
            if (!SupportsInstall) { Status = "发现新版本 " + release.Version; Details = "源码运行仅检查版本；完整便携包支持安装更新。"; return; }
            if (LauncherStore.IsRejected(LauncherContext.Root!, release.Sha256)) { Status = "此更新包曾启动失败"; Details = "已保留可用版本，相同更新包不会重复安装。"; return; }
            CachedPackage? cached = null;
            try { if (DurableFiles.Exists(ReadyPath)) cached = DurableFiles.Read<CachedPackage>(ReadyPath); }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { }
            if (cached is not null && cached.Version == release.Version && cached.Sha256 == release.Sha256 &&
                UpdatePackage.IsWithin(cached.Package, CacheRoot) && File.Exists(cached.Package))
            {
                try
                {
                    await UpdatePackage.VerifyHashAsync(cached.Package, release.Sha256, token);
                    package = cached.Package; Status = "新版本 " + release.Version + " 已就绪";
                    Details = "已复用并重新校验下载缓存，点击“重启并更新”安装。"; return;
                }
                catch (InvalidDataException) { DurableFiles.Delete(ReadyPath); }
            }
            var directory = Path.Combine(CacheRoot, release.Version + "-" + Guid.NewGuid().ToString("N"));
            var download = Path.Combine(directory, release.PackageName);
            Status = "正在下载 " + release.Version; Notify();
            await client.DownloadAsync(release, download, new Progress<double>(p => { if (!disposed) { Details = $"已下载 {p:0}% · 截图功能可继续使用。"; Notify(); } }), token);
            await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var verified = Path.Combine(directory, "verified");
                UpdatePackage.Extract(download, verified, release.Version);
                VersionLayout.Validate(verified, release.Version);
                Directory.Delete(verified, true);
            }, token);
            token.ThrowIfCancellationRequested(); package = download;
            DurableFiles.Write(ReadyPath, new CachedPackage(release.Version, release.Sha256, download));
            Status = "新版本 " + release.Version + " 已就绪";
            Details = "完成当前编辑后，点击“重启并更新”。普通退出不会自动安装或重新启动。";
        }
        catch (OperationCanceledException) { Status = "更新已取消或超时"; Details = "当前版本保持可用，可以稍后重试。"; }
        catch (Exception ex) { failed = true; Status = "暂时无法更新"; Details = ex.Message + " 当前版本保持可用。"; }
        finally
        {
            IsBusy = false; active.Dispose(); active = null;
            nextCheck = DateTimeOffset.UtcNow.Add(failed ? TimeSpan.FromMinutes(15 + Random.Shared.Next(10)) : TimeSpan.FromHours(6) + TimeSpan.FromMinutes(Random.Shared.Next(20)));
            if (disposed) client.Dispose(); Notify();
        }
    }
    public void Install()
    {
        if (!CanInstall) throw new InvalidOperationException("更新包尚未就绪。");
        if (!canRestart()) throw new InvalidOperationException("请先完成或关闭截图、长图编辑、识别结果、AI 对话和服务设置，再重启更新。");
        using var process = Process.GetCurrentProcess();
        LauncherStore.Queue(new UpdateRequest(LauncherContext.Root!, UpdatePaths.Data, package!, Available!.Sha256,
            Available.Version, process.Id, process.StartTime.ToUniversalTime().Ticks, Startup: true));
        package = null; exit();
    }
    private void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    public void Dispose()
    {
        disposed = true; timer.Stop(); active?.Cancel(); if (active is null) client.Dispose();
    }
}
