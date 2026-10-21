using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
using Harbor.Application.Agents;
using Harbor.Application.Permissions;
using Harbor.Application.Resilience;
using Harbor.Application.Sessions;
using Harbor.Application.Tests.Fakes;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TestSessionContext = Harbor.TestKit.TestSessionContext;

namespace Harbor.Application.Tests;

/// <summary>
///     #1760: a tool call fails with a STRUCTURED error — a stable code next
///     to the message — and a tool may declare its own execution budget.
/// </summary>
/// <remarks>
///     <para>
///         Before this, a caller that wanted to tell a timeout from a
///         permission refusal from an unknown tool had to parse the message
///         text, and a tool call had exactly one deadline, the agent's. These
///         tests pin the replacement: the code is on the result, the model
///         reads it, and a tool's own budget wins over the agent's default.
///     </para>
/// </remarks>
public class ToolErrorCode1760Tests
{
    private static readonly JsonElement EmptyArgs = JsonDocument.Parse("{}").RootElement;

    private static TestSessionContext NewSession() =>
        new(Session.Create("/tmp/harbor-1760-tool-error-code", "code", "test", "test-model"));

    private static ToolCallPart Call(string id = "tc1", string tool = "tool") =>
        new(id, tool, EmptyArgs);

    private static ToolDispatcher NewDispatcher(ITool tool, out FakeEventBus bus)
    {
        bus = new FakeEventBus();
        var agents = new FakeAgentRegistry(TestAgents.AllowAll());
        return new ToolDispatcher(
            new FakeToolRegistry(tool),
            new PermissionService(agents, NullLogger<PermissionService>.Instance),
            bus,
            NullLogger<ToolDispatcher>.Instance,
            coordinator: null);
    }

    /// <summary>A tool that answers with whatever the test hands it.</summary>
    private sealed class ScriptedTool(Func<ToolResult> answer, int? timeoutMs = null) : ITool
    {
        public int? TimeoutMs => timeoutMs;

        public ToolName Name => ToolName.Create("tool");

        public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;

        public string DisplayName => "Tool";

        public string Description => "Answers with a scripted result.";

        public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""{"type":"object"}""");

        public ExecutionMode ExecutionMode => ExecutionMode.Parallel;

        public string? PromptSnippet => null;

        public IReadOnlyList<string> PromptGuidelines => [];

        public Result ValidateArguments(JsonElement args) => Result.Success();

        public Task<ToolResult> ExecuteAsync(
            JsonElement args,
            ToolContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(answer());
    }

    /// <summary>A tool that never returns unless it is cancelled.</summary>
    private sealed class HangingTool(int? timeoutMs = null) : ITool
    {
        public int? TimeoutMs => timeoutMs;

        public ToolName Name => ToolName.Create("hang");

        public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;

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
            // Observes the token, so the dispatcher's deadline can interrupt it.
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return ToolResult.Success("unreachable");
        }
    }

    /// <summary>A tool whose arguments never validate.</summary>
    private sealed class RejectingTool : ITool
    {
        public ToolName Name => ToolName.Create("tool");

        public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;

        public string DisplayName => "Tool";

        public string Description => "Rejects every argument.";

        public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""{"type":"object"}""");

        public ExecutionMode ExecutionMode => ExecutionMode.Parallel;

        public string? PromptSnippet => null;

        public IReadOnlyList<string> PromptGuidelines => [];

        public Result ValidateArguments(JsonElement args) => Result.Failure("Missing 'path' argument.");

