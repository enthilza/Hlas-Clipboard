using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Hlas;

public sealed record Settings
{
    public string SpeechModel { get; set; } = "google/gemini-3.8-flash-lite-tts";
    public string TextModel { get; set; } = "google/gemini-3.5-flash-lite";
    public string Voice { get; set; } = "Kore";
    public bool PrepareText { get; set; } = true;
    public bool AutoStart { get; set; }
    public bool LightTheme { get; set; }
    public uint Modifiers { get; set; } = 3;
    public uint Key { get; set; } = 86;
    public double Speed { get; set; } = 1;
    public double? Left { get; set; }
    public double? Top { get; set; }
    // Only update scratch files use LocalAppData. Configuration travels with the EXE.
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HlasClipboard");
    public static string PortableDirectory => Path.GetDirectoryName(Environment.ProcessPath!)!;
    public static string FilePath => Path.Combine(PortableDirectory, "hlas.settings.json");
    public static string KeyPath => Path.Combine(PortableDirectory, "openrouter.api");
    public static Settings Load()
    {
        try {
            var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new();
            if (!double.IsFinite(s.Speed)) s.Speed = 1;
            s.Speed = Math.Clamp(s.Speed, .5, 2);
            if (!Native.ValidHotkey(s.Modifiers, s.Key)) { s.Modifiers = 3; s.Key = 86; }
            return s;
        } catch { return new(); }
    }
    public string ReadKey()
    {
        return File.Exists(KeyPath) ? File.ReadAllText(KeyPath).Trim().Trim('\uFEFF') : "";
    }
    public void SetKey(string key)
    {
        key = key.Trim();
        if (key.Contains('\n') || key.Contains('\r')) throw new ArgumentException("API kľúč musí byť na jednom riadku.");
        File.WriteAllText(KeyPath + ".tmp", key, new UTF8Encoding(false));
        File.Move(KeyPath + ".tmp", KeyPath, true);
    }
    public void Save()
    {
        string temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, FilePath, true);
    }
    public static void SetAutoStart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) key.SetValue("HlasClipboard", $"\"{Environment.ProcessPath}\" --tray");
        else key.DeleteValue("HlasClipboard", false);
    }
}
