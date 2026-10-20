using System.Text.Json;
using System.Threading.Channels;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Agents;
using Harbor.Application.Permissions;
using Harbor.Application.Resilience;
using Harbor.Application.Sessions;
using Harbor.Application.Tests.Fakes;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TestSessionContext = Harbor.Application.Tests.Fakes.TestSessionContext;

namespace Harbor.Application.Tests;

/// <summary>
///     #1751: a <c>tool_use</c> without a matching <c>tool_result</c> is a
///     provider 400, not a cosmetic gap — Anthropic wants every tool_use
///     answered in the next message, OpenAI wants a <c>tool</c> role message
///     with the same id — and the session that carries one cannot be resumed.
/// </summary>
/// <remarks>
///     <para>
///         The degraded turn is the guaranteed case: the runner persists the
///         assistant message with its tool calls BEFORE the tools run, and the
///         transient-budget branch ends the turn with an EMPTY result list, so
///         the transcript kept a tool_use with nothing to answer it.
///     </para>
///     <para>
///         The two codes are not decoration — they answer the only question
///         that matters when the model reads one back: may this call be
///         retried? <c>TOOL_NOT_STARTED</c> (the runtime knows the call never
///         ran) is safe to retry; <c>TOOL_OUTCOME_UNKNOWN</c> (the transcript
///         cannot prove it) is not.
///     </para>
/// </remarks>
public class DanglingToolCall1751Tests
{
    /// <summary>
    ///     Context that presses Stop as the first persist lands — the cancel
    ///     between the stream and the tool execution.
    /// </summary>
    private sealed class CancelOnFirstAppendContext : ISessionContext
    {
        private readonly TestSessionContext _inner;
        private readonly CancellationTokenSource _cts;
        private int _appends;

        public CancelOnFirstAppendContext(Session session, CancellationTokenSource cts)
        {
            _inner = new TestSessionContext(session);
            _cts = cts;
        }

        public Session Session => _inner.Session;

        public IReadOnlyList<AgentMessage> Messages => _inner.Messages;

        public Channel<AgentMessage> SteeringQueue => _inner.SteeringQueue;

        public Task AppendMessageAsync(AgentMessage message, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _appends) == 1)
            {
                _cts.Cancel();
            }