        public Task<ToolResult> ExecuteAsync(
            JsonElement args,
            ToolContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Success("never reached"));
    }

    [Test]
    public async Task Success_CarriesNoError()
    {
        var tool = new ScriptedTool(() => ToolResult.Success("all good"));
        ToolDispatcher dispatcher = NewDispatcher(tool, out _);

        ToolResultMessage message = await dispatcher.ExecuteAsync(
            [Call()], NewSession(), AssistantMessage.Empty("s", "m"),
            TestAgents.AllowAll(), CancellationToken.None).ConfigureAwait(false);

        await Assert.That(message.Results.Count).IsEqualTo(1);
        await Assert.That(message.Results[0].IsError).IsFalse();
        await Assert.That(message.Results[0].ErrorInfo).IsNull();
        // A success is not rewritten: the tool's own text is the text.
        await Assert.That(message.Results[0].Output).IsEqualTo("all good");
    }

    [Test]
    public async Task ToolError_WithoutACode_GetsTheGenericCode()
    {
        // The tool reported a failure and did not say why. The dispatcher does
        // not guess WHY — it says THAT, and the model gets a code instead of a
        // prose puzzle.
        var tool = new ScriptedTool(() => ToolResult.Error("something went wrong"));
        ToolDispatcher dispatcher = NewDispatcher(tool, out _);

        ToolResultMessage message = await dispatcher.ExecuteAsync(
            [Call()], NewSession(), AssistantMessage.Empty("s", "m"),
            TestAgents.AllowAll(), CancellationToken.None).ConfigureAwait(false);

        await Assert.That(message.Results[0].IsError).IsTrue();
        await Assert.That(message.Results[0].ErrorInfo!.Code).IsEqualTo(ToolErrorCodes.Error);
        await Assert.That(message.Results[0].Output).IsEqualTo("TOOL_ERROR: something went wrong");
    }

    [Test]
    public async Task ToolError_WithItsOwnCode_KeepsIt()
    {
        // A tool that knows why it failed says so, and the code survives the
        // trip through the dispatcher untouched.
        var tool = new ScriptedTool(() => ToolResult.Failed(
            "no such file: x.txt",
            ToolErrorCodes.Create(ToolErrorCodes.NotFound, "x.txt")));
        ToolDispatcher dispatcher = NewDispatcher(tool, out _);

        ToolResultMessage message = await dispatcher.ExecuteAsync(
            [Call()], NewSession(), AssistantMessage.Empty("s", "m"),
            TestAgents.AllowAll(), CancellationToken.None).ConfigureAwait(false);

        await Assert.That(message.Results[0].ErrorInfo!.Code).IsEqualTo(ToolErrorCodes.NotFound);
        await Assert.That(message.Results[0].Output).IsEqualTo("TOOL_NOT_FOUND: no such file: x.txt");
    }

    [Test]
    public async Task UnknownCode_IsCarriedVerbatim()
    {
        // A plugin may return a code this build has never heard of. It is not
        // swallowed and not rewritten: the code is carried, the model reads
        // it, and "not known" only means this build has no policy for it.
        const string unknown = "PLUGIN_TOOL_QUOTA_EXCEEDED";
        var tool = new ScriptedTool(() => ToolResult.Failed(
            "the plugin's quota is spent",
            new ToolErrorInfo("PluginQuotaError", unknown, "quota")));
        ToolDispatcher dispatcher = NewDispatcher(tool, out _);

        ToolResultMessage message = await dispatcher.ExecuteAsync(
            [Call()], NewSession(), AssistantMessage.Empty("s", "m"),
            TestAgents.AllowAll(), CancellationToken.None).ConfigureAwait(false);

        ToolResultEntry entry = message.Results[0];
        ToolErrorInfo error = entry.ErrorInfo!;
        await Assert.That(error.Code).IsEqualTo(unknown);
        await Assert.That(error.Name).IsEqualTo("PluginQuotaError");
        await Assert.That(error.Reason).IsEqualTo("quota");
        await Assert.That(ToolErrorCodes.IsKnown(error.Code)).IsFalse();
        // The text still leads with the code — including one this build does
        // not know.
        await Assert.That(entry.Output).IsEqualTo("PLUGIN_TOOL_QUOTA_EXCEEDED: the plugin's quota is spent");
    }

    [Test]
    public async Task ValidationFailure_CarriesTheValidationCode()
    {
        var tool = new RejectingTool();
        ToolDispatcher dispatcher = NewDispatcher(tool, out _);

        ToolResultMessage message = await dispatcher.ExecuteAsync(
            [Call()], NewSession(), AssistantMessage.Empty("s", "m"),
            TestAgents.AllowAll(), CancellationToken.None).ConfigureAwait(false);

        await Assert.That(message.Results[0].ErrorInfo!.Code).IsEqualTo(ToolErrorCodes.ValidationFailed);
        await Assert.That(message.Results[0].Output)
            .IsEqualTo("TOOL_VALIDATION_FAILED: Missing 'path' argument.");
    }

    [Test]
    public async Task PermissionDenied_CarriesThePermissionCode()
    {
        // The policy refused the call. A decision, not a malfunction — and the
        // code says which, so a policy can tell it from a broken gate.
        var bus = new FakeEventBus();
        var agents = new FakeAgentRegistry(TestAgents.WithPermission(
            ruleset: new PermissionRuleset(new PermissionRule[] { new("*", "*", PermissionAction.Deny) })));
        var dispatcher = new ToolDispatcher(
            new FakeToolRegistry(new ScriptedTool(() => ToolResult.Success("never reached"))),
            new PermissionService(agents, NullLogger<PermissionService>.Instance),
            bus,
            NullLogger<ToolDispatcher>.Instance,
            coordinator: null);

        ToolResultMessage message = await dispatcher.ExecuteAsync(
            [Call()], NewSession(), AssistantMessage.Empty("s", "m"),
            TestAgents.AllowAll(), CancellationToken.None).ConfigureAwait(false);

        await Assert.That(message.Results[0].ErrorInfo!.Code).IsEqualTo(ToolErrorCodes.PermissionDenied);
        await Assert.That(message.Results[0].Output).IsEqualTo("TOOL_PERMISSION_DENIED: Permission denied");
    }

    [Test]
    public async Task Timeout_CarriesTheTimeoutCode()
    {
        // The deadline fired: the same structured code as any other timeout,
        // not a raw exception and not a prose puzzle.
        var tool = new HangingTool(timeoutMs: 60);
        ToolDispatcher dispatcher = NewDispatcher(tool, out _);

        ToolResultMessage message = await dispatcher.ExecuteAsync(
            [Call("tc1", "hang")], NewSession(), AssistantMessage.Empty("s", "m"),
            TestAgents.AllowAll(), CancellationToken.None).ConfigureAwait(false);

        ToolResultEntry entry = message.Results[0];
        await Assert.That(entry.IsError).IsTrue();
        await Assert.That(entry.ErrorInfo!.Code).IsEqualTo(ToolErrorCodes.Timeout);
        await Assert.That(entry.Output).StartsWith("TOOL_TIMEOUT: ");
        await Assert.That(entry.Output).Contains("timed out after");
    }

    [Test]
    public async Task PerToolBudget_AppliesWithoutAnAgentBudget()
    {
        // The new capability: a tool declares its own budget, and it holds
        // even when the agent has no budget at all. Before #1760 the only
        // deadline was the agent's, so a slow tool and a fast one shared one
        // number — or had none.
        var tool = new HangingTool(timeoutMs: 60);
        ToolDispatcher dispatcher = NewDispatcher(tool, out _);

        ToolResultMessage message = await dispatcher.ExecuteAsync(
            [Call("tc1", "hang")], NewSession(), AssistantMessage.Empty("s", "m"),
            TestAgents.AllowAll(), CancellationToken.None).ConfigureAwait(false);

        await Assert.That(message.Results[0].ErrorInfo!.Code).IsEqualTo(ToolErrorCodes.Timeout);
    }

    [Test]
    public async Task PerToolBudget_WinsOverTheAgentBudget()
    {
        // The tool's number is a fact about the tool; the agent's is a
        // default. A default that overrode the specific value would make the
        // specific value pointless — so the tool's 60ms fires long before the
        // agent's 10s.
        var tool = new HangingTool(timeoutMs: 60);
        ToolDispatcher dispatcher = NewDispatcher(tool, out _);
        AgentDefinition agent = TestAgents.AllowAll() with { ToolTimeoutSeconds = 10 };

        long started = Environment.TickCount64;
        ToolResultMessage message = await dispatcher.ExecuteAsync(
            [Call("tc1", "hang")], NewSession(), AssistantMessage.Empty("s", "m"),
            agent, CancellationToken.None, AgentBudget(agent)).ConfigureAwait(false);
        long elapsedMs = Environment.TickCount64 - started;

        await Assert.That(message.Results[0].ErrorInfo!.Code).IsEqualTo(ToolErrorCodes.Timeout);
        await Assert.That(elapsedMs).IsLessThan(5_000);
    }

    [Test]
    public async Task AgentBudget_AppliesWhenTheToolDeclaresNone()
    {
        // The common default still covers the tools that have no opinion of
        // their own.
        var tool = new HangingTool();
        ToolDispatcher dispatcher = NewDispatcher(tool, out _);
        AgentDefinition agent = TestAgents.AllowAll() with { ToolTimeoutSeconds = 1 };

        ToolResultMessage message = await dispatcher.ExecuteAsync(
            [Call("tc1", "hang")], NewSession(), AssistantMessage.Empty("s", "m"),
            agent, CancellationToken.None, AgentBudget(agent)).ConfigureAwait(false);

        await Assert.That(message.Results[0].ErrorInfo!.Code).IsEqualTo(ToolErrorCodes.Timeout);
    }

    /// <summary>
    ///     The agent's common default, the way the turn runner hands it to the
    ///     dispatcher. The dispatcher does not read the agent itself — the
    ///     budget reaches it as an argument, and a test that forgets to pass
    ///     it would hang rather than time out.
    /// </summary>
    private static TimeSpan? AgentBudget(AgentDefinition agent) =>
        agent.ToolTimeoutSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null;

    [Test]
    public async Task TheModelReadsTheCode_NotTheTrace()
    {
        // The point of the whole change: what the model gets back is a code it
        // can act on, not a paragraph it has to interpret.
        var tool = new HangingTool(timeoutMs: 60);
        var client = new ScriptedLlmClient(
        [
            new LlmEvent[]
            {
                TestEvents.ToolCallStart(toolName: "hang"),
                TestEvents.ToolCallDelta(),
                TestEvents.StepFinish(finishReason: "tool_use", usage: new Usage(4, 2)),
            },
            new LlmEvent[]
            {
                TestEvents.TextDelta(delta: "recovered"),
                TestEvents.StepFinish(1, usage: new Usage(1, 1)),
            },
        ]);
        var agents = new FakeAgentRegistry(TestAgents.AllowAll());
        var loop = new AgentLoop(
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

        var session = NewSession();
        var result = await loop.RunAsync(session, TestAgents.AllowAll()).ConfigureAwait(false);

        await Assert.That(result.IsSuccess).IsTrue();
        string secondRequest = TestMessages.RenderText(client.Requests[1]);
        await Assert.That(secondRequest).Contains("TOOL_TIMEOUT: ");
    }
}
