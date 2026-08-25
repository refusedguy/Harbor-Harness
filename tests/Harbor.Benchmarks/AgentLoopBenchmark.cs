using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using BenchmarkDotNet.Attributes;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Core.Agents;
using Harbor.Core.Resilience;
using Harbor.Core.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Benchmarks;
/// <summary>
///     Benchmarks the per-turn fixed overhead of <see cref="AgentLoop" /> — the orchestration
///     cost of one agent-loop iteration (LLM call → optional tool calls → tool execution → next
///     turn) with the network I/O fully stubbed. This isolates the cost of event publishing,
///     message assembly, tool dispatch, and session bookkeeping from the actual model latency.
///     <para>
///         Two scenarios are exercised:
///         - <see cref="Run_NoToolCall" />: the stubbed <see cref="ILlmClient" /> emits a single
///         text delta and a <c>stop</c> finish, so the loop completes in exactly one turn. This
///         measures pure orchestration overhead (prompt build, message conversion, event stream).
///         - <see cref="Run_WithToolCall" />: the stubbed client emits one tool call; a stubbed
///         <see cref="ITool" /> executes, exercising the <see cref="ToolDispatcher" /> path as
///         well (validation, permission check, execution, result publishing).
///     </para>
///     Every dependency of <see cref="AgentLoop" /> is replaced by a minimal in-process stub; no
///     provider, file, or network I/O occurs. Each benchmark invocation builds a fresh
///     <see cref="ISessionContext" /> so accumulated messages do not leak across iterations.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 2, iterationCount: 3)]
public class AgentLoopBenchmark
{
    private AgentDefinition _agent = null!;
    private AgentLoop _loopNoTool = null!;
    private AgentLoop _loopWithTool = null!;

    [GlobalSetup]
    public void Setup()
    {
        var providerId = ProviderId.Create("stub");
        var model = new ModelInfo(
            "stub-1", "stub", "Stub Model", 32_768, 4_096, false, false, true, Pricing.Unknown, "openai");

        _agent = new AgentDefinition(
            AgentName.Create("code"),
            "Code",
            "Benchmark agent with a single-step budget.",
            model.Id,
            providerId.Value,
            PermissionRuleset.Default,
            1);

        var eventBus = new BenchEventBus();
        var tokenTracker = new TokenTracker();
        var retryPolicy = new RetryPolicy();
        var compaction = new BenchCompactionService();
        var promptBuilder = new BenchSystemPromptBuilder();
        var permission = new BenchPermissionService();
        var toolRegistry = new BenchToolRegistry();
        var agentRegistry = new BenchAgentRegistry();
        var messageConverter = new MessageConverter();
        var logger = NullLogger<AgentLoop>.Instance;

        // Text-only scenario: provider returns a client that emits a plain-text turn.
        var textClient = new BenchTextLlmClient(providerId, model);
        var textProviders = new BenchProviderRegistry(providerId, textClient);
        _loopNoTool = new AgentLoop(
            textProviders, toolRegistry, agentRegistry, promptBuilder,
            compaction, tokenTracker, retryPolicy, eventBus, permission, messageConverter, logger);

        // Tool-dispatch scenario: provider returns a client that emits a single tool call.
        var toolClient = new BenchToolLlmClient(providerId, model);
        var toolProviders = new BenchProviderRegistry(providerId, toolClient);
        _loopWithTool = new AgentLoop(
            toolProviders, toolRegistry, agentRegistry, promptBuilder,
            compaction, tokenTracker, retryPolicy, eventBus, permission, messageConverter, logger);
    }

