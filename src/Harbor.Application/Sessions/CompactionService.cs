using Harbor.Abstractions.Models;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Sessions;
using System.Diagnostics;
using System.Text;
using Harbor.Abstractions.Extensions;
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
    public int ReserveTokens { get; set; } = CompactionPolicy.DefaultReserveTokens;

    /// <summary>
    ///     Target token count for the kept tail when compacting.
    /// </summary>
    public int KeepRecentTokens { get; set; } = 20000;

    /// <summary>
    ///     Minimum number of recent turns to keep verbatim after compaction.
    /// </summary>
    public int TailTurns { get; set; } = 2;

    // #472: the history-shaping policy is NOT here any more. TruncateToFit,
    // TruncateToFitStrict and MaterializeCompactedView moved to CompactionPolicy
    // because the two callers that use them — TurnRunner.cs:136 and
    // CompactionBehavior.cs:78 — are not ICompactionService consumers: they reached
    // into this DI-registered implementation for its statics. CompactionBehavior
    // even holds an ICompactionService and still called the static. What remains in
    // this class is the summarization itself, and its rules are graded by
    // CompactionPolicyInvariantTests.

    /// <inheritdoc />
    public bool ShouldCompact(IReadOnlyList<AgentMessage> messages, ModelInfo model)
    {
        return IsOverBudget(messages, model, ReserveTokens);
    }

    /// <summary>
    ///     Budget check behind <see cref="ShouldCompact" />: the estimated
    ///     history exceeds the model's context window minus the safety reserve.
    ///     A negative threshold (reserve larger than the window) trips on any content.
    /// </summary>
    private bool IsOverBudget(IReadOnlyList<AgentMessage> messages, ModelInfo model, int reserveTokens)
    {
        int estimated = tokenTracker.EstimateTokens(messages);
        return estimated > model.ContextWindow - reserveTokens;
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
            int tailStart = FindCutPoint(messages, KeepRecentTokens, TailTurns);

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
            logger.LogError(ex, "Compaction failed for session {SessionId}", sessionId);
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

            string prompt = BuildSummarizationPrompt(messages, tailStart);
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
                logger.LogWarning(
                    "Summarization produced an empty summary for session {SessionId}; refusing to persist an empty anchor",
                    sessionId);
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
    private static LlmRequest BuildSummaryRequest(ModelInfo summaryModel, string prompt)
    {
        // Ф8/A1: the summarization system prompt is a compile-time constant, so the
        // request is a perfect prefix-cache candidate — flag it Ephemeral.
        return new LlmRequest(
            summaryModel.Id,
            new[] { LlmUserMessage.Text(prompt) },
            SummarizationPrompt,
            Array.Empty<ToolDefinition>(),
            Temperature: 0.3m,
            MaxOutputTokens: 4096,
            CacheStrategy: CacheStrategy.Ephemeral);
    }

    /// <summary>Streams the summarization call, collecting full text into a pooled builder.</summary>
    private static async Task<Result<string>> CollectSummaryAsync(
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
    private int FindCutPoint(
        IReadOnlyList<AgentMessage> messages,
        int keepRecentTokens,
        int tailTurns)
    {
        int tailStart = FindTailStartByBudget(messages, keepRecentTokens);
        return ApplyTailTurnsMinimum(messages, tailStart, tailTurns);
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

            // Don't cut in the middle of a turn (tool_call ↔ tool_result pair)
            if (messages[i] is ToolResultMessage)
            {
                continue;
            }

            tailTokens += msgTokens;
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
