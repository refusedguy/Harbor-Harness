// R2 steal (epic #1155): TestBackend harness ported from ratatui-core
// src/backend/test.rs (with_lines, assert_buffer_lines, scrollback).
//
// RULE (units-vs-Buffer, ratatui's own guidance): widget unit tests render
// directly into a ScreenBuffer and assert on it — never through this backend.
// TestBackend is for INTEGRATION tests that drive a whole screen/panel and
// need seeded initial content (WithLines), per-line asserts
// (AssertBufferLines), a cursor, and a scrollback buffer. See
// TestBackendRuleTests for the executable form of this rule.
//
// What is deliberately NOT ported: the Backend-trait surface (draw/clear_region
// pixel ops — widgets here paint ScreenBuffers directly), scroll regions,
// cursor-visibility/query counting, and u16::MAX-exact truncation tests.
// Scrollback keeps captured lines as width-frozen strings: Resize never
// reflows them (ratatui rewidths the scrollback area — documented divergence).

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// In-memory screen backend for integration tests: a <see cref="ScreenBuffer"/>
/// viewport plus a scrollback buffer of lines scrolled off the top, with a
/// <c>WithLines</c> seeding constructor and per-line asserts as the single
/// idiom (same shape as <c>PtyBufferAsserts</c> on the PTY side).
/// </summary>
internal sealed class TestBackend
{
    /// <summary>
    /// Scrollback cap, mirroring ratatui's <c>u16::MAX</c> line limit: lines
    /// appended past it drop the oldest lines first.
    /// </summary>
    public const int MaxScrollbackLines = 65535;

    private sealed record ScrollbackLine(int Cols, string Art);

    private readonly List<ScrollbackLine> _scrollback = [];

    public TestBackend(int cols, int rows)
    {
        Buffer = new ScreenBuffer(cols, rows);
    }

    /// <summary>Visible viewport grid. Widgets under integration test paint here.</summary>
    public ScreenBuffer Buffer { get; }

    /// <summary>
    /// Lines scrolled off the top via <see cref="AppendLines"/>, oldest first.
    /// Each entry is one width-frozen art line (wide tails collapsed, trailing
    /// blanks kept) — compare via <see cref="AssertScrollbackLinesAsync"/>.
    /// </summary>
    public IReadOnlyList<string> Scrollback => _scrollback.Select(s => s.Art).ToList();

    public int CursorX { get; private set; }

    public int CursorY { get; private set; }

    /// <summary>
    /// Seeds a backend from text lines, like ratatui's
    /// <c>TestBackend::with_lines</c>: geometry is derived from the content
    /// (cols = widest display width, rows = line count).
    /// </summary>
    public static TestBackend WithLines(params string[] lines)
        => WithLines((IEnumerable<string>)lines);

    /// <inheritdoc cref="WithLines(string[])"/>
    public static TestBackend WithLines(IEnumerable<string> lines)
    {
        var list = lines.ToList();
        int cols = 0;
        foreach (var line in list)
        {
            cols = Math.Max(cols, UnicodeWidth.Width(line.AsSpan()));
        }

        var backend = new TestBackend(cols, list.Count);
        for (int y = 0; y < list.Count; y++)
        {
            backend.Buffer.SetText(0, y, list[y], CellStyle.Plain);
        }

        return backend;
    }

    /// <summary>Visible viewport as one art line per row (wide tails collapsed).</summary>
    public string[] Lines() => ArtLines(Buffer);

    /// <summary>Scrollback as stored (oldest first). Prefer the assert helpers.</summary>
    public string[] ScrollbackLines() => [.. Scrollback];

    public void SetCursor(int x, int y)
    {
        CursorX = Math.Clamp(x, 0, Math.Max(Buffer.Cols - 1, 0));
        CursorY = Math.Clamp(y, 0, Math.Max(Buffer.Rows - 1, 0));
    }

    /// <summary>
    /// Resizes the viewport. Scrollback is kept as captured (no reflow) —
    /// intentional divergence from ratatui, which rewidths its scrollback area.
    /// </summary>
    public void Resize(int cols, int rows) => Buffer.Resize(cols, rows);

    /// <summary>
    /// Blanks the viewport. Scrollback is preserved (ratatui <c>clear</c> parity).
    /// </summary>
    public void Clear() => Buffer.BlankAll();

    /// <summary>
    /// Appends <paramref name="lineCount"/> line breaks at the cursor, with
    /// ratatui <c>append_lines</c> semantics: the cursor moves down (and one
    /// cell right, clamped); rows pushed off the top land in
    /// <see cref="Scrollback"/>; the vacated bottom rows are blanked.
    /// </summary>
    public void AppendLines(int lineCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lineCount);
        if (Buffer.Cols == 0 || Buffer.Rows == 0)
        {
            throw new InvalidOperationException("AppendLines needs a non-empty viewport.");
        }

        int maxY = Buffer.Rows - 1;
        int cursorY = Math.Clamp(CursorY, 0, maxY);
        int linesAfterCursor = maxY - cursorY;

