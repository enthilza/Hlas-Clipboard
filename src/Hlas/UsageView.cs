using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;

namespace Hlas;

internal sealed class UsageView : StackPanel
{
    readonly App app;
    readonly TextBlock total = Ui.Text("", 28, Ui.Accent), periods = Ui.Text("", 12, Ui.Muted), detail = Ui.Text("", 12, Ui.Muted);
    readonly StackPanel models = new();
    readonly DataGrid rows = new();
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly CancellationToken token;
    internal UsageView(App app, CancellationToken token)
    {
        this.app = app; this.token = token; Margin = new Thickness(25);
        Children.Add(Ui.Heading("Použitie a ceny"));
        Children.Add(Ui.Text("Celkové zistené náklady za celé používanie", 12, Ui.Muted)); total.Margin = new Thickness(0, 10, 0, 8); Children.Add(total);
        Children.Add(periods); detail.TextWrapping = TextWrapping.Wrap; detail.Margin = new Thickness(0, 8, 0, 12); Children.Add(detail);
        var refresh = Ui.Button("Dočítať chýbajúce ceny", "update", async () => {
            try { detail.Text = "Overujem skutočné ceny v OpenRouteri…"; await app.RefreshPendingCosts(token); Refresh(); }
            catch (OperationCanceledException) { }
        }); refresh.HorizontalAlignment = HorizontalAlignment.Left; Children.Add(refresh);
        var modelHeading = Ui.Text("PODĽA MODELU", 11, Ui.Muted); modelHeading.Margin = new Thickness(0, 22, 0, 8); Children.Add(modelHeading);
        Children.Add(new ScrollViewer { Content = models, MaxHeight = 145, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var recentHeading = Ui.Text("POSLEDNÝCH 100 VOLANÍ", 11, Ui.Muted); recentHeading.Margin = new Thickness(0, 20, 0, 8); Children.Add(recentHeading);
        rows.AutoGenerateColumns = false; rows.IsReadOnly = true; rows.CanUserAddRows = false; rows.CanUserDeleteRows = false;
        rows.HeadersVisibility = DataGridHeadersVisibility.Column; rows.GridLinesVisibility = DataGridGridLinesVisibility.None;
        rows.Background = Theme.Brush("#172131"); rows.RowBackground = Theme.Brush("#202B3B"); rows.AlternatingRowBackground = Theme.Brush("#172131"); rows.Foreground = Theme.Brush("#EDF4FF"); rows.BorderThickness = new Thickness(0);
        rows.Height = 250; rows.RowHeight = 31; rows.FontSize = 11;
        var header = new Style(typeof(DataGridColumnHeader)); header.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("Selected"))); header.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("Text"))); header.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(7))); rows.ColumnHeaderStyle = header;
        void Column(string label, string field, double width) => rows.Columns.Add(new DataGridTextColumn { Header = label, Binding = new Binding(field), Width = width });
        Column("Kedy", "Time", 115); Column("Typ", "Kind", 50); Column("Model", "Model", 190); Column("Cena USD", "Cost", 125); Column("Stav", "State", 140);
        Children.Add(rows);
        var note = Ui.Text("Ukladá sa dátum, model, počet znakov a cena. Text zo schránky ani API kľúč nie sú súčasťou histórie. Prehľad zahŕňa iba volania z tejto aplikácie.", 11, Ui.Muted); note.TextWrapping = TextWrapping.Wrap; note.Margin = new Thickness(0, 14, 0, 0); Children.Add(note);
        timer.Tick += (_, _) => { if (IsVisible) Refresh(); };
        Loaded += (_, _) => { timer.Start(); Refresh(); }; Unloaded += (_, _) => timer.Stop();
        Refresh();
    }
    static string Money(decimal amount) => "$" + amount.ToString("0.############################", CultureInfo.InvariantCulture);
    internal void Refresh()
    {
        var items = app.Usage.Snapshot(); var now = DateTime.Now;
        decimal Sum(Func<Bill, bool> predicate) => items.Where(b => b.Cost.HasValue && predicate(b)).Sum(b => b.Cost!.Value);
        total.Text = Money(Sum(_ => true));
        periods.Text = $"Dnes: {Money(Sum(b => b.At.LocalDateTime.Date == now.Date))}   ·   Tento mesiac: {Money(Sum(b => b.At.LocalDateTime.Year == now.Year && b.At.LocalDateTime.Month == now.Month))}";
        int unknown = items.Count(b => !b.Cost.HasValue && b.Provider == "OpenRouter");
        int internalCalls = items.Count(b => b.Provider != "OpenRouter");
        detail.Text = $"{items.Length} volaní · Text {Money(Sum(b => b.Kind == "Text"))} · Audio {Money(Sum(b => b.Kind == "Audio"))}" +
            (unknown > 0 ? $"\n{unknown} volaní ešte nemá známu cenu a nie je započítaných. Neznáma cena neznamená nulu." : "\nVšetky zaznamenané ceny sú zahrnuté.") +
            (internalCalls > 0 ? $"\nInterný model: {internalCalls} volaní bez cenových údajov, mimo súčtu." : "") +
            (app.Usage.Warning is null ? "" : "\n" + app.Usage.Warning);
        models.Children.Clear();
        foreach (var group in items.GroupBy(b => new { b.Kind, b.Model }).OrderByDescending(g => g.Where(b => b.Cost.HasValue).Sum(b => b.Cost!.Value))) {
            var row = Ui.Text($"{group.Key.Model}   ·   {group.Count()}×   ·   {Money(group.Where(b => b.Cost.HasValue).Sum(b => b.Cost!.Value))}", 12);
            row.Margin = new Thickness(0, 3, 0, 3); row.TextWrapping = TextWrapping.Wrap; models.Children.Add(row);
        }
        if (items.Length == 0) models.Children.Add(Ui.Text("Zatiaľ žiadne volania.", 12, Ui.Muted));
        rows.ItemsSource = items.OrderByDescending(b => b.At).Take(100).Select(b => new { Time = b.At.LocalDateTime.ToString("dd.MM. HH:mm:ss"), b.Kind, b.Model, Cost = b.Cost.HasValue ? Money(b.Cost.Value) : b.Provider != "OpenRouter" ? "interný · bez ceny" : "nezistená", State = b.Status }).ToArray();
    }
}
