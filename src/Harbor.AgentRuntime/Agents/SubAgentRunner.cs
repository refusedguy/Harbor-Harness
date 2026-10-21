using System.Threading.Channels;
using Harbor.Abstractions.Extensions;
using Harbor.Abstractions.Results;
using Harbor.Abstractions.Sessions;
using Microsoft.Extensions.Logging;
using Result = CSharpFunctionalExtensions.Result;

namespace Harbor.Application.Agents;

/// <summary>
///     Real sub-agent execution: spawns an isolated <see cref="Harbor.Abstractions.Models.Session" />
///     for the requested <see cref="AgentDefinition" />, drives <see cref="IAgentLoop.RunAsync" />
///     to completion with the parent's prompt as the only user turn, and returns the final
///     assistant text as a <see cref="SubAgentRunResult" /> (G4 follow-up: replaces the old
///     "not implemented" stub path in <c>TaskTool</c>).
/// </summary>
/// <remarks>
///     <para>
///         <b>Isolation.</b> The sub-run gets its own session record
///         (<c>ParentSessionId</c> points back at the caller's session), own message
///         history, own steering channel, and the sub-agent definition's permission set.
///         Nothing the sub-run writes lands in the parent's history — only the final
///         assistant text crosses back as tool output.
///     </para>
///     <para>
///         <b>Nesting guard.</b> A running sub-agent MUST NOT invoke <c>task</c> again.
///         Enforcement uses an <see cref="AsyncLocal{T}" /> depth counter, which flows down
///         the entire async call tree of the sub-run (loop → dispatcher → tools): any nested
///         <see cref="RunAsync" /> observes a non-zero depth and fails fast instead of
///         recursing (which, without this guard, could recurse unboundedly since the sub
///         registry still exposes the shared <c>task</c> tool).
///     </para>
/// </remarks>
public sealed class SubAgentRunner(
    ISessionStore store,
    IAgentLoop loop,
    ILogger<SubAgentRunner> logger) : ISubAgentRunner
{
    /// <summary>
    ///     Cap on how much of the sub-run's final answer is returned to the parent.
    ///     Everything beyond the cap is cut so one chatty sub-agent cannot blow the
    ///     parent's context window in a single tool result.
    /// </summary>
    internal const int MaxOutputChars = 32_000;

    /// <summary>Title prefix marker stored on spawned sessions.</summary>
    private const string TitlePrefix = "task";

    private static readonly AsyncLocal<int> Depth = new();

    /// <summary>
    ///     Per-parent-session gates serializing child-cost propagation ([UX6]
    ///     #266). Parallel sub-runs (detached <c>background=true</c> tasks)
    ///     share one <see cref="ISessionStore" /> whose stats/message updates
    ///     are read-modify-write: without the gate two completions can
    ///     interleave GetStats→AddUsage→UpdateStats and lose one delta.
    ///     Fixed stripes (#1105), not per-parent entries: the old per-session-id
    ///     dictionary grew forever — one entry per parent, never removed
    ///     (removal would race waiters).
    /// </summary>
    private const int ParentCostStripes = 16;
    private static readonly SemaphoreSlim[] ParentCostGates = CreateParentCostGates();

    private static SemaphoreSlim[] CreateParentCostGates()
    {
        var gates = new SemaphoreSlim[ParentCostStripes];
        for (int i = 0; i < gates.Length; i++)
            gates[i] = new SemaphoreSlim(1, 1);
        return gates;
    }

    private static SemaphoreSlim GateFor(string parentSessionId) =>
        ParentCostGates[(uint)parentSessionId.GetHashCode() % (uint)ParentCostGates.Length];

    /// <summary>
    ///     Shared serialization for one session's stats read-modify-write
    ///     (#1105): the turn path (<c>DefaultSessionContext.UpdateStatsAsync</c>)
    ///     and child-cost propagation both fold through
    ///     <c>GetStatsAsync → AddUsage → UpdateStatsAsync</c> — without a
    ///     common gate a turn debit landing mid-propagation is silently lost.
    /// </summary>
    internal static SemaphoreSlim StatsGateFor(string sessionId) => GateFor(sessionId);

    /// <inheritdoc />
    public bool CanSpawn => Depth.Value == 0;

    /// <inheritdoc />
    public async Task<Result<SubAgentRunResult>> RunAsync(
        AgentDefinition agent,
        SubAgentRunRequest request,
        CancellationToken ct = default)
    {
        Ensure.NotNull(agent);
        Ensure.NotNull(request);

        if (!agent.IsSubAgent)
            return Result.Failure<SubAgentRunResult>(
                $"Agent '{agent.Name.Value}' is not a sub-agent (IsSubAgent=false).");
        if (!CanSpawn)
            return Result.Failure<SubAgentRunResult>(
                "Nesting limit reached: sub-agents cannot invoke 'task'. Finish your work with the available tools.");

        // The increment lives INSIDE the try: if the core throws
        // synchronously (before its first await), the finally still restores
        // the depth. Otherwise a single sync throw poisons CanSpawn for the
        // whole session and every later 'task' call dies with the nesting
        // error even on the top-level agent.
        try
        {
            Depth.Value++;
            return await RunCoreAsync(agent, request, ct).ConfigureAwait(false);
        }
        finally
        {
            Depth.Value--;
        }
    }

    /// <summary>
    ///     Resolve the spawned session's working directory (#1105): an explicit
    ///     request wins; otherwise inherit the PARENT session's directory, so
    ///     the child starts where its caller works instead of wherever the
    ///     process ambient directory happens to point. The process directory
    ///     is only the last resort (no parent, or the parent is unreadable).
    /// </summary>
    private async Task<string> ResolveChildDirectoryAsync(SubAgentRunRequest request, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
            return request.WorkingDirectory!;
        if (string.IsNullOrEmpty(request.ParentSessionId))
            return Environment.CurrentDirectory;
        var parent = await store.GetAsync(request.ParentSessionId, ct).ConfigureAwait(false);
        if (parent.IsFailure)
            return Environment.CurrentDirectory;
        return string.IsNullOrWhiteSpace(parent.Value.Directory)
            ? Environment.CurrentDirectory
            : parent.Value.Directory;
    }

    private async Task<Result<SubAgentRunResult>> RunCoreAsync(
        AgentDefinition agent,
        SubAgentRunRequest request,
        CancellationToken ct)
    {
        var directory = await ResolveChildDirectoryAsync(request, ct).ConfigureAwait(false);
        var title = BuildTitle(agent, request.Prompt);

        var created = await store.CreateAsync(directory, agent.Name.Value, agent.ProviderId, agent.Model, ct)
            .ConfigureAwait(false);
        if (created.IsFailure) // §4.6-ok: single rail-step; store error already diagnostic.
            return Result.Failure<SubAgentRunResult>(
                $"Failed to create sub-agent session: {created.Error}");
        var session = created.Value with
        {
            ParentSessionId = request.ParentSessionId,
            Title = title,
            Kind = SessionKind.Subagent,
            Status = SessionStatus.Working
        };

        // Best-effort metadata write per the F14 policy: the run itself does not
        // depend on it, divergence only loses parent linkage, kind, status and
        // title in listings.
        var linked = await store.UpdateAsync(session, ct).ConfigureAwait(false);
        if (linked.IsFailure)
            SubAgentRunnerLog.PersistMetadataFailed(logger, session.Id, linked.Error);

        SubAgentRunnerLog.SubAgentStarting(logger, agent.Name.Value, session.Id, request.ParentSessionId ?? "-");

        var messages = await store.GetMessagesAsync(session.Id, ct).ConfigureAwait(false);
        if (messages.IsFailure) // §4.6-ok: fresh session should always be readable; treat total store breakage as terminal.
            return Result.Failure<SubAgentRunResult>(
                SubAgentFailureFormat.WithResumeTrailer(
                    $"Failed to load sub-agent session '{session.Id}': {messages.Error}",
                    session.Id));

        // Fresh unbounded steering channel mirrors DefaultAgent's shape. Nothing will
        // ever steer a sub-run — no external handle to it is published anywhere.
        var steering = Channel.CreateUnbounded<Harbor.Abstractions.Models.AgentMessage>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        // A sub-run has no UI attached to its own session, so its context is built
        // without an event bus: the live totals a session publishes exist for the
        // session someone is watching, and this one's cost reaches the parent
        // through PropagateChildCostAsync instead. The store record is still
        // written with the sub-agent's real rates (TurnRunner resolves them).
        var context = await DefaultSessionContext.CreateAsync(
            session, messages.Value, store, steering, eventBus: null, ct, logger)
            .ConfigureAwait(false);

        try
        {
            // Persist the parent's task as the only user turn of this isolated session
            // (mirrors DefaultAgent.PromptAsync, F14: memory + store in one step).
            var userMessage = new UserMessage(
                Guid.NewGuid().ToString("N"),
                session.Id,
                DateTimeOffset.UtcNow,
                request.Prompt,
                agent.Name.Value,
                agent.Model);
            // #1103: the parent's task is setup, not work — it persists with
            // None so a cancel racing the spawn cannot throw OCE past
            // MarkStatusAsync and leave the session Working forever. The
            // cancel is still honored: loop.RunAsync below observes it and
            // returns the cancelled failure, which marks the session Aborted.
            await context.AppendMessageAsync(userMessage, CancellationToken.None).ConfigureAwait(false);

            var run = await loop.RunAsync(context, agent, ct).ConfigureAwait(false);
            if (run.IsFailure)
            {
                // #407: everything below is post-run bookkeeping and rides
                // CancellationToken.None, not the run token — a cancel that
                // landed mid-run must not take the child's terminal status or
                // its spend debit down with it. A cancelled child is Stopped
                // (Aborted), never Error: nothing malfunctioned.
                bool wasCancelled = ct.IsCancellationRequested;
                SubAgentRunnerLog.RunEndedAbnormally(logger, agent.Name.Value, session.Id, run.Error);
                await MarkStatusAsync(
                    session,
                    wasCancelled ? SessionStatus.Aborted : SessionStatus.Error,
                    CancellationToken.None).ConfigureAwait(false);
                // [UX6] #266: tokens burned before the failure still count —
                // propagate the partial delta so the parent totals stay honest.
                // A failed aggregate degrades to zero HERE, at the call site:
                // the run already failed, and the aggregate must not mask that
                // error with a storage failure of its own.
                Usage failedUsage = (await TryAggregateChildUsageAsync(session.Id, CancellationToken.None).ConfigureAwait(false))
                    .Match(
                        usage => usage,
                        _ => new Usage(0, 0));
                await PropagateChildCostAsync(
                    request.ParentSessionId, request.ParentMessageId, session.Id, failedUsage, CancellationToken.None).ConfigureAwait(false);
                // #270: degrade — surface whatever the sub-run produced before
                // failing, so the parent receives partial output, not just a
                // session id to go look at.
                string partialNote = await TryExtractPartialNoteAsync(store, session.Id, CancellationToken.None).ConfigureAwait(false);
                if (wasCancelled)
                {
                    // [UX6] #266: the trailer is the resumable id — kept here
                    // too, the sub-session survived the cancel.
                    return Result.Failure<SubAgentRunResult>(
                        SubAgentFailureFormat.WithResumeTrailer(
                            $"Sub-agent '{agent.Name.Value}' was cancelled (session {session.Id}).{partialNote}",
                            session.Id));
                }

                // [UX6] #266: the trailer is the resumable id — the parent can
                // continue the same sub-chat instead of dying with the run.
                return Result.Failure<SubAgentRunResult>(
                    SubAgentFailureFormat.WithResumeTrailer(
                        $"Sub-agent '{agent.Name.Value}' failed: {run.Error}. Its partial history is preserved in session {session.Id}.{partialNote}",
                        session.Id));
            }

            // #407: post-run reads ride CancellationToken.None — a cancel
            // landing exactly on the finish line must not lose the report.
            var history = await store.GetMessagesAsync(session.Id, CancellationToken.None).ConfigureAwait(false);
            // Storage failure vs empty history are distinct diagnoses: the
            // store's own error travels verbatim, emptiness gets its own text.
            // Both carry the resume trailer — the sub-session exists either way.
            if (history.IsFailure)
                return Result.Failure<SubAgentRunResult>(
                    SubAgentFailureFormat.WithResumeTrailer(
                        $"Sub-agent '{agent.Name.Value}' history unreadable: {history.Error} (session {session.Id}).",
                        session.Id));
            if (history.Value.Count == 0)
                return Result.Failure<SubAgentRunResult>(
                    SubAgentFailureFormat.WithResumeTrailer(
                        $"Sub-agent '{agent.Name.Value}' finished without producing a final assistant message (session {session.Id}).",
                        session.Id));

            var finalOutput = ExtractFinalOutput(history.Value);
            if (string.IsNullOrWhiteSpace(finalOutput))
                return Result.Failure<SubAgentRunResult>(
                    SubAgentFailureFormat.WithResumeTrailer(
                        $"Sub-agent '{agent.Name.Value}' finished without producing a final assistant message (session {session.Id}).",
                        session.Id));

            SubAgentRunnerLog.SubAgentFinished(logger, agent.Name.Value, session.Id, history.Value.Count, finalOutput.Length);
            await MarkStatusAsync(session, SessionStatus.Done, CancellationToken.None).ConfigureAwait(false);

            // [UX6] #266: fold the child's token delta into the parent message
            // + stats (under the per-parent gate) and hand it back in the
            // envelope so the parent side can render the cost.
            Usage childUsage = AggregateUsage(history.Value);
            await PropagateChildCostAsync(
                request.ParentSessionId, request.ParentMessageId, session.Id, childUsage, CancellationToken.None).ConfigureAwait(false);

            return new SubAgentRunResult(session.Id, agent.Name.Value, finalOutput.TruncateWithDropCount(MaxOutputChars), history.Value.Count, childUsage);
        }
        finally
        {
            // #201: the producer always completes the writer — a failing run must
            // never hang a reader. The drain only uses TryRead today, but
            // completion is the hang-proof contract for any future blocking read.
            steering.Writer.TryComplete();
        }
    }

    /// <summary>
    ///     Best-effort terminal status stamp (same F14 policy as the metadata
    ///     write above): the run's outcome does not depend on it, divergence
    ///     only leaves a stale status dot in session listings.
    /// </summary>
    private async Task MarkStatusAsync(
        Harbor.Abstractions.Models.Session session,
        Harbor.Abstractions.Models.SessionStatus status,
        CancellationToken ct)
    {
        var marked = await store.UpdateAsync(
            session with { Status = status, UpdatedAt = DateTimeOffset.UtcNow }, ct).ConfigureAwait(false);
        if (marked.IsFailure)
            SubAgentRunnerLog.PersistStatusFailed(logger, session.Id, marked.Error);
    }

    /// <summary>
    ///     Cap on the degraded partial-output excerpt surfaced inside a failure
    ///     message (#270): enough for the parent to continue, small enough to
    ///     never blow its context. The full text stays in the sub-session.
    /// </summary>
    private const int PartialExcerptChars = 2000;

    /// <summary>
    ///     Best-effort degraded excerpt of what the sub-run produced before
    ///     failing (#270): the final assistant prose when present, otherwise an
    ///     empty note. Storage failures and cancellation propagate — only
    ///     diagnostics gathering is best-effort.
    /// </summary>
    private async Task<string> TryExtractPartialNoteAsync(ISessionStore store, string sessionId, CancellationToken ct)
    {
        try
        {
            var history = await store.GetMessagesAsync(sessionId, ct).ConfigureAwait(false);
            if (history.IsFailure)
                return string.Empty;
            string partial = ExtractFinalOutput(history.Value);
            if (string.IsNullOrWhiteSpace(partial))
                return string.Empty;
            string excerpt = partial.Length <= PartialExcerptChars
                ? partial
                : partial[..PartialExcerptChars] + "\n…[truncated]";
            return $" Partial output (degraded): {excerpt}";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            SubAgentRunnerLog.DegradedOutputExtractFailed(logger, ex, sessionId);
            return string.Empty;
        }
    }

    /// <summary>
    ///     Sum token usage over every assistant message of a child run ([UX6]
    ///     #266). Index loop, no LINQ — mirrors the allocation discipline of
    ///     the neighboring extractors.
    /// </summary>
    private static Usage AggregateUsage(IReadOnlyList<Harbor.Abstractions.Models.AgentMessage> messages)
    {
        int input = 0;
        int output = 0;
        int reasoning = 0;
        int cacheRead = 0;
        int cacheWrite = 0;
        bool hasReasoning = false;
        bool hasCacheRead = false;
        bool hasCacheWrite = false;
        for (int i = 0; i < messages.Count; i++)
        {
            if (messages[i] is not AssistantMessage assistant)
                continue;
            Usage usage = assistant.Usage;
            input += usage.InputTokens;
            output += usage.OutputTokens;
            if (usage.ReasoningTokens is { } r)
            {
                reasoning += r;
                hasReasoning = true;
            }

            if (usage.CacheReadTokens is { } cr)
            {
                cacheRead += cr;
                hasCacheRead = true;
            }

            if (usage.CacheWriteTokens is { } cw)
            {
                cacheWrite += cw;
                hasCacheWrite = true;
            }
        }

        return new Usage(
            input,
            output,
            hasReasoning ? reasoning : null,
            hasCacheRead ? cacheRead : null,
            hasCacheWrite ? cacheWrite : null);
    }

    private static bool IsZeroUsage(Usage usage) =>
        usage.InputTokens == 0
        && usage.OutputTokens == 0
        && (usage.ReasoningTokens ?? 0) == 0
        && (usage.CacheReadTokens ?? 0) == 0
        && (usage.CacheWriteTokens ?? 0) == 0;

    private static Usage AddUsage(Usage current, Usage delta) => new(
        current.InputTokens + delta.InputTokens,
        current.OutputTokens + delta.OutputTokens,
        SumOptional(current.ReasoningTokens, delta.ReasoningTokens),
        SumOptional(current.CacheReadTokens, delta.CacheReadTokens),
        SumOptional(current.CacheWriteTokens, delta.CacheWriteTokens));

    private static int? SumOptional(int? left, int? right)
    {
        if (left is null && right is null)
            return null;
        return (left ?? 0) + (right ?? 0);
    }

    /// <summary>
    ///     Child-usage aggregate for the failure path ([UX6] #266). The read
    ///     rides the canonical try→Result rail (bible §4.5): a storage failure
    ///     comes back as a failed Result, and the CALLER decides how to
    ///     degrade — this method no longer decides "zero" on its own.
    ///     Cancellation propagates — it must never turn a cancel into a quiet
    ///     success-shaped failure message.
    /// </summary>
    private async Task<Result<Usage>> TryAggregateChildUsageAsync(string sessionId, CancellationToken ct)
    {
        // The store is the boundary: a read that throws (a third-party store,
        // a transport hiccup) is an exception, and the canonical handler turns
        // it into a value. ResultErrors.Message rethrows a cancellation, so a
        // cancel still escapes instead of masquerading as a zero aggregate.
        var usage = await Result.Try(
                () => store.GetMessagesAsync(sessionId, ct),
                ResultErrors.Message)
            .Bind(read => read)
            .Map(AggregateUsage)
            .TapError(error => SubAgentRunnerLog.UsageAggregateFailed(logger, error, sessionId))
            .ConfigureAwait(false);

        return usage;
    }

    /// <summary>
    ///     Fold a child's token delta into the parent side ([UX6] #266):
    ///     the issuing assistant message's <see cref="Usage" /> (so JSONL,
    ///     which derives stats from messages, picks it up) plus the parent
    ///     session stats record (for stores that persist metadata). Serialized
    ///     per parent session — parallel background children completing at
    ///     once must not interleave the read-modify-write and lose a delta.
    ///     Best-effort throughout: propagation never fails the run it follows.
    /// </summary>
    private async Task PropagateChildCostAsync(
        string? parentSessionId,
        string? parentMessageId,
        string childSessionId,
        Usage childUsage,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(parentSessionId) || IsZeroUsage(childUsage))
            return;
        var gate = GateFor(parentSessionId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await PropagateUnderLockAsync(parentSessionId, parentMessageId, childSessionId, childUsage, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task PropagateUnderLockAsync(
        string parentSessionId,
        string? parentMessageId,
        string childSessionId,
        Usage childUsage,
        CancellationToken ct)
    {
        try
        {
            if (!string.IsNullOrEmpty(parentMessageId))
            {
                var messages = await store.GetMessagesAsync(parentSessionId, ct).ConfigureAwait(false);
                if (messages.IsFailure)
                {
                    SubAgentRunnerLog.ReadParentSessionFailed(logger, parentSessionId, messages.Error);
                }
                else
                {
                    AssistantMessage? parent = null;
                    #pragma warning disable CFE0001
                    // CFE0001: false positive — the IsFailure/IsSuccess guard is an early
                    // return or continue, a control-flow shape the analyzer does not model.
                    // The .Value is safe. Baseline: docs/ROP-API-INVENTORY.md §5.
                    var list = messages.Value;
                    #pragma warning restore CFE0001
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (list[i] is AssistantMessage assistant
                            && string.Equals(assistant.Id, parentMessageId, StringComparison.Ordinal))
                        {
                            parent = assistant;
                            break;
                        }
                    }

                    if (parent is null)
                    {
                        SubAgentRunnerLog.ParentMessageNotFound(logger, parentMessageId, parentSessionId);
                    }
                    else
                    {
                        var merged = parent with { Usage = AddUsage(parent.Usage, childUsage) };
                        var rewritten = await store.UpdateMessageAsync(parentSessionId, merged, ct).ConfigureAwait(false);
                        if (rewritten.IsFailure)
                            SubAgentRunnerLog.PropagateToParentMessageFailed(logger, parentMessageId, parentSessionId, rewritten.Error);
                    }
                }
            }

            var stats = await store.GetStatsAsync(parentSessionId, ct).ConfigureAwait(false);
            if (stats.IsFailure)
            {
                SubAgentRunnerLog.ReadParentStatsFailed(logger, parentSessionId, stats.Error);
                return;
            }

            // #1105: the child's model is NOT resolved here — this class holds
            // no provider catalogue, only the store, the loop and a logger. But
            // the child's own store record was written by the core turn path
            // with the child model's real rates, so that priced total is
            // carried across instead of re-pricing with Pricing.Unknown (which
            // floors the money to zero and flips IsCostKnown=false). A store
            // that derives stats from messages (JSONL) reports cost 0/unknown,
            // and the fold below degrades to that floor — tokens stay exact.
            SessionMetadata propagated = await FoldChildCostAsync(
                    stats.Value, childSessionId, childUsage, ct).ConfigureAwait(false);
            var stored = await store.UpdateStatsAsync(
                    parentSessionId,
                    propagated,
                    ct)
                .ConfigureAwait(false);
            if (stored.IsFailure)
                SubAgentRunnerLog.StorePropagatedCostFailed(logger, parentSessionId, stored.Error);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            SubAgentRunnerLog.PropagateCostFailed(logger, ex, parentSessionId);
        }
    }

    /// <summary>
    ///     Fold a child's token delta into the parent stats record (#1105),
    ///     carrying the cost the child's own store record already priced. A
    ///     child whose stats are unreadable degrades to the old unpriced floor
    ///     so the tokens still land; the loss is logged, not silent.
    /// </summary>
    private async Task<SessionMetadata> FoldChildCostAsync(
        SessionMetadata parentStats,
        string childSessionId,
        Usage childUsage,
        CancellationToken ct)
    {
        var childStats = await store.GetStatsAsync(childSessionId, ct).ConfigureAwait(false);
        if (childStats.IsFailure)
        {
            logger.LogWarning(
                "Failed to read sub-agent stats for cost propagation (session {SessionId}): {Error}",
                childSessionId, childStats.Error);
            return parentStats.AddUsage(childUsage, Pricing.Unknown);
        }

        return parentStats.AddExternalCost(childUsage, childStats.Value.Cost, childStats.Value.IsCostKnown);
    }

    /// <summary>
    ///     Walk the history backwards and concatenate the text parts of the LAST
    ///     assistant message that carries any prose. Thinking blocks and tool-call
    ///     payloads are ignored — parents receive the answer, not the chatter.
    /// </summary>
    private static string ExtractFinalOutput(IReadOnlyList<Harbor.Abstractions.Models.AgentMessage> messages)
    {
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i] is not AssistantMessage assistant)
                continue;

            string text = ConcatTextParts(assistant.Parts);
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        return string.Empty;
    }

    private static string ConcatTextParts(IReadOnlyList<ContentPart> parts)
    {
        // Fast path: exactly one text part (the overwhelmingly common case).
        if (parts.Count == 1 && parts[0] is TextPart single)
            return single.Text;

        string? joined = null;
        for (int i = 0; i < parts.Count; i++)
        {
            if (parts[i] is not TextPart part)
                continue;
            joined = joined is null ? part.Text : $"{joined}\n{part.Text}";
        }

        return joined ?? string.Empty;
    }

    private static string BuildTitle(AgentDefinition agent, string prompt)
    {
        int newline = prompt.IndexOf('\n');
        string firstLine = (newline >= 0 ? prompt[..newline] : prompt).Trim();
        return $"{TitlePrefix}({agent.Name.Value}): {firstLine.Truncate(60)}";
    }
}

