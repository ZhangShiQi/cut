using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cut.Core;
using Cut.Services;

namespace Cut;

internal static class VisionTests
{
    private const string TestKey = "synthetic-vision-key-not-a-credential";
    private static AiSettings Config => new() { Endpoint = "https://vision.example/v1/chat/completions", Model = "test-vision", ApiKey = TestKey };
    private static void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); }
    private sealed class Handler : HttpMessageHandler
    {
        public int Count;
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Handle = (_, _) => Task.FromResult(Reply("示例回答"));
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Count++; return Handle(request, token); }
    }
    private sealed class Stub : IAiService
    {
        public Func<AiRequest, AiSettings, Func<string, Task>, CancellationToken, Task<AiCompletion>> Handle =
            async (_, _, progress, _) => { await progress("视觉分析示例"); return new(false); };
        public Task<AiCompletion> GenerateAsync(AiRequest request, AiSettings settings, Func<string, Task> onText, CancellationToken token) =>
            Handle(request, settings, onText, token);
    }
    private static HttpResponseMessage Reply(string text) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { index = 0, message = new { content = text }, finish_reason = "stop" } } }), Encoding.UTF8, "application/json") };
    private static async Task<string> Failure(Func<Task> action)
    {
        try { await action(); }
        catch (AiException ex) { Check(!ex.ToString().Contains(TestKey), "Safe errors"); return ex.Message; }
        throw new InvalidOperationException("Expected AI failure");
    }
    private static BitmapSource Decode(string dataUrl)
    {
        byte[] bytes = Convert.FromBase64String(dataUrl.Split(',', 2)[1]);
        using var memory = new MemoryStream(bytes);
        var frame = BitmapFrame.Create(memory, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad); frame.Freeze(); return frame;
    }
    private static byte[] Pixels(BitmapSource image)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgr32, null, 0);
        byte[] bytes = new byte[image.PixelWidth * image.PixelHeight * 4];
        converted.CopyPixels(bytes, image.PixelWidth * 4, 0); return bytes;
    }
    private static BitmapSource Solid(int width, int height, byte b, byte g, byte r)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = b; pixels[i + 1] = g; pixels[i + 2] = r; }
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4); source.Freeze(); return source;
    }
    private static void Preview(Window window, string file)
    {
        window.UpdateLayout(); var dpi = VisualTreeHelper.GetDpi(window);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(window); ExportService.SavePng(bitmap, file);
    }
    private static void Modal(Action open, Func<VisionResultWindow, Task> action)
    {
        bool visited = false; Exception? error = null;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        timer.Tick += async (_, _) =>
        {
            var popup = Application.Current.Windows.OfType<VisionResultWindow>().FirstOrDefault(w => w.IsVisible);
            if (popup is not null)
            {
                timer.Stop(); visited = true;
                try { await action(popup); }
                catch (Exception ex) { error = ex; popup.Close(); }
            }
            else if (elapsed.Elapsed > TimeSpan.FromSeconds(5))
            {
                timer.Stop(); error = new InvalidOperationException("Vision modal did not open");
                foreach (Window w in Application.Current.Windows.Cast<Window>().ToArray())
                    if (w is not OverlayWindow) w.Close();
            }
        };
        timer.Start();
        try { open(); } finally { timer.Stop(); }
        if (error is not null) throw error;
        Check(visited, "Visited vision dialog");
    }
    public static async Task<int> RunAsync()
    {
        string output = Path.Combine(Environment.CurrentDirectory, "artifacts"); Directory.CreateDirectory(output);
        var log = new StringBuilder("Cut vision tests: synthetic images and simulated model transport. No external model or real key.\n");
        try
        {
            var image = OcrTests.Render(new[] { "Application Error", "Error CS0246", "找不到类型或命名空间名称", "Check references and target framework." }, 28, false, 900);
            var encoded = await VisionImage.PrepareAsync(image);
            Check(!encoded.Resized && encoded.ByteCount <= VisionImage.MaxBytes && encoded.Source.IsFrozen, "Original pixels retained for ordinary screenshot");
            Check(Pixels(Decode(encoded.DataUrl)).SequenceEqual(Pixels(image)), "Lossless PNG encoding roundtrip");
            var wide = await VisionImage.PrepareAsync(Solid(6000, 100, 0, 200, 80));
            Check(wide.Resized && wide.Source.PixelWidth <= 4096 && wide.OriginalWidth == 6000, "Long edge limit and original size metadata");
            var thin = await VisionImage.PrepareAsync(Solid(1, 16000, 20, 30, 40));
            Check(thin.Source.PixelWidth == 1 && thin.Source.PixelHeight == 4096, "Very thin selection never rounds down to zero pixels");
            var random = new byte[2500 * 1800 * 3]; new Random(42).NextBytes(random);
            var noisy = BitmapSource.Create(2500, 1800, 96, 96, PixelFormats.Rgb24, null, random, 2500 * 3); noisy.Freeze();
            var small = await VisionImage.PrepareAsync(noisy);
            Check(small.Resized && small.ByteCount <= VisionImage.MaxBytes, "Encoded byte budget resizes noisy image");
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                try { await VisionImage.PrepareAsync(image, canceled.Token); throw new InvalidOperationException("Expected encode cancellation"); }
                catch (OperationCanceledException) { }
            }
            var snapshot = new DesktopSnapshot(new[]
            {
                new MonitorSnapshot(new PixelRect(-100, 0, 100, 100), Solid(100, 100, 0, 0, 255)),
                new MonitorSnapshot(new PixelRect(0, 0, 100, 100), Solid(100, 100, 255, 0, 0))
            });
            var cropped = await VisionImage.PrepareAsync(snapshot.Crop(new PixelRect(-20, 10, 40, 30)));
            byte[] crop = Pixels(Decode(cropped.DataUrl));
            Check(cropped.Source.PixelWidth == 40 && cropped.Source.PixelHeight == 30 && crop[2] == 255 && crop[20 * 4] == 255, "Negative coordinate cross-monitor exact crop");
            log.AppendLine("PASS lossless PNG, exact negative/cross-monitor selection, dimension and encoded byte limits, cancellation, no disk image staging");

            using var handler = new Handler(); using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            var service = new CompatibleAiService(client);
            foreach (AiTask task in new[] { AiTask.VisionExplain, AiTask.VisionInterface, AiTask.VisionChart, AiTask.VisionError, AiTask.Custom })
            {
                handler.Handle = async (request, token) =>
                {
                    Check(request.RequestUri!.ToString() == Config.Endpoint && request.Headers.Authorization?.Parameter == TestKey, "Visual endpoint/key");
                    using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                    var root = json.RootElement; var messages = root.GetProperty("messages");
                    string system = messages[0].GetProperty("content").GetString()!;
                    Check(root.GetProperty("model").GetString() == Config.Model && root.GetProperty("stream").GetBoolean(), "Visual model/stream");
                    Check(system.Contains("视觉助手") && !system.Contains("你没有看到图片"), "Vision-specific system prompt");
                    string expected = task switch { AiTask.VisionExplain => "各区域", AiTask.VisionInterface => "按钮", AiTask.VisionChart => "坐标轴", AiTask.VisionError => "报错弹窗", _ => "请解释蓝色区域" };
                    Check(system.Contains(expected), "Vision task: " + task);
                    var content = messages[1].GetProperty("content");
                    Check(content.GetArrayLength() == 2 && content[0].GetProperty("type").GetString() == "text" && content[1].GetProperty("type").GetString() == "image_url", "Text plus image_url content array");
                    string uri = content[1].GetProperty("image_url").GetProperty("url").GetString()!;
                    Check(uri == encoded.DataUrl && Pixels(Decode(uri)).SequenceEqual(Pixels(image)), "Only bounded selection PNG sent");
                    return Reply("看图回答");
                };
                var answer = new StringBuilder();
                await service.GenerateAsync(new("", task, "请解释蓝色区域", encoded), Config, value => { answer.Append(value); return Task.CompletedTask; }, default);
                Check(answer.ToString() == "看图回答", "Visual response");
            }
            int count = handler.Count;
            await Failure(() => service.GenerateAsync(new("", AiTask.VisionChart), Config, _ => Task.CompletedTask, default));
            await Failure(() => service.GenerateAsync(new("", AiTask.Custom, "", encoded), Config, _ => Task.CompletedTask, default));
            Check(count == handler.Count, "Missing image/question blocked before HTTP");
            foreach (int status in new[] { 400, 413, 415, 422 })
            {
                handler.Handle = (_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(TestKey) });
                string error = await Failure(() => service.GenerateAsync(new("", AiTask.VisionExplain, Image: encoded), Config, _ => Task.CompletedTask, default));
                Check(error.Contains("图片"), "Image-specific rejection guidance"); 
            }
            log.AppendLine("PASS five visual tasks, exact image_url data URL and pixels, visual prompt/model, empty OCR accepted, invalid input blocked and image rejection without text fallback");

            string configPath = Path.Combine(output, "vision-test-config.json");
            var store = new AiSettingsStore(configPath);
            store.Save(new() { Model = "vision-only", UseTextService = true, ApiKey = TestKey });
            var saved = store.Load();
            var inherited = saved.ResolveVision(() => new() { Endpoint = Config.Endpoint, ApiKey = "synthetic-text-key", Model = "text-only" });
            Check(inherited.Model == "vision-only" && inherited.ApiKey == "synthetic-text-key" && inherited.Endpoint == Config.Endpoint, "Inherit endpoint/key while keeping visual model");
            Check(saved.ApiKey == TestKey && !File.ReadAllText(configPath).Contains(TestKey), "Separate encrypted visual key");
            await Failure(() => { new AiSettings { UseTextService = true }.ResolveVision(() => Config); return Task.CompletedTask; });
            store.Save(Config); Check(store.Load().ResolveVision(() => throw new InvalidOperationException()).Model == Config.Model, "Independent visual service");
            log.AppendLine("PASS separate visual configuration, DPAPI storage, explicit service inheritance, independent model and missing visual model guidance");

            var stub = new Stub(); string copied = ""; int calls = 0; AiRequest? sent = null;
            stub.Handle = async (request, _, progress, _) =>
            {
                calls++; sent = request;
                await progress("画面内容：这是一个编译报错提示。\n\n");
                await progress("错误 CS0246 表示找不到引用的类型或命名空间。\n建议检查：\n1. 类型名称与 using 声明。\n2. 项目引用与 NuGet 依赖。\n3. 目标框架及条件编译。\n\n截图没有显示具体类型名称，需要结合完整错误列表确认。");
                return new(false);
            };
            VisionResultWindow Create() => new(image, (text, _) => { copied = text; return Task.CompletedTask; }, stub, () => Config);
            var window = Create(); window.Show(); await window.PreparedImage; await Task.Yield();
            Check(calls == 0, "Opening/preparing/previewing never sends");
            window.Assistant.SetTask(AiTask.VisionError); await window.Assistant.GenerateAsync();
            Check(sent!.Image is not null && sent.Text == "" && sent.Task == AiTask.VisionError, "UI sends image without OCR dependency");
            Preview(window, Path.Combine(output, "vision-preview.png"));
            window.Width = 820; window.Height = 560; window.Assistant.SetTask(AiTask.Custom, false, "这个报错应该先检查什么？");
            Preview(window, Path.Combine(output, "vision-compact-preview.png"));
            Check(window.Assistant.AnswerText.ActualHeight > 60, "Usable compact custom answer area");
            await window.Assistant.GenerateAsync(); Check(sent!.Question == "这个报错应该先检查什么？", "Custom visual question");
            window.Assistant.Answer = "人工校对的视觉分析 😀"; window.Assistant.SelectAnswer(0, 4); window.Assistant.CopyCommand(); await Task.Yield();
            Check(copied == "人工校对" && window.IsVisible, "Selected copy keeps window");
            await window.Assistant.CopyAsync(false); Check(window.ActionCompleted && copied.EndsWith("😀") && !window.IsVisible, "Edited full copy completes");
            foreach (string mode in new[] { "stop", "task", "question", "close", "newer" })
            {
                var gate = new TaskCompletionSource<AiCompletion>(); Func<string, Task>? update = null; CancellationToken observed = default;
                stub.Handle = (_, _, progress, token) => { update = progress; observed = token; return gate.Task; };
                var race = Create(); race.Show(); await race.PreparedImage;
                var pending = race.Assistant.GenerateAsync(); await Task.Yield(); await update!("部分回答");
                if (mode == "close") race.Close();
                else if (mode == "task") race.Assistant.SetTask(AiTask.VisionChart);
                else if (mode == "question") race.Assistant.SetTask(AiTask.Custom, false, "新问题");
                else race.Assistant.Stop();
                Check(observed.IsCancellationRequested, "Visual cancellation: " + mode);
                if (mode == "newer") { stub.Handle = async (_, _, p, _) => { await p("新的回答"); return new(false); }; await race.Assistant.GenerateAsync(); }
                string retained = race.Assistant.Answer; await update("旧片段"); gate.SetResult(new(false)); await pending;
                Check(race.Assistant.Answer == retained, "Discard late image result: " + mode);
                if (race.IsVisible) race.Close();
            }
            var preparing = new TaskCompletionSource<VisionImage>(); int unwanted = 0;
            stub.Handle = (_, _, _, _) => { unwanted++; return Task.FromResult(new AiCompletion(false)); };
            var panel = new AiAssistantPanel();
            panel.ConfigureVision(token => preparing.Task.WaitAsync(token), (_, _) => Task.CompletedTask, stub, () => Config);
            var preparingTask = panel.GenerateAsync(); panel.ClosePanel(); preparing.SetResult(encoded); await preparingTask;
            Check(unwanted == 0, "Close during preparation prevents upload");
            log.AppendLine("PASS visual UI no auto-send, custom tasks, preview/compact layout, editable Unicode copy, stop/task/question/close/newer invalidation and cancel-before-upload");

            store.Save(new());
            stub.Handle = async (request, config, progress, token) =>
            {
                Check(config.Model == Config.Model && request.Image is not null && request.Text == "", "Probe uses configured visual model and generated image");
                // The fake model reads ONLY pixels through the local OCR engine; no expected code is provided to it.
                var ocr = await OcrService.RecognizeAsync(request.Image!.Source, token);
                await progress(ocr.Text); return new(false);
            };
            var settings = new AiSettingsWindow(store, stub, true); settings.Show(); settings.SetFields(Config);
            await ((App)Application.Current).StartCaptureAsync();
            Check(!Application.Current.Windows.OfType<OverlayWindow>().Any(w => w.IsVisible), "Visual config capture guard");
            await settings.TestConnectionAsync();
            Check(settings.Status.Contains("图片测试通过") && !store.Load().IsConfigured, "Random image challenge passes only after reading pixels; no implicit save");
            Preview(settings, Path.Combine(output, "vision-settings-preview.png")); settings.SaveSettings(); Check(settings.WasSaved && store.Load().Model == Config.Model, "Visual config save");
            stub.Handle = async (_, _, progress, _) => { await progress("OK"); return new(false); };
            var rejected = new AiSettingsWindow(store, stub, true); rejected.Show(); await rejected.TestConnectionAsync();
            Check(rejected.Status.Contains("未通过"), "Generic text reply is not a visual test pass"); rejected.Close(); store.Save(new());
            log.AppendLine("PASS visual connection test uses randomized image challenge, fixed prompt without answer, no user screenshot; local OCR fake model passes, generic OK fails; explicit save and capture guard");

            var captureImage = Solid(320, 400, 210, 230, 240);
            var capture = new DesktopSnapshot(new[] { new MonitorSnapshot(new PixelRect(0, 0, 320, 400), captureImage) });
            foreach (string mode in new[] { "button-return", "shortcut-copy", "abort" })
            {
                var block = new TaskCompletionSource<AiCompletion>(); Func<string, Task>? progress = null; CancellationToken observed = default; Task? generating = null;
                stub.Handle = async (_, _, update, token) =>
                {
                    if (mode == "abort") { progress = update; observed = token; return await block.Task; }
                    await update("完整看图流程"); return new(false);
                };
                bool finished = false;
                var session = new CaptureSession(capture)
                {
                    RecognizeText = (_, _) => throw new InvalidOperationException("Vision must not call OCR"),
                    CreateVisionResultWindow = picture => new VisionResultWindow(picture, (value, _) => { copied = value; return Task.CompletedTask; }, stub, () => Config)
                };
                session.Completed += () => finished = true;
                session.Show();
                session.Interaction.Hover(new PixelPoint(60, 60));
                var owner = Application.Current.Windows.OfType<OverlayWindow>().First(w => w.IsVisible);
                if (mode == "button-return") { session.ConfirmPreview(); owner.UpdateLayout(); Preview(owner, Path.Combine(output, "vision-toolbar-compact-preview.png")); }
                Modal(() =>
                {
                    if (mode == "button-return") owner.AiButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    else owner.HandleKeyAsync(Key.I, ModifierKeys.Control).GetAwaiter().GetResult();
                }, async popup =>
                {
                    await popup.PreparedImage;
                    Check(session.Selection.Selection == capture.Bounds, "Shortcut confirms current window preview");
                    if (mode == "button-return") { popup.Close(); return; }
                    if (mode == "abort") { generating = popup.Assistant.GenerateAsync(); await Task.Yield(); session.Abort(); return; }
                    await popup.Assistant.GenerateAsync(); popup.Assistant.Answer += "（校对）"; await popup.Assistant.CopyAsync(false);
                });
                if (mode == "button-return")
                { Check(!session.IsFinished && !session.Busy && session.Selection.Selection == capture.Bounds, "Return preserves selection"); session.Abort(); }
                else if (mode == "abort")
                {
                    Check(observed.IsCancellationRequested, "Session abort cancels model");
                    await progress!("late"); block.SetResult(new(false)); await generating!;
                    Check(finished, "Session abort finished");
                }
                else Check(finished && copied == "完整看图流程（校对）", "Copy completes full capture session");
                Check(!Application.Current.Windows.OfType<OverlayWindow>().Any(w => w.IsVisible), "No overlay remains");
            }
            log.AppendLine("PASS actual toolbar click, Ctrl+I handler confirms preview, narrow toolbar wrapping, direct image path without OCR, return keeps selection, copy/abort close all overlays");

            stub.Handle = async (_, _, progress, _) => { await progress("从 OCR 原图进入"); return new(false); };
            var ocrWindow = new OcrResultWindow(image, new OcrResult(Array.Empty<OcrLine>()), (_, _) => Task.CompletedTask)
            {
                CreateVisionWindow = picture => new VisionResultWindow(picture, (text, _) => { copied = text; return Task.CompletedTask; }, stub, () => Config)
            };
            ocrWindow.Show();
            Modal(ocrWindow.OpenVision, async popup => { await popup.Assistant.GenerateAsync(); await popup.Assistant.CopyAsync(false); });
            Check(ocrWindow.ActionCompleted && !ocrWindow.IsVisible && copied == "从 OCR 原图进入", "OCR-to-vision completion propagates");
            log.AppendLine("PASS OCR original image entry and completion propagation");
            log.AppendLine("ALL LOCAL VISION CHECKS PASSED");
            log.AppendLine("LIVE VISION MODEL: NOT RUN. User will configure their service later. Actual visual accuracy, account connectivity and billing are unverified.");
            File.WriteAllText(Path.Combine(output, "vision-test.txt"), log.ToString()); return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(output, "vision-test.txt"), log + ex.ToString());
            foreach (Window window in Application.Current.Windows.Cast<Window>().ToArray()) window.Close();
            return 1;
        }
    }
}
