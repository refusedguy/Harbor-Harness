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

    public bool ShouldCompact(IReadOnlyList<AgentMessage> messages, ModelInfo model)
    {
        int estimated = _estimator.EstimateMessages(messages);
        return estimated > model.ContextWindow - ReserveTokens;
    }

    public TokenStats GetStats()
    {
        return new TokenStats(_totalInputTokens, _totalOutputTokens, _totalReasoningTokens, _totalCacheReadTokens, _totalCacheWriteTokens);
    }
}