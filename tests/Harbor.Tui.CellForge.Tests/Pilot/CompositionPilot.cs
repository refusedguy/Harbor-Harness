// #2522: chat-independent headless driver for arbitrary CellForge composition.
// Textual's Pilot has two layers: a fast logic layer (press / wait_for_idle /
// read frame) and a slow byte layer. Harbor already has the slow byte layer
// (PTY scenarios + CellForgePilot, which is bound to ChatKeyMap and three chat
// view-models). CompositionPilot is the missing fast layer for composition
// that does NOT own a chat runtime: it renders an INode tree straight into a
// ScreenBuffer via NodeFrame.Render and reads back the visible output.
//
// This is deliberately NOT ElementId (#2361/#2436) and NOT the in-flight
// WidgetIdRegistry product type (#2498, not on dev at the time of writing):
//   * ElementId is a per-frame identity PATH used by prepaint to reuse records.
//   * the product registry maps a stable name to an INode for production hosts.
//   * this pilot keeps a tiny test-local ordinal id -> INode map, only so a
//     test can say "give me the visible output of the widget named 'center'".
// Keeping the map here (internal, test assembly) avoids duplicating either
// product type and adds no public API surface.

using System.Text;

namespace Harbor.Tui.CellForge.Tests.Pilot;

/// <summary>
/// Headless driver for a chat-independent <see cref="EngineCells.INode"/>
/// composition. Renders the tree into an engine <see cref="EngineCells.ScreenBuffer"/>
/// and reads back the visible output of the whole frame or of one named widget.
/// </summary>
internal sealed class CompositionPilot
{
    private readonly Dictionary<string, EngineCells.INode> _byId = new(StringComparer.Ordinal);
    private readonly List<string> _idTrace = [];

    public CompositionPilot(Func<EngineCells.INode> root, int cols, int rows)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cols);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rows);

        Cols = cols;
        Rows = rows;
        Root = root();
        Buffer = new EngineCells.ScreenBuffer(cols, rows);
    }

    public int Cols { get; }
    public int Rows { get; }

    /// <summary>The composition root, built once at construction.</summary>
    public EngineCells.INode Root { get; }

    /// <summary>The grid the last <see cref="Render"/> painted into.</summary>
    public EngineCells.ScreenBuffer Buffer { get; }

    /// <summary>Shared prepaint context: cache hits across frames are observable.</summary>
    public EngineCells.PrepaintContext Context { get; } = new();

    /// <summary>
    /// ElementId paths observed by traced widgets during the last render. This
    /// is the frame-identity trace, kept distinct from the stable id lookup.
    /// </summary>
    public IReadOnlyList<string> IdTrace => _idTrace;

    /// <summary>Register a widget under a stable, ordinal id for lookup.</summary>
    public CompositionPilot Register(string id, EngineCells.INode node)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(node);
        if (!_byId.TryAdd(id, node))
        {
            throw new InvalidOperationException($"A widget with id '{id}' is already registered.");
        }

        return this;
    }

    /// <summary>Find a registered widget by its exact id (no selectors).</summary>
    public EngineCells.INode? FindById(string id) =>
        id is not null && _byId.TryGetValue(id, out var node) ? node : null;

    /// <summary>Record an ElementId path seen by a widget this frame.</summary>
    internal void Trace(EngineCells.ElementId id) => _idTrace.Add(id.ToString());

    /// <summary>Clear the per-frame id trace before a render.</summary>
    public CompositionPilot ResetTrace()
    {
        _idTrace.Clear();
        return this;
    }

    /// <summary>Run measure → arrange → prepaint → paint into <see cref="Buffer"/>.</summary>
    public CompositionPilot Render()
    {
        EngineCells.NodeFrame.Render(
            Root,
            Context,
            Buffer,
            new EngineCells.Constraints(Cols, Rows),
            new EngineCells.Rect(0, 0, Cols, Rows));
        return this;
    }

    /// <summary>Whole frame as normalized text, one string per row.</summary>
    public string[] FrameLines() => ReadRegion(new EngineCells.Rect(0, 0, Cols, Rows));

    /// <summary>Whole frame joined with '\n'.</summary>
    public string FrameText() => string.Join('\n', FrameLines());

    /// <summary>
    /// Visible output of the widget registered under <paramref name="id"/>,
    /// cropped to the rect the node was assigned at arrange time. Returns an
    /// empty array when the id is unknown or the node was never arranged.
    /// </summary>
    public string[] NodeLines(string id)
    {
        var node = FindById(id);
        if (node is null || node.Arranged.Width <= 0 || node.Arranged.Height <= 0)
        {
            return [];
        }

        return ReadRegion(node.Arranged);
    }

    /// <summary>Visible output of one widget joined with '\n'.</summary>
    public string NodeText(string id) => string.Join('\n', NodeLines(id));

    /// <summary>The rect assigned to a widget at arrange time.</summary>
    public EngineCells.Rect BoundsOf(string id) => FindById(id)?.Arranged ?? default;

    /// <summary>
    /// Reads a buffer region into normalized lines: wide-tail cells are
    /// skipped, each line is trimmed on the right, trailing empty lines drop.
    /// Same contract as CellForgePilot.FrameLines so assertions read the same.
    /// </summary>
    private string[] ReadRegion(EngineCells.Rect region)
    {
        int x0 = Math.Max(0, region.X);
        int y0 = Math.Max(0, region.Y);
        int x1 = Math.Min(Cols, region.X + region.Width);
        int y1 = Math.Min(Rows, region.Y + region.Height);
        if (x0 >= x1 || y0 >= y1)
        {
            return [];
        }

        var lines = new string[y1 - y0];
        for (int y = y0; y < y1; y++)
        {
            var sb = new StringBuilder(x1 - x0);
            for (int x = x0; x < x1; x++)
            {
                var cell = Buffer.Get(x, y);
                if (cell.Width == EngineCells.Cell.WSkip)
                {
                    continue;
                }

                sb.Append(Printable(cell.Rune));
            }

            lines[y - y0] = sb.ToString().TrimEnd();
        }

        int last = lines.Length - 1;
        while (last >= 0 && lines[last].Length == 0)
        {
            last--;
        }

        return lines[..(last + 1)];
    }

    /// <summary>One printable cell as text: control and surrogate runes read as blanks.</summary>
    private static string Printable(int rune)
    {
        if (rune is >= 0x20 and <= 0x7E or >= 0xA0 and <= 0xD7FF or >= 0xE000 and <= 0x10FFFF)
        {
            return char.ConvertFromUtf32(rune);
        }

        return " ";
    }
}
