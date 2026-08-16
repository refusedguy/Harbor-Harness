using MemoryPack;
namespace Harbor.Abstractions.Models;
/// <summary>
///     Base type for all messages in a session.
/// </summary>
/// <remarks>
///     <para>
///         All messages belong to a session (<see cref="SessionId" />) and carry a stable
///         <see cref="Id" /> (guid) plus a <see cref="CreatedAt" /> timestamp. Messages form an
///         optional tree via <see cref="ParentId" /> — used for compaction summaries and
///         branch-style edits.
///     </para>
///     <para>
///         MemoryPack uses <see cref="MemoryPackUnionAttribute" /> to serialize the polymorphic
///         hierarchy as tagged unions: <c>0</c> = <see cref="UserMessage" />, <c>1</c> =
///         <see cref="AssistantMessage" />, <c>2</c> = <see cref="ToolResultMessage" />.
///     </para>
/// </remarks>
/// <param name="Id">Stable unique identifier (typically a guid as N-string).</param>
/// <param name="SessionId">The owning session id.</param>
/// <param name="CreatedAt">UTC timestamp when the message was created.</param>
/// <param name="ParentId">Optional parent message id (for compaction summaries and branches).</param>
[MemoryPackable]
[MemoryPackUnion(0, typeof(UserMessage))]
[MemoryPackUnion(1, typeof(AssistantMessage))]
[MemoryPackUnion(2, typeof(ToolResultMessage))]
public abstract partial record AgentMessage(
    string Id,
    string SessionId,
    DateTimeOffset CreatedAt,
    string? ParentId = null)
{
    /// <summary>
    ///     The role string used in JSONL persistence and LLM conversion
    ///     (e.g. <c>"user"</c>, <c>"assistant"</c>, <c>"tool_result"</c>).
    /// </summary>
    public abstract string Role { get; }
}

/// <summary>
///     User input message.
/// </summary>
/// <param name="Id">Stable unique identifier.</param>
/// <param name="SessionId">The owning session id.</param>
/// <param name="CreatedAt">UTC timestamp.</param>
/// <param name="Content">The user's prompt text.</param>
/// <param name="Agent">The agent name that received the prompt.</param>
/// <param name="Model">The model id targeted at the time of submission.</param>
/// <param name="ParentId">Optional parent message id.</param>
[MemoryPackable]
public sealed partial record UserMessage(
    string Id,
    string SessionId,
    DateTimeOffset CreatedAt,
    string Content,
    string Agent,
    string Model,
    string? ParentId = null) : AgentMessage(Id, SessionId, CreatedAt, ParentId)
{
    /// <inheritdoc />
    public override string Role => "user";
}

