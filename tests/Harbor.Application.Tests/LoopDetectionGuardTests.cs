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

using FakeToolBase = Harbor.TestKit.Generated.FakeTool;
using TestSessionContext = Harbor.TestKit.TestSessionContext;

namespace Harbor.Application.Tests;

/// <summary>
///     Issue #2212 — windowed loop detection on top of the advisory
///     <see cref="RepeatToolGuard" />. The signature is name + arguments +
///     tool output; the sliding window is 10 calls and the sixth repeat of one
///     signature ends the run when the host opts into the hard stop.
/// </summary>
public class LoopDetectionGuardTests
{
    private const string PathA = """{"path":"a.txt"}""";
    private const string PathB = """{"path":"b.txt"}""";
    private const string Output = "the file content";

    // ── the detector itself ────────────────────────────────────────────────

    [Test]
    public async Task Signature_IncludesNameArgumentsAndOutput()
    {
        string baseline = LoopDetectionGuard.Signature("peek", Json(PathA), Output);
        string repeated = LoopDetectionGuard.Signature("peek", Json(PathA), Output);
        string otherTool = LoopDetectionGuard.Signature("grep", Json(PathA), Output);
        string otherArgs = LoopDetectionGuard.Signature("peek", Json(PathB), Output);
        string otherOutput = LoopDetectionGuard.Signature("peek", Json(PathA), "a different result");

        await Assert.That(repeated).IsEqualTo(baseline)
            .Because("the same interaction must hash to the same signature every time");
        await Assert.That(otherTool).IsNotEqualTo(baseline);
        await Assert.That(otherArgs).IsNotEqualTo(baseline);
        await Assert.That(otherOutput).IsNotEqualTo(baseline)
            .Because("the tool's result is part of the signature — same call with a new result is progress");
    }

    [Test]
    public async Task Observe_SixRepeatsInAWindowWithInterruptions_Detects()
    {
        var guard = new LoopDetectionGuard();
        // Six 'a' calls with four different calls between them: no consecutive
        // chain (the advisory guard stays silent), but the window sees a loop.
        string[] paths = ["a", "b", "a", "b", "a", "b", "a", "b", "a", "a"];

        LoopDetection? found = null;
        for (int i = 0; i < paths.Length; i++)
        {
            var detection = guard.Observe("peek", Json(Args(paths[i])), Output);
            if (i < paths.Length - 1)
            {
                await Assert.That(detection.HasValue).IsFalse()
                    .Because($"call {i + 1} is below the window's repeat threshold");
            }

            if (detection.HasValue)
            {
                found = detection.Value;
            }
        }

        await Assert.That(found).IsNotNull()
            .Because("the sixth repeat inside a ten-call window is the loop Crush reports");
        await Assert.That(found!.ToolName).IsEqualTo("peek");
        await Assert.That(found.Repeats).IsEqualTo(6);
        await Assert.That(found.WindowSize).IsEqualTo(LoopDetectionGuard.DefaultWindowSize);
        await Assert.That(found.Reason).Contains("loop detected");
        await Assert.That(found.Reason).Contains("peek");
    }

    [Test]
    public async Task Observe_FourRepeatsInAWindow_DoesNotDetect()
    {
        var guard = new LoopDetectionGuard();
        string[] paths = ["a", "b", "a", "b", "a", "b", "a", "b", "c", "d"];

        bool detected = false;
        for (int i = 0; i < paths.Length; i++)
        {
            var detection = guard.Observe("peek", Json(Args(paths[i])), Output);
            detected |= detection.HasValue;
        }

        await Assert.That(guard.Count).IsEqualTo(LoopDetectionGuard.DefaultWindowSize);
        await Assert.That(detected).IsFalse()
            .Because("four repeats are below the threshold of five — no gate was loosened");
    }

    [Test]
    public async Task Observe_ChangedOutput_DoesNotExtendTheRepeatCount()
    {
        var guard = new LoopDetectionGuard();
        for (int i = 0; i < LoopDetectionGuard.DefaultWindowSize; i++)
        {
            await Assert.That(guard.Observe("peek", Json(PathA), $"result {i}").HasValue).IsFalse();
        }

        await Assert.That(guard.Count).IsEqualTo(LoopDetectionGuard.DefaultWindowSize)
            .Because("every call still occupies a slot even when its signature is different");
    }

    // ── through the loop ───────────────────────────────────────────────────

