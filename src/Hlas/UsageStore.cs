using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Hlas;

internal sealed class UsageStore
{
    readonly object gate = new();
    readonly string path;
    readonly List<Bill> entries;
    internal string? Warning { get; private set; }
    internal UsageStore(string path)
    {
        this.path = path;
        try { entries = File.Exists(path) ? JsonSerializer.Deserialize<List<Bill>>(File.ReadAllText(path)) ?? new() : new(); }
        catch (Exception) {
            // Preserve a corrupt journal rather than silently replacing the spending history.
            entries = new(); Warning = "Históriu použitia sa nepodarilo načítať. Pôvodný súbor zostal zachovaný.";
            if (File.Exists(path + ".bak")) {
                try { entries = JsonSerializer.Deserialize<List<Bill>>(File.ReadAllText(path + ".bak")) ?? new(); Warning = "História bola načítaná zo zálohy; posledný zápis môže chýbať."; }
                catch { }
            }
        }
    }
    internal Bill[] Snapshot() { lock (gate) return entries.Select(b => b with { }).ToArray(); }
    internal Bill Start(string kind, string model, int characters)
    {
        var bill = new Bill(kind, model, null) { Characters = characters, Status = "Odoslané" };
        lock (gate) { entries.Add(bill); Save(); }
        return bill;
    }
    internal void Update(Bill bill)
    {
        lock (gate) {
            int index = entries.FindIndex(b => b.RecordId == bill.RecordId);
            if (index >= 0) { entries[index] = bill; Save(); }
        }
    }
    void Save()
    {
        try {
            if (Warning?.StartsWith("Históriu") == true) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(path)) File.Replace(path + ".tmp", path, path + ".bak", true);
            else File.Move(path + ".tmp", path);
        } catch (Exception) { Warning = "Históriu sa nepodarilo uložiť. Skontroluj právo zápisu do priečinka aplikácie."; }
    }
}
