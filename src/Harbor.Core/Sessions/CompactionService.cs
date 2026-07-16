using System.Diagnostics;
using Harbor.Abstractions.Extensions;
using Microsoft.Extensions.Logging;
namespace Harbor.Core.Sessions;
/// <summary>
///     Default compaction service using anchored-summary strategy.
///     Generates a structured Markdown summary of compacted messages.
///     Performance: pooled StringBuilder, index-based cut-point (no List allocations),
///     pooled buffers for serializing intermediate message text.
/// </summary>
public sealed class CompactionService : ICompactionService
{
    private const string SummarizationPrompt = """
                                               You are creating a summary of the conversation so far to provide context to a teammate who is taking over the task.

                                               The summary should preserve ALL important information needed to continue the work, including:
                                               - The original goal and current state
                                               - Decisions made and their rationale
                                               - Files read and modified (with paths)
                                               - Commands run and their outcomes
                                               - Errors encountered and how they were resolved
                                               - Outstanding questions or blockers

                                               Output the summary in this exact Markdown structure:

                                               ## Goal
                                               [What the user is trying to accomplish]

                                               ## Constraints & Preferences
                                               [Any constraints, preferences, or rules discovered]

                                               ## Progress
                                               ### Done
                                               - [Completed tasks]

                                               ### In Progress
                                               - [Currently being worked on]

                                               ### Blocked
                                               - [Items blocked, with reason]

                                               ## Key Decisions
                                               - [Decision: rationale]

                                               ## Next Steps
                                               - [Immediate next actions]

                                               ## Critical Context
                                               [Any other information needed to continue]

                                               ## Files
                                               ### Read
                                               - `path/to/file`

                                               ### Modified
                                               - `path/to/file` — what was changed

                                               Rules:
                                               - Keep every section, even when empty (use "None" if no content).
                                               - Preserve exact file paths, commands, error strings, identifiers.
                                               - Do not mention the summary process or that context was compacted.
                                               - Be concise but complete — every detail matters.
                                               """;
    private readonly ILogger<CompactionService> _logger;
    private readonly IProviderRegistry _providers;

    private readonly ITokenEstimator _tokenEstimator;

    /// <summary>
    ///     Construct a <see cref="CompactionService" /> wired to the supplied services.
    /// </summary>
    /// <param name="tokenEstimator">The token estimator used to decide when to compact.</param>
    /// <param name="providers">The provider registry for invoking the summarization LLM call.</param>
    /// <param name="logger">The logger.</param>
    public CompactionService(
        ITokenEstimator tokenEstimator,
        IProviderRegistry providers,
        ILogger<CompactionService> logger)
    {
        _tokenEstimator = tokenEstimator;
        _providers = providers;
        _logger = logger;
    }

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

    /// <inheritdoc />
    public bool ShouldCompact(IReadOnlyList<AgentMessage> messages, ModelInfo model)
    {
        int estimated = _tokenEstimator.EstimateMessages(messages);
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

            var clientResult = _providers.GetClient(providerIdResult.Value);
            if (clientResult.IsFailure)
            {
                return Result.Failure<CompactionResult>(clientResult.Error);
            }

            string prompt = BuildSummarizationPrompt(messages, tailStart);
            var request = new LlmRequest(
                model.Id,
                new[] { LlmUserMessage.Text(prompt) },
                SummarizationPrompt,
                Array.Empty<ToolDefinition>(),
                Temperature: 0.3m,
                MaxOutputTokens: 4096);

            // 3. Stream LLM (collect full text into pooled StringBuilder)
            using var summaryBuilder = StringBuilderPool.Rent(4096);
            await foreach (var evt in clientResult.Value.StreamAsync(request, ct).ConfigureAwait(false))
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

            // 4. Compute tokens saved — iterate head slice directly without materializing a List.
            int headTokens = 0;
            for (int i = 0; i < tailStart; i++)
            {
                headTokens += _tokenEstimator.EstimateMessage(messages[i]);
            }
            int summaryTokens = _tokenEstimator.Estimate(summary);
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
                model.Id,
                IsSummary: true,
                SummaryFirstKeptId: summaryFirstKeptId);

            return Result.Success(new CompactionResult(
                summary,
                tailStart,
                tokensSaved,
                stopwatch.Elapsed,
                summaryMessage));
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Compaction failed for session {SessionId}", sessionId);
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
            int msgTokens = _tokenEstimator.EstimateMessage(messages[i]);
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
            builder.Append('[').Append(msg.Role).Append("] ").AppendLine(FormatMessage(msg));
        }
        builder.AppendLine("</conversation>");
        return builder.ToString();
    }

    private static string FormatMessage(AgentMessage msg) => msg switch
    {
        UserMessage u => u.Content,
        AssistantMessage a => a.Parts.Select(FormatPart).JoinToString("\n"),
        ToolResultMessage tr => tr.Results.Select(r => $"[tool:{r.ToolName}] {r.Output}").JoinToString("\n"),
        _ => msg.ToString() ?? string.Empty
    };

    private static string FormatPart(ContentPart part) => part switch
    {
        TextPart t => t.Text,
        ThinkingPart th => $"[thinking] {th.Text}",
        ToolCallPart tc => $"[tool_call:{tc.ToolName}] {tc.Args.GetRawText()}",
        _ => string.Empty
    };
}
