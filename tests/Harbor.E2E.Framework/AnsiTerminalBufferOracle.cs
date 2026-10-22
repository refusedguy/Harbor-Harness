// AnsiTerminalBufferOracle.cs — the OLD terminal-grid searches, kept as the
// ORACLE the pooled ones are proved against.
//
// WHY THE OLD IMPLEMENTATION IS STILL HERE
// -----------------------------------------
// `AnsiTerminalBuffer` is the 2D terminal emulator every PTY scenario asserts
// on, and `TuiDriver.WaitForTextAsync` searches it on every poll. Both
// searches used to MATERIALISE what they searched:
//
//   * `ContainsText` built a `StringBuilder` per row and a string per row —
//     fifty of each, per call — and searched that;
//   * `GetVisibleText` built a `StringBuilder` per row and a string per row as
//     well, and `ContainsTextIgnoringWhitespace` then built the whole visible
//     text AGAIN with its whitespace removed.
//
// The new ones read the grid in place and write into a pooled buffer. They are
// only allowed to be cheaper: the grid is what a scenario's verdict is read
// off, so a search that answers differently makes a scenario pass that used to
// fail.
//
// So the old implementations stay, as the thing the new ones are compared
// against, and they are used in exactly one place: the parity test. The
// proof is in `AnsiTerminalBufferParityTests` (Harbor.E2E.Framework.Tests),
// which also MEASURES what the old ones allocated.
//
// THE SEAM
// --------
// `AnsiTerminalBuffer.GridForOracle` hands the grid over by reference, so the
// oracle reads exactly the bytes the new implementation reads. Without that
// seam the oracle would have to rebuild the grid from a string, and the two
// sides would be comparing different things.

using System.Text;

namespace Harbor.E2E.Framework;

/// <summary>
///     The terminal-grid searches as they were before the grid was read in
///     place, kept verbatim as the ground truth.
/// </summary>
internal static class AnsiTerminalBufferOracle
{
    /// <summary>
    ///     The OLD grid search: a string per row, then an ordinal search.
    /// </summary>
    /// <param name="buffer">The buffer to search.</param>
    /// <param name="pattern">The text to look for.</param>
    /// <returns><c>true</c> when the pattern occurs in some row.</returns>
    internal static bool ContainsText(AnsiTerminalBuffer buffer, string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
            return false;

        TerminalCell[,] grid = buffer.GridForOracle;

        for (int r = 0; r < buffer.Height; r++)
        {
            var rowSb = new StringBuilder(buffer.Width);
            for (int c = 0; c < buffer.Width; c++)
            {
                char ch = grid[r, c].Character;
                rowSb.Append(ch == '\0' ? ' ' : ch);
            }
            if (rowSb.ToString().Contains(pattern, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    ///     The OLD visible text: a builder and a string per row, then one
    ///     builder for the whole thing.
    /// </summary>
    /// <param name="buffer">The buffer to read.</param>
    /// <returns>The visible text.</returns>
    internal static string GetVisibleText(AnsiTerminalBuffer buffer)
    {
        TerminalCell[,] grid = buffer.GridForOracle;

        var sb = new StringBuilder(buffer.Height * (buffer.Width + 1));
        for (int r = 0; r < buffer.Height; r++)
        {
            var rowSb = new StringBuilder(buffer.Width);
            for (int c = 0; c < buffer.Width; c++)
            {
                char ch = grid[r, c].Character;
                rowSb.Append(ch == '\0' ? ' ' : ch);
            }
            sb.AppendLine(rowSb.ToString().TrimEnd());
        }
        return sb.ToString();
    }

    /// <summary>
    ///     The OLD tear-tolerant grid search: the visible text, built as a
    ///     string, then built AGAIN without its whitespace.
    /// </summary>
    /// <param name="buffer">The buffer to search.</param>
    /// <param name="needle">The text to look for, already free of whitespace.</param>
    /// <returns><c>true</c> when the needle occurs in the whitespace-stripped visible text.</returns>
    internal static bool ContainsTextIgnoringWhitespace(AnsiTerminalBuffer buffer, string needle) =>
        ScreenText.StripWhitespace(GetVisibleText(buffer)).Contains(needle, StringComparison.Ordinal);
}