    [Test]
    public async Task RunAsync_HardStop_SixInterleavedRepeats_EndsTheRunWithAReason()
    {
        var tool = new CountingTool();
        var client = new ScriptedLlmClient(ToolTurns(["a", "b", "a", "b", "a", "b", "a", "b", "a", "a"]));
        var bus = new FakeEventBus();
        var agent = Agent();

        var loop = NewLoop(client, agent, tool, bus, hardStopOnLoop: true);
        var session = new TestSessionContext(
            Session.Create("/tmp/harbor-loop-detection-tests", "code", "test", "test-model"));

        var result = await loop.RunAsync(session, agent).ConfigureAwait(false);

        await Assert.That(result).IsSuccessful();
        await Assert.That(tool.Executions).IsEqualTo(10)
            .Because("the tenth call is the sixth repeat — the run stops on it");
        await Assert.That(client.StreamCalls).IsEqualTo(10)
            .Because("a hard stop must not ask the model for another turn");
        AgentErrorEvent? reason = FindReason(bus);
        await Assert.That(reason).IsNotNull()
            .Because("the reason rides the event bus so the TUI can show why the run ended");
        await Assert.That(reason!.Message).Contains("loop detected");
        await Assert.That(reason.Message).Contains("peek");
        await Assert.That(HasEvent<AgentEndEvent>(bus)).IsTrue()
            .Because("the run still closes with its terminal fact");
    }

    [Test]
    public async Task RunAsync_HardStop_FourRepeats_DoesNotEndTheRun()
    {
        var tool = new CountingTool();
        var client = new ScriptedLlmClient(ToolTurns(["a", "b", "a", "b", "a", "b", "a", "b", "c", "d"]));
        var bus = new FakeEventBus();
        var agent = Agent();

        var loop = NewLoop(client, agent, tool, bus, hardStopOnLoop: true);
        var session = new TestSessionContext(
            Session.Create("/tmp/harbor-loop-detection-tests", "code", "test", "test-model"));

        var result = await loop.RunAsync(session, agent).ConfigureAwait(false);

        await Assert.That(result).IsSuccessful();
        await Assert.That(tool.Executions).IsEqualTo(10);
        await Assert.That(client.StreamCalls).IsEqualTo(11)
            .Because("the run reached the quiet text turn — nothing stopped it");
        await Assert.That(FindReason(bus)).IsNull()
            .Because("four repeats are below the threshold");
    }

    [Test]
    public async Task RunAsync_HardStopOff_SixRepeats_KeepsTheAdvisoryBehaviour()
    {
        var tool = new CountingTool();
        var client = new ScriptedLlmClient(ToolTurns(["a", "b", "a", "b", "a", "b", "a", "b", "a", "a"]));
        var bus = new FakeEventBus();
        var agent = Agent();

        var loop = NewLoop(client, agent, tool, bus, hardStopOnLoop: false);
        var session = new TestSessionContext(
            Session.Create("/tmp/harbor-loop-detection-tests", "code", "test", "test-model"));

        var result = await loop.RunAsync(session, agent).ConfigureAwait(false);

        await Assert.That(result).IsSuccessful();
        await Assert.That(client.StreamCalls).IsEqualTo(11)
            .Because("the default is advisory: no new stop, no new event, no changed scenario");
        await Assert.That(FindReason(bus)).IsNull();
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static JsonElement Json(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string Args(string path) => $$"""{"path":"{{path}}.txt"}""";

    private static LlmEvent[][] ToolTurns(IReadOnlyList<string> paths)
    {
        var turns = new LlmEvent[paths.Count + 1][];
        for (int i = 0; i < paths.Count; i++)
        {
            string id = $"call-{i + 1}";
            turns[i] = new LlmEvent[]
            {
                TestEvents.ToolCallStart(id: id, toolName: "peek"),
                TestEvents.ToolCallDelta(id: id, argsDelta: Args(paths[i])),
                TestEvents.StepFinish(finishReason: "tool_use", usage: new Usage(4, 2))
            };
        }

        turns[paths.Count] = new LlmEvent[] { TestEvents.TextDelta(delta: "done"), TestEvents.StepFinish() };
        return turns;
    }

    private static AgentDefinition Agent() => new AgentDefinition(
        AgentName.Create("code"),
        "Code",
        "loop-detection harness",
        "test-model",
        "test",
        new PermissionRuleset(new PermissionRule[] { new("*", "*", PermissionAction.Allow) }),
        MaxSteps: 20);

    private static AgentLoop NewLoop(
        ScriptedLlmClient client,
        AgentDefinition agent,
        ITool tool,
        FakeEventBus bus,
        bool hardStopOnLoop)
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
            bus,
            new PermissionService(agents, NullLogger<PermissionService>.Instance),
            new MessageConverter(),
            NullLogger<AgentLoop>.Instance,
            hardStopOnLoop: hardStopOnLoop);
    }

    private static AgentErrorEvent? FindReason(FakeEventBus bus)
    {
        foreach (AgentEvent e in bus.Events)
        {
            if (e is AgentErrorEvent error)
            {
                return error;
            }
        }

        return null;
    }

    private static bool HasEvent<TEvent>(FakeEventBus bus) where TEvent : AgentEvent
    {
        foreach (AgentEvent e in bus.Events)
        {
            if (e is TEvent)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The tool the loop calls. Its output is fixed: only args differ.</summary>
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
            return Task.FromResult(ToolResult.Success(Output));
        }
    }
}
