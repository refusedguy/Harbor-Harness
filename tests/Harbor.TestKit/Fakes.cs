using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Abstractions.Providers;

namespace Harbor.TestKit;

/// <summary>Registry containing exactly the given agents (P.6 canonical fake).</summary>
public sealed class FakeAgentRegistry(params AgentDefinition[] agents) : IAgentRegistry
{
    private readonly Dictionary<string, AgentDefinition> _agents =
        agents.ToDictionary(a => a.Name.Value, StringComparer.Ordinal);

    public IReadOnlyList<AgentDefinition> GetAllAgents() => [.. _agents.Values];

    public Result<AgentDefinition> GetAgent(AgentName name) =>
        _agents.TryGetValue(name.Value, out AgentDefinition? definition)
            ? Result.Success(definition)
            : Result.Failure<AgentDefinition>($"Agent '{name.Value}' is not registered.");

    public Result Register(AgentDefinition agent) =>
        _agents.TryAdd(agent.Name.Value, agent)
            ? Result.Success()
            : Result.Failure($"Agent '{agent.Name.Value}' is already registered.");

    public Result Unregister(AgentName name) =>
        _agents.Remove(name.Value)
            ? Result.Success()
            : Result.Failure($"Agent '{name.Value}' is not registered.");
}

/// <summary>In-memory tool registry over the given tools.</summary>
public sealed class FakeToolRegistry(params ITool[] tools) : IToolRegistry
{
    private readonly Dictionary<string, ITool> _tools =
        tools.ToDictionary(t => t.Name.Value, StringComparer.Ordinal);

    public IReadOnlyList<ToolDescriptor> GetAllTools() => Snapshot();

    public IReadOnlyList<ToolDescriptor> ResolveTools(string agentName, PermissionRuleset? sessionPermission = null)
        => Snapshot();

    public Result<ITool> GetTool(ToolName name) =>
        _tools.TryGetValue(name.Value, out ITool? tool)
            ? Result.Success(tool)
            : Result.Failure<ITool>($"Unknown tool '{name.Value}'.");

    public Result Register(ITool tool) =>
        _tools.TryAdd(tool.Name.Value, tool)
            ? Result.Success()
            : Result.Failure($"Tool '{tool.Name.Value}' is already registered.");

    public Result Unregister(ToolName name) =>
        _tools.Remove(name.Value)
            ? Result.Success()
            : Result.Failure($"Tool '{name.Value}' is not registered.");

    private IReadOnlyList<ToolDescriptor> Snapshot()
    {
        var list = new List<ToolDescriptor>(_tools.Values.Count);
        foreach (ITool t in _tools.Values)
        {
            list.Add(new ToolDescriptor(
                t.Name, t.DisplayName, t.Description, t.ParameterSchema,
                t.ExecutionMode, t.PromptSnippet, t.PromptGuidelines));
        }

        return list;
    }
}

/// <summary>Tool that counts executions and records raw args — a canary for permission/timeout tests.</summary>
public sealed class CountingTool : ITool
{
    private readonly object _lock = new();
    private int _executions;

    public int Executions => Volatile.Read(ref _executions);

    public List<string> ExecutedArgs { get; } = [];

    public ToolName Name => ToolName.Create("counter");

    public string DisplayName => "Counter";

    public string Description => "Counts executions.";

    public JsonDocument ParameterSchema { get; } = JsonDocument.Parse(
        """{"type":"object","properties":{"n":{"type":"number"}}}""");

    public ExecutionMode ExecutionMode => ExecutionMode.Parallel;

    public string? PromptSnippet => null;

    public IReadOnlyList<string> PromptGuidelines => [];

    public Result ValidateArguments(JsonElement args) => Result.Success();

    public Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _executions);
        lock (_lock)
        {
            ExecutedArgs.Add(args.GetRawText());
        }

        return Task.FromResult(ToolResult.Success("counted"));
    }
}

/// <summary>In-memory provider registry returning the same client for any id.</summary>
public sealed class FakeProviderRegistry(ILlmClient client) : IProviderRegistry
{
    public IReadOnlyList<ProviderId> GetRegisteredProviderIds() => [client.ProviderId];

    public Result<ILlmClient> GetClient(ProviderId providerId) => Result.Success(client);

    public Task<Result<IReadOnlyList<ModelInfo>>> GetAllModelsAsync(CancellationToken cancellationToken = default)
        => client.GetModelsAsync(cancellationToken);

    public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsCachedAsync(ProviderId providerId, CancellationToken cancellationToken = default)
        => client.GetModelsAsync(cancellationToken);

    public void Register(ProviderId providerId, Func<ILlmClient> factory)
    {
    }

    public Result Unregister(ProviderId providerId) => Result.Failure("FakeProviderRegistry does not support unregister.");
}

/// <summary>In-memory session store with optional pre-seeded session and gate support for concurrency tests.</summary>
public sealed class FakeSessionStore(Session? session = null) : ISessionStore
{
    private readonly List<AgentMessage> _messages = [];
    private readonly object _lock = new();
    private TaskCompletionSource? _gatedAppend;
    private int _appends;

    public int Appends => Volatile.Read(ref _appends);

    public string? LastCreatedDirectory { get; private set; }

    public void GateNextAppend(TaskCompletionSource gate)
    {
        lock (_lock)
        {
            _gatedAppend = gate;
        }
    }

