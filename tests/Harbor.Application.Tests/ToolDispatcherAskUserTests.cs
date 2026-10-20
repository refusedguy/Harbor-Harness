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
using FakePermissionServiceBase = Harbor.TestKit.Generated.FakePermissionService; // #1769: generated double

using TestSessionContext = Harbor.Application.Tests.Fakes.TestSessionContext;

using FakeToolBase = Harbor.TestKit.Generated.FakeTool; // #1769: generated double


namespace Harbor.Application.Tests;

/// <summary>
///     #201 B1 (§ROP-002 recurrence): a failing <c>AskUserAsync</c> railway must
///     fail closed to a Deny verdict inside the tool's Ask callback — never throw
///     <c>.Value</c> into the run.
/// </summary>
public class ToolDispatcherAskUserTests
{
    private sealed class AllowCheckFailAskPermissions : FakePermissionServiceBase
    {
        public AllowCheckFailAskPermissions()
        {
            CheckAsyncResult = Result.Success(new PermissionResponse(PermissionAction.Allow, false));
            AskUserAsyncResult = Result.Failure<PermissionResponse>("ask subsystem down");
            GetRulesetValue = PermissionRuleset.Empty;
            SaveAsyncResult = Result.Success();
        }
    }

    private sealed class AskEchoTool : FakeToolBase
    {
        public string? ObservedAction;

        public AskEchoTool()
        {
            Name = ToolName.Create("askecho");
            DisplayName = "AskEcho";
            Description = "Echoes the Ask verdict back as output.";
        }

        public override async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken ct = default)
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
