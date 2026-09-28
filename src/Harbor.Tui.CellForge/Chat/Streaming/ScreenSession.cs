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

    /// <summary>A frame is open between <see cref="BeginFrame"/> and its
    /// flush — gates <see cref="AbortFrame"/> so the scope stays idempotent
    /// after a shipped frame.</summary>
    private bool _frameInFlight;

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

    /// <summary>
    /// True when the writer's backend can ship a frame synchronously, i.e.
    /// when <see cref="FlushFrame"/> is usable. False means this session is
    /// async-only and must be flushed with <see cref="FlushFrameAsync"/> —
    /// an <see cref="ITerminalBackend"/> that is not an
    /// <see cref="ISyncTerminalBackend"/> cannot back the sync twin (issue
    /// #468).
    /// </summary>
    public bool SupportsSyncFlush => _writer.SupportsSyncWrites;

    /// <summary>Lock-free buffer handoff used by <see cref="OfferSwap"/> /
    /// <see cref="AdoptPendingSwap"/> (renderer-moat hot-swap runtime).</summary>
    public BufferSwapChain SwapChain => _swapChain;

    /// <summary>
    /// Post-render effect pipeline (renderer-moat T3). Fill its slots before
    /// the flush; <see cref="FlushFrame"/>/<see cref="FlushFrameAsync"/> arm it
    /// on the engine when non-empty — the diff then re-styles selected cells
    /// after selection and before SGR encoding, and FRONT keeps mirroring the
    /// terminal through the transform.
    /// </summary>
    public PostFxPipeline Effects { get; } = new();

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
        _engine.Front.InvalidateAll();
        _engine.ClearHints();

        if (horizontalShrink)
        {
            _eraseBeforeNextFrame = true;
        }
    }

    /// <summary>Starts a frame: swap adoption at the frame boundary, palette
    /// snapshot pin (theme swaps cannot tear a frame mid-paint), sync-on,
    /// optional erase-in-display first.</summary>
    public void BeginFrame() => BeginFrameCore();

    /// <summary>
    /// Exception-safe <see cref="BeginFrame"/>. The pin (and the writer frame
    /// it belongs to) is released when the returned scope leaves scope, on
    /// EVERY exit path — a throw between begin and flush used to leave the
    /// render thread pinned for the rest of the session (#458: the stale
    /// snapshot froze the theme until restart, and the half-written frame
    /// corrupted the next diff).
    /// </summary>
    public FrameScope BeginFrameScope()
    {
        BeginFrameCore();
        return new FrameScope(this);
    }

    /// <summary>
    /// Ends a frame that will never ship: unpins the palette and reopens the
    /// diff cleanly, so the next frame is a full repaint instead of a diff
    /// against a half-written buffer. Idempotent — a no-op once
    /// <see cref="FlushFrame"/> has closed the frame.
    /// </summary>
    public void AbortFrame()
    {
        if (_frameInFlight)
        {
            CloseFrame(shipped: false);
        }
    }

    private void BeginFrameCore()
    {
        AdoptPendingSwap();
        ChatPalette.PinFrame();
        _writer.BeginFrame();
        _frameInFlight = true;
        if (_eraseBeforeNextFrame)
        {
            _writer.EmitEraseInDisplay(2);
            _eraseBeforeNextFrame = false;
        }
    }

    /// <summary>
    /// Frame teardown — reached on EVERY exit path (#458: the unpin used to
    /// live at the tail of <c>FlushFrame*</c>, so a throw in between left the
    /// palette pinned on the render thread until process exit). A frame that
    /// never shipped invalidates both grids: the diff mirrors FRONT cell by
    /// cell as it drains, so a half-written frame would otherwise corrupt the
    /// next one.
    /// </summary>
    private void CloseFrame(bool shipped)
    {
        _frameInFlight = false;
        if (!shipped)
        {
            _back.InvalidateAll();
            _engine.Front.InvalidateAll();
            _engine.ClearHints();
        }

        ChatPalette.UnpinFrame();
    }

    /// <summary>Frame-scope handle from <see cref="BeginFrameScope"/>: ships the
    /// frame through <see cref="FlushAsync"/> (or <see cref="Flush"/>), and on
    /// <see cref="Dispose"/> aborts it when no flush ran — the exception path.
    /// Allocation-free struct: frames are the hot path.</summary>
    public readonly struct FrameScope : IDisposable
    {
        private readonly ScreenSession? _session;

        internal FrameScope(ScreenSession session) => _session = session;

        /// <summary>Ships the frame; the scope's later dispose is then a no-op.</summary>
        public void Flush() => _session?.FlushFrame();

        /// <summary>Ships the frame; the scope's later dispose is then a no-op.</summary>
        public ValueTask FlushAsync(CancellationToken cancellationToken = default) =>
            _session is null ? ValueTask.CompletedTask : _session.FlushFrameAsync(cancellationToken);

        public void Dispose() => _session?.AbortFrame();
    }

    /// <summary>Diffs BACK against FRONT and ships the frame in one write.</summary>
    public async ValueTask FlushFrameAsync(CancellationToken cancellationToken = default)
    {
        bool shipped = false;
        try
        {
            ArmEffects();
            _engine.Flush(_back, _writer);
            await _writer.EndFrameAsync(cancellationToken).ConfigureAwait(false);
            shipped = true;
        }
        finally
        {
            CloseFrame(shipped);
        }
    }

    /// <summary>
    /// Synchronous twin of <see cref="FlushFrameAsync"/> for sync render
    /// contexts and perf probes: same diff + empty-frame + sync-update
    /// semantics, no async machinery on the steady-state path.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The writer's backend is async-only (see <see cref="SupportsSyncFlush"/>).
    /// Thrown BEFORE the diff runs: mutating FRONT for a frame that then fails
    /// to ship would leave the engine diffing against a terminal state that
    /// never existed (issue #468).
    /// </exception>
    public void FlushFrame()
    {
        bool shipped = false;
        try
        {
            // Inside the try on purpose: the guard throws, so `finally` must
            // still run CloseFrame(false) to unpin the palette and invalidate
            // the grids. Checking before ArmEffects/_engine.Flush keeps FRONT
            // from advancing for a frame that can never ship (#468).
            if (!SupportsSyncFlush)
            {
                throw new InvalidOperationException(
                    $"{_writer.Backend.GetType().Name} is an async-only terminal backend; " +
                    "ScreenSession.FlushFrame needs an ISyncTerminalBackend. Use FlushFrameAsync instead.");
            }

            ArmEffects();
            _engine.Flush(_back, _writer);
            _writer.EndFrame();
            shipped = true;
        }
        finally
        {
            CloseFrame(shipped);
        }
    }

    /// <summary>Arms the effect pipeline only when it holds active effects —
    /// the empty pipeline keeps the engine on its exact classic path
    /// (zero perf regression on non-effect frames).</summary>
    private void ArmEffects() =>
        _engine.Effects = Effects.Count > 0 ? Effects : null;
}