/// <summary>
///     Forwarding holder used by eager host composition. The tool registries are built
///     BEFORE <see cref="ISessionStore" />/<see cref="IAgentLoop" /> singletons exist, so
///     <c>RegistriesModule</c> hands <c>TaskTool</c> this forwarder and attaches the real
///     <see cref="SubAgentRunner" /> into DI right after. Detached state fails honestly
///     instead of NRE-ing (ROP: no silent fake success — G4 contract).
/// </summary>
public sealed class DeferredSubAgentRunner : ISubAgentRunner
{
    private volatile ISubAgentRunner? _inner;

    /// <summary>Wire in the real runner once the container can build it. One-shot, idempotent-after-attach semantics are NOT required (host composes once).</summary>
    public void Attach(ISubAgentRunner inner)
        => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <inheritdoc />
    public bool CanSpawn => _inner?.CanSpawn ?? false;

    /// <inheritdoc />
    public Task<Result<SubAgentRunResult>> RunAsync(
        AgentDefinition agent,
        SubAgentRunRequest request,
        CancellationToken ct = default)
    {
        var current = _inner;
        return current is not null
            ? current.RunAsync(agent, request, ct)
            : Task.FromResult(Result.Failure<SubAgentRunResult>(
                "Sub-agent runtime is not initialized yet (host composition incomplete)."));
    }
}

