using Harbor.Abstractions.Events;

namespace Harbor.Tui.Abstractions.State;

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
}
