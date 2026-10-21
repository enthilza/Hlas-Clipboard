using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace Hlas;

internal sealed class PlayerWindow : Window
{
    readonly App app;
    readonly TextBlock title = Ui.Text("Text, ktorý môžeš počúvať.", 18);
    readonly TextBlock status = Ui.Text("Skopíruj text a stlač Ctrl + Alt + V.", 12, Ui.Muted);
    readonly TextBlock cost = Ui.Text("Pripravené", 11, Ui.Muted);
    readonly TextBlock position = Ui.Text("", 11, Ui.Muted);
    readonly Button pause, stop;
    readonly ProgressBar progress = new() { Height = 3, Minimum = 0, Maximum = 100, Foreground = Ui.Accent, Background = Theme.Brush("#111A1E"), BorderThickness = new Thickness(0), Margin = new Thickness(0, 14, 0, 14) };
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    internal PlayerWindow(App app)
    {
        this.app = app;
        Title = "Hlas · prehrávač"; Width = 460; SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true; ShowActivated = false;
        var body = new StackPanel { Margin = new Thickness(22, 15, 22, 18) };
        var shell = new Border { Child = body, Margin = new Thickness(12), Background = new SolidColorBrush(Color.FromArgb(242, 24, 34, 38)), CornerRadius = new CornerRadius(18), BorderBrush = Ui.Brush("#445E6F69"), BorderThickness = new Thickness(1), Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = .35 } };
        Content = shell;
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 18), LastChildFill = true };
        header.MouseLeftButtonDown += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); };
        var controls = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(controls, Dock.Right); header.Children.Add(controls);
        void Small(string text, string icon, Action action) { var b = Ui.Button("", icon, action); b.ToolTip = text; b.Padding = new Thickness(6); b.MinHeight = 27; b.Width = 30; b.Margin = new Thickness(3, 0, 0, 0); controls.Children.Add(b); }
        Small("Nastavenia", "settings", app.OpenSettings);
        Small("Aktualizovať", "update", () => _ = app.Update());
        Small("Skryť prehrávač · čítanie pokračuje", "close", Hide);
        var brand = Ui.Text("▥  hlas", 19, Ui.Accent); header.Children.Add(brand); body.Children.Add(header);
        title.TextTrimming = TextTrimming.CharacterEllipsis; body.Children.Add(title);
        status.TextWrapping = TextWrapping.Wrap; status.Margin = new Thickness(0, 7, 0, 0); status.MaxHeight = 66; body.Children.Add(status);
        body.Children.Add(progress);
        var actions = new DockPanel();
        var speedPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center }; DockPanel.SetDock(speedPanel, Dock.Right); actions.Children.Add(speedPanel);
        speedPanel.Children.Add(Ui.Text("Rýchlosť", 11, Ui.Muted));
        var speed = new ComboBox { Width = 80, Height = 34, Margin = new Thickness(8, 0, 0, 0) };
        foreach (double value in new[] { .5, .75, 1, 1.25, 1.5, 1.75, 2 }) speed.Items.Add(new ComboBoxItem { Content = $"{value:0.##}×", Tag = value });
        foreach (ComboBoxItem item in speed.Items) if (Math.Abs((double)item.Tag - app.Preferences.Speed) < .01) speed.SelectedItem = item;
        if (speed.SelectedIndex < 0) speed.SelectedIndex = 2;
        speed.SelectionChanged += (_, _) => { if (speed.SelectedItem is ComboBoxItem item) app.SetSpeed((double)item.Tag); }; speedPanel.Children.Add(speed);
        var left = new StackPanel { Orientation = Orientation.Horizontal }; actions.Children.Add(left);
        pause = Ui.Button("Prehrať", null, app.TogglePause, true); pause.MinWidth = 105; pause.ToolTip = "Pozastaviť / pokračovať"; pause.IsEnabled = false; left.Children.Add(pause);
        stop = Ui.Button("Zastaviť", null, app.Stop); stop.Margin = new Thickness(8, 0, 0, 0); stop.IsEnabled = false; left.Children.Add(stop);
        body.Children.Add(actions);
        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0) }; DockPanel.SetDock(position, Dock.Right); footer.Children.Add(position); footer.Children.Add(cost); body.Children.Add(footer);
        Closing += (_, e) => { if (!app.Exiting) { e.Cancel = true; Hide(); } };
        LocationChanged += (_, _) => { if (IsLoaded) { app.Preferences.Left = Left; app.Preferences.Top = Top; } };
        IsVisibleChanged += (_, _) => { if (!IsVisible) app.SavePreferencesQuietly(); };
        timer.Tick += (_, _) => Refresh(); timer.Start(); Closed += (_, _) => timer.Stop();
        var area = SystemParameters.WorkArea;
        Left = app.Preferences.Left ?? area.Right - Width - 20; Top = app.Preferences.Top ?? area.Bottom - 285;
        if (!double.IsFinite(Left) || !double.IsFinite(Top) || Left + Width < SystemParameters.VirtualScreenLeft || Left > SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth || Top < SystemParameters.VirtualScreenTop || Top + 50 > SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight) { Left = area.Right - Width - 20; Top = area.Bottom - 285; }
    }
    internal void SetMessage(string heading, string message) { title.Text = heading; status.Text = message; status.ToolTip = message; }
    internal void Refresh()
    {
        var session = app.Session;
        pause.IsEnabled = stop.IsEnabled = session is not null;
        progress.IsIndeterminate = session is not null && (!session.Started || session.Queue.Waiting);
        if (session is not null) {
            title.Text = session.Paused ? "Pozastavené" : !session.Started ? "O chvíľu počúvaš…" : session.Queue.Waiting ? "Pripravujem ďalšiu časť…" : "Čítam text zo schránky";
            status.Text = session.Preparation; status.ToolTip = session.Preparation;
            pause.Content = session.Paused ? "▶  Pokračovať" : "Ⅱ  Pauza";
            progress.Value = session.ProductionDone ? 100 : 0;
            position.Text = $"časť {Math.Max(1, session.Queue.Part)} / {session.Prepared}{(session.ProductionDone ? "" : "+")}";
        } else { pause.Content = "▶  Prehrať"; position.Text = ""; progress.Value = 0; }
        cost.Text = app.CostSummary; cost.ToolTip = app.CostDetails;
    }
}
