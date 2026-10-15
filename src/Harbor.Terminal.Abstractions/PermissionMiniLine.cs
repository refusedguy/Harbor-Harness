using System.Buffers;
using System.Text;

namespace Harbor.Terminal.Abstractions;

/// <summary>
///     One-line permission footer density ([steal/opencode] #1182, epic
///     #1155): the same approval machine as the fullscreen gate card,
///     collapsed into a single footer line plus a mono fallback.
///     Sources: <c>mini/footer.permission.tsx</c> (the <c>compact()</c>
///     footer — zero padding, short titles, inline controls) and
///     <c>mini/permission.shared.ts</c> (<c>permissionInfo(..., mono)</c> —
///     diff/patch inlined as text lines, icon swapped to an ASCII mark).
///     Pure BCL string combining — no tool table, no rendering, no I/O —
///     so every renderer (CellForge, AnsiPlain) and the CLI asker share one
///     shape instead of each inventing its own.
/// </summary>
public static class PermissionMiniLine
{
    /// <summary>Key hint closing the mini line (the footer density of <c>CompactHint</c>).</summary>
    public const string Hint = "[y/a/n]";

    /// <summary>Default line budget for pipes and unknown widths.</summary>
    public const int DefaultMaxWidth = 80;

    /// <summary>
    ///     Formats one footer line: <c>⚠ {title} — {snippet} [y/a/n]</c>
    ///     (mono: <c>! {title} - {snippet} [y/a/n]</c>). A blank
    ///     <paramref name="snippet"/> yields the title plus hint only; a
    ///     blank <paramref name="title"/> yields <c>?</c>, matching the gate
    ///     card's fallback for an unnamed tool.
    /// </summary>
    public static string Format(string? title, string? snippet, int maxWidth = DefaultMaxWidth, bool mono = false)
    {
        if (maxWidth <= 0)
        {
            maxWidth = DefaultMaxWidth;
        }

        string head = SingleLine(title);
        if (head.Length == 0)
        {
            head = "?";
        }

        string tail = SingleLine(snippet);
        string prefix = mono ? "! " : "⚠ ";
        string line = tail.Length == 0
            ? $"{prefix}{head} {Hint}"
            : mono ? $"{prefix}{head} - {tail} {Hint}" : $"{prefix}{head} — {tail} {Hint}";
        return Truncate(line, maxWidth, mono);
    }

    /// <summary>
    ///     Collapses arbitrary text onto one terminal-safe line: CR/LF/tab
    ///     become spaces, other control characters (incl. ESC, so
    ///     model-supplied args paint as inert text) are dropped. Mirrors the
    ///     fast-path scan of the AnsiPlain tool-start sanitizer.
    /// </summary>
    private static string SingleLine(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        int dirty = -1;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\n' || c == '\r' || c == '\t' || char.IsControl(c))
            {
                dirty = i;
                break;
            }
        }

        if (dirty < 0)
        {
            return text.Trim();
        }

        var sb = new StringBuilder(text.Length);
        sb.Append(text, 0, dirty);
        while (dirty < text.Length)
        {
            char c = text[dirty];
            if (c == '\n' || c == '\r' || c == '\t')
            {
                sb.Append(' ');
            }
            else if (!char.IsControl(c))
            {
                sb.Append(c);
            }

            dirty++;
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    /// Truncates to <paramref name="maxWidth"/> <em>cells</em>, not chars:
    /// CJK/emoji runes occupy two cells, so a char-count cut overflows a
    /// narrow footer. The cut walks <see cref="Rune"/>s, never splitting a
    /// surrogate pair, and a wide rune needs both cells free (same skip
    /// policy as <c>ScreenBuffer.SetRune</c>). Pinned by
    /// <c>PermissionMiniLineTests.Format_Cjk_*</c>. Combining/ZWJ seams stay
    /// open (counted 1, as before) — see #1017.
    /// </summary>
    private static string Truncate(string line, int maxWidth, bool mono)
    {
        string ellipsis = mono ? "..." : "…";
        int ellipsisCells = mono ? 3 : 1;
        if (CellWidth(line) <= maxWidth)
        {
            return line;
        }

        int budget = maxWidth - ellipsisCells;
        if (budget <= 0)
        {
            return ellipsis;
        }

        int cells = 0;
        int keep = 0;
        var span = line.AsSpan();
        while (keep < span.Length)
        {
            if (Rune.DecodeFromUtf16(span[keep..], out var rune, out int consumed) != OperationStatus.Done)
            {
                consumed = 1;
                rune = Rune.ReplacementChar;
            }

            int w = IsWide(rune) ? 2 : 1;
            if (cells + w > budget)
            {
                break;
            }

            cells += w;
            keep += consumed;
        }

        return line[..keep] + ellipsis;
    }

    private static int CellWidth(string line)
    {
        int cells = 0;
        var span = line.AsSpan();
        int i = 0;
        while (i < span.Length)
        {
            if (Rune.DecodeFromUtf16(span[i..], out var rune, out int consumed) != OperationStatus.Done)
            {
                consumed = 1;
                rune = Rune.ReplacementChar;
            }

            cells += IsWide(rune) ? 2 : 1;
            i += consumed;
        }

        return cells;
    }

    /// <summary>
    /// Same conservative wide ranges as the terminal table renderer
    /// (<c>GfmTableFormatter.IsWide</c>): CJK/Hangul/fullwidth are 2 cells,
    /// Cyrillic stays 1. Astral runes (emoji) are 2 cells.
    /// </summary>
    private static bool IsWide(Rune rune)
    {
        int v = rune.Value;
        if (v >= 0x10000)
        {
            return true;
        }

        return v is >= 0x1100 and <= 0x115F or // Hangul Jamo
            >= 0x2E80 and <= 0x303E or
            >= 0x3041 and <= 0x33FF or
            >= 0x3400 and <= 0x4DBF or
            >= 0x4E00 and <= 0x9FFF or
            >= 0xA000 and <= 0xA4CF or
            >= 0xAC00 and <= 0xD7A3 or
            >= 0xF900 and <= 0xFAFF or
            >= 0xFE30 and <= 0xFE4F or
            >= 0xFF00 and <= 0xFF60 or
            >= 0xFFE0 and <= 0xFFE6;
    }
}
