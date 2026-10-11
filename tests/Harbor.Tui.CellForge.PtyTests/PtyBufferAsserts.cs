// R2 steal (epic #1155): PTY-side half of the per-line assert idiom.
//
// Same shape as TestBackend.AssertBufferLinesAsync / AssertScrollbackLinesAsync
// in Harbor.Tui.CellForge.Tests, but over emulator text: AnsiTerminalBuffer
// rows are already trailing-trimmed strings (see CellForgePtyScenarioBase
// normalization), so there is no width-padding step — expected lines are
// compared verbatim. Failures render both sides as quoted lines (ratatui
// buffer_view), so trailing blanks stay visible.

namespace Harbor.Tui.CellForge.PtyTests;

/// <summary>
/// Per-line asserts for PTY grids and scrollback — the single idiom shared
/// with <c>TestBackend</c>: <c>AssertBufferLinesAsync</c> for the visible
/// viewport, <c>AssertScrollbackLinesAsync</c> for rows scrolled off the top.
/// </summary>
internal static class PtyBufferAsserts
{
    /// <summary>Asserts the visible grid equals one string per row, top first.</summary>
    public static async Task AssertBufferLinesAsync(string[] actual, params string[] expected)
        => await AssertBufferLinesAsync(actual, (IEnumerable<string>)expected).ConfigureAwait(false);

    /// <inheritdoc cref="AssertBufferLinesAsync(string[], string[])"/>
    public static async Task AssertBufferLinesAsync(string[] actual, IEnumerable<string> expected)
    {
        var list = expected.ToList();
        await Assert.That(actual.Length).IsEqualTo(list.Count).ConfigureAwait(false);
        await Assert.That(BufferView(actual)).IsEqualTo(BufferView(list)).ConfigureAwait(false);
    }

    /// <summary>
    /// Asserts the scrollback equals one string per row, oldest first. This is
    /// the assert virtualized-лента coverage was missing: without the
    /// scrollback buffer, scrolled-off rows exist nowhere to assert on.
    /// </summary>
    public static async Task AssertScrollbackLinesAsync(string[] actual, params string[] expected)
        => await AssertScrollbackLinesAsync(actual, (IEnumerable<string>)expected).ConfigureAwait(false);

    /// <inheritdoc cref="AssertScrollbackLinesAsync(string[], string[])"/>
    public static async Task AssertScrollbackLinesAsync(string[] actual, IEnumerable<string> expected)
    {
        var list = expected.ToList();
        await Assert.That(actual.Length).IsEqualTo(list.Count).ConfigureAwait(false);
        await Assert.That(BufferView(actual)).IsEqualTo(BufferView(list)).ConfigureAwait(false);
    }

    /// <summary>Quoted per-row view for failure messages (ratatui buffer_view).</summary>
    public static string BufferView(IEnumerable<string> lines)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var line in lines)
        {
            sb.Append('"').Append(line).AppendLine("\"");
        }

        return sb.ToString();
    }
}
