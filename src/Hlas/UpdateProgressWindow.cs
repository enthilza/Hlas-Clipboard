using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Hlas;

internal sealed record UpdateProgress(string Message, int? Percent = null, string Detail = "Prosím, počkaj…");

internal sealed class UpdateProgressWindow : Window
{
    readonly TextBlock message, detail;
    readonly ProgressBar bar;
    readonly Button cancel;
    readonly CancellationTokenSource cancellation = new();
    bool finished;
    internal string? Job { get; private set; }
    internal UpdateOffer? Offer { get; private set; }
    internal Exception? Error { get; private set; }
    internal bool Cancelled { get; private set; }

    internal UpdateProgressWindow(Window? owner)
    {
        Owner = owner; Ui.SetupDialog(this, "Hlas — aktualizácia", 510);
        ShowInTaskbar = false;
        var panel = new StackPanel { Margin = new Thickness(28) }; Content = panel;
        panel.Children.Add(Ui.Heading("Aktualizácia Hlasu"));
        message = Ui.Text("Vyhľadávam novú verziu…", 14); message.TextWrapping = TextWrapping.Wrap;
        message.Margin = new Thickness(0, 14, 0, 0); panel.Children.Add(message);
        bar = new ProgressBar { Minimum = 0, Maximum = 100, IsIndeterminate = true, Height = 12,
            Foreground = Ui.Accent, Background = Theme.Brush("#101A29"), BorderThickness = new Thickness(0), Margin = new Thickness(0, 18, 0, 12) };
        System.Windows.Automation.AutomationProperties.SetName(bar, "Priebeh aktualizácie"); panel.Children.Add(bar);
        detail = Ui.Text("Prosím, počkaj…", 12, Ui.Muted); detail.TextWrapping = TextWrapping.Wrap; detail.MinHeight = 36; panel.Children.Add(detail);
        cancel = Ui.Button("Zrušiť", null, RequestCancel); cancel.HorizontalAlignment = HorizontalAlignment.Right;
        cancel.Margin = new Thickness(0, 18, 0, 0); panel.Children.Add(cancel);
        Closing += (_, e) => { if (!finished) { e.Cancel = true; RequestCancel(); } };
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; RequestCancel(); } };
    }

    internal void Display(UpdateProgress state)
    {
        if (finished || cancellation.IsCancellationRequested) return;
        message.Text = state.Message; detail.Text = state.Detail;
        bar.IsIndeterminate = state.Percent is null;
        bar.Value = Math.Clamp(state.Percent ?? 0, 0, 100);
    }

    void RequestCancel()
    {
        if (finished || cancellation.IsCancellationRequested) return;
        cancellation.Cancel(); cancel.IsEnabled = false;
        message.Text = "Ruším aktualizáciu…"; detail.Text = "Odstraňujem rozpracované sťahovanie."; bar.IsIndeterminate = true;
    }

    internal void Run()
    {
        Loaded += async (_, _) =>
        {
            var progress = new Progress<UpdateProgress>(Display);
            try
            {
                // Keep networking, hashing and disk operations away from the WPF dispatcher.
                await Task.Run(async () =>
                {
                    Offer = await Updates.CheckAsync(cancellation.Token);
                    if (Offer is not null) Job = await Updates.DownloadAsync(Offer, progress, cancellation.Token);
                });
                if (cancellation.IsCancellationRequested)
                {
                    if (Job is not null) await Task.Run(() => Updates.CleanJob(Job));
                    Job = null; Cancelled = true;
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { Cancelled = true; }
            catch (Exception ex) { Error = ex; }
            finally { finished = true; Close(); }
        };
        try { ShowDialog(); }
        finally { cancellation.Dispose(); }
    }
}

