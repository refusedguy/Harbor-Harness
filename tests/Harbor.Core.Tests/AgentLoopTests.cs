using System.Runtime.CompilerServices;
using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Core.Agents;
using Harbor.Core.Permissions;
using Harbor.Core.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Core.Tests;

/// <summary>
///     Tests for <see cref="AgentLoop" /> using a mock <see cref="ILlmClient" /> that yields
///     a scripted sequence of <see cref="LlmEvent" />s. Verifies the happy-path event sequence
///     (AgentStart → TurnStart → MessageStart → MessageUpdate → MessageEnd → AgentEnd) and
///     that the streamed text is materialized into the final assistant message.
/// </summary>
public class AgentLoopTests
{
    private static readonly ModelInfo TestModel = new(
        Id: "test-model",
        ProviderId: "test",
        DisplayName: "Test Model",
        ContextWindow: 200_000,
        MaxOutputTokens: 4096,
        SupportsReasoning: false,
        SupportsVision: false,
        SupportsToolUse: true,
        Pricing: Pricing.Unknown,
        PromptTemplate: "openai");

    /// <summary>
    ///     A scripted <see cref="ILlmClient" /> that replays a fixed sequence of events on each
    ///     <see cref="StreamAsync" /> call. <see cref="GetModelsAsync" /> always returns
    ///     <see cref="TestModel" />.
    /// </summary>
    private sealed class MockLlmClient : ILlmClient
    {
        private readonly LlmEvent[] _events;

        public MockLlmClient(params LlmEvent[] events) => _events = events;

        public ProviderId ProviderId => ProviderId.Create("test");

        public async IAsyncEnumerable<LlmEvent> StreamAsync(
            LlmRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var e in _events)
            {
                yield return e;
                await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            }
        }

