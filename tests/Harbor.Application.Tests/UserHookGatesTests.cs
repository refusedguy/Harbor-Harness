using System.Text.Json;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Application.Agents;
using Harbor.Application.Hooks;
using Harbor.Application.Permissions;
using Harbor.Application.Resilience;
using Harbor.Application.Sessions;
using Harbor.Application.Tests.Fakes;
using Harbor.TestKit;
using TestSessionContext = Harbor.TestKit.TestSessionContext;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;

namespace Harbor.Application.Tests;

/// <summary>
///     Issue #1101 (god <c>AgentLoop</c>, slice 2): user-hook gates
///     (<c>SessionEnd</c> / <c>UserPromptSubmit</c> / <c>SessionStart</c>)
///     moved verbatim to <c>UserHookGates</c>, keeping forwarders in the loop.
///     These tests pin the observable contract of the extract through the loop:
///     deny ends the run before the first turn, allow fires start/end, and a
///     throwing hook fails closed to deny.
/// </summary>
public class UserHookGatesTests
{
    private sealed class FakeHookRunner : IHookRunner
    {
        public int PromptCalls;
        public int StartCalls;
        public int EndCalls;
        public string? LastPrompt;
        public Func<string, string, CancellationToken, Task<HookVerdict>>? PromptImpl;

        public Task<HookVerdict> RunPreToolUseAsync(string toolName, JsonElement args, string sessionId, CancellationToken ct = default) =>
            Task.FromResult(HookVerdict.Allow);

        public Task RunPostToolUseAsync(string toolName, JsonElement args, ToolResult result, string sessionId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task RunSessionEndAsync(string sessionId, CancellationToken ct = default)
        {
            EndCalls++;
            return Task.CompletedTask;
        }

        public Task RunSessionStartAsync(string sessionId, string? agent = null, string? model = null, CancellationToken ct = default)
        {
            StartCalls++;
            return Task.CompletedTask;
        }

        public Task<HookVerdict> RunUserPromptSubmitAsync(string sessionId, string prompt, string? agent = null, string? model = null, CancellationToken ct = default)
        {
            PromptCalls++;
            LastPrompt = prompt;
            return PromptImpl is null
                ? Task.FromResult(HookVerdict.Allow)
                : PromptImpl(sessionId, prompt, ct);
        }

        public Task RunPreCompactAsync(string sessionId, string? agent = null, string? model = null, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private static AgentLoop CreateLoop(ScriptedLlmClient client, FakeEventBus bus, FakeHookRunner hooks)
    {
        var agent = TestAgents.AllowAll();
        var agents = new FakeAgentRegistry(agent);
        return new AgentLoop(
            new FakeProviderRegistry(client),
            new FakeToolRegistry(),
            agents,
            new StubSystemPromptBuilder(),
            new FakeCompactionService(),
            new FakeTokenTracker(),
            new RetryPolicy(),
            bus,
            new PermissionService(agents, NullLogger<PermissionService>.Instance),
            new MessageConverter(),
            NullLogger<AgentLoop>.Instance,
            hookRunner: hooks);
    }

    [Test]
    public async Task RunAsync_PromptDenied_EndsRunBeforeFirstTurn()
    {
        var client = new ScriptedLlmClient(
            [[TestEvents.TextDelta(delta: "hi"), TestEvents.StepFinish(usage: new Usage(1, 1))]]);
        var bus = new FakeEventBus();
        var hooks = new FakeHookRunner
        {
            PromptImpl = (_, _, _) => Task.FromResult(new HookVerdict(HookDecision.Deny, "nope", null)),
        };
        var loop = CreateLoop(client, bus, hooks);
        var session = Session.Create("/tmp/harbor-hook-gates-tests", "code", "test", "test-model");
        var context = new TestSessionContext(session, [AgentFixtures.UserMsg(session.Id, 0)]);

        var result = await loop.RunAsync(context, TestAgents.AllowAll());

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("nope");
        await Assert.That(hooks.PromptCalls).IsEqualTo(1);
        await Assert.That(hooks.LastPrompt).IsEqualTo("question-0");
        // Deny ends the run before a single model turn: no start hooks, no end hooks, no appended turns.
        await Assert.That(hooks.StartCalls).IsEqualTo(0);
        await Assert.That(hooks.EndCalls).IsEqualTo(0);
        await Assert.That(context.Messages.Count).IsEqualTo(1);
        var hasStart = false;
        var hasEnd = false;
        foreach (var evt in bus.Events)
        {
            if (evt is AgentStartEvent)
            {
                hasStart = true;
            }

            if (evt is AgentEndEvent)
            {
                hasEnd = true;
            }
        }

        await Assert.That(hasStart).IsTrue();
        await Assert.That(hasEnd).IsFalse();
    }

    [Test]
    public async Task RunAsync_PromptAllowed_FiresSessionStartAndEnd()
    {
        var client = new ScriptedLlmClient(
            [[TestEvents.TextDelta(delta: "hi"), TestEvents.StepFinish(usage: new Usage(1, 1))]]);
        var bus = new FakeEventBus();
        var hooks = new FakeHookRunner();
        var loop = CreateLoop(client, bus, hooks);
        var session = Session.Create("/tmp/harbor-hook-gates-tests", "code", "test", "test-model");
        var context = new TestSessionContext(session, [AgentFixtures.UserMsg(session.Id, 0)]);

        var result = await loop.RunAsync(context, TestAgents.AllowAll());

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(hooks.PromptCalls).IsEqualTo(1);
        await Assert.That(hooks.StartCalls).IsEqualTo(1);
        await Assert.That(hooks.EndCalls).IsEqualTo(1);
    }

    [Test]
    public async Task RunAsync_PromptHookThrows_FailsClosedToDeny()
    {
        var client = new ScriptedLlmClient(
            [[TestEvents.TextDelta(delta: "hi"), TestEvents.StepFinish(usage: new Usage(1, 1))]]);
        var bus = new FakeEventBus();
        var hooks = new FakeHookRunner
        {
            PromptImpl = (_, _, _) => throw new InvalidOperationException("boom"),
        };
        var loop = CreateLoop(client, bus, hooks);
        var session = Session.Create("/tmp/harbor-hook-gates-tests", "code", "test", "test-model");
        var context = new TestSessionContext(session, [AgentFixtures.UserMsg(session.Id, 0)]);

        var result = await loop.RunAsync(context, TestAgents.AllowAll());

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("Hook execution failed");
        await Assert.That(hooks.StartCalls).IsEqualTo(0);
        await Assert.That(context.Messages.Count).IsEqualTo(1);
    }
}
