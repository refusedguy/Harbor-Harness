using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;

namespace Harbor.Core.Sessions;

public sealed class TokenTracker : ITokenTracker
{
    private readonly HeuristicTokenEstimator _estimator;
    private int _totalInputTokens;
    private int _totalOutputTokens;
    private int _totalReasoningTokens;
    private int _totalCacheReadTokens;
    private int _totalCacheWriteTokens;

    // Running-estimate cache (B3): _runningEstimate covers exactly the leading
    // _trackedCount messages of the history. Appends reported through
    // <see cref="RecordAppendedMessage" /> extend the cache incrementally; any
    // other change to the history (external append, compaction prune,
    // truncation) desynchronizes the count and forces exactly one full rescan
    // on the next ShouldCompact call before O(1) checks resume.
    private int _runningEstimate;
    private int _trackedCount;

    public int ReserveTokens { get; set; } = 16384;

    public TokenTracker() : this(new HeuristicTokenEstimator()) { }

    public TokenTracker(HeuristicTokenEstimator estimator)
    {
        _estimator = estimator;
    }

    public void RecordTurnUsage(Usage usage)
    {
        _totalInputTokens += usage.InputTokens;
        _totalOutputTokens += usage.OutputTokens;
        _totalReasoningTokens += usage.ReasoningTokens ?? 0;
        _totalCacheReadTokens += usage.CacheReadTokens ?? 0;
        _totalCacheWriteTokens += usage.CacheWriteTokens ?? 0;
    }

    public int Estimate(string text) => _estimator.Estimate(text);

    public int EstimateMessage(AgentMessage message) => _estimator.EstimateMessage(message);

    public int EstimateTokens(IReadOnlyList<AgentMessage> messages) => _estimator.EstimateMessages(messages);

    /// <inheritdoc />
    public void RecordAppendedMessage(AgentMessage message)
    {
        _runningEstimate += _estimator.EstimateMessage(message);
        _trackedCount++;
    }

    /// <inheritdoc />
    public bool ShouldCompact(IReadOnlyList<AgentMessage> messages, ModelInfo model)
    {
        int estimated;
        if (messages.Count == _trackedCount)
        {
            // Fast path: the history length matches what the running cache
            // covers, so no message can have been appended or pruned since.
            estimated = _runningEstimate;
        }
        else
        {
            // Staleness fallback: the cache cannot know about externally
            // appended messages (or a compaction prune) — recompute once and
            // re-sync so subsequent turns are O(1) again.
            estimated = _estimator.EstimateMessages(messages);
            _runningEstimate = estimated;
            _trackedCount = messages.Count;
        }

        return estimated > model.ContextWindow - ReserveTokens;
    }

    public TokenStats GetStats()
    {
        return new TokenStats(_totalInputTokens, _totalOutputTokens, _totalReasoningTokens, _totalCacheReadTokens, _totalCacheWriteTokens);
    }
}