using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Hlas;

internal static class TextChunks
{
    // Sizes are latency targets, not claims about the provider's input limit.
    internal static IEnumerable<string> Split(string text, int first = 450, int following = 1800)
    {
        if (first < 2 || following < 2) throw new ArgumentOutOfRangeException(nameof(first));
        string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        string pending = ""; int target = first;
        foreach (string paragraph in Regex.Split(normalized, @"\n\s*\n")) {
            string rest = paragraph.Trim();
            if (rest.Length == 0) continue;
            if (pending.Length > 0 && pending.Length + rest.Length + 2 > target) {
                yield return pending; pending = ""; target = following;
            }
            while (rest.Length > target) {
                int cut = Boundary(rest, target);
                yield return rest[..cut].Trim(); rest = rest[cut..].TrimStart(); target = following;
            }
            if (rest.Length > 0) pending = pending.Length == 0 ? rest : pending + "\n\n" + rest;
        }
        if (pending.Length > 0) yield return pending;
    }
    static int Boundary(string value, int target)
    {
        for (int i = target - 1; i >= target / 3; i--)
            if (char.IsWhiteSpace(value[i]) && value[i - 1] is '.' or '!' or '?' or '…') return i;
        for (int i = target; i > 0; i--) if (char.IsWhiteSpace(value[i])) return i;
        return char.IsHighSurrogate(value[target - 1]) ? target - 1 : target;
    }
}
