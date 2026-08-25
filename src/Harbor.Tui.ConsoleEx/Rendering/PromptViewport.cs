using System.Buffers;
using System.Text;

namespace Harbor.Tui.ConsoleEx.Rendering;

/// <summary>
/// Horizontal scroll window of a single logical line
/// (grok SingleLineViewport pattern): the smallest char offset such that the
/// caret stays inside a <c>width</c>-cell window with one trailing cell of
/// lookahead.
/// </summary>
public readonly struct PromptViewport
{
    /// <summary>Char offset where the visible slice starts.</summary>
    public int Start { get; }

    private PromptViewport(int start) => Start = start;

    /// <summary>Computes the window start for the given line and caret offset.</summary>
    public static PromptViewport ScrollIntoView(ReadOnlySpan<char> line, int caretInLine, int widthCells)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(widthCells);
        if (caretInLine < 0 || caretInLine > line.Length)
        {
            return default;
        }

        int totalCells = PromptBuffer.DisplayCells(line);
        if (totalCells <= widthCells)
        {
            return new PromptViewport(0);
        }

        int caretCell = PromptBuffer.DisplayCells(line[..caretInLine]);

        // Slide the window so the caret cell satisfies:
        //   startCell <= caretCell <= startCell + width - 1
        // (one cell reserved for the caret itself at the right edge).
        int startCell = Math.Max(0, Math.Min(totalCells - widthCells, caretCell - widthCells + 1));

        // Convert startCell back to a char offset on a rune boundary.
        var rest = line;
        int consumed = 0;
        int acc = 0;
        while (!rest.IsEmpty && acc < startCell)
        {
            if (Rune.DecodeFromUtf16(rest, out var rune, out int size) == OperationStatus.Done)
            {
                acc += UnicodeWidth.Width(rune);
                rest = rest[size..];
                consumed += size;
            }
            else
            {
                acc += 1;
                rest = rest[1..];
                consumed += 1;
            }
        }

        return new PromptViewport(consumed);
    }
}