/// <summary>
///     Assistant response message.
/// </summary>
/// <param name="Id">Stable unique identifier.</param>
/// <param name="SessionId">The owning session id.</param>
/// <param name="CreatedAt">UTC timestamp.</param>
/// <param name="Parts">Ordered content parts (text, thinking, tool calls).</param>
/// <param name="StopReason">Why the model stopped generating.</param>
/// <param name="Usage">Token usage reported by the provider.</param>
/// <param name="Model">The model id that produced this message.</param>
/// <param name="ParentId">Optional parent message id.</param>
/// <param name="IsSummary">Whether this message is a compaction summary.</param>
/// <param name="SummaryFirstKeptId">For summary messages: id of the first tail message kept after compaction.</param>
[MemoryPackable]
public sealed partial record AssistantMessage(
    string Id,
    string SessionId,
    DateTimeOffset CreatedAt,
    IReadOnlyList<ContentPart> Parts,
    StopReason StopReason,
    Usage Usage,
    string Model,
    string? ParentId = null,
    bool IsSummary = false,
    string? SummaryFirstKeptId = null) : AgentMessage(Id, SessionId, CreatedAt, ParentId)
{
    /// <inheritdoc />
    public override string Role => "assistant";

    /// <summary>
    ///     Returns an empty assistant message with a fresh id and zero usage. Used to seed
    ///     the streaming accumulator in <c>AgentLoop</c>.
    /// </summary>
    /// <param name="sessionId">The owning session id.</param>
    /// <param name="model">The model id producing the message.</param>
    /// <returns>An empty <see cref="AssistantMessage" /> ready to accumulate streamed parts.</returns>
    public static AssistantMessage Empty(string sessionId, string model) => new(
        Guid.NewGuid().ToString("N"),
        sessionId,
        DateTimeOffset.UtcNow,
        Array.Empty<ContentPart>(),
        StopReason.Stop,
        new Usage(0, 0),
        model);

    /// <summary>
    ///     Returns a copy of this message with an additional <see cref="TextPart" /> appended.
    /// </summary>
    /// <param name="text">The text to append.</param>
    /// <returns>A new <see cref="AssistantMessage" /> with the appended part.</returns>
    public AssistantMessage AppendText(string text)
    {
        // Manual array copy avoids the LINQ `Append(...).ToArray()` pipeline, which
        // allocates a struct iterator + final array. We allocate exactly one array
        // (the new Parts) and copy the existing items by index.
        var newArray = new ContentPart[Parts.Count + 1];
        for (int i = 0; i < Parts.Count; i++)
        {
            newArray[i] = Parts[i];
        }
        newArray[Parts.Count] = new TextPart(text);
        return this with { Parts = newArray };
    }

    /// <summary>
    ///     Returns a copy of this message with an additional <see cref="ThinkingPart" /> appended.
    /// </summary>
    /// <param name="text">The thinking text to append.</param>
    /// <returns>A new <see cref="AssistantMessage" /> with the appended part.</returns>
    public AssistantMessage AppendThinking(string text)
    {
        var newArray = new ContentPart[Parts.Count + 1];
        for (int i = 0; i < Parts.Count; i++)
        {
            newArray[i] = Parts[i];
        }
        newArray[Parts.Count] = new ThinkingPart(text);
        return this with { Parts = newArray };
    }

    /// <summary>
    ///     Returns a copy of this message with an additional <see cref="ToolCallPart" /> appended.
    /// </summary>
    /// <param name="toolCall">The tool call part to append.</param>
    /// <returns>A new <see cref="AssistantMessage" /> with the appended part.</returns>
    public AssistantMessage AppendToolCall(ToolCallPart toolCall)
    {
        var newArray = new ContentPart[Parts.Count + 1];
        for (int i = 0; i < Parts.Count; i++)
        {
            newArray[i] = Parts[i];
        }
        newArray[Parts.Count] = toolCall;
        return this with { Parts = newArray };
    }

    /// <summary>
    ///     Returns a copy of this message with the finish metadata set.
    /// </summary>
    /// <param name="reason">Why generation stopped.</param>
    /// <param name="usage">Final token usage from the provider.</param>
    /// <returns>A new <see cref="AssistantMessage" /> with finish metadata.</returns>
    public AssistantMessage WithFinish(StopReason reason, Usage usage) =>
        this with { StopReason = reason, Usage = usage };
}

/// <summary>
///     Message containing results of tool executions.
/// </summary>
/// <param name="Id">Stable unique identifier.</param>
/// <param name="SessionId">The owning session id.</param>
/// <param name="CreatedAt">UTC timestamp.</param>
/// <param name="Results">The tool execution results for this turn.</param>
/// <param name="ParentId">Optional parent message id.</param>
[MemoryPackable]
public sealed partial record ToolResultMessage(
    string Id,
    string SessionId,
    DateTimeOffset CreatedAt,
    IReadOnlyList<ToolResultEntry> Results,
    string? ParentId = null) : AgentMessage(Id, SessionId, CreatedAt, ParentId)
{
    /// <inheritdoc />
    public override string Role => "tool_result";
}

