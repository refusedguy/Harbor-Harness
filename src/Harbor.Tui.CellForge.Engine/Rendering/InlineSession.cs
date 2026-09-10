namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// Inline (non-alternate-screen) session bookkeeping, xai-ratatui-inline
/// pattern simplified to «emit + clear-from-cursor-down»:
///
/// The terminal shows a scrollback history above and a <em>live region</em> at
/// the bottom — the composer plus any streaming tail. Finalized content is
/// committed by erasing the live region, printing the block (each line ends
/// with CR+LF so it scrolls into terminal scrollback), then letting the caller
/// redraw the live region and report its new height via
/// <see cref="SetLiveLines"/>.
/// </summary>
public sealed class InlineSession
{
    private readonly AnsiWriter _writer;
    private int _liveLines;

    public InlineSession(AnsiWriter writer) => _writer = writer;

    /// <summary>Screen rows currently occupied by the live region.</summary>
    public int LiveLines => _liveLines;

    /// <summary>Caller reports the rendered height of the live region.</summary>
    public void SetLiveLines(int lines)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lines);
        _liveLines = lines;
    }

    /// <summary>
    /// Wipes the live region: cursor returns to the first live row, everything
    /// from there down is erased. Safe when the region is empty.
    /// </summary>
    public void EraseLiveRegion()
    {
        if (_liveLines <= 0)
        {
            return;
        }

        if (_liveLines == 1)
        {
            _writer.CarriageReturn();
            _writer.EraseEntireLine();
        }
        else
        {
            _writer.MoveUpToColumnStart(_liveLines - 1);
            _writer.EraseFromCursorDown();
        }

        _liveLines = 0;
    }

    /// <summary>
    /// Prints finalized text above the live region. The caller must have
    /// erased the region first; each wrapped line is terminated with CR+LF so
    /// the block settles into scrollback. Returns the number of lines written
    /// (empty blocks write nothing).
    /// </summary>
    public int WriteFinalizedBlock(ReadOnlySpan<char> text, int width, CellStyle? style = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        if (text.IsEmpty)
        {
            return 0;
        }

        var lines = new List<string>(64);
        TextWrap.WrapDocument(text, width, lines);

        if (style.HasValue)
        {
            var resolved = style.GetValueOrDefault();
            _writer.SetStyle(in resolved);
        }

        foreach (var line in lines)
        {
            _writer.WriteText(line);
            _writer.WriteLineBreak();
        }

        if (style.HasValue)
        {
            _writer.ResetStyle();
        }

        return lines.Count;
    }
}
