using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Hlas;

internal static class Native
{
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    internal static bool ValidHotkey(uint modifiers, uint key) => modifiers is > 0 and <= 15 && key is >= 8 and <= 254 && key is not (16 or 17 or 18 or 91 or 92);
    internal static string HotkeyText(uint modifiers, uint key) => ((modifiers & 2) != 0 ? "Ctrl + " : "") + ((modifiers & 1) != 0 ? "Alt + " : "") + ((modifiers & 4) != 0 ? "Shift + " : "") + ((modifiers & 8) != 0 ? "Win + " : "") + (key is >= 48 and <= 57 ? ((char)key).ToString() : KeyInterop.KeyFromVirtualKey((int)key).ToString());
    internal static void ModernWindow(Window window)
    {
        var h = new WindowInteropHelper(window).Handle;
        if (h == IntPtr.Zero) return;
        if (window.AllowsTransparency || window.WindowStyle == WindowStyle.None)
        {
            // The layered player draws its own outline and shadow. DWM rounding
            // otherwise frames the entire HWND, including the transparent margin.
            int noRounding = 1, noBorderColor = unchecked((int)0xFFFFFFFE);
            DwmSetWindowAttribute(h, 33, ref noRounding, 4);
            DwmSetWindowAttribute(h, 34, ref noBorderColor, 4);
            return;
        }
        int dark = Theme.IsLight ? 0 : 1, round = 2;
        DwmSetWindowAttribute(h, 20, ref dark, 4); DwmSetWindowAttribute(h, 33, ref round, 4);
    }
}

internal sealed class Hotkeys : IDisposable
{
    readonly HwndSource source = new(new HwndSourceParameters("Hlas hotkey") { ParentWindow = new IntPtr(-3) });
    int current, next = 100;
    uint modifiers, key;
    internal event Action? Pressed;
    internal Hotkeys() => source.AddHook(Hook);
    internal bool Apply(uint m, uint k)
    {
        if (!Native.ValidHotkey(m, k)) return false;
        if (current != 0 && m == modifiers && k == key) return true;
        int id = ++next;
        if (!Native.RegisterHotKey(source.Handle, id, m | 0x4000, k)) return false;
        if (current != 0) Native.UnregisterHotKey(source.Handle, current);
        current = id; modifiers = m; key = k; return true;
    }
    IntPtr Hook(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == 0x0312 && w.ToInt32() == current) { handled = true; Pressed?.Invoke(); }
        return IntPtr.Zero;
    }
    public void Dispose() { if (current != 0) Native.UnregisterHotKey(source.Handle, current); source.Dispose(); }
}
