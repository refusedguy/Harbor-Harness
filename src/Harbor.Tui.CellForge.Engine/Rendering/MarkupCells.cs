// TR8 (#1227, epic #1195): thin markup→CellStyle layer (no Markdig).
//
// ConsoleEx-side single parser straight to cells: no ANSI roundtrip, Unicode-aware
// (wide/CJK/combining/emoji via UnicodeWidth + ScreenBuffer wide-pair rules).
// Ours: this file. Full Markdown stays in the chat layer.
// here only the three neutral tags generic screens need: bold for emphasis,
// dim for de-emphasis, and link (with an optional equals-url target) for
// underlined runs whose target travels out-of-band on the run.
//
// Spectre-compatible subset: tag names case-insensitive, [[ escapes a literal [,
// unknown tags and a missing [/] degrade to literal text (never throw).
// BCL-only, AOT-compatible: no regex, no reflection, no Harbor imports.

using System.Buffers;
using System.Text;

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>One styled text run produced by <see cref="MarkupCells"/>.</summary>
/// <param name="Text">Literal text (tags stripped, escapes resolved).</param>
/// <param name="Style">Resolved style (base + bold/dim/underline deltas).</param>
/// <param name="LinkTarget">Link URL when inside a <c>[link]</c> scope, else null.</param>
public readonly record struct MarkupRun(string Text, CellStyle Style, string? LinkTarget);

/// <summary>Link span in cell coordinates, as painted by <see cref="MarkupCells.Paint"/>.</summary>
/// <param name="X">Start column (inclusive).</param>
/// <param name="Y">Row (zero-based).</param>
/// <param name="Length">Span width in cells (wide runes count 2, combining 0).</param>
/// <param name="Target">Link URL (empty when the tag carried none).</param>
public readonly record struct MarkupLinkRange(int X, int Y, int Length, string Target);

/// <summary>
/// Thin <c>[bold]</c>/<c>[dim]</c>/<c>[link]</c> parser onto <see cref="CellStyle"/>
/// (TR8). Parses straight to styled runs — never through an ANSI string — so the
/// SGR automaton (<see cref="AnsiWriter"/>) and the cell grid see the same style
/// vocabulary, and wide/combining runes measure exactly as
/// <see cref="ScreenBuffer"/> paints them.
/// </summary>
public static class MarkupCells
{
    /// <summary>Parses <paramref name="markup"/> into styled runs.</summary>
    /// <param name="markup">Spectre-subset markup (null coerces to empty).</param>
    /// <param name="baseStyle">Style outside any tag.</param>
    public static List<MarkupRun> Parse(string? markup, CellStyle baseStyle = default)
    {
        var runs = new List<MarkupRun>();
        if (string.IsNullOrEmpty(markup))
        {
            return runs;
        }

        var text = new StringBuilder(markup!.Length);
        int boldDepth = 0;
        int dimDepth = 0;
        var links = new Stack<string>();
        string? activeLink = null;

        void Flush()
        {
            if (text.Length == 0)
            {
                return;
            }

            runs.Add(new MarkupRun(text.ToString(), Resolve(baseStyle, boldDepth, dimDepth, activeLink is not null), activeLink));
            text.Clear();
        }

        int i = 0;
        while (i < markup.Length)
        {
            char c = markup[i];
            if (c != '[')
            {
                text.Append(c);
                i++;
                continue;
            }

            // [[ → literal [ (Spectre escape).
            if (i + 1 < markup.Length && markup[i + 1] == '[')
            {
                text.Append('[');
                i += 2;
                continue;
            }

            int close = markup.IndexOf(']', i + 1);
            if (close < 0)
            {
                text.Append(c);
                i++;
                continue;
            }

            string tag = markup.Substring(i + 1, close - i - 1);
            if (TryApplyTag(tag, ref boldDepth, ref dimDepth, links, ref activeLink, Flush))
            {
                i = close + 1;
                continue;
            }

            // Unknown tag → literal text (keeps partial authoring visible).
            text.Append(c);
            i++;
        }

        Flush();
        return runs;
    }

    /// <summary>Cell width of the parsed markup (tags excluded, Unicode-aware).</summary>
    public static int MeasureWidth(string? markup)
    {
        int total = 0;
        foreach (var run in Parse(markup))
        {
            total += UnicodeWidth.Width(run.Text.AsSpan());
        }

        return total;
    }

