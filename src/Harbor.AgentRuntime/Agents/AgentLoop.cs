using System.Globalization;
using Harbor.Diagnostics;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Agents.Pipeline;
using Harbor.Application.Hooks;
using Harbor.Application.Resilience;
using Harbor.Application.Resources;
using Harbor.Application.Sessions;
using Harbor.Application.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Application.Agents;
/// <summary>
///     Default agent loop. Implements Chain of Responsibility pattern (GOF):
///     prompt → LLM stream → tool execution → next turn → (compaction if needed) → repeat.
///     <para>
///         <b>Decomposition (Task R32):</b> the streaming-buffer coalescing
///         and tool-execution dispatch were extracted into:
///         <list type="bullet">
///             <item><see cref="StreamingCoalescer" /> — text/thinking/tool-call buffer management</item>
///             <item><see cref="ToolDispatcher" /> — parallel/sequential tool execution + permission gating</item>
///         </list>
///         The loop itself now focuses on turn orchestration, event
///         publishing, and compaction checks.
///     </para>
///     <para>
///         <b>Decomposition ([G4], issue #179):</b> per-turn execution lives in
///         <see cref="TurnRunner" /> (resolve → prompt → stream → tools →
///         drains → end-of-run decision), the background-task ping in
///         <see cref="BackgroundDrain" />, and the tool-definition table in
///         <see cref="ToolTableBuilder" />. The loop keeps run lifecycle
///         (activity, start/end events, cancellation) and iterates turns.
///         Behavior is 1-to-1 with the pre-split loop; public contracts unchanged.
///     </para>
///     <para>
///         <b>Decomposition (#1101, slice 1):</b> message snapshotting for events
///         lives in <see cref="MessageSnapshot" /> (verbatim move); the loop keeps
///         a forwarder so the five event call sites are untouched.
///     </para>
///     <para>
///         <b>Decomposition (#1101, slice 2):</b> user-hook gates
///         (<c>SessionEnd</c> / <c>UserPromptSubmit</c> / <c>SessionStart</c>)
///         live in <see cref="UserHookGates" /> (verbatim move); the loop keeps
///         forwarders so the run-lifecycle call sites are untouched.
///         Turn execution (incl. the <c>LlmEvent</c> switch) stays in
///         <see cref="TurnRunner" /> — not touched by this slice.
///     </para>
/// </summary>
public sealed class AgentLoop : IAgentLoop
{
    private readonly IAgentRegistry _agents;
    private readonly ICompactionService _compaction;
    private readonly IEventBus _eventBus;
    private readonly ILogger<AgentLoop> _logger;
    private readonly MessageConverter _messageConverter;
    private readonly IPermissionService _permissions;
    private readonly IRetryPolicy _retryPolicy;
    private readonly ISystemPromptBuilder _promptBuilder;
    private readonly IProviderRegistry _providers;
    // #471: no ITokenTracker field — it was assigned and never read (#981).
    private readonly IToolDispatcher _toolDispatcher;
    private readonly IToolRegistry _tools;
    private readonly IBackgroundTaskRegistry? _backgroundTasks;
    private readonly AgentPipeline _pipeline;
    private readonly CompactionBehavior _compactionBehavior;
    private readonly SteeringDrainBehavior _steering;
    private readonly TurnRunner _turnRunner;
    private readonly BackgroundDrain _backgroundDrain;
    private readonly IMcpRegistry? _mcpRegistry;
    private readonly IMetrics _metrics;
    private readonly ITracer _tracer;
    // #1024: wall-clock budget enforcement reads these — an elapsed check at
    // the turn boundary, deliberately NOT a CancelAfter on the run token.
    private readonly TimeProvider _clock;
    private readonly TimeSpan? _runTimeout;
    private readonly UserHookGates _hookGates;

