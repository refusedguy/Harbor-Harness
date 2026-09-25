using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
using Harbor.Application.Agents;
using Harbor.Application.Permissions;
using TestSessionContext = Harbor.Application.Tests.Fakes.TestSessionContext;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;

namespace Harbor.Application.Tests;

/// <summary>
///     #49 PR2: execution-commit barrier. Cancel winning after approval but
///     before the first tool instruction must prevent the START (a
///     token-ignoring tool would otherwise run despite the abort); after
///     commit only best-effort token observation remains. The spy tool counts
///     starts — barrier violations are observable as executions.
/// </summary>
public class ToolDispatcherCommitTests
{
    private sealed class FakeRunner : IAgentRunner
    {
        public CancellationTokenSource AbortSource { get; } = new();
        public Task<Result> PromptAsync(string text, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());
        public Task WaitForIdleAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void ResetAbortSource() { }
    }

    private sealed class GatedPermissions(
        TaskCompletionSource<bool>? gate,
        PermissionAction action = PermissionAction.Allow) : IPermissionService
    {
        public int Checks;

        public async Task<Result<PermissionResponse>> CheckAsync(
            string agentName, string toolName, JsonElement args, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Checks);
            if (gate is not null)
            {
                await gate.Task.ConfigureAwait(false);
            }

            return Result.Success(new PermissionResponse(action, false));
        }

        public Task<Result<PermissionResponse>> AskUserAsync(
            PermissionRequest request, CancellationToken ct = default) =>
            Task.FromResult(Result.Success(new PermissionResponse(PermissionAction.Deny, false)));

        public PermissionRuleset GetRuleset(string agentName) => PermissionRuleset.Empty;

        public Task<Result> SaveAsync(CancellationToken ct = default) =>
            Task.FromResult(Result.Success());
    }

    private sealed class SpyTool : ITool
    {
        private int _executions;
        public int Executions => Volatile.Read(ref _executions);
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ToolName Name => ToolName.Create("counter");
        public string DisplayName => "Counter";
        public string Description => "Counts starts for the commit barrier.";
        public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
        public string? PromptSnippet => null;
        public IReadOnlyList<string> PromptGuidelines => [];
        public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""{"type":"object"}""");

        public Result ValidateArguments(JsonElement args) => Result.Success();

        public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _executions);
            Entered.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            return ToolResult.Success("unreachable");
        }
    }

    private static AgentDefinition CodeAgent() => new(
        AgentName.Create("code"),
        "Code",
        "commit-barrier harness",
        "test-model",
        "test",
        new PermissionRuleset(new PermissionRule[] { new("*", "*", PermissionAction.Allow) }));

    private static TestSessionContext NewSession() => new(
        Session.Create("/tmp/harbor-commit-tests", "code", "test", "test-model"));

    private static ToolCallPart Call() => new(
        "tc1", "counter", JsonDocument.Parse("""{"n":1}""").RootElement);

    private static ToolDispatcher NewDispatcher(IPermissionService permissions, ITool tool, IApprovalCoordinator coordinator) =>
        new(new FakeToolRegistry(tool), permissions, new FakeEventBus(),
            NullLogger<ToolDispatcher>.Instance, coordinator);

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(5).ConfigureAwait(false);
        }

        if (!condition())
        {
            throw new TimeoutException($"Timed out waiting for: {what}");
        }
    }

    [Test]
    public async Task CancelBeforeCommit_ToolNeverStarts()
    {
        var coordinator = new ApprovalCoordinator(NullLogger<ApprovalCoordinator>.Instance);
        var runner = new FakeRunner();
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var permissions = new GatedPermissions(gate);
        var tool = new SpyTool();
        var dispatcher = NewDispatcher(permissions, tool, coordinator);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(runner.AbortSource.Token);

        var run = dispatcher.ExecuteAsync([Call()], NewSession(), AssistantMessage.Empty("s", "m"), CodeAgent(), cts.Token);
        await WaitForAsync(() => permissions.Checks == 1, "permission check entered");

        // Cancel wins after approval started but before commit.
        coordinator.RequestCancel(runner);
        gate.TrySetResult(true);

        var message = await run.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await Assert.That(tool.Executions).IsEqualTo(0);
        await Assert.That(message.Results.Count).IsEqualTo(1);
        await Assert.That(message.Results[0].IsError).IsTrue();
        await Assert.That(message.Results[0].Output).Contains("cancelled before start");
    }

    [Test]
    public async Task NoCancel_ExecutesOnceCleanly()
    {
        var coordinator = new ApprovalCoordinator(NullLogger<ApprovalCoordinator>.Instance);
        var permissions = new GatedPermissions(null);
        var tool = new CountingTool();
        var dispatcher = NewDispatcher(permissions, tool, coordinator);

        var message = await dispatcher
            .ExecuteAsync([Call()], NewSession(), AssistantMessage.Empty("s", "m"), CodeAgent(), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);

        await Assert.That(tool.Executions).IsEqualTo(1);
        await Assert.That(message.Results[0].IsError).IsFalse();
    }

    [Test]
    public async Task CancelAfterCommit_OneExecutionCancelledToken()
    {
        var coordinator = new ApprovalCoordinator(NullLogger<ApprovalCoordinator>.Instance);
        var runner = new FakeRunner();
        var permissions = new GatedPermissions(null);
        var tool = new SpyTool();
        var dispatcher = NewDispatcher(permissions, tool, coordinator);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(runner.AbortSource.Token);

        var run = dispatcher.ExecuteAsync([Call()], NewSession(), AssistantMessage.Empty("s", "m"), CodeAgent(), cts.Token);
        await tool.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        // Commit already happened (tool started) — cancel is best-effort now.
        coordinator.RequestCancel(runner);

        var message = await run.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await Assert.That(tool.Executions).IsEqualTo(1);
        await Assert.That(message.Results[0].IsError).IsTrue();
        await Assert.That(message.Results[0].Output).Contains("cancelled");
    }

    [Test]
    public async Task DenyWhileCancelled_HonestCancelledEntry()
    {
        var coordinator = new ApprovalCoordinator(NullLogger<ApprovalCoordinator>.Instance);
        var permissions = new GatedPermissions(null, PermissionAction.Deny);
        var tool = new CountingTool();
        var dispatcher = NewDispatcher(permissions, tool, coordinator);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var message = await dispatcher
            .ExecuteAsync([Call()], NewSession(), AssistantMessage.Empty("s", "m"), CodeAgent(), cts.Token)
            .WaitAsync(TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);

        await Assert.That(tool.Executions).IsEqualTo(0);
        await Assert.That(message.Results[0].IsError).IsTrue();
        await Assert.That(message.Results[0].Output).Contains("cancelled before start");
    }
}
