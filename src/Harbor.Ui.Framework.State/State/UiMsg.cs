using System.Collections.Immutable;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Ui.Framework.Panels;
namespace Harbor.Ui.Framework.State;
/// <summary>
///     The single message type for the interactive UI (TEA/MVU "Msg"). Every input
///     — agent events, key presses, and view-measured geometry — flows through this
///     one discriminated union into <see cref="UiReducer.Update" />. Renderers never
///     mutate state or run effects directly; they only emit <see cref="UiMsg" />.
/// </summary>
public abstract record UiMsg
{
    /// <summary>Wrap an agent-driven event into the UI pipeline (existing data path).</summary>
    /// <param name="Event">The agent event to reduce.</param>
    public sealed record Agent(AgentEvent Event) : UiMsg;

    /// <summary>
    ///     Effect-host bookkeeping: a prompt run is starting (the loop's own
    ///     <see cref="AgentStartEvent" /> may not have arrived yet). Marks the store
    ///     running so user input is suppressed during the run.
    /// </summary>
    public sealed record AgentStarted : UiMsg;

    /// <summary>
    ///     Effect-host bookkeeping: the prompt run ended. <paramref name="Status" />
    ///     overrides the status bar explicitly; when null, an existing
    ///     <c>"error"</c> status is preserved and everything else falls back to
    ///     <c>"idle"</c>. <paramref name="Error" /> appends an error transcript line.
    /// </summary>
    public sealed record AgentEnded(string? Status = null, string? Error = null) : UiMsg;

    /// <summary>Direct status-bar text update (e.g. <c>"idle"</c> after an abort).</summary>
    /// <param name="Status">The new status-bar text.</param>
    public sealed record StatusChanged(string Status) : UiMsg;

    /// <summary>
    ///     Host-side transcript line (slash handler errors, session-switch notes).
    ///     The TEA replacement for ad-hoc <c>Transition(s => s.AddLine(...))</c> folds.
    /// </summary>
    public sealed record AppendLine(ChatRole Role, string Text, string? ToolCallId = null) : UiMsg;

    /// <summary>Replace the input box text programmatically (e.g. renderer prefill).</summary>
    /// <param name="Text">The new input text.</param>
    public sealed record InputText(string Text) : UiMsg;

    /// <summary>Host asks the app to quit (<see cref="UiState.ShouldQuit" />).</summary>
    public sealed record Quit : UiMsg;

    /// <summary>A resolved UI action with the originating key (key-input path).</summary>
    /// <param name="Action">The abstract action (already mapped from a key by the renderer).</param>
    /// <param name="Pressed">The original key, for any action that needs the character/modifiers.</param>
    public sealed record KeyInput(ChatAction Action, UiKey Pressed) : UiMsg;

    /// <summary>The renderer reports the visible history height (for scroll clamping).</summary>
    /// <param name="HistoryHeight">Number of history rows visible this frame.</param>
    public sealed record Viewport(int HistoryHeight) : UiMsg;

    /// <summary>The renderer reports the wrapped transcript height (for scroll %).</summary>
    /// <param name="TotalLines">Total wrapped history rows.</param>
    public sealed record HistoryMeasured(int TotalLines) : UiMsg;

    /// <summary>
    ///     Toggle a panel between <see cref="TuiPanelState.Hidden" /> and
    ///     <see cref="TuiPanelState.Visible" />. If the panel is currently focused,
    ///     toggling hides it and returns focus to chat.
    /// </summary>
    /// <param name="Id">The panel id (must already be registered).</param>
    public sealed record TogglePanel(string Id) : UiMsg;

    /// <summary>
    ///     Set focus to a specific panel, or return focus to chat when
    ///     <paramref name="Id" /> is <see langword="null" />. The previously focused
    ///     panel (if any) drops back to <see cref="TuiPanelState.Visible" />.
    /// </summary>
    /// <param name="Id">Panel id, or <see langword="null" /> to focus chat.</param>
    public sealed record FocusPanel(string? Id) : UiMsg;

    /// <summary>
    ///     Cycle keyboard focus to the next visible panel; if the last panel is
    ///     currently focused, return focus to chat.
    /// </summary>
    public sealed record CyclePanelFocus : UiMsg;

    /// <summary>
    ///     Grow or shrink the panel by <paramref name="Delta" /> rows (Top/Bottom) or
    ///     columns (Left/Right). Clamped to [<c>PanelRegistry.MinSize</c> ..
    ///     <c>PanelRegistry.MaxSize</c>] by the reducer.
    /// </summary>
    /// <param name="Id">The panel id.</param>
    /// <param name="Delta">Signed delta (positive = grow, negative = shrink).</param>
    public sealed record ResizePanel(string Id, int Delta) : UiMsg;

    /// <summary>
    ///     Reset <see cref="UiState.ScrollOffset" /> to 0 (pin to live tail). Emitted by
    ///     a renderer when it detects the agent just started a new run
    ///     (<c>state.IsAgentRunning &amp;&amp; !state.WasRunning</c>) so streaming output
    ///     is always visible. The reducer also does this on <c>AgentStartEvent</c> as a
    ///     belt-and-braces guarantee.
    /// </summary>
    public sealed record ScrollResetToTail : UiMsg;

    /// <summary>
    ///     Clamp <see cref="UiState.ScrollOffset" /> to <c>[0 .. <paramref name="MaxScroll" />]</c>
    ///     after the renderer measured the current maximum (which depends on the wrapped
    ///     transcript height + pinned stream rows — both only known after layout).
    /// </summary>
    /// <param name="MaxScroll">Maximum legal scroll offset this frame.</param>
    public sealed record ScrollClamp(int MaxScroll) : UiMsg;

    /// <summary>
    ///     Host-side seeding of the registered panel ids + default states + default
    ///     sizes into <see cref="UiState" />. Dispatched once at startup by
    ///     <c>SpectreTuiRenderer.SeedPanelRegistryIntoState</c> (and on plugin reload).
    ///     Not for renderer-time use — this is a host initialization message, the TEA
    ///     equivalent of <see cref="UiStore.BindSession" />.
    /// </summary>
    /// <param name="Ids">Registered panel ids in registration order.</param>
    /// <param name="States">Per-panel default state (Hidden unless re-registering).</param>
    /// <param name="Sizes">Per-panel default size (provider's DefaultSize unless re-registering).</param>
    public sealed record SeedPanels(
        ImmutableArray<string> Ids,
        ImmutableDictionary<string, TuiPanelState> States,
        ImmutableDictionary<string, int> Sizes) : UiMsg;
}
