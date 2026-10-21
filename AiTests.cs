using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cut.Core;
using Cut.Services;

namespace Cut;

internal static class AiTests
{
    private const string TestKey = "synthetic-ai-key-not-a-real-credential";
    private static AiSettings Config => new() { Endpoint = "https://model.example/v1/chat/completions", Model = "test-model", ApiKey = TestKey };
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Handler : HttpMessageHandler
    {
        public int Count;
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Handle = (_, _) => Task.FromResult(Sse(Chunk("你好") + Finish()));
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Count++; return Handle(request, token); }
    }
    private sealed class Stub : IAiService
    {
        public Func<AiRequest, AiSettings, Func<string, Task>, CancellationToken, Task<AiCompletion>> Handle =
            async (_, _, progress, _) => { await progress("示例回答"); return new(false); };
        public Task<AiCompletion> GenerateAsync(AiRequest request, AiSettings settings, Func<string, Task> onText, CancellationToken token) =>
            Handle(request, settings, onText, token);
    }
    // Force byte boundaries inside UTF-8 characters and CRLF pairs.
    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, 3)], token);
    }
    private sealed class HangingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { await Task.Delay(Timeout.Infinite, token); return 0; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private static string Chunk(string text) => "data: " + JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta = new { content = text }, finish_reason = (string?)null } } }) + "\r\n\r\n";
    private static string Finish(string reason = "stop") => "data: " + JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta = new { }, finish_reason = reason } } }) + "\n\ndata: [DONE]\n\n";
    private static HttpResponseMessage Sse(string value, bool fragmented = false)
    {
        HttpContent content = fragmented ? new StreamContent(new FragmentedStream(Encoding.UTF8.GetBytes(value))) : new StringContent(value, Encoding.UTF8);
        content.Headers.ContentType = new("text/event-stream");
        return new(HttpStatusCode.OK) { Content = content };
    }
    private static async Task<string> Failure(Func<Task> action)
    {
        try { await action(); }
        catch (AiException ex) { Check(!ex.ToString().Contains(TestKey), "Safe error must not expose credentials"); return ex.Message; }
        throw new InvalidOperationException("Expected an AI failure");
    }
    private static void SavePreview(Window window, string path)
    {
        window.UpdateLayout(); var dpi = VisualTreeHelper.GetDpi(window);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(window); ExportService.SavePng(bitmap, path);
    }
    public static async Task<int> RunAsync()
    {
        string output = Path.Combine(Environment.CurrentDirectory, "artifacts"); Directory.CreateDirectory(output);
        var log = new StringBuilder("Cut AI tests: simulated model transport; no real credentials or external network.\n");
        try
        {
            using var handler = new Handler(); using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            var service = new CompatibleAiService(client);
            var answer = new StringBuilder();
            Task<AiCompletion> Generate(AiRequest? request = null, AiSettings? config = null, CancellationToken token = default) =>
                service.GenerateAsync(request ?? new("截图原文", AiTask.Explain), config ?? Config, part => { answer.Append(part); return Task.CompletedTask; }, token);
            string input = "Error CS0246\r\n找不到类型 😀 <code> & a=1";
            handler.Handle = async (message, token) =>
            {
                Check(message.Method == HttpMethod.Post && message.RequestUri!.ToString() == Config.Endpoint, "Exact full endpoint");
                Check(message.Headers.Authorization?.Scheme == "Bearer" && message.Headers.Authorization.Parameter == TestKey, "Bearer authentication");
                Check(!message.RequestUri!.ToString().Contains(TestKey), "No key in URI");
                using var body = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
                var root = body.RootElement;
                Check(root.GetProperty("model").GetString() == "test-model" && root.GetProperty("stream").GetBoolean(), "Model and streaming payload");
                var messages = root.GetProperty("messages");
                Check(messages.GetArrayLength() == 2 && messages[1].GetProperty("content").GetString() == input, "Only exact selected Unicode source as user content");
                Check(messages[0].GetProperty("content").GetString()!.Contains("解释报错"), "Error explanation preset");
                Check(!root.TryGetProperty("tools", out _) && !root.TryGetProperty("images", out _), "No tools or images");
                return Sse(":heartbeat\r\n\r\n" + Chunk("你好😀") + "data: {\"choices\":\ndata: [{\"index\":0,\"delta\":{\"content\":\"\\nEnglish\"}}]}\n\n" +
                    "data: {\"choices\":[]}\n\n" + Finish(), true);
            };
            await Generate(new(input, AiTask.Error));
            Check(answer.ToString() == "你好😀\nEnglish", "Fragmented UTF8, CRLF, SSE multiline, empty usage and DONE");
            log.AppendLine("PASS POST endpoint/model/Bearer contract; exact Unicode source; text-only payload; fragmented UTF8, SSE CRLF/comments/multiline/usage/DONE");
            handler.Handle = (_, _) => Task.FromResult(Sse(Chunk("部分回答") + Finish("length")));
            Check((await Generate()).Truncated, "Truncated finish reason");
            handler.Handle = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"index\":0,\"message\":{\"content\":\"JSON 回答\"},\"finish_reason\":\"stop\"}]}", Encoding.UTF8, "application/json")
            });
            answer.Clear(); await Generate(); Check(answer.ToString() == "JSON 回答", "Nonstreaming compatible JSON fallback");
            handler.Handle = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"incomplete\"}}]}", Encoding.UTF8, "application/json") });
            await Failure(() => Generate());
            handler.Handle = (_, _) => Task.FromResult(Sse("data: {\"choices\":[{\"index\":0,\"delta\":{\"refusal\":\"无法回答\"}}]}\n\n" + Finish()));
            answer.Clear(); await Generate(); Check(answer.ToString() == "无法回答", "Refusal text");
            foreach (AiTask task in Enum.GetValues<AiTask>())
            {
                handler.Handle = async (message, token) =>
                {
                    string payload = await message.Content!.ReadAsStringAsync(token);
                    using var document = JsonDocument.Parse(payload);
                    string prompt = document.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
                    string expected = task switch { AiTask.Explain => "关键术语", AiTask.Error => "排查步骤", AiTask.Summarize => "摘要", AiTask.Polish => "润色", _ => "请列出数字" };
                    Check(prompt.Contains(expected), "Preset prompt: " + task);
                    return Sse(Chunk("回答") + Finish());
                };
                await Generate(new("input", task, "请列出数字"));
            }
            log.AppendLine("PASS five preset/custom prompts, JSON fallback, refusal text, length truncation flag");
            int count = handler.Count;
            await Failure(() => Generate(config: new()));
            await Failure(() => Generate(config: new() { Model = "test-model" }));
            await Failure(() => Generate(new("", AiTask.Explain)));
            await Failure(() => Generate(new(new string('a', 20001), AiTask.Summarize)));
            await Failure(() => Generate(new("text", AiTask.Custom, "")));
            await Failure(() => Generate(new("text", AiTask.Custom, new string('q', 2001))));
            foreach (string endpoint in new[] { "http://remote.example/v1/chat/completions", "https://user:pass@example.com", "https://example.com?key=secret", "file:///x", "https://example.com/#key" })
                await Failure(() => Generate(config: new() { Endpoint = endpoint, Model = "test", ApiKey = TestKey }));
            Check(handler.Count == count, "Validation before network");
            handler.Handle = (message, _) =>
            {
                Check(message.Headers.Authorization is null && message.RequestUri!.Host == "localhost", "Explicit loopback without authentication");
                return Task.FromResult(Sse(Chunk("本地") + Finish()));
            };
            await Generate(config: new() { Endpoint = "http://localhost:1234/v1/chat/completions", Model = "local-model" });
            log.AppendLine("PASS missing config/key, unsafe endpoint, blank/long text/question validation without network; explicit HTTP loopback without key");
            foreach (int status in new[] { 400, 401, 403, 404, 429, 500, 503, 302 })
            {
                handler.Handle = (_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(TestKey) });
                await Failure(() => Generate());
            }
            handler.Handle = (_, _) => throw new HttpRequestException(TestKey);
            await Failure(() => Generate());
            foreach (string response in new[]
            {
                "data: {broken}\n\n", Chunk("断开"), "data: {\"error\":{\"message\":\"" + TestKey + "\"}}\n\n",
                "data: [DONE]\n\n", Chunk("部分") + Finish("tool_calls"), Chunk("部分") + Finish("content_filter"),
                "data: " + new string('x', 131073), Chunk(new string('x', 64001)) + Finish()
            })
            {
                handler.Handle = (_, _) => Task.FromResult(Sse(response));
                await Failure(() => Generate());
            }
            handler.Handle = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>bad</html>", Encoding.UTF8, "text/html") });
            await Failure(() => Generate());
            handler.Handle = (_, _) =>
            {
                var content = new StreamContent(new HangingStream()); content.Headers.ContentType = new("text/event-stream");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            };
            using (var cancel = new CancellationTokenSource(40))
            {
                try { await Generate(token: cancel.Token); throw new InvalidOperationException("Expected cancellation"); }
                catch (OperationCanceledException) { Check(cancel.IsCancellationRequested, "Read cancellation"); }
            }
            var quick = new CompatibleAiService(client, TimeSpan.FromMilliseconds(40));
            Check((await Failure(() => quick.GenerateAsync(new("text", AiTask.Explain), Config, _ => Task.CompletedTask, default))).Contains("停止等待"), "Read timeout mapped");
            log.AppendLine("PASS HTTP auth/rate-limit/server/redirect errors, safe errors, malformed/empty/oversized/interrupted streams, unsupported tool/filter results, cancellation and stalled-read timeout");

            string settingsPath = Path.Combine(output, "ai-test-config.json");
            var store = new AiSettingsStore(settingsPath); store.Save(new()); store.Save(Config);
            Check(store.Load().ApiKey == TestKey && store.Load().Model == Config.Model && !File.ReadAllText(settingsPath).Contains(TestKey), "DPAPI encrypted roundtrip");
            await Failure(() => { store.Save(new() { Endpoint = "http://remote.example", ApiKey = TestKey }); return Task.CompletedTask; });
            Check(store.Load().ApiKey == TestKey, "Invalid save preserves config");
            File.WriteAllText(settingsPath, "{\"Version\":99}"); await Failure(() => { store.Load(); return Task.CompletedTask; });
            store.Save(new()); Check(store.Load().ApiKey == "", "Recovery and key clearing");
            log.AppendLine("PASS isolated settings DPAPI roundtrip, no plaintext key, invalid save preservation, corrupt config recovery and key removal");

            var image = OcrTests.Render(new[] { "Error CS0246", "找不到类型或命名空间名称", "Check your project references." });
            var result = new OcrResult(new[] { new OcrLine("Error CS0246\n找不到类型或命名空间名称\nCheck your project references.", new PixelRect(0, 0, 900, 120), .99f) });
            var stub = new Stub(); string copied = "", sent = ""; AiTask sentTask = AiTask.Explain;
            OcrResultWindow Create() => new(image, result, (text, _) => { copied = text; return Task.CompletedTask; }, aiService: stub, loadAiSettings: () => Config);
            int requests = 0;
            stub.Handle = async (request, _, progress, _) =>
            {
                requests++; sent = request.Text; sentTask = request.Task;
                await progress("错误含义：编译器无法找到引用的类型。\n");
                await progress("排查步骤：\n1. 核对类型名称与命名空间。\n2. 检查项目引用和 NuGet 依赖。\n3. 确认目标框架与条件编译设置。\n\n这些建议基于识别文字；请结合项目代码核对。");
                return new(false);
            };
            var window = Create(); window.Show(); window.ShowAi();
            Check(requests == 0, "No automatic submission on open/tab switch");
            window.Assistant.SetTask(AiTask.Error); await window.Assistant.GenerateAsync();
            Check(sent == result.Text && sentTask == AiTask.Error && window.Assistant.Answer.Contains("NuGet"), "Current full source and error preset");
            SavePreview(window, Path.Combine(output, "ai-preview.png"));
            window.Width = 820; window.Height = 560; SavePreview(window, Path.Combine(output, "ai-compact-preview.png"));
            window.Assistant.SetTask(AiTask.Custom, false, "请解释这个错误，并列出需要检查的项目配置。");
            SavePreview(window, Path.Combine(output, "ai-custom-compact-preview.png"));
            Check(window.Assistant.AnswerText.ActualHeight >= 60, "Custom question keeps a usable answer area in compact layout");
            window.EditedText = "校对后的原文 selected text";
            Check(window.Assistant.IsStale, "Original edit marks answer stale");
            window.SelectText(7, 8); window.Assistant.SetTask(AiTask.Summarize, true);
            await window.Assistant.GenerateAsync(); Check(sent == window.EditedText.Substring(7, 8) && sentTask == AiTask.Summarize, "Only selection is sent");
            window.SelectText(0, 0); int previous = requests; await window.Assistant.GenerateAsync();
            Check(requests == previous && window.Assistant.Status.Contains("选中"), "Empty selection does not fall back to all");
            window.Assistant.Answer = "人工校对 😀\nEnglish"; window.Assistant.SelectAnswer(0, 4);
            window.Assistant.CopyCommand(); await Task.Yield(); Check(copied == "人工校对" && window.IsVisible, "Copy edited selection keeps dialog");
            await window.Assistant.CopyAsync(false); Check(copied == "人工校对 😀\nEnglish" && window.ActionCompleted && !window.IsVisible, "Copy all completes");
            log.AppendLine("PASS AI tab/presets/full and selected edited source/no auto-upload/empty selection; editable Unicode answer, routed copy selection and completion; normal/compact UI snapshots");

            foreach (string mode in new[] { "stop", "source", "selection", "task", "question", "answer", "close", "newer" })
            {
                var gate = new TaskCompletionSource<AiCompletion>(); Func<string, Task>? progress = null; CancellationToken observed = default;
                stub.Handle = (_, _, update, token) => { progress = update; observed = token; return gate.Task; };
                var race = Create(); race.Show(); race.ShowAi(); race.Assistant.Answer = "保留旧内容"; race.SelectText(0, 5);
                race.Assistant.SetTask(AiTask.Explain, mode == "selection");
                var pending = race.Assistant.GenerateAsync(); Check(race.Assistant.IsGenerating, "In-flight UI state");
                await progress!("部分新回答");
                switch (mode)
                {
                    case "source": race.EditedText += " changed"; break;
                    case "selection": race.SelectText(0, 2); break;
                    case "task": race.Assistant.SetTask(AiTask.Polish); break;
                    case "question": race.Assistant.SetTask(AiTask.Custom, false, "新问题"); break;
                    case "answer": race.Assistant.Answer = "手工修改"; break;
                    case "close": race.Close(); break;
                    default: race.Assistant.Stop(); break;
                }
                Check(observed.IsCancellationRequested && !race.Assistant.IsGenerating, "Cancel immediately: " + mode);
                if (mode == "newer")
                {
                    stub.Handle = async (_, _, update, _) => { await update("新的回答"); return new(false); };
                    await race.Assistant.GenerateAsync();
                }
                string retained = race.Assistant.Answer;
                await progress("迟到旧片段"); gate.SetResult(new(false)); await pending;
                Check(race.Assistant.Answer == retained, "Stale fragment/completion discarded: " + mode);
                if (race.IsVisible) race.Close();
            }
            stub.Handle = (_, _, _, _) => throw new AiException("模拟故障");
            var failed = Create(); failed.Show(); failed.Assistant.Answer = "已有编辑";
            await failed.Assistant.GenerateAsync(); Check(failed.Assistant.Answer == "已有编辑" && !failed.Assistant.IsGenerating, "Failure before first token preserves edits");
            stub.Handle = async (_, _, update, _) => { await update("保留部分"); throw new AiException("连接提前结束"); };
            await failed.Assistant.GenerateAsync(); Check(failed.Assistant.Answer == "保留部分" && failed.Assistant.Status.Contains("部分"), "Failure keeps partial answer and status");
            stub.Handle = async (_, _, update, _) => { await update("重试成功"); return new(false); };
            await failed.Assistant.GenerateAsync(); Check(failed.Assistant.Answer == "重试成功", "Regenerate after failure"); failed.Close();
            log.AppendLine("PASS stop/source/selection/task/question/edit/close/new generation reject late chunks; failure preserves old or partial output; retry");

            string probe = "";
            stub.Handle = async (request, config, progress, _) =>
            { probe = request.Text; Check(config.ApiKey == TestKey, "Unsaved settings passed to connection test"); await progress("OK"); return new(false); };
            var configWindow = new AiSettingsWindow(store, stub); configWindow.Show();
            await ((App)Application.Current).StartCaptureAsync();
            Check(!Application.Current.Windows.OfType<OverlayWindow>().Any(w => w.IsVisible), "Do not capture credentials settings");
            configWindow.SetFields(Config); await configWindow.TestConnectionAsync();
            Check(probe == "Hello" && configWindow.Status.Contains("连接成功") && !store.Load().IsConfigured, "Fixed test input, explicit save required");
            SavePreview(configWindow, Path.Combine(output, "ai-settings-preview.png"));
            configWindow.SaveSettings(); Check(configWindow.WasSaved && store.Load().ApiKey == TestKey, "Settings save action");
            var block = new TaskCompletionSource<AiCompletion>(); CancellationToken configToken = default;
            stub.Handle = (_, _, _, token) => { configToken = token; return block.Task; };
            var closeConfig = new AiSettingsWindow(store, stub); closeConfig.Show();
            var testing = closeConfig.TestConnectionAsync(); closeConfig.Close(); block.SetResult(new(false)); await testing;
            Check(configToken.IsCancellationRequested, "Closing config cancels connection test"); store.Save(new());
            log.AppendLine("PASS settings UI: masked key, unsaved fixed connection probe, explicit save, capture guard and cancellation on close");

            stub.Handle = async (_, _, update, _) => { await update("完整流程 AI 回答"); return new(false); };
            var snapshot = new DesktopSnapshot(new[] { new MonitorSnapshot(new PixelRect(0, 0, image.PixelWidth, image.PixelHeight), image) });
            var session = new CaptureSession(snapshot)
            {
                RecognizeText = (_, _) => Task.FromResult(result),
                CreateOcrResultWindow = (picture, text) => new OcrResultWindow(picture, text, (value, _) => { copied = value; return Task.CompletedTask; }, aiService: stub, loadAiSettings: () => Config)
            };
            session.Show(); session.Selection.SetSelection(snapshot.Bounds); session.EndDrag(new(30, 30));
            var owner = Application.Current.Windows.OfType<OverlayWindow>().First(w => w.IsVisible);
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            var elapsed = System.Diagnostics.Stopwatch.StartNew(); bool visited = false;
            timer.Tick += async (_, _) =>
            {
                var popup = Application.Current.Windows.OfType<OcrResultWindow>().FirstOrDefault(w => w.IsVisible);
                if (popup is not null)
                {
                    timer.Stop(); visited = true; popup.ShowAi(); await popup.Assistant.GenerateAsync();
                    popup.Assistant.Answer += "（校对）"; await popup.Assistant.CopyAsync(false);
                }
                else if (elapsed.Elapsed > TimeSpan.FromSeconds(5)) { timer.Stop(); session.Abort(); }
            };
            timer.Start(); await session.RecognizeTextAsync(owner); timer.Stop();
            Check(visited && session.IsFinished && copied == "完整流程 AI 回答（校对）" &&
                !Application.Current.Windows.OfType<OverlayWindow>().Any(w => w.IsVisible), "AI completion closes capture overlays");
            log.AppendLine("PASS capture -> OCR -> AI -> edit -> copy -> close overlays with synthetic callbacks");
            log.AppendLine("ALL LOCAL AI CHECKS PASSED");
            log.AppendLine("LIVE MODEL TEST: NOT RUN. No real provider credentials supplied; actual account connectivity and answer quality unverified.");
            File.WriteAllText(Path.Combine(output, "ai-test.txt"), log.ToString()); return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(output, "ai-test.txt"), log + ex.ToString());
            foreach (Window window in Application.Current.Windows.Cast<Window>().ToArray()) window.Close();
            return 1;
        }
    }
}