/// <summary>
///     Base type for content parts within a message.
/// </summary>
/// <remarks>
///     A message's <c>Parts</c> array is an ordered sequence of <see cref="ContentPart" />
///     subclasses. MemoryPack serializes them as tagged unions: <c>0</c> = <see cref="TextPart" />,
///     <c>1</c> = <see cref="ThinkingPart" />, <c>2</c> = <see cref="ToolCallPart" />, <c>3</c> = <see cref="FilePart" />.
/// </remarks>
[MemoryPackable]
[MemoryPackUnion(0, typeof(TextPart))]
[MemoryPackUnion(1, typeof(ThinkingPart))]
[MemoryPackUnion(2, typeof(ToolCallPart))]
[MemoryPackUnion(3, typeof(FilePart))]
public abstract partial record ContentPart
{
    /// <summary>
    ///     Type discriminator string (e.g. <c>"text"</c>, <c>"thinking"</c>, <c>"tool_call"</c>, <c>"file"</c>).
    /// </summary>
    public abstract string Type { get; }
}

/// <summary>
///     A text content part.
/// </summary>
/// <param name="Text">The text content.</param>
[MemoryPackable]
public sealed partial record TextPart(string Text) : ContentPart
{
    /// <inheritdoc />
    public override string Type => "text";
}

/// <summary>
///     A reasoning/thinking content part (extended thinking, o1 reasoning summaries).
/// </summary>
/// <param name="Text">The reasoning text.</param>
[MemoryPackable]
public sealed partial record ThinkingPart(string Text) : ContentPart
{
    /// <inheritdoc />
    public override string Type => "thinking";
}

/// <summary>
///     A tool call content part.
/// </summary>
/// <param name="Id">The tool call id (matches <see cref="ToolResultEntry.ToolCallId" />).</param>
/// <param name="ToolName">The name of the tool to invoke.</param>
/// <param name="Args">The raw JSON arguments.</param>
[MemoryPackable]
public sealed partial record ToolCallPart(
    string Id,
    string ToolName,
    [property: MemoryPackAllowSerialize] JsonElement Args) : ContentPart
{
    /// <inheritdoc />
    public override string Type => "tool_call";

    // MemoryPack source-gen hook: register the JsonElement formatter before any
    // (de)serialization of ToolCallPart occurs. This ensures the Args member can
    // be round-tripped even though JsonElement is not natively MemoryPackable.
    static partial void StaticConstructor() => JsonElementMemoryPackFormatter.EnsureRegistered();
}

/// <summary>
///     A file content part (image, audio, etc.).
/// </summary>
/// <param name="Path">Logical path of the file.</param>
/// <param name="MimeType">MIME type (e.g. <c>image/png</c>).</param>
/// <param name="SizeBytes">File size in bytes.</param>
/// <param name="Data">Optional in-memory file bytes. May be <see langword="null" /> for lazy loading.</param>
[MemoryPackable]
public sealed partial record FilePart(
    string Path,
    string MimeType,
    long SizeBytes,
    byte[]? Data = null) : ContentPart
{
    /// <inheritdoc />
    public override string Type => "file";
}

/// <summary>
///     Result of a single tool execution (returned by ITool.ExecuteAsync).
///     Does not include tool call ID/name — those are added by the agent loop when persisting.
/// </summary>
[MemoryPackable]
public sealed partial record ToolResult
{

    /// <summary>
    ///     Private constructor used by MemoryPack (excludes <see cref="Metadata" /> to
    ///     keep the binary payload type-safe).
    /// </summary>
    [MemoryPackConstructor]
    private ToolResult(string Output, bool IsError, IReadOnlyList<FileAttachment>? Attachments)
    {
        this.Output = Output;
        this.IsError = IsError;
        this.Attachments = Attachments;
    }

    /// <summary>
    ///     Public constructor preserving the original positional API.
    /// </summary>
    public ToolResult(string Output, bool IsError, object? Metadata = null, IReadOnlyList<FileAttachment>? Attachments = null)
    {
        this.Output = Output;
        this.IsError = IsError;
        this.Metadata = Metadata;
        this.Attachments = Attachments;
    }
    /// <summary>Tool output text.</summary>
    public string Output { get; init; }

    /// <summary>Whether the result represents an error.</summary>
    public bool IsError { get; init; }

