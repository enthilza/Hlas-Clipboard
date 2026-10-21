using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Hlas;

internal sealed record UpdateManifest(string Version, string Tag, string Runtime, string File, long Size, string Sha256);
internal sealed record UpdateOffer(UpdateManifest Manifest, byte[] ManifestBytes, string Signature, string DownloadUrl);
internal sealed record UpdatePlan(string Target, int ParentId, long ParentStartTicks, string PreviousHash);

internal static class Updates
{
    internal const string Repository = "enthilza/Hlas-Clipboard";
    internal const long MaxPackageBytes = 200 * 1024 * 1024;
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    internal static Version CurrentVersion => NormalizeVersion(Assembly.GetExecutingAssembly().GetName().Version!.ToString());
    internal static string Runtime => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
    internal static string Root => Path.Combine(Settings.DirectoryPath, "Updates");
    internal static string PublicKey
    {
        get { using var reader = new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream("Hlas.UpdatePublicKey")!); return reader.ReadToEnd(); }
    }
    internal static Version NormalizeVersion(string version)
    {
        if (!Regex.IsMatch(version, @"^\d+\.\d+\.\d+(\.\d+)?$") || !Version.TryParse(version, out var parsed)) throw new InvalidDataException("Neplatná verzia aktualizácie.");
        return new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build), Math.Max(0, parsed.Revision));
    }
    internal static UpdateManifest VerifyManifest(byte[] bytes, string signature, string key, string expectedTag, string runtime)
    {
        using var rsa = RSA.Create(); rsa.FromXmlString(key);
        if (!rsa.VerifyData(bytes, Convert.FromBase64String(signature.Trim()), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) throw new InvalidDataException("Digitálny podpis aktualizácie nie je platný.");
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(bytes, Json) ?? throw new InvalidDataException("Chýba popis aktualizácie.");
        _ = NormalizeVersion(manifest.Version);
        if (manifest.Tag != "v" + manifest.Version || manifest.Tag != expectedTag || manifest.Runtime != runtime || manifest.File != "Hlas.exe" || manifest.Size <= 0 || manifest.Size > MaxPackageBytes || manifest.Sha256 is null || !Regex.IsMatch(manifest.Sha256, "^[a-fA-F0-9]{64}$")) throw new InvalidDataException("Popis aktualizácie nezodpovedá verzii alebo architektúre aplikácie.");
        return manifest;
    }
    internal static string FileHash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    internal static void VerifyPackage(string path, UpdateManifest manifest)
    {
        if (new FileInfo(path).Length != manifest.Size || !FileHash(path).Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Kontrolný súčet SHA-256 aktualizácie nesúhlasí.");
    }
    internal static void VerifyReleaseDirectory(string directory)
    {
        var bytes = File.ReadAllBytes(Path.Combine(directory, "update.json"));
        var unsigned = JsonSerializer.Deserialize<UpdateManifest>(bytes, Json)!;
        var manifest = VerifyManifest(bytes, File.ReadAllText(Path.Combine(directory, "update.sig")), PublicKey, unsigned.Tag, Runtime);
        VerifyPackage(Path.Combine(directory, "Hlas.exe"), manifest);
    }
    static HttpClient Client()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("HlasClipboard/" + CurrentVersion.ToString(3));
        return client;
    }
    static async Task<byte[]> ReadLimitedAsync(HttpClient client, string url, int limit, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(45));
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token); response.EnsureSuccessStatusCode();
        using var input = await response.Content.ReadAsStreamAsync(timeout.Token); using var output = new MemoryStream();
        var buffer = new byte[8192]; int read;
        while ((read = await input.ReadAsync(buffer, timeout.Token)) > 0) { if (output.Length + read > limit) throw new InvalidDataException("Odpoveď aktualizačného servera je príliš veľká."); output.Write(buffer, 0, read); }
        return output.ToArray();
    }
    public static async Task<UpdateOffer?> CheckAsync(CancellationToken cancellation = default)
    {
        using var client = Client();
        return await CheckAsync(client, PublicKey, CurrentVersion, Runtime, cancellation);
    }
    internal static async Task<UpdateOffer?> CheckAsync(HttpClient client, string key, Version currentVersion, string runtime, CancellationToken cancellation)
    {
        // Public release downloads do not use the shared anonymous REST API quota.
        var bytes = await ReadLimitedAsync(client, $"https://github.com/{Repository}/releases/latest/download/update.json", 16384, cancellation);
        var unsigned = JsonSerializer.Deserialize<UpdateManifest>(bytes, Json) ?? throw new InvalidDataException("Chýba popis aktualizácie.");
        _ = NormalizeVersion(unsigned.Version);
        if (unsigned.Tag != "v" + unsigned.Version) throw new InvalidDataException("Neplatné vydanie aktualizácie.");
        // Pin subsequent assets to this exact version even if 'latest' changes mid-check.
        // The unsigned tag only selects a path in our repository; trust nothing until signature verification.
        string Asset(string name) => $"https://github.com/{Repository}/releases/download/{Uri.EscapeDataString(unsigned.Tag)}/{name}";
        var signature = Encoding.UTF8.GetString(await ReadLimitedAsync(client, Asset("update.sig"), 4096, cancellation));
        cancellation.ThrowIfCancellationRequested();
        var manifest = VerifyManifest(bytes, signature, key, unsigned.Tag, runtime);
        if (NormalizeVersion(manifest.Version) <= currentVersion) return null;
        return new UpdateOffer(manifest, bytes, signature, Asset("Hlas.exe"));
    }
    public static async Task<string> DownloadAsync(UpdateOffer offer, IProgress<UpdateProgress> progress, CancellationToken cancellation = default)
    {
        using var client = Client();
        return await DownloadAsync(offer, progress, cancellation, client);
    }
    internal static async Task<string> DownloadAsync(UpdateOffer offer, IProgress<UpdateProgress> progress, CancellationToken cancellation, HttpClient client)
    {
        cancellation.ThrowIfCancellationRequested();
        var job = Path.Combine(Root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(job);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromMinutes(5));
            progress.Report(new($"Našla sa verzia {offer.Manifest.Version}. Sťahujem…", 0));
            using var response = await client.GetAsync(offer.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token); response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length != offer.Manifest.Size) throw new InvalidDataException("Veľkosť aktualizácie nesúhlasí.");
            await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token))
            await using (var output = new FileStream(Path.Combine(job, "Hlas.exe"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920]; long total = 0; int read, last = -1;
                while ((read = await input.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    total += read; if (total > offer.Manifest.Size) throw new InvalidDataException("Aktualizácia presahuje deklarovanú veľkosť.");
                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                    var percent = (int)(100 * total / offer.Manifest.Size);
                    if (percent != last) { last = percent; progress.Report(new($"Sťahujem verziu {offer.Manifest.Version} · {percent} %", percent,
                        $"{total / 1048576d:0.0} / {offer.Manifest.Size / 1048576d:0.0} MB")); }
                }
            }
            cancellation.ThrowIfCancellationRequested();
            progress.Report(new("Overujem stiahnutú aktualizáciu…", null, "Kontrolujem SHA-256. Digitálny podpis bol overený pred sťahovaním."));
            VerifyPackage(Path.Combine(job, "Hlas.exe"), offer.Manifest);
            await File.WriteAllBytesAsync(Path.Combine(job, "update.json"), offer.ManifestBytes, cancellation);
            await File.WriteAllTextAsync(Path.Combine(job, "update.sig"), offer.Signature, cancellation);
            cancellation.ThrowIfCancellationRequested();
            return job;
        }
        catch { CleanJob(job); throw; }
    }
    internal static void RequireJob(string job)
    {
        var full = Path.GetFullPath(job);
        if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase) || !Regex.IsMatch(Path.GetFileName(full), "^[a-f0-9]{32}$") || new DirectoryInfo(full).LinkTarget is not null) throw new InvalidDataException("Neplatný dočasný adresár aktualizácie.");
    }
    public static void StartInstaller(string job)
    {
        RequireJob(job);
        var target = Environment.ProcessPath ?? throw new InvalidOperationException("Umiestnenie aplikácie nie je dostupné.");
        // Test write access before asking the running application to exit.
        var probe = Path.Combine(Path.GetDirectoryName(target)!, ".luma-write-" + Guid.NewGuid().ToString("N"));
        using (File.Create(probe)) { } File.Delete(probe);
        using var parent = Process.GetCurrentProcess();
        var plan = new UpdatePlan(target, parent.Id, parent.StartTime.ToUniversalTime().Ticks, FileHash(target));
        File.WriteAllText(Path.Combine(job, "plan.json"), JsonSerializer.Serialize(plan));
        var helper = Path.Combine(job, "updater.exe"); File.Copy(target, helper, false);
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = job };
        start.ArgumentList.Add("--apply-update"); start.ArgumentList.Add(job);
        using var process = Process.Start(start) ?? throw new IOException("Nepodarilo sa spustiť aktualizátor.");
    }
    internal static void ReplaceAndRestart(string target, string candidate, string backup, Action restart)
    {
        File.Replace(candidate, target, backup, true);
        try { restart(); }
        catch { File.Replace(backup, target, null, true); throw; }
    }
    internal static async Task<int> ApplyAsync(string job)
    {
        string? candidate = null, backup = null;
        try
        {
            RequireJob(job);
            var plan = JsonSerializer.Deserialize<UpdatePlan>(File.ReadAllText(Path.Combine(job, "plan.json"))) ?? throw new InvalidDataException("Chýba plán aktualizácie.");
            var target = Path.GetFullPath(plan.Target);
            if (!Path.IsPathFullyQualified(plan.Target) || !Path.GetExtension(target).Equals(".exe", StringComparison.OrdinalIgnoreCase) || new FileInfo(target).LinkTarget is not null) throw new InvalidDataException("Neplatný cieľ aktualizácie.");
            var bytes = File.ReadAllBytes(Path.Combine(job, "update.json"));
            var unsigned = JsonSerializer.Deserialize<UpdateManifest>(bytes, Json)!;
            var manifest = VerifyManifest(bytes, File.ReadAllText(Path.Combine(job, "update.sig")), PublicKey, unsigned.Tag, Runtime);
            if (NormalizeVersion(manifest.Version) <= CurrentVersion) throw new InvalidDataException("Aktualizácia nie je novšia než bežiaca verzia.");
            VerifyPackage(Path.Combine(job, "Hlas.exe"), manifest);
            Process? parent = null;
            try { parent = Process.GetProcessById(plan.ParentId); } catch (ArgumentException) { }
            if (parent is not null)
            {
                using (parent)
                {
                    if (parent.StartTime.ToUniversalTime().Ticks != plan.ParentStartTicks || !string.Equals(parent.MainModule?.FileName, target, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Bežiaca aplikácia nezodpovedá plánu aktualizácie.");
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                    await parent.WaitForExitAsync(timeout.Token);
                }
            }
            if (!FileHash(target).Equals(plan.PreviousHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Aplikácia bola počas aktualizácie zmenená.");
            var suffix = Path.GetFileName(job);
            candidate = target + "." + suffix + ".new"; backup = target + "." + suffix + ".bak";
            File.Copy(Path.Combine(job, "Hlas.exe"), candidate, false); VerifyPackage(candidate, manifest);
            var installedBackup = backup;
            ReplaceAndRestart(target, candidate, backup, () =>
            {
                var start = new ProcessStartInfo(target) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(target)! };
                start.ArgumentList.Add("--updated"); start.ArgumentList.Add(installedBackup);
                using var restarted = Process.Start(start) ?? throw new IOException("Novú verziu sa nepodarilo spustiť.");
                // Keep the rollback copy until the new application's normal startup acknowledges it.
                var ready = installedBackup + ".ready";
                for (var attempt = 0; attempt < 150; attempt++)
                {
                    if (File.Exists(ready)) { File.Delete(ready); File.Delete(installedBackup); return; }
                    if (restarted.HasExited) throw new IOException("Nová verzia sa ukončila pred dokončením štartu.");
                    Thread.Sleep(100);
                }
                // A slow but running process keeps its backup; never replace an executable in use.
            });
            return 0;
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Path.Combine(Settings.DirectoryPath, "update-error.txt"), DateTimeOffset.Now + " " + ex); } catch { }
            System.Windows.MessageBox.Show("Aktualizácia sa nepodarila. Pôvodné EXE alebo jeho záloha zostali zachované.\n\n" + ex.Message, "Hlas — aktualizácia");
            return 1;
        }
        finally { if (candidate is not null) { try { File.Delete(candidate); } catch { } } CleanJob(job); }
    }
    internal static void CompleteStartup(string backup)
    {
        var target = Environment.ProcessPath!;
        if (backup.StartsWith(target + ".", StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(backup[(target.Length + 1)..], "^[a-f0-9]{32}\\.bak$"))
        { try { if (File.Exists(backup)) File.WriteAllText(backup + ".ready", "ready"); } catch { } }
    }
    internal static void CleanJob(string job)
    {
        try
        {
            RequireJob(job);
            foreach (var name in new[] { "Hlas.exe", "updater.exe", "update.json", "update.sig", "plan.json" })
            { try { File.Delete(Path.Combine(job, name)); } catch (IOException) { } }
            if (!Directory.EnumerateFileSystemEntries(job).Any()) Directory.Delete(job);
        }
        catch { }
    }
    internal static void CleanOldJobs()
    {
        if (!Directory.Exists(Root)) return;
        foreach (var job in Directory.EnumerateDirectories(Root)) if (Directory.GetLastWriteTimeUtc(job) < DateTime.UtcNow.AddDays(-2)) CleanJob(job);
    }
}

