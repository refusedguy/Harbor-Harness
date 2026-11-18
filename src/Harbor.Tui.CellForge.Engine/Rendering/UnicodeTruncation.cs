// #2645: single owner for display-cell string truncation. StringTruncation
// (Harbor.Abstractions.Extensions) is char-based and explicitly disclaims this
// axis; the two CellForge widget-local Rune walks disagreed with it and with
// each other. This helper is engine-owned (BCL-only, AOT-clean) and measures
// through UnicodeWidth, the same table the grid uses to reserve cells.

using System.Buffers;
using System.Text;

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
///     Truncate text to a terminal-cell budget without splitting wide glyphs.
///     <para>
///         Unlike <c>StringTruncation.Truncate</c> (UTF-16 char count), this walks
///         <see cref="Rune"/>s and bills each one through <see cref="UnicodeWidth.Width(Rune)"/>,
///         so an emoji takes two cells and a combining mark travels with its base
///         at zero cost. The <paramref name="suffix"/> is reserved <b>within</b>
///         <paramref name="cells"/>: pass <see cref="string.Empty"/> for a hard
///         clip (tab strip, overlay headers) or <see cref="Ellipsis"/> for a cut
///         marker (column grid, diff rows).
///     </para>
/// </summary>
public static class UnicodeTruncation
{
    /// <summary>Single-cell cut marker. Fits any budget down to 1.</summary>
    public const string Ellipsis = "…";

    /// <summary>
    ///     Fit <paramref name="text"/> into <paramref name="cells"/> display columns.
    /// </summary>
    /// <param name="text">Source text. <c>null</c> or empty yields <see cref="string.Empty"/>.</param>
    /// <param name="cells">Cell budget, suffix included. Non-positive yields <see cref="string.Empty"/>.</param>
    /// <param name="suffix">
    ///     Marker appended on a cut, billed within the budget. <see cref="string.Empty"/>
    ///     is a hard clip; <c>null</c> is treated as no marker too (use
    ///     <see cref="Ellipsis"/> explicitly for an ellipsis cut).
    /// </param>
    /// <returns>
    ///     <paramref name="text"/> untouched when it already fits; otherwise the
    ///     leading runes that fit in <c>cells - width(suffix)</c> followed by
    ///     <paramref name="suffix"/>. Wide glyphs are dropped whole, never split
    ///     across cells, and the result never exceeds <paramref name="cells"/>.
    /// </returns>
    public static string Truncate(string? text, int cells, string suffix)
    {
        if (cells <= 0 || string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        suffix ??= string.Empty;
        int contentCells = cells - UnicodeWidth.Width(suffix.AsSpan());
        if (contentCells < 0)
        {
            contentCells = 0;
        }

        if (UnicodeWidth.Width(text.AsSpan()) <= cells)
        {
            return text;
        }

        int width = 0;
        var sb = new StringBuilder(cells);
        var rest = text.AsSpan();
        while (!rest.IsEmpty
               && Rune.DecodeFromUtf16(rest, out var rune, out int consumed) == OperationStatus.Done)
        {
            int w = UnicodeWidth.Width(rune);
            if (width + w > contentCells)
            {
                break;
            }

            sb.Append(rest[..consumed]);
            width += w;
            rest = rest[consumed..];
        }

        sb.Append(suffix);
        return sb.ToString();
    }
}