    public Task<Result<Session>> CreateAsync(string directory, string agentName, string providerId, string modelId, CancellationToken ct = default)
    {
        LastCreatedDirectory = directory;
        Session created = session ?? Session.Create(directory, agentName, providerId, modelId);
        return Task.FromResult(Result.Success(created));
    }

    public Task<Result<Session>> GetAsync(string sessionId, CancellationToken ct = default)
    {
        if (session is null) return Task.FromResult(Result.Failure<Session>("No session configured."));
        return session.Id == sessionId
            ? Task.FromResult(Result.Success(session))
            : Task.FromResult(Result.Failure<Session>($"Session '{sessionId}' was not found."));
    }

    public Task<Result<IReadOnlyList<Session>>> ListAsync(string? projectId = null, CancellationToken ct = default)
    {
        if (session is null) return Task.FromResult(Result.Success<IReadOnlyList<Session>>([]));
        return Task.FromResult(Result.Success<IReadOnlyList<Session>>([session]));
    }

    public Task<Result> AppendMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _appends);
        TaskCompletionSource? gate;
        lock (_lock)
        {
            gate = _gatedAppend;
            _gatedAppend = null;
        }

        if (gate is null)
        {
            lock (_lock) { _messages.Add(message); }
            return Task.FromResult(Result.Success());
        }

        return AwaitGateThenAppend(gate, message);
    }

    public Task<Result> UpdateMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default)
        => Task.FromResult(Result.Success());

    public Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(string sessionId, CancellationToken ct = default)
    {
        lock (_lock) { return Task.FromResult(Result.Success<IReadOnlyList<AgentMessage>>([.. _messages])); }
    }

    public Task<Result> DeleteAsync(string sessionId, CancellationToken ct = default)
        => Task.FromResult(Result.Success());

    public Task<Result> UpdateAsync(Session session, CancellationToken ct = default)
        => Task.FromResult(Result.Success());

    public Task<Result<SessionMetadata>> GetStatsAsync(string sessionId, CancellationToken ct = default)
        => Task.FromResult(Result.Success(SessionMetadata.Empty));

    public Task<Result> UpdateStatsAsync(string sessionId, SessionMetadata metadata, CancellationToken ct = default)
        => Task.FromResult(Result.Success());

    public Task<Result<int>> DeleteMessagesAfterAsync(string sessionId, string messageId, CancellationToken ct = default)
        => Task.FromResult(Result.Success(0));

    private async Task<Result> AwaitGateThenAppend(TaskCompletionSource gate, AgentMessage message)
    {
        await gate.Task.ConfigureAwait(false);
        lock (_lock)
        {
            _messages.Add(message);
        }

        return Result.Success();
    }
}

/// <summary>Event bus that records all published events and forwards to subscribers — merged canonical for bridge & assertion tests.</summary>
public sealed class FakeEventBus : IEventBus
{
    private readonly object _lock = new();
    private readonly List<Func<AgentEvent, CancellationToken, ValueTask>> _handlers = [];
    public List<AgentEvent> Events { get; } = [];

    public async Task PublishAsync(AgentEvent @event, CancellationToken ct = default)
    {
        lock (_lock) { Events.Add(@event); }
        List<Func<AgentEvent, CancellationToken, ValueTask>> snapshot;
        lock (_lock) { snapshot = [.. _handlers]; }
        foreach (var handler in snapshot)
        {
            await handler(@event, ct).ConfigureAwait(false);
        }
    }

    public IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> handler)
    {
        lock (_lock) { _handlers.Add(handler); }
        return new Disposer(() =>
        {
            lock (_lock) { _handlers.Remove(handler); }
        });
    }

    public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler) where TEvent : AgentEvent
        => Subscribe((e, ct) => e is TEvent typed ? handler(typed, ct) : ValueTask.CompletedTask);

    public IReadOnlyList<AgentEvent> GetScrollback(int maxEvents) => [];

    private sealed class Disposer(Action dispose) : IDisposable
    {
        private readonly Action _dispose = dispose;
        public void Dispose() => _dispose();
    }
}

/// <summary>Stub system prompt builder returning a constant string.</summary>
public sealed class StubSystemPromptBuilder : ISystemPromptBuilder
{
    public Task<string> BuildAsync(SystemPromptContext context, CancellationToken ct = default)
        => Task.FromResult("stub-system-prompt");
}

/// <summary>Token tracker with configurable compaction behavior.</summary>
public sealed class FakeTokenTracker(bool shouldCompact = false) : ITokenTracker
{
    public void RecordTurnUsage(Usage usage) { }
    public int Estimate(string text) => 0;
    public int EstimateMessage(AgentMessage message) => 0;
    public int EstimateTokens(IReadOnlyList<AgentMessage> messages) => 0;
    public bool ShouldCompact(IReadOnlyList<AgentMessage> messages, ModelInfo model) => shouldCompact;
    public TokenStats GetStats() => new(0, 0, null, null, null);
}

/// <summary>Compaction service that never compacts and records call count.</summary>
public sealed class FakeCompactionService : ICompactionService
{
    public int Calls { get; private set; }
    public bool ShouldCompact(IReadOnlyList<AgentMessage> messages, ModelInfo model) => false;
    public Task<Result<CompactionResult>> CompactAsync(string sessionId, IReadOnlyList<AgentMessage> messages, ModelInfo model, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(Result.Failure<CompactionResult>("simulated compaction failure"));
    }
}
