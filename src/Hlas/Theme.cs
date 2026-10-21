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
        ["#91EFD3"] = "#9AE8CF", ["#EDF3F3"] = "#20332F", ["#A4B5BB"] = "#526961",
        ["#202B30"] = "#F5F9F7", ["#182226"] = "#EFF5F2", ["#111A1E"] = "#DDE8E3",
        ["#151F23"] = "#E5EEE9", ["#172125"] = "#E5EEE9", ["#1D282D"] = "#F3F8F5",
        ["#283E3A"] = "#E0F2E9", ["#304E44"] = "#C1E7D7", ["#2A3E3D"] = "#CDE7DC",
        ["#45675B"] = "#B3DEC9", ["#CCD9DD"] = "#3B554B", ["#E8F0F1"] = "#253E33",
        ["#13382D"] = "#13382D", ["#16FFFFFF"] = "#28364F43", ["#15FFFFFF"] = "#28364F43",
        ["#44FFFFFF"] = "#66435B4E", ["#55FFFFFF"] = "#80536B5D", ["#FFB5A8"] = "#A52920"
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
        Set("Accent", "#91EFD3", "#9AE8CF"); Set("Text", "#EDF3F3", "#20332F"); Set("Muted", "#A4B5BB", "#526961");
        Set("Background", "#182226", "#EFF5F2"); Set("Panel", "#202B30", "#F5F9F7");
        Set("Border", "#42575F", "#B4C9BE"); Set("Input", "#111B20", "#FFFFFF");
        Set("Hover", "#3B6254", "#CBE9DC"); Set("Selected", "#335345", "#BCE3D0");
        Set("Popup", "#26373C", "#FFFFFF"); Set("Track", "#52615E", "#859D91");
        Set("ButtonBackground", "#26373C", "#E4EEE8"); Set("Selection", "#357C69", "#9BD7BF");
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

