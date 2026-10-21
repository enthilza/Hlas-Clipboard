using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NAudio.Wave;

namespace Hlas;
internal static class SelfTest
{
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

    static void VerifyPlayerFrame(Action<bool, string> check, List<string> skipped)
    {
        var window = new PlayerWindow((App)Application.Current);
        var handle = new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
        try {
            foreach (bool light in new[] { false, true, false }) {
                Theme.Apply(light);
                int cornerResult = DwmGetWindowAttribute(handle, 33, out int corners, 4);
                int borderResult = DwmGetWindowAttribute(handle, 34, out int border, 4);
                if (cornerResult != 0 || borderResult != 0) { skipped.Add("DWM frame attributes unavailable on this Windows version"); return; }
                check(corners == 1 && border == unchecked((int)0xFFFFFFFE), "Player has no DWM border or rounding after theme switch: " + (light ? "light" : "dark"));
            }
        } finally {
            // Closing is normally intercepted to hide the player. Destroy this test HWND explicitly.
            System.Windows.Interop.HwndSource.FromHwnd(handle)?.Dispose();
        }
    }
    sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => callback(request, token);
    }
    sealed class FakeApi : IApi
    {
        internal readonly List<string> Spoken = new();
        internal int Preparations;
        internal Func<CancellationToken, Task>? BeforeSpeak;
        public Task<string> Prepare(string input, Settings s, string key, Action<Bill> billed, CancellationToken token)
        { Interlocked.Increment(ref Preparations); billed(new Bill("Text", s.TextModel, "fake") { Cost = .001m }); return Task.FromResult(input); }
        public async Task<AudioData> Speak(string input, Settings s, string key, CancellationToken token)
        {
            lock (Spoken) Spoken.Add(input);
            if (BeforeSpeak is not null) await BeforeSpeak(token);
            return new(Api.WrapPcm(new byte[2400], 24000, 1), "wav", new Bill("Audio", s.SpeechModel, "fake") { Cost = .002m });
        }
    }
    sealed class ArraySamples(float[] samples) : ISampleProvider
    {
        int offset;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(24000, 1);
        public int Read(float[] buffer, int start, int count) { int n = Math.Min(count, samples.Length - offset); samples.AsSpan(offset, n).CopyTo(buffer.AsSpan(start, n)); offset += n; return n; }
    }
    internal static void SaveImage(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var file = File.Create(path); encoder.Save(file);
    }
    static void Render(Window window, string path, int width, Action<bool, string> check)
    {
        var content = (FrameworkElement)window.Content; window.Content = null;
        var surface = new Border { Width = width, Background = window.Background, Child = content };
        surface.SetValue(System.Windows.Documents.TextElement.FontFamilyProperty, window.FontFamily);
        if (window is SettingsWindow) surface.Height = window.Height - 32;
        surface.Measure(new Size(width, double.PositiveInfinity)); surface.Arrange(new Rect(new Point(), surface.DesiredSize)); surface.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface);
        byte[] pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        check(pixels.Any(x => x != 0) && pixels.Distinct().Count() > 50, "WPF rendered visible content: " + Path.GetFileName(path)); SaveImage(bitmap, path);
    }
    internal static async Task<int> Run(string[] args)
    {
        string report = Path.GetFullPath(args.SkipWhile(x => x != "--self-test").Skip(1).FirstOrDefault() ?? "self-test.json");
        string directory = Path.GetDirectoryName(report)!; Directory.CreateDirectory(directory);
        var checks = new List<string>(); var skipped = new List<string>(); string? error = null;
        void Check(bool pass, string name) { if (!pass) throw new Exception(name); checks.Add(name); }
        try {
            string Normalize(string t) => Regex.Replace(t, @"\s+", " ").Trim();
            var paragraphs = Enumerable.Range(1, 30).Select(i => $"Odsek {i}. " + string.Join(" ", Enumerable.Repeat("Žltý kôň číta text.", 40))).ToArray();
            string longText = string.Join("\n\n", paragraphs); var chunks = TextChunks.Split(longText).ToArray();
            Check(Normalize(string.Join(" ", chunks)) == Normalize(longText), "Chunking preserves all prose and order beyond 20,000 characters");
            Check(chunks[0].Length <= 450 && chunks.All(c => c.Length <= 1800 && c.Length > 0), "Short first chunk and bounded following chunks");
            Check(!TextChunks.Split(" \r\n ").Any(), "Whitespace has no paid chunks");
            Check(TextChunks.Split("Prvý odsek.\n\nDruhý odsek.", 15, 20).SequenceEqual(new[] { "Prvý odsek.", "Druhý odsek." }), "Paragraph boundaries preferred");
            string emoji = new string('x', 449) + "😀" + new string('y', 2100);
            var unicode = TextChunks.Split(emoji).ToArray();
            Check(string.Concat(unicode) == emoji && unicode.All(s => !char.IsHighSurrogate(s[^1]) && !char.IsLowSurrogate(s[0])), "Oversized unbroken text preserves Unicode surrogate pairs");
            var bundled = Catalog.Bundled();
            Check(bundled.Speech.Any(m => m.Id == new Settings().SpeechModel && m.Voices.Contains("Kore")) && bundled.Text.Contains(new Settings().TextModel), "Verified default models and voice included in offline catalog");
            Check(Path.GetDirectoryName(Settings.KeyPath) == Settings.PortableDirectory && Path.GetDirectoryName(Settings.FilePath) == Settings.PortableDirectory, "Key and settings live beside executable");
            string ledgerPath = Path.Combine(directory, "usage-fixture.json");
            if (File.Exists(ledgerPath)) File.Delete(ledgerPath);
            var ledger = new UsageStore(ledgerPath); var paid = ledger.Start("Text", "test/model", 12); paid.Id = "fixture-id"; paid.Cost = .0000123456789m; paid.Status = "Hotovo"; ledger.Update(paid);
            var unknown = ledger.Start("Audio", "test/voice", 20); unknown.Status = "Zrušené"; ledger.Update(unknown);
            var restored = new UsageStore(ledgerPath).Snapshot();
            Check(restored.Length == 2 && restored.Sum(b => b.Cost ?? 0) == .0000123456789m && restored.Count(b => b.Cost is null) == 1, "Portable usage history survives restart with exact costs and explicit unknown billing");
            Check(!File.ReadAllText(ledgerPath).Contains("test-secret") && restored[0].Characters == 12 && restored[0].Id == "fixture-id", "Usage journal records only metadata, no input text or API key");
            File.WriteAllText(ledgerPath, "broken");
            Check(new UsageStore(ledgerPath).Warning is not null && File.ReadAllText(ledgerPath) == "broken", "Damaged usage history is preserved and reported");
            File.Delete(ledgerPath); File.Delete(ledgerPath + ".bak");
            Check(Native.HotkeyText(3, 86) == "Ctrl + Alt + V" && !Native.ValidHotkey(0, 86), "Default shortcut and modifier validation");
            using (var firstKey = new Hotkeys()) using (var secondKey = new Hotkeys()) {
                bool available = firstKey.Apply(7, 123);
                if (available) Check(!secondKey.Apply(7, 123) && firstKey.Apply(7, 123), "Hotkey conflict rejected and existing registration preserved");
                else skipped.Add("Test shortcut Ctrl+Alt+Shift+F12 unavailable in this desktop session");
            }
            using (var fake = new Api(new Handler(async (r, token) => {
                using var body = JsonDocument.Parse(await r.Content!.ReadAsStringAsync(token));
                Check(r.RequestUri!.AbsolutePath.EndsWith("chat/completions") && body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString() == "Žltý kôň", "Preparation sends UTF-8 source separately from instructions");
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"test\",\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"Žltý kôň\"}}],\"usage\":{\"cost\":0.0001234567}}") };
            }))) {
                Bill? bill = null; string prepared = await fake.Prepare("Žltý kôň", new(), "test-key", b => bill = b, CancellationToken.None);
                Check(prepared == "Žltý kôň" && bill?.Cost == .0001234567m, "Prepared text and exact decimal usage cost retained");
            }
            int attempts = 0;
            using (var denied = new Api(new Handler((_, _) => { attempts++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":{\"message\":\"test-secret\"}}") }); }))) {
                try { await denied.Speak("text", new(), "test-secret", CancellationToken.None); throw new Exception("Expected rejection"); }
                catch (InvalidOperationException ex) { Check(!ex.Message.Contains("test-secret") && attempts == 1, "Provider errors redact key and never retry paid generation"); }
            }
            using (var incomplete = new Api(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"partial\",\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"content\":\"partial\"}}]}") })))) {
                bool billed = false;
                try { await incomplete.Prepare("text", new(), "key", _ => billed = true, CancellationToken.None); throw new Exception("Expected truncation rejection"); }
                catch (InvalidOperationException) { Check(billed, "Incomplete preparation retains billing but cannot reach speech"); }
            }
            using (var pcmApi = new Api(new Handler((_, _) => {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[48000]) };
                response.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("audio/pcm;rate=24000;channels=1"); response.Headers.Add("X-Generation-Id", "pcm-test"); return Task.FromResult(response);
            }))) {
                var audio = await pcmApi.Speak("text", new(), "key", CancellationToken.None);
                Check(AudioDecode.Decode(audio).Length == 24000 && audio.Bill.Id == "pcm-test", "PCM becomes decodable WAV with retained generation ID");
            }
            Check(Api.WrapPcm(new byte[48000], 24000, 1).Length == 48044, "WAV frame length correct");
            using (var json = JsonDocument.Parse("[0, null, \"0.00000001\"]")) Check(Api.DecimalCost(json.RootElement[0]) == 0 && Api.DecimalCost(json.RootElement[1]) == null && Api.DecimalCost(json.RootElement[2]) == .00000001m, "Zero cost distinguished from unavailable metadata");
            foreach (double rate in new[] { .5, 1, 1.5, 2 }) {
                float[] tone = Enumerable.Range(0, 24000 * 4).Select(i => (float)(.4 * Math.Sin(i * Math.PI * 2 * 440 / 24000))).ToArray();
                var tempo = new TempoSamples(new ArraySamples(tone)) { Speed = rate };
                var output = new List<float>(); var buf = new float[2048]; int n;
                while ((n = tempo.Read(buf, 0, buf.Length)) > 0) output.AddRange(buf.AsSpan(0, n).ToArray());
                double seconds = output.Count / 24000d; int crossings = 0;
                for (int i = 1; i < output.Count; i++) if (output[i - 1] < 0 && output[i] >= 0) crossings++;
                Check(Math.Abs(seconds - 4 / rate) < .2, $"Tempo {rate} changes duration correctly");
                Check(Math.Abs(crossings / seconds - 440) < 12, $"Tempo {rate} preserves pitch");
            }
            int consumed = 0; var queue = new AudioQueue(() => consumed++); queue.Enqueue(new float[] { 1, 2 }); queue.Enqueue(new float[] { 3, 4 }); queue.Finish(); var samples = new float[10];
            Check(queue.Read(samples, 0, 10) == 4 && samples.Take(4).SequenceEqual(new float[] { 1, 2, 3, 4 }) && consumed == 2, "Audio chunks join without injected gaps and in order");
            await PipelineTests(Check);
            await OmniVoiceTests.Run(Check, directory);
            UpdateTests.Run(Check, directory);
            UpdateTests.VerifyProgressUi(Check, directory, true);
            VerifyPlayerFrame(Check, skipped);
            Render(new PlayerWindow((App)Application.Current), Path.Combine(directory, "player-preview.png"), 460, Check);
            Theme.Apply(false);
            Render(new SettingsWindow((App)Application.Current), Path.Combine(directory, "settings-preview.png"), 760, Check);
            var usageWindow = new SettingsWindow((App)Application.Current); ((TabControl)usageWindow.Content).SelectedIndex = 1;
            void SampleUsage(Window window) {
                var tab = (TabItem)((TabControl)window.Content).Items[1];
                var view = (UsageView)((ScrollViewer)tab.Content).Content;
                var grid = view.Children.OfType<DataGrid>().Single();
                grid.ItemsSource = Enumerable.Range(1, 30).Select(i => new { Time = "21.10. 12:00:00", Kind = "Audio", Model = "Ukážka · model", Cost = "$0.001", State = "Ukážkový záznam" }).ToArray();
            }
            SampleUsage(usageWindow);
            Render(usageWindow, Path.Combine(directory, "usage-preview.png"), 650, Check);
            Theme.Apply(true);
            var preferences = ((App)Application.Current).Preferences;
            bool originalLight = preferences.LightTheme; preferences.LightTheme = true;
            Render(new SettingsWindow((App)Application.Current), Path.Combine(directory, "settings-light-preview.png"), 760, Check);
            bool originalInternal = preferences.InternalSpeech; preferences.InternalSpeech = true;
            Render(new SettingsWindow((App)Application.Current), Path.Combine(directory, "settings-internal-preview.png"), 760, Check);
            preferences.InternalSpeech = originalInternal;
            var lightUsage = new SettingsWindow((App)Application.Current); ((TabControl)lightUsage.Content).SelectedIndex = 1;
            SampleUsage(lightUsage);
            Render(lightUsage, Path.Combine(directory, "usage-light-preview.png"), 650, Check);
            Render(new PlayerWindow((App)Application.Current), Path.Combine(directory, "player-light-preview.png"), 460, Check);
            preferences.LightTheme = originalLight;
            Theme.Apply(false);
            var compact = new SettingsWindow((App)Application.Current) { Height = 560 };
            Render(compact, Path.Combine(directory, "settings-compact-preview.png"), 720, Check);
            if (WaveOut.DeviceCount > 0) {
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var device = new WaveOutEvent { Volume = 0 };
                device.PlaybackStopped += (_, e) => { if (e.Exception is not null) done.TrySetException(e.Exception); else done.TrySetResult(); };
                var deviceQueue = new AudioQueue(() => { }); deviceQueue.Enqueue(new float[4800]); deviceQueue.Finish();
                device.Init(new TempoSamples(deviceQueue) { Speed = 1.5 }); device.Play(); await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(true, "Windows audio device opens and completes silent playback");
            } else skipped.Add("No Windows audio output device available");
        } catch (Exception ex) { error = ex.ToString(); }
        File.WriteAllText(report, JsonSerializer.Serialize(new { checks, skipped, error, runtime = Environment.Version.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
        return error is null ? 0 : 1;
    }
    static async Task PipelineTests(Action<bool, string> check)
    {
        string source = string.Join("\n\n", Enumerable.Range(1, 20).Select(i => $"Odsek {i}. " + new string('a', 800)));
        var fake = new FakeApi(); var sinkEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var session = new ReadingSession(fake, new Settings(), "fake", async (queue, paused, token) => {
            sinkEntered.TrySetResult(); await Task.Delay(Timeout.Infinite, token);
        })) {
            session.TogglePause(); var run = session.Run(source);
            await sinkEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (int i = 0; i < 100 && session.Prepared < 3; i++) await Task.Delay(10);
            check(session.Paused && session.Prepared == 3 && !session.ProductionDone, "Pause keeps preparation running with only three buffered clips");
            check(session.Started && !session.ProductionDone, "Playback begins before whole document is generated");
            session.Stop(); try { await run.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
            check(fake.Spoken.Count == 3, "Stop cancels bounded background work without starting more requests");
        }
        fake = new FakeApi();
        using (var session = new ReadingSession(fake, new Settings { PrepareText = false }, "fake", async (queue, paused, token) => {
            var buffer = new float[1024];
            while (true) { token.ThrowIfCancellationRequested(); if (!paused() && queue.Read(buffer, 0, buffer.Length) == 0) break; await Task.Delay(1, token); }
        })) {
            await session.Run("Prvý text.\n\nDruhý text.").WaitAsync(TimeSpan.FromSeconds(5));
            check(session.ProductionDone && fake.Preparations == 0 && string.Join("", fake.Spoken).Contains("Druhý text."), "Disabled preparation goes straight to speech and drains complete audio");
        }
        fake = new FakeApi { BeforeSpeak = token => Task.Delay(Timeout.Infinite, token) };
        using (var session = new ReadingSession(fake, new Settings(), "fake", (_, _, token) => Task.Delay(Timeout.Infinite, token))) {
            var run = session.Run("Zrušiteľná požiadavka.");
            for (int i = 0; i < 100 && fake.Spoken.Count == 0; i++) await Task.Delay(10);
            session.Stop(); bool cancelled = false;
            try { await run.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { cancelled = true; }
            check(cancelled && fake.Spoken.Count == 1, "Cancellation during HTTP work exits promptly without retry");
        }
    }
    internal static async Task<int> LiveInternal(string report)
    {
        string? error = null; int frames = 0;
        using var api = new Api();
        try {
            var audio = await api.Speak("Dobrý deň. Toto je krátka skúška interného hlasového modelu.", new Settings { InternalSpeech = true, PrepareText = false }, "", CancellationToken.None);
            frames = AudioDecode.Decode(audio).Length;
            File.WriteAllBytes(Path.ChangeExtension(report, ".mp3"), audio.Bytes);
        } catch (Exception ex) { error = ex.Message; }
        File.WriteAllText(report, JsonSerializer.Serialize(new { frames, seconds = frames / 24000d, error }));
        return error is null && frames > 0 ? 0 : 1;
    }
    internal static async Task<int> Live(string report)
    {
        var bills = new List<Bill>();
        var settings = new Settings(); string key = settings.ReadKey();
        string? error = null; int frames = 0;
        var usage = ((App)Application.Current).Usage;
        using var api = new Api(usage: usage);
        try {
            if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("Missing local test key.");
            string prepared = await api.Prepare("**Dobrý deň.** Toto je krátka skúška aplikácie Hlas. Cena je dvanásť eur.", settings, key, bills.Add, CancellationToken.None);
            var audio = await api.Speak(prepared, settings, key, CancellationToken.None); bills.Add(audio.Bill);
            frames = AudioDecode.Decode(audio).Length;
            File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(report)!, "live-test.wav"), audio.Bytes);
        } catch (Exception ex) { error = ex.Message; }
        async Task ReadCosts() {
            foreach (var bill in bills.Where(b => !b.Cost.HasValue && b.Id is not null)) {
                for (int i = 0; i < 20 && !bill.Cost.HasValue; i++) {
                    try { bill.Cost = await api.Cost(bill.Id!, key, CancellationToken.None); if (bill.Cost.HasValue) usage.Update(bill); } catch { }
                    if (!bill.Cost.HasValue) await Task.Delay(15000);
                }
            }
        }
        void WriteReport() => File.WriteAllText(report, JsonSerializer.Serialize(new { frames, error, bills = bills.Select(b => new { b.Kind, b.Model, b.Id, b.Cost }) }, new JsonSerializerOptions { WriteIndented = true }));
        WriteReport(); await ReadCosts(); WriteReport(); return error is null && frames > 0 ? 0 : 1;
    }
}
