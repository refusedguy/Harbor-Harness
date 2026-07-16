using System.Threading.Channels;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Sessions;
using Microsoft.Extensions.Logging;

namespace Harbor.Core.Agents;

/// <summary>
/// Default IAgent implementation. Stateful wrapper around <see cref="AgentLoop"/>.
/// Implements Command pattern (GOF) — encapsulates prompt submission and execution.
/// </summary>
public sealed class DefaultAgent : IAgent
{
    private readonly ISessionStore _sessionStore;
    private readonly IAgentLoop _agentLoop;
    private readonly IEventBus _eventBus;
    private readonly ILogger<DefaultAgent> _logger;
    private readonly Channel<AgentMessage> _steeringQueue;
    private readonly Channel<AgentMessage> _followUpQueue;
    private readonly List<Func<AgentEvent, CancellationToken, ValueTask>> _listeners = new();
    private readonly object _listenersLock = new();
    private readonly IDisposable _eventBusSubscription;
    private TaskCompletionSource<Result> _runCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Construct a <see cref="DefaultAgent"/> wired to the supplied services.
    /// </summary>
    /// <param name="sessionStore">The session store for loading session context.</param>
    /// <param name="agentLoop">The agent loop to drive.</param>
    /// <param name="eventBus">The event bus to subscribe to.</param>
    /// <param name="logger">The logger.</param>
    /// <summary>
    /// Construct a <see cref="DefaultAgent"/> wired to the supplied services.
    /// </summary>
    /// <param name="sessionStore">The session store for loading session context.</param>
    /// <param name="agentLoop">The agent loop to drive.</param>
    /// <param name="eventBus">The event bus to subscribe to.</param>
    /// <param name="logger">The logger.</param>
    public DefaultAgent(
        ISessionStore sessionStore,
        IAgentLoop agentLoop,
        IEventBus eventBus,
        ILogger<DefaultAgent> logger)
    {
        _sessionStore = sessionStore;
        _agentLoop = agentLoop;
        _eventBus = eventBus;
        _logger = logger;

        _steeringQueue = Channel.CreateUnbounded<AgentMessage>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        _followUpQueue = Channel.CreateUnbounded<AgentMessage>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        _eventBusSubscription = _eventBus.Subscribe(async (evt, ct) =>
        {
            List<Func<AgentEvent, CancellationToken, ValueTask>> snapshot;
            lock (_listenersLock)
                snapshot = _listeners.ToList();

            foreach (var listener in snapshot)
            {
                try
                {
                    await listener(evt, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Listener failed");
                }
            }
        });
    }

    /// <summary>
    /// Current agent state snapshot. <see langword="null"/> until <see cref="Initialize"/> is called.
    /// </summary>
    public AgentState State { get; private set; } = null!;

    /// <summary>
    /// Cancellation token source used to abort the current run.
    /// </summary>
    public CancellationTokenSource AbortSource { get; } = new();

    /// <summary>
    /// Subscribe a listener to all agent events. The listener is invoked for every event
    /// published to the <see cref="IEventBus"/> by this agent's run.
    /// </summary>
    /// <param name="listener">Async callback.</param>
    /// <returns>A disposable that unsubscribes on dispose.</returns>
    public IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> listener)
    {
        lock (_listenersLock)
            _listeners.Add(listener);

        return new Unsubscriber(() =>
        {
            lock (_listenersLock)
                _listeners.Remove(listener);
        });
    }