    /// <summary>Plain text of the markup (tags stripped, escapes resolved).</summary>
    public static string Strip(string? markup)
    {
        if (string.IsNullOrEmpty(markup))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(markup!.Length);
        foreach (var run in Parse(markup))
        {
            sb.Append(run.Text);
        }

        return sb.ToString();
    }

    /// <summary>Escapes user text so it survives <see cref="Parse"/> literally.</summary>
    public static string Escape(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : text!.Replace("[", "[[", StringComparison.Ordinal);

    /// <summary>
    /// Paints <paramref name="markup"/> at (<paramref name="x"/>, <paramref name="y"/>),
    /// stopping at the row end. Returns the painted cell width; link spans (when
    /// <paramref name="links"/> is supplied) are reported in cell coordinates.
    /// Zero-width runes attach nowhere (per-rune <see cref="UnicodeWidth"/> rule);
    /// wide runes that do not fit are skipped with the rest of the row (ratatui policy).
    /// </summary>
    public static int Paint(
        ScreenBuffer buffer,
        int x,
        int y,
        string? markup,
        CellStyle baseStyle = default,
        List<MarkupLinkRange>? links = null)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (string.IsNullOrEmpty(markup))
        {
            return 0;
        }

        int cursor = x;
        foreach (var run in Parse(markup, baseStyle))
        {
            int start = cursor;
            var rest = run.Text.AsSpan();
            while (!rest.IsEmpty)
            {
                if (Rune.DecodeFromUtf16(rest, out var rune, out int consumed) != OperationStatus.Done)
                {
                    rune = Rune.ReplacementChar;
                    consumed = 1;
                }

                int w = UnicodeWidth.Width(rune);
                if (w == 0)
                {
                    rest = rest[consumed..];
                    continue;
                }

                if (!buffer.SetRune(cursor, y, rune, run.Style))
                {
                    return cursor - x;
                }

                cursor += w;
                rest = rest[consumed..];
            }

            if (run.LinkTarget is not null && links is not null && cursor > start)
            {
                links.Add(new MarkupLinkRange(start, y, cursor - start, run.LinkTarget));
            }
        }

        return cursor - x;
    }

    private static CellStyle Resolve(CellStyle baseStyle, int boldDepth, int dimDepth, bool linked)
    {
        var attrs = baseStyle.Attrs;
        if (boldDepth > 0)
        {
            attrs |= StyleAttr.Bold;
        }

        if (dimDepth > 0)
        {
            attrs |= StyleAttr.Dim;
        }

        if (linked)
        {
            attrs |= StyleAttr.Underline;
        }

        return attrs == baseStyle.Attrs ? baseStyle : new CellStyle(baseStyle.Fg, baseStyle.Bg, attrs);
    }

    private static bool TryApplyTag(
        string tag,
        ref int boldDepth,
        ref int dimDepth,
        Stack<string> links,
        ref string? activeLink,
        Action flush)
    {
        string name = tag.Trim();
        if (name.Length == 0)
        {
            return false;
        }

        // [/] closes the innermost open scope: link first, then dim, then bold.
        if (name == "/")
        {
            if (links.Count > 0)
            {
                flush();
                links.Pop();
                activeLink = links.Count > 0 ? links.Peek() : null;
                return true;
            }

            if (dimDepth > 0)
            {
                flush();
                dimDepth--;
                return true;
            }

            if (boldDepth > 0)
            {
                flush();
                boldDepth--;
                return true;
            }

            return false;
        }

        if (name.Equals("bold", StringComparison.OrdinalIgnoreCase))
        {
            flush();
            boldDepth++;
            return true;
        }

        if (name.Equals("dim", StringComparison.OrdinalIgnoreCase))
        {
            flush();
            dimDepth++;
            return true;
        }

        if (name.Equals("link", StringComparison.OrdinalIgnoreCase))
        {
            flush();
            links.Push(string.Empty);
            activeLink = string.Empty;
            return true;
        }

        const string linkPrefix = "link=";
        if (name.StartsWith(linkPrefix, StringComparison.OrdinalIgnoreCase))
        {
            flush();
            string target = name[linkPrefix.Length..].Trim().Trim('\'', '"');
            links.Push(target);
            activeLink = target;
            return true;
        }

        return false;
    }
}