    /// <summary>
    ///     The goal round driver (#1761). It reserves a goal's next round at
    ///     the agent's quiescence point and injects the round prompt into the
    ///     SAME session.
    /// </summary>
    /// <remarks>
    ///     A driver without a goal store is inert: the run ends exactly where
    ///     it would have ended without a goal. That is the shape of every
    ///     optional dependency here — absent means "the old behaviour", never
    ///     "a broken one".
    /// </remarks>
    private readonly GoalRoundDriver _goalDriver;

    /// <summary>
    ///     Construct an <see cref="AgentLoop" /> wired to the supplied services.
    /// </summary>
    public AgentLoop(
        IProviderRegistry providers,
        IToolRegistry tools,
        IAgentRegistry agents,
        ISystemPromptBuilder promptBuilder,
        ICompactionService compaction,
        ITokenTracker tokenTracker,
        IRetryPolicy retryPolicy,
        IEventBus eventBus,
        IPermissionService permissions,
        MessageConverter messageConverter,
        ILogger<AgentLoop> logger,
        IMetrics? metrics = null,
        ITracer? tracer = null,
        IToolDispatcher? toolDispatcher = null,
        IMcpRegistry? mcpRegistry = null,
        IBackgroundTaskRegistry? backgroundTasks = null,
        // #49 PR2: forwarded to the fallback dispatcher so tests driving the
        // loop directly still get the commit barrier when they pass one.
        IApprovalCoordinator? coordinator = null,
        // #480 A10: the run's cross-cutting behaviours arrive from the
        // composition root, so a third concern is a registration rather than an
        // edit to this class. Optional so the direct-construction callers (tests,
        // benchmarks, load harnesses) keep compiling unchanged — they take
        // DefaultRunBehaviors below, which is the same two concerns in the same
        // order. The product half of that claim is gated:
        // tests/Harbor.Hosting.Tests/PipelineBehaviorCompositionTests.cs.
        IEnumerable<IPipelineBehavior>? pipelineBehaviors = null,
        // #1024: wall-clock budget + clock. Both optional so every
        // direct-construction caller (tests, benchmarks, load harnesses) keeps
        // compiling unchanged and keeps the legacy unbounded behaviour.
        // Null budget (default) preserves it. A set budget is enforced by an
        // elapsed check at the turn boundary — NOT by CancelAfter on the run
        // token, which would read as a user cancel.
        TimeProvider? timeProvider = null,
        TimeSpan? runTimeout = null,
        // PX4: user hooks (slice 1: Pre/PostToolUse + SessionEnd; slice 2:
        // SessionStart + UserPromptSubmit + PreCompact). Optional so every
        // direct-construction caller keeps compiling unchanged; null keeps
        // the hooks-free path. Forwarded to the fallback dispatcher and the
        // compaction behavior below for their own event points.
        IHookRunner? hookRunner = null,
        // #1612: history-shaping seam for the turn pipeline. Optional so every
        // direct-construction caller keeps compiling unchanged; null keeps the
        // default forwarder. Forwarded to the compaction behavior and the
        // turn runner below.
        ICompactionView? compactionView = null,
        // #1527: the flush policy of the per-delta update coalescer. Optional
        // so every direct-construction caller keeps compiling unchanged; null
        // keeps the shipped default (UpdateFlushPolicy.Default — the numbers
        // are stated in ONE place so the docs, the tests and the runtime
        // cannot drift). Forwarded to the turn runner below.
        UpdateFlushPolicy? updateFlushPolicy = null,
        // #1735: the skill registry. Optional so every direct-construction
        // caller keeps compiling unchanged; null keeps the workspace
        // discovery and no invocation policy. Forwarded to the turn runner
        // below, which is where the model-facing catalog is built.
        ISkillRegistry? skillRegistry = null,
        // #1736: the scope-aware provider resolver. Optional so every
        // direct-construction caller keeps compiling unchanged; null keeps the
        // process-wide provider registry, which is what the product resolved
        // before scopes existed. Forwarded to the turn runner below, which
        // resolves the client a run uses.
        IScopedProviderResolver? scopedProviders = null,
        // #1761: the session store, for the GOAL axis. Optional so every
        // direct-construction caller keeps compiling unchanged; null (or a
        // store that has no goal axis) keeps the run free of goals: the
        // round driver is then inert and the loop behaves exactly as it
        // did before the goal existed.
        ISessionStore? sessionStore = null)
    {
        _providers = providers;
        _tools = tools;
        _agents = agents;
        // Ф6/A2: memoize prompt builds — same (agent, model, tools, context)
        // hash returns the cached string instead of re-running the ~180-line
        // template assembly every turn.
        _promptBuilder = new CachingSystemPromptBuilder(promptBuilder);
        _compaction = compaction;
        // #471: the tracker reaches the four behaviours below as this parameter.
        _retryPolicy = retryPolicy;
        _eventBus = eventBus;
        _permissions = permissions;
        _messageConverter = messageConverter;
        _logger = logger;
        _metrics = metrics ?? NullMetrics.Instance;
        _tracer = tracer ?? NullTracer.Instance;
        _clock = timeProvider ?? TimeProvider.System;
        _runTimeout = runTimeout;
        _hookGates = new UserHookGates(hookRunner, logger);
        // ROP-C П.5: the dispatcher is injected via DI when composed by the host,
        // while tests and benchmarks fall back to a locally built one. That
        // fallback uses a NullLogger because the loop's own typed logger must
        // not be lent out under a foreign category (S6672).
        _toolDispatcher = toolDispatcher
            ?? new ToolDispatcher(tools, permissions, eventBus, NullLogger<ToolDispatcher>.Instance, coordinator, hookRunner: hookRunner);
        // §3.5 pipeline: run-level cross-cutting concerns are middleware over the
        // whole run; per-turn behaviors (compaction, steering, max steps) are
        // extracted classes the core loop calls each turn. The list is the
        // container's (#480 A10), NOT a literal here — that was the defect: an
        // IPipelineBehavior nobody could register meant a third run-level concern
        // could only be added by editing the class that runs the agent.
        _pipeline = new AgentPipeline(pipelineBehaviors ?? DefaultRunBehaviors(logger));
        // PX4 slice 2: the optional hook runner rides here too, so the
        // advisory PreCompact point fires from the same seam the loop already
        // used for the Pre/PostToolUse gate. Null keeps the hooks-free path.
        // #1612: one seam instance for the whole run — the behavior and the
        // turn runner must agree on the view, so the loop resolves the default
        // once instead of each of them falling back independently.
        ICompactionView view = compactionView ?? CompactionView.Instance;
        _compactionBehavior = new CompactionBehavior(
            compaction, tokenTracker, eventBus, _metrics, logger, hookRunner, view);
        _steering = new SteeringDrainBehavior(tokenTracker, logger);
        // ROP-D Z3: MCP server instructions flow into the system prompt when a
        // registry is composed in; tests without one keep the section absent.
        _mcpRegistry = mcpRegistry;
        // Background-task ping: detached runs drain into the session when the
        // loop is composed with a registry; tests without one skip silently.
        _backgroundTasks = backgroundTasks;
        // [G4]: per-turn execution, background drain and tool-table helpers live
        // in collaborators; the loop keeps run lifecycle. Collaborators share
        // the loop's logger so log categories stay identical to pre-extraction.
        _backgroundDrain = new BackgroundDrain(backgroundTasks, tokenTracker, logger);
        _turnRunner = new TurnRunner(
            providers, tools, _promptBuilder, messageConverter, eventBus, logger,
            _metrics, tokenTracker, retryPolicy, _toolDispatcher, mcpRegistry,
            _compactionBehavior, _steering, _backgroundDrain, view,
            updateFlushPolicy, _clock, skillRegistry, scopedProviders);
        // #1761: the goal axis is a SEPARATE seam from the session store
        // itself — only a durable backend implements it — so the driver is
        // built from the store's goal view, and from nothing at all when the
        // store has none.
        _goalDriver = new GoalRoundDriver(sessionStore as ISessionGoalStore, logger);
    }

