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

    private static string Truncate(string line, int maxWidth, bool mono)
    {
        if (line.Length <= maxWidth)
        {
            return line;
        }

        string ellipsis = mono ? "..." : "…";
        int keep = maxWidth - ellipsis.Length;
        if (keep < 0)
        {
            keep = 0;
        }

        while (keep > 0 && char.IsLowSurrogate(line[keep]))
        {
            keep--;
        }

        return line[..keep] + ellipsis;
    }
}
