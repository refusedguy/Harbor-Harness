using Harbor.Abstractions.Models;

namespace Harbor.Abstractions.Sessions;

/// <summary>
///     Tracks token usage and estimates how much of a model's context window a
///     session has consumed.
/// </summary>
/// <remarks>
///     The tracker is the input to compaction: <see cref="ShouldCompact" />
///     decides whether the history fits, and <see cref="GetStats" /> reports the
///     totals published to the session.
/// </remarks>
public interface ITokenTracker
{
    /// <summary>
    ///     Records the usage reported by one completed LLM call.
    /// </summary>
    /// <param name="usage">The usage figures returned by the provider.</param>
    void RecordTurnUsage(Usage usage);

    /// <summary>
    ///     Records a message that was appended to the session history so the tracker can
    ///     maintain a running token estimate instead of re-scanning the whole history on
    ///     every <see cref="ShouldCompact" /> call.
    /// </summary>
    /// <remarks>
    ///     Default implementation is a no-op so existing implementors keep compiling;
    ///     they simply fall back to whatever estimation strategy their
    ///     <see cref="ShouldCompact" /> already uses.
    /// </remarks>
    /// <param name="message">The message that was appended to the history.</param>
    void RecordAppendedMessage(AgentMessage message)
    {
    }

    /// <summary>
    ///     Records that a message already in the session history was replaced in
    ///     place (same history length, different content — what
    ///     <see cref="ISessionStore.UpdateMessageAsync" /> does: it rewrites the
    ///     entry for the id) so the tracker stops answering from the running
    ///     estimate built for the old content. The next
    ///     <see cref="ShouldCompact" /> call rescans once and O(1) checks resume.
    /// </summary>
    /// <remarks>
    ///     Default implementation is a no-op so existing implementors keep compiling;
    ///     they simply keep whatever staleness behavior their
    ///     <see cref="ShouldCompact" /> already has.
    /// </remarks>
    /// <param name="message">The message that replaced the stored one (same id).</param>
    void RecordUpdatedMessage(AgentMessage message)
    {
    }

    /// <summary>Estimates the token count of a piece of text.</summary>
    /// <param name="text">The text to estimate.</param>
    /// <returns>The estimated token count.</returns>
    int Estimate(string text);

    /// <summary>Estimates the token count of one message, including its framing.</summary>
    /// <param name="message">The message to estimate.</param>
    /// <returns>The estimated token count.</returns>
    int EstimateMessage(AgentMessage message);

    /// <summary>Estimates the token count of a whole message history.</summary>
    /// <param name="messages">The messages to estimate, in order.</param>
    /// <returns>The estimated token count.</returns>
    int EstimateTokens(IReadOnlyList<AgentMessage> messages);

    /// <summary>
    ///     Decides whether the history is close enough to the model's context
    ///     window that compaction should run.
    /// </summary>
    /// <param name="messages">The current history.</param>
    /// <param name="model">The model whose window bounds the history.</param>
    /// <returns>True when compaction is due.</returns>
    bool ShouldCompact(IReadOnlyList<AgentMessage> messages, ModelInfo model);

    /// <summary>The accumulated usage totals for the session.</summary>
    /// <returns>The current token statistics.</returns>
    TokenStats GetStats();
}

/// <summary>
///     Accumulated token usage for a session, split by the model's usage
///     categories. The cache and reasoning fields are null when the provider
///     does not report them.
/// </summary>
/// <param name="TotalInputTokens">Total prompt tokens sent to the model.</param>
/// <param name="TotalOutputTokens">Total completion tokens received from the model.</param>
/// <param name="TotalReasoningTokens">Reasoning tokens, when the provider reports them.</param>
/// <param name="TotalCacheReadTokens">Tokens read from the prompt cache, when reported.</param>
/// <param name="TotalCacheWriteTokens">Tokens written to the prompt cache, when reported.</param>
public sealed record TokenStats(int TotalInputTokens, int TotalOutputTokens, int? TotalReasoningTokens, int? TotalCacheReadTokens, int? TotalCacheWriteTokens);