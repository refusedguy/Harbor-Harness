using Harbor.Abstractions.Models;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Core.Sessions;
using System.Diagnostics;
using System.Text;
using Harbor.Abstractions.Extensions;
using Microsoft.Extensions.Logging;
namespace Harbor.Core.Sessions;
/// <summary>
///     Default compaction service using anchored-summary strategy.
///     Generates a structured Markdown summary of compacted messages.
///     Performance: pooled StringBuilder, index-based cut-point (no List allocations),
///     pooled buffers for serializing intermediate message text.
/// </summary>
/// <remarks>
///     Ф8/A3: an optional <paramref name="secondaryModel" /> reference
///     (<c>"provider/model"</c>) routes the summarization request to a cheap
///     model instead of the primary one. Resolution is lazy and cached per
///     successful pair; ANY resolution failure (provider missing, model not
///     found, catalog fetch failed) falls back to the primary model so a bad
///     secondary config can never break compaction itself.
/// </remarks>
public sealed class CompactionService(
    ITokenTracker tokenTracker,
    IProviderRegistry providers,
    ILogger<CompactionService> logger,
    string? secondaryModel = null) : ICompactionService
{
    /// <summary>
    ///     A successfully resolved secondary (cheap) summarization client+model pair.
    /// </summary>
    private sealed record ResolvedSecondary(ILlmClient Client, ModelInfo Model);

    // Ф8/A3: lazily resolved secondary client; successes are cached for the
    // service lifetime, failures are NOT cached (a transient provider outage
    // must not pin the fallback forever). Reference writes are atomic, so two
    // concurrent first calls may both resolve once (benign and idempotent),
    // while every later call reads the cached pair without locking.
    private readonly ModelRef? _secondaryRef = ParseSecondary(secondaryModel);

    /// <summary>Parse the configured reference; an invalid value silently disables the feature.</summary>
    private static ModelRef? ParseSecondary(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        Result<ModelRef> parsed = ModelRef.TryParse(value);
        return parsed.IsSuccess ? parsed.Value : null;
    }

    private ResolvedSecondary? _resolvedSecondary;

    /// <summary>
    ///     Resolve the secondary summarization client+model asynchronously, or
    ///     null when no secondary is configured / it cannot be resolved right now.
    /// </summary>
    /// <remarks>
    ///     ROP-B П.22: cache hit short-circuits up front; the miss path is one
    ///     railway (client → catalog → matching model) with memoization as a
    ///     <c>Tap</c> and every failure funneling into a single logged fallback.
    /// </remarks>
    private async Task<ResolvedSecondary?> TryResolveSecondaryAsync(ModelInfo primaryModel, CancellationToken ct)
    {
        if (_secondaryRef is null)
        {
            return null;
        }

        ResolvedSecondary? cached = _resolvedSecondary;
        if (cached is not null)
        {
            return cached;
        }

        ModelRef secondaryRef = _secondaryRef;
        Result<ResolvedSecondary> outcome = await providers.GetClient(secondaryRef.ProviderId)
            .Bind(client => client.GetModelsAsync(ct).Bind(models =>
                MatchById(models, secondaryRef.ModelId)
                    .ToResult($"model '{secondaryRef.ModelId}' is not in provider '{secondaryRef.ProviderId}' catalog")
                    .Map(model => new ResolvedSecondary(client, model))))
            .ConfigureAwait(false);

        ResolvedSecondary? resolved = outcome
            .Tap(r => _resolvedSecondary = r)
            .Match(static r => (ResolvedSecondary?)r, _ => LogSecondaryFallback(primaryModel));
        return resolved;
    }

    private Maybe<ModelInfo> MatchById(IReadOnlyList<ModelInfo> models, string modelId)
    {
        for (int i = 0; i < models.Count; i++)
        {
            if (string.Equals(models[i].Id, modelId, StringComparison.Ordinal))
                return models[i];
        }

        return Maybe<ModelInfo>.None;
    }

    /// <summary>Log the fallback once per unresolved attempt and return null.</summary>
    private ResolvedSecondary? LogSecondaryFallback(ModelInfo primaryModel)
    {
        logger.LogWarning(
            "Secondary compaction model '{Secondary}' could not be resolved; falling back to primary model '{Primary}'",
            _secondaryRef, primaryModel.Id);
        return null;
    }

    private const string SummarizationPrompt = 
        "You are creating a summary of the conversation so far to provide context to a teammate who is taking over the task.\n" +
        "\n" +
        "The summary should preserve ALL important information needed to continue the work, including:\n" +
        "- The original goal and current state\n" +
        "- Decisions made and their rationale\n" +
        "- Files read and modified (with paths)\n" +
        "- Commands run and their outcomes\n" +
        "- Errors encountered and how they were resolved\n" +
        "- Outstanding questions or blockers\n" +
        "\n" +
        "Output the summary in this exact Markdown structure:\n" +
        "\n" +
        "## Goal\n" +
        "[What the user is trying to accomplish]\n" +
        "\n" +
        "## Constraints & Preferences\n" +
        "[Any constraints, preferences, or rules discovered]\n" +
        "\n" +
        "## Progress\n" +
        "### Done\n" +
        "- [Completed tasks]\n" +
        "\n" +
        "### In Progress\n" +
        "- [Currently being worked on]\n" +
        "\n" +
        "### Blocked\n" +
        "- [Items blocked, with reason]\n" +
        "\n" +
        "## Key Decisions\n" +
        "- [Decision: rationale]\n" +
        "\n" +
        "## Next Steps\n" +
        "- [Immediate next actions]\n" +
        "\n" +
        "## Critical Context\n" +
        "[Any other information needed to continue]\n" +
        "\n" +
        "## Files\n" +
        "### Read\n" +
        "- `path/to/file`\n" +
        "\n" +
        "### Modified\n" +
        "- `path/to/file` — what was changed\n" +
        "\n" +
        "Rules:\n" +
        "- Keep every section, even when empty (use \"None\" if no content).\n" +
        "- Preserve exact file paths, commands, error strings, identifiers.\n" +
        "- Do not mention the summary process or that context was compacted.\n" +
        "- Be concise but complete — every detail matters.";

    /// <summary>
    ///     Token reserve below the model's context window that triggers compaction.
    /// </summary>
    public int ReserveTokens { get; set; } = 16384;

    /// <summary>
    ///     Target token count for the kept tail when compacting.
    /// </summary>
    public int KeepRecentTokens { get; set; } = 20000;

    /// <summary>
    ///     Minimum number of recent turns to keep verbatim after compaction.
    /// </summary>
    public int TailTurns { get; set; } = 2;

    /// <summary>
    ///     Default token reserve used by <see cref="TruncateToFit" /> when the
    ///     caller does not supply one (mirrors <see cref="ReserveTokens" />).
    /// </summary>
    public const int DefaultReserveTokens = 16384;

    /// <summary>
    ///     Floor for the truncation budget so very small context windows still
    ///     keep a usable slice of history instead of an effectively empty one.
    /// </summary>
    private const int MinimumTruncationBudget = 4096;

    /// <summary>
    ///     Aggressive fallback for when LLM-based compaction fails: keep only
    ///     the most recent messages that fit the model's context budget
    ///     (<c>ContextWindow − reserve − MaxOutputTokens</c>) and drop the older
    ///     middle/head. The result is a plain tail slice — no summary is
    ///     produced, but the next request is no longer known-overfull.
    ///     <para>
    ///         The cut point never lands on a <see cref="ToolResultMessage" />:
    ///         orphan tool results whose assistant tool_call was dropped would
    ///         be rejected by providers. Orphaned results at the boundary are
    ///         dropped together with the head instead. At least one message is
    ///         always kept.
    ///     </para>
    /// </summary>
    /// <param name="messages">The current message history.</param>
    /// <param name="model">The target model (context window + output budget).</param>
    /// <param name="tokenTracker">Token estimator used to size the kept tail.</param>
    /// <param name="reserveTokens">Safety reserve below the context window.</param>
    /// <returns>A new list with the kept tail messages, or <paramref name="messages" /> when it is empty.</returns>
    public static IReadOnlyList<AgentMessage> TruncateToFit(
        IReadOnlyList<AgentMessage> messages,
        ModelInfo model,
        ITokenTracker tokenTracker,
        int reserveTokens = DefaultReserveTokens)
    {
        if (messages.Count == 0)
        {
            return messages;
        }

        int budget = ComputeTruncationBudget(model, reserveTokens);

        // Walk backwards accumulating the newest messages until the budget is hit.
        int tailTokens = 0;
        int tailStart = messages.Count;
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            int msgTokens = tokenTracker.EstimateMessage(messages[i]);
            if (tailTokens + msgTokens > budget)
            {
                break;
            }

            tailTokens += msgTokens;
            tailStart = i;
        }

        // Never open the kept slice with an orphan tool_result — its assistant
        // tool_call is in the dropped head and providers reject the pair.
        while (tailStart < messages.Count && messages[tailStart] is ToolResultMessage)
        {
            tailStart++;
        }

        // Degenerate budget (every single message overflows): keep at least the
        // most recent message rather than returning an empty history.
        if (tailStart >= messages.Count)
        {
            tailStart = messages.Count - 1;
        }

        var kept = new List<AgentMessage>(messages.Count - tailStart);
        for (int i = tailStart; i < messages.Count; i++)
        {
            kept.Add(messages[i]);
        }

        return kept;
    }

    /// <summary>
    ///     Shared token budget for <see cref="TruncateToFit" /> and
    ///     <see cref="TruncateToFitStrict" />: context window minus reserve and
    ///     max output tokens, floored so tiny windows still keep a usable slice.
    /// </summary>
    private static int ComputeTruncationBudget(ModelInfo model, int reserveTokens)
    {
        int budget = model.ContextWindow - reserveTokens - model.MaxOutputTokens;
        if (budget < MinimumTruncationBudget)
        {
            budget = Math.Max(MinimumTruncationBudget, model.ContextWindow / 2);
        }

        return budget;
    }

    /// <summary>
    ///     Strict-reduction variant of <see cref="TruncateToFit" />, used as the
    ///     compaction-failure fallback. Unlike the budget-fit walk — which keeps
    ///     everything when the history already fits — this ALWAYS drops part of
    ///     the head once the history exceeds a small floor, because after a
    ///     failed LLM compaction the next request must actually shrink, not
    ///     merely be "not provably overfull".
    ///     <para>
    ///         Policy (deterministic): keep system-role messages (none exist in
    ///         session history — the system prompt travels separately on
    ///         <c>LlmRequest</c>) plus the newest K messages, where
    ///         K = min(max(total / 2, 4), budgetFit) — the newest half clamped
    ///         to [4 .. budget-fit]. Whenever total &gt; 4 the target is strictly
    ///         below total, so reduction is guaranteed even when the whole
    ///         history would trivially fit. The cut point never opens on an
    ///         orphan <see cref="ToolResultMessage" /> (see <see cref="TruncateToFit" />),
    ///         and at least one message is always kept.
    ///     </para>
    /// </summary>
    /// <param name="messages">The current message history.</param>
    /// <param name="model">The target model (context window + output budget).</param>
    /// <param name="tokenTracker">Token estimator used to size the budget-fit ceiling.</param>
    /// <param name="reserveTokens">Safety reserve below the context window.</param>
    /// <returns>A new list holding strictly fewer messages than the input whenever the input exceeds the keep floor.</returns>
    public static IReadOnlyList<AgentMessage> TruncateToFitStrict(
        IReadOnlyList<AgentMessage> messages,
        ModelInfo model,
        ITokenTracker tokenTracker,
        int reserveTokens = DefaultReserveTokens)
    {
        if (messages.Count == 0)
        {
            return messages;
        }

        const int MinimumKeptMessages = 4;
        int total = messages.Count;

        // Budget-fit ceiling: size of the newest run that fits the token
        // budget; at least one message is always kept.
        int budget = ComputeTruncationBudget(model, reserveTokens);
        int budgetFit = 0;
        int tailTokens = 0;
        for (int i = total - 1; i >= 0; i--)
        {
            int msgTokens = tokenTracker.EstimateMessage(messages[i]);
            if (tailTokens + msgTokens > budget)
            {
                break;
            }

            tailTokens += msgTokens;
            budgetFit++;
        }

        budgetFit = Math.Max(budgetFit, 1);

        // Newest half, clamped to [MinimumKeptMessages .. budgetFit].
        // For total > MinimumKeptMessages this is strictly less than total.
        int keep = Math.Min(Math.Max(MinimumKeptMessages, total / 2), budgetFit);

        int tailStart = total - keep;

        // Never open the kept slice with an orphan tool_result — its assistant
        // tool_call is in the dropped head and providers reject the pair.
        while (tailStart < total && messages[tailStart] is ToolResultMessage)
        {
            tailStart++;
        }

        // Degenerate boundary (orphan skip consumed the whole tail): keep at
        // least the most recent message rather than returning an empty history.
        if (tailStart >= total)
        {
            tailStart = total - 1;
        }

        var kept = new List<AgentMessage>(total - tailStart);
        for (int i = tailStart; i < total; i++)
        {
            kept.Add(messages[i]);
        }

        return kept;
    }

    /// <summary>
    ///     Materialize the effective post-compaction history from a raw session
    ///     history that contains compaction summaries.
    ///     <para>
    ///         Compaction is lazy: the raw history keeps every message, and the
    ///         newest <see cref="AssistantMessage.IsSummary" /> message anchors
    ///         the cut through its <see cref="AssistantMessage.SummaryFirstKeptId" />.
    ///         The returned view is <c>[summary] + tail-from-anchor +
    ///         messages-appended-after-the-summary</c> — everything folded into
    ///         the summary is dropped, so token estimation and LLM requests see
    ///         the compacted history instead of an ever-growing raw list.
    ///     </para>
    ///     <para>
    ///         Fail-safe: when no summary exists, or the anchor id cannot be
    ///         resolved, the input instance is returned unchanged rather than
    ///         risking silent history loss.
    ///     </para>
    /// </summary>
    /// <param name="messages">The raw (append-only) session history.</param>
    /// <returns>The compacted view, or <paramref name="messages" /> when nothing is compacted.</returns>
    public static IReadOnlyList<AgentMessage> MaterializeCompactedView(IReadOnlyList<AgentMessage> messages)
    {
        int summaryIndex = -1;
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i] is AssistantMessage { IsSummary: true })
            {
                summaryIndex = i;
                break;
            }
        }

        if (summaryIndex < 0)
        {
            return messages;
        }

        var summary = (AssistantMessage)messages[summaryIndex];

        // Resolve the kept-tail start. A null anchor means the summary folded
        // in the ENTIRE pre-summary history (nothing was kept verbatim).
        int keptStart = summaryIndex;
        if (summary.SummaryFirstKeptId is string anchor)
        {
            bool resolved = false;
            for (int i = 0; i < summaryIndex; i++)
            {
                if (string.Equals(messages[i].Id, anchor, StringComparison.Ordinal))
                {
                    keptStart = i;
                    resolved = true;
                    break;
                }
            }

            if (!resolved)
            {
                return messages;
            }
        }

        var view = new List<AgentMessage>(
            1 + summaryIndex - keptStart + (messages.Count - summaryIndex - 1));
        view.Add(summary);
        for (int i = keptStart; i < summaryIndex; i++)
        {
            view.Add(messages[i]);
        }
        for (int i = summaryIndex + 1; i < messages.Count; i++)
        {
            view.Add(messages[i]);
        }

        return view;
    }

    /// <inheritdoc />
    public bool ShouldCompact(IReadOnlyList<AgentMessage> messages, ModelInfo model)
    {
        int estimated = tokenTracker.EstimateTokens(messages);
        return estimated > model.ContextWindow - ReserveTokens;
    }

    /// <inheritdoc />
    public async Task<Result<CompactionResult>> CompactAsync(
        string sessionId,
        IReadOnlyList<AgentMessage> messages,
        ModelInfo model,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            // 1. Find cut point (index-based; no List allocations)
            int tailStart = FindCutPoint(messages, KeepRecentTokens, TailTurns);

            if (tailStart == 0)
            {
                return Result.Failure<CompactionResult>("No messages to compact.");
            }

            // 2. Build summarization request
            var providerIdResult = ProviderId.TryCreate(model.ProviderId);
            if (providerIdResult.IsFailure)
            {
                return Result.Failure<CompactionResult>(providerIdResult.Error);
            }

            var clientResult = providers.GetClient(providerIdResult.Value);
            if (clientResult.IsFailure)
            {
                return Result.Failure<CompactionResult>(clientResult.Error);
            }

            // Ф8/A3: prefer the configured cheap secondary model for the
            // summarization call; fall back to the primary client/model when
            // no secondary is configured or it cannot be resolved.
            ILlmClient summaryClient = clientResult.Value;
            ModelInfo summaryModel = model;
            var secondary = await TryResolveSecondaryAsync(model, ct).ConfigureAwait(false);
            if (secondary is not null)
            {
                summaryClient = secondary.Client;
                summaryModel = secondary.Model;
            }

            string prompt = BuildSummarizationPrompt(messages, tailStart);
            // Ф8/A1: the summarization system prompt is a compile-time constant, so the
            // request is a perfect prefix-cache candidate — flag it Ephemeral.
            var request = new LlmRequest(
                summaryModel.Id,
                new[] { LlmUserMessage.Text(prompt) },
                SummarizationPrompt,
                Array.Empty<ToolDefinition>(),
                Temperature: 0.3m,
                MaxOutputTokens: 4096,
                CacheStrategy: CacheStrategy.Ephemeral);

            // 3. Stream LLM (collect full text into pooled StringBuilder)
            using var summaryBuilder = StringBuilderPool.Rent(4096);
            await foreach (var evt in summaryClient.StreamAsync(request, ct).ConfigureAwait(false))
            {
                if (evt is TextDeltaEvent td)
                {
                    summaryBuilder.Builder.Append(td.Delta);
                }
                if (evt is ErrorEvent err)
                {
                    return Result.Failure<CompactionResult>($"LLM error during compaction: {err.Message}");
                }
            }

            stopwatch.Stop();

            string summary = summaryBuilder.ToString();

            // F19: an empty summary (content filter, silent provider) used to be
            // accepted as success — the anchor would then discard the ENTIRE
            // compressed history and the model silently lost all memory of it.
            if (summary.Length == 0)
            {
                logger.LogWarning(
                    "Summarization produced an empty summary for session {SessionId}; refusing to persist an empty anchor",
                    sessionId);
                return Result.Failure<CompactionResult>("Compaction produced an empty summary.");
            }

            // 4. Compute tokens saved — iterate head slice directly without materializing a List.
            int headTokens = 0;
            for (int i = 0; i < tailStart; i++)
            {
                headTokens += tokenTracker.EstimateMessage(messages[i]);
            }
            int summaryTokens = tokenTracker.Estimate(summary);
            int tokensSaved = headTokens - summaryTokens;

            // 5. Capture first kept (tail) message id (if any) without allocating a Skip().FirstOrDefault().
            string? summaryFirstKeptId = null;
            if (tailStart < messages.Count)
            {
                summaryFirstKeptId = messages[tailStart].Id;
            }

            var summaryMessage = new AssistantMessage(
                Guid.NewGuid().ToString("N"),
                sessionId,
                DateTimeOffset.UtcNow,
                new[] { new TextPart(summary) },
                StopReason.Stop,
                new Usage(0, summaryTokens),
                summaryModel.Id,
                IsSummary: true,
                SummaryFirstKeptId: summaryFirstKeptId);

            return Result.Success(new CompactionResult(
                summary,
                tailStart,
                tokensSaved,
                stopwatch.Elapsed,
                summaryMessage));
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            // F17: cancellation is not a compaction failure. Treating Esc during
            // summarisation as a generic Exception made the caller flip the
            // session into destructive truncation fallback and report a spurious
            // error — the run is simply ending.
            stopwatch.Stop();
            logger.LogInformation(ex, "Compaction cancelled for session {SessionId}", sessionId);
            return Result.Failure<CompactionResult>("Compaction cancelled.");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            logger.LogError(ex, "Compaction failed for session {SessionId}", sessionId);
            return Result.Failure<CompactionResult>($"Compaction failed: {ex.Message}");
        }
    }

    /// <summary>
    ///     Returns the index at which the tail begins (head = messages[0..tailStart], tail = messages[tailStart..]).
    ///     Returning an index (instead of two List slices) eliminates two List allocations per compaction.
    /// </summary>
    private int FindCutPoint(
        IReadOnlyList<AgentMessage> messages,
        int keepRecentTokens,
        int tailTurns)
    {
        int tailTokens = 0;
        int tailStart = messages.Count;

        for (int i = messages.Count - 1; i >= 0; i--)
        {
            int msgTokens = tokenTracker.EstimateMessage(messages[i]);
            if (tailTokens + msgTokens > keepRecentTokens)
            {
                break;
            }

            // Don't cut in the middle of a turn (tool_call ↔ tool_result pair)
            if (messages[i] is ToolResultMessage)
            {
                continue;
            }

            tailTokens += msgTokens;
            tailStart = i;
        }

        // Enforce tail_turns minimum
        int minTailStart = messages.Count - tailTurns * 4;
        if (minTailStart < tailStart)
        {
            tailStart = Math.Max(0, minTailStart);
        }

        return tailStart;
    }

    private static string BuildSummarizationPrompt(IReadOnlyList<AgentMessage> messages, int count)
    {
        using var sb = StringBuilderPool.Rent(4096);
        var builder = sb.Builder;
        builder.AppendLine("Summarize the following conversation, preserving all important details:");
        builder.AppendLine();
        builder.AppendLine("<conversation>");
        for (int i = 0; i < count; i++)
        {
            var msg = messages[i];
            builder.Append('[').Append(msg.Role).Append("] ");
            // Append the formatted message body inline to avoid the intermediate string
            // that the previous `AppendLine(FormatMessage(msg))` produced.
            AppendFormattedMessage(builder, msg);
            builder.AppendLine();
        }
        builder.AppendLine("</conversation>");
        return builder.ToString();
    }

    private static void AppendFormattedMessage(StringBuilder builder, AgentMessage msg)
    {
        switch (msg)
        {
            case UserMessage u:
                builder.Append(u.Content);
                break;
            case AssistantMessage a:
            {
                var parts = a.Parts;
                for (int i = 0; i < parts.Count; i++)
                {
                    if (i > 0) builder.Append('\n');
                    AppendFormattedPart(builder, parts[i]);
                }
                break;
            }
            case ToolResultMessage tr:
            {
                var results = tr.Results;
                for (int i = 0; i < results.Count; i++)
                {
                    if (i > 0) builder.Append('\n');
                    var r = results[i];
                    builder.Append("[tool:").Append(r.ToolName).Append("] ").Append(r.Output);
                }
                break;
            }
            default:
                builder.Append(msg.ToString() ?? string.Empty);
                break;
        }
    }

    private static void AppendFormattedPart(StringBuilder builder, ContentPart part)
    {
        switch (part)
        {
            case TextPart t:
                builder.Append(t.Text);
                break;
            case ThinkingPart th:
                builder.Append("[thinking] ").Append(th.Text);
                break;
            case ToolCallPart tc:
                // GetRawText() allocates a string each call; this is the only call site in
                // the formatter, so the cost is one allocation per tool-call part per
                // summarization — acceptable for compaction (runs rarely).
                builder.Append("[tool_call:").Append(tc.ToolName).Append("] ").Append(tc.Args.GetRawText());
                break;
        }
    }
}
