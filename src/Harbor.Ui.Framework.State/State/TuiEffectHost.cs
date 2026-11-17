using System.Collections.Immutable;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
using Microsoft.Extensions.Logging;
namespace Harbor.Ui.Framework.State;

/// <summary>
///     A Command: encapsulates the execution of one concrete <see cref="TuiEffect" />
///     type. Handlers are looked up by effect type in a
///     <see cref="TuiEffectHandlerRegistry" /> so the runner dispatches instead of
///     switching. This stays inside the existing effect axis (#555). It is a
///     dispatch detail, not a new plugin extension seam.
/// </summary>
/// <typeparam name="TEffect">The effect this handler executes.</typeparam>
public interface ITuiEffectHandler<in TEffect>
    where TEffect : TuiEffect
{
    /// <summary>Execute the effect with the same fire-and-forget contract as the old switch arms.</summary>
    void Execute(TEffect effect);
}

/// <summary>
///     Command registry for <see cref="TuiEffect" />: maps a concrete effect type
///     to the handler that runs it. A missing mapping is a no-op, matching the
///     former switch's default behaviour for unknown / <see cref="TuiEffect.None" />
///     effects.
/// </summary>
internal sealed class TuiEffectHandlerRegistry
{
    private readonly Dictionary<Type, Action<TuiEffect>> _handlers = new();

    public void Register<TEffect>(ITuiEffectHandler<TEffect> handler)
        where TEffect : TuiEffect
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handlers[typeof(TEffect)] = effect => handler.Execute((TEffect)effect);
    }

    public bool TryExecute(TuiEffect effect)
    {
        if (effect is null || !_handlers.TryGetValue(effect.GetType(), out var handler))
        {
            return false;
        }

        handler(effect);
        return true;
    }
}

/// <summary>
///     Default <see cref="ITuiEffectRunner" />. The ONLY place that touches
///     <see cref="IAgentRunner" /> / the slash handler. Renderers stay free of
///     the Application layer and instead emit <see cref="TuiEffect" />.
/// </summary>
/// <remarks>
///     <para>
///         Effects run on the thread pool. Prompt/abort feed agent events back into
///         the supplied <see cref="UiStore" /> so the UI state stays in sync without
///         the renderer polling <c>IAgent.State</c>.
///     </para>
///     <para>
///         <b>Layering (§ARCH-002):</b> depends on <see cref="IAgentRunner" /> (the
///         minimal runner surface in <c>Harbor.Abstractions</c>), not the full
///         <see cref="IAgent" />. This keeps <c>Harbor.Terminal.Abstractions</c> in the
///         Domain layer — it never needs to reference <c>Harbor.Application</c> for agent
///         types. <see cref="IAgent" /> extends <see cref="IAgentRunner" />, so the
///         Composition Root (<c>HostBuilder</c>) can pass a concrete <c>DefaultAgent</c>
///         wherever an <c>IAgentRunner</c> is required.
///     </para>
/// </remarks>
public sealed class TuiEffectHost : ITuiEffectRunner
{
    private readonly IAgentRunner _agent;
    private readonly CancellationToken _appCt;
    private readonly ILogger<TuiEffectHost>? _logger;
    private readonly Func<string, Task>? _slash;
    private readonly IApprovalCoordinator? _coordinator;
    private readonly Func<string, Task>? _activateSession;
    private readonly Func<Task>? _openSession;

    // Command dispatch: one handler per effect type; Run only looks up + executes.
    private readonly TuiEffectHandlerRegistry _handlers;

    // #91: rebindable mid-flight via RebindStore. Every async effect captures
    // its store once at entry (a volatile read) and dispatches the whole run
    // into that snapshot, so a session switch can never redirect an in-flight
    // run's terminal messages into the new session's store.
    private UiStore _store;