    /// <summary>
    ///     The run-level behaviours a directly-constructed loop gets when no
    ///     container supplied any: the two concerns that were hardcoded here
    ///     before #480 A10, in the same order, so a test-built loop and a
    ///     host-built one wrap the same chain.
    /// </summary>
    /// <remarks>
    ///     The logger is the loop's own, exactly as before the split, so a
    ///     directly-constructed loop's log categories do not move. The DI path
    ///     deliberately differs: <c>CoreModule</c> hands each behaviour its own
    ///     typed logger, the same call it already makes for
    ///     <c>IToolDispatcher</c> (ROP-C П.8). The product half of the pair —
    ///     "every IPipelineBehavior is registered" — is gated by
    ///     <c>tests/Harbor.Hosting.Tests/PipelineBehaviorCompositionTests.cs</c>,
    ///     so this fallback cannot drift away from the registered set unnoticed.
    /// </remarks>
    private static IEnumerable<IPipelineBehavior> DefaultRunBehaviors(ILogger logger) =>
        [new LoggingBehavior(logger), new PermissionCheckBehavior(logger)];

    /// <summary>
    ///     Run the agent loop to completion: prompt → LLM stream → tool execution → next turn,
    ///     repeating until either no tool calls are emitted or <see cref="AgentDefinition.MaxSteps" />
    ///     is reached. Compaction runs at the start of each turn if the token estimator says so.
    /// </summary>
    /// <param name="session">The session context for this run.</param>
    /// <param name="agent">The agent definition driving the loop.</param>
    /// <param name="ct">Cancellation token used to abort the run at the next safe boundary.</param>
    /// <returns>
    ///     Success when the loop reached a terminal event WITHOUT a provider or stream failure
    ///     — which includes a run ended by a limit, and does NOT mean the agent's work was
    ///     finished. Failure carries a provider/stream error, or marks a user cancellation.
    ///     <para>
    ///         #403: the WHY lives on <see cref="AgentEndEvent.Limit" />, not here, and that
    ///         split is deliberate rather than a gap left for later. This channel has exactly
    ///         two states and one of them is an error string, so a limit reported through it
    ///         would arrive as <c>Failure</c> — and every consumer reads that as a malfunction:
    ///         <c>SubAgentRunner</c> (:168-188) turns it into
    ///         <c>SessionStatus.Error</c> and a "Sub-agent 'x' failed" note fed to the parent
    ///         model. Reporting "the step budget was reached" as a failure is the same error in
    ///         the opposite direction, so the reason is carried on the event and this
    ///         <see cref="Result" /> keeps meaning "the loop did not die".
    ///     </para>
    /// </returns>
    public Task<Result> RunAsync(ISessionContext session, AgentDefinition agent, CancellationToken ct = default)
    {
        // §3.5: the run enters the behavior pipeline; the original turn loop is the
        // terminal handler (RunCoreAsync).
        return _pipeline.HandleAsync(new PromptRequest(session, agent), RunCoreAsync, ct);
    }

