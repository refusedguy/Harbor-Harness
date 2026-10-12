using Harbor.Ui.Framework.State;

namespace Harbor.Tui.CellForge.Generic;

/// <summary>
/// Neutral host for a <see cref="GenericScreen"/> (epic #1195, part of #1226):
/// solves plus paints the screen and folds input through <see cref="UiStore"/>
/// with the generic TEA half only (<c>UiState.Ui</c> plus <see cref="AppMsg"/>).
/// Holds zero chat knowledge — no transcript, no costs, no agents, no chat
/// view-models — so arbitrary TUI surfaces compose on the CellForge solver
/// without thinking about chats. The chat renderer stays alongside untouched.
/// </summary>
public sealed class GenericTuiRenderer : IDisposable
{
    private readonly UiStore _store;
    private TerminalUiState _ui;
    private bool _disposed;

    /// <summary>Creates a renderer over <paramref name="store"/> plus <paramref name="screen"/>.</summary>
    /// <param name="store">Shared TEA store (a private one when null).</param>
    /// <param name="screen">Screen to solve plus paint (an empty one when null).</param>
    public GenericTuiRenderer(UiStore? store = null, GenericScreen? screen = null)
    {
        _store = store ?? new UiStore();
        Screen = screen ?? GenericScreen.Build([new GenericNodeSpec { Id = "generic.root", Title = "generic" }]);
        _ui = _store.State.Ui;
        _store.Changed += OnStoreChanged;
    }

    /// <summary>Screen this renderer solves plus paints.</summary>
    public GenericScreen Screen { get; set; }

    /// <summary>Latest generic snapshot (the <c>Ui</c> half only).</summary>
    public TerminalUiState UiSnapshot => _ui;

    /// <summary>Underlying store (the single source of truth).</summary>
    public UiStore Store => _store;

    /// <summary>True after a generic quit message folds.</summary>
    public bool ShouldQuit => _ui.ShouldQuit;

    /// <summary>Panel owning keyboard focus, or null when the base surface does.</summary>
    public string? FocusedPanelId => _ui.FocusedPanelId;

    private void OnStoreChanged(object? sender, UiStateChangedEventArgs e)
    {
        _ui = e.State.Ui;
    }

    /// <summary>
    /// Folds a generic message through the store and answers the host effect.
    /// Renderers call this and run the returned effect — they never mutate
    /// state directly.
    /// </summary>
    public TuiEffect Dispatch(AppMsg msg)
    {
        ArgumentNullException.ThrowIfNull(msg);
        return _store.Dispatch(msg);
    }

    /// <summary>Asks the app to quit (folds <see cref="AppMsg.Quit"/>).</summary>
    public TuiEffect Quit() => Dispatch(new AppMsg.Quit());

    /// <summary>Focuses a panel, or the base surface when <paramref name="id"/> is null.</summary>
    public TuiEffect FocusPanel(string? id) => Dispatch(new AppMsg.FocusPanel(id));

    /// <summary>Cycles keyboard focus to the next visible panel.</summary>
    public TuiEffect CycleFocus() => Dispatch(new AppMsg.CyclePanelFocus());

    /// <summary>Toggles a panel between hidden plus visible.</summary>
    public TuiEffect TogglePanel(string id) => Dispatch(new AppMsg.TogglePanel(id));

    /// <summary>Seeds the registered panel ids plus states plus sizes (host startup).</summary>
    public TuiEffect SeedPanels(
        System.Collections.Immutable.ImmutableArray<string> ids,
        System.Collections.Immutable.ImmutableDictionary<string, Harbor.Ui.Framework.Panels.TuiPanelState> states,
        System.Collections.Immutable.ImmutableDictionary<string, int> sizes) =>
        Dispatch(new AppMsg.SeedPanels(ids, states, sizes));

    /// <summary>
    /// Solves the screen at <paramref name="cols"/>×<paramref name="rows"/>
    /// and paints it into a caller-owned buffer. Allocation per call is one
    /// buffer — the steady-state host reuses the returned grid as its frame.
    /// </summary>
    public ScreenBuffer Render(int cols, int rows)
    {
        int w = Math.Max(1, cols);
        int h = Math.Max(1, rows);
        Screen.Solve(w, h);
        var buffer = new ScreenBuffer(w, h);
        Screen.PaintAll(buffer);
        SyncFocusFromStore();
        return buffer;
    }

    /// <summary>
    /// Paints the solved screen into <paramref name="buffer"/> (host-owned grid,
    /// e.g. a golden-frame test). Solves first so rects match the grid.
    /// </summary>
    public void Paint(ScreenBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        Screen.Solve(buffer.Cols, buffer.Rows);
        Screen.PaintAll(buffer);
        SyncFocusFromStore();
    }

    /// <summary>
    /// Routes a frame key: focus motion stays here, everything else reaches
    /// the focused field through <see cref="GenericScreen.HandleKey"/>.
    /// Judges no runes — the hosted fields own their typed-input gates.
    /// </summary>
    /// <returns>True when consumed.</returns>
    public bool HandleKey(in KeyEvent key)
    {
        if (key.EventType == KeyEventType.Release)
        {
            return false;
        }

        return Screen.HandleKey(in key);
    }

    /// <summary>
    /// Mirrors store focus into the screen so the painted marker follows the
    /// TEA snapshot (the screen owns the marker; the store owns the truth).
    /// </summary>
    private void SyncFocusFromStore()
    {
        string? id = _ui.FocusedPanelId;
        if (id is null)
        {
            return;
        }

        Screen.SetFocus(id);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _store.Changed -= OnStoreChanged;
    }
}