    public TuiEffectHost(
        IAgentRunner agent,
        UiStore store,
        Func<string, Task>? slash = null,
        CancellationToken appCt = default,
        ILogger<TuiEffectHost>? logger = null,
        IApprovalCoordinator? coordinator = null,
        Func<string, Task>? activateSession = null,
        Func<Task>? openSession = null)
    {
        _agent = agent;
        _store = store;
        _slash = slash;
        _appCt = appCt;
        _logger = logger;
        _coordinator = coordinator;
        _activateSession = activateSession;
        _openSession = openSession;

        _handlers = new TuiEffectHandlerRegistry();
        _handlers.Register(new PromptAgentHandler(this));
        _handlers.Register(new RunSlashHandler(this));
        _handlers.Register(new AbortAgentHandler(this));
        _handlers.Register(new QuitAppHandler(this));
        _handlers.Register(new ActivateSessionHandler(this));
        _handlers.Register(new RequestOpenSessionHandler(this));
    }

    public void RebindStore(UiStore newStore)
    {
        Harbor.Abstractions.Extensions.Ensure.NotNull(newStore);
        _store = newStore;
    }

    /// <summary>Slash command list for input autocomplete.</summary>
    public static ImmutableArray<string> KnownSlashCommands => ChatCommands.Slash;

    /// <summary>
    ///     Dispatch an effect to its registered handler. Lookup + execute only,
    ///     with no per-effect arms, so adding an effect never edits this method.
    ///     <para>
    ///         §FP-006 (RESOLVED): each fire-and-forget async branch lives in its
    ///         handler and goes through the shared TaskFireAndForget helper (task
    ///         tracking + OnlyOnFaulted log). The Run contract stays synchronous
    ///         (per ITuiEffectRunner.Run): unobserved-task exceptions surface via
    ///         _logger instead of dying in TaskScheduler.UnobservedTaskException.
    ///     </para>
    ///     <para>
    ///         Architecture audit v2 §3.4 (CT-002 RESOLVED): the inner async methods
    ///         (PromptAsync, RunSlashAsync, AbortAsync) catch
    ///         OperationCanceledException FIRST and route it to a clean "idle"
    ///         transition instead of letting the generic Exception catch set the
    ///         status to "error". An Esc-abort is no longer misreported as a failure.
    ///     </para>
    /// </summary>
    public void Run(TuiEffect effect)
    {
        _handlers.TryExecute(effect);
    }

    /// <summary>
    ///     Register (or replace) the handler for an effect type. Lets callers and
    ///     tests add a new effect without editing <see cref="Run" />. The registry
    ///     is keyed by the concrete <see cref="TuiEffect" /> runtime type.
    /// </summary>
    public void RegisterEffectHandler<TEffect>(ITuiEffectHandler<TEffect> handler)
        where TEffect : TuiEffect
        => _handlers.Register(handler);

    // Command handlers. Each owns exactly one former switch arm, including the
    // fire-and-forget / logging shape, and reaches the host's dependencies through
    // its instance. The async methods below stay unchanged.
    private sealed class PromptAgentHandler(TuiEffectHost host)
        : ITuiEffectHandler<TuiEffect.PromptAgent>
    {
        public void Execute(TuiEffect.PromptAgent effect) =>
            TaskFireAndForget.Forget(
                host.PromptAsync(effect.Text),
                ex => { if (host._logger is not null) TuiEffectHostLog.PromptEffectFailed(host._logger, ex); });
    }

    private sealed class RunSlashHandler(TuiEffectHost host)
        : ITuiEffectHandler<TuiEffect.RunSlash>
    {
        public void Execute(TuiEffect.RunSlash effect) =>
            TaskFireAndForget.Forget(
                host.RunSlashAsync(effect.Command),
                ex => { if (host._logger is not null) TuiEffectHostLog.RunSlashFailed(host._logger, ex, effect.Command); });
    }

    private sealed class AbortAgentHandler(TuiEffectHost host)
        : ITuiEffectHandler<TuiEffect.AbortAgent>
    {
        public void Execute(TuiEffect.AbortAgent effect) =>
            TaskFireAndForget.Forget(
                host.AbortAsync(),
                ex => { if (host._logger is not null) TuiEffectHostLog.AbortFailed(host._logger, ex); });
    }

