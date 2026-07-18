using System.Threading.Channels;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Microsoft.Extensions.Logging;

namespace Harbor.Ipc.InProcess;

/// <summary>
///     Default <see cref="IHarborClient" /> implementation. Calls
///     <see cref="IAgent" />, <see cref="ISessionStore" />,
///     <see cref="IProviderRegistry" />, <see cref="IToolRegistry" /> and
///     <see cref="IEventBus" /> directly — no serialization, no network.
/// </summary>
/// <remarks>
///     <para>
///         This is the implementation used when <c>HARBOR_MODE=inprocess</c>
///         (the default). The UI layer gets the exact same
///         <see cref="IHarborClient" /> surface as the IPC variant, so
///         switching modes is a one-line DI change.
///     </para>
///     <para>
///         <b>Event bridging:</b> <see cref="SubscribeToEventsAsync" />
///         subscribes to <see cref="IEventBus" /> and projects the rich
///         <see cref="AgentEvent" /> hierarchy down to the wire-stable
///         <see cref="HarborEvent" /> union via an internal channel.
///     </para>
///     <para>
///         <b>Thread safety:</b> the underlying <see cref="IAgent" /> is
///         single-flight (<c>PromptAsync</c> returns failure if a run is
///         already in flight). All other methods delegate to thread-safe
///         registries.
///     </para>
/// </remarks>
public sealed class InProcessHarborClient : IHarborClient
{
    private readonly IAgent _agent;
    private readonly IAgentRegistry _agents;
    private readonly IEventBus _eventBus;
    private readonly ILogger<InProcessHarborClient> _logger;
    private readonly IProviderRegistry _providers;
    private readonly ISessionStore _sessionStore;
    private readonly IToolRegistry _tools;
    private readonly Channel<HarborEvent> _eventChannel;
    private readonly IDisposable _eventBusSubscription;
    private readonly CancellationTokenSource _eventBusCts = new();
    private int _currentTurn;
    private int _disposed;

    /// <summary>
    ///     Construct an in-process client wired to the supplied services.
    /// </summary>
    /// <param name="agent">The agent (single-flight runner).</param>
    /// <param name="agents">The agent registry (lookup by name).</param>
    /// <param name="sessionStore">The session store (CRUD + messages).</param>
    /// <param name="providers">The provider registry.</param>
    /// <param name="tools">The tool registry.</param>
    /// <param name="eventBus">The event bus (source of streaming events).</param>
    /// <param name="logger">Logger.</param>
    public InProcessHarborClient(
        IAgent agent,
        IAgentRegistry agents,
        ISessionStore sessionStore,
        IProviderRegistry providers,
        IToolRegistry tools,
        IEventBus eventBus,
        ILogger<InProcessHarborClient> logger)
    {
        _agent = agent;
        _agents = agents;
        _sessionStore = sessionStore;
        _providers = providers;
        _tools = tools;
        _eventBus = eventBus;
        _logger = logger;

        // Bounded channel: backpressure if the consumer can't keep up.
        // Drop-oldest keeps the latest events visible (matches TUI semantics).
        _eventChannel = Channel.CreateBounded<HarborEvent>(new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = false
        });