    /// <summary>Optional file attachments.</summary>
    public IReadOnlyList<FileAttachment>? Attachments { get; init; }

    /// <summary>
    ///     Optional metadata. Excluded from MemoryPack serialization because
    ///     <see cref="object" /> is not safely serializable across assembly boundaries.
    /// </summary>
    [MemoryPackIgnore]
    public object? Metadata { get; init; }

    /// <summary>
    ///     Returns a successful tool result with optional metadata.
    /// </summary>
    /// <param name="output">The tool output text.</param>
    /// <param name="metadata">Optional metadata (not serialized).</param>
    /// <returns>A <see cref="ToolResult" /> with <see cref="IsError" /> = <see langword="false" />.</returns>
    public static ToolResult Success(string output, object? metadata = null) =>
        new(output, false, metadata);

    /// <summary>
    ///     Returns an error tool result with optional metadata.
    /// </summary>
    /// <param name="output">The error message text.</param>
    /// <param name="metadata">Optional metadata (not serialized).</param>
    /// <returns>A <see cref="ToolResult" /> with <see cref="IsError" /> = <see langword="true" />.</returns>
    public static ToolResult Error(string output, object? metadata = null) =>
        new(output, true, metadata);
}

/// <summary>
///     Tool execution result with call ID and tool name attached (used in messages).
/// </summary>
[MemoryPackable]
public sealed partial record ToolResultEntry
{

    /// <summary>
    ///     Private constructor used by MemoryPack (excludes <see cref="Metadata" /> to
    ///     keep the binary payload type-safe).
    /// </summary>
    [MemoryPackConstructor]
    private ToolResultEntry(string ToolCallId, string ToolName, string Output, bool IsError)
    {
        this.ToolCallId = ToolCallId;
        this.ToolName = ToolName;
        this.Output = Output;
        this.IsError = IsError;
    }

    /// <summary>
    ///     Public constructor preserving the original positional API.
    /// </summary>
    public ToolResultEntry(string ToolCallId, string ToolName, string Output, bool IsError, object? Metadata = null)
    {
        this.ToolCallId = ToolCallId;
        this.ToolName = ToolName;
        this.Output = Output;
        this.IsError = IsError;
        this.Metadata = Metadata;
    }
    /// <summary>Tool call ID this result corresponds to.</summary>
    public string ToolCallId { get; init; }

    /// <summary>Name of the tool that produced this result.</summary>
    public string ToolName { get; init; }

    /// <summary>Tool output text.</summary>
    public string Output { get; init; }

    /// <summary>Whether the result represents an error.</summary>
    public bool IsError { get; init; }

    /// <summary>
    ///     Optional metadata. Excluded from MemoryPack serialization because
    ///     <see cref="object" /> is not safely serializable across assembly boundaries.
    /// </summary>
    [MemoryPackIgnore]
    public object? Metadata { get; init; }

    /// <summary>
    ///     Build a <see cref="ToolResultEntry" /> from a <see cref="ToolResult" />, attaching the
    ///     call id and tool name from the originating <see cref="ToolCallPart" />.
    /// </summary>
    /// <param name="toolCallId">The originating tool call id.</param>
    /// <param name="toolName">The tool name that produced the result.</param>
    /// <param name="result">The raw tool result.</param>
    /// <returns>A <see cref="ToolResultEntry" /> ready to persist in a <see cref="ToolResultMessage" />.</returns>
    public static ToolResultEntry From(string toolCallId, string toolName, ToolResult result) =>
        new(toolCallId, toolName, result.Output, result.IsError, result.Metadata);
}

/// <summary>
///     File attachment returned by tools (e.g. read of image file).
/// </summary>
/// <param name="Path">The file path.</param>
/// <param name="MimeType">MIME type of the file (e.g. <c>image/png</c>).</param>
/// <param name="Data">The raw file bytes.</param>
[MemoryPackable]
public sealed partial record FileAttachment(
    string Path,
    string MimeType,
    byte[] Data);
