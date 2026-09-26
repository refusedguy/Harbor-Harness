using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
using Harbor.Application.Agents;
using Harbor.Application.Resilience;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TestSessionContext = Harbor.Application.Tests.Fakes.TestSessionContext;

namespace Harbor.Application.Tests;

/// <summary>
///     #43: dispatcher retries transport-class failures with a bounded backoff,
///     never retries fatal errors, never retries without a decider (legacy),
///     and never starts a second attempt into a cancelled token.
/// </summary>
public class ToolDispatcherRetryTests
{
    private sealed class AllowPermissions : IPermissionService
    {
        public Task<Result<PermissionResponse>> CheckAsync(
            string agentName, string toolName, JsonElement args, CancellationToken ct = default) =>
            Task.FromResult(Result.Success(new PermissionResponse(PermissionAction.Allow, false)));

        public Task<Result<PermissionResponse>> AskUserAsync(
            PermissionRequest request, CancellationToken ct = default) =>
            Task.FromResult(Result.Success(new PermissionResponse(PermissionAction.Deny, false)));

        public PermissionRuleset GetRuleset(string agentName) => PermissionRuleset.Empty;

        public Task<Result> SaveAsync(CancellationToken ct = default) =>
            Task.FromResult(Result.Success());
    }

    private sealed class FixedDecider(Func<Exception, bool> retryable) : IToolRetryDecider
    {
        public RetryOptions Options { get; } = new(MaxAttempts: 2, BaseDelay: TimeSpan.FromMilliseconds(500), UseJitter: false);

        public bool ShouldRetry(string toolName, Exception error, int failedAttempt) =>
            failedAttempt < Options.MaxAttempts && retryable(error);
    }

    /// <summary>#76: fast no-jitter decider so the projection test never sleeps.</summary>
    private sealed class FastDecider : IToolRetryDecider
    {
        public RetryOptions Options { get; } = new(MaxAttempts: 3, BaseDelay: TimeSpan.FromMilliseconds(1), UseJitter: false);

        public bool ShouldRetry(string toolName, Exception error, int failedAttempt) =>
            failedAttempt < Options.MaxAttempts && error is IOException;
    }

    private sealed class FlakyTool(Func<int, Exception?> script) : ITool
    {
        private int _executions;
        public int Executions => Volatile.Read(ref _executions);

