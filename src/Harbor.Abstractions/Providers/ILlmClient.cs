using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
namespace Harbor.Abstractions.Providers;
/// <summary>
///     Strategy interface for LLM providers (Strategy pattern, GOF).
///     Each provider (Anthropic, OpenAI, OpenAI-compatible, etc.) implements this.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="ILlmClient" /> is the single abstraction every LLM provider implements. The
///         agent loop calls <see cref="StreamAsync" /> to receive a stream of <see cref="LlmEvent" />s
///         (text deltas, thinking deltas, tool-call deltas, finish/error events) and converts them
///         into agent events on the event bus.
///     </para>
///     <para>
///         Implementations MUST be thread-safe for <see cref="GetModelsAsync" />. <see cref="StreamAsync" />
///         is single-use per call: a fresh <see cref="IAsyncEnumerable{T}" /> is returned each time.
///     </para>
/// </remarks>
public interface ILlmClient
{
    /// <summary>
    ///     The provider id this client serves.
    /// </summary>
    public ProviderId ProviderId { get; }

    /// <summary>
    ///     Stream completion from the model.
    ///     Returns an async enumerable of <see cref="LlmEvent" />.
    /// </summary>
    /// <param name="request">The request to send.</param>
    /// <param name="cancellationToken">Cancellation token used to abort the stream mid-flight.</param>
    /// <returns>An async stream of provider events.</returns>
    public IAsyncEnumerable<LlmEvent> StreamAsync(
        LlmRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Get available models for this provider.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success with the list of models, or failure with an error message.</returns>
    public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
///     Request to an LLM provider.
/// </summary>
/// <param name="Model">Model id (without provider prefix).</param>
/// <param name="Messages">Ordered list of messages forming the conversation.</param>
/// <param name="SystemPrompt">System prompt for the call (Anthropic-style separate field).</param>
/// <param name="Tools">Tools available to the model.</param>
/// <param name="ToolChoice">Optional tool-choice strategy; <see langword="null" /> means model-default.</param>
/// <param name="MaxOutputTokens">Optional cap on output tokens.</param>
/// <param name="Temperature">Optional sampling temperature.</param>
/// <param name="TopP">Optional nucleus sampling probability.</param>
/// <param name="TopK">Optional top-k sampling parameter.</param>
/// <param name="ReasoningEffort">Optional reasoning-effort hint for reasoning-aware models.</param>
/// <param name="CacheStrategy">Optional prompt-caching strategy.</param>
/// <param name="ExtraHeaders">Optional extra HTTP headers to send with the request.</param>
/// <param name="SessionAffinity">Optional session affinity hint (for sticky routing on multi-region providers).</param>
public sealed record LlmRequest(
    string Model,
    IReadOnlyList<LlmMessage> Messages,
    string SystemPrompt,
    IReadOnlyList<ToolDefinition> Tools,
    ToolChoice? ToolChoice = null,
    int? MaxOutputTokens = null,
    decimal? Temperature = null,
    decimal? TopP = null,
    int? TopK = null,
    ReasoningEffort? ReasoningEffort = null,
    CacheStrategy CacheStrategy = CacheStrategy.None,
    IReadOnlyDictionary<string, string>? ExtraHeaders = null,
    string? SessionAffinity = null);

/// <summary>
///     Message in LLM-specific format (provider-agnostic).
/// </summary>
public abstract record LlmMessage
{
    /// <summary>
    ///     The role string (<c>user</c>, <c>assistant</c>, etc.).
    /// </summary>
    public abstract string Role { get; }
}

/// <summary>
///     A user message in LLM format.
/// </summary>
/// <param name="Content">The content blocks of the message.</param>
public sealed record LlmUserMessage(IReadOnlyList<LlmContentBlock> Content) : LlmMessage
{
    /// <inheritdoc />
    public override string Role => "user";

    /// <summary>
    ///     Convenience factory for a plain-text user message.
    /// </summary>
    /// <param name="text">The text content.</param>
    /// <returns>A new <see cref="LlmUserMessage" /> wrapping a single <see cref="LlmTextBlock" />.</returns>
    public static LlmUserMessage Text(string text) =>
        new(new LlmContentBlock[] { new LlmTextBlock(text) });
}

/// <summary>
///     An assistant message in LLM format.
/// </summary>
/// <param name="Content">The content blocks of the message.</param>
/// <param name="StopReason">Optional provider-native stop reason string.</param>
public sealed record LlmAssistantMessage(
    IReadOnlyList<LlmContentBlock> Content,
    string? StopReason = null) : LlmMessage
{
    /// <inheritdoc />
    public override string Role => "assistant";
}

/// <summary>
///     A tool result message in LLM format.
/// </summary>
/// <param name="ToolCallId">The id of the originating tool call.</param>
/// <param name="ToolName">The tool that produced this result.</param>
/// <param name="Output">The output text of the tool call.</param>
/// <param name="IsError">Whether the result represents an error.</param>
public sealed record LlmToolResultMessage(
    string ToolCallId,
    string ToolName,
    string Output,
    bool IsError) : LlmMessage
{
    /// <inheritdoc />
    public override string Role => "user";
}

/// <summary>
///     Base type for content blocks in LLM-specific messages.
/// </summary>
public abstract record LlmContentBlock
{
    /// <summary>
    ///     The block type discriminator (<c>text</c>, <c>image</c>, <c>tool_call</c>, <c>tool_result</c>, <c>thinking</c>).
    /// </summary>
    public abstract string Type { get; }
}

/// <summary>
///     A text content block.
/// </summary>
/// <param name="Text">The text content.</param>
public sealed record LlmTextBlock(string Text) : LlmContentBlock
{
    /// <inheritdoc />
    public override string Type => "text";
}

/// <summary>
///     An image content block.
/// </summary>
/// <param name="MimeType">MIME type (e.g. <c>image/png</c>).</param>
/// <param name="Data">Raw image bytes.</param>
public sealed record LlmImageBlock(string MimeType, byte[] Data) : LlmContentBlock
{
    /// <inheritdoc />
    public override string Type => "image";
}

/// <summary>
///     A tool-call content block (the model's request to invoke a tool).
/// </summary>
/// <param name="Id">The tool call id (later matched against tool results).</param>
/// <param name="Name">The tool name.</param>
/// <param name="Arguments">The raw JSON arguments.</param>
public sealed record LlmToolCallBlock(string Id, string Name, JsonElement Arguments) : LlmContentBlock
{
    /// <inheritdoc />
    public override string Type => "tool_call";
}

/// <summary>
///     A tool-result content block (Anthropic-style tool results as content blocks).
/// </summary>
/// <param name="ToolUseId">The id of the originating tool call.</param>
/// <param name="Content">The output text of the tool call.</param>
/// <param name="IsError">Whether the result represents an error.</param>
public sealed record LlmToolResultBlock(string ToolUseId, string Content, bool IsError) : LlmContentBlock
{
    /// <inheritdoc />
    public override string Type => "tool_result";
}

/// <summary>
///     A thinking/reasoning content block (extended thinking, o1 reasoning summaries).
/// </summary>
/// <param name="Text">The reasoning text.</param>
public sealed record LlmThinkingBlock(string Text) : LlmContentBlock
{
    /// <inheritdoc />
    public override string Type => "thinking";
}

/// <summary>
///     Definition of a tool passed to the LLM.
/// </summary>
/// <param name="Name">The tool name.</param>
/// <param name="Description">Human-readable description shown to the model.</param>
/// <param name="InputSchema">JSON Schema describing the tool's input arguments.</param>
public sealed record ToolDefinition(
    string Name,
    string Description,
    JsonDocument InputSchema);
