using Harbor.Abstractions.Models;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using System.Diagnostics;
using System.Text;
using Harbor.Abstractions.Extensions;
using Harbor.Application.Resilience;
using Microsoft.Extensions.Logging;
namespace Harbor.Application.Sessions;
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
///     secondary config can never break compaction itself. An invalid
///     configured value (unparseable reference) is logged at Warning at
///     construction (#1582) — the feature degrades the same way, but the
///     config typo is visible.
/// </remarks>
public sealed class CompactionService(
    ITokenTracker tokenTracker,
    IProviderRegistry providers,
    ILogger<CompactionService> logger,
    string? secondaryModel = null,
    IRetryPolicy? retryPolicy = null) : ICompactionService
{
    // #1106 (no-retry summary): the summarization stream rides the same retry
    // budget as the main-loop turn stream (TurnRunner.StreamRetryOptions) —
    // general transients on a seconds budget, 429s on a separate minutes-scale
    // budget honoring server Retry-After (#270). Without it any transient 429
    // during summarization fell straight through to destructive truncation,
    // while the main loop would have weathered the same storm.
    private static readonly RetryOptions SummaryRetryOptions = new(
        MaxAttempts: 3,
        BaseDelay: TimeSpan.FromSeconds(1),
        UseJitter: true,
        MaxRateLimitAttempts: 10,
        RateLimitBaseDelay: TimeSpan.FromSeconds(5),
        MaxRetryAfter: TimeSpan.FromMinutes(5));

    /// <summary>
    ///     A successfully resolved secondary (cheap) summarization client+model pair.
    /// </summary>
    private sealed record ResolvedSecondary(ILlmClient Client, ModelInfo Model);

    // Ф8/A3: lazily resolved secondary client; successes are cached for the
    // service lifetime, failures are NOT cached (a transient provider outage
    // must not pin the fallback forever). Reference writes are atomic, so two
    // concurrent first calls may both resolve once (benign and idempotent),
    // while every later call reads the cached pair without locking.
    private readonly ModelRef? _secondaryRef = ParseSecondary(secondaryModel, logger);

    /// <summary>
    ///     Parse the configured reference; an invalid value logs the reason at
    ///     Warning and disables the feature. #1582 (ROP corridor, §ROP-001/§ROP-002
    ///     precedent): the old
    ///     <c>parsed.IsSuccess ? parsed.Value : null</c> threw the
    ///     <see cref="ModelRef.TryParse" /> error away — a typo in the
    ///     secondary-model config silently switched fallback summarization off
    ///     with zero signal in logs or diagnostics. The raw invalid value and the
    ///     parse reason now surface via
    ///     <see cref="CompactionServiceLog.SecondaryModelInvalid" />,
    ///     the same observability the resolution path gets from
    ///     <see cref="LogSecondaryFallback" />. Silence stays legitimate only for
    ///     a value that was never configured (null/blank) — feature off, not broken.
    /// </summary>
    private static ModelRef? ParseSecondary(string? value, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        Result<ModelRef> parsed = ModelRef.TryParse(value);
        if (parsed.IsFailure)
        {
            CompactionServiceLog.SecondaryModelInvalid(logger, value, parsed.Error);
            return null;
        }

        return parsed.Value;
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
        CompactionServiceLog.SecondaryModelUnresolved(logger, _secondaryRef, primaryModel.Id);
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
    ///     Output slice reserved for the summary itself. The summarization
    ///     request caps output here (<see cref="BuildSummaryRequest" />), so
    ///     the prompt cap (<see cref="FindPromptStartByBudget" />) subtracts
    ///     the same constant — one source, not two literals that can drift.
    /// </summary>
    private const int SummaryMaxOutputTokens = 4096;
    private int _keepRecentTokens = 20000;
    private int _tailTurns = 2;

    /// <summary>
    ///     Target token count for the kept tail when compacting.
    ///     Must be &gt; 0 (mirrors <c>CompactionConfig.Validate</c>).
    /// </summary>
    public int KeepRecentTokens
    {
        get => _keepRecentTokens;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, 0);
            _keepRecentTokens = value;
        }
    }

    /// <summary>
    ///     Minimum number of recent turns to keep verbatim after compaction.
    ///     Zero disables the floor (existing test/loop usage); negative is refused.
    /// </summary>
    public int TailTurns
    {
        get => _tailTurns;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _tailTurns = value;
        }
    }

    // #472: the history-shaping policy is NOT here any more. TruncateToFit,
    // TruncateToFitStrict and MaterializeCompactedView moved to CompactionPolicy
    // because the two callers that use them — src/Harbor.AgentRuntime/Agents/TurnRunner.cs:144 and
    // src/Harbor.AgentRuntime/Agents/Pipeline/CompactionBehavior.cs:114 — are not ICompactionService consumers: they reached
    // into this DI-registered implementation for its statics. CompactionBehavior
    // even holds an ICompactionService and still called the static. What remains in
    // this class is the summarization itself, and its rules are graded by
    // CompactionPolicyInvariantTests.

    /// <inheritdoc />
    /// <remarks>
    ///     #1106 (double estimate, divergent reserves): the budget verdict is
    ///     the tracker's O(1) cached decision, not a second O(n) scan past it.
    ///     The previous body ran its own <c>EstimateTokens</c> full-history
    ///     scan here, so a turn that had already asked
    ///     <see cref="ITokenTracker.ShouldCompact" /> paid the estimate twice.
    ///     The reserve that applies is the tracker's alone — this class keeps
    ///     no second threshold, so the two can never diverge again.
    /// </remarks>
    public bool ShouldCompact(IReadOnlyList<AgentMessage> messages, ModelInfo model)
    {
        return tokenTracker.ShouldCompact(messages, model);
    }

    /// <inheritdoc />
    public async Task<Result<CompactionResult>> CompactAsync(
        string sessionId,
        IReadOnlyList<AgentMessage> messages,
        ModelInfo model,
        CancellationToken ct = default)
    {
        try
        {
            // 1. Find cut point (index-based; no List allocations)
            int tailStart = FindCutPoint(messages, model, KeepRecentTokens, TailTurns);

            if (tailStart == 0)
            {
                return Result.Failure<CompactionResult>("No messages to compact.");
            }

            // 2. Build summarization request — name parse → registry lookup ride
            // one Bind chain (ROP-B П.12 pattern); a passthrough ladder here would
            // just re-raise each Error verbatim.
            return await ProviderId.TryCreate(model.ProviderId)
                .Bind(providers.GetClient)
                .Bind(client => CompactCoreAsync(sessionId, messages, model, client, tailStart, ct))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CompactionServiceLog.CompactionExecuteFailed(logger, ex, sessionId);
            return Result.Failure<CompactionResult>($"Compaction failed: {ex.Message}");
        }
    }

    /// <summary>Secondary-model selection + summarization call + tail splice.</summary>
    private async Task<Result<CompactionResult>> CompactCoreAsync(
        string sessionId,
        IReadOnlyList<AgentMessage> messages,
        ModelInfo model,
        ILlmClient client,
        int tailStart,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        try
            {
            (ILlmClient summaryClient, ModelInfo summaryModel) =
                await ResolveSummaryTargetAsync(client, model, ct).ConfigureAwait(false);

            string prompt = BuildSummarizationPrompt(messages, FindPromptStartByBudget(messages, tailStart, summaryModel), tailStart);
            LlmRequest request = BuildSummaryRequest(summaryModel, prompt);

            Result<string> summaryOutcome =
                await CollectSummaryAsync(summaryClient, request, ct).ConfigureAwait(false);
            if (summaryOutcome.IsFailure)
                return summaryOutcome.ConvertFailure<CompactionResult>();

            stopwatch.Stop();

            string summary = summaryOutcome.Value;

            // F19: an empty summary (content filter, silent provider) used to be
            // accepted as success — the anchor would then discard the ENTIRE
            // compressed history and the model silently lost all memory of it.
            if (summary.Length == 0)
            {
                CompactionServiceLog.EmptySummaryRefused(logger, sessionId);
                return Result.Failure<CompactionResult>("Compaction produced an empty summary.");
            }

            int summaryTokens = tokenTracker.Estimate(summary);
            int tokensSaved = ComputeTokensSaved(messages, tailStart, summaryTokens);

            // 5. Capture first kept (tail) message id (if any) without allocating a Skip().FirstOrDefault().
            string? summaryFirstKeptId = null;
            if (tailStart < messages.Count)
            {
                summaryFirstKeptId = messages[tailStart].Id;
            }

            var summaryMessage = CreateSummaryMessage(
                Guid.NewGuid().ToString("N"),
                sessionId,
                summary,
                summaryTokens,
                summaryModel.Id,
                summaryFirstKeptId);

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
            CompactionServiceLog.CompactionCancelled(logger, ex, sessionId);
            return Result.Failure<CompactionResult>("Compaction cancelled.");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            CompactionServiceLog.CompactionRunFailed(logger, ex, sessionId);
            return Result.Failure<CompactionResult>($"Compaction failed: {ex.Message}");
        }
    }

    /// <summary>
    ///     Prefer the configured cheap secondary model for the summarization
    ///     call; fall back to the primary client/model when no secondary is
    ///     configured or it cannot be resolved.
    /// </summary>
    private async Task<(ILlmClient Client, ModelInfo Model)> ResolveSummaryTargetAsync(
        ILlmClient client,
        ModelInfo model,
        CancellationToken ct)
    {
        // Ф8/A3: prefer the configured cheap secondary model for the
        // summarization call; fall back to the primary client/model when
        // no secondary is configured or it cannot be resolved.
        var secondary = await TryResolveSecondaryAsync(model, ct).ConfigureAwait(false);
        if (secondary is not null)
            return (secondary.Client, secondary.Model);

        return (client, model);
    }

    /// <summary>Builds the summarization LLM request for the given prompt.</summary>
    /// <remarks>
    ///     #1106 row 6: <see cref="SummaryMaxOutputTokens" /> is the same
    ///     constant <see cref="FindPromptStartByBudget" /> subtracts, so the
    ///     prompt cap and the request agree on the output slice by construction.
    /// </remarks>
    private static LlmRequest BuildSummaryRequest(ModelInfo summaryModel, string prompt)
    {
        // Ф8/A1: the summarization system prompt is a compile-time constant, so the
        // request is a perfect prefix-cache candidate — flag it Ephemeral.
        return LlmRequestBuilder.ForSummary(
            summaryModel.Id,
            prompt,
            SummarizationPrompt,
            SummaryMaxOutputTokens).Build();
    }

    /// <summary>
    ///     Streams the summarization call, collecting full text into a pooled builder.
    ///     Transient provider failures (429/5xx, network blips, timeouts) are
    ///     retried through the injected <see cref="IRetryPolicy" /> on the
    ///     main-loop budget (#1106); fatal failures return immediately.
    ///     A null policy (tests, manual construction) keeps the legacy
    ///     single-attempt behaviour.
    /// </summary>
    private async Task<Result<string>> CollectSummaryAsync(
        ILlmClient summaryClient,
        LlmRequest request,
        CancellationToken ct)
    {
        if (retryPolicy is null)
        {
            return await StreamSummaryOnceAsync(summaryClient, request, ct).ConfigureAwait(false);
        }

        try
        {
            return await retryPolicy.ExecuteAsync(
                attemptCt => StreamSummaryOnceAsync(summaryClient, request, attemptCt),
                SummaryRetryOptions,
                (ex, attempt) => CompactionServiceLog.SummaryTransientRetry(logger, attempt, ex.Message),
                ct).ConfigureAwait(false);
        }
        catch (LlmStreamErrorException lex)
        {
            // Transient budget exhausted (fatal kinds never throw — they return
            // Failure from the attempt itself): same failure shape as the
            // single-attempt path, so the caller still falls back to truncation
            // instead of crashing.
            return Result.Failure<string>($"LLM error during compaction: {lex.Message}");
        }
    }

    /// <summary>One summarization streaming attempt: transient error events throw so the retry policy can replay them.</summary>
    private static async Task<Result<string>> StreamSummaryOnceAsync(
        ILlmClient summaryClient,
        LlmRequest request,
        CancellationToken ct)
    {
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
                // Main-loop parity (ROP-A ПР.5): the transport verdict rides on
                // the event — transient kinds throw so IRetryPolicy replays the
                // attempt, fatal kinds fail fast with no retry.
                if (ProviderErrors.IsTransient(err.Kind))
                {
                    throw new LlmStreamErrorException(err);
                }

                return Result.Failure<string>($"LLM error during compaction: {err.Message}");
            }
        }

        return Result.Success(summaryBuilder.ToString());
    }

    /// <summary>
    ///     Tokens saved by replacing the head slice with the summary.
    ///     Iterates the head slice directly without materializing a List.
    /// </summary>
    private int ComputeTokensSaved(IReadOnlyList<AgentMessage> messages, int tailStart, int summaryTokens)
    {
        // 4. Compute tokens saved — iterate head slice directly without materializing a List.
        int headTokens = 0;
        for (int i = 0; i < tailStart; i++)
        {
            headTokens += tokenTracker.EstimateMessage(messages[i]);
        }
        return headTokens - summaryTokens;
    }

    /// <summary>Builds the summary anchor message carrying the compacted history.</summary>
    private static AssistantMessage CreateSummaryMessage(
        string messageId,
        string sessionId,
        string summary,
        int summaryTokens,
        string modelId,
        string? summaryFirstKeptId)
    {
        return new AssistantMessage(
            messageId,
            sessionId,
            DateTimeOffset.UtcNow,
            new[] { new TextPart(summary) },
            StopReason.Stop,
            new Usage(0, summaryTokens),
            modelId,
            IsSummary: true,
            SummaryFirstKeptId: summaryFirstKeptId);
    }

    /// <summary>
    ///     Returns the index at which the tail begins (head = messages[0..tailStart], tail = messages[tailStart..]).
    ///     Returning an index (instead of two List slices) eliminates two List allocations per compaction.
    /// </summary>
    /// <remarks>
    ///     #1755: two rules the reactive overflow recovery depends on. The tail
    ///     budget is capped by what the model's context window can hold — a
    ///     compaction that keeps a tail the window cannot take frees nothing,
    ///     and the retry after an overflow would overflow again. And the cut
    ///     never opens the tail on an orphan tool result, so the request the
    ///     retry sends is a valid transcript.
    /// </remarks>
    private int FindCutPoint(
        IReadOnlyList<AgentMessage> messages,
        ModelInfo model,
        int keepRecentTokens,
        int tailTurns)
    {
        int tailStart = FindTailStartByBudget(messages, ClampTailBudget(model, keepRecentTokens));
        tailStart = ApplyTailTurnsMinimum(messages, tailStart, tailTurns);
        return SkipOrphanToolResults(messages, tailStart);
    }

    /// <summary>
    ///     The tail budget, capped by the model's usable context (#1755).
    /// </summary>
    /// <remarks>
    ///     The configured <see cref="KeepRecentTokens" /> is a fixed number,
    ///     but the tail has to FIT the model the request goes to: the 20k
    ///     default overflows a 16k model on its own, and a compaction that
    ///     frees nothing cannot fix an overflow. The cap is the model's usable
    ///     budget — the window minus the reserve and the output slice — so the
    ///     compacted view is smaller than the window by construction.
    /// </remarks>
    /// <param name="model">The model the request goes to.</param>
    /// <param name="keepRecentTokens">The configured tail budget.</param>
    /// <returns>The tail budget to compact with.</returns>
    private int ClampTailBudget(ModelInfo model, int keepRecentTokens)
    {
        int budget = model.ContextWindow - ReserveTokens - model.MaxOutputTokens;
        if (budget <= 0)
        {
            // The window is smaller than the reserve — the tracker's own guard
            // refuses to compact such a model at all. Leave the configured
            // budget alone rather than inventing a negative one.
            return keepRecentTokens;
        }

        return Math.Min(keepRecentTokens, budget);
    }

    /// <summary>
    ///     The token reserve the tracker's own compaction verdict uses — the
    ///     single source (#1106: two reserves drifted once already).
    /// </summary>
    private int ReserveTokens =>
        (tokenTracker as TokenTracker)?.ReserveTokens ?? CompactionPolicy.DefaultReserveTokens;

    /// <summary>
    ///     The cut point never opens the kept tail on an orphan tool result
    ///     (#1755).
    /// </summary>
    /// <remarks>
    ///     The tail-turns floor can move the cut BACKWARD, onto a tool result
    ///     whose tool_use is in the pruned head. The kept slice would then
    ///     carry a tool_result with no tool_use before it, and providers reject
    ///     that pair outright — the request the reactive retry sends would be
    ///     refused for a reason that has nothing to do with the context
    ///     window. The cut moves FORWARD past the orphan run instead: the
    ///     results and their calls then both sit in the pruned head, so the
    ///     summary covers the pair and the kept slice stays self-consistent.
    /// </remarks>
    /// <param name="messages">The history being compacted.</param>
    /// <param name="tailStart">The cut point to adjust.</param>
    /// <returns>The adjusted cut point.</returns>
    private static int SkipOrphanToolResults(IReadOnlyList<AgentMessage> messages, int tailStart)
    {
        int count = messages.Count;
        while (tailStart < count && messages[tailStart] is ToolResultMessage)
        {
            tailStart++;
        }

        return tailStart;
    }

    /// <summary>
    ///     Walk backwards accumulating the newest messages until the token
    ///     budget is hit. Never cuts in the middle of a turn
    ///     (tool_call ↔ tool_result pair).
    /// </summary>
    private int FindTailStartByBudget(
        IReadOnlyList<AgentMessage> messages,
        int keepRecentTokens)
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

            // The tool result is KEPT (its tool_call is in the tail too), so
            // its tokens count against the budget — but the cut never lands on
            // it: the tail starts at the call, so the pair stays together.
            tailTokens += msgTokens;

            if (messages[i] is ToolResultMessage)
            {
                continue;
            }

            tailStart = i;
        }

        return tailStart;
    }

    /// <summary>Enforces the tail-turns minimum over a budget-computed cut point.</summary>
    private static int ApplyTailTurnsMinimum(
        IReadOnlyList<AgentMessage> messages,
        int tailStart,
        int tailTurns)
    {
        // Enforce tail_turns minimum
        int minTailStart = messages.Count - tailTurns * 4;
        if (minTailStart < tailStart)
        {
            tailStart = Math.Max(0, minTailStart);
        }

        return tailStart;
    }

    /// <summary>
    ///     First head index that fits the summary call: the prompt pastes the
    ///     head back into a model with <c>ContextWindow</c> input tokens, so an
    ///     uncapped paste re-overfills the very window compaction is freeing
    ///     (#1106 row 6). Walks back from the tail keeping the newest head
    ///     messages inside <c>ContextWindow − output − reserve</c>; at least
    ///     the newest head message is always kept.
    /// </summary>
    /// <remarks>
    ///     #1106 (divergent reserves): the subtracted reserve is the tracker's
    ///     own <see cref="TokenTracker.ReserveTokens" /> — the same value that
    ///     drives the per-turn <see cref="ShouldCompact" /> verdict. No second
    ///     threshold lives on this class any more, so the two cannot drift.
    /// </remarks>
    private int FindPromptStartByBudget(
        IReadOnlyList<AgentMessage> messages,
        int tailStart,
        ModelInfo summaryModel)
    {
        // Single reserve source: the tracker owns the threshold. CompactionService
        // kept its own (now deleted) copy that drifted from this one; the tracker's
        // is the only one that applies (#1106 divergent reserves).
        int reserveTokens = ReserveTokens;
        int budget = summaryModel.ContextWindow - SummaryMaxOutputTokens - reserveTokens;
        int start = tailStart - 1;
        if (budget <= 0 || start < 0)
        {
            return Math.Max(0, start);
        }

        int promptTokens = 0;
        for (int i = tailStart - 1; i >= 0; i--)
        {
            int msgTokens = tokenTracker.EstimateMessage(messages[i]);
            if (promptTokens + msgTokens > budget)
            {
                break;
            }

            promptTokens += msgTokens;
            start = i;
        }

        return start;
    }

    /// <summary>
    ///     Renders the head slice <c>messages[start..count]</c> into the
    ///     summarization prompt. A non-zero <paramref name="start" /> means the
    ///     oldest head messages were dropped by the prompt cap, so a marker
    ///     says so instead of letting the summary read as the whole history.
    /// </summary>
    private static string BuildSummarizationPrompt(IReadOnlyList<AgentMessage> messages, int start, int count)
    {
        using var sb = StringBuilderPool.Rent(4096);
        var builder = sb.Builder;
        builder.AppendLine("Summarize the following conversation, preserving all important details:");
        builder.AppendLine();
        builder.AppendLine("<conversation>");
        if (start > 0)
        {
            builder.AppendLine("[... earlier history omitted to fit context ...]");
        }
        for (int i = start; i < count; i++)
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

    /// <summary>
    ///     Renders one message into the summarization prompt. #461: the per-kind
    ///     dispatch lives in <see cref="FormattedMessageVisitor" />; an unknown
    ///     role now throws instead of being stringified by a <c>default:</c> arm.
    /// </summary>
    private static void AppendFormattedMessage(StringBuilder builder, AgentMessage msg) =>
        new FormattedMessageVisitor(builder).Accept(msg);

    /// <summary>
    ///     Message-level arm of the summarization formatter. Byte-for-byte the
    ///     shape the previous switch produced, separators included.
    /// </summary>
    private sealed class FormattedMessageVisitor(StringBuilder builder)
        : AgentMessageVisitor<FormattedMessageVisitor>
    {
        private readonly FormattedPartVisitor _parts = new(builder);

        public override FormattedMessageVisitor Visit(UserMessage message)
        {
            builder.Append(message.Content);
            return this;
        }

        public override FormattedMessageVisitor Visit(AssistantMessage message)
        {
            var parts = message.Parts;
            for (int i = 0; i < parts.Count; i++)
            {
                if (i > 0) builder.Append('\n');
                _parts.Accept(parts[i]);
            }

            return this;
        }

        public override FormattedMessageVisitor Visit(ToolResultMessage message)
        {
            var results = message.Results;
            for (int i = 0; i < results.Count; i++)
            {
                if (i > 0) builder.Append('\n');
                var r = results[i];
                builder.Append("[tool:").Append(r.ToolName).Append("] ").Append(r.Output);
            }

            return this;
        }
    }

    /// <summary>
    ///     Part-level arm of the summarization formatter (#461). Each part kind is
    ///     an explicit decision rather than a <c>switch</c> arm that a new
    ///     <see cref="ContentPart" /> subtype would fall straight through.
    /// </summary>
    private sealed class FormattedPartVisitor(StringBuilder builder)
        : ContentPartVisitor<FormattedPartVisitor>
    {
        public override FormattedPartVisitor Visit(TextPart part)
        {
            builder.Append(part.Text);
            return this;
        }

        public override FormattedPartVisitor Visit(ThinkingPart part)
        {
            builder.Append("[thinking] ").Append(part.Text);
            return this;
        }

        public override FormattedPartVisitor Visit(ToolCallPart part)
        {
            // GetRawText() allocates a string each call; this is the only call site in
            // the formatter, so the cost is one allocation per tool-call part per
            // summarization — acceptable for compaction (runs rarely).
            builder.Append("[tool_call:").Append(part.ToolName).Append("] ").Append(part.Args.GetRawText());
            return this;
        }

        /// <summary>
        ///     File parts carry no summarizable prose — the path and MIME type are
        ///     noise in a conversation summary. Explicitly a no-op so a future
        ///     subtype can never be mistaken for this one.
        /// </summary>
        public override FormattedPartVisitor Visit(FilePart part) => this;
    }
}

