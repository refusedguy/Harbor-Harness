using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
using Harbor.Application.Agents;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TestSessionContext = Harbor.Application.Tests.Fakes.TestSessionContext;

namespace Harbor.Application.Tests;

/// <summary>
///     #201 B1 (§ROP-002 recurrence): a failing <c>AskUserAsync</c> railway must
///     fail closed to a Deny verdict inside the tool's Ask callback — never throw
///     <c>.Value</c> into the run.
/// </summary>
public class ToolDispatcherAskUserTests
{
    private sealed class AllowCheckFailAskPermissions : IPermissionService
    {
        public Task<Result<PermissionResponse>> CheckAsync(
            string agentName, string toolName, JsonElement args, CancellationToken ct = default,
            string? invocationId = null, int generation = 1) =>
            Task.FromResult(Result.Success(new PermissionResponse(PermissionAction.Allow, false)));

        public Task<Result<PermissionResponse>> AskUserAsync(
            PermissionRequest request, CancellationToken ct = default) =>
            Task.FromResult(Result.Failure<PermissionResponse>("ask subsystem down"));

        public PermissionRuleset GetRuleset(string agentName) => PermissionRuleset.Empty;

        public Task<Result> SaveAsync(CancellationToken ct = default) =>
            Task.FromResult(Result.Success());
    }

    private sealed class AskEchoTool : ITool
    {
        public string? ObservedAction;

        public ToolName Name => ToolName.Create("askecho");
        public string DisplayName => "AskEcho";
        public string Description => "Echoes the Ask verdict back as output.";
        public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
        public string? PromptSnippet => null;
        public IReadOnlyList<string> PromptGuidelines => [];
        public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""{"type":"object"}""");

        public Result ValidateArguments(JsonElement args) => Result.Success();

        public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken ct = default)
        {
            PermissionResponse response = await context.Ask(
                new PermissionRequest("read", "*", args, Array.Empty<string>()), ct).ConfigureAwait(false);
            ObservedAction = response.Action.ToString();
            return ToolResult.Success($"ask-verdict:{response.Action}");
        }
    }

    private static AgentDefinition CodeAgent() => new(
        AgentName.Create("code"),
        "Code",
        "ask-failure harness",
        "test-model",
        "test",
        new PermissionRuleset(new PermissionRule[] { new("*", "*", PermissionAction.Allow) }));

    private static TestSessionContext NewSession() => new(
        Session.Create("/tmp/harbor-ask-tests", "code", "test", "test-model"));

    [Test]
    public async Task AskUserFailure_ToolSeesDenyInsteadOfThrow()
    {
        var tool = new AskEchoTool();
        var dispatcher = new ToolDispatcher(
            new FakeToolRegistry(tool),
            new AllowCheckFailAskPermissions(),
            new FakeEventBus(),
            NullLogger<ToolDispatcher>.Instance);
        using var args = JsonDocument.Parse("""{}""");

        var message = await dispatcher
            .ExecuteAsync(
                [new ToolCallPart("tc1", "askecho", args.RootElement)],
                NewSession(),
                AssistantMessage.Empty("s", "m"),
                CodeAgent(),
                CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);

        await Assert.That(message.Results[0].IsError).IsFalse();
        await Assert.That(message.Results[0].Output).Contains("ask-verdict:Deny");
        await Assert.That(tool.ObservedAction).IsEqualTo("Deny");
    }
}