        public ToolName Name => ToolName.Create("flaky");
        public string DisplayName => "Flaky";
        public string Description => "Fails per script, then succeeds.";
        public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
        public string? PromptSnippet => null;
        public IReadOnlyList<string> PromptGuidelines => [];
        public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""{"type":"object"}""");

        public Result ValidateArguments(JsonElement args) => Result.Success();

        public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken ct = default)
        {
            int n = Interlocked.Increment(ref _executions);
            return Task.FromResult(script(n) is { } ex
                ? throw ex
                : ToolResult.Success("recovered"));
        }
    }

    private static AgentDefinition CodeAgent() => new(
        AgentName.Create("code"),
        "Code",
        "retry harness",
        "test-model",
        "test",
        new PermissionRuleset(new PermissionRule[] { new("*", "*", PermissionAction.Allow) }));

    private static TestSessionContext NewSession() => new(
        Session.Create("/tmp/harbor-retry-tests", "code", "test", "test-model"));

    private static ToolCallPart Call() => new(
        "tc1", "flaky", JsonDocument.Parse("""{}""").RootElement);

    private static ToolDispatcher NewDispatcher(IPermissionService permissions, ITool tool, IToolRetryDecider? decider) =>
        NewDispatcher(permissions, tool, decider, new FakeEventBus());

    private static ToolDispatcher NewDispatcher(
        IPermissionService permissions, ITool tool, IToolRetryDecider? decider, FakeEventBus bus) =>
        new(new FakeToolRegistry(tool), permissions, bus,
            NullLogger<ToolDispatcher>.Instance, null, decider);

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
    public async Task TransientFailsOnce_ThenSucceeds()
    {
        var tool = new FlakyTool(n => n == 1 ? new IOException("reset") : null);
        var dispatcher = NewDispatcher(new AllowPermissions(), tool, new DefaultToolRetryDecider());

        var message = await dispatcher
            .ExecuteAsync([Call()], NewSession(), AssistantMessage.Empty("s", "m"), CodeAgent(), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);

        await Assert.That(tool.Executions).IsEqualTo(2);
        await Assert.That(message.Results[0].IsError).IsFalse();
        await Assert.That(message.Results[0].Output).IsEqualTo("recovered");
    }

    [Test]
    public async Task FatalError_NoRetry_MessageUnchanged()
    {
        var tool = new FlakyTool(_ => new InvalidOperationException("boom"));
        var dispatcher = NewDispatcher(new AllowPermissions(), tool, new DefaultToolRetryDecider());

        var message = await dispatcher
            .ExecuteAsync([Call()], NewSession(), AssistantMessage.Empty("s", "m"), CodeAgent(), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);

        await Assert.That(tool.Executions).IsEqualTo(1);
        await Assert.That(message.Results[0].IsError).IsTrue();
        await Assert.That(message.Results[0].Output).IsEqualTo("Tool execution failed: boom");
    }

    [Test]
    public async Task RetryExhausted_ReportsAttempts()
    {
        var tool = new FlakyTool(_ => new IOException("down"));
        var dispatcher = NewDispatcher(new AllowPermissions(), tool, new DefaultToolRetryDecider());

        var message = await dispatcher
            .ExecuteAsync([Call()], NewSession(), AssistantMessage.Empty("s", "m"), CodeAgent(), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);

        await Assert.That(tool.Executions).IsEqualTo(2);
        await Assert.That(message.Results[0].IsError).IsTrue();
        await Assert.That(message.Results[0].Output).Contains("after 2 attempts");
    }

    [Test]
    public async Task NullDecider_LegacyNoRetry()
    {
        var tool = new FlakyTool(n => n == 1 ? new IOException("reset") : null);
        var dispatcher = NewDispatcher(new AllowPermissions(), tool, null);

        var message = await dispatcher
            .ExecuteAsync([Call()], NewSession(), AssistantMessage.Empty("s", "m"), CodeAgent(), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);

        await Assert.That(tool.Executions).IsEqualTo(1);
        await Assert.That(message.Results[0].IsError).IsTrue();
    }

    [Test]
    public async Task CancelDuringBackoff_NoSecondStart()
    {
        var tool = new FlakyTool(_ => new IOException("reset"));
        var dispatcher = NewDispatcher(
            new AllowPermissions(), tool, new FixedDecider(ex => ex is IOException));
        using var cts = new CancellationTokenSource();

        var run = dispatcher.ExecuteAsync(
            [Call()], NewSession(), AssistantMessage.Empty("s", "m"), CodeAgent(), cts.Token);
        await WaitForAsync(() => tool.Executions == 1, "first attempt");
        cts.Cancel(); // lands inside the fixed 500ms backoff

        var message = await run.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await Assert.That(tool.Executions).IsEqualTo(1);
        await Assert.That(message.Results[0].IsError).IsTrue();
        await Assert.That(message.Results[0].Output).Contains("cancelled");
    }

    [Test]
    public async Task RetryScheduled_PublishesUpdateWithAttemptMaxAndBackoff()
    {
        // #76: the dispatcher feeds the retry-countdown UI slot — attempt/max
        // plus the backoff it is about to sleep — render-only, never a trigger.
        var bus = new FakeEventBus();
        var tool = new FlakyTool(n => n == 1 ? new IOException("reset") : null);
        var dispatcher = NewDispatcher(new AllowPermissions(), tool, new FastDecider(), bus);

        var message = await dispatcher
            .ExecuteAsync([Call()], NewSession(), AssistantMessage.Empty("s", "m"), CodeAgent(), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);

        await Assert.That(message.Results[0].IsError).IsFalse();
        var retry = bus.Events.OfType<ToolExecutionUpdateEvent>().Single(e => e.RetryAttempt.HasValue);
        await Assert.That(retry.ToolCallId).IsEqualTo("tc1");
        await Assert.That(retry.RetryAttempt).IsEqualTo(1);
        await Assert.That(retry.RetryMaxAttempts).IsEqualTo(3);
        await Assert.That(retry.RetryBackoffSeconds!.Value).IsGreaterThanOrEqualTo(0);
        await Assert.That(retry.RetryBackoffSeconds!.Value).IsLessThanOrEqualTo(1);
    }
}
