namespace Harbor.Terminal.Abstractions.Rendering;
/// <summary>
///     Alignment of a GFM table column, derived from the separator row
///     (<c>:---</c>, <c>:--:</c>, <c>---:</c>).
/// </summary>
public enum GfmAlign : byte
{
    Left,
    Center,
    Right
}

/// <summary>
///     Parsed GFM pipe-table. Framework-free: holds only cell strings and
///     alignment. No <c>TextLine</c> / color / Spectre types.
/// </summary>
/// <param name="Headers">Header cell text, trimmed.</param>
/// <param name="Rows">Body rows; each is a list of trimmed cell strings.</param>
/// <param name="Alignments">Per-column alignment, length = column count.</param>
public sealed record GfmTable(
    IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    IReadOnlyList<GfmAlign> Alignments);
