using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Streaming;

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
    private ScreenBuffer _back;
    private readonly DiffEngine _engine;
    private readonly AnsiWriter _writer;
    private readonly BufferSwapChain _swapChain = new();
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

    /// <summary>Lock-free buffer handoff used by <see cref="OfferSwap"/> /
    /// <see cref="AdoptPendingSwap"/> (renderer-moat hot-swap runtime).</summary>
    public BufferSwapChain SwapChain => _swapChain;

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

    /// <summary>
    /// Marks a screen region damaged for the next flush — the diff then
    /// rescans only hinted regions (partial-scan mode) instead of the whole
    /// grid. Damage is CONSERVATIVE by contract: every region a frame might
    /// have touched must be hinted, or callers fall back to
    /// <see cref="DamageAll"/> / no hints (plain full scan).
    /// </summary>
    public void Damage(in Rect rect) => _engine.FrameHint(in rect);

    /// <summary>
    /// Forces the next flush to a full scan (the no-hints path) — used when
    /// damage is broad or untrackable: resize, theme swap, layout animation,
    /// appends that shift whole viewports.
    /// </summary>
    public void DamageAll() => _engine.ClearHints();

    // ── Hot-swap runtime (renderer-moat T2) ────────────────────────────────

    /// <summary>
    /// Publishes a replacement BACK/FRONT pair from ANY thread — the lock-free
    /// handoff behind ConsoleEx ↔ Avalonia ↔ Blazor buffer swaps. The render
    /// loop keeps running untouched: the offer is adopted atomically at the
    /// next <see cref="BeginFrame"/> (single reference swap, both grids
    /// invalidated → one clean full repaint, never a torn frame). Last
    /// writer wins; a displaced offer is dropped.
    /// </summary>
    public void OfferSwap(ScreenBuffer back, ScreenBuffer front)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(front.Cols, back.Cols);
        ArgumentOutOfRangeException.ThrowIfNotEqual(front.Rows, back.Rows);
        _swapChain.Publish(new BufferPair(back, front));
    }

    /// <summary>
    /// Adopts a pending swap offer, if any. Returns true when the active
    /// buffer pair was replaced: geometry follows the offered pair, both
    /// grids start invalidated (full repaint), a horizontal shrink arms the
    /// erase-in-display policy, and the retired buffers return to the
    /// <see cref="SwapChain"/> pool. Called automatically by
    /// <see cref="BeginFrame"/> — the frame boundary is the adoption point.
    /// </summary>
    public bool AdoptPendingSwap()
    {
        var offer = _swapChain.TryTake();
        if (offer is null)
        {
            return false;
        }

        bool horizontalShrink = offer.Back.Cols < CurrentCols;
        var retiredBack = _back;
        var retiredFront = _engine.Front;

        _back = offer.Back;
        _engine.SwapFront(offer.Front);
        CurrentCols = _back.Cols;
        CurrentRows = _back.Rows;

        _back.InvalidateAll();
        _engine.Front.InvalidateAll();
        _engine.ClearHints();

        if (horizontalShrink)
        {
            _eraseBeforeNextFrame = true;
        }

        _swapChain.Return(retiredBack);
        _swapChain.Return(retiredFront);
        return true;
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

    /// <summary>Starts a frame: swap adoption at the frame boundary, palette
    /// snapshot pin (theme swaps cannot tear a frame mid-paint), sync-on,
    /// optional erase-in-display first.</summary>
    public void BeginFrame()
    {
        AdoptPendingSwap();
        ChatPalette.PinFrame();
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
        ChatPalette.UnpinFrame();
    }

    /// <summary>
    /// Synchronous twin of <see cref="FlushFrameAsync"/> for sync render
    /// contexts and perf probes: same diff + empty-frame + sync-update
    /// semantics, no async machinery on the steady-state path.
    /// </summary>
    public void FlushFrame()
    {
        _engine.Flush(_back, _writer);
        _writer.EndFrame();
        ChatPalette.UnpinFrame();
    }
}
