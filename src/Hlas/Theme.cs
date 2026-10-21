using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace Hlas;

internal static class Theme
{
    public static bool IsLight { get; private set; }
    static readonly Dictionary<string, string> Light = new(StringComparer.OrdinalIgnoreCase)
    {
        ["#F2172131"] = "#F2EDF3FA", ["#445E7899"] = "#66859BBB",
        ["#83BFFF"] = "#2463A5", ["#EDF4FF"] = "#182D47", ["#A7B8CD"] = "#536B86",
        ["#202B3B"] = "#F5F8FD", ["#172131"] = "#EDF3FA", ["#101A29"] = "#DBE6F4",
        ["#152031"] = "#E4EDF8", ["#172334"] = "#E4EDF8", ["#1D293B"] = "#F3F7FC",
        ["#293F5B"] = "#E0ECFA", ["#304D70"] = "#C1D9F5", ["#2A405D"] = "#CDDEF5",
        ["#45658A"] = "#B3CFF0", ["#CCDCEE"] = "#3B5575", ["#E8F1FF"] = "#253E60",
        ["#102C4C"] = "#FFFFFF", ["#16FFFFFF"] = "#28364A66", ["#15FFFFFF"] = "#28364A66",
        ["#44FFFFFF"] = "#66435978", ["#55FFFFFF"] = "#80536A8A", ["#FFB5A8"] = "#A52920"
    };
    static readonly Dictionary<string, SolidColorBrush> brushes = new(StringComparer.OrdinalIgnoreCase);
    public static Color Color(string dark) => (Color)ColorConverter.ConvertFromString(IsLight && Light.TryGetValue(dark, out var light) ? light : dark);
    public static SolidColorBrush Brush(string dark)
    {
        if (!brushes.TryGetValue(dark, out var brush)) brushes[dark] = brush = new SolidColorBrush(Color(dark));
        return brush;
    }
    public static void Apply(bool light)
    {
        IsLight = light;
        foreach (var (hex, brush) in brushes) brush.Color = Color(hex);
        var resources = Application.Current.Resources;
        void Set(string key, string dark, string bright) => resources[key] = Ui.Brush(light ? bright : dark);
        Set("Accent", "#83BFFF", "#2463A5"); Set("Text", "#EDF4FF", "#182D47"); Set("Muted", "#A7B8CD", "#536B86");
        Set("Background", "#172131", "#EDF3FA"); Set("Panel", "#202B3B", "#F5F8FD");
        Set("Border", "#435874", "#B4C8E2"); Set("Input", "#111C2D", "#FFFFFF");
        Set("Hover", "#365780", "#D2E4FA"); Set("Selected", "#304F76", "#BDD8F7");
        Set("Popup", "#26384F", "#FFFFFF"); Set("Track", "#526781", "#859BB8");
        Set("ButtonBackground", "#26384F", "#E4EDF8"); Set("Selection", "#356BA8", "#A7CFF9");
        resources[SystemColors.ControlBrushKey] = resources["Panel"];
        resources["AccentText"] = Ui.Brush(light ? "#FFFFFF" : "#102C4C");
        foreach (Window window in Application.Current.Windows)
            if (new WindowInteropHelper(window).Handle != IntPtr.Zero) Native.ModernWindow(window);
    }
    public static void StyleTray(Forms.ContextMenuStrip menu)
    {
        menu.Renderer = new Forms.ToolStripProfessionalRenderer(new TrayColors());
        var background = (SolidColorBrush)Application.Current.Resources["Popup"];
        var foreground = (SolidColorBrush)Application.Current.Resources["Text"];
        menu.BackColor = System.Drawing.Color.FromArgb(background.Color.R, background.Color.G, background.Color.B);
        menu.ForeColor = System.Drawing.Color.FromArgb(foreground.Color.R, foreground.Color.G, foreground.Color.B);
        foreach (Forms.ToolStripItem item in menu.Items) item.ForeColor = menu.ForeColor;
    }
    sealed class TrayColors : Forms.ProfessionalColorTable
    {
        System.Drawing.Color Get(string key) { var c = ((SolidColorBrush)Application.Current.Resources[key]).Color; return System.Drawing.Color.FromArgb(c.R, c.G, c.B); }
        public override System.Drawing.Color ToolStripDropDownBackground => Get("Popup");
        public override System.Drawing.Color MenuItemSelected => Get("Hover");
        public override System.Drawing.Color MenuItemBorder => Get("Border");
        public override System.Drawing.Color MenuBorder => Get("Border");
        public override System.Drawing.Color SeparatorDark => Get("Border");
        public override System.Drawing.Color SeparatorLight => Get("Popup");
    }
}

