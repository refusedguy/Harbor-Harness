using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Application.Sessions;

public sealed class TokenTracker : ITokenTracker
{
    private readonly HeuristicTokenEstimator _estimator;
    private readonly ILogger<TokenTracker> _logger;
    private readonly object _sync = new();
    private int _totalInputTokens;
    private int _totalOutputTokens;
    private int _totalReasoningTokens;
    private int _totalCacheReadTokens;
    private int _totalCacheWriteTokens;

    // Running-estimate cache (B3), scoped per session (#80): each entry covers
    // exactly the leading Count messages of that session's history. Appends
    // reported through <see cref="RecordAppendedMessage" /> extend the entry
    // incrementally; any other change to the history (external append,
    // compaction prune, truncation) desynchronizes the count and forces exactly
    // one full rescan on the next ShouldCompact call before O(1) checks resume.
    // A single count-keyed cache here used to collide across sessions sharing
    // this singleton (same message count, different content → wrong estimate →
    // wrong compaction decision), hence the per-session dictionary. Guarded by
    // _sync: the tracker is a singleton fed by concurrent session runs.
    private readonly Dictionary<string, SessionEstimate> _estimates = new(StringComparer.Ordinal);

    private int _reserveTokens = 16384;

    /// <summary>
    ///     Token reserve below the model's context window that triggers compaction.
    ///     Init-only (#80): mutating the threshold on the shared singleton mid-run
    ///     would silently move the goalposts for every session at once.
    /// </summary>
    public int ReserveTokens
    {
        get => _reserveTokens;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, 0);
            _reserveTokens = value;
        }
    }

    public TokenTracker() : this(new HeuristicTokenEstimator()) { }

    public TokenTracker(HeuristicTokenEstimator estimator, ILogger<TokenTracker>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(estimator);
        _estimator = estimator;
        _logger = logger ?? NullLogger<TokenTracker>.Instance;
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
        ArgumentNullException.ThrowIfNull(message);
        int increment = _estimator.EstimateMessage(message);
        lock (_sync)
        {
            if (_estimates.TryGetValue(message.SessionId, out SessionEstimate? current))
            {
                _estimates[message.SessionId] = current with
                {
                    Estimate = current.Estimate + increment,
                    Count = current.Count + 1
                };
            }
            else
            {
                _estimates[message.SessionId] = new SessionEstimate(increment, 1);
            }
        }
    }

    /// <inheritdoc />
    public bool ShouldCompact(IReadOnlyList<AgentMessage> messages, ModelInfo model)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(model);

        // Degenerate window (#80 companion): missing provider metadata surfaces
        // as ContextWindow == 0, which used to make `estimated > negative`
        // true on EVERY turn. Refuse instead of compacting blindly.
        if (model.ContextWindow <= ReserveTokens)
        {
            _logger.LogDebug(
                "Skipping compaction check: context window {ContextWindow} does not exceed reserve {ReserveTokens}",
                model.ContextWindow,
                ReserveTokens);
            return false;
        }

        if (messages.Count == 0)
        {
            return false;
        }

        string? sessionId = messages[0]?.SessionId;
        int estimated;
        lock (_sync)
        {
            if (sessionId is not null
                && _estimates.TryGetValue(sessionId, out SessionEstimate? cached)
                && cached.Count == messages.Count)
            {
                // Fast path: the entry covers exactly this session's history
                // length, so no message can have been appended or pruned since.
                estimated = cached.Estimate;
            }
            else
            {
                // Staleness fallback: the cache cannot know about externally
                // appended messages (or a compaction prune) — recompute once and
                // re-sync so subsequent turns are O(1) again.
                estimated = _estimator.EstimateMessages(messages);
                if (sessionId is not null)
                {
                    _estimates[sessionId] = new SessionEstimate(estimated, messages.Count);
                }
            }
        }

        return estimated > model.ContextWindow - ReserveTokens;
    }

    public TokenStats GetStats()
    {
        return new TokenStats(_totalInputTokens, _totalOutputTokens, _totalReasoningTokens, _totalCacheReadTokens, _totalCacheWriteTokens);
    }

    /// <summary>Per-session running estimate: token sum over the leading Count messages.</summary>
    /// <param name="Estimate">Cached token estimate.</param>
    /// <param name="Count">History length the estimate covers.</param>
    private sealed record SessionEstimate(int Estimate, int Count);
}
