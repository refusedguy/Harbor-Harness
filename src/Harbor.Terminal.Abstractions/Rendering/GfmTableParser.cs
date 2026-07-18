using System.Globalization;
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
        var alignments = ParseAlignments(lines[index + 1]);
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
        var cells = SplitRow(line!);
        foreach (var raw in cells)
        {
            var t = raw.Trim();
            if (t.Length == 0)
                return false;
            int s = t[0] == ':' ? 1 : 0;
            int e = t.Length - 1;
            if (t[e] == ':')
                e--;
            if (s > e)
                return false;
            for (int k = s; k <= e; k++)
                if (t[k] != '-')
                    return false;
        }
        return true;
    }

    private static IReadOnlyList<string> SplitRow(string line)
    {
        line = line.Trim();
        line = line[1..^1]; // drop outer pipes
        var parts = line.Split('|');
        for (int k = 0; k < parts.Length; k++)
            parts[k] = parts[k].Trim();
        return parts;
    }

    private static IReadOnlyList<GfmAlign> ParseAlignments(string separatorLine)
    {
        var cells = SplitRow(separatorLine);
        var result = new GfmAlign[cells.Count];
        for (int i = 0; i < cells.Count; i++)
        {
            var t = cells[i].Trim();
            bool left = t.Length > 0 && t[0] == ':';
            bool right = t.Length > 1 && t[^1] == ':';
            result[i] = (left, right) switch
            {
                (true, true) => GfmAlign.Center,
                (false, true) => GfmAlign.Right,
                (true, false) => GfmAlign.Left,
                _ => GfmAlign.Left
            };
        }
        return result;
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
        var outp = new string[cols];
        for (int c = 0; c < cols; c++)
            outp[c] = c < row.Count ? row[c] : string.Empty;
        return outp;
    }
}