    /// <summary>
    ///     Terminal pipeline handler: the turn loop itself — prompt → LLM stream →
    ///     tool execution → next turn, repeating until no tool calls are emitted,
    ///     the step budget is exhausted, or the run is cancelled.
    /// </summary>
    private async Task<Result> RunCoreAsync(PromptRequest run, CancellationToken ct)
    {
        ISessionContext session = run.Session;
        AgentDefinition agent = run.Agent;
        using var activity = HarborTelemetry.Source.StartActivity("Agent.Run");
        activity?.SetTag(GenAiTags.AgentName, agent.Name.Value);
        activity?.SetTag(GenAiTags.RequestModel, agent.Model);
        try
        {
            // Resolve the model once up front so the context window can be carried
            // on AgentStartEvent (renderers need it to show context usage).
            // ROP-C П.1-П.3/П.7: the TryCreate → GetClient → catalog chain rides
            // one Bind railway with a single failure exit; the TTL-cached catalog
            // lives in the shared provider registry, not per-loop.
            // ([G4]: resolution owned by TurnRunner; the loop keeps the single failure exit.)
            var resolved = await _turnRunner.ResolveModelAsync(agent, ct).ConfigureAwait(false);
            if (resolved.IsFailure) // §4.6-ok: единственный выход Bind-рельсы setup'а (rop-final-mile L1).
                return resolved.ConvertFailure();

            var (client, model) = resolved.Value;

            await _eventBus.PublishAsync(new AgentStartEvent(session.Session.Id, SnapshotMessages(session.Messages), model, session.Session.Kind), ct).ConfigureAwait(false);

            // PX4 slice 2: the run's start events. UserPromptSubmit is the
            // only new one that can stop the run, so it runs FIRST and its
            // deny ends the run before a single model call — the same
            // fail-closed gate shape slice 1 gave PreToolUse, one level up.
            HookVerdict promptVerdict = await RunUserPromptHooksAsync(session, agent, model, ct).ConfigureAwait(false);
            if (promptVerdict.Decision == HookDecision.Deny)
            {
                // A prompt the user's own hook refused is not a run failure
                // in the provider sense: it never started. Nothing further is
                // published for it — AgentStartEvent above already happened,
                // and a turn/agent end pair for a run with no turns would lie
                // about what occurred.
                AgentLoopLog.UserHookDeniedPrompt(_logger, session.Session.Id, agent.Name.Value, promptVerdict.Reason);
                return Result.Failure(promptVerdict.Reason ?? "A user hook denied this prompt.");
            }

            await RunSessionStartHooksAsync(
                session.Session.Id, agent.Name.Value, model.Id, ct).ConfigureAwait(false);

            // Previous-run background completions land before the first turn
            // so a new run picks up reports that finished while idle.
            await _backgroundDrain.DrainAsync(session, ct).ConfigureAwait(false);

            int turn = 0;
            // Set when LLM-based compaction fails; the CURRENT and every
            // subsequent turn then build their request from a strictly
            // reduced tail of the history instead of continuing with a
            // known-overfull context. Owned by the loop; fed back into each turn.
            bool truncationFallback = false;
            // #403: which limit ended the run, when one did. Null on every
            // other exit — including the cancel path below, because a ceiling
            // the user set and a stop the user pressed are different facts and
            // the terminal event is where they must stay distinguishable.
            RunLimitKind? limit = null;
            // #1024: the wall-clock stamp. Read once per run so the elapsed
            // check below is a comparison, not a second clock to skew.
            DateTimeOffset startedAt = _clock.GetUtcNow();
            // #404: the run's budget ledger. Null when the agent sets no
            // caps — the loop then pays one null-branch per turn (no table,
            // no lookup), which is the zero-added-cost claim for uncapped
            // runs. One instance per run, never shared: the counters are run
            // totals, and sharing would attribute one run's spend to the next.
            // Named runBudget: the wall-clock check below already binds the
            // name budget to its TimeSpan.
            RunBudgetTracker? runBudget = agent.Budget is { } caps ? new RunBudgetTracker(caps) : null;
            while (!ct.IsCancellationRequested)
            {
                // #1024: boundary enforcement for the wall-clock budget. This
                // MUST stay a read of the clock, never a CancelAfter on `ct`:
                // firing the run token would route the run into the cancel
                // branch below and report a user-set ceiling as a user-pressed
                // stop. An in-flight turn is never interrupted — it completes,
                // and the run ends before the next one starts (#997 leaves
                // in-flight calls alone the same way).
                if (_runTimeout is { } budget && _clock.GetUtcNow() - startedAt >= budget)
                {
                    AgentLoopLog.WallClockBudgetHit(_logger, budget, session.Session.Id, agent.Name.Value);
                    limit = RunLimitKind.Timeout;
                    break;
                }
                turn++;
                // [G4]: the whole turn (compaction → prompt → stream → tools →
                // drains → turn-end event → end-of-run decision) runs inside TurnRunner.
                TurnStepResult step = await _turnRunner.RunTurnAsync(
                    session, agent, client, model, turn, truncationFallback, ct, runBudget).ConfigureAwait(false);
                truncationFallback = step.TruncationFallback;
                if (step.RunFailure is { } runFailure)
                {
                    // #1103: a terminal provider error must still close the run.
                    // Without the terminal event the turn card hangs and the IPC
                    // projection never learns the run ended. The error itself was
                    // already published as AgentErrorEvent by the turn, so only
                    // the terminal fact is missing here. Published with None: the
                    // terminal record must not depend on the run token.
                    await _eventBus.PublishAsync(
                        new AgentEndEvent(SnapshotMessages(session.Messages)), CancellationToken.None).ConfigureAwait(false);
                    await RunSessionEndHooksAsync(session.Session.Id).ConfigureAwait(false);
                    return Result.Failure(runFailure);
                }

                if (step.EndRun)
                {
                    limit = step.Limit;

                    // #1761: the goal's round. The turn went QUIET — the
                    // agent asked for no more tools — so a goal that still
                    // has rounds gets one, in THIS session: the round prompt
                    // is appended to the same conversation the earlier rounds
                    // are in. The driver decides; the loop only carries the
                    // answer.
                    if (step.Quiescent)
                    {
                        GoalRoundDriver.RoundOutcome outcome = await _goalDriver
                            .TryReserveRoundAsync(session, agent, ct).ConfigureAwait(false);
                        if (outcome == GoalRoundDriver.RoundOutcome.Scheduled)
                        {
                            // Another round: the loop keeps going, and the
                            // step budget still caps the whole run.
                            continue;
                        }

                        if (outcome == GoalRoundDriver.RoundOutcome.Blocked)
                        {
                            // The goal's rounds are spent. The run ends the
                            // way every other bounded run ends — with a
                            // limit on the terminal event — and the goal
                            // itself carries the reason.
                            limit = RunLimitKind.MaxGoalRounds;
                        }
                    }

                    break;
                }
            }

            if (ct.IsCancellationRequested)
            {
                // A cancelled run is NOT a successful run: report failure so callers
                // (and WaitForIdleAsync consumers) can distinguish it from normal
                // completion. The AgentEndEvent carries Cancelled=true so renderers
                // can reflect the aborted state instead of a clean finish.
                AgentLoopLog.RunCancelled(_logger, session.Session.Id, agent.Name.Value);
                // #407: a cancelled parent takes its detached children with it.
                // The registry owns a linked CTS per background run, so this
                // reaches children even when the launching turn's token is gone.
                // Turn boundaries and normal completion do NOT come here.
                _backgroundTasks?.CancelSession(session.Session.Id);
                await _eventBus.PublishAsync(
                    new AgentEndEvent(SnapshotMessages(session.Messages), Cancelled: true), CancellationToken.None).ConfigureAwait(false);
                await RunSessionEndHooksAsync(session.Session.Id).ConfigureAwait(false);

                return Result.Failure("Agent run was cancelled.");
            }

            AgentLoopLog.LoopCompleted(_logger, session.Session.Id, agent.Name.Value);
            // #403: `Limit` rides the terminal event so every reader gets the
            // reason, not only `RunOutcome`. Renderers read this event and NOT
            // the read-model, so without it the fact stops here.
            // #1103: published with None — the terminal record of a finished run
            // must not depend on the run token (a cancel racing the finish would
            // otherwise turn the terminal event into an error).
            await _eventBus.PublishAsync(
                new AgentEndEvent(SnapshotMessages(session.Messages), Limit: limit), CancellationToken.None).ConfigureAwait(false);
            await RunSessionEndHooksAsync(session.Session.Id).ConfigureAwait(false);

            return Result.Success();
        }
        catch (OperationCanceledException oce) when (ct.IsCancellationRequested)
        {
            // #1103: a cancel landing inside a turn (event publish, persist)
            // used to surface as AgentErrorEvent — a user-pressed stop read as
            // a malfunction. Cancellation is not an error: close the run as
            // cancelled with no Error event, on the same path as the boundary
            // check above.
            _logger.LogInformation(oce, "Agent run cancelled: session={SessionId} agent={Agent}", session.Session.Id, agent.Name.Value);
            _backgroundTasks?.CancelSession(session.Session.Id);
            await _eventBus.PublishAsync(
                new AgentEndEvent(SnapshotMessages(session.Messages), Cancelled: true), CancellationToken.None).ConfigureAwait(false);
            await RunSessionEndHooksAsync(session.Session.Id).ConfigureAwait(false);
            return Result.Failure("Agent run was cancelled.");
        }
        catch (Exception ex)
        {
            activity?.AddException(ex);
            // O1: keep the localized message AND the correlation key in one record.
            string failure = string.Format(CultureInfo.InvariantCulture, CoreResources.GetError("AgentFailed"), ex.Message);
            AgentLoopLog.RunFailed(_logger, ex, session.Session.Id, failure);
            await _eventBus.PublishAsync(new AgentErrorEvent(ex.Message, ex.ToString()), CancellationToken.None).ConfigureAwait(false);
            return Result.Failure(ex.Message);
        }
    }

