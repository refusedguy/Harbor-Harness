using Harbor.Abstractions.Models;
namespace Harbor.Abstractions.Sessions;

public interface ITokenTracker
{
    public void RecordTurnUsage(Usage usage);

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
    public void RecordAppendedMessage(AgentMessage message)
    {
    }

    public int Estimate(string text);
    public int EstimateMessage(AgentMessage message);
    public int EstimateTokens(IReadOnlyList<AgentMessage> messages);
    public bool ShouldCompact(IReadOnlyList<AgentMessage> messages, ModelInfo model);
    public TokenStats GetStats();
}

public sealed record TokenStats(int TotalInputTokens, int TotalOutputTokens, int? TotalReasoningTokens, int? TotalCacheReadTokens, int? TotalCacheWriteTokens);
