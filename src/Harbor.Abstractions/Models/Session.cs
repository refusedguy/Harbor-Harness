using System.Text.Json.Serialization;
using MemoryPack;
namespace Harbor.Abstractions.Models;
/// <summary>
///     Represents a conversation session.
/// </summary>
/// <param name="Id">Stable unique identifier (guid as N-string).</param>
/// <param name="ProjectId">Hash-based project id derived from <see cref="Directory" />, used for grouping sessions.</param>
/// <param name="Directory">Absolute working directory of the session.</param>
/// <param name="Title">Human-readable session title.</param>
/// <param name="Agent">The agent name bound to this session.</param>
/// <param name="Model">The model id bound to this session.</param>
/// <param name="ProviderId">The provider id bound to this session.</param>
/// <param name="CreatedAt">UTC timestamp when the session was created.</param>
/// <param name="UpdatedAt">UTC timestamp of the last activity in the session.</param>
/// <param name="Metadata">Aggregated session stats (cost, tokens, etc.).</param>
/// <param name="ParentSessionId">Optional parent session id (for branches/forks).</param>
[MemoryPackable]
public sealed partial record Session(
    string Id,
    string ProjectId,
    string Directory,
    string Title,
    string Agent,
    string Model,
    string ProviderId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    SessionMetadata Metadata,
    string? ParentSessionId = null)
{
    /// <summary>
    ///     Factory for a new session: generates a fresh id, derives the project id from the
    ///     directory, and seeds the metadata with zero usage.
    /// </summary>
    /// <param name="directory">The working directory of the session.</param>
    /// <param name="agentName">The agent name to bind.</param>
    /// <param name="providerId">The provider id to bind.</param>
    /// <param name="modelId">The model id to bind.</param>
    /// <param name="title">Optional title; defaults to <c>Session yyyy-MM-dd HH:mm</c>.</param>
    /// <returns>A new <see cref="Session" /> ready to persist.</returns>
    public static Session Create(string directory, string agentName, string providerId, string modelId, string? title = null)
    {
        string id = Guid.NewGuid().ToString("N");
        string projectId = directory.GetHashCode(StringComparison.Ordinal).ToString("x");
        var now = DateTimeOffset.UtcNow;
        return new Session(
            id,
            projectId,
            directory,
            title ?? $"Session {now:yyyy-MM-dd HH:mm}",
            agentName,
            modelId,
            providerId,
            now,
            now,
            SessionMetadata.Empty);
    }
}

/// <summary>
///     Aggregated metadata about a session.
/// </summary>
/// <param name="Cost">Total estimated cost in USD.</param>
/// <param name="TokensInput">Total input tokens consumed.</param>
/// <param name="TokensOutput">Total output tokens generated.</param>
/// <param name="TokensReasoning">Total reasoning tokens consumed (when supported).</param>
/// <param name="TokensCacheRead">Total cache-read tokens (when supported).</param>
/// <param name="TokensCacheWrite">Total cache-write tokens (when supported).</param>
/// <param name="MessageCount">Total number of messages appended to the session.</param>
/// <param name="TimeCompacting">Total time spent compacting the session.</param>
[MemoryPackable]
public sealed partial record SessionMetadata(
    decimal Cost,
    int TokensInput,
    int TokensOutput,
    int TokensReasoning,
    int TokensCacheRead,
    int TokensCacheWrite,
    int MessageCount,
    TimeSpan? TimeCompacting)
{
    /// <summary>
    ///     A zeroed <see cref="SessionMetadata" /> for fresh sessions.
    /// </summary>
    public static SessionMetadata Empty => new(0m, 0, 0, 0, 0, 0, 0, null);

    /// <summary>
    ///     Returns a copy of this metadata with the usage from one LLM call added in.
    /// </summary>
    /// <param name="usage">The usage to add.</param>
    /// <returns>A new <see cref="SessionMetadata" /> with incremented counters.</returns>
    public SessionMetadata AddUsage(Usage usage)
    {
        return this with
        {
            TokensInput = TokensInput + usage.InputTokens,
            TokensOutput = TokensOutput + usage.OutputTokens,
            TokensReasoning = TokensReasoning + (usage.ReasoningTokens ?? 0),
            TokensCacheRead = TokensCacheRead + (usage.CacheReadTokens ?? 0),
            TokensCacheWrite = TokensCacheWrite + (usage.CacheWriteTokens ?? 0),
            MessageCount = MessageCount + 1
        };
    }
}

/// <summary>
///     Token usage returned by LLM provider.
/// </summary>
/// <param name="InputTokens">Number of input (prompt) tokens consumed.</param>
/// <param name="OutputTokens">Number of output (generated) tokens consumed.</param>
/// <param name="ReasoningTokens">Optional reasoning tokens (extended thinking, o1 reasoning summaries).</param>
/// <param name="CacheReadTokens">Optional cache-read tokens (Anthropic prompt caching).</param>
/// <param name="CacheWriteTokens">Optional cache-write tokens (Anthropic prompt caching).</param>
[MemoryPackable]
public sealed partial record Usage(
    int InputTokens,
    int OutputTokens,
    int? ReasoningTokens = null,
    int? CacheReadTokens = null,
    int? CacheWriteTokens = null);

