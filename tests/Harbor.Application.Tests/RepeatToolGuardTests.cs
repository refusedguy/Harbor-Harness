using System.Text.Json;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Application.Agents;
using Harbor.Application.Permissions;
using Harbor.Application.Resilience;
using Harbor.Application.Sessions;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;

using FakeToolBase = Harbor.TestKit.Generated.FakeTool; // #1769: generated double

namespace Harbor.Application.Tests;

/// <summary>
///     Issue #1756 — the repeat guard: it counts CONSECUTIVE identical tool
///     calls (the same tool, the same arguments) and, once the chain reaches
///     the configured threshold, tells the MODEL that it is repeating itself.
/// </summary>
/// <remarks>
/// <para>
///     The guard is ADVISORY. It never blocks a call and never ends a run — a
///     stuck agent gets the chance to change its mind on its own, which is the
///     whole point of counting instead of limiting.
/// </para>
/// <para>
///     The counter lives in the RUN (one instance per run, created by the
///     loop), never in a process-wide place: a shared counter would attribute
///     one run's repeats to the next, and two sessions running side by side
///     would count each other's calls.
/// </para>
/// </remarks>
public class RepeatToolGuardTests
{
    /// <summary>The threshold the guard is exercised with in most cases.</summary>
    private const int Threshold = 3;

    /// <summary>Arguments of the call the guard is counting.</summary>
    private const string Args = """{"path":"a.txt","limit":10}""";

    // ── the unit under test ────────────────────────────────────────────────

    [Test]
    public async Task Observe_BelowTheThreshold_SaysNothing()
    {
        var guard = new RepeatToolGuard(Threshold);

        await Assert.That(guard.Observe("read", Json(Args)).HasValue).IsFalse()
            .Because("the first call of a chain is not a repeat — there is nothing to say about it");
        await Assert.That(guard.Observe("read", Json(Args)).HasValue).IsFalse()
            .Because("two identical calls are still below the threshold of three");
        await Assert.That(guard.ConsecutiveCount).IsEqualTo(2);
    }

    [Test]
    public async Task Observe_AtTheThreshold_TellsTheModel()
    {
        var guard = new RepeatToolGuard(Threshold);

        guard.Observe("read", Json(Args));
        guard.Observe("read", Json(Args));
        var reminder = guard.Observe("read", Json(Args));

        await Assert.That(reminder.HasValue).IsTrue()
            .Because("the third consecutive identical call is the one the model is told about");
        await Assert.That(reminder.Value.Count).IsEqualTo(3);
        await Assert.That(reminder.Value.ToolName).IsEqualTo("read");
        await Assert.That(reminder.Value.Text).Contains("repeated tool call")
            .Because("the text has to say what happened, or the model cannot act on it");
    }

    [Test]
    public async Task Observe_DifferentArguments_StartsANewChain()
    {
        var guard = new RepeatToolGuard(Threshold);

        guard.Observe("read", Json(Args));
        guard.Observe("read", Json(Args));
        var reminder = guard.Observe("read", Json("""{"path":"b.txt","limit":10}"""));

        await Assert.That(reminder.HasValue).IsFalse()
            .Because("different arguments are a different call — the model changed its mind, which is the opposite of a loop");
        await Assert.That(guard.ConsecutiveCount).IsEqualTo(1);
    }

    [Test]
    public async Task Observe_DifferentTool_SameArguments_StartsANewChain()
    {
        var guard = new RepeatToolGuard(Threshold);

        guard.Observe("read", Json(Args));
        guard.Observe("read", Json(Args));
        var reminder = guard.Observe("grep", Json(Args));

        await Assert.That(reminder.HasValue).IsFalse()
            .Because("the chain is per TOOL: the same arguments to another tool are another call");
        await Assert.That(guard.ChainToolName).IsEqualTo("grep");
    }

    [Test]
    public async Task Observe_ArgumentsInAnotherOrder_IsTheSameCall()
    {
        var guard = new RepeatToolGuard(Threshold);

        guard.Observe("read", Json("""{"path":"a.txt","limit":10}"""));
        guard.Observe("read", Json("""{"limit":10,"path":"a.txt"}"""));
        var reminder = guard.Observe("read", Json("""{"path":"a.txt","limit":10}"""));

        await Assert.That(reminder.HasValue).IsTrue()
            .Because("the canonical form sorts the keys, so the order the arguments were written in does not make a new call");
        await Assert.That(reminder.Value.Count).IsEqualTo(3);
    }

