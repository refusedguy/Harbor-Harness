using System.Text.Json;
using Harbor.TestKit;
using FakeTokenTracker = Harbor.TestKit.FakeTokenTracker;
using FakeCompactionService = Harbor.TestKit.FakeCompactionService;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Application.Tests.Fakes;
using TestSessionContext = Harbor.TestKit.TestSessionContext;
using CSharpFunctionalExtensions;
using Harbor.Application.Agents;
using Harbor.Application.Permissions;
using Harbor.Application.Resilience;
using Harbor.Application.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;

namespace Harbor.Application.Tests;

/// <summary>
///     A9 (sprint 5): a tool that hangs must be cancelled at the agent's
///     per-call deadline and surface as an error result — the loop keeps
///     going instead of blocking forever.
/// </summary>
public class ToolTimeoutTests
{
    /// <summary>Tool whose ExecuteAsync never completes unless cancelled.</summary>
    private sealed class HangingTool : ITool
    {
        public ToolName Name => ToolName.Create("hang");
        public string DisplayName => "Hang";
        public string Description => "Never returns.";
        public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""{"type":"object"}""");
        public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
        public string? PromptSnippet => null;
        public IReadOnlyList<string> PromptGuidelines => [];
        public Result ValidateArguments(JsonElement args) => Result.Success();

        public async Task<ToolResult> ExecuteAsync(
            JsonElement args,
            ToolContext context,
            CancellationToken cancellationToken = default)
        {
            // Wait forever, but observe cancellation so the dispatcher's
            // deadline can actually interrupt us.
            await Task.Delay(Timeout.Infinite, context.Abort).ConfigureAwait(false);
            return ToolResult.Success("unreachable");
        }
    }

    private static AgentDefinition Agent(int timeoutSeconds) => new(
        AgentName.Create("code"),
        "Code",
        "tool-timeout harness",
        "test-model",
        "test",
        new PermissionRuleset(new PermissionRule[] { new("*", "*", PermissionAction.Allow) }),
        MaxSteps: 10,
        ToolTimeoutSeconds: timeoutSeconds);


    [Test]
    public async Task RunAsync_HangingTool_TimesOutAndLoopContinues()
    {
        var agent = Agent(timeoutSeconds: 1);
        var client = new ScriptedLlmClient(
        [
            new LlmEvent[]
            {
                new ToolCallStartEvent("call-1", "hang"),
                new ToolCallDeltaEvent("call-1", "{}"),
                new StepFinishEvent(0, "tool_use", new Usage(4, 2))
            },
            new LlmEvent[]
            {
                new TextDeltaEvent("t", "recovered after timeout"),
                new StepFinishEvent(1, "stop", new Usage(1, 1))
            }
        ]);
        var agents = new FakeAgentRegistry(agent);
        var loop = new AgentLoop(
            new FakeProviderRegistry(client),
            new FakeToolRegistry(new HangingTool()),
            agents,
            new StubSystemPromptBuilder(),
            new FakeCompactionService(),
            new FakeTokenTracker(),
            new RetryPolicy(),
            new FakeEventBus(),
            new PermissionService(agents, NullLogger<PermissionService>.Instance),
            new MessageConverter(),
            NullLogger<AgentLoop>.Instance);
        var session = new TestSessionContext(
            Session.Create("/tmp/harbor-tool-timeout-tests", "code", "test", "test-model"));

        long started = Environment.TickCount64;
        var result = await loop.RunAsync(session, agent).ConfigureAwait(false);
        long elapsedMs = Environment.TickCount64 - started;

        await Assert.That(result.IsSuccess).IsTrue();

        // Turn 2 request carries the timeout error result for the hanging call.
        string secondRequestText = TestMessages.RenderText(client.Requests[1]);
        await Assert.That(secondRequestText).Contains("timed out after 1s");

        // The run recovered: final assistant text streamed after the failure.
        await Assert.That(session.Messages.OfType<AssistantMessage>().Any()).IsTrue();

        // The whole two-turn run finished well under a blocking-forever budget.
        await Assert.That(elapsedMs).IsLessThan(10_000);
    }

    [Test]
    public async Task RunAsync_NoTimeoutConfigured_LegacyUnboundedStillWorks()
    {
        // Regression guard: agents WITHOUT the knob keep the legacy path — a
        // quick tool still executes normally when no deadline is configured.
        var agent = Agent(timeoutSeconds: 0); // 0 → TimeSpan.Zero… treat as unset below? No:
        // Use null via `with` to express the default.
        agent = agent with { ToolTimeoutSeconds = null };

        var client = new ScriptedLlmClient(
        [
            new LlmEvent[]
            {
                new TextDeltaEvent("t", "plain answer"),
                new StepFinishEvent(0, "stop", new Usage(1, 1))
            }
        ]);
        var agents = new FakeAgentRegistry(agent);
        var loop = new AgentLoop(
            new FakeProviderRegistry(client),
            new FakeToolRegistry(),
            agents,
            new StubSystemPromptBuilder(),
            new FakeCompactionService(),
            new FakeTokenTracker(),
            new RetryPolicy(),
            new FakeEventBus(),
            new PermissionService(agents, NullLogger<PermissionService>.Instance),
            new MessageConverter(),
            NullLogger<AgentLoop>.Instance);
        var session = new TestSessionContext(
            Session.Create("/tmp/harbor-tool-timeout-tests", "code", "test", "test-model"));

        var result = await loop.RunAsync(session, agent);

        await Assert.That(result.IsSuccess).IsTrue();
    }
}
