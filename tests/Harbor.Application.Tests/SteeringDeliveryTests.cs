using System.Text.Json;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Application.Tests.Fakes;
using Harbor.TestKit;
using CSharpFunctionalExtensions;
using Harbor.Application.Agents;
using Harbor.Application.Permissions;
using Harbor.Application.Resilience;
using Harbor.Application.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;

namespace Harbor.Application.Tests;

/// <summary>
///     Ф2/B1-B4: steering delivery contract. A prompt submitted while a run is
///     active is steered into the CURRENT run (not dropped with "busy"), the
///     loop drains the queue mid-turn (after tool results) as well as at turn
///     boundaries, FIFO order is preserved, and cancellation still terminates
///     the run cleanly.
/// </summary>
public class SteeringDeliveryTests
{
    private static AgentDefinition AllowAllAgent() => new(
        AgentName.Create("code"),
        "Code",
        "Steering harness agent",
        "test-model",
        "test",
        new PermissionRuleset(new PermissionRule[] { new("*", "*", PermissionAction.Allow) }));

    /// <summary>Tool whose execution enqueues steering messages — simulates a steer arriving while the run is busy.</summary>
    private sealed class SteeringTool(Fakes.TestSessionContext session, params string[] steeredTexts) : ITool
    {
        public ToolName Name => ToolName.Create("steerer");

        public string DisplayName => "Steerer";

        public string Description => "Enqueues steering messages during execution.";

        public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""{"type":"object"}""");

        public ExecutionMode ExecutionMode => ExecutionMode.Parallel;

        public string? PromptSnippet => null;

        public IReadOnlyList<string> PromptGuidelines => [];

        public Result ValidateArguments(JsonElement args) => Result.Success();

        public Task<ToolResult> ExecuteAsync(
            JsonElement args,
            ToolContext context,
            CancellationToken cancellationToken = default)
        {
            foreach (string text in steeredTexts)
            {
                session.EnqueueSteering(new UserMessage(
                    Guid.NewGuid().ToString("N"),
                    session.Session.Id,
                    DateTimeOffset.UtcNow,
                    text,
                    "user",
                    "test-model"));
            }

            return Task.FromResult(ToolResult.Success("steered"));
        }
    }

    /// <summary>Tool that cancels the supplied CTS during execution.</summary>
    private sealed class CancellingTool(CancellationTokenSource cts) : ITool
    {
        public ToolName Name => ToolName.Create("canceller");

        public string DisplayName => "Canceller";

        public string Description => "Cancels the run token.";

        public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""{"type":"object"}""");

        public ExecutionMode ExecutionMode => ExecutionMode.Parallel;

        public string? PromptSnippet => null;

        public IReadOnlyList<string> PromptGuidelines => [];

        public Result ValidateArguments(JsonElement args) => Result.Success();

