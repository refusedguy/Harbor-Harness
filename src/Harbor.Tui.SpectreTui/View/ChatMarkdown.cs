using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Spectre.Console;
using Spectre.Tui;
namespace Harbor.Tui.SpectreTui.View;
/// <summary>
///     Optional inline markdown → TextSpan. Used for committed transcript only.
///     Stream path should stay plain (Enable does not apply there by policy).
/// </summary>
internal static class ChatMarkdown
{

    private const int MaxInlineLength = 4096;

    // §FP-004 / §PERF-009 (RESOLVED): Cache is now a ConcurrentDictionary, so the
    // per-render `lock(Cache)` is gone. ConcurrentDictionary's GetOrAdd handles
    // the read+populate race atomically without blocking parallel renderers.
    // The `Cache.Count > 2048 → Clear()` thundering-herd eviction is removed:
    // the cache is already bounded upstream by ChatTranscriptCache._rows (only
    // lines currently in the transcript are reachable from ToSpans, so the cache
    // can hold at most that many entries; an explicit cap here would only kick
    // in for pathological cycling of the same N>2048 distinct strings).
    private static readonly Regex HeadingRegex = new(
        @"^\s{0,3}(#{1,3})\s+(.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly ConcurrentDictionary<string, List<TextSpan>> Cache = new(Environment.ProcessorCount, 512);
    /// <summary>Default ON so agent #/**/` output is readable.</summary>
    public static bool Enabled { get; set; } = true;

    public static IEnumerable<TextSpan> ToSpans(string text)
        => ToSpans(text, Color.White);

    public static IEnumerable<TextSpan> ToSpans(string text, Color baseColor)
    {
        if (!Enabled)
        {
            yield return new TextSpan(text, new Style(baseColor));
            yield break;
        }

        foreach (var span in GetCached(text, baseColor))
            yield return span;
    }

    private static List<TextSpan> GetCached(string text, Color baseColor)
    {
        // Color is not part of key: styles re-base on role color for plain runs
        // via Render; headings/code use fixed colors. Good enough for chat.
        // ConcurrentDictionary.GetOrAdd may run the factory multiple times in
        // parallel (only one writer wins) — that's fine, Render is pure and the
        // lost throwaway is just GC'd.
        return Cache.GetOrAdd(text, static (t, color) => Render(t, color).ToList(), baseColor);
    }

    public static void ClearCache() => Cache.Clear();

    private static IEnumerable<TextSpan> Render(string text, Color baseColor)
    {
        // Guard against runaway/garbage lines so Inline() stays bounded.
        if (text.Length > MaxInlineLength)
            text = text[..MaxInlineLength];

        var heading = HeadingRegex.Match(text);
        if (heading.Success)
        {
            int level = heading.Groups[1].Value.Length;
            var style = level == 1
                ? new Style(Color.Yellow, null, Decoration.Bold)
                : new Style(Color.Yellow);
            yield return new TextSpan(heading.Groups[2].Value, style);
            yield break;
        }

        string trimmed = text.Trim();
        if (trimmed is "---" or "***" or "___")
        {
            yield return new TextSpan("────────────", new Style(Color.Grey));
            yield break;
        }

        if (text.StartsWith("- ", StringComparison.Ordinal) || text.StartsWith("* ", StringComparison.Ordinal))
        {
            yield return new TextSpan("• ", new Style(Color.Grey));
            foreach (var s in Inline(text.AsSpan(2).ToString(), baseColor))
                yield return s;
            yield break;
        }

        if (text.Length > 2 && char.IsDigit(text[0]))
        {
            int dot = text.IndexOf(". ", StringComparison.Ordinal);
            if (dot is > 0 and <= 3)
            {
                yield return new TextSpan(text[..(dot + 1)] + " ", new Style(Color.Grey));
                foreach (var s in Inline(text[(dot + 2)..], baseColor))
                    yield return s;
                yield break;
            }
        }

        foreach (var s in Inline(text, baseColor))
            yield return s;
    }

    private static IEnumerable<TextSpan> Inline(string text, Color baseColor)
    {
        var result = new List<(string Text, Style Style)>();
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '`' && i + 1 < text.Length && text[i + 1] != '`')
            {
                int end = text.IndexOf('`', i + 1);
                if (end > i)
                {
                    result.Add((text.Substring(i + 1, end - i - 1), new Style(Color.Orchid1)));
                    i = end + 1;
                    continue;
                }
            }

            if (text[i] == '*' && Peek(text, i, "**") || text[i] == '_' && Peek(text, i, "__"))
            {
                string token = text[i] == '*' ? "**" : "__";
                int end = text.IndexOf(token, i + 2, StringComparison.Ordinal);
                if (end > i)
                {
                    result.Add((text.Substring(i + 2, end - i - 2),
                        new Style(baseColor, null, Decoration.Bold)));
                    i = end + 2;
                    continue;
                }
            }

            if (text[i] is '*' or '_')
            {
                char c = text[i];
                int end = text.IndexOf(c, i + 1);
                if (end > i && (end == text.Length - 1 || text[end + 1] != c))
                {
                    result.Add((text.Substring(i + 1, end - i - 1),
                        new Style(baseColor, null, Decoration.Italic)));
                    i = end + 1;
                    continue;
                }
            }

            int next = IndexOfAny(text, i, '*', '_', '`');
            if (next <= i) // unmatched delimiter: emit one char, always advance
                next = i + 1;
            result.Add((text.Substring(i, next - i), new Style(baseColor)));
            i = next;
        }

        foreach ((string t, var s) in result)
            yield return new TextSpan(t, s);
    }

    private static int IndexOfAny(string text, int start, params char[] chars)
    {
        int best = text.Length;
        foreach (char c in chars)
        {
            int idx = text.IndexOf(c, start);
            if (idx >= 0 && idx < best) best = idx;
        }

        return best;
    }

    private static bool Peek(string text, int i, string token)
        => i + token.Length <= text.Length
           && string.CompareOrdinal(text, i, token, 0, token.Length) == 0;
}
