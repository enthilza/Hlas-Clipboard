using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.IO;

namespace Hlas;

internal sealed class SettingsWindow : Window
{
    readonly App app;
    readonly PasswordBox key = new() { Padding = new Thickness(10, 8, 10, 8), FontSize = 15 };
    readonly ComboBox speech = new() { IsEditable = true }, text = new() { IsEditable = true }, voice = new() { IsEditable = true };
    readonly CheckBox prepare = new() { Content = "Pred čítaním upraviť text na hovorenú reč" }, startup = new() { Content = "Spúšťať pri prihlásení do Windowsu" };
    readonly TextBox shortcut = new() { IsReadOnly = true };
    readonly CheckBox lightTheme = new() { Content = "Svetlá schéma" };
    readonly TextBlock notice = Ui.Text("", 12, Ui.Muted);
    readonly CancellationTokenSource lifetime = new();
    uint modifiers, virtualKey;
    Catalog catalog = Catalog.Bundled();
    bool preserveUnreadableKey;
    internal SettingsWindow(App app)
    {
        this.app = app; Ui.SetupDialog(this, "Hlas · nastavenia", Math.Min(760, SystemParameters.WorkArea.Width - 24)); MaxHeight = SystemParameters.WorkArea.Height - 24; ShowInTaskbar = true;
        var panel = new StackPanel { Margin = new Thickness(24, 16, 24, 20), Width = 740 };
        var tabs = new TabControl { Background = Theme.Brush("#202B3B"), BorderThickness = new Thickness(0) };
        tabs.Items.Add(new TabItem { Header = "Nastavenia", Content = new System.Windows.Controls.Viewbox { Child = panel, Stretch = System.Windows.Media.Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, VerticalAlignment = VerticalAlignment.Top } });
        tabs.Items.Add(new TabItem { Header = "Použitie a ceny", Content = new ScrollViewer { Content = new UsageView(app, lifetime.Token), VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        Content = tabs; SizeToContent = SizeToContent.Manual; Height = Math.Min(780, SystemParameters.WorkArea.Height - 24);
        var heading = new DockPanel(); var update = Ui.Button("Aktualizovať", "update", () => _ = app.Update()); DockPanel.SetDock(update, Dock.Right); heading.Children.Add(update); heading.Children.Add(Ui.Heading("Hlas · nastavenia")); panel.Children.Add(heading);
        panel.Children.Add(Ui.Text("Text zo schránky. Pokojne, nahlas, po svojom.", 13, Ui.Muted));
        lightTheme.IsChecked = app.Preferences.LightTheme; lightTheme.Margin = new Thickness(0, 12, 0, 0); panel.Children.Add(lightTheme);
        lightTheme.Checked += (_, _) => Theme.Apply(true); lightTheme.Unchecked += (_, _) => Theme.Apply(false);
        void Label(string value) { var label = Ui.Text(value, 12, Ui.Muted); label.Margin = new Thickness(0, 12, 0, 6); panel.Children.Add(label); }
        Label("OPENROUTER API KĽÚČ"); panel.Children.Add(key);
        key.Background = Theme.Brush("#101A29"); key.Foreground = Theme.Brush("#EDF4FF");
        try { key.Password = app.Preferences.ReadKey(); } catch (Exception ex) { preserveUnreadableKey = true; notice.Text = ex.Message; }
        key.PasswordChanged += (_, _) => preserveUnreadableKey = false;
        var import = Ui.Button("Načítať kľúč zo súboru…", "folder", () => {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Súbor s kľúčom|*.api;*.key;*.txt|Všetky súbory|*.*" };
            if (dialog.ShowDialog(this) == true) { try { key.Password = File.ReadAllText(dialog.FileName).Trim().Trim('\uFEFF'); } catch (Exception ex) { notice.Text = ex.Message; } }
        }); import.HorizontalAlignment = HorizontalAlignment.Left; import.Margin = new Thickness(0, 8, 0, 0); panel.Children.Add(import);
        Label("MODEL NA ČÍTANIE"); panel.Children.Add(speech);
        Label("HLAS"); panel.Children.Add(voice);
        prepare.IsChecked = app.Preferences.PrepareText; prepare.Margin = new Thickness(0, 14, 0, 9); panel.Children.Add(prepare);
        panel.Children.Add(text); text.IsEnabled = prepare.IsChecked == true;
        prepare.Checked += (_, _) => text.IsEnabled = true; prepare.Unchecked += (_, _) => text.IsEnabled = false;
        var explanation = Ui.Text("Úprava odstráni formátovanie a nahradí kód či zložité vzorce krátkou poznámkou. Text sa odosiela vybraným modelom cez OpenRouter.", 12, Ui.Muted); explanation.TextWrapping = TextWrapping.Wrap; explanation.Margin = new Thickness(0, 8, 0, 0); panel.Children.Add(explanation);
        var refresh = Ui.Button("Obnoviť zoznam modelov a hlasov", "update", async () => {
            try { notice.Text = "Načítavam katalóg…"; catalog = await app.Api.GetCatalog(lifetime.Token); Populate(); notice.Text = "Zoznam modelov a hlasov bol obnovený."; }
            catch (OperationCanceledException) { }
            catch (Exception ex) { notice.Text = "Katalóg sa nepodarilo obnoviť: " + ex.Message; }
        }); refresh.HorizontalAlignment = HorizontalAlignment.Left; refresh.Margin = new Thickness(0, 10, 0, 0); panel.Children.Add(refresh);
        // Editable selectors deliberately use a standard editable template, with themed brushes.
        foreach (var combo in new[] { speech, text, voice }) combo.Style = (Style)FindResource("EditableCombo");
        speech.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(RefreshVoices);
        speech.LostKeyboardFocus += (_, _) => RefreshVoices();
        speech.Text = app.Preferences.SpeechModel; text.Text = app.Preferences.TextModel; voice.Text = app.Preferences.Voice; Populate();
        Label("KLÁVESOVÁ SKRATKA · KLIKNI A STLAČ NOVÚ KOMBINÁCIU"); panel.Children.Add(shortcut);
        modifiers = app.Preferences.Modifiers; virtualKey = app.Preferences.Key; shortcut.Text = Native.HotkeyText(modifiers, virtualKey);
        shortcut.PreviewKeyDown += (_, e) => {
            e.Handled = true; var k = e.Key == Key.System ? e.SystemKey : e.Key; uint v = (uint)KeyInterop.VirtualKeyFromKey(k), m = 0;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) m |= 2; if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) m |= 1;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) m |= 4; if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) m |= 8;
            if (Native.ValidHotkey(m, v)) { modifiers = m; virtualKey = v; shortcut.Text = Native.HotkeyText(m, v); }
        };
        startup.IsChecked = app.Preferences.AutoStart; startup.Margin = new Thickness(0, 18, 0, 0); panel.Children.Add(startup);
        notice.TextWrapping = TextWrapping.Wrap; notice.Margin = new Thickness(0, 8, 0, 8); panel.Children.Add(notice);
        var bottom = new DockPanel(); var buttons = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(buttons, Dock.Right); bottom.Children.Add(buttons);
        var cancel = Ui.Button("Zavrieť", null, Close); cancel.Margin = new Thickness(0, 0, 10, 0); buttons.Children.Add(cancel); buttons.Children.Add(Ui.Button("Uložiť nastavenia", null, Save, true));
        bottom.Children.Add(Ui.Text("Verzia " + Updates.CurrentVersion.ToString(3), 11, Ui.Muted)); panel.Children.Add(bottom);
        Closed += (_, _) => { Theme.Apply(app.Preferences.LightTheme); lifetime.Cancel(); lifetime.Dispose(); key.Clear(); };
    }
    void Populate()
    {
        string s = speech.Text, t = text.Text, v = voice.Text;
        speech.ItemsSource = catalog.Speech.Select(m => m.Id).ToArray(); text.ItemsSource = catalog.Text;
        speech.Text = s; text.Text = t; RefreshVoices(); voice.Text = v;
    }
    void RefreshVoices()
    {
        string previous = voice.Text;
        string model = speech.SelectedItem as string ?? speech.Text;
        var voices = catalog.Speech.FirstOrDefault(m => m.Id == model)?.Voices ?? Array.Empty<string>();
        voice.ItemsSource = voices;
        voice.Text = voices.Contains(previous) || voices.Length == 0 ? previous : voices.Contains("Kore") ? "Kore" : voices[0];
    }
    void Save()
    {
        try {
            if (!speech.Text.Contains('/') || (prepare.IsChecked == true && !text.Text.Contains('/'))) throw new InvalidOperationException("Zadaj celé ID modelu: poskytovateľ/model.");
            if (string.IsNullOrWhiteSpace(voice.Text)) throw new InvalidOperationException("Vyber alebo zadaj hlas.");
            var next = app.Preferences with { LightTheme = lightTheme.IsChecked == true, SpeechModel = speech.Text.Trim(), TextModel = text.Text.Trim(), Voice = voice.Text.Trim(), PrepareText = prepare.IsChecked == true, AutoStart = startup.IsChecked == true, Modifiers = modifiers, Key = virtualKey };
            app.ApplySettings(next, preserveUnreadableKey ? null : key.Password); Close();
        } catch (Exception ex) { notice.Text = ex.Message; }
    }
}