    /// <summary>
    ///     One full agent-loop turn with a stubbed LLM client that returns plain text (no tool
    ///     calls). Measures pure orchestration overhead.
    /// </summary>
    [Benchmark(Description = "One turn, no tool call (orchestration only)")]
    public async Task<Result> Run_NoToolCall()
    {
        var session = CreateSessionContext();
        return await _loopNoTool.RunAsync(session, _agent, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    ///     One full agent-loop turn where the stubbed LLM emits a single tool call and a stubbed
    ///     tool executes. Measures the tool-dispatch path on top of orchestration.
    /// </summary>
    [Benchmark(Description = "One turn with one tool call (dispatch path)")]
    public async Task<Result> Run_WithToolCall()
    {
        var session = CreateSessionContext();
        return await _loopWithTool.RunAsync(session, _agent, CancellationToken.None).ConfigureAwait(false);
    }

    private static ISessionContext CreateSessionContext()
    {
        var session = Session.Create(Path.GetTempPath(), "code", "stub", "stub-1");
        var messages = new List<AgentMessage>
        {
            new UserMessage(
                Guid.NewGuid().ToString("N"),
                session.Id,
                DateTimeOffset.UtcNow,
                "Benchmark prompt.",
                "code",
                "stub-1")
        };
        return new BenchSessionContext(session, messages);
    }
}

/// <summary>
///     Minimal <see cref="ILlmClient" /> that streams a single text delta then a normal
///     <c>stop</c> finish — no tool calls.
/// </summary>
internal sealed class BenchTextLlmClient : ILlmClient
{
    private readonly ModelInfo _model;

    public BenchTextLlmClient(ProviderId providerId, ModelInfo model)
    {
        ProviderId = providerId;
        _model = model;
    }

    public ProviderId ProviderId
    {
        get;
    }

    public IAsyncEnumerable<LlmEvent> StreamAsync(LlmRequest request, CancellationToken cancellationToken = default)
        => StreamText(cancellationToken);

    public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Result.Success<IReadOnlyList<ModelInfo>>(new[] { _model }));

    private static async IAsyncEnumerable<LlmEvent> StreamText([EnumeratorCancellation] CancellationToken ct)
    {
        yield return new TextDeltaEvent("d1", "Hello from the stubbed agent loop benchmark.");
        await Task.Yield();
        yield return new StepFinishEvent(1, "stop", new Usage(10, 5));
    }
}

/// <summary>
///     Minimal <see cref="ILlmClient" /> that streams a single tool call
///     (<see cref="ToolCallStartEvent" /> + <see cref="ToolCallDeltaEvent" />) then a
///     <c>tool_use</c> finish, driving the <see cref="Harbor.Core.Agents.ToolDispatcher" /> path.
/// </summary>
internal sealed class BenchToolLlmClient : ILlmClient
{
    private readonly ModelInfo _model;

    public BenchToolLlmClient(ProviderId providerId, ModelInfo model)
    {
        ProviderId = providerId;
        _model = model;
    }

    public ProviderId ProviderId
    {
        get;
    }

    public IAsyncEnumerable<LlmEvent> StreamAsync(LlmRequest request, CancellationToken cancellationToken = default)
        => StreamTool(cancellationToken);

    public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Result.Success<IReadOnlyList<ModelInfo>>(new[] { _model }));

    private static async IAsyncEnumerable<LlmEvent> StreamTool([EnumeratorCancellation] CancellationToken ct)
    {
        yield return new ToolCallStartEvent("tc_bench", "bench_tool");
        await Task.Yield();
        yield return new ToolCallDeltaEvent("tc_bench", "{\"input\":\"x\"}");
        yield return new StepFinishEvent(1, "tool_use", new Usage(12, 6));
    }
}

/// <summary>
///     Minimal <see cref="IProviderRegistry" /> that returns a fixed <see cref="ILlmClient" />.
/// </summary>
internal sealed class BenchProviderRegistry : IProviderRegistry
{
    private readonly ILlmClient _client;
    private readonly ProviderId _providerId;

    public BenchProviderRegistry(ProviderId providerId, ILlmClient client)
    {
        _providerId = providerId;
        _client = client;
    }

    public IReadOnlyList<ProviderId> GetRegisteredProviderIds() => new[] { _providerId };

    public Result<ILlmClient> GetClient(ProviderId providerId) => Result.Success(_client);

    public Task<Result<IReadOnlyList<ModelInfo>>> GetAllModelsAsync(CancellationToken cancellationToken = default)
        => _client.GetModelsAsync(cancellationToken);

    public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsCachedAsync(ProviderId providerId, CancellationToken cancellationToken = default)
        => _client.GetModelsAsync(cancellationToken);

    public void Register(ProviderId providerId, Func<ILlmClient> factory) { }

    public Result Unregister(ProviderId providerId) => Result.Success();
}

/// <summary>
///     Minimal <see cref="IToolRegistry" /> holding a single trivial <see cref="ITool" />.
/// </summary>
internal sealed class BenchToolRegistry : IToolRegistry
{
    private readonly ToolDescriptor _descriptor;
    private readonly ITool _tool = new BenchTool();

    public BenchToolRegistry()
    {
        _descriptor = new ToolDescriptor(
            _tool.Name,
            _tool.DisplayName,
            _tool.Description,
            _tool.ParameterSchema,
            _tool.ExecutionMode,
            _tool.PromptSnippet,
            _tool.PromptGuidelines);
    }

    public IReadOnlyList<ToolDescriptor> GetAllTools() => new[] { _descriptor };

    public IReadOnlyList<ToolDescriptor> ResolveTools(string agentName, PermissionRuleset? sessionPermission = null)
        => new[] { _descriptor };

    public Result<ITool> GetTool(ToolName name)
        => name == _tool.Name ? Result.Success(_tool) : Result.Failure<ITool>($"Unknown tool '{name.Value}'");

    public Result Register(ITool tool) => Result.Success();

    public Result Unregister(ToolName name) => Result.Success();
}

