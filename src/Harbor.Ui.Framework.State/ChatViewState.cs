using System.Collections.Immutable;
using Harbor.Abstractions.Models;

namespace Harbor.Ui.Framework.State;

/// <summary>
///     Immutable chat-view state slice. Produced only by <see cref="ChatViewReducer" />
///     — never mutated inside a renderer.
/// </summary>
/// <remarks>
///     <para>
///         Designed for NativeAOT and zero-reflection: all members are value types
///         or <see cref="ImmutableArray{T}" />. No <see cref="List{T}" />, no
///         reflection-based binding.
///     </para>
/// </remarks>
public sealed record ChatViewState
{
    /// <summary>Current transcript lines, oldest first.</summary>
    public ImmutableArray<ChatLineViewModel> Lines { get; init; } = ImmutableArray<ChatLineViewModel>.Empty;

    /// <summary>Active tool calls for the current turn.</summary>
    public ImmutableArray<ToolCallViewModel> ToolCalls { get; init; } = ImmutableArray<ToolCallViewModel>.Empty;

    /// <summary>Whether the assistant is currently streaming text.</summary>
    public bool IsStreaming { get; init; }

    /// <summary>Whether the assistant is currently emitting thinking/reasoning tokens.</summary>
    public bool IsThinking { get; init; }

    /// <summary>Whether the agent loop is actively running.</summary>
    public bool IsAgentRunning { get; init; }

    /// <summary>Accumulated text delta for the currently streaming message.</summary>
    public string StreamingBuffer { get; init; } = string.Empty;

    /// <summary>
    ///     Deltas not yet concatenated into <see cref="StreamingBuffer" />
    ///     (flush policy: <see cref="StreamingSync" />).
    /// </summary>
    public ChunkedBuffer PendingStreaming { get; init; } = ChunkedBuffer.Empty;

    /// <summary>Human-readable status message (e.g. "thinking…", "calling tool…").</summary>
    public string StatusMessage { get; init; } = string.Empty;

    /// <summary>Pull-to-load progress (0–1).</summary>
    public double PullProgress { get; init; }

    /// <summary>Pull-to-load vertical offset.</summary>
    public double PullOffset { get; init; }

    /// <summary>Whether older messages can be loaded.</summary>
    public bool CanLoadOlder { get; init; }

    /// <summary>Whether the pull indicator is visible.</summary>
    public bool ShowPullIndicator { get; init; }

    /// <summary>Content zoom scale factor.</summary>
    public double ContentScale { get; init; }
}

/// <summary>
///     One chat line projected for the chat view state. Immutable snapshot
///     of a transcript entry.
/// </summary>
/// <param name="Role">Semantic origin of the line.</param>
/// <param name="Text">Display-ready text.</param>
public sealed record ChatLineViewModel(ChatRole Role, string Text)
{
    /// <summary>Optional UTC timestamp.</summary>
    public DateTime? TimestampUtc { get; init; }
}

/// <summary>
///     One tool call projected for the chat view state. Immutable snapshot
///     of a tool invocation.
/// </summary>
/// <param name="Id">Stable identifier used to coalesce start/end events.</param>
/// <param name="ToolName">Human-readable tool name.</param>
/// <param name="ArgsPreview">Truncated arguments preview.</param>
/// <param name="Status">Current status: "running", "success", or "error".</param>
/// <param name="ResultPreview">Truncated result preview.</param>
/// <param name="Duration">Elapsed execution time.</param>
/// <param name="IsExpanded">Whether the tool-call card is expanded.</param>
/// <param name="IsDiffTool">Whether this is a diff-producing tool.</param>
/// <param name="DiffFilePath">Optional diff file path.</param>
/// <param name="DiffPreview">Optional diff preview text.</param>
/// <param name="DiffFull">Optional full diff text.</param>
public sealed record ToolCallViewModel(
    string Id,
    string ToolName,
    string ArgsPreview,
    string Status,
    string ResultPreview,
    TimeSpan Duration,
    bool IsExpanded,
    bool IsDiffTool,
    string? DiffFilePath = null,
    string? DiffPreview = null,
    string? DiffFull = null);
