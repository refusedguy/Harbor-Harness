using System.Collections.Immutable;
using Harbor.Ui.Framework.Panels;
namespace Harbor.Ui.Framework.State;

/// <summary>
///     The generic message type for an interactive terminal UI (TEA/MVU "Msg").
///     Every framework-owned input — key presses, view-measured geometry, panel
///     and scroll transitions — flows through this one discriminated union into
///     <see cref="AppReducer.Update" />. Renderers never mutate state or run
///     effects directly; they only emit <see cref="AppMsg" />.
/// </summary>
/// <remarks>
///     <para>
///         This half of the union is deliberately domain-free: no transcript, no
///         agent, no costs. The Harbor chat arms live on
///         <see cref="ChatAppMsg" /> (a subtype), so a non-chat host can reduce
///         the generic arms with <see cref="AppReducer" /> alone and without
///         pulling the AI domain in.
///     </para>
///     <para>
///         Extension point: <see cref="IAppReducerPlugin" />. The generic
///         reducer calls <c>Reduce</c> first (domain claims the message), then
///         its own arms, then <c>After</c> (domain folds the state the generic
///         pass produced). See <see cref="AppReducer.Update" /> for the ordering
///         rationale.
///     </para>
/// </remarks>
public abstract record AppMsg
{
    /// <summary>Replace the input box text programmatically (e.g. renderer prefill).</summary>
    /// <param name="Text">The new input text.</param>
    public sealed record InputText(string Text) : AppMsg;

    /// <summary>Host asks the app to quit (<see cref="TerminalUiState.Ui.ShouldQuit" />).</summary>
    public sealed record Quit : AppMsg;

    /// <summary>
    ///     Reset to a fresh empty state (e.g. clear-screen). The TEA replacement
    ///     for the old <c>Transition(_ =&gt; new UiState())</c> fold — rides the same
    ///     CAS + <see cref="AppReducer.Update" /> path as every other message, so a
    ///     racing agent event serializes strictly before or after the reset,
    ///     never interleaved mid-state.
    /// </summary>
    public sealed record Reset : AppMsg;

    /// <summary>A resolved UI action with the originating key (key-input path).</summary>
    /// <param name="Action">The abstract action, resolved from the raw key via <see cref="ChatKeyMap" />.</param>
    /// <param name="Pressed">The original key, for any action that needs the character/modifiers.</param>
    public sealed record KeyInput(ChatAction Action, UiKey Pressed) : AppMsg;

    /// <summary>The renderer reports the visible history height (for scroll clamping).</summary>
    /// <param name="HistoryHeight">Number of history rows visible this frame.</param>
    public sealed record Viewport(int HistoryHeight) : AppMsg;

    /// <summary>The renderer reports the wrapped transcript height (for scroll %).</summary>
    /// <param name="TotalLines">Total wrapped history rows.</param>
    public sealed record HistoryMeasured(int TotalLines) : AppMsg;

    /// <summary>
    ///     Toggle a panel between <see cref="TuiPanelState.Hidden" /> and
    ///     <see cref="TuiPanelState.Visible" />. If the panel is currently focused,
    ///     toggling hides it and returns focus to chat.
    /// </summary>
    /// <param name="Id">The panel id (must already be registered).</param>
    public sealed record TogglePanel(string Id) : AppMsg;

    /// <summary>
    ///     Set focus to a specific panel, or return focus to chat when
    ///     <paramref name="Id" /> is <see langword="null" />. The previously focused
    ///     panel (if any) drops back to <see cref="TuiPanelState.Visible" />.
    /// </summary>
    /// <param name="Id">Panel id, or <see langword="null" /> to focus chat.</param>
    public sealed record FocusPanel(string? Id) : AppMsg;

    /// <summary>
    ///     Cycle keyboard focus to the next visible panel; if the last panel is
    ///     currently focused, return focus to chat.
    /// </summary>
    public sealed record CyclePanelFocus : AppMsg;

    /// <summary>
    ///     Grow or shrink the panel by <paramref name="Delta" /> rows (Top/Bottom) or
    ///     columns (Left/Right). Clamped to [<c>PanelRegistry.MinSize</c> ..
    ///     <c>PanelRegistry.MaxSize</c>] by the reducer.
    /// </summary>
    /// <param name="Id">The panel id.</param>
    /// <param name="Delta">Signed delta (positive = grow, negative = shrink).</param>
    public sealed record ResizePanel(string Id, int Delta) : AppMsg;

    /// <summary>
    ///     Reset <see cref="TerminalUiState.Ui.ScrollOffset" /> to 0 (pin to live tail).
    ///     Emitted by a renderer when it detects a new run just started so
    ///     streaming output is always visible. <see cref="IAppReducerPlugin.After" />
    ///     gives the domain a chance to fold its own rising-edge bookkeeping on top.
    /// </summary>
    public sealed record ScrollResetToTail : AppMsg;

    /// <summary>
    ///     Clamp <see cref="TerminalUiState.Ui.ScrollOffset" /> to
    ///     <c>[0 .. <paramref name="MaxScroll" />]</c> after the renderer measured
    ///     the current maximum (which depends on the wrapped transcript height +
    ///     pinned stream rows — both only known after layout).
    /// </summary>
    /// <param name="MaxScroll">Maximum legal scroll offset this frame.</param>
    public sealed record ScrollClamp(int MaxScroll) : AppMsg;

    /// <summary>
    ///     Host-side seeding of the registered panel ids + default states + default
    ///     sizes into <see cref="TerminalUiState" />. Dispatched once at startup by
    ///     the host renderer (and on plugin reload). Not for renderer-time use — this
    ///     is a host initialization message, the TEA equivalent of dispatching
    ///     <see cref="ChatAppMsg.ConfigureRuntime" />.
    /// </summary>
    /// <param name="Ids">Registered panel ids in registration order.</param>
    /// <param name="States">Per-panel default state (Hidden unless re-registering).</param>
    /// <param name="Sizes">Per-panel default size (provider's DefaultSize unless re-registering).</param>
    public sealed record SeedPanels(
        ImmutableArray<string> Ids,
        ImmutableDictionary<string, TuiPanelState> States,
        ImmutableDictionary<string, int> Sizes) : AppMsg;

    /// <summary>
    ///     Move a panel-local cursor into the store (#360, FP-005/TEA).
    ///     Providers dispatch this instead of mutating provider-local fields;
    ///     <c>Build</c> reads the cursor back from
    ///     <c>UiState.Ui.PanelCursors[Id]</c> (missing key = 0).
    /// </summary>
    /// <param name="Id">The panel id (e.g. <c>"diagnostics"</c>, <c>"file-tree"</c>).</param>
    /// <param name="Cursor">The new zero-based cursor position (clamped to ≥ 0).</param>
    public sealed record SetPanelCursor(string Id, int Cursor) : AppMsg;

    /// <summary>
    ///     Move a panel-local directory into the store (#360, FP-005/TEA).
    ///     Sets <c>UiState.Ui.PanelDirs[Id]</c> and resets the panel cursor to 0
    ///     atomically so descend/parent navigation never leaves a stale
    ///     selection behind. Empty <paramref name="Directory" /> clears back to
    ///     the process working directory.
    /// </summary>
    /// <param name="Id">The panel id (e.g. <c>"file-tree"</c>).</param>
    /// <param name="Directory">The new directory (full path, or empty for CWD).</param>
    public sealed record SetPanelDirectory(string Id, string Directory) : AppMsg;
}
