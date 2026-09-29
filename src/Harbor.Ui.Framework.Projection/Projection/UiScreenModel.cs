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

public sealed record UiStatusSegment(
    string Text,
    Alignment Align,
    int Importance,
    UiSpanStyle? Style);

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