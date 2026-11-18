namespace Harbor.Terminal.Abstractions.Rendering;
/// <summary>
///     Block-level GFM pipe-table detection + parsing. No layout, no color,
///     no Spectre — just lines in, <see cref="GfmTable" /> out. Shared by every
///     renderer (Spectre / Terminal.Gui / plain) so the parse logic is not
///     duplicated per framework.
/// </summary>
public static class GfmTableParser
{
    /// <summary>
    ///     True if <paramref name="lines" />[<paramref name="index" />] is a pipe row
    ///     immediately followed by a <c>|---|</c> separator row.
    /// </summary>
    public static bool IsTableStart(IReadOnlyList<string> lines, int index)
    {
        if (index < 0 || index + 1 >= lines.Count)
            return false;
        return IsRow(lines[index]) && IsSeparator(lines[index + 1]);
    }

    /// <summary>
    ///     Parse the table block beginning at <paramref name="index" />. Returns the
    ///     table and the index of the first line after the block (header + separator
    ///     + every following pipe row).
    /// </summary>
    public static bool TryParse(
        IReadOnlyList<string> lines, int index, out GfmTable table, out int nextIndex)
    {
        table = null!;
        nextIndex = index;
        if (!IsTableStart(lines, index))
            return false;

        var headers = SplitRow(lines[index]);
        var alignments = ParseAlignments(lines[index + 1].AsSpan());
        var rows = new List<IReadOnlyList<string>>();

        int i = index + 2;
        while (i < lines.Count && IsRow(lines[i]))
        {
            rows.Add(SplitRow(lines[i]));
            i++;
        }

        // Align column count with header (short rows padded, extra cols ignored).
        int cols = headers.Count;
        if (alignments.Count != cols)
            alignments = NormalizeAlignments(alignments, cols);
        for (int r = 0; r < rows.Count; r++)
            rows[r] = NormalizeRow(rows[r], cols);

        table = new GfmTable(headers, rows, alignments);
        nextIndex = i;
        return true;
    }

    private static bool IsRow(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;
        line = line!.Trim();
        return line.Length > 1 && line[0] == '|' && line[^1] == '|';
    }

    private static bool IsSeparator(string? line)
    {
        if (!IsRow(line))
            return false;
        var cells = new RowCells(RowInterior(line!.AsSpan()));
        while (cells.MoveNext(out ReadOnlySpan<char> cell))
        {
            // RowCells already yields trimmed cells, so the extra Trim in the
            // original SplitRow + IsSeparator pair was a no-op.
            if (cell.Length == 0)
                return false;
            int s = cell[0] == ':' ? 1 : 0;
            int e = cell.Length - 1;
            if (cell[e] == ':')
                e--;
            if (s > e)
                return false;
            for (int k = s; k <= e; k++)
            {
                if (cell[k] != '-')
                    return false;
            }
        }
        return true;
    }

    /// <summary>
    ///     The trimmed body of a pipe row, with the outer pipes removed. Callers
    ///     only pass lines that already passed <see cref="IsRow" />.
    /// </summary>
    private static ReadOnlySpan<char> RowInterior(ReadOnlySpan<char> line)
    {
        ReadOnlySpan<char> trimmed = line.Trim();
        return trimmed[1..^1];
    }

    /// <summary>
    ///     Splits a row into trimmed cells without materialising the intermediate
    ///     strings that <c>string.Split('|')</c> used to allocate. Only the cells the
    ///     <see cref="GfmTable" /> model retains are turned into strings.
    /// </summary>
    private static IReadOnlyList<string> SplitRow(string line)
    {
        ReadOnlySpan<char> inner = RowInterior(line.AsSpan());
        int count = inner.Count('|') + 1; // string.Split('|') keeps empty cells
        var parts = new string[count];
        var cells = new RowCells(inner);
        int i = 0;
        while (cells.MoveNext(out ReadOnlySpan<char> cell))
            parts[i++] = cell.ToString();
        return parts;
    }

    private static IReadOnlyList<GfmAlign> ParseAlignments(ReadOnlySpan<char> separatorLine)
    {
        ReadOnlySpan<char> inner = RowInterior(separatorLine);
        var result = new GfmAlign[inner.Count('|') + 1];
        var cells = new RowCells(inner);
        int i = 0;
        while (cells.MoveNext(out ReadOnlySpan<char> cell))
        {
            bool left = cell.Length > 0 && cell[0] == ':';
            bool right = cell.Length > 1 && cell[^1] == ':';
            result[i++] = (left, right) switch
            {
                (true, true) => GfmAlign.Center,
                (false, true) => GfmAlign.Right,
                (true, false) => GfmAlign.Left,
                _ => GfmAlign.Left
            };
        }
        return result;
    }

    /// <summary>
    ///     Enumerates the trimmed cells of a pipe row's interior. Uses
    ///     <c>IndexOf('|')</c> to find delimiters, so the row is never copied and
    ///     non-retained cells (separator rows) cost nothing.
    /// </summary>
    private ref struct RowCells
    {
        private ReadOnlySpan<char> _remaining;
        private bool _done;

        public RowCells(ReadOnlySpan<char> interior)
        {
            _remaining = interior;
            _done = false;
        }

        public bool MoveNext(out ReadOnlySpan<char> cell)
        {
            if (_done)
            {
                cell = default;
                return false;
            }

            int pipe = _remaining.IndexOf('|');
            if (pipe < 0)
            {
                cell = _remaining.Trim();
                _remaining = default;
                _done = true;
                return true;
            }

            cell = _remaining[..pipe].Trim();
            _remaining = _remaining[(pipe + 1)..];
            return true;
        }
    }

    private static IReadOnlyList<GfmAlign> NormalizeAlignments(
        IReadOnlyList<GfmAlign> src, int cols)
    {
        var outp = new GfmAlign[cols];
        for (int c = 0; c < cols; c++)
            outp[c] = c < src.Count ? src[c] : GfmAlign.Left;
        return outp;
    }

    private static IReadOnlyList<string> NormalizeRow(IReadOnlyList<string> row, int cols)
    {
        if (row.Count == cols)
            return row;
        string[] outp = new string[cols];
        for (int c = 0; c < cols; c++)
            outp[c] = c < row.Count ? row[c] : string.Empty;
        return outp;
    }
}
