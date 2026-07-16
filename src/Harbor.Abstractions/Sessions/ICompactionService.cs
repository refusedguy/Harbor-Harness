using Harbor.Abstractions.Models;
namespace Harbor.Abstractions.Sessions;
/// <summary>
///     Compaction service — summarizes session when context window is exceeded.
/// </summary>
/// <remarks>
///     <para>
///         The compaction service is invoked by the agent loop at the start of each turn. If
///         <see cref="ShouldCompact" /> returns <see langword="true" />, <see cref="CompactAsync" />
///         runs an LLM summarization call over the head of the message history and produces a
///         structured Markdown summary that replaces the pruned messages.
///     </para>
///     <para>
///         Implementations MUST be thread-safe for <see cref="ShouldCompact" />. <see cref="CompactAsync" />
///         is single-flight per call.
///     </para>
/// </remarks>
public interface ICompactionService
{
    /// <summary>
    ///     Returns <see langword="true" /> if the estimated token count of the messages is within
    ///     the model's reserve threshold of its context window.
    /// </summary>
    /// <param name="messages">The current message history.</param>
    /// <param name="model">The model whose context window to check against.</param>
    /// <returns><see langword="true" /> if compaction should run.</returns>
    public bool ShouldCompact(IReadOnlyList<AgentMessage> messages, ModelInfo model);

    /// <summary>
    ///     Compact the message history by summarizing the head and keeping a recent tail.
    /// </summary>
    /// <param name="sessionId">The session id (for logging).</param>
    /// <param name="messages">The full message history.</param>
    /// <param name="model">The model whose context window triggered the compaction.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="CompactionResult" /> with the summary and pruned-message metadata, or failure.</returns>
    public Task<Result<CompactionResult>> CompactAsync(
        string sessionId,
        IReadOnlyList<AgentMessage> messages,
        ModelInfo model,
        CancellationToken ct = default);
}

/// <summary>
///     The result of a successful compaction run.
/// </summary>
/// <param name="Summary">The generated summary text.</param>
/// <param name="PrunedMessageCount">How many messages were folded into the summary.</param>
/// <param name="TokensSaved">Estimated tokens saved by the compaction.</param>
/// <param name="Duration">Wall-clock time spent compacting.</param>
/// <param name="SummaryMessage">The summary as an <see cref="AgentMessage" /> ready to append to the session.</param>
public sealed record CompactionResult(
    string Summary,
    int PrunedMessageCount,
    int TokensSaved,
    TimeSpan Duration,
    AgentMessage SummaryMessage);

/// <summary>
///     Token estimator — heuristic-based (chars/4).
/// </summary>
/// <remarks>
///     <para>
///         Used by <see cref="ICompactionService" /> to decide when to compact without needing to
///         round-trip a tokenization request to the model. Implementations SHOULD be fast and
///         deterministic — they are called on every turn.
///     </para>
/// </remarks>
public interface ITokenEstimator
{
    /// <summary>
    ///     Estimate the token count of a string.
    /// </summary>
    /// <param name="text">The text to estimate.</param>
    /// <returns>Estimated token count.</returns>
    public int Estimate(string text);

    /// <summary>
    ///     Estimate the token count of a single message.
    /// </summary>
    /// <param name="message">The message to estimate.</param>
    /// <returns>Estimated token count.</returns>
    public int EstimateMessage(AgentMessage message);

    /// <summary>
    ///     Estimate the token count of an enumeration of messages.
    /// </summary>
    /// <param name="messages">The messages to estimate.</param>
    /// <returns>Estimated total token count.</returns>
    public int EstimateMessages(IEnumerable<AgentMessage> messages);
}

/// <summary>
///     Default token estimator using chars/4 heuristic.
/// </summary>
/// <remarks>
///     For CJK characters (Han range) uses chars/2 instead of chars/4 to account for the
///     higher token density of CJK text. Adds a fixed 100-token per-message overhead for
///     structural framing.
/// </remarks>
public sealed class HeuristicTokenEstimator : ITokenEstimator
{
    /// <inheritdoc />
    public int Estimate(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        int cjkCount = 0;
        foreach (char c in text)
        {
            if (c >= 0x4E00 && c <= 0x9FFF) cjkCount++;
        }

        int otherCount = text.Length - cjkCount;
        return (int)Math.Ceiling(cjkCount / 2.0 + otherCount / 4.0);
    }

    /// <inheritdoc />
    public int EstimateMessage(AgentMessage message)
    {
        return message switch
        {
            UserMessage u => Estimate(u.Content) + 100,
            AssistantMessage a => a.Parts.Sum(EstimatePart) + 100,
            ToolResultMessage tr => tr.Results.Sum(r => Estimate(r.Output)) + 100,
            _ => 50
        };
    }

    /// <inheritdoc />
    public int EstimateMessages(IEnumerable<AgentMessage> messages) =>
        messages.Sum(EstimateMessage);

    private int EstimatePart(ContentPart part) => part switch
    {
        TextPart t => Estimate(t.Text),
        ThinkingPart th => Estimate(th.Text),
        ToolCallPart tc => tc.ToolName.Length + Estimate(tc.Args.GetRawText()),
        FilePart => 200,
        _ => 50
    };
}
