using Harbor.Abstractions.Models;

namespace Harbor.Abstractions.Sessions;

public interface ITokenTracker
{
    void RecordTurnUsage(Usage usage);
    int Estimate(string text);
    int EstimateMessage(AgentMessage message);
    int EstimateTokens(IReadOnlyList<AgentMessage> messages);
    bool ShouldCompact(IReadOnlyList<AgentMessage> messages, ModelInfo model);
    TokenStats GetStats();
}

public sealed record TokenStats(int TotalInputTokens, int TotalOutputTokens, int? TotalReasoningTokens, int? TotalCacheReadTokens, int? TotalCacheWriteTokens);