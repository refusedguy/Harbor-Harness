using Harbor.Tui.ConsoleEx.Rendering;

namespace Harbor.Tui.ConsoleEx.Streaming;

/// <summary>
/// Owns the alt-screen frame pipeline (celldiff §0/§4): BACK buffer for
/// painters, FRONT mirrored through the <see cref="DiffEngine"/>, one atomic
/// flush per frame wrapped in synchronized output.
///
/// Resize policy (ratatui): horizontal shrink ⇒ Erase-in-display before the
/// frame to kill soft-wrap artifacts; every geometry change invalidates both
/// grids so the next frame is a clean full repaint from state.
/// </summary>
public sealed class ScreenSession
{
    private readonly ScreenBuffer _back;
    private readonly DiffEngine _engine;
    private readonly AnsiWriter _writer;
    private readonly Func<(int Cols, int Rows)>? _sizeSource;
    private bool _eraseBeforeNextFrame;

    public ScreenSession(AnsiWriter writer, int cols, int rows, Func<(int Cols, int Rows)>? sizeSource = null)
    {
        _writer = writer;
        _back = new ScreenBuffer(cols, rows);
        _engine = new DiffEngine(cols, rows);
        _sizeSource = sizeSource;
        CurrentCols = cols;
        CurrentRows = rows;
    }

    public ScreenBuffer Back => _back;

    public ScreenBuffer Front => _engine.Front;

    public DiffEngine Engine => _engine;

    public int CurrentCols { get; private set; }
    public int CurrentRows { get; private set; }

    /// <summary>Per-frame autoresize check (ratatui policy: the render tick is
    /// the single point of truth about terminal size).</summary>
    public void CheckAutoSize()
    {
        if (_sizeSource is null)
        {
            return;
        }

        var (cols, rows) = _sizeSource();
        ApplyResize(cols, rows);
    }

    /// <summary>Applies new geometry. Deduplicates; records Erase-in-display
    /// requirement for horizontal shrink.</summary>
    public void Resize(int cols, int rows)
    {
        ApplyResize(cols, rows);
    }

    private void ApplyResize(int cols, int rows)
    {
        if (cols == CurrentCols && rows == CurrentRows)
        {
            return;
        }

        bool horizontalShrink = cols < CurrentCols;
        CurrentCols = cols;
        CurrentRows = rows;
        _back.Resize(cols, rows);
        _engine.Front.Resize(cols, rows);
        _back.InvalidateAll();
        _engine.ClearHints();

        if (horizontalShrink)
        {
            _eraseBeforeNextFrame = true;
        }
    }

    /// <summary>Starts a frame: sync-on, optional erase-in-display first.</summary>
    public void BeginFrame()
    {
        _writer.BeginFrame();
        if (_eraseBeforeNextFrame)
        {
            _writer.EmitEraseInDisplay(2);
            _eraseBeforeNextFrame = false;
        }
    }

    /// <summary>Diffs BACK against FRONT and ships the frame in one write.</summary>
    public async ValueTask FlushFrameAsync(CancellationToken cancellationToken = default)
    {
        _engine.Flush(_back, _writer);
        await _writer.EndFrameAsync(cancellationToken).ConfigureAwait(false);
    }
}