/// <summary>
///     Trivial <see cref="ITool" /> that always succeeds with a fixed output.
/// </summary>
internal sealed class BenchTool : ITool
{
    public ToolName Name => ToolName.Create("bench_tool");
    public string DisplayName => "Bench Tool";
    public string Description => "A trivial tool used to exercise the dispatch path.";
    public JsonDocument ParameterSchema => JsonDocument.Parse("{}");
    public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
    public string? PromptSnippet => null;
    public IReadOnlyList<string> PromptGuidelines => Array.Empty<string>();

    public Task<ToolResult> ExecuteAsync(
        JsonElement args, ToolContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(ToolResult.Success("bench tool output"));
}

/// <summary>
///     Minimal <see cref="IAgentRegistry" /> (not exercised by <see cref="AgentLoop.RunAsync" />,
///     but required by the constructor).
/// </summary>
internal sealed class BenchAgentRegistry : IAgentRegistry
{
    public IReadOnlyList<AgentDefinition> GetAllAgents() => Array.Empty<AgentDefinition>();

    public Result<AgentDefinition> GetAgent(AgentName name) => Result.Failure<AgentDefinition>($"Unknown agent '{name.Value}'");

    public Result Register(AgentDefinition agent) => Result.Success();

    public Result Unregister(AgentName name) => Result.Success();
}

/// <summary>
///     Minimal <see cref="ISystemPromptBuilder" /> returning a canned prompt.
/// </summary>
internal sealed class BenchSystemPromptBuilder : ISystemPromptBuilder
{
    public Task<string> BuildAsync(SystemPromptContext context, CancellationToken ct = default)
        => Task.FromResult("You are a benchmark agent.");
}

/// <summary>
///     Minimal <see cref="ICompactionService" /> that never triggers compaction.
/// </summary>
internal sealed class BenchCompactionService : ICompactionService
{
    public bool ShouldCompact(IReadOnlyList<AgentMessage> messages, ModelInfo model) => false;

    public Task<Result<CompactionResult>> CompactAsync(
        string sessionId, IReadOnlyList<AgentMessage> messages, ModelInfo model, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<CompactionResult>("Compaction not exercised by this benchmark."));
}

/// <summary>
///     Minimal <see cref="ITokenTracker" /> (not exercised on the single-turn stub path).
/// </summary>
internal sealed class BenchTokenEstimator : ITokenTracker
{
    public void RecordTurnUsage(Usage usage) { }
    public int Estimate(string text) => 0;
    public int EstimateMessage(AgentMessage message) => 0;
    public int EstimateTokens(IReadOnlyList<AgentMessage> messages) => 0;
    public bool ShouldCompact(IReadOnlyList<AgentMessage> messages, ModelInfo model) => false;
    public TokenStats GetStats() => new(0, 0, null, null, null);
}

/// <summary>
///     Minimal <see cref="IEventBus" /> that drops all events (no subscribers).
/// </summary>
internal sealed class BenchEventBus : IEventBus
{
    public Task PublishAsync(AgentEvent @event, CancellationToken ct = default) => Task.CompletedTask;

    public IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> handler) => new NoopDisposable();

    public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler) where TEvent : AgentEvent
        => new NoopDisposable();

    public IReadOnlyList<AgentEvent> GetScrollback(int maxEvents) => Array.Empty<AgentEvent>();

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose() { }
    }
}

/// <summary>
///     Minimal <see cref="IPermissionService" /> that always allows tool calls.
/// </summary>
internal sealed class BenchPermissionService : IPermissionService
{
    public Task<Result<PermissionResponse>> CheckAsync(
        string agentName, string toolName, JsonElement args, CancellationToken ct = default)
        => Task.FromResult(Result.Success(new PermissionResponse(PermissionAction.Allow, false)));

    public Task<Result<PermissionResponse>> AskUserAsync(
        PermissionRequest request, CancellationToken ct = default)
        => Task.FromResult(Result.Success(new PermissionResponse(PermissionAction.Allow, false)));

    public PermissionRuleset GetRuleset(string agentName) => PermissionRuleset.Empty;
}

/// <summary>
///     In-memory <see cref="ISessionContext" /> for driving the loop without a durable store.
/// </summary>
internal sealed class BenchSessionContext : ISessionContext
{
    private readonly List<AgentMessage> _messages;

    public BenchSessionContext(Session session, List<AgentMessage> messages)
    {
        Session = session;
        _messages = messages;
        SteeringQueue = Channel.CreateUnbounded<AgentMessage>();
    }

    public Session Session
    {
        get;
    }
    public IReadOnlyList<AgentMessage> Messages => _messages;
    public Channel<AgentMessage> SteeringQueue { get; }

    public Task AppendMessageAsync(AgentMessage message, CancellationToken ct = default)
    {
        _messages.Add(message);
        return Task.CompletedTask;
    }

    public Task UpdateStatsAsync(Usage usage, CancellationToken ct = default) => Task.CompletedTask;
}
