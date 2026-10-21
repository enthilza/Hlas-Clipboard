using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Hlas;

internal static partial class UpdateTests
{
    internal static void Run(Action<bool, string> assert, string directory)
    {
        using var rsa = RSA.Create(2048); using var other = RSA.Create(2048);
        var package = new byte[] { 1, 2, 3, 4, 5 };
        var manifest = new UpdateManifest("9.0.0", "v9.0.0", Updates.Runtime, "Hlas.exe", package.Length, Convert.ToHexString(SHA256.HashData(package)));
        byte[] Bytes(UpdateManifest value) => JsonSerializer.SerializeToUtf8Bytes(value);
        string Sign(byte[] bytes) => Convert.ToBase64String(rsa.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        bool Rejects(Action action) { try { action(); return false; } catch (Exception ex) when (ex is InvalidDataException or CryptographicException or FormatException) { return true; } }
        var bytes = Bytes(manifest); var signature = Sign(bytes); var key = rsa.ToXmlString(false);
        assert(Updates.VerifyManifest(bytes, signature, key, manifest.Tag, Updates.Runtime) == manifest, "Publisher-signed update manifest verifies with the pinned public key");
        assert(Rejects(() => Updates.VerifyManifest(Bytes(manifest with { Size = 999 }), signature, key, manifest.Tag, Updates.Runtime)), "Tampered update manifest is rejected before download");
        assert(Rejects(() => Updates.VerifyManifest(bytes, signature, other.ToXmlString(false), manifest.Tag, Updates.Runtime)), "A signature from a different publisher is rejected");
        assert(Rejects(() => Updates.VerifyManifest(bytes, signature, key, "v8.0.0", Updates.Runtime)), "Manifest must match the exact release tag");
        assert(Rejects(() => Updates.VerifyManifest(bytes, signature, key, manifest.Tag, "wrong-architecture")), "Updates for another architecture are rejected");
        var invalid = Bytes(manifest with { File = "../Hlas.exe" });
        assert(Rejects(() => Updates.VerifyManifest(invalid, Sign(invalid), key, manifest.Tag, Updates.Runtime)), "Signed metadata cannot choose an arbitrary destination filename");
        invalid = Bytes(manifest with { Size = Updates.MaxPackageBytes + 1 });
        assert(Rejects(() => Updates.VerifyManifest(invalid, Sign(invalid), key, manifest.Tag, Updates.Runtime)), "Oversized packages are rejected even with a valid signature");
        assert(Updates.NormalizeVersion("1.3.0") == Updates.NormalizeVersion("1.3.0.0") && Updates.NormalizeVersion("1.3.1") > Updates.NormalizeVersion("1.3.0"), "Version comparison handles equal three/four-part versions and newer releases");
        var test = Path.Combine(directory, "update-fixture-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(test);
        var target = Path.Combine(test, "target.exe"); var candidate = Path.Combine(test, "candidate.exe"); var backup = Path.Combine(test, "backup.exe");
        try
        {
            File.WriteAllBytes(candidate, package); Updates.VerifyPackage(candidate, manifest);
            assert(true, "Downloaded package matches signed SHA-256 and size");
            File.WriteAllBytes(candidate, new byte[] { 5, 4, 3, 2, 1 });
            assert(Rejects(() => Updates.VerifyPackage(candidate, manifest)), "Same-size corrupted package fails SHA-256 verification");
            File.WriteAllText(target, "old version"); File.WriteAllText(candidate, "new version");
            bool restarted = false;
            Updates.ReplaceAndRestart(target, candidate, backup, () => { restarted = File.ReadAllText(target) == "new version"; });
            assert(restarted && File.ReadAllText(backup) == "old version", "Atomic update preserves a backup and restarts the replaced executable");
            File.Delete(backup); File.WriteAllText(candidate, "broken version");
            try { Updates.ReplaceAndRestart(target, candidate, backup, () => throw new IOException("Simulated restart failure")); } catch (IOException) { }
            assert(File.ReadAllText(target) == "new version", "A restart failure rolls back the previous executable");
            File.WriteAllText(candidate, "blocked version");
            using (var locked = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bool rejected = false;
                try { Updates.ReplaceAndRestart(target, candidate, backup, () => throw new Exception("Must not restart")); } catch (IOException) { rejected = true; }
                assert(rejected, "A locked executable prevents replacement without forcing another process to exit");
            }
            assert(File.ReadAllText(target) == "new version", "Failed replacement leaves the original executable intact");
        }
        finally { foreach (var path in new[] { target, candidate, backup }) if (File.Exists(path)) File.Delete(path); Directory.Delete(test); }
        VerifyDownload(assert);
        VerifyDiscovery(assert);
    }

    sealed class FixtureHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }
    sealed class CaptureProgress(Action<UpdateProgress> action) : IProgress<UpdateProgress>
    {
        public void Report(UpdateProgress value) => action(value);
    }
    static void VerifyDownload(Action<bool, string> assert)
    {
        Task.Run(async () =>
        {
            var bytes = new byte[1024 * 1024]; Random.Shared.NextBytes(bytes);
            var manifest = new UpdateManifest("9.0.0", "v9.0.0", Updates.Runtime, "Hlas.exe", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)));
            var offer = new UpdateOffer(manifest, JsonSerializer.SerializeToUtf8Bytes(manifest), "fixture", "https://fixture.invalid/Hlas.exe");
            using var client = new HttpClient(new FixtureHandler(bytes));
            var states = new List<UpdateProgress>();
            var job = await Updates.DownloadAsync(offer, new CaptureProgress(states.Add), CancellationToken.None, client);
            try
            {
                var percentages = states.Where(s => s.Percent is not null).Select(s => s.Percent!.Value).ToArray();
                assert(percentages.First() == 0 && percentages.Last() == 100 && percentages.SequenceEqual(percentages.Order()), "Download reports monotonic byte-based progress from 0 to 100 percent");
                assert(states.Last().Percent is null && states.Last().Message.Contains("Overujem") && File.ReadAllBytes(Path.Combine(job, "Hlas.exe")).SequenceEqual(bytes), "Download switches to verification phase and preserves the complete package");
            }
            finally { Updates.CleanJob(job); }
            var before = Directory.GetDirectories(Updates.Root).Order().ToArray();
            using var cancel = new CancellationTokenSource();
            bool cancelled = false;
            try { await Updates.DownloadAsync(offer, new CaptureProgress(p => { if (p.Percent > 0) cancel.Cancel(); }), cancel.Token, client); }
            catch (OperationCanceledException) { cancelled = true; }
            assert(cancelled && before.SequenceEqual(Directory.GetDirectories(Updates.Root).Order()), "Cancelling a partial download removes its owned job and never produces an installable result");
            bool rejected = false;
            try { await Updates.DownloadAsync(offer with { Manifest = manifest with { Sha256 = new string('0', 64) } }, new CaptureProgress(_ => { }), CancellationToken.None, client); }
            catch (InvalidDataException) { rejected = true; }
            assert(rejected && before.SequenceEqual(Directory.GetDirectories(Updates.Root).Order()), "Failed package verification cleans up the downloaded job");
        }).GetAwaiter().GetResult();
    }

    internal static void VerifyProgressUi(Action<bool, string> assert, string directory, bool renderingAvailable)
    {
        var window = new UpdateProgressWindow(null);
        var panel = (StackPanel)window.Content;
        var bar = panel.Children.OfType<ProgressBar>().Single();
        assert(bar.IsIndeterminate, "Update dialog visibly starts in indeterminate checking state");
        window.Display(new("Sťahujem verziu 1.4.1 · 45 %", 45, "35.1 / 78.1 MB"));
        assert(!bar.IsIndeterminate && bar.Value == 45 && panel.Children.OfType<TextBlock>().Any(t => t.Text.Contains("45 %")), "Update dialog displays download percentage and determinate progress");
        if (renderingAvailable)
        {
            window.Content = null;
            var surface = new Border { Child = panel, Width = 510, Background = window.Background };
            surface.Measure(new Size(510, double.PositiveInfinity)); surface.Arrange(new Rect(new Point(), surface.DesiredSize)); surface.UpdateLayout();
            var image = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth), (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            image.Render(surface); SelfTest.SaveImage(image, Path.Combine(directory, "update-progress-preview.png"));
        }
        window.Display(new("Overujem stiahnutú aktualizáciu…"));
        assert(bar.IsIndeterminate, "Hash verification returns to an indeterminate busy indicator");
    }
}

