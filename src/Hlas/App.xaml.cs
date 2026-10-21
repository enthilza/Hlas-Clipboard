using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Media;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Forms = System.Windows.Forms;

namespace Hlas;

public partial class App : Application
{
    Mutex? mutex;
    EventWaitHandle? showEvent;
    RegisteredWaitHandle? wait;
    Hotkeys? hotkeys;
    Forms.NotifyIcon? tray;
    PlayerWindow? player;
    SettingsWindow? settings;
    readonly CancellationTokenSource lifetime = new();
    readonly List<Bill> bills = new();
    bool updating;
    int billingRun;
    internal bool Exiting { get; private set; }
    internal Settings Preferences { get; private set; } = Settings.Load();
    internal UsageStore Usage { get; } = new(Path.Combine(Settings.PortableDirectory, "hlas.usage.json"));
    internal Api Api { get; }
    public App() => Api = new Api(usage: Usage);
    internal ReadingSession? Session { get; private set; }
    internal string CostSummary => bills.Count == 0 ? "Pripravené na čítanie" : bills.All(b => !b.Cost.HasValue) ? "Cena: čakám na údaje" : "Cena: $" + bills.Where(b => b.Cost.HasValue).Sum(b => b.Cost!.Value).ToString("0.############################", CultureInfo.InvariantCulture) + (bills.Any(b => !b.Cost.HasValue) ? " + čakám na údaje" : "");
    internal string CostDetails => string.Join("\n", bills.Select(b => $"{b.Kind}: {b.Model} · {(b.Cost.HasValue ? "$" + b.Cost.Value.ToString(CultureInfo.InvariantCulture) : "cena nedostupná")} · {b.Id ?? "bez ID"}"));
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e); Theme.Apply(Preferences.LightTheme);
        if (e.Args.Length == 2 && e.Args[0] == "--apply-update") { Shutdown(await Updates.ApplyAsync(e.Args[1])); return; }
        if (e.Args.Length == 2 && e.Args[0] == "--verify-package") { try { Updates.VerifyReleaseDirectory(e.Args[1]); Shutdown(0); } catch { Shutdown(1); } return; }
        if (e.Args.Length == 2 && e.Args[0] == "--check-update") {
            try { var offer = await Task.Run(() => Updates.CheckAsync()); File.WriteAllText(e.Args[1], offer is null ? "Signed public feed verified; current version is up to date." : "Verified update: " + offer.Manifest.Version); Shutdown(0); }
            catch (Exception ex) { File.WriteAllText(e.Args[1], ex.ToString()); Shutdown(1); } return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--live-test") { Shutdown(await SelfTest.Live(e.Args[1])); return; }
        if (e.Args.Contains("--self-test")) { Shutdown(await SelfTest.Run(e.Args)); return; }
        mutex = new Mutex(true, @"Local\HlasClipboard.App", out bool first);
        if (!first) { try { using var signal = EventWaitHandle.OpenExisting(@"Local\HlasClipboard.Show"); signal.Set(); } catch { } Shutdown(); return; }
        showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\HlasClipboard.Show");
        wait = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => Dispatcher.BeginInvoke(ShowPlayer), null, -1, false);
        player = new PlayerWindow(this); MainWindow = player;
        hotkeys = new Hotkeys(); hotkeys.Pressed += StartClipboard;
        bool registered = hotkeys.Apply(Preferences.Modifiers, Preferences.Key);
        var menu = new Forms.ContextMenuStrip { ShowImageMargin = false };
        menu.Opening += (_, _) => Theme.StyleTray(menu);
        menu.Items.Add("Prehrávač", null, (_, _) => ShowPlayer());
        menu.Items.Add("Prečítať schránku", null, (_, _) => StartClipboard());
        menu.Items.Add("Nastavenia", null, (_, _) => OpenSettings());
        menu.Items.Add("Aktualizovať", null, async (_, _) => await Update());
        menu.Items.Add(new Forms.ToolStripSeparator()); menu.Items.Add("Ukončiť Hlas", null, (_, _) => ExitApp());
        using var stream = GetResourceStream(new Uri("pack://application:,,,/Assets/Hlas.ico")).Stream;
        tray = new Forms.NotifyIcon { Text = "Hlas · " + Native.HotkeyText(Preferences.Modifiers, Preferences.Key), Icon = new System.Drawing.Icon(stream), ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => ShowPlayer();
        if (!registered) { ShowPlayer(); player.SetMessage("Skratka je obsadená", "Vyber inú kombináciu v nastaveniach."); OpenSettings(); }
        else if (!File.Exists(Settings.KeyPath) && !e.Args.Contains("--tray")) OpenSettings();
        else if (!e.Args.Contains("--tray")) ShowPlayer();
        if (e.Args.Length == 2 && e.Args[0] == "--updated") Updates.CompleteStartup(e.Args[1]);
        _ = Task.Run(Updates.CleanOldJobs);
        _ = RefreshPendingCosts(lifetime.Token);
    }
    internal void ShowPlayer() { player?.Show(); }
    internal void OpenSettings()
    {
        if (settings is null) { settings = new SettingsWindow(this); settings.Closed += (_, _) => settings = null; }
        settings.Show(); settings.Activate();
    }
    internal void ApplySettings(Settings next, string? apiKey = null)
    {
        var previous = Preferences;
        if (!hotkeys!.Apply(next.Modifiers, next.Key)) throw new InvalidOperationException("Táto skratka je obsadená. Pôvodná skratka zostala aktívna.");
        try { if (apiKey is not null) next.SetKey(apiKey); Settings.SetAutoStart(next.AutoStart); next.Save(); }
        catch { hotkeys.Apply(previous.Modifiers, previous.Key); try { Settings.SetAutoStart(previous.AutoStart); } catch { } throw; }
        Preferences = next;
        Theme.Apply(next.LightTheme);
        if (tray is not null) tray.Text = "Hlas · " + Native.HotkeyText(next.Modifiers, next.Key);
        if (Session is null) player?.SetMessage("Text, ktorý môžeš počúvať.", "Skopíruj text a stlač " + Native.HotkeyText(next.Modifiers, next.Key) + ".");
    }
    internal async void StartClipboard()
    {
        if (Session is not null || updating) { SystemSounds.Beep.Play(); return; }
        ShowPlayer();
        string text, key;
        try {
            if (!Clipboard.ContainsText()) { player!.SetMessage("V schránke nie je text", "Skopíruj text a stlač skratku znova."); return; }
            text = Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(text)) { player!.SetMessage("Schránka je prázdna", "Skopíruj text a stlač skratku znova."); return; }
            key = Preferences.ReadKey();
            if (string.IsNullOrWhiteSpace(key)) { player!.SetMessage("Chýba OpenRouter kľúč", "Zadaj API kľúč v nastaveniach."); OpenSettings(); return; }
        } catch (Exception ex) { player!.SetMessage("Text sa nepodarilo načítať", ex.Message); return; }
        bills.Clear(); int run = ++billingRun;
        var session = new ReadingSession(Api, Preferences, key); Session = session;
        session.Billed += bill => Dispatcher.BeginInvoke(() => { if (billingRun == run) bills.Add(bill); if (!bill.Cost.HasValue && bill.Id is not null) _ = FetchCost(bill, key); });
        player!.Refresh();
        try { await session.Run(text); player.SetMessage("Dočítané", "Nový text načítaš skratkou " + Native.HotkeyText(Preferences.Modifiers, Preferences.Key) + "."); }
        catch (OperationCanceledException) { player.SetMessage("Zastavené", "Môžeš načítať nový text zo schránky."); }
        catch (Exception ex) { player.SetMessage("Čítanie sa nepodarilo", ex.Message + " Požiadavku automaticky neopakujem."); }
        finally { Session = null; session.Dispose(); player.Refresh(); }
    }
    async Task FetchCost(Bill bill, string key)
    {
        for (int attempt = 0; attempt < 20 && !lifetime.IsCancellationRequested; attempt++) {
            try {
                if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(15), lifetime.Token);
                bill.Cost = await Api.Cost(bill.Id!, key, lifetime.Token);
                if (bill.Cost.HasValue) { Usage.Update(bill); return; }
            } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
            catch (Exception) { }
        }
    }
    internal void TogglePause() => Session?.TogglePause();
    internal async Task RefreshPendingCosts(CancellationToken token)
    {
        string key;
        try { key = Preferences.ReadKey(); } catch { return; }
        if (string.IsNullOrWhiteSpace(key)) return;
        foreach (var bill in Usage.Snapshot().Where(b => !b.Cost.HasValue && b.Id is not null).OrderByDescending(b => b.At).Take(100)) {
            token.ThrowIfCancellationRequested();
            try { bill.Cost = await Api.Cost(bill.Id!, key, token); if (bill.Cost.HasValue) Usage.Update(bill); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch { }
        }
    }
    internal void Stop() => Session?.Stop();
    internal void SetSpeed(double speed) { Preferences.Speed = speed; Session?.SetSpeed(speed); SavePreferencesQuietly(); }
    internal void SavePreferencesQuietly() { try { Preferences.Save(); } catch (Exception ex) { player?.SetMessage("Nastavenie sa nepodarilo uložiť", ex.Message); } }
    internal async Task Update()
    {
        if (updating) return; updating = true; string? job = null;
        try {
            var owner = settings?.IsVisible == true ? (Window)settings : player?.IsVisible == true ? player : null;
            var progress = new UpdateProgressWindow(owner); progress.Run(); job = progress.Job;
            if (progress.Cancelled) return;
            if (progress.Error is not null) throw progress.Error;
            if (progress.Offer is null) { MessageBox.Show("Máš aktuálnu verziu Hlas " + Updates.CurrentVersion.ToString(3), "Hlas · aktualizácia"); return; }
            if (job is null) throw new InvalidOperationException("Aktualizácia sa nestiahla.");
            string message = $"Verzia {progress.Offer.Manifest.Version} je overená. Nainštalovať a reštartovať Hlas?" + (Session is not null ? "\n\nAktuálne čítanie sa zastaví." : "");
            if (MessageBox.Show(message, "Hlas · aktualizácia", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            Updates.StartInstaller(job); job = null; ExitApp();
        } catch (Exception ex) { MessageBox.Show("Aktualizácia sa nepodarila: " + ex.Message, "Hlas · aktualizácia"); }
        finally { if (job is not null) await Task.Run(() => Updates.CleanJob(job)); updating = false; }
    }
    internal void ExitApp() { Exiting = true; Stop(); SavePreferencesQuietly(); Shutdown(); }
    protected override void OnExit(ExitEventArgs e)
    {
        Exiting = true; lifetime.Cancel(); Session?.Stop(); tray?.Dispose(); hotkeys?.Dispose(); wait?.Unregister(null); showEvent?.Dispose(); mutex?.Dispose(); Api.Dispose(); base.OnExit(e);
    }
}
