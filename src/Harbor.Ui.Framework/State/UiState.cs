using System.Collections.Immutable;
using Harbor.Abstractions.Events;
using Harbor.Ui.Framework.Panels;
namespace Harbor.Ui.Framework.State;
/// <summary>
///     Semantic role of a rendered transcript line. Shared by every renderer so
///     colour/prefix logic stays consistent across frameworks.
/// </summary>
public enum ChatRole : byte
{
    User,
    Assistant,
    Thinking,
    Tool,
    ToolResult,
    System,
    Error
}

/// <summary>
///     One immutable rendered transcript line. Uses a <see cref="string" /> (not a
///     mutable builder) so the history is append-only and allocation-free to read.
/// </summary>
/// <param name="Role">Semantic origin of the line.</param>
/// <param name="Text">Already-escaped, display-ready text.</param>
public readonly record struct ChatLine(ChatRole Role, string Text);

/// <summary>
///     Currently-streaming assistant message. Mutable deltas are folded into
///     <see cref="UiState.Lines" /> on <see cref="MessageEndEvent" />.
/// </summary>
public sealed record ActiveMessage(
    string TextBuffer,
    string ThinkBuffer)
{
    public static readonly ActiveMessage Empty = new(string.Empty, string.Empty);
}

/// <summary>
///     Running cost/token accounting for the session status line.
/// </summary>
/// <param name="TokensIn">Cumulative input tokens.</param>
/// <param name="TokensOut">Cumulative output tokens.</param>
/// <param name="CostUsd">Cumulative estimated cost in USD.</param>
public readonly record struct CostSnapshot(
    long TokensIn,
    long TokensOut,
    decimal CostUsd);

/// <summary>
///     Renderer-agnostic, immutable UI snapshot. The single source of truth that
///     every interactive renderer projects from. Produced only by
///     <see cref="UiReducer" /> — never mutated inside a renderer.
/// </summary>
/// <remarks>
///     <para>
///         Designed for NativeAOT and zero-reflection: all members are value types
///         or <see cref="ImmutableArray{T}" /> (no <see cref="List{T}" />, no
///         reflection-based binding). Renderers read it on each frame and build
///         their framework-specific widgets from it.
///     </para>
/// </remarks>
public sealed record UiState
{
    /// <summary>The full transcript (user/assistant/tool/… lines), oldest first.</summary>
    public ImmutableArray<ChatLine> Lines { get; init; } = ImmutableArray<ChatLine>.Empty;

    /// <summary>Live streaming message (text + thinking) for the current turn.</summary>
    public ActiveMessage Active { get; init; } = ActiveMessage.Empty;

    /// <summary>Whether a message is actively streaming right now.</summary>
    public bool IsStreaming { get; init; }

    /// <summary>Human-readable status: idle / running / compacting / error.</summary>
    public string Status { get; init; } = "idle";

    /// <summary>Running token/cost accounting for the status line.</summary>
    public CostSnapshot Cost { get; init; }

    /// <summary>Active model id (for the header/status).</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>Active provider id (for the header/status).</summary>
    public string Provider { get; init; } = string.Empty;

    /// <summary>Active agent name (for the header/status).</summary>
    public string AgentName { get; init; } = string.Empty;

    /// <summary>Whether the agent is currently running a prompt.</summary>
    public bool IsAgentRunning { get; init; }

    /// <summary>
    ///     Snapshot of <see cref="IsAgentRunning" /> from the previous agent event
    ///     (AgentStart/AgentEnd). Lets renderers detect the rising edge
    ///     (<c>IsAgentRunning &amp;&amp; !WasRunning</c>) without keeping local mutable
    ///     state — TEA compliance (§FP-005).
    /// </summary>
    public bool WasRunning { get; init; }

    /// <summary>Whether the user has requested to quit the interactive loop.</summary>
    public bool ShouldQuit { get; init; }

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

    /// <summary>
    ///     Append a line to the transcript, returning a new immutable snapshot.
    ///     Avoids allocating an intermediate list.
    /// </summary>
    public UiState AddLine(ChatRole role, string text) =>
        this with { Lines = Lines.Add(new ChatLine(role, text)) };

    /// <summary>Replace a line at the given index (used only for in-place edits if needed).</summary>
    public UiState SetLine(int index, ChatRole role, string text)
    {
        if (index < 0 || index >= Lines.Length)
            return this;
        var builder = Lines.ToBuilder();
        builder[index] = new ChatLine(role, text);
        return this with { Lines = builder.MoveToImmutable() };
    }

    /// <summary>Return a snapshot with the editable input model replaced.</summary>
    public UiState SetInput(InputModel input) => this with { Input = input };

    /// <summary>Return a snapshot with the keyboard focus replaced.</summary>
    public UiState SetFocus(FocusMode focus) => this with { Focus = focus };

    /// <summary>
    ///     Return a snapshot with the transcript, live message, input, and scroll
    ///     state cleared. Session chrome (model/provider/agent/cost/status) is
    ///     preserved so a clear-screen does not wipe the active session identity.
    /// </summary>
    public UiState ClearTranscript() => this with
    {
        Lines = ImmutableArray<ChatLine>.Empty,
        Active = ActiveMessage.Empty,
        IsStreaming = false,
        Input = InputModel.Empty,
        ScrollOffset = 0,
        TotalLines = 0,
        ViewportLines = 0,
        Status = IsAgentRunning ? "running" : "idle"
    };

    /// <summary>Return a snapshot with the history scroll offset clamped to valid range.</summary>
    public UiState SetScroll(int offset)
    {
        int max = Math.Max(0, TotalLines - ViewportLines);
        int clamped = Math.Clamp(offset, 0, max);
        return clamped == ScrollOffset ? this : this with { ScrollOffset = clamped };
    }
}