/// <summary>
///     SG2: BCL <c>[LoggerMessage]</c> delegates for <see cref="CompactionService" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class CompactionServiceLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Secondary compaction model '{Secondary}' could not be resolved; falling back to primary model '{Primary}'")]
    public static partial void SecondaryModelUnresolved(ILogger logger, ModelRef? secondary, string primary);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Compaction failed for session {SessionId}")]
    public static partial void CompactionExecuteFailed(ILogger logger, Exception ex, string sessionId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Summarization produced an empty summary for session {SessionId}; refusing to persist an empty anchor")]
    public static partial void EmptySummaryRefused(ILogger logger, string sessionId);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Compaction cancelled for session {SessionId}")]
    public static partial void CompactionCancelled(ILogger logger, Exception ex, string sessionId);

    [LoggerMessage(EventId = 5, Level = LogLevel.Error, Message = "Compaction failed for session {SessionId}")]
    public static partial void CompactionRunFailed(ILogger logger, Exception ex, string sessionId);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information, Message = "Summarization attempt {Attempt} failed transiently ({Message}); retrying")]
    public static partial void SummaryTransientRetry(ILogger logger, int attempt, string message);

    [LoggerMessage(EventId = 7, Level = LogLevel.Warning, Message = "Configured secondary compaction model '{Secondary}' is invalid ({Error}); fallback summarization stays on the primary model")]
    public static partial void SecondaryModelInvalid(ILogger logger, string secondary, string error);

}