    [Test]
    public async Task Observe_PastTheThreshold_EscalatesTheWording()
    {
        var guard = new RepeatToolGuard(Threshold);

        guard.Observe("read", Json(Args));
        guard.Observe("read", Json(Args));
        var first = guard.Observe("read", Json(Args));
        var second = guard.Observe("read", Json(Args));

        await Assert.That(first.Value.Escalated).IsFalse()
            .Because("the first crossing of the threshold is the gentle nudge");
        await Assert.That(second.Value.Escalated).IsTrue()
            .Because("a model that ignored the first nudge gets the firmer wording");
        await Assert.That(second.Value.Text).Contains(RepeatToolGuard.Canonicalize(Json(Args)))
            .Because("the detailed wording quotes the arguments back, so the model can see WHICH call repeats");
    }

    [Test]
    public async Task Observe_DisabledThreshold_NeverSaysAnything()
    {
        var guard = new RepeatToolGuard(RepeatToolGuard.DisabledThreshold);

        for (int i = 0; i < 10; i++)
        {
            await Assert.That(guard.Observe("read", Json(Args)).HasValue).IsFalse()
                .Because("a threshold of zero turns the guard off — the chain is counted, nothing is said");
        }
    }

    [Test]
    public async Task SyncUserContext_UserMessage_EndsTheChain()
    {
        var guard = new RepeatToolGuard(Threshold);
        guard.SyncUserContext([UserText("do the thing")]);

        guard.Observe("read", Json(Args));
        guard.Observe("read", Json(Args));
        await Assert.That(guard.ConsecutiveCount).IsEqualTo(2);

        // The user answers; the chain is over — repetition ACROSS an
        // interjection is not the same loop.
        guard.SyncUserContext([UserText("do the thing"), UserText("again, please")]);

        await Assert.That(guard.ConsecutiveCount).IsEqualTo(0)
            .Because("a user interjection ends the chain, so the next call starts a fresh one");
        var reminder = guard.Observe("read", Json(Args));
        await Assert.That(reminder.HasValue).IsFalse()
            .Because("after the reset the count starts at one again");
    }

    [Test]
    public async Task SyncUserContext_FirstCall_OnlyRecordsTheHistory()
    {
        var guard = new RepeatToolGuard(Threshold);

        guard.SyncUserContext([UserText("do the thing")]);

        await Assert.That(guard.ConsecutiveCount).IsEqualTo(0)
            .Because("the run's own prompt is already in the history; treating it as an interjection would reset the chain every turn");
    }

    [Test]
    public async Task SyncUserContext_NoUserMessage_KeepsTheChain()
    {
        var guard = new RepeatToolGuard(Threshold);
        guard.SyncUserContext([UserText("do the thing")]);

        guard.Observe("read", Json(Args));
        guard.Observe("read", Json(Args));
        guard.SyncUserContext([UserText("do the thing")]);

        await Assert.That(guard.ConsecutiveCount).IsEqualTo(2)
            .Because("nothing changed in the history, so the chain continues");
    }

    [Test]
    public async Task Reset_ForgetsTheChain()
    {
        var guard = new RepeatToolGuard(Threshold);
        guard.Observe("read", Json(Args));
        guard.Observe("read", Json(Args));

        guard.Reset();

        await Assert.That(guard.ConsecutiveCount).IsEqualTo(0);
        await Assert.That(guard.ChainToolName).IsNull();
        await Assert.That(guard.Observe("read", Json(Args)).HasValue).IsFalse();
    }

    [Test]
    public async Task TwoGuards_DoNotShareTheirChains()
    {
        var first = new RepeatToolGuard(Threshold);
        var second = new RepeatToolGuard(Threshold);

        first.Observe("read", Json(Args));
        first.Observe("read", Json(Args));

        await Assert.That(second.ConsecutiveCount).IsEqualTo(0)
            .Because("the counter is per RUN — one run's repeats are not another run's repeats");
    }

    [Test]
    public async Task Canonicalize_SortsTheKeys_AndKeepsTheArrayOrder()
    {
        await Assert.That(RepeatToolGuard.Canonicalize(Json("""{"b":2,"a":1}""")))
            .IsEqualTo(RepeatToolGuard.Canonicalize(Json("""{"a":1,"b":2}""")))
            .Because("the canonical form is what makes two writings of the same call ONE call");

        await Assert.That(RepeatToolGuard.Canonicalize(Json("""{"xs":[1,2]}""")))
            .IsNotEqualTo(RepeatToolGuard.Canonicalize(Json("""{"xs":[2,1]}""")))
            .Because("the order of an array is meaning, not noise");
    }

    // ── through the loop ───────────────────────────────────────────────────

