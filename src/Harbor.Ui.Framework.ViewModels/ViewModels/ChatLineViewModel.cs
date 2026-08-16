using Harbor.Ui.Framework.Converters;
using Harbor.Ui.Framework.State;
namespace Harbor.Ui.Framework.ViewModels;
/// <summary>
///     One chat line projected for the UI. Role + text + brush key + optional
///     timestamp, all driven by primitive properties so the same record can
///     be bound from Avalonia, WPF, MAUI, Blazor, or a TUI renderer without
///     per-platform projection logic.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why a record:</b> chat lines are immutable snapshots of a
///         <c>UiState.Lines</c> entry. Mutability would race with the
///         reducer's append-only contract, so the projector creates a fresh
///         <see cref="ChatLineViewModel" /> per UiStore transition.
///     </para>
///     <para>
///         <b>Role mapping:</b> both <see cref="RoleBrushKey" /> (resource
///         key string for the role accent color) and <see cref="RoleLabel" />
///         (short lowercase label) are derived from <see cref="Role" /> via
///         switch expressions. Platform apps resolve <see cref="RoleBrushKey" />
///         to an actual brush via a <c>BrushKeyConverter</c>-equivalent.
///     </para>
///     <para>
///         <b>Timestamp:</b> optional UTC <see cref="DateTime" /> for the
///         line. <see cref="TimestampText" /> returns a pre-formatted
///         "5m ago" / "Mar 5" string via <see cref="Converters.StatusMappers.TimeAgo" />,
///         so platforms without a relative-time converter can bind directly.
///     </para>
/// </remarks>
public sealed record ChatLineViewModel(ChatRole Role, string Text)
{
    /// <summary>
    ///     Optional timestamp for this line (when the message was received
    ///     from the agent / typed by the user). Null for lines without a
    ///     known time (e.g. replayed from history without metadata).
    /// </summary>
    public DateTime? TimestampUtc { get; init; }

    /// <summary>
    ///     The brush resource key for this role's color. Resolved by the
    ///     platform's <c>BrushKeyConverter</c>-equivalent at bind time.
    /// </summary>
    public string RoleBrushKey => Role switch
    {
        ChatRole.User => "ChatUserBrush",
        ChatRole.Assistant => "ChatAssistantBrush",
        ChatRole.Thinking => "ChatThinkingBrush",
        ChatRole.Tool => "ChatToolBrush",
        ChatRole.ToolResult => "ChatToolResultBrush",
        ChatRole.System => "ChatSystemBrush",
        ChatRole.Error => "ChatErrorBrush",
        _ => "ChatAssistantBrush"
    };

    /// <summary>
    ///     Legacy alias for <see cref="RoleBrushKey" />. Kept for backward
    ///     compatibility with existing AXAML bindings that reference
    ///     <c>BrushKey</c> — new code should use <see cref="RoleBrushKey" />.
    /// </summary>
    public string BrushKey => RoleBrushKey;

    /// <summary>Human-readable role label (lowercase).</summary>
    public string RoleLabel => Role switch
    {
        ChatRole.User => "user",
        ChatRole.Assistant => "assistant",
        ChatRole.Thinking => "thinking",
        ChatRole.Tool => "tool",
        ChatRole.ToolResult => "tool-result",
        ChatRole.System => "system",
        ChatRole.Error => "error",
        _ => Role.ToString().ToLowerInvariant()
    };

    /// <summary>
    ///     Pre-formatted relative-time string ("5m ago" / "2h ago" / "Mar 5").
    ///     Empty when <see cref="TimestampUtc" /> is null. Bound directly by
    ///     platforms that don't have a relative-time value converter.
    /// </summary>
    public string TimestampText =>
        TimestampUtc is null
            ? string.Empty
            : StatusMappers.TimeAgo(TimestampUtc.Value);

    /// <summary>
    ///     Optional short preview of <see cref="Text" />, truncated to 80
    ///     characters. Used by session-list tooltips and search results
    ///     where the full message would be too long. Returns the full text
    ///     when it's already short enough.
    /// </summary>
    public string Preview =>
        Text.Length <= 80 ? Text : Text[..77] + "...";
}