        _eventBusSubscription = _eventBus.Subscribe(OnEventBusEventAsync);
    }

    /// <inheritdoc />
    public bool IsConnected => Volatile.Read(ref _disposed) == 0;

    /// <inheritdoc />
    public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;

    // ── Agent ──────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<Result> StartAgentAsync(string sessionId, string agentName, CancellationToken ct = default)
    {
        var nameResult = AgentName.TryCreate(agentName);
        if (nameResult.IsFailure)
            return Result.Failure(nameResult.Error);

        var agentDefResult = _agents.GetAgent(nameResult.Value);
        if (agentDefResult.IsFailure)
            return Result.Failure(agentDefResult.Error);

        var sessionResult = await _sessionStore.GetAsync(sessionId, ct).ConfigureAwait(false);
        if (sessionResult.IsFailure)
            return Result.Failure(sessionResult.Error);

        _agent.Initialize(sessionResult.Value, agentDefResult.Value);
        _logger.LogInformation("Agent started: session={SessionId} agent={Agent}", sessionId, agentName);
        return Result.Success();
    }

    /// <inheritdoc />
    public Task<Result> AbortAgentAsync(CancellationToken ct = default)
    {
        _agent.AbortSource.Cancel();
        return Task.FromResult(Result.Success());
    }

    /// <inheritdoc />
    public async Task<Result> SendPromptAsync(string prompt, CancellationToken ct = default)
    {
        return await _agent.PromptAsync(prompt, ct).ConfigureAwait(false);
    }

    // ── Sessions ───────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<Result<Session>> CreateSessionAsync(
        string dir, string agent, string provider, string model, CancellationToken ct = default)
    {
        return await _sessionStore.CreateAsync(dir, agent, provider, model, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<Session>>> ListSessionsAsync(CancellationToken ct = default)
    {
        return await _sessionStore.ListAsync(null, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<Session>> GetSessionAsync(string sessionId, CancellationToken ct = default)
    {
        return await _sessionStore.GetAsync(sessionId, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result> DeleteSessionAsync(string sessionId, CancellationToken ct = default)
    {
        return await _sessionStore.DeleteAsync(sessionId, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(
        string sessionId, CancellationToken ct = default)
    {
        return await _sessionStore.GetMessagesAsync(sessionId, ct).ConfigureAwait(false);
    }

    // ── Providers ──────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<ProviderId>>> ListProvidersAsync(CancellationToken ct = default)
    {
        var ids = _providers.GetRegisteredProviderIds();
        return Task.FromResult(Result.Success(ids));
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<ModelInfo>>> ListModelsAsync(
        string? providerId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(providerId))
        {
            return await _providers.GetAllModelsAsync(ct).ConfigureAwait(false);
        }

        var pidResult = ProviderId.TryCreate(providerId);
        if (pidResult.IsFailure)
            return Result.Failure<IReadOnlyList<ModelInfo>>(pidResult.Error);

        var clientResult = _providers.GetClient(pidResult.Value);
        if (clientResult.IsFailure)
            return Result.Failure<IReadOnlyList<ModelInfo>>(clientResult.Error);

        return await clientResult.Value.GetModelsAsync(ct).ConfigureAwait(false);
    }

    // ── Tools ──────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<ToolDescriptor>>> ListToolsAsync(CancellationToken ct = default)
    {
        var tools = _tools.GetAllTools();
        return Task.FromResult(Result.Success(tools));
    }

    // ── Streaming events ───────────────────────────────────────────────────

    /// <inheritdoc />
    public async IAsyncEnumerable<HarborEvent> SubscribeToEventsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _eventBusCts.Token);
        await foreach (var evt in _eventChannel.Reader.ReadAllAsync(linked.Token).ConfigureAwait(false))
        {
            yield return evt;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _eventBusSubscription?.Dispose();
        _eventBusCts.Cancel();
        _eventBusCts.Dispose();
        _eventChannel.Writer.TryComplete();
        await _eventChannel.Reader.Completion.ConfigureAwait(false);
    }

    // ── Internal: AgentEvent → HarborEvent projection ──────────────────────

    private async ValueTask OnEventBusEventAsync(AgentEvent evt, CancellationToken ct)
    {
        HarborEvent? projected = ProjectEvent(evt);
        if (projected is null) return;
        await _eventChannel.Writer.WriteAsync(projected, ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Map a domain <see cref="AgentEvent" /> to a wire-stable
    ///     <see cref="HarborEvent" />. Returns <see langword="null" /> for
    ///     event kinds that have no projection (e.g. <c>SessionStatsEvent</c>
    ///     — not yet in the wire union).
    /// </summary>
    internal HarborEvent? ProjectEvent(AgentEvent evt)
    {
        return evt switch
        {
            AgentStartEvent e => ResetTurnAndProject(e.SessionId),
            MessageStartEvent => null, // start implied by first MessageUpdate
            MessageUpdateEvent e => new HarborEvent.MessageUpdate(e.Partial, ExtractDelta(e.LlmEvent)),
            MessageEndEvent e => new HarborEvent.MessageEnd(e.Message),
            ToolExecutionStartEvent e => new HarborEvent.ToolStart(e.ToolCallId, e.ToolName),
            ToolExecutionEndEvent e => new HarborEvent.ToolEnd(e.ToolCallId, e.Result),
            TurnStartEvent e => TrackTurnAndProject(e.TurnIndex),
            TurnEndEvent => new HarborEvent.TurnEnd(_currentTurn),
            AgentEndEvent => null, // AgentEnded emitted separately by the agent runner hook
            AgentErrorEvent e => new HarborEvent.AgentError(e.Message),
            CompactionStartedEvent e => new HarborEvent.CompactionStarted(e.SessionId),
            CompactionCompletedEvent e => new HarborEvent.CompactionCompleted(
                e.SessionId, e.PrunedMessageCount, e.TokensSaved),
            SessionStatsEvent => null,
            _ => null
        };
    }

    private HarborEvent ResetTurnAndProject(string sessionId)
    {
        _currentTurn = 0;
        return new HarborEvent.AgentStarted(sessionId);
    }

    private HarborEvent TrackTurnAndProject(int turnIndex)
    {
        _currentTurn = turnIndex;
        return new HarborEvent.TurnStart(turnIndex);
    }

    private static string ExtractDelta(LlmEvent llmEvent)
    {
        return llmEvent switch
        {
            TextDeltaEvent e => e.Delta,
            ThinkingDeltaEvent e => e.Delta,
            ToolCallDeltaEvent e => e.ArgsDelta,
            _ => string.Empty
        };
    }
}