        public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Result.Success<IReadOnlyList<ModelInfo>>(new[] { TestModel }));
    }

    /// <summary>
    ///     Minimal <see cref="ISessionContext" /> for tests — keeps an in-memory message list
    ///     and discards stats updates. The agent loop only reads <see cref="Messages" /> and
    ///     calls <see cref="AppendMessageAsync" /> / <see cref="UpdateStatsAsync" />.
    /// </summary>
    private sealed class TestSessionContext : ISessionContext
    {
        private readonly List<AgentMessage> _messages;

        public TestSessionContext(Session session, IReadOnlyList<AgentMessage> messages)
        {
            Session = session;
            _messages = new List<AgentMessage>(messages);
            SteeringQueue = System.Threading.Channels.Channel.CreateUnbounded<AgentMessage>();
        }

        public Session Session { get; }
        public IReadOnlyList<AgentMessage> Messages => _messages;
        public System.Threading.Channels.Channel<AgentMessage> SteeringQueue { get; }

        public Task AppendMessageAsync(AgentMessage message, CancellationToken ct = default)
        {
            _messages.Add(message);
            return Task.CompletedTask;
        }

        public Task UpdateStatsAsync(Usage usage, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }
    }

    private static (AgentLoop loop, ProviderRegistry providers, ToolRegistry tools, AgentRegistry agents, InMemoryEventBus bus) CreateLoop(MockLlmClient client)
    {
        var providers = new ProviderRegistry();
        providers.Register(ProviderId.Create("test"), () => client);

        var tools = new ToolRegistry();
        tools.Freeze();

        var agents = new AgentRegistry();
        agents.Register(AgentDefinition.CodeDefault("test-model", "test"));

        var bus = new InMemoryEventBus();
        var promptBuilder = new SystemPromptBuilder();
        var compaction = new CompactionService(
            new HeuristicTokenEstimator(),
            providers,
            NullLogger<CompactionService>.Instance);
        var permissions = new PermissionService(agents, NullLogger<PermissionService>.Instance);
        var converter = new MessageConverter();

        var loop = new AgentLoop(
            providers,
            tools,
            agents,
            promptBuilder,
            compaction,
            new HeuristicTokenEstimator(),
            bus,
            permissions,
            converter,
            NullLogger<AgentLoop>.Instance);

        return (loop, providers, tools, agents, bus);
    }

    private static TestSessionContext CreateSession(params AgentMessage[] messages)
    {
        var session = Session.Create("/tmp", "code", "test", "test-model");
        return new TestSessionContext(session, messages);
    }

    [Test]
    public async Task RunAsync_TextDeltaOnly_CompletesAfterFirstTurn()
    {
        // Script: a single text run + step finish. No tool calls → loop exits after turn 1.
        var client = new MockLlmClient(
            new TextDeltaEvent("0", "Hello, "),
            new TextDeltaEvent("0", "World!"),
            new StepFinishEvent(0, "stop", new Usage(10, 5)));

        var (loop, _, _, _, bus) = CreateLoop(client);
        var session = CreateSession();
        var receivedEvents = new List<AgentEvent>();
        bus.Subscribe(async (evt, ct) => receivedEvents.Add(evt));

        var agent = AgentDefinition.CodeDefault("test-model", "test");
        var result = await loop.RunAsync(session, agent);

        await Assert.That(result.IsSuccess).IsTrue();
        // The agent loop appends the assistant message + emits the full event sequence.
        await Assert.That(receivedEvents.Any(e => e is AgentStartEvent)).IsTrue();
        await Assert.That(receivedEvents.Any(e => e is TurnStartEvent)).IsTrue();
        await Assert.That(receivedEvents.Any(e => e is MessageStartEvent)).IsTrue();
        await Assert.That(receivedEvents.Count(e => e is MessageUpdateEvent)).IsEqualTo(2);
        await Assert.That(receivedEvents.Any(e => e is MessageEndEvent)).IsTrue();
        await Assert.That(receivedEvents.Any(e => e is AgentEndEvent)).IsTrue();
    }

    [Test]
    public async Task RunAsync_TextDeltas_AreJoinedInAssistantMessage()
    {
        var client = new MockLlmClient(
            new TextDeltaEvent("0", "Hello, "),
            new TextDeltaEvent("0", "World!"),
            new StepFinishEvent(0, "stop", new Usage(10, 5)));

        var (loop, _, _, _, _) = CreateLoop(client);
        var session = CreateSession();

        await loop.RunAsync(session, AgentDefinition.CodeDefault("test-model", "test"));

        // The loop appended an assistant message with the joined text.
        var assistant = session.Messages.OfType<AssistantMessage>().Single();
        var text = string.Concat(assistant.Parts.OfType<TextPart>().Select(t => t.Text));
        await Assert.That(text).IsEqualTo("Hello, World!");
    }

    [Test]
    public async Task RunAsync_StepFinishUsage_FlowsIntoSessionStats()
    {
        var usage = new Usage(100, 50);
        var client = new MockLlmClient(
            new TextDeltaEvent("0", "ok"),
            new StepFinishEvent(0, "stop", usage));

        var (loop, _, _, _, bus) = CreateLoop(client);
        var statsUpdates = new List<Usage>();
        var session = new CapturingStatsSession(CreateSession().Session, Array.Empty<AgentMessage>(), statsUpdates);

        await loop.RunAsync(session, AgentDefinition.CodeDefault("test-model", "test"));

        await Assert.That(statsUpdates.Count).IsEqualTo(1);
        await Assert.That(statsUpdates[0].InputTokens).IsEqualTo(100);
        await Assert.That(statsUpdates[0].OutputTokens).IsEqualTo(50);
    }

    [Test]
    public async Task RunAsync_ErrorEvent_ReturnsFailure()
    {
        var client = new MockLlmClient(
            new ErrorEvent("upstream blew up"));

        var (loop, _, _, _, bus) = CreateLoop(client);
        var session = CreateSession();
        var errors = new List<AgentErrorEvent>();
        bus.Subscribe<AgentErrorEvent>(async (evt, ct) => errors.Add(evt));

        var result = await loop.RunAsync(session, AgentDefinition.CodeDefault("test-model", "test"));

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("upstream blew up");
        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0].Message).IsEqualTo("upstream blew up");
    }

    [Test]
    public async Task RunAsync_UnknownModel_ReturnsFailure()
    {
        var client = new MockLlmClient(new TextDeltaEvent("0", "x"), new StepFinishEvent(0, "stop", null));
        var (loop, _, _, _, _) = CreateLoop(client);
        var session = CreateSession();

        // Agent references a model that doesn't exist in the mock's GetModelsAsync result.
        var agent = AgentDefinition.CodeDefault("not-a-real-model", "test");
        var result = await loop.RunAsync(session, agent);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("not-a-real-model");
    }

    [Test]
    public async Task RunAsync_ThinkingDeltaFollowedByText_BothAreAppended()
    {
        var client = new MockLlmClient(
            new ThinkingDeltaEvent("0", "Let me think..."),
            new TextDeltaEvent("0", "Answer"),
            new StepFinishEvent(0, "stop", new Usage(1, 1)));

        var (loop, _, _, _, _) = CreateLoop(client);
        var session = CreateSession();

        await loop.RunAsync(session, AgentDefinition.CodeDefault("test-model", "test"));

        var assistant = session.Messages.OfType<AssistantMessage>().Single();
        await Assert.That(assistant.Parts.OfType<ThinkingPart>().Count()).IsEqualTo(1);
        await Assert.That(assistant.Parts.OfType<TextPart>().Count()).IsEqualTo(1);
        await Assert.That(assistant.Parts.OfType<ThinkingPart>().Single().Text).IsEqualTo("Let me think...");
        await Assert.That(assistant.Parts.OfType<TextPart>().Single().Text).IsEqualTo("Answer");
    }

    /// <summary>
    ///     Session context wrapper that captures every <see cref="UpdateStatsAsync" /> call
    ///     so tests can assert the usage flowed through.
    /// </summary>
    private sealed class CapturingStatsSession : ISessionContext
    {
        private readonly TestSessionContext _inner;
        private readonly List<Usage> _captured;

        public CapturingStatsSession(Session session, IReadOnlyList<AgentMessage> messages, List<Usage> captured)
        {
            _captured = captured;
            _inner = new TestSessionContext(session, messages);
        }

        public Session Session => _inner.Session;
        public IReadOnlyList<AgentMessage> Messages => _inner.Messages;
        public System.Threading.Channels.Channel<AgentMessage> SteeringQueue => _inner.SteeringQueue;
        public Task AppendMessageAsync(AgentMessage message, CancellationToken ct = default) => _inner.AppendMessageAsync(message, ct);
        public Task UpdateStatsAsync(Usage usage, CancellationToken ct = default)
        {
            _captured.Add(usage);
            return Task.CompletedTask;
        }
    }
}
