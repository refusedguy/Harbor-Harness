using Harbor.Tui.Abstractions.Rendering;
using Spectre.Console;
using Spectre.Tui;
namespace Harbor.Tui.SpectreTui.View;
/// <summary>
///     Spectre adapter: shared <see cref="GfmTableParser" /> / <see cref="GfmTableFormatter" />
///     produce the plain Unicode grid; this maps each grid line to a <see cref="TextLine" />
///     with role coloring. Block-level: a table is detected across multiple body lines and
///     yields N display rows in the same list (no extra widget, no row-model break). Only
///     the committed transcript path uses this.
/// </summary>
internal static class ChatTableRenderer
{
    public static bool IsTableStart(string[] lines, int index)
        => GfmTableParser.IsTableStart(lines, index);

    /// <summary>Render the table block beginning at <paramref name="index" />.</summary>
    public static (List<TextLine> Rows, int NextIndex) Render(
        string[] lines, int index, Color baseColor, int maxWidth)
    {
        var rows = new List<TextLine>();
        if (!GfmTableParser.TryParse(lines, index, out var table, out int next))
            return (rows, index + 1);

        var grid = GfmTableFormatter.Format(table, maxWidth);
        var grey = new Style(Color.Grey);
        var body = new Style(baseColor);
        foreach (var line in grid)
            rows.Add(Plain(line, line[0] == '│' ? body : grey));

        return (rows, next);
    }

    private static TextLine Plain(string text, Style style)
    {
        var line = new TextLine();
        line.Spans.Add(new TextSpan(text, style));
        return line;
    }
}
