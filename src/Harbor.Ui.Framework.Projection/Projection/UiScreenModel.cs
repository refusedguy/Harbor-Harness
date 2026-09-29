using System.Collections.Immutable;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;

namespace Harbor.Ui.Framework.Projection;

public sealed record UiHeaderModel(
    string Model,
    string Provider,
    string AgentName,
    bool IsAgentRunning,
    bool IsStreaming,
    bool ShouldQuit,
    CostSnapshot Cost,
    string FooterText);

public sealed record UiTranscriptModel(
    IReadOnlyList<UiBlock> Blocks,
    IReadOnlyList<UiRenderedLine> RenderedLines,
    string? StreamingBlockId);

public abstract record UiBlock(string Id);

public sealed record UiMessageBlock(
    string Id,
    ChatRole Role,
    IReadOnlyList<StyledSpan> Spans,
    MessageRenderPhase Phase) : UiBlock(Id);

public enum MessageRenderPhase
{
    Complete,
    Streaming,
    Thinking
}

// #567: the projection used to declare its own `ToolCallStatus`
// { Pending, Running, Done, Error } with zero consumers in the repo. The
// shared `ToolCallState` (Harbor.Ui.Framework.Abstractions) owns the tool-call
// lifecycle now; re-declaring it here only widened the gap.

public enum UiSpanStyle
{
    Default,
    Dim,
    Accent,
    Danger,
    Success,
    RoleUser,
    RoleAssistant,
    RoleSystem,
    Tool
}

public sealed record UiInputModel(
    string Text,
    int Caret,
    bool IsEnabled,
    string Placeholder);

public sealed record UiStatusBarModel(
    IReadOnlyList<UiStatusSegment> Segments);

/// <summary>
///     One status-bar cell. <para>
///         <see cref="FixedPriority" /> is part of what the cell <i>is</i>, not
///         something each renderer decides (#568). It used to live nowhere:
///         <c>StatusSegmentOrdering</c> exists so every backend "sorts
///         identically and cannot drift apart", yet the CellForge footer never
///         called it and hand-wrote <c>FixedPriority: true</c> per cell instead —
///         a fourth hand-maintained list on the axis
///         <c>StatusSegmentBar.Fit</c> actually consumes. A type that cannot
///         express a property has it re-derived by every surface that needs it,
///         and that is the whole fan-out. Carrying it here means adding a cell
///         declares its kind once, in <c>StatusProjector</c>.
///     </para>
///     <para>
///         A <c>readonly record struct</c>, like <c>StatusSeg</c> on the CellForge
///         side: a status row is rewritten per frame, and a reference type per
///         cell would put the projection on the allocator for a row that is
///         usually six scalars. It also lets the footer take the row in a
///         <c>stackalloc</c> span and read it without touching the heap.
///     </para>
/// </summary>
/// <param name="Text">The cell's text, already formatted by the one rule.</param>
/// <param name="Align">Which third of the bar the cell sits in.</param>
/// <param name="Importance">
///     Order within its <paramref name="Align" /> group: higher sorts earlier,
///     and among right-aligned cells the rightmost flexible one is what
///     <c>StatusSegmentBar.Fit</c> drops first.
/// </param>
/// <param name="Style">Span style; renderers map it to their own accent.</param>
/// <param name="FixedPriority">
///     Whether the cell must survive truncation. The chrome and status cells are
///     the session's identity and its phase, so they stay; the counters are
///     numbers the user can ask for again, so they go first.
/// </param>
public readonly record struct UiStatusSegment(
    string Text,
    Alignment Align,
    int Importance,
    UiSpanStyle? Style,
    bool FixedPriority = false);

public enum Alignment
{
    Left,
    Center,
    Right
}

public sealed record UiScreenModel(
    UiHeaderModel Header,
    UiTranscriptModel Transcript,
    UiStatusBarModel StatusBar,
    UiInputModel Input,
    FocusMode Focus,
    string StateRevision);