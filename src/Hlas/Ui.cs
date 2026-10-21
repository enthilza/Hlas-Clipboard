using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Hlas;

internal static class Ui
{
    public static SolidColorBrush Brush(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }
    public static readonly Brush Accent = Theme.Brush("#83BFFF"), Muted = Theme.Brush("#A7B8CD"), Panel = Theme.Brush("#202B3B");
    public static ImageSource Logo
    {
        get
        {
            using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/Hlas.ico")).Stream;
            var decoder = new System.Windows.Media.Imaging.IconBitmapDecoder(stream, System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[decoder.Frames.Count - 1]; frame.Freeze(); return frame;
        }
    }
    static readonly Dictionary<string, string> Icons = new()
    {
        ["number"]="M12,2 A10,10 0 1 1 11.99,2 M9,9 L12,6 L12,18 M9,18 L15,18",
        ["redact"]="M3,5 L21,5 L21,19 L3,19 Z M5,9 L19,9 M5,12 L19,12 M5,15 L19,15",
        ["picker"]="M15,3 L21,9 M17,1 L23,7 M17,7 L6,18 L2,22 L3,17 L14,6 Z",
        ["resize"]="M3,9 L3,3 L9,3 M3,3 L10,10 M15,21 L21,21 L21,15 M14,14 L21,21",
        ["background"]="M3,3 L21,3 L21,21 L3,21 Z M3,9 L21,9 M9,3 L9,21 M3,21 L21,3",
        ["scan"]="M9,3 L4,3 Q3,3 3,4 L3,9 M15,3 L20,3 Q21,3 21,4 L21,9 M3,15 L3,20 Q3,21 4,21 L9,21 M15,21 L20,21 Q21,21 21,20 L21,15",
        ["arrow"]="M5,19 L19,5 M8,5 L19,5 L19,16",
        ["move"]="M5,3 L19,13 L12,14 L9,21 Z",
        ["update"]="M20,9 A8,8 0 1 0 20,15 M20,3 L20,9 L14,9",
        ["left"]="M15,5 L8,12 L15,19",
        ["right"]="M9,5 L16,12 L9,19",
        ["rotate"]="M19,8 A8,8 0 1 0 20,15 M19,2 L19,8 L13,8",
        ["mirror"]="M12,2 L12,22 M8,5 L2,18 L8,18 Z M16,5 L22,18 L16,18 Z",
        ["theme"]="M12,3 A9,9 0 1 0 21,12 A7,7 0 0 1 12,3 Z",
        ["rectangle"]="M6,4 L18,4 Q20,4 20,6 L20,18 Q20,20 18,20 L6,20 Q4,20 4,18 L4,6 Q4,4 6,4 Z",
        ["ellipse"]="M12,3 A9,9 0 1 1 11.99,3 Z",
        ["pen"]="M4,16 L16,4 Q17,3 18,4 L20,6 Q21,7 20,8 L8,20 L3,21 Z M14,6 L18,10",
        ["highlight"]="M4,15 L14,5 L20,11 L10,21 Z M3,22 L11,22 M14,5 L17,2 L23,8 L20,11",
        ["text"]="M4,5 L20,5 M12,5 L12,21 M8,21 L16,21",
        ["selection"]="M3,8 L3,3 L8,3 M16,3 L21,3 L21,8 M21,16 L21,21 L16,21 M8,21 L3,21 L3,16",
        ["undo"]="M8,5 L3,10 L8,15 M3,10 L14,10 Q21,10 21,17",
        ["redo"]="M16,5 L21,10 L16,15 M21,10 L10,10 Q3,10 3,17",
        ["copy"]="M9,8 L19,8 Q21,8 21,10 L21,20 Q21,22 19,22 L9,22 Q7,22 7,20 L7,10 Q7,8 9,8 Z M16,4 L16,2 L2,2 L2,16 L4,16",
        ["save"]="M12,3 L12,16 M7,11 L12,16 L17,11 M4,17 L4,21 L20,21 L20,17",
        ["share"]="M8,6 L4,6 L4,21 L20,21 L20,13 M12,13 L12,3 M7,8 L12,3 L17,8",
        ["settings"]="M3,6 L9,6 M15,6 L21,6 M3,12 L13,12 M19,12 L21,12 M3,18 L5,18 M11,18 L21,18 M9,3 L15,3 L15,9 L9,9 Z M13,9 L19,9 L19,15 L13,15 Z M5,15 L11,15 L11,21 L5,21 Z",
        ["monitor"]="M3,3 L21,3 L21,17 L3,17 Z M12,17 L12,22 M7,22 L17,22",
        ["crop"]="M6,2 L6,18 L22,18 M2,6 L18,6 L18,22",
        ["close"]="M6,6 L18,18 M18,6 L6,18",
        ["minimize"]="M5,12 L19,12",
        ["maximize"]="M5,5 L19,5 L19,19 L5,19 Z",
        ["folder"]="M3,7 L3,4 L10,4 L13,7 L21,7 L21,20 L3,20 Z",
        ["image"]="M3,3 L21,3 L21,21 L3,21 Z M3,17 L9,11 L14,16 L17,13 L21,17 M15,7 L15,8",
        ["chevron"]="M6,9 L12,15 L18,9"
    };
    public static FrameworkElement Icon(string name, Brush? color = null, double size = 18)
    {
        var path = new Path { Data = Geometry.Parse(Icons.GetValueOrDefault(name, Icons["image"])), Stroke = color ?? Theme.Brush("#CCDCEE"), StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
        var canvas = new Canvas { Width = 24, Height = 24 }; canvas.Children.Add(path);
        return new Viewbox { Child = canvas, Width = size, Height = size, Stretch = Stretch.Uniform };
    }
    public static TextBlock Text(string text, double size = 13, Brush? color = null) => new() { Text = text, FontSize = size, Foreground = color ?? Theme.Brush("#EDF4FF"), VerticalAlignment = VerticalAlignment.Center };
    public static Button Button(string text, string? icon, Action click, bool primary = false)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var foreground = primary ? Theme.Brush("#102C4C") : Theme.Brush("#E8F1FF");
        if (icon != null) { var i = Icon(icon, foreground); i.Margin = new Thickness(0, 0, string.IsNullOrEmpty(text) ? 0 : 8, 0); row.Children.Add(i); }
        if (!string.IsNullOrEmpty(text)) row.Children.Add(Text(text, 12, foreground));
        var b = new Button { Content = row };
        if (primary) { b.Foreground = foreground; b.Background = Accent; b.BorderThickness = new Thickness(0); }
        b.Click += (_, _) => click();
        System.Windows.Automation.AutomationProperties.SetName(b, string.IsNullOrEmpty(text) ? icon ?? "" : text);
        return b;
    }
    public static Border Card(UIElement child, Thickness padding, string color = "#202B3B") => new() { Child = child, Padding = padding, Background = Theme.Brush(color), CornerRadius = new CornerRadius(12), BorderBrush = Theme.Brush("#16FFFFFF"), BorderThickness = new Thickness(1) };
    public static FormattedText Formatted(string text, double size, Brush brush) => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, 1);
    public static void SetupDialog(Window window, string title, double width = 490)
    {
        window.Title = title; window.Width = width; window.SizeToContent = SizeToContent.Height; window.ResizeMode = ResizeMode.NoResize; window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        window.Background = Theme.Brush("#202B3B"); window.Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/Hlas.ico"));
        window.SourceInitialized += (_, _) => Native.ModernWindow(window);
    }
    public static TextBlock Heading(string text) => new() { Text = text, FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
}


