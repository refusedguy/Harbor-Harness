using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;

namespace Harbor.Application.Tests.Fakes;

public sealed class ScriptedLlmClient : ILlmClient
{
    private static readonly ModelInfo TestModel =
        new("test-model", "test", "Test Model", 200_000, 4096, false, false, true, Pricing.Unknown, "openai");

    private readonly LlmEvent[][] _scripts;
    private int _callIndex;

    public ScriptedLlmClient(params LlmEvent[][] scripts)
    {
        _scripts = scripts.Length == 0 ? [[]] : scripts;
    }

    public List<LlmRequest> Requests { get; } = [];

    public ProviderId ProviderId => ProviderId.Create("test");

    public async IAsyncEnumerable<LlmEvent> StreamAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        LlmEvent[] script = _scripts[Math.Min(_callIndex, _scripts.Length - 1)];
        _callIndex++;
        foreach (LlmEvent evt in script)
        {
            yield return evt;
            await Task.Yield();
        }
    }

    public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Result.Success<IReadOnlyList<ModelInfo>>(new[] { TestModel }));

    public static ModelInfo CreateTestModel() => TestModel;
}

public sealed class FakeProviderRegistry(ILlmClient client) : IProviderRegistry
{
    public IReadOnlyList<ProviderId> GetRegisteredProviderIds() => [client.ProviderId];

    public Result<ILlmClient> GetClient(ProviderId providerId) => Result.Success(client);

    public Task<Result<IReadOnlyList<ModelInfo>>> GetAllModelsAsync(CancellationToken cancellationToken = default)
        => client.GetModelsAsync(cancellationToken);

    public void Register(ProviderId providerId, Func<ILlmClient> factory)
    {
    }

    public Result Unregister(ProviderId providerId) => Result.Failure("FakeProviderRegistry does not support unregister.");
}

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

public sealed class CountingTool : ITool
{
    private readonly object _lock = new();
    private int _executions;

    public int Executions => Volatile.Read(ref _executions);

    public List<string> ExecutedArgs { get; } = [];

    public ToolName Name => ToolName.Create("counter");

    public string DisplayName => "Counter";

    public string Description => "Counts executions for red-team lifecycle tests.";

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
        // A6 rebuild fix: ZLinq drop-in breaks `..` spread over Select() on
        // arrays — materialize explicitly instead.
        var list = new List<ToolDescriptor>(_tools.Values.Count);
        foreach (var t in _tools.Values)
        {
            list.Add(new ToolDescriptor(
                t.Name, t.DisplayName, t.Description, t.ParameterSchema,
                t.ExecutionMode, t.PromptSnippet, t.PromptGuidelines));
        }
        return list;
    }
}

public sealed class FakeEventBus : IEventBus
{
    private static readonly IDisposable NoopSubscription = new NoopDisposable();

    public List<AgentEvent> Events { get; } = [];

    public Task PublishAsync(AgentEvent @event, CancellationToken ct = default)
    {
        Events.Add(@event);
        return Task.CompletedTask;
    }

    public IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> handler) => NoopSubscription;

    public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler) where TEvent : AgentEvent
        => NoopSubscription;

    public IReadOnlyList<AgentEvent> GetScrollback(int maxEvents) => [];

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

public sealed class StubSystemPromptBuilder : ISystemPromptBuilder
{
    public Task<string> BuildAsync(SystemPromptContext context, CancellationToken ct = default)
        => Task.FromResult("stub-system-prompt");
}

public sealed class FakeTokenTracker(bool shouldCompact = false) : ITokenTracker
{
    public void RecordTurnUsage(Usage usage)
    {
    }

    public int Estimate(string text) => 0;

    public int EstimateMessage(AgentMessage message) => 0;

    public int EstimateTokens(IReadOnlyList<AgentMessage> messages) => 0;

    public bool ShouldCompact(IReadOnlyList<AgentMessage> messages, ModelInfo model) => shouldCompact;

    public TokenStats GetStats() => new(0, 0, null, null, null);
}

public sealed class FakeCompactionService : ICompactionService
{
    private readonly Result<CompactionResult> _outcome =
        Result.Failure<CompactionResult>("simulated compaction failure");

    public int Calls { get; private set; }

    public bool ShouldCompact(IReadOnlyList<AgentMessage> messages, ModelInfo model) => false;

    public Task<Result<CompactionResult>> CompactAsync(
        string sessionId,
        IReadOnlyList<AgentMessage> messages,
        ModelInfo model,
        CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(_outcome);
    }
}

public sealed class TestSessionContext(Session session, IReadOnlyList<AgentMessage>? seedMessages = null) : ISessionContext
{
    private readonly List<AgentMessage> _messages = [.. seedMessages ?? []];

    public Session Session { get; } = session;

    public IReadOnlyList<AgentMessage> Messages => _messages;

    public Channel<AgentMessage> SteeringQueue { get; } = Channel.CreateUnbounded<AgentMessage>();

    public Task AppendMessageAsync(AgentMessage message, CancellationToken ct = default)
    {
        _messages.Add(message);
        return Task.CompletedTask;
    }

    public Task UpdateStatsAsync(Usage usage, CancellationToken ct = default) => Task.CompletedTask;

    public void EnqueueSteering(params AgentMessage[] messages)
    {
        foreach (AgentMessage message in messages)
        {
            SteeringQueue.Writer.TryWrite(message);
        }
    }
}