/// <summary>
///     Pricing for a model (per million tokens, USD).
/// </summary>
/// <param name="InputPerMillion">USD per 1M input tokens.</param>
/// <param name="OutputPerMillion">USD per 1M output tokens.</param>
/// <param name="CacheReadPerMillion">Optional USD per 1M cache-read tokens.</param>
/// <param name="CacheWritePerMillion">Optional USD per 1M cache-write tokens.</param>
[MemoryPackable]
public sealed partial record Pricing(
    decimal InputPerMillion,
    decimal OutputPerMillion,
    decimal? CacheReadPerMillion = null,
    decimal? CacheWritePerMillion = null)
{
    /// <summary>
    ///     Sentinel pricing for models with unknown pricing. <see cref="CalculateCost" /> returns 0.
    /// </summary>
    public static Pricing Unknown => new(0m, 0m);

    /// <summary>
    ///     Calculate the cost in USD for the given <see cref="Usage" />.
    /// </summary>
    /// <param name="usage">The usage to price.</param>
    /// <returns>The total cost in USD.</returns>
    public decimal CalculateCost(Usage usage)
    {
        decimal input = usage.InputTokens / 1_000_000m * InputPerMillion;
        decimal output = usage.OutputTokens / 1_000_000m * OutputPerMillion;
        decimal cacheRead = (usage.CacheReadTokens ?? 0) / 1_000_000m * (CacheReadPerMillion ?? 0m);
        decimal cacheWrite = (usage.CacheWriteTokens ?? 0) / 1_000_000m * (CacheWritePerMillion ?? 0m);
        return input + output + cacheRead + cacheWrite;
    }
}

/// <summary>
///     Information about an LLM model.
/// </summary>
/// <param name="Id">Model id (without provider prefix).</param>
/// <param name="ProviderId">Owning provider id.</param>
/// <param name="DisplayName">Human-readable display name.</param>
/// <param name="ContextWindow">Maximum number of input tokens the model accepts.</param>
/// <param name="MaxOutputTokens">Maximum number of output tokens the model can produce in one call.</param>
/// <param name="SupportsReasoning">Whether the model supports reasoning/thinking.</param>
/// <param name="SupportsVision">Whether the model supports image inputs.</param>
/// <param name="SupportsToolUse">Whether the model supports tool/function calling.</param>
/// <param name="Pricing">Pricing information for cost calculations.</param>
/// <param name="PromptTemplate">The prompt template name the model uses (e.g. <c>anthropic</c>, <c>openai</c>).</param>
[MemoryPackable]
public sealed partial record ModelInfo(
    string Id,
    string ProviderId,
    string DisplayName,
    int ContextWindow,
    int MaxOutputTokens,
    bool SupportsReasoning,
    bool SupportsVision,
    bool SupportsToolUse,
    Pricing Pricing,
    string PromptTemplate);

/// <summary>
///     Reason the model stopped generating.
/// </summary>
[JsonConverter(typeof(StopReasonJsonConverter))]
public enum StopReason
{
    /// <summary>The model emitted a normal end-of-turn stop.</summary>
    Stop,

    /// <summary>The model hit the <c>max_output_tokens</c> limit.</summary>
    Length,

    /// <summary>The model emitted a tool call and yielded control back to the host.</summary>
    ToolUse,

    /// <summary>The model was blocked by a content filter.</summary>
    ContentFilter,

    /// <summary>An error occurred during generation.</summary>
    Error,

    /// <summary>The user cancelled the run via <see cref="IAgent.AbortSource" />.</summary>
    Aborted
}

internal sealed class StopReasonJsonConverter : JsonStringConverter<StopReason>
{
}

/// <summary>
///     Reasoning effort for reasoning-aware models.
/// </summary>
[JsonConverter(typeof(ReasoningEffortJsonConverter))]
public enum ReasoningEffort
{
    /// <summary>Minimal reasoning — fastest, cheapest.</summary>
    Low,

    /// <summary>Default reasoning effort.</summary>
    Medium,

    /// <summary>Higher-quality reasoning at greater cost.</summary>
    High,

    /// <summary>Maximum reasoning effort.</summary>
    Max
}

internal sealed class ReasoningEffortJsonConverter : JsonStringConverter<ReasoningEffort>
{
}

/// <summary>
///     Tool choice strategy.
/// </summary>
public abstract record ToolChoice
{
    /// <summary>The model decides whether and which tool to call.</summary>
    public sealed record Auto : ToolChoice;

    /// <summary>The model must not call any tools.</summary>
    public sealed record None : ToolChoice;

    /// <summary>The model must call at least one tool.</summary>
    public sealed record Required : ToolChoice;

    /// <summary>The model must call the named tool.</summary>
    /// <param name="ToolName">The tool the model must call.</param>
    public sealed record Specific(string ToolName) : ToolChoice;
}

/// <summary>
///     Cache strategy for prompt caching (Anthropic-style).
/// </summary>
public enum CacheStrategy
{
    /// <summary>No prompt caching.</summary>
    None,

    /// <summary>Short-lived (5-minute) cache entry.</summary>
    Ephemeral,

    /// <summary>Long-lived (1-hour) cache entry.</summary>
    LongLived
}

/// <summary>
///     Base class for JSON string enum converters.
/// </summary>
/// <typeparam name="T">The enum type to convert.</typeparam>
public abstract class JsonStringConverter<T> : JsonConverter<T> where T : struct, Enum
{
    /// <inheritdoc />
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? s = reader.GetString();
        return s is not null && Enum.TryParse<T>(s, true, out var v) ? v : default;
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString().ToLowerInvariant());
}
