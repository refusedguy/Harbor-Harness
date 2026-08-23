using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Core.Agents;
using Harbor.Core.Permissions;
using Harbor.Core.Resilience;
using Harbor.Core.Sessions;
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
        "test-model",
        "test",
        "Test Model",
        200_000,
        4096,
        false,
        false,
        true,
        Pricing.Unknown,
        "openai");

    private static (AgentLoop loop, ProviderRegistry providers, ToolRegistry tools, AgentRegistry agents, InMemoryEventBus bus) CreateLoop(
        ILlmClient client,
        Action<CompactionService>? configureCompaction = null)
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
            new TokenTracker(),
            providers,
            NullLogger<CompactionService>.Instance);
        configureCompaction?.Invoke(compaction);
        var permissions = new PermissionService(agents, NullLogger<PermissionService>.Instance);
        var converter = new MessageConverter();
        var tokenTracker = new TokenTracker();
        var retryPolicy = new RetryPolicy();

        var loop = new AgentLoop(
            providers,
            tools,
            agents,
            promptBuilder,
            compaction,
            tokenTracker,
            retryPolicy,
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

    /// <summary>
    ///     Model with a 32k window: the compaction threshold
    ///     (window − default 16_384 reserve) is 15_616 estimated tokens, so a
    ///     seeded history of twenty ~850-token messages (17_000) triggers
    ///     compaction while the compacted view (~4 tail messages + summary)
    ///     stays comfortably below it.
    /// </summary>
    private static readonly ModelInfo CompactibleModel = new(
        "test-model",
        "test",
        "Test Model",
        32_000,
        1024,
        false,
        false,
        true,
        Pricing.Unknown,
        "openai");

    /// <summary>
    ///     Session pre-seeded with <paramref name="seedCount" /> user messages of
    ///     ~850 estimated tokens each (3000 chars / 4 + per-message overhead).
    /// </summary>
    private static TestSessionContext CreateSeededSession(int seedCount)
    {
        var session = Session.Create("/tmp", "code", "test", "test-model");
        var messages = new List<AgentMessage>(seedCount);
        for (int i = 0; i < seedCount; i++)
        {
            messages.Add(new UserMessage(
                Guid.NewGuid().ToString("N"),
                session.Id,
                DateTimeOffset.UtcNow,
                new string('x', 3000),
                "code",
                "test-model"));
        }

        return new TestSessionContext(session, messages);
    }

    private static void ConfigureAggressiveCompaction(CompactionService svc)
    {
        svc.KeepRecentTokens = 4000;
        svc.TailTurns = 0;
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
        // MessageUpdateEvent fires once per streamed TextDelta PLUS once on StepFinishEvent
        // (the loop forwards StepFinish to the bus so status bars / views can tally token
        // usage — see AgentLoop.StreamAsync switch → case StepFinishEvent). For the
        // 2-delta script here, that is 2 (deltas) + 1 (step finish) = 3 updates.
        await Assert.That(receivedEvents.Count(e => e is MessageUpdateEvent)).IsEqualTo(3);
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
        string text = string.Concat(assistant.Parts.OfType<TextPart>().Select(t => t.Text));
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
    ///     Compaction must actually shrink the history the model sees. After a
    ///     successful compaction the effective history (the compacted view
    ///     materialized from <c>SummaryFirstKeptId</c>) is strictly shorter than
    ///     the seeded history, contains the summary first, and preserves the
    ///     kept tail verbatim — and the LLM request for the very same turn is
    ///     already built from that truncated view.
    /// </summary>
    [Test]
    public async Task RunAsync_CompactionTriggers_RequestUsesShortenedView_AndTailIsPreserved()
    {
        var client = new ScriptedLlmClient(
            new LlmEvent[] { new TextDeltaEvent("s", "summary text"), new StepFinishEvent(0, "stop", new Usage(0, 10)) },
            new LlmEvent[] { new TextDeltaEvent("t", "done"), new StepFinishEvent(0, "stop", new Usage(5, 5)) });

        var (loop, _, _, _, bus) = CreateLoop(client, ConfigureAggressiveCompaction);
        var session = CreateSeededSession(seedCount: 20);
        var seedIds = session.Messages.Select(m => m.Id).ToArray();
        var completed = new List<CompactionCompletedEvent>();
        bus.Subscribe<CompactionCompletedEvent>(async (evt, ct) => completed.Add(evt));

        var result = await loop.RunAsync(session, AgentDefinition.CodeDefault("test-model", "test"));

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(completed.Count).IsEqualTo(1);
        await Assert.That(completed[0].PrunedMessageCount).IsEqualTo(16);

        // Raw history is append-only: seed + summary + turn answer.
        await Assert.That(session.Messages.Count).IsEqualTo(22);
        var summaries = session.Messages.OfType<AssistantMessage>().Where(m => m.IsSummary).ToList();
        await Assert.That(summaries.Count).IsEqualTo(1);
        string anchorId = summaries[0].SummaryFirstKeptId ?? string.Empty;
        await Assert.That(anchorId).IsEqualTo(seedIds[16]);

        // The EFFECTIVE history is actually shorter: summary + kept tail + answer.
        var view = CompactionService.MaterializeCompactedView(session.Messages);
        await Assert.That(view.Count).IsEqualTo(6);
        await Assert.That(view[0].Id).IsEqualTo(summaries[0].Id);
        for (int i = 1; i <= 4; i++)
        {
            await Assert.That(view[i].Id).IsEqualTo(seedIds[15 + i]);
        }
        await Assert.That(view.Count).IsLessThan(20);

        // The request of the compaction turn was already built from the
        // truncated view (summarizer call sees 1 packed message; the turn
        // request carries exactly summary + tail = 5 messages).
        await Assert.That(client.RequestSizes.Count).IsEqualTo(2);
        await Assert.That(client.RequestSizes[0]).IsEqualTo(1);
        await Assert.That(client.RequestSizes[1]).IsEqualTo(5);
    }

    /// <summary>
    ///     After one successful compaction the next turn's check runs against
    ///     the shortened view and stays under the threshold — compaction is NOT
    ///     re-triggered on every subsequent turn.
    /// </summary>
    [Test]
    public async Task RunAsync_SecondTurnAfterCompaction_DoesNotRetriggerCompaction()
    {
        // Call order: (1) summarizer at turn-1 start, (2) turn-1 stream ending
        // in a tool call, (3) turn-2 stream ending the run.
        var client = new ScriptedLlmClient(
            new LlmEvent[] { new TextDeltaEvent("s", "summary text"), new StepFinishEvent(0, "stop", new Usage(0, 10)) },
            new LlmEvent[]
            {
                new ToolCallStartEvent("call-1", "read"),
                new ToolCallDeltaEvent("call-1", "{}"),
                new StepFinishEvent(0, "tool_use", new Usage(10, 5))
            },
            new LlmEvent[] { new TextDeltaEvent("t", "done"), new StepFinishEvent(0, "stop", new Usage(5, 5)) });

        var (loop, _, _, _, bus) = CreateLoop(client, ConfigureAggressiveCompaction);
        var session = CreateSeededSession(seedCount: 20);
        var started = new List<CompactionStartedEvent>();
        var failed = new List<CompactionFailedEvent>();
        bus.Subscribe<CompactionStartedEvent>(async (evt, ct) => started.Add(evt));
        bus.Subscribe<CompactionFailedEvent>(async (evt, ct) => failed.Add(evt));

        var result = await loop.RunAsync(session, AgentDefinition.CodeDefault("test-model", "test"));

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(failed.Count).IsEqualTo(0);
        await Assert.That(started.Count).IsEqualTo(1);
        await Assert.That(client.StreamCalls).IsEqualTo(3);
        // Turn-2 request = summary + 4 kept tail + assistant + tool result.
        await Assert.That(client.RequestSizes.Count).IsEqualTo(3);
        await Assert.That(client.RequestSizes[0]).IsEqualTo(1);
        await Assert.That(client.RequestSizes[1]).IsEqualTo(5);
        await Assert.That(client.RequestSizes[2]).IsEqualTo(7);
        await Assert.That(session.Messages.Count(m => m is AssistantMessage { IsSummary: true })).IsEqualTo(1);
    }

    /// <summary>
    ///     A scripted <see cref="ILlmClient" /> that replays a fixed sequence of events on each
    ///     <see cref="StreamAsync" /> call. <see cref="GetModelsAsync" /> always returns
    ///     <see cref="TestModel" />.
    /// </summary>
    private sealed class MockLlmClient : ILlmClient
    {
        private readonly LlmEvent[] _events;

        public MockLlmClient(params LlmEvent[] events)
        {
            _events = events;
        }

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
    ///     A scripted <see cref="ILlmClient" /> that replays a DIFFERENT event
    ///     sequence per <see cref="StreamAsync" /> call (compaction summarizer,
    ///     turn 1, turn 2, …); the last sequence repeats if more calls arrive.
    ///     Records every request's message count so tests can assert exactly
    ///     how much history each call carried.
    /// </summary>
    private sealed class ScriptedLlmClient(params LlmEvent[][] calls) : ILlmClient
    {
        private int _callIndex;

        public List<int> RequestSizes { get; } = [];

        public int StreamCalls => _callIndex;

        public ProviderId ProviderId => ProviderId.Create("test");

        public async IAsyncEnumerable<LlmEvent> StreamAsync(
            LlmRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            RequestSizes.Add(request.Messages.Count);
            var events = calls[Math.Min(_callIndex, calls.Length - 1)];
            _callIndex++;
            foreach (var e in events)
            {
                yield return e;
                await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            }
        }

        public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Result.Success<IReadOnlyList<ModelInfo>>(new[] { CompactibleModel }));
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
            SteeringQueue = Channel.CreateUnbounded<AgentMessage>();
        }

        public Session Session { get; }
        public IReadOnlyList<AgentMessage> Messages => _messages;
        public Channel<AgentMessage> SteeringQueue { get; }

        public Task AppendMessageAsync(AgentMessage message, CancellationToken ct = default)
        {
            _messages.Add(message);
            return Task.CompletedTask;
        }

        public Task UpdateStatsAsync(Usage usage, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>
    ///     Session context wrapper that captures every <see cref="UpdateStatsAsync" /> call
    ///     so tests can assert the usage flowed through.
    /// </summary>
    private sealed class CapturingStatsSession : ISessionContext
    {
        private readonly List<Usage> _captured;
        private readonly TestSessionContext _inner;

        public CapturingStatsSession(Session session, IReadOnlyList<AgentMessage> messages, List<Usage> captured)
        {
            _captured = captured;
            _inner = new TestSessionContext(session, messages);
        }

        public Session Session => _inner.Session;
        public IReadOnlyList<AgentMessage> Messages => _inner.Messages;
        public Channel<AgentMessage> SteeringQueue => _inner.SteeringQueue;
        public Task AppendMessageAsync(AgentMessage message, CancellationToken ct = default) => _inner.AppendMessageAsync(message, ct);
        public Task UpdateStatsAsync(Usage usage, CancellationToken ct = default)
        {
            _captured.Add(usage);
            return Task.CompletedTask;
        }
    }
}