        if (lineCount > linesAfterCursor)
        {
            int scrollBy = lineCount - linesAfterCursor;
            string[] art = ArtLines(Buffer);
            for (int i = 0; i < scrollBy; i++)
            {
                string line = i < art.Length ? art[i] : new string(' ', Buffer.Cols);
                _scrollback.Add(new ScrollbackLine(Buffer.Cols, line));
            }

            DrainScrollbackCap();

            for (int y = 0; y + scrollBy < Buffer.Rows; y++)
            {
                for (int x = 0; x < Buffer.Cols; x++)
                {
                    Buffer.At(x, y) = Buffer.Get(x, y + scrollBy);
                }
            }

            Buffer.Fill(
                new Rect(0, Buffer.Rows - scrollBy, Buffer.Cols, scrollBy),
                Cell.Blank);
            Buffer.InvalidateAll();
        }

        CursorX = Math.Min(CursorX + 1, Buffer.Cols - 1);
        CursorY = Math.Min(cursorY + lineCount, maxY);
    }

    /// <summary>
    /// Asserts the viewport equals <paramref name="expected"/>, one string per
    /// row — the <c>assert_buffer_lines</c> idiom. Short strings are padded by
    /// rendering through a same-width buffer (ratatui <c>with_lines</c>
    /// semantics), so authors never hand-pad trailing blanks.
    /// </summary>
    public async Task AssertBufferLinesAsync(params string[] expected)
        => await AssertBufferLinesAsync((IEnumerable<string>)expected).ConfigureAwait(false);

    /// <inheritdoc cref="AssertBufferLinesAsync(string[])"/>
    public async Task AssertBufferLinesAsync(IEnumerable<string> expected)
    {
        var list = expected.ToList();
        await Assert.That(list.Count).IsEqualTo(Buffer.Rows);
        await Assert.That(BufferView(Buffer)).IsEqualTo(BufferView(RenderLines(list, Buffer.Cols, Buffer.Rows)));
    }

    /// <summary>
    /// Asserts the scrollback equals <paramref name="expected"/> (oldest
    /// first) — the <c>assert_scrollback_lines</c> idiom. Without it, asserts
    /// on virtualized ленты are impossible: scrolled-off rows exist nowhere else.
    /// </summary>
    public async Task AssertScrollbackLinesAsync(params string[] expected)
        => await AssertScrollbackLinesAsync((IEnumerable<string>)expected).ConfigureAwait(false);

    /// <inheritdoc cref="AssertScrollbackLinesAsync(string[])"/>
    public async Task AssertScrollbackLinesAsync(IEnumerable<string> expected)
    {
        var list = expected.ToList();
        await Assert.That(list.Count).IsEqualTo(_scrollback.Count);

        var rendered = new string[list.Count];
        for (int i = 0; i < list.Count; i++)
        {
            // Each expected line is rendered at the width its stored counterpart
            // was captured with (Resize never reflows — widths may differ).
            rendered[i] = ArtLines(RenderLines([list[i]], _scrollback[i].Cols, 1))[0];
        }

        await Assert.That(BufferViewLines(Scrollback)).IsEqualTo(BufferViewLines(rendered));
    }

    /// <summary>Asserts no line has scrolled off yet (<c>assert_scrollback_empty</c>).</summary>
    public async Task AssertScrollbackEmptyAsync()
    {
        await Assert.That(_scrollback.Count).IsEqualTo(0);
    }

    /// <summary>Asserts the cursor position (<c>assert_cursor_position</c>).</summary>
    public async Task AssertCursorPositionAsync(int x, int y)
    {
        await Assert.That(CursorX).IsEqualTo(x);
        await Assert.That(CursorY).IsEqualTo(y);
    }

    /// <summary>
    /// Quoted per-row view for failure messages (ratatui <c>buffer_view</c>):
    /// trailing blanks stay visible inside the quotes.
    /// </summary>
    public static string BufferView(ScreenBuffer buffer)
        => BufferViewLines(ArtLines(buffer));

    private static string BufferViewLines(IReadOnlyList<string> lines)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var line in lines)
        {
            sb.Append('"').Append(line).AppendLine("\"");
        }

        return sb.ToString();
    }

    private static string[] ArtLines(ScreenBuffer buffer)
    {
        // GridDump.Art emits exactly one '\n'-terminated line per row.
        string art = GridDump.Art(buffer);
        string[] split = art.Split('\n');
        var lines = new string[buffer.Rows];
        Array.Copy(split, lines, Math.Min(split.Length, lines.Length));
        for (int i = split.Length; i < lines.Length; i++)
        {
            lines[i] = string.Empty;
        }

        return lines;
    }

    private static ScreenBuffer RenderLines(IReadOnlyList<string> lines, int cols, int rows)
    {
        var buffer = new ScreenBuffer(cols, rows);
        for (int y = 0; y < Math.Min(lines.Count, rows); y++)
        {
            buffer.SetText(0, y, lines[y], CellStyle.Plain);
        }

        return buffer;
    }

    private void DrainScrollbackCap()
    {
        int overflow = _scrollback.Count - MaxScrollbackLines;
        if (overflow > 0)
        {
            _scrollback.RemoveRange(0, overflow);
        }
    }
}