    private sealed class QuitAppHandler(TuiEffectHost host)
        : ITuiEffectHandler<TuiEffect.QuitApp>
    {
        public void Execute(TuiEffect.QuitApp effect) =>
            host._store.Dispatch(new AppMsg.Quit());
    }

    private sealed class ActivateSessionHandler(TuiEffectHost host)
        : ITuiEffectHandler<TuiEffect.ActivateSession>
    {
        public void Execute(TuiEffect.ActivateSession effect)
        {
            // Tab-strip activate (#388). The session switch itself lives in the
            // composition root (ISessionManager.OpenSessionAsync) and arrives as a
            // delegate; hosts that never switch sessions leave it null and the tab
            // focus is then a pure state change.
            if (host._activateSession is null)
            {
                if (host._logger is not null) TuiEffectHostLog.ActivateSessionIgnored(host._logger, effect.SessionId.Value);
                return;
            }

            TaskFireAndForget.Forget(
                host._activateSession(effect.SessionId.Value),
                ex => { if (host._logger is not null) TuiEffectHostLog.ActivateSessionFailed(host._logger, ex, effect.SessionId.Value); });
        }
    }

    private sealed class RequestOpenSessionHandler(TuiEffectHost host)
        : ITuiEffectHandler<TuiEffect.RequestOpenSession>
    {
        public void Execute(TuiEffect.RequestOpenSession effect)
        {
            // Opening a session from the strip (issue #389). Same delegate shape
            // as the activate case above: the picker lives in the composition
            // root, so a host without one just logs and the key stays harmless.
            if (host._openSession is null)
            {
                if (host._logger is not null) TuiEffectHostLog.RequestOpenSessionIgnored(host._logger);
                return;
            }

            TaskFireAndForget.Forget(
                host._openSession(),
                ex => { if (host._logger is not null) TuiEffectHostLog.RequestOpenSessionFailed(host._logger, ex); });
        }
    }


    private async Task PromptAsync(string text)
    {
        // #91: per-run store capture — a RebindStore landing mid-flight must
        // not redirect this run's dispatches into the new session's store.
        var store = Volatile.Read(ref _store);
        store.Dispatch(new ChatAppMsg.AgentStarted());

        // #91: single terminal end. The failure branches used to dispatch
        // AgentEnded("error", ...) AND fall through into the finally's
        // AgentEnded() (double end); the success path's finally dispatch
        // additionally folds on top of the AgentEndEvent-driven update. The
        // flag makes this method emit exactly one terminal message per run.
        // (The cancel branch keeps its StatusChanged("idle") shape — the abort
        // ingress in AbortAsync owns the terminal AgentEnded("idle") there.)
        var ended = false;
        void EndOnce(ChatAppMsg.AgentEnded msg)
        {
            if (!ended)
            {
                ended = true;
                store.Dispatch(msg);
            }
        }

        try
        {
            var result = await _agent.PromptAsync(text, _appCt).ConfigureAwait(false);

            // CRITICAL: check Result — if failure, set status to "error" and
            // reset IsAgentRunning so the UI doesn't hang in "thinking" forever.
            if (result.IsFailure)
            {
                if (_logger is not null) TuiEffectHostLog.AgentFailed(_logger, result.Error);
                EndOnce(new ChatAppMsg.AgentEnded("error", result.Error));
            }
        }
        catch (OperationCanceledException) when (_appCt.IsCancellationRequested)
        {
            // Architecture audit v2 §3.4 (CT-002 RESOLVED): an Esc-abort
            // cancels _appCt, which surfaces as OperationCanceledException
            // from _agent.PromptAsync. The previous generic Exception
            // catch treated this as an error and set the status bar to
            // "error". The user just wanted to abort — route to a clean
            // "idle" transition instead.
            store.Dispatch(new ChatAppMsg.StatusChanged("idle"));
        }
        catch (Exception ex)
        {
            if (_logger is not null) TuiEffectHostLog.PromptFailed(_logger, ex);
            EndOnce(new ChatAppMsg.AgentEnded("error"));
        }
        finally
        {
            // Ensure IsAgentRunning is always reset — even on success path
            // where the agent loop might not have published AgentEndEvent yet.
            EndOnce(new ChatAppMsg.AgentEnded());
        }
    }

