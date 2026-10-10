using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Application.Agents;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TestSessionContext = Harbor.Application.Tests.Fakes.TestSessionContext;

namespace Harbor.Application.Tests;

/// <summary>
///     #1109: <c>ToolDispatcher.CheckPermissionAsync</c> treated any verdict
///     <c>!= Deny</c> as allowed — a deferred <c>Ask</c> (asker returned
///     without an explicit approval) executed the tool (fail-open).
///     Fail-closed: only <c>Allow</c> proceeds; deferred <c>Ask</c> and
///     <c>Deny</c> both refuse without executing.
/// </summary>
public class ToolDispatcherAskFailClosedTests
{
    private sealed class FixedVerdictPermissions(PermissionAction action) : IPermissionService
    {
        public Task<Result<PermissionResponse>> CheckAsync(
            string agentName, string toolName, JsonElement args, CancellationToken ct = default,
            string? invocationId = null, int generation = 1) =>
            Task.FromResult(Result.Success(new PermissionResponse(action, false)));

        public Task<Result<PermissionResponse>> AskUserAsync(
            PermissionRequest request, CancellationToken ct = default) =>
            Task.FromResult(Result.Success(new PermissionResponse(PermissionAction.Deny, false)));

        public PermissionRuleset GetRuleset(string agentName) => PermissionRuleset.Empty;

        public Task<Result> SaveAsync(CancellationToken ct = default) =>
            Task.FromResult(Result.Success());
    }

    private static AgentDefinition CodeAgent() => new(
        AgentName.Create("code"),
        "Code",
        "ask fail-closed harness",
        "test-model",
        "test",
        new PermissionRuleset(new PermissionRule[] { new("*", "*", PermissionAction.Allow) }));

    private static TestSessionContext NewSession() => new(
        Session.Create("/tmp/harbor-ask-fail-closed-tests", "code", "test", "test-model"));

    private static ToolCallPart Call() => new(
        "tc1", "counter", JsonDocument.Parse("""{"n":1}""").RootElement);

    private static Task<ToolResultMessage> DispatchAsync(IPermissionService permissions, CountingTool tool) =>
        new ToolDispatcher(
                new FakeToolRegistry(tool),
                permissions,
                new FakeEventBus(),
                NullLogger<ToolDispatcher>.Instance)
            .ExecuteAsync([Call()], NewSession(), AssistantMessage.Empty("s", "m"), CodeAgent(), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

    [Test]
    public async Task ApprovedAsk_AllowsExecution()
    {
        var tool = new CountingTool();
        var message = await DispatchAsync(new FixedVerdictPermissions(PermissionAction.Allow), tool)
            .ConfigureAwait(false);

        await Assert.That(tool.Executions).IsEqualTo(1);
        await Assert.That(message.Results[0].IsError).IsFalse();
    }

    [Test]
    public async Task DeferredAsk_RefusesWithoutExecuting()
    {
        var tool = new CountingTool();
        var message = await DispatchAsync(new FixedVerdictPermissions(PermissionAction.Ask), tool)
            .ConfigureAwait(false);

        await Assert.That(tool.Executions).IsEqualTo(0);
        await Assert.That(message.Results[0].IsError).IsTrue();
    }

    [Test]
    public async Task Deny_RefusesWithoutExecuting()
    {
        var tool = new CountingTool();
        var message = await DispatchAsync(new FixedVerdictPermissions(PermissionAction.Deny), tool)
            .ConfigureAwait(false);

        await Assert.That(tool.Executions).IsEqualTo(0);
        await Assert.That(message.Results[0].IsError).IsTrue();
    }
}
