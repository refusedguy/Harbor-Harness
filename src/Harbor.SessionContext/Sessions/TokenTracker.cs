using Harbor.Abstractions.Extensions;
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
    // compaction prune, truncation, or an in-place content rewrite reported
    // through <see cref="RecordUpdatedMessage" />) desynchronizes the entry and
    // forces exactly one full rescan on the next ShouldCompact call before O(1)
    // checks resume. A bare same-count/different-content rewrite without such a
    // report is invisible to the Count-keyed fast path by construction (#1106).
    // A single count-keyed cache here used to collide across sessions sharing
    // this singleton (same message count, different content → wrong estimate →
    // wrong compaction decision), hence the per-session dictionary. Guarded by
    // _sync: the tracker is a singleton fed by concurrent session runs.
    private readonly Dictionary<string, SessionEstimate> _estimates = new(StringComparer.Ordinal);

    private int _reserveTokens = CompactionPolicy.DefaultReserveTokens;

    /// <summary>
    ///     Fraction of a model's context window held back as a compaction reserve
    ///     (#2225, opencode <c>RESERVE_SHARE</c>). A fixed floor alone is too small
    ///     for large windows: the heuristic estimator runs low on real providers,
    ///     so a 16k reserve on a 200k window lets the request grow until the
    ///     provider refuses it. Growing the reserve with the window makes the
    ///     preventive pass fire while there is still headroom to summarize,
    ///     instead of leaving the reactive overflow retry to pay for it.
    /// </summary>
    public const double ContextReserveShare = 0.15;

    /// <summary>
    ///     Token reserve below the model's context window that triggers compaction.
    ///     Init-only (#80): mutating the threshold on the shared singleton mid-run
    ///     would silently move the goalposts for every session at once.
    ///     Single source of truth for the threshold (#1106 divergent reserves):
    ///     <c>CompactionService</c> keeps no second reserve — its
    ///     <c>ShouldCompact</c> verdict is this tracker's.
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
        Ensure.NotNull(estimator);
        _estimator = estimator;
        _logger = logger ?? NullLogger<TokenTracker>.Instance;
    }

    /// <summary>
    ///     The reserve that actually applies to <paramref name="model" />: the
    ///     configured floor, raised to <see cref="ContextReserveShare" /> of the
    ///     window when the window is large enough for that to matter (#2225).
    ///     Both the compaction verdict here and <c>CompactionService</c>'s budget
    ///     math read this one value, so the two cannot drift apart (#1106).
    /// </summary>
    /// <remarks>
    ///     <see cref="ReserveTokens" /> stays the configured floor (init-only):
    ///     small windows keep the historical 16,384 threshold, and a test or a
    ///     host that lowers the floor still moves the verdict for its window.
    /// </remarks>
    /// <param name="model">The model whose context window sizes the reserve.</param>
    /// <returns>The effective reserve, never below <see cref="ReserveTokens" />.</returns>
    public int EffectiveReserveTokens(ModelInfo model)
    {
        Ensure.NotNull(model);
        return Math.Max(ReserveTokens, (int)(model.ContextWindow * ContextReserveShare));
    }

    public void RecordTurnUsage(Usage usage)
    {
        lock (_sync)
        {
            _totalInputTokens += usage.InputTokens;
            _totalOutputTokens += usage.OutputTokens;
            _totalReasoningTokens += usage.ReasoningTokens ?? 0;
            _totalCacheReadTokens += usage.CacheReadTokens ?? 0;
            _totalCacheWriteTokens += usage.CacheWriteTokens ?? 0;
        }
    }

    public int Estimate(string text) => _estimator.Estimate(text);

    public int EstimateMessage(AgentMessage message) => _estimator.EstimateMessage(message);

    public int EstimateTokens(IReadOnlyList<AgentMessage> messages) => _estimator.EstimateMessages(messages);

    /// <inheritdoc />
    public void RecordAppendedMessage(AgentMessage message)
    {
        Ensure.NotNull(message);
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
    public void RecordUpdatedMessage(AgentMessage message)
    {
        Ensure.NotNull(message);
        lock (_sync)
        {
            // No delta to apply: the tracker keeps no per-message breakdown, so
            // an in-place rewrite cannot adjust the running total — drop the
            // entry instead. The next ShouldCompact call rescans once and O(1)
            // checks resume, exactly like the external-append/prune fallback.
            _estimates.Remove(message.SessionId);
        }
    }

    /// <inheritdoc />
    public bool ShouldCompact(IReadOnlyList<AgentMessage> messages, ModelInfo model)
    {
        Ensure.NotNull(messages);
        Ensure.NotNull(model);

        // An unreported window (0, the sentinel ProviderConfig uses when
        // context_length is missing) cannot produce a token budget. Refuse to
        // compact, but say so at Warning: the old code folded this into the
        // small-window case and logged only at Debug, so a provider that omits
        // context_length silently disabled compaction for the whole session.
        if (model.ContextWindow <= 0)
        {
            TokenTrackerLog.UnknownContextWindow(_logger, model.Id, model.ProviderId);
            return false;
        }

        // The reserve scales with the window (#2225): 15% of a large context,
        // but never below the configured floor for small ones. Computed once
        // here so the guard and the verdict use the same number.
        int reserve = EffectiveReserveTokens(model);

        // Degenerate but reported window (#80 companion): at or below the
        // reserve it would make `estimated > negative` true on EVERY turn.
        // Refuse instead of compacting blindly.
        if (model.ContextWindow <= reserve)
        {
            TokenTrackerLog.SkippingCompactionCheck(_logger, model.ContextWindow, reserve);
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

        return estimated > model.ContextWindow - reserve;
    }

    public TokenStats GetStats()
    {
        lock (_sync)
        {
            return new TokenStats(_totalInputTokens, _totalOutputTokens, _totalReasoningTokens, _totalCacheReadTokens, _totalCacheWriteTokens);
        }
    }

    /// <summary>Per-session running estimate: token sum over the leading Count messages.</summary>
    /// <param name="Estimate">Cached token estimate.</param>
    /// <param name="Count">History length the estimate covers.</param>
    private sealed record SessionEstimate(int Estimate, int Count);
}

/// <summary>
///     SG2: BCL <c>[LoggerMessage]</c> delegates for <see cref="TokenTracker" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class TokenTrackerLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Skipping compaction check: context window {ContextWindow} does not exceed reserve {ReserveTokens}")]
    public static partial void SkippingCompactionCheck(ILogger logger, int contextWindow, int reserveTokens);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Model {ModelId} ({ProviderId}) reports no context window; compaction checks are disabled because the token budget cannot be computed")]
    public static partial void UnknownContextWindow(ILogger logger, string modelId, string providerId);

}