        public Task<ToolResult> ExecuteAsync(
            JsonElement args,
            ToolContext context,
            CancellationToken cancellationToken = default)
        {
            cts.Cancel();
            return Task.FromResult(ToolResult.Success("cancelled"));
        }
    }

    [Test]
    public async Task RunAsync_SteerArrivesDuringToolExecution_ReachesNextRequestInCurrentRun()
    {
        var session = new Fakes.TestSessionContext(
            Session.Create("/tmp/harbor-steering-tests", "code", "test", "test-model"));
        var client = new ScriptedLlmClient(
        [
            new LlmEvent[]
            {
                new ToolCallStartEvent("call-1", "steerer"),
                new ToolCallDeltaEvent("call-1", "{}"),
                new StepFinishEvent(0, "tool_use", new Usage(4, 2))
            },
            new LlmEvent[]
            {
                new TextDeltaEvent("t", "done"),
                new StepFinishEvent(1, "stop", new Usage(1, 1))
            }
        ]);
        var agent = AllowAllAgent();
        var agents = new FakeAgentRegistry(agent);
        var loop = new AgentLoop(
            new FakeProviderRegistry(client),
            new FakeToolRegistry(new SteeringTool(session, "use --verbose next time")),
            agents,
            new StubSystemPromptBuilder(),
            new FakeCompactionService(),
            new FakeTokenTracker(),
            new RetryPolicy(),
            new FakeEventBus(),
            new PermissionService(agents, NullLogger<PermissionService>.Instance),
            new MessageConverter(),
            NullLogger<AgentLoop>.Instance);

        var result = await loop.RunAsync(session, agent);

        await Assert.That(result.IsSuccess).IsTrue();
        // The steered message must be part of the SECOND request of THIS run.
        string secondRequestText = RenderRequestText(client.Requests[1]);
        await Assert.That(secondRequestText).Contains("use --verbose next time");
        // …and it must be persisted AFTER the tool results (provider adjacency preserved).
        int toolResultIndex = IndexOfMessage<ToolResultMessage>(session.Messages);
        int steerIndex = IndexWhere(session.Messages, m => m is UserMessage u && u.Content.Contains("use --verbose"));
        await Assert.That(steerIndex).IsGreaterThan(toolResultIndex);
    }

    [Test]
    public async Task RunAsync_TwoSteersDuringExecution_PreserveFifoOrder()
    {
        var session = new Fakes.TestSessionContext(
            Session.Create("/tmp/harbor-steering-tests", "code", "test", "test-model"));
        var client = new ScriptedLlmClient(
        [
            new LlmEvent[]
            {
                new ToolCallStartEvent("call-1", "steerer"),
                new ToolCallDeltaEvent("call-1", "{}"),
                new StepFinishEvent(0, "tool_use", new Usage(4, 2))
            },
            new LlmEvent[]
            {
                new TextDeltaEvent("t", "done"),
                new StepFinishEvent(1, "stop", new Usage(1, 1))
            }
        ]);
        var agent = AllowAllAgent();
        var agents = new FakeAgentRegistry(agent);
        var loop = new AgentLoop(
            new FakeProviderRegistry(client),
            new FakeToolRegistry(new SteeringTool(session, "first-steer", "second-steer")),
            agents,
            new StubSystemPromptBuilder(),
            new FakeCompactionService(),
            new FakeTokenTracker(),
            new RetryPolicy(),
            new FakeEventBus(),
            new PermissionService(agents, NullLogger<PermissionService>.Instance),
            new MessageConverter(),
            NullLogger<AgentLoop>.Instance);

        _ = await loop.RunAsync(session, agent);

        List<int> indexes =
        [
            IndexWhere(session.Messages, m => m is UserMessage u && u.Content.Contains("first-steer")),
            IndexWhere(session.Messages, m => m is UserMessage u && u.Content.Contains("second-steer"))
        ];
        await Assert.That(indexes[0]).IsGreaterThanOrEqualTo(0);
        await Assert.That(indexes[1]).IsGreaterThan(indexes[0]);
    }

    [Test]
    public async Task RunAsync_CancelledFromWithinTool_ReturnsFailureWithoutHang()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var session = new Fakes.TestSessionContext(
            Session.Create("/tmp/harbor-steering-tests", "code", "test", "test-model"));
        var client = new ScriptedLlmClient(
        [
            new LlmEvent[]
            {
                new ToolCallStartEvent("call-1", "canceller"),
                new ToolCallDeltaEvent("call-1", "{}"),
                new StepFinishEvent(0, "tool_use", new Usage(4, 2))
            }
        ]);
        var agent = AllowAllAgent();
        var agents = new FakeAgentRegistry(agent);
        var loop = new AgentLoop(
            new FakeProviderRegistry(client),
            new FakeToolRegistry(new CancellingTool(cts)),
            agents,
            new StubSystemPromptBuilder(),
            new FakeCompactionService(),
            new FakeTokenTracker(),
            new RetryPolicy(),
            new FakeEventBus(),
            new PermissionService(agents, NullLogger<PermissionService>.Instance),
            new MessageConverter(),
            NullLogger<AgentLoop>.Instance);

        // The tool cancels the caller's token mid-execution: the loop must
        // exit through the cancellation path (failure), not hang or succeed.
        var result = await loop.RunAsync(session, agent, cts.Token);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("cancel");
    }

    [Test]
    public async Task DefaultAgent_PromptWhileRunning_SteersInsteadOfBusyFailure()
    {
        var session = Session.Create("/tmp/harbor-steering-agent-tests", "code", "test", "test-model");
        var store = new Harbor.Application.Tests.Fakes.FakeSessionStore(session);

        // A loop that BLOCKS until released — keeps the gate held so the
        // second prompt hits the busy path.
        var releaseRun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockingLoop = new BlockingDrainingAgentLoop(releaseRun.Task);

        var agent = AllowAllAgent();
        var subject = new DefaultAgent(store, blockingLoop, new FakeEventBus(), NullLogger<DefaultAgent>.Instance);
        subject.Initialize(session, agent);

        Task<Result> firstRun = subject.PromptAsync("start working");
        await Assert.That(subject.State.IsRunning).IsTrue();

        // B1: while running, this must SUCCEED immediately and route to Steer().
        Result second = await subject.PromptAsync("use --verbose please");

        await Assert.That(second.IsSuccess).IsTrue();
        await Assert.That(subject.State.IsRunning).IsTrue();

        releaseRun.TrySetResult();
        Result first = await firstRun;
        await Assert.That(first.IsSuccess).IsTrue();
        // The blocking loop drained the steering channel when its run finished.
        await Assert.That(blockingLoop.SteeredTexts.Count).IsEqualTo(1);
    }

    /// <summary>Concatenate every text block of every message in a request for substring assertions.</summary>
    private static string RenderRequestText(LlmRequest request)
    {
        var sb = new System.Text.StringBuilder();
        foreach (LlmMessage message in request.Messages)
        {
            if (message is LlmUserMessage user)
            {
                foreach (LlmContentBlock block in user.Content)
                {
                    if (block is LlmTextBlock text)
                    {
                        sb.Append(text.Text);
                    }
                }
            }
        }

        return sb.ToString();
    }

    private static int IndexOfMessage<T>(IReadOnlyList<AgentMessage> messages) where T : AgentMessage
    {
        for (int i = 0; i < messages.Count; i++)
        {
            if (messages[i] is T)
            {
                return i;
            }
        }

        return -1;
    }

    private static int IndexWhere(IReadOnlyList<AgentMessage> messages, Func<AgentMessage, bool> predicate)
    {
        for (int i = 0; i < messages.Count; i++)
        {
            if (predicate(messages[i]))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    ///     Minimal blocking IAgentLoop for the DefaultAgent busy-path test:
    ///     RunAsync parks on the supplied task; Steer() calls are recorded by
    ///     draining the steering channel the way the real loop would.
    /// </summary>
    private sealed class BlockingDrainingAgentLoop(Task gate) : IAgentLoop
    {
        public List<string> SteeredTexts { get; } = [];

        public async Task<Result> RunAsync(ISessionContext session, AgentDefinition agent, CancellationToken ct = default)
        {
            // Drain whatever was steered while parked (mirrors the real loop).
            while (session.SteeringQueue.Reader.TryRead(out AgentMessage? msg))
            {
                if (msg is UserMessage u)
                {
                    SteeredTexts.Add(u.Content);
                }
            }

            await gate.ConfigureAwait(false);

            while (session.SteeringQueue.Reader.TryRead(out AgentMessage? msg))
            {
                if (msg is UserMessage u)
                {
                    SteeredTexts.Add(u.Content);
                }
            }

            return ct.IsCancellationRequested
                ? Result.Failure("Agent run was cancelled.")
                : Result.Success();
        }
    }
}
