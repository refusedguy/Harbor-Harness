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
    private readonly IHookRunner? _hooks;

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
        // PX4: user SessionEnd hooks. Optional so every direct-construction
        // caller keeps compiling unchanged; null keeps the hooks-free path.
        // Forwarded to the fallback dispatcher below for Pre/PostToolUse.
        IHookRunner? hookRunner = null)
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
        _hooks = hookRunner;
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
        _compactionBehavior = new CompactionBehavior(compaction, tokenTracker, eventBus, _metrics, logger);
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
            _compactionBehavior, _steering, _backgroundDrain);
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
                    _logger.LogInformation("Agent run hit wall-clock budget ({Budget}): session={SessionId} agent={Agent}", budget, session.Session.Id, agent.Name.Value);
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
                    // Terminal provider error — same as the pre-extraction inline
                    // early-return: fail the run without turn/agent end events.
                    return Result.Failure(runFailure);
                }

                if (step.EndRun)
                {
                    limit = step.Limit;
                    break;
                }
            }

            if (ct.IsCancellationRequested)
            {
                // A cancelled run is NOT a successful run: report failure so callers
                // (and WaitForIdleAsync consumers) can distinguish it from normal
                // completion. The AgentEndEvent carries Cancelled=true so renderers
                // can reflect the aborted state instead of a clean finish.
                _logger.LogInformation("Agent run cancelled: session={SessionId} agent={Agent}", session.Session.Id, agent.Name.Value);
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

            _logger.LogInformation("Agent loop completed: session={SessionId} agent={Agent}", session.Session.Id, agent.Name.Value);
            // #403: `Limit` rides the terminal event so every reader gets the
            // reason, not only `RunOutcome`. Renderers read this event and NOT
            // the read-model, so without it the fact stops here.
            await _eventBus.PublishAsync(
                new AgentEndEvent(SnapshotMessages(session.Messages), Limit: limit), ct).ConfigureAwait(false);
            await RunSessionEndHooksAsync(session.Session.Id).ConfigureAwait(false);

            return Result.Success();
        }
        catch (Exception ex)
        {
            activity?.AddException(ex);
            // O1: keep the localized message AND the correlation key in one record.
            string failure = string.Format(CultureInfo.InvariantCulture, CoreResources.GetError("AgentFailed"), ex.Message);
            _logger.LogError(ex, "Agent run failed: session={SessionId} error={Error}", session.Session.Id, failure);
            await _eventBus.PublishAsync(new AgentErrorEvent(ex.Message, ex.ToString()), CancellationToken.None).ConfigureAwait(false);
            return Result.Failure(ex.Message);
        }
    }

    /// <summary>
    ///     Run user SessionEnd hooks after the terminal event. Advisory: the
    ///     runner logs hook failures and never throws on its own (except on a
    ///     cancelled token, which cannot happen here — the token is
    ///     <see cref="CancellationToken.None" /> and the run is already over),
    ///     so a hook can neither fail the run nor rewrite its outcome.
    /// </summary>
    private async Task RunSessionEndHooksAsync(string sessionId)
    {
        if (_hooks is null)
        {
            return;
        }

        await _hooks.RunSessionEndAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    ///     Materialize a snapshot list of the current session messages for events.
    /// </summary>
    private static List<AgentMessage> SnapshotMessages(IReadOnlyList<AgentMessage> messages)
    {
        var snapshot = new List<AgentMessage>(messages.Count);
        for (int i = 0; i < messages.Count; i++)
        {
            snapshot.Add(messages[i]);
        }
        return snapshot;
    }
}
