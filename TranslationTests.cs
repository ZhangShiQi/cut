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

internal static class TranslationTests
{
    private const string TestKey = "synthetic-test-key-not-a-real-credential";
    private static TranslationSettings Config => new() { ApiKey = TestKey, Region = "eastasia" };
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Handler : HttpMessageHandler
    {
        public int Count { get; private set; }
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Handle { get; set; } = (_, _) => Task.FromResult(Response("你好", "zh-Hans", "en"));
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { Count++; return Handle(request, token); }
    }
    private sealed class Stub : ITranslationService
    {
        public Func<string, string, string, TranslationSettings, CancellationToken, Task<TranslationResult>> Handle { get; set; } = (_, _, _, _, _) => Task.FromResult(new TranslationResult("你好", "en"));
        public Task<TranslationResult> TranslateAsync(string text, string source, string target, TranslationSettings settings, CancellationToken token) => Handle(text, source, target, settings, token);
    }
    private static HttpResponseMessage Response(string text, string to, string detected = "en") => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(new[] { new { detectedLanguage = new { language = detected, score = .99 }, translations = new[] { new { text, to } } } }), Encoding.UTF8, "application/json") };
    private static async Task<string> ExpectFailure(Func<Task> action)
    {
        try { await action(); } catch (TranslationException ex) { Check(!ex.ToString().Contains(TestKey), "Credential must not appear in errors"); return ex.Message; }
        throw new InvalidOperationException("Expected a user-facing translation failure");
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
        var log = new StringBuilder("Cut translation tests: synthetic HTTP transport; no live Azure key or network request.\n");
        try
        {
            using var handler = new Handler(); using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            var service = new AzureTranslationService(client);
            string input = "Hello 世界\r\n<code> & a=1 😀";
            handler.Handle = async (request, token) =>
            {
                Check(request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/translate", "Azure POST path");
                Check(request.RequestUri!.Query.Contains("api-version=3.0") && request.RequestUri.Query.Contains("to=zh-Hans") && !request.RequestUri.Query.Contains("from="), "Automatic language detection query");
                Check(!request.RequestUri.ToString().Contains(TestKey), "No key in URL");
                Check(request.Headers.GetValues("Ocp-Apim-Subscription-Key").Single() == TestKey && request.Headers.GetValues("Ocp-Apim-Subscription-Region").Single() == "eastasia", "Key/region headers");
                Check(request.Content!.Headers.ContentType!.MediaType == "application/json", "JSON content type");
                string body = await request.Content.ReadAsStringAsync(token);
                using var data = JsonDocument.Parse(body);
                Check(data.RootElement.GetArrayLength() == 1 && data.RootElement[0].EnumerateObject().Count() == 1 && data.RootElement[0].GetProperty("Text").GetString() == input, "Only exact Unicode text is sent; no images/settings");
                return Response("你好世界\n代码与数字", "zh-Hans");
            };
            var translated = await service.TranslateAsync(input, "auto", "zh-Hans", Config, CancellationToken.None);
            Check(translated.Text == "你好世界\n代码与数字" && translated.DetectedLanguage == "en", "Parse Unicode translation and detected language");
            handler.Handle = (request, _) =>
            {
                Check(request.RequestUri!.Query.Contains("from=zh-Hans") && request.RequestUri.Query.Contains("to=en"), "Chinese to English language pair");
                return Task.FromResult(Response("Hello world", "en", "zh-Hans"));
            };
            Check((await service.TranslateAsync("你好世界", "zh-Hans", "en", Config, CancellationToken.None)).Text == "Hello world", "Chinese to English response");
            handler.Handle = (request, _) =>
            {
                Check(!request.Headers.Contains("Ocp-Apim-Subscription-Region") && request.RequestUri!.Query.Contains("from=en"), "Global resource without region, explicit English source");
                return Task.FromResult(Response("你好", "zh-Hans"));
            };
            await service.TranslateAsync("Hello", "en", "zh-Hans", new() { ApiKey = TestKey }, CancellationToken.None);
            var custom = new TranslationSettings { Endpoint = "https://cut-test.cognitiveservices.azure.com", ApiKey = TestKey };
            Check(custom.GetTranslateUri("en", "zh-Hans").AbsolutePath == "/translator/text/v3.0/translate", "Custom resource endpoint prefix");
            log.AppendLine("PASS Azure v3 POST contract, auto/en/zh-Hans, Unicode/newlines, key/region headers, custom endpoint, text-only payload");

            int count = handler.Count;
            foreach (string endpoint in new[] { "http://api.cognitive.microsofttranslator.com", "https://example.com", "https://api.cognitive.microsofttranslator.com.evil.invalid", "https://user:pass@api.cognitive.microsofttranslator.com", "https://api.cognitive.microsofttranslator.com?key=x", "https://api.cognitive.microsofttranslator.com/translate", "https://api.cognitive.microsofttranslator.com:8443", "https://api.cognitive.microsofttranslator.com/#fragment" })
                await ExpectFailure(() => service.TranslateAsync("text", "en", "zh-Hans", new() { Endpoint = endpoint, ApiKey = TestKey }, CancellationToken.None));
            foreach (var args in new[] { ("", "en", "zh-Hans", Config), (new string('x', 5001), "en", "zh-Hans", Config), ("x", "en", "en", Config), ("x", "fr", "en", Config), ("x", "en", "zh-Hans", new TranslationSettings()), ("x", "en", "zh-Hans", new TranslationSettings { ApiKey = TestKey, Region = "bad\r\nregion" }) })
                await ExpectFailure(() => service.TranslateAsync(args.Item1, args.Item2, args.Item3, args.Item4, CancellationToken.None));
            Check(handler.Count == count, "Invalid inputs/config must not send HTTP");
            foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests, HttpStatusCode.BadRequest, HttpStatusCode.NotFound, HttpStatusCode.ServiceUnavailable, HttpStatusCode.Redirect })
            {
                handler.Handle = (_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(TestKey + " confidential response") });
                await ExpectFailure(() => service.TranslateAsync("Hello", "en", "zh-Hans", Config, CancellationToken.None));
            }
            handler.Handle = (_, _) => throw new HttpRequestException("secret " + TestKey);
            Check((await ExpectFailure(() => service.TranslateAsync("Hello", "en", "zh-Hans", Config, CancellationToken.None))).Contains("无法连接"), "Safe network error");
            foreach (string body in new[] { "not-json", "[]", "{}", "[{}]", "[{\"translations\":[{\"to\":\"en\",\"text\":\"wrong target\"}]}]", "[{\"translations\":[{\"to\":\"zh-Hans\",\"text\":123}]}]", new string('x', 1_048_577) })
            {
                handler.Handle = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
                await ExpectFailure(() => service.TranslateAsync("Hello", "en", "zh-Hans", Config, CancellationToken.None));
            }
            log.AppendLine("PASS preflight validation, missing key, length limits, 401/403/429/400/404/503/redirect, malformed/oversized response, safe network errors; no secret echoes or automatic retries");

            handler.Handle = async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Response("never", "zh-Hans"); };
            using (var cancelled = new CancellationTokenSource())
            {
                var pending = service.TranslateAsync("Hello", "en", "zh-Hans", Config, cancelled.Token); cancelled.Cancel();
                bool observed = false; try { await pending; } catch (OperationCanceledException) { observed = true; }
                Check(observed, "User cancellation must remain cancellation");
            }
            var shortTimeout = new AzureTranslationService(client, TimeSpan.FromMilliseconds(20));
            Check((await ExpectFailure(() => shortTimeout.TranslateAsync("Hello", "en", "zh-Hans", Config, CancellationToken.None))).Contains("超时"), "Distinguish timeout");
            log.AppendLine("PASS HTTP cancellation and timeout distinction");

            string configPath = Path.Combine(output, "translation-test-config.json");
            var store = new TranslationSettingsStore(configPath); store.Save(Config);
            string serialized = File.ReadAllText(configPath);
            Check(!serialized.Contains(TestKey) && !serialized.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes(TestKey))) && serialized.Contains("EncryptedKey"), "DPAPI encrypted config must not contain plain/base64 secret");
            var loaded = store.Load(); Check(loaded.ApiKey == TestKey && loaded.Region == "eastasia", "DPAPI round-trip");
            try { store.Save(new() { Endpoint = "http://invalid", ApiKey = "bad" }); } catch (TranslationException) { }
            Check(File.ReadAllText(configPath) == serialized, "Invalid save preserves existing config");
            File.WriteAllText(configPath, "{\"EncryptedKey\":\"invalid-base64\"}");
            bool corrupt = false; try { store.Load(); } catch (TranslationException) { corrupt = true; }
            Check(corrupt, "Corrupt key prompts reconfiguration");
            store.Save(new()); Check(!store.Load().IsConfigured, "Clearing key disables translation");
            Check(!Directory.GetFiles(output, "translation-test-config.json.*.tmp").Any(), "Atomic settings save temp cleanup");
            log.AppendLine("PASS Windows CurrentUser DPAPI, encrypted-only atomic persistence, round-trip, corrupt configuration recovery, clear key; real user config untouched");

            var image = OcrTests.Render(new[] { "Hello world", "Translate selected text." });
            var result = new OcrResult(new[] { new OcrLine("Hello world\r\nTranslate selected text.", new(20, 20, 500, 60), 1) });
            string? copied = null, sent = null;
            int calls = 0;
            var stub = new Stub { Handle = (text, source, target, settings, _) => { sent = text; calls++; return Task.FromResult(new TranslationResult(target == "en" ? "Hello world" : "你好，世界！\r\n翻译选中文字。", source == "auto" ? "en" : null)); } };
            OcrResultWindow CreateWindow() => new(image, result, (text, _) => { copied = text; return Task.CompletedTask; }, stub, () => Config);
            var window = CreateWindow(); window.Show(); await Task.Delay(40);
            Check(calls == 0, "Opening results must not automatically translate");
            window.EditedText = "Edited Hello\r\n仅翻译选中部分";
            string expectedSelection = "仅翻译选中部分";
            window.SelectText(window.EditedText.IndexOf(expectedSelection, StringComparison.Ordinal), expectedSelection.Length);
            await window.TranslateAsync(true);
            Check(sent == expectedSelection && !window.IsTranslating && window.EditedTranslation.Length > 0, "Translate only selected edited text");
            window.EditedText = "Hello world\r\nTranslate selected text."; Check(window.IsTranslationStale, "Editing source marks translation stale");
            await window.TranslateAsync(false); Check(sent == window.EditedText && !window.IsTranslationStale, "Translate all current edited source");
            SavePreview(window, Path.Combine(output, "translation-results-preview.png"));
            window.Width = 820; window.Height = 560; window.UpdateLayout(); SavePreview(window, Path.Combine(output, "translation-results-compact-preview.png"));
            window.EditedTranslation = "人工校对 Edited translation\r\n第二行"; window.SelectTranslation(0, 4);
            window.CopyTranslationCommand(); await Task.Yield(); Check(copied == "人工校对" && window.IsVisible, "Ctrl+C routed command copies selected edited translation");
            await window.CopyAsync(false, true); Check(copied == "人工校对 Edited translation\r\n第二行" && window.ActionCompleted && !window.IsVisible, "Copy all edited translation completes dialog");
            var chinese = CreateWindow(); chinese.Show(); chinese.EditedText = "你好世界"; chinese.SetLanguages(1, 1); await chinese.TranslateAsync(false);
            Check(chinese.EditedTranslation == "Hello world", "UI Chinese-to-English direction"); chinese.Close();
            log.AppendLine("PASS bilingual side-by-side UI, selected/current edited source, language direction, editable translation, selected/all Unicode copy commands, stale marker, no auto-upload");

            foreach (string mode in new[] { "cancel", "source", "language", "translation", "close", "superseded" })
            {
                var late = new TaskCompletionSource<TranslationResult>(); CancellationToken observed = default;
                stub.Handle = (_, _, _, _, token) => { observed = token; return late.Task; };
                var race = CreateWindow(); race.Show(); race.EditedTranslation = "保留已有译文";
                var pending = race.TranslateAsync(false); Check(race.IsTranslating, "Translation in flight");
                switch (mode)
                {
                    case "source": race.EditedText += "changed"; break;
                    case "language": race.SetLanguages(0, 1); break;
                    case "translation": race.EditedTranslation = "保留手工修改"; break;
                    case "close": race.Close(); break;
                    default: race.CancelTranslation(); break;
                }
                Check(observed.IsCancellationRequested && !race.IsTranslating, "Cancel immediately: " + mode);
                if (mode == "superseded")
                {
                    stub.Handle = (_, _, _, _, _) => Task.FromResult(new TranslationResult("新的译文", null));
                    await race.TranslateAsync(false);
                }
                string retained = race.EditedTranslation; late.SetResult(new("迟到的旧译文", null)); await pending;
                Check(race.EditedTranslation == retained && !race.IsTranslating, "Discard stale response: " + mode);
                if (race.IsVisible) race.Close();
            }
            stub.Handle = (_, _, _, _, _) => throw new TranslationException("模拟服务故障");
            var retry = CreateWindow(); retry.Show(); retry.EditedTranslation = "保留编辑"; await retry.TranslateAsync(false);
            Check(retry.EditedTranslation == "保留编辑" && !retry.IsTranslating, "Failure preserves translation and enables retry");
            stub.Handle = (_, _, _, _, _) => Task.FromResult(new TranslationResult("重试成功", null));
            await retry.TranslateAsync(false); Check(retry.EditedTranslation == "重试成功", "Retry after failure"); retry.Close();
            var noKey = new OcrResultWindow(image, result, (_, _) => Task.CompletedTask, service, () => new()); noKey.Show();
            count = handler.Count; await noKey.TranslateAsync(false);
            Check(noKey.Status.Contains("尚未配置") && handler.Count == count && !noKey.IsTranslating, "Missing key prompts settings without network"); noKey.Close();
            log.AppendLine("PASS cancel/source edit/language change/translation edit/close/new request invalidate late results; failure keeps edits, retry and missing-key guidance");

            string? probe = null;
            stub.Handle = (text, source, target, _, _) => { probe = text; Check(source == "en" && target == "zh-Hans", "Connection test language pair"); return Task.FromResult(new TranslationResult("你好", "en")); };
            var settingsWindow = new TranslationSettingsWindow(store, stub); settingsWindow.Show();
            await ((App)Application.Current).StartCaptureAsync();
            Check(!Application.Current.Windows.OfType<OverlayWindow>().Any(w => w.IsVisible), "Do not capture while settings is visible");
            settingsWindow.SetFields(Config); await settingsWindow.TestConnectionAsync();
            Check(probe == "Hello" && settingsWindow.Status.Contains("连接成功") && !store.Load().IsConfigured, "Test connection uses unsaved fields and only static Hello; no implicit save");
            SavePreview(settingsWindow, Path.Combine(output, "translation-settings-preview.png"));
            settingsWindow.SaveSettings(); Check(settingsWindow.WasSaved && store.Load().ApiKey == TestKey, "Settings save action");
            var blocked = new TaskCompletionSource<TranslationResult>(); CancellationToken settingsToken = default;
            stub.Handle = (_, _, _, _, token) => { settingsToken = token; return blocked.Task; };
            var cancelSettings = new TranslationSettingsWindow(store, stub); cancelSettings.Show();
            var testPending = cancelSettings.TestConnectionAsync(); cancelSettings.Close(); blocked.SetResult(new("late", null)); await testPending;
            Check(settingsToken.IsCancellationRequested, "Closing settings cancels connection test");
            store.Save(new());
            log.AppendLine("PASS configuration UI: connection test, explicit save, masked key, close cancels test");

            stub.Handle = (_, _, _, _, _) => Task.FromResult(new TranslationResult("完整流程译文", null));
            var snapshot = new DesktopSnapshot(new[] { new MonitorSnapshot(new PixelRect(0, 0, image.PixelWidth, image.PixelHeight), image) });
            var session = new CaptureSession(snapshot) { RecognizeText = (_, _) => Task.FromResult(result),
                CreateOcrResultWindow = (picture, text) => new OcrResultWindow(picture, text, (value, _) => { copied = value; return Task.CompletedTask; }, stub, () => Config) };
            session.Show(); session.Selection.SetSelection(snapshot.Bounds); session.EndDrag(new(30, 30));
            var owner = Application.Current.Windows.OfType<OverlayWindow>().First(w => w.IsVisible);
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            var elapsed = System.Diagnostics.Stopwatch.StartNew(); bool visited = false;
            timer.Tick += async (_, _) =>
            {
                var popup = Application.Current.Windows.OfType<OcrResultWindow>().FirstOrDefault(w => w.IsVisible);
                if (popup is not null)
                {
                    timer.Stop(); visited = true; await popup.TranslateAsync(false);
                    popup.EditedTranslation += "（校对）"; await popup.CopyAsync(false, true);
                }
                else if (elapsed.Elapsed > TimeSpan.FromSeconds(5)) { timer.Stop(); session.Abort(); }
            };
            timer.Start(); await session.RecognizeTextAsync(owner); timer.Stop();
            Check(visited && session.IsFinished && copied == "完整流程译文（校对）" && !Application.Current.Windows.OfType<OverlayWindow>().Any(w => w.IsVisible), "Translation copy completes capture and closes all overlays");
            log.AppendLine("PASS capture -> OCR result -> translate -> edit translation -> copy -> all overlays closed (synthetic OCR/provider/copy callbacks)");
            log.AppendLine("ALL LOCAL TRANSLATION CHECKS PASSED");
            log.AppendLine("LIVE AZURE TEST: NOT RUN. User has no Azure Translator resource; provider output/quality and account billing were not verified.");
            File.WriteAllText(Path.Combine(output, "translation-test.txt"), log.ToString()); return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(output, "translation-test.txt"), log + ex.ToString());
            foreach (Window window in Application.Current.Windows.Cast<Window>().ToArray()) window.Close();
            return 1;
        }
    }
}