    /// <summary>
    ///     Run user SessionEnd hooks after the terminal event.
    ///     Forwarder over <see cref="UserHookGates" /> (#1101, slice 2: verbatim move).
    /// </summary>
    private Task RunSessionEndHooksAsync(string sessionId) =>
        _hookGates.RunSessionEndHooksAsync(sessionId);

    /// <summary>
    ///     Run the <c>UserPromptSubmit</c> gate for the prompt that opened this run.
    ///     Forwarder over <see cref="UserHookGates" /> (#1101, slice 2: verbatim move).
    /// </summary>
    private Task<HookVerdict> RunUserPromptHooksAsync(
        ISessionContext session, AgentDefinition agent, ModelInfo model, CancellationToken ct) =>
        _hookGates.RunUserPromptHooksAsync(session, agent, model, ct);

    /// <summary>
    ///     Run the advisory <c>SessionStart</c> hooks once, after the prompt gate.
    ///     Forwarder over <see cref="UserHookGates" /> (#1101, slice 2: verbatim move).
    /// </summary>
    private Task RunSessionStartHooksAsync(
        string sessionId, string agent, string model, CancellationToken ct) =>
        _hookGates.RunSessionStartHooksAsync(sessionId, agent, model, ct);

    /// <summary>
    ///     Materialize a snapshot list of the current session messages for events.
    ///     Forwarder over <see cref="MessageSnapshot" /> (#1101, slice 1: verbatim move).
    /// </summary>
    private static List<AgentMessage> SnapshotMessages(IReadOnlyList<AgentMessage> messages) =>
        MessageSnapshot.Snapshot(messages);
}

/// <summary>
///     SG2: BCL <c>[LoggerMessage]</c> delegates for <see cref="AgentLoop" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class AgentLoopLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "User hook denied the prompt for session={SessionId} agent={Agent}: {Reason}")]
    public static partial void UserHookDeniedPrompt(ILogger logger, string sessionId, string agent, string? reason);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Agent run hit wall-clock budget ({Budget}): session={SessionId} agent={Agent}")]
    public static partial void WallClockBudgetHit(ILogger logger, TimeSpan budget, string sessionId, string agent);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Agent run cancelled: session={SessionId} agent={Agent}")]
    public static partial void RunCancelled(ILogger logger, string sessionId, string agent);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Agent loop completed: session={SessionId} agent={Agent}")]
    public static partial void LoopCompleted(ILogger logger, string sessionId, string agent);

    [LoggerMessage(EventId = 5, Level = LogLevel.Error, Message = "Agent run failed: session={SessionId} error={Error}")]
    public static partial void RunFailed(ILogger logger, Exception ex, string sessionId, string error);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "UserPromptSubmit hooks failed for session={SessionId}; failing closed to Deny")]
    public static partial void UserPromptSubmitHooksFailed(ILogger logger, Exception ex, string sessionId);

}