    /// <summary>
    /// Submit a plain-text prompt and run the agent loop to completion. Throws-free: returns
    /// <see cref="Result.Failure"/> if the agent is already running or not initialized.
    /// </summary>
    /// <param name="text">The user's prompt text.</param>
    /// <param name="ct">Optional cancellation token linked to <see cref="AbortSource"/>.</param>
    /// <returns>Success on completion, or failure with an error message.</returns>
    public async Task<Result> PromptAsync(string text, CancellationToken ct = default)
    {
        if (State?.IsRunning == true)
            return Result.Failure("Agent is already running. Use Steer() to interrupt or wait for completion.");

        if (State is null)
            return Result.Failure("Agent is not initialized. Call InitializeAsync first.");

        var userMessage = new UserMessage(
            Id: Guid.NewGuid().ToString("N"),
            SessionId: State.SessionId,
            CreatedAt: DateTimeOffset.UtcNow,
            Content: text,
            Agent: State.Agent.Name.Value,
            Model: State.Agent.Model);

        return await PromptAsync(userMessage, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Submit a pre-built <see cref="UserMessage"/> and run the agent loop to completion.
    /// </summary>
    /// <param name="message">The user message to submit.</param>
    /// <param name="ct">Optional cancellation token linked to <see cref="AbortSource"/>.</param>
    /// <returns>Success on completion, or failure with an error message.</returns>
    public async Task<Result> PromptAsync(UserMessage message, CancellationToken ct = default)
    {
        if (State?.IsRunning == true)
            return Result.Failure("Agent is already running.");

        if (State is null)
            return Result.Failure("Agent is not initialized.");

        await _sessionStore.AppendMessageAsync(State.SessionId, message, ct).ConfigureAwait(false);

        State = State with { IsRunning = true, StartedAt = DateTimeOffset.UtcNow };

        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(AbortSource.Token, ct);

        try
        {
            var session = await LoadSessionContextAsync(State.SessionId, ct).ConfigureAwait(false);
            var result = await _agentLoop.RunAsync(session, State.Agent, linkedCts.Token).ConfigureAwait(false);

            State = State with
            {
                IsRunning = false,
                LastActivityAt = DateTimeOffset.UtcNow,
            };

            return result;
        }
        catch (OperationCanceledException) when (linkedCts.Token.IsCancellationRequested)
        {
            State = State with { IsRunning = false, LastActivityAt = DateTimeOffset.UtcNow };
            return Result.Failure("Agent was cancelled.");
        }
        catch (Exception ex)
        {
            State = State with { IsRunning = false, LastActivityAt = DateTimeOffset.UtcNow };
            _logger.LogError(ex, "Agent run failed");
            return Result.Failure(ex.Message);
        }
        finally
        {
            linkedCts.Dispose();
        }
    }

    /// <summary>
    /// Inject a steering message into the current run. The message is processed at the next
    /// safe boundary (between turns).
    /// </summary>
    /// <param name="message">The message to inject.</param>
    public void Steer(AgentMessage message)
    {
        _steeringQueue.Writer.TryWrite(message);
    }

    /// <summary>
    /// Queue a follow-up message to be processed after the current run completes.
    /// </summary>
    /// <param name="message">The message to queue.</param>
    public void FollowUp(AgentMessage message)
    {
        _followUpQueue.Writer.TryWrite(message);
    }

    /// <summary>
    /// Wait for the agent to become idle (no <see cref="PromptAsync"/> in flight).
    /// </summary>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>A task that completes when the agent is idle.</returns>
    public Task WaitForIdleAsync(CancellationToken ct = default)
    {
        return State?.IsRunning == true
            ? Task.Run(async () => await _runCompletion.Task.ConfigureAwait(false), ct)
            : Task.CompletedTask;
    }

    /// <summary>
    /// Bind this agent to a session + agent definition. Must be called before the first
    /// <see cref="PromptAsync"/> call.
    /// </summary>
    /// <param name="session">The session to bind to.</param>
    /// <param name="agent">The agent definition to use.</param>
    public void Initialize(Session session, AgentDefinition agent)
    {
        State = AgentState.Idle(session.Id, agent);
    }

    private async Task<ISessionContext> LoadSessionContextAsync(string sessionId, CancellationToken ct)
    {
        var session = await _sessionStore.GetAsync(sessionId, ct).ConfigureAwait(false);
        if (session.IsFailure)
            throw new InvalidOperationException(session.Error);

        var messages = await _sessionStore.GetMessagesAsync(sessionId, ct).ConfigureAwait(false);
        if (messages.IsFailure)
            throw new InvalidOperationException(messages.Error);

        return new DefaultSessionContext(session.Value, messages.Value, _sessionStore, _steeringQueue);
    }

    /// <summary>
    /// Release all resources held by this agent: event-bus subscription, abort source,
    /// steering/follow-up channels.
    /// </summary>
    public void Dispose()
    {
        _eventBusSubscription?.Dispose();
        AbortSource?.Dispose();
        _steeringQueue.Writer.TryComplete();
        _followUpQueue.Writer.TryComplete();
    }

    private sealed class Unsubscriber : IDisposable
    {
        private Action? _action;
        public Unsubscriber(Action action) => _action = action;
        public void Dispose()
        {
            _action?.Invoke();
            _action = null;
        }
    }
}

/// <summary>
/// Default session context implementation.
/// </summary>
internal sealed class DefaultSessionContext : ISessionContext
{
    private readonly ISessionStore _store;
    private readonly List<AgentMessage> _messages;
    private readonly Channel<AgentMessage> _steeringQueue;

    public DefaultSessionContext(
        Session session,
        IReadOnlyList<AgentMessage> messages,
        ISessionStore store,
        Channel<AgentMessage> steeringQueue)
    {
        Session = session;
        _messages = messages.ToList();
        _store = store;
        _steeringQueue = steeringQueue;
    }

    public Session Session { get; }

    public IReadOnlyList<AgentMessage> Messages => _messages;

    public Channel<AgentMessage> SteeringQueue => _steeringQueue;

    public async Task AppendMessageAsync(AgentMessage message, CancellationToken ct = default)
    {
        _messages.Add(message);
        await _store.AppendMessageAsync(Session.Id, message, ct).ConfigureAwait(false);
    }

    public async Task UpdateStatsAsync(Usage usage, CancellationToken ct = default)
    {
        var stats = await _store.GetStatsAsync(Session.Id, ct).ConfigureAwait(false);
        if (stats.IsFailure)
        {
            _ = stats.Error;
            return;
        }

        var updated = stats.Value.AddUsage(usage);
        await _store.UpdateStatsAsync(Session.Id, updated, ct).ConfigureAwait(false);
    }
}
