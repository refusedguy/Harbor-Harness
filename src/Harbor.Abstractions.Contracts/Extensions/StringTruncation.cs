namespace Harbor.Abstractions.Extensions;

/// <summary>
///     Canonical string-truncation policy (#1566).
///     Replaces ~13 hand-rolled <c>Truncate</c> copies scattered across tools,
///     overlays and runners so a UX-policy fix touches one body instead of a
///     dozen files.
/// </summary>
/// <remarks>
///     <para>
///         Two shapes, one rule each:
///         <list type="bullet">
///             <item><see cref="Truncate(string?,int)" /> fits the suffix <b>within</b> the budget
///             (result is never longer than <c>max</c>).</item>
///             <item><see cref="TruncateWithDropCount" /> keeps a full <c>max</c>-char head and
///             appends a <c>…[truncated N chars]</c> marker <b>beyond</b> it
///             (for bounded dumps where the head width is the contract).</item>
///         </list>
///     </para>
///     <para>
///         Deliberately NOT covered (separate axes, pinned by their own tests):
///         display-cell truncation over <c>UnicodeWidth</c> (status strip, tab strip,
///         rule titles, column blocks), rune-based collapse, middle-ellipsis and
///         mono-width truncation.
///     </para>
/// </remarks>
public static class StringTruncation
{
    /// <summary>Single-char ellipsis marking a cut. Fits any budget down to 1.</summary>
    public const string Ellipsis = "…";

    /// <summary>
    ///     Hard-truncate <paramref name="text" /> to at most <paramref name="max" /> chars,
    ///     keeping the <see cref="Ellipsis" /> marker within the budget.
    /// </summary>
    /// <param name="text">Source text. <c>null</c> or empty yields <see cref="string.Empty" />.</param>
    /// <param name="max">Maximum result length. Non-positive yields <see cref="string.Empty" />.</param>
    /// <returns>
    ///     <paramref name="text" /> untouched when it fits; otherwise the leading
    ///     <c>max - 1</c> chars plus <see cref="Ellipsis" /> (<c>"…"</c> alone when
    ///     <c>max == 1</c>).
    /// </returns>
    public static string Truncate(this string? text, int max) => Truncate(text, max, Ellipsis);

    /// <summary>
    ///     Hard-truncate <paramref name="text" /> to at most <paramref name="max" /> chars
    ///     with an explicit cut marker.
    /// </summary>
    /// <param name="text">Source text. <c>null</c> or empty yields <see cref="string.Empty" />.</param>
    /// <param name="max">Maximum result length. Non-positive yields <see cref="string.Empty" />.</param>
    /// <param name="suffix">
    ///     Marker appended on a cut, kept within the budget. <see cref="string.Empty" />
    ///     means a hard clip with no marker; a <c>null</c> marker falls back to
    ///     <see cref="Ellipsis" />.
    /// </param>
    /// <returns>
    ///     <paramref name="text" /> untouched when it fits; otherwise the leading chars
    ///     plus <paramref name="suffix" /> with total length at most <paramref name="max" />.
    /// </returns>
    public static string Truncate(this string? text, int max, string suffix)
    {
        suffix ??= Ellipsis;
        if (string.IsNullOrEmpty(text) || max <= 0)
        {
            return string.Empty;
        }

        if (text.Length <= max)
        {
            return text;
        }

        if (suffix.Length == 0)
        {
            return text[..max];
        }

        if (max <= suffix.Length)
        {
            return suffix[..max];
        }

        return text[..(max - suffix.Length)] + suffix;
    }

    /// <summary>
    ///     Bound a dump to a full <paramref name="max" />-char head plus a
    ///     <c>…[truncated N chars]</c> marker appended beyond it.
    /// </summary>
    /// <param name="text">Source text. <c>null</c> or empty yields <see cref="string.Empty" />.</param>
    /// <param name="max">Head length kept verbatim. Non-positive yields <see cref="string.Empty" />.</param>
    /// <param name="separator">Separator between the head and the marker (<c>"\n"</c> by default).</param>
    /// <param name="detail">
    ///     Optional extra appended inside the marker brackets
    ///     (e.g. <c>"showing first 1000"</c>), or <c>null</c> for the plain marker.
    /// </param>
    /// <returns>
    ///     <paramref name="text" /> untouched when it fits; otherwise
    ///     <c>head + separator + …[truncated {dropped} chars[; detail]]</c>.
    /// </returns>
    public static string TruncateWithDropCount(this string? text, int max, string separator = "\n", string? detail = null)
    {
        separator ??= "\n";
        if (string.IsNullOrEmpty(text) || max <= 0)
        {
            return string.Empty;
        }

        if (text.Length <= max)
        {
            return text;
        }

        int dropped = text.Length - max;
        return detail is null
            ? text[..max] + separator + $"…[truncated {dropped} chars]"
            : text[..max] + separator + $"…[truncated {dropped} chars; {detail}]";
    }
}