    [Test]
    public async Task RunAsync_RepeatedToolCall_CarriesTheReminderToTheModel()
    {
        var tool = new CountingTool();
        var client = new ScriptedLlmClient(
        [
            ToolCallTurn("call-1"),
            ToolCallTurn("call-2"),
            ToolCallTurn("call-3"),
            [TestEvents.TextDelta(delta: "done"), TestEvents.StepFinish()]
        ]);
        var agent = Agent(Threshold);

        var loop = NewLoop(client, agent, tool);
        var session = new TestSessionContext(
            Session.Create("/tmp/harbor-repeat-guard-tests", "code", "test", "test-model"));

        var result = await loop.RunAsync(session, agent).ConfigureAwait(false);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(tool.Executions).IsEqualTo(3)
            .Because("the guard is ADVISORY: it never blocks a call, it only tells the model");

        // The request after the third call carries the reminder...
        string afterThird = TestMessages.RenderText(client.Requests[3]);
        await Assert.That(afterThird).Contains("repeated tool call");
        await Assert.That(afterThird).Contains("peek")
            .Because("the reminder names the tool that repeated, so the model knows which call it is");

        // ...and the request after the second one does not: the chain was
        // still below the threshold there.
        string afterSecond = TestMessages.RenderText(client.Requests[2]);
        await Assert.That(afterSecond).DoesNotContain("repeated tool call")
            .Because("below the threshold the model is told nothing");
    }

    [Test]
    public async Task RunAsync_ThresholdDisabled_NeverCarriesTheReminder()
    {
        var tool = new CountingTool();
        var client = new ScriptedLlmClient(
        [
            ToolCallTurn("call-1"),
            ToolCallTurn("call-2"),
            ToolCallTurn("call-3"),
            [TestEvents.TextDelta(delta: "done"), TestEvents.StepFinish()]
        ]);
        var agent = Agent(RepeatToolGuard.DisabledThreshold);

        var loop = NewLoop(client, agent, tool);
        var session = new TestSessionContext(
            Session.Create("/tmp/harbor-repeat-guard-tests", "code", "test", "test-model"));

        var result = await loop.RunAsync(session, agent).ConfigureAwait(false);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(tool.Executions).IsEqualTo(3)
            .Because("a disabled guard changes nothing about the run");
        await Assert.That(TestMessages.RenderText(client.Requests[3])).DoesNotContain("repeated tool call")
            .Because("a threshold of zero turns the guard off");
    }

    // ── helpers ────────────────────────────────────────────────────────────

    /// <summary>The parsed arguments of a call.</summary>
    private static JsonElement Json(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>A user message in the history.</summary>
    private static UserMessage UserText(string content) => TestMessages.User(content);

    /// <summary>One turn of the script: a call to <c>peek</c> with fixed arguments.</summary>
    private static LlmEvent[] ToolCallTurn(string callId) =>
    [
        TestEvents.ToolCallStart(id: callId, toolName: "peek"),
        TestEvents.ToolCallDelta(id: callId, argsDelta: """{"path":"a.txt"}"""),
        TestEvents.StepFinish(finishReason: "tool_use", usage: new Usage(4, 2))
    ];

    /// <summary>An agent that allows everything and runs the repeat guard.</summary>
    private static AgentDefinition Agent(int repeatToolThreshold) => new AgentDefinition(
        AgentName.Create("code"),
        "Code",
        "repeat-guard harness",
        "test-model",
        "test",
        new PermissionRuleset(new PermissionRule[] { new("*", "*", PermissionAction.Allow) }),
        MaxSteps: 10)
    {
        RepeatToolThreshold = repeatToolThreshold,
    };

    /// <summary>A loop over the scripted client, the agent and the one tool.</summary>
    private static AgentLoop NewLoop(ScriptedLlmClient client, AgentDefinition agent, ITool tool)
    {
        var agents = new FakeAgentRegistry(agent);
        return new AgentLoop(
            new FakeProviderRegistry(client),
            new FakeToolRegistry(tool),
            agents,
            new StubSystemPromptBuilder(),
            new FakeCompactionService(),
            new FakeTokenTracker(),
            new RetryPolicy(),
            new FakeEventBus(),
            new PermissionService(agents, NullLogger<PermissionService>.Instance),
            new MessageConverter(),
            NullLogger<AgentLoop>.Instance);
    }

    /// <summary>The tool the loop calls — it counts its own executions.</summary>
    private sealed class CountingTool : FakeToolBase
    {
        public int Executions { get; private set; }

        public CountingTool()
        {
            Name = ToolName.Create("peek");
            DisplayName = "Peek";
            Description = "Returns a fixed line.";
        }

        public override Task<ToolResult> ExecuteAsync(
            JsonElement args,
            ToolContext context,
            CancellationToken cancellationToken = default)
        {
            Executions++;
            return Task.FromResult(ToolResult.Success("the file content"));
        }
    }
}
