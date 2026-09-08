using System.Collections.Immutable;
using Harbor.Ui.Framework.Panels;

namespace Harbor.Ui.Framework.State;

/// <summary>
///     Generic terminal-UI state: zero chat/AI concerns. Input, scroll,
///     focus, panels and quit flag — everything a non-chat TUI needs.
///     BCL + framework panels only; no transcript, no costs, no agents.
/// </summary>
public sealed record TerminalUiState
{
    /// <summary>Editable prompt state (text + history navigation).</summary>
    public InputModel Input { get; init; } = InputModel.Empty;

    /// <summary>Which region currently owns the keyboard (drives highlight + routing).</summary>
    public FocusMode Focus { get; init; } = FocusMode.Input;

    /// <summary>
    ///     History scroll-back offset (0 = pinned to newest line, grows toward the
    ///     top). Clamped to <c>TotalLines - ViewportLines</c> by the reducer.
    /// </summary>
    public int ScrollOffset { get; init; }

    /// <summary>Number of history rows currently visible (reported by the renderer).</summary>
    public int ViewportLines { get; init; }

    /// <summary>Total number of wrapped history rows (reported by the renderer).</summary>
    public int TotalLines { get; init; }

    /// <summary>How far the history is scrolled, as a percentage (0 = bottom/live, 100 = top).</summary>
    public int ScrollPercent
    {
        get
        {
            int max = Math.Max(0, TotalLines - ViewportLines);
            if (max == 0) return 0;
            // ScrollOffset is rows lifted from the tail (0 = bottom).
            return (int)Math.Round(100.0 * ScrollOffset / max);
        }
    }

    /// <summary>
    ///     Per-panel runtime state. Mirrors the <c>PanelRegistry</c>; updated by
    ///     <c>UiReducer</c> on <c>TogglePanel</c> / <c>FocusPanel</c> /
    ///     <c>ResizePanel</c>. Renderers read this to decide which panels to render.
    /// </summary>
    public ImmutableDictionary<string, TuiPanelState> PanelStates { get; init; }
        = ImmutableDictionary<string, TuiPanelState>.Empty;

    /// <summary>
    ///     Per-panel size override (rows or cols, depending on the panel's placement).
    ///     <c>0</c> = use the provider's <c>DefaultSize</c>.
    /// </summary>
    public ImmutableDictionary<string, int> PanelSizes { get; init; }
        = ImmutableDictionary<string, int>.Empty;

    /// <summary>
    ///     Id of the panel currently owning keyboard focus, or <see langword="null" />
    ///     when the chat / input box owns focus. Driven by <c>FocusPanel</c> /
    ///     <c>CyclePanelFocus</c> messages.
    /// </summary>
    public string? FocusedPanelId { get; init; }

    /// <summary>
    ///     Registered panel ids in registration order. Maintained by the host
    ///     (<c>PanelRegistry</c>) via <c>UiStore.Transition</c>. Read by the reducer
    ///     for <c>CyclePanelFocus</c> so it stays pure (no IRegistry dependency).
    /// </summary>
    public ImmutableArray<string> RegisteredPanelIds { get; init; }
        = ImmutableArray<string>.Empty;

    /// <summary>Whether the user has requested to quit the interactive loop.</summary>
    public bool ShouldQuit { get; init; }

    public static readonly TerminalUiState Empty = new();

    /// <summary>Return a snapshot with the editable input model replaced.</summary>
    public TerminalUiState SetInput(InputModel input) => this with { Input = input };

    /// <summary>Return a snapshot with the keyboard focus replaced.</summary>
    public TerminalUiState SetFocus(FocusMode focus) => this with { Focus = focus };

    /// <summary>Return a snapshot with the history scroll offset clamped to valid range.</summary>
    public TerminalUiState SetScroll(int offset)
    {
        int max = Math.Max(0, TotalLines - ViewportLines);
        int clamped = Math.Clamp(offset, 0, max);
        return clamped == ScrollOffset ? this : this with { ScrollOffset = clamped };
    }
}