/// <summary>
///     SG2: BCL <c>[LoggerMessage]</c> delegates for <see cref="SubAgentRunner" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class SubAgentRunnerLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Failed to persist sub-session metadata {SessionId}: {Error}")]
    public static partial void PersistMetadataFailed(ILogger logger, string sessionId, string error);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Sub-agent starting: agent={Agent} session={SessionId} parent={ParentSessionId}")]
    public static partial void SubAgentStarting(ILogger logger, string agent, string sessionId, string parentSessionId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Sub-agent run ended abnormally: agent={Agent} session={SessionId} error={Error}")]
    public static partial void RunEndedAbnormally(ILogger logger, string agent, string sessionId, string error);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Sub-agent finished: agent={Agent} session={SessionId} messages={Count} outputChars={Length}")]
    public static partial void SubAgentFinished(ILogger logger, string agent, string sessionId, int count, int length);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "Failed to persist sub-session status {SessionId}: {Error}")]
    public static partial void PersistStatusFailed(ILogger logger, string sessionId, string error);

    [LoggerMessage(EventId = 6, Level = LogLevel.Debug, Message = "Failed to extract degraded sub-agent output for session {SessionId}")]
    public static partial void DegradedOutputExtractFailed(ILogger logger, Exception ex, string sessionId);

    [LoggerMessage(EventId = 7, Level = LogLevel.Warning, Message = "Failed to aggregate sub-agent usage for session {SessionId}: {Error}")]
    public static partial void UsageAggregateFailed(ILogger logger, string error, string sessionId);

    [LoggerMessage(EventId = 8, Level = LogLevel.Warning, Message = "Failed to read parent session {SessionId} for sub-agent cost propagation: {Error}")]
    public static partial void ReadParentSessionFailed(ILogger logger, string sessionId, string error);

    [LoggerMessage(EventId = 9, Level = LogLevel.Debug, Message = "Parent message {MessageId} not found in session {SessionId}; propagating sub-agent cost to stats only")]
    public static partial void ParentMessageNotFound(ILogger logger, string messageId, string sessionId);

    [LoggerMessage(EventId = 10, Level = LogLevel.Warning, Message = "Failed to propagate sub-agent cost into parent message {MessageId} (session {SessionId}): {Error}")]
    public static partial void PropagateToParentMessageFailed(ILogger logger, string messageId, string sessionId, string error);

    [LoggerMessage(EventId = 11, Level = LogLevel.Warning, Message = "Failed to read parent stats for sub-agent cost propagation (session {SessionId}): {Error}")]
    public static partial void ReadParentStatsFailed(ILogger logger, string sessionId, string error);

    [LoggerMessage(EventId = 12, Level = LogLevel.Warning, Message = "Failed to store propagated sub-agent cost (session {SessionId}): {Error}")]
    public static partial void StorePropagatedCostFailed(ILogger logger, string sessionId, string error);

    [LoggerMessage(EventId = 13, Level = LogLevel.Warning, Message = "Failed to propagate sub-agent cost into parent session {SessionId}")]
    public static partial void PropagateCostFailed(ILogger logger, Exception ex, string sessionId);

}