    private async Task RunSlashAsync(string command)
    {
        // #91: per-run store capture (see PromptAsync).
        var store = Volatile.Read(ref _store);
        if (_slash is null)
        {
            store.Dispatch(new ChatAppMsg.AppendLine(ChatRole.Error, $"no handler for {command}"));
            return;
        }

        try
        {
            await _slash(command).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_appCt.IsCancellationRequested)
        {
            // §3.4: same rationale as PromptAsync — abort ≠ error.
            store.Dispatch(new ChatAppMsg.StatusChanged("idle"));
        }
        catch (Exception ex)
        {
            store.Dispatch(new ChatAppMsg.AppendLine(ChatRole.Error, ex.Message));
        }
    }

    private async Task AbortAsync()
    {
        // #91: per-run store capture — the terminal AgentEnded("idle") below
        // belongs to the session that owned the abort, even if a rebind lands
        // while WaitForIdleAsync is parked.
        var store = Volatile.Read(ref _store);
        // #49 PR1: single cancellation ingress. Null (tests, contrib hosts
        // without the coordinator) falls back to the direct cancel.
        if (_coordinator is not null)
        {
            _coordinator.RequestCancel(_agent);
        }
        else
        {
            _agent.RequestAbort();
        }
        try
        {
            // Bound the wait so a misbehaving agent loop can't hang the UI
            // forever — 5s is generous (the agent's only safe boundaries are
            // between turns / during streaming awaits, both sub-second). The
            // session manager uses a 3s bound on its own switches; here we
            // give the user-initiated Stop a slightly longer fuse.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _agent.WaitForIdleAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_appCt.IsCancellationRequested)
        {
            // Expected when the abort token fires; the agent is idle now.
            // §3.4: do not propagate as an error.
        }
        catch (Exception ex)
        {
            store.Dispatch(new ChatAppMsg.AppendLine(ChatRole.Error, ex.Message));
        }

        // Recreate the abort source so the next PromptAsync call observes a
        // live, un-cancelled token. Without this, a single Stop click would
        // permanently disable sending — every subsequent prompt would observe
        // IsCancellationRequested=true and immediately throw
        // OperationCanceledException.
        // #91: the reset is generation-guarded — if the bounded wait above
        // timed out while the run is still alive, this is a no-op and the next
        // PromptAsync self-heals once idle instead of orphaning the zombie run.
        _agent.ResetAbortSource();

        store.Dispatch(new ChatAppMsg.AgentEnded("idle"));
    }
}

/// <summary>
///     SG11: BCL <c>[LoggerMessage]</c> delegates for <see cref="TuiEffectHost" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class TuiEffectHostLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "PromptAsync failed")]
    public static partial void PromptEffectFailed(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "RunSlashAsync failed for {Command}")]
    public static partial void RunSlashFailed(ILogger logger, Exception ex, string command);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "AbortAsync failed")]
    public static partial void AbortFailed(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 4, Level = LogLevel.Debug, Message = "ActivateSession({SessionId}) ignored: no activator wired")]
    public static partial void ActivateSessionIgnored(ILogger logger, string sessionId);

    [LoggerMessage(EventId = 5, Level = LogLevel.Error, Message = "ActivateSession failed for {SessionId}")]
    public static partial void ActivateSessionFailed(ILogger logger, Exception ex, string sessionId);

    [LoggerMessage(EventId = 6, Level = LogLevel.Debug, Message = "RequestOpenSession ignored: no session picker wired")]
    public static partial void RequestOpenSessionIgnored(ILogger logger);

    [LoggerMessage(EventId = 7, Level = LogLevel.Error, Message = "RequestOpenSession failed")]
    public static partial void RequestOpenSessionFailed(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 8, Level = LogLevel.Error, Message = "Agent failed: {Error}")]
    public static partial void AgentFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 9, Level = LogLevel.Error, Message = "PromptAsync failed")]
    public static partial void PromptFailed(ILogger logger, Exception ex);
}