            return _inner.AppendMessageAsync(message, ct);
        }

        public Task UpdateStatsAsync(Usage usage, Pricing pricing, CancellationToken ct = default) =>
            _inner.UpdateStatsAsync(usage, pricing, ct);
    }

    private static AgentLoop LoopWithClock(
        ScriptedLlmClient client,
        FakeEventBus bus,
        FakeToolRegistry? tools = null)
    {
        var agent = TestAgents.AllowAll();
        var agents = new FakeAgentRegistry(agent);
        return new AgentLoop(
            new FakeProviderRegistry(client),
            tools ?? new FakeToolRegistry(),
            agents,
            new StubSystemPromptBuilder(),
            new FakeCompactionService(),
            new FakeTokenTracker(),
            new RetryPolicy(new ManualTimeProvider(fireTimerOnCreate: true)),
            bus,
            new PermissionService(agents, NullLogger<PermissionService>.Instance),
            new MessageConverter(),
            NullLogger<AgentLoop>.Instance);
    }

    private static TestSessionContext NewSession() =>
        new(Session.Create("/tmp/harbor-1751-dangling", "code", "test", "test-model"));

    /// <summary>
    ///     The tool calls of the transcript that no result answers — the
    ///     dangling ones. Empty means the transcript is valid for a provider.
    /// </summary>
    private static List<string> DanglingToolCallIds(IReadOnlyList<AgentMessage> messages)
    {
        var answered = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < messages.Count; i++)
        {
            if (messages[i] is ToolResultMessage results)
            {
                for (int j = 0; j < results.Results.Count; j++)
                {
                    answered.Add(results.Results[j].ToolCallId);
                }
            }
        }

        var dangling = new List<string>();
        for (int i = 0; i < messages.Count; i++)
        {
            if (messages[i] is not AssistantMessage assistant)
            {
                continue;
            }

            for (int p = 0; p < assistant.Parts.Count; p++)
            {
                if (assistant.Parts[p] is ToolCallPart call && !answered.Contains(call.Id))
                {
                    dangling.Add(call.Id);
                }
            }
        }

        return dangling;
    }

    /// <summary>
    ///     A stream that asks for a tool and then dies on a transient failure:
    ///     the tool call is already in the assistant message, and the turn
    ///     degrades before the dispatcher is ever reached.
    /// </summary>
    private static ScriptedLlmClient DegradingClient() => new(
    [
        new ToolCallStartEvent("call-1", "counter"),
        new ToolCallDeltaEvent("call-1", "{}"),
        new StepFinishEvent(0, "tool_use", new Usage(3, 4)),
        new ErrorEvent("network blip", Kind: ProviderErrorKind.Network),
    ]);

    [Test]
    public async Task DegradedTurn_PersistsAPairedToolResult_NoDanglingToolUse()
    {
        // The reproduction: the degraded branch persisted the assistant message
        // with its tool call and ended the turn with an EMPTY result list, so
        // the saved session held a tool_use with no tool_result — a provider
        // 400 on the next turn and a session that cannot be resumed.
        var bus = new FakeEventBus();
        var loop = LoopWithClock(DegradingClient(), bus);
        var session = NewSession();

        var result = await loop.RunAsync(session, TestAgents.AllowAll());

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(DanglingToolCallIds(session.Messages)).IsEmpty();

        var closures = session.Messages.OfType<ToolResultMessage>().ToList();
        await Assert.That(closures.Count).IsEqualTo(1);
        var entry = closures[0].Results.Single();
        await Assert.That(entry.ToolCallId).IsEqualTo("call-1");
        await Assert.That(entry.ToolName).IsEqualTo("counter");
        await Assert.That(entry.IsError).IsTrue();
        // The runtime KNOWS the call never started — the dispatcher was never
        // reached — so the model is told the retry is safe.
        await Assert.That(entry.Output).Contains(UnresolvedToolCall.NotStartedCode);
    }

    [Test]
    public async Task DegradedTurn_PublishesAToolEndEvent_SoTheUiSeesWhichCallIsUnresolved()
    {
        // The degraded turn must not look like a normal end: the unresolved
        // call is named, with the code, on the event the dispatcher publishes
        // for a call that failed.
        var bus = new FakeEventBus();
        var loop = LoopWithClock(DegradingClient(), bus);
        var session = NewSession();

        await loop.RunAsync(session, TestAgents.AllowAll());

        var ends = bus.Events.OfType<ToolExecutionEndEvent>().ToList();
        await Assert.That(ends.Count).IsEqualTo(1);
        await Assert.That(ends[0].ToolCallId).IsEqualTo("call-1");
        await Assert.That(ends[0].IsError).IsTrue();
        await Assert.That(ends[0].Result.Output).Contains(UnresolvedToolCall.NotStartedCode);

        // The turn end carries the closure too, so a consumer that reads the
        // turn's results sees the same pairing the session holds.
        var turnEnd = bus.Events.OfType<TurnEndEvent>().Single();
        await Assert.That(turnEnd.ToolResults.Count).IsEqualTo(1);
        await Assert.That(turnEnd.ToolResults[0].Results.Single().ToolCallId).IsEqualTo("call-1");
    }

    [Test]
    public async Task NormalTurn_KeepsEveryToolUsePairedWithAToolResult()
    {
        // The other half of the invariant: the repaired degraded path must not
        // have changed the ordinary one. The tool runs, its result is the tool's
        // own output, and nothing is synthesized.
        var counter = new CountingTool();
        var client = new ScriptedLlmClient(
        [
            new ToolCallStartEvent("call-1", "counter"),
            new ToolCallDeltaEvent("call-1", "{}"),
            new StepFinishEvent(0, "tool_use", new Usage(3, 4)),
        ],
        [
            new TextDeltaEvent("t", "done"),
            new StepFinishEvent(1, "stop", new Usage(1, 1)),
        ]);
        var bus = new FakeEventBus();
        var loop = LoopWithClock(client, bus, new FakeToolRegistry(counter));
        var session = NewSession();

        var result = await loop.RunAsync(session, TestAgents.AllowAll());

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(counter.Executions).IsEqualTo(1);
        await Assert.That(DanglingToolCallIds(session.Messages)).IsEmpty();

        var results = session.Messages.OfType<ToolResultMessage>().ToList();
        await Assert.That(results.Count).IsEqualTo(1);
        var entry = results[0].Results.Single();
        await Assert.That(entry.ToolCallId).IsEqualTo("call-1");
        await Assert.That(entry.IsError).IsFalse();
        await Assert.That(entry.Output).DoesNotContain(UnresolvedToolCall.NotStartedCode);
    }

    [Test]
    public async Task CancelledTurn_ClosesTheToolCall_TheSessionStaysResumable()
    {
        // The second path that used to leave a hole: the cancel landed after
        // the stream, so the assistant message with its tool call was already
        // persisted and the turn ended with no result for it.
        var client = new ScriptedLlmClient(
        [
            new ToolCallStartEvent("call-1", "counter"),
            new ToolCallDeltaEvent("call-1", "{}"),
            new StepFinishEvent(0, "tool_use", new Usage(3, 4)),
        ]);
        var bus = new FakeEventBus();
        var loop = LoopWithClock(client, bus);
        using var cts = new CancellationTokenSource();
        var session = new CancelOnFirstAppendContext(
            Session.Create("/tmp/harbor-1751-cancel", "code", "test", "test-model"), cts);

        var result = await loop.RunAsync(session, TestAgents.AllowAll(), cts.Token);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(DanglingToolCallIds(session.Messages)).IsEmpty();
        var closures = session.Messages.OfType<ToolResultMessage>().ToList();
        await Assert.That(closures.Count).IsEqualTo(1);
        await Assert.That(closures[0].Results.Single().ToolCallId).IsEqualTo("call-1");
        await Assert.That(closures[0].Results.Single().Output).Contains(UnresolvedToolCall.NotStartedCode);
    }

    [Test]
    public async Task MessageConverter_ClosesADanglingToolUse_AsOutcomeUnknown()
    {
        // The repair for a transcript this build never wrote — a session whose
        // process was killed mid-call, or one written by an older Harbor. The
        // transcript cannot prove the call never ran, so the code is the
        // conservative one: the model may NOT retry blindly.
        var converter = new MessageConverter();
        var assistant = new AssistantMessage(
            "a1",
            "session-1",
            DateTimeOffset.UtcNow,
            [new ToolCallPart("call-1", "read", TestArgs.Empty())],
            StopReason.ToolUse,
            new Usage(1, 1),
            "test-model");

        var result = converter.ToLlmMessages([assistant]);

        await Assert.That(result.Count).IsEqualTo(2);
        await Assert.That(result[0]).IsTypeOf<LlmAssistantMessage>();
        await Assert.That(result[1]).IsTypeOf<LlmToolResultMessage>();
        var closure = (LlmToolResultMessage)result[1];
        await Assert.That(closure.ToolCallId).IsEqualTo("call-1");
        await Assert.That(closure.ToolName).IsEqualTo("read");
        await Assert.That(closure.IsError).IsTrue();
        await Assert.That(closure.Output).Contains(UnresolvedToolCall.OutcomeUnknownCode);
    }

    [Test]
    public async Task MessageConverter_LeavesAPairedHistory_Alone()
    {
        // The normal path must not gain a second result for a call that
        // already has one — a duplicated tool_result is as invalid as a
        // missing one.
        var converter = new MessageConverter();
        var assistant = new AssistantMessage(
            "a1",
            "session-1",
            DateTimeOffset.UtcNow,
            [new ToolCallPart("call-1", "read", TestArgs.Empty())],
            StopReason.ToolUse,
            new Usage(1, 1),
            "test-model");
        var answered = new ToolResultMessage(
            "r1",
            "session-1",
            DateTimeOffset.UtcNow,
            [new ToolResultEntry("call-1", "read", "file contents", false)]);

        var result = converter.ToLlmMessages([assistant, answered]);

        await Assert.That(result.Count).IsEqualTo(2);
        await Assert.That(result[0]).IsTypeOf<LlmAssistantMessage>();
        await Assert.That(result[1]).IsTypeOf<LlmToolResultMessage>();
        var closure = (LlmToolResultMessage)result[1];
        await Assert.That(closure.Output).IsEqualTo("file contents");
        await Assert.That(closure.IsError).IsFalse();
    }

}
