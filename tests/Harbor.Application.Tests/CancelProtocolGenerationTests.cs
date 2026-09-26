using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Agents;
using Harbor.Application.Tests.Fakes;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;

namespace Harbor.Application.Tests;

/// <summary>
///     #91 (cancel/reset protocol): <see cref="DefaultAgent.ResetAbortSource" />
///     must refuse the source swap while a run bound to the current (or an
///     older) abort generation is still alive. A session switch whose bounded
///     <c>WaitForIdleAsync</c> times out used to swap the token out from under
///     the zombie run — the next prompt then raced it, and a later cancel hit
///     the fresh source while the old run still waited on the disposed one.
/// </summary>
public class CancelProtocolGenerationTests
{
    /// <summary>Loop that parks until the test releases the gate (ignores ct — stays "alive" even when cancelled).</summary>
    private sealed class BlockingLoop(TaskCompletionSource<Result> gate) : IAgentLoop
    {
        public Task<Result> RunAsync(ISessionContext session, AgentDefinition agent, CancellationToken ct = default)
            => gate.Task;
    }

    private static (DefaultAgent Agent, TaskCompletionSource<Result> Gate) CreateAgent()
    {
        var session = Session.Create("/tmp/harbor-cancel-protocol-tests", "code", "test", "test-model");
        var gate = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new DefaultAgent(
            new Harbor.Application.Tests.Fakes.FakeSessionStore(session),
            new BlockingLoop(gate),
            new FakeEventBus(),
            NullLogger<DefaultAgent>.Instance);
        agent.Initialize(session, new AgentDefinition(
            AgentName.Create("code"),
            "Code",
            "cancel protocol harness",
            "test-model",
            "test",
            new PermissionRuleset(new PermissionRule[] { new("*", "*", PermissionAction.Allow) })));
        return (agent, gate);
    }

    private static async Task WaitUntilRunningAsync(DefaultAgent agent)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (agent.State?.IsRunning != true && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        await Assert.That(agent.State?.IsRunning ?? false).IsTrue();
    }

    [Test]
    public async Task ResetAbortSource_WhileRunAlive_RefusesSwap_ThenHealsOnceIdle()
    {
        (DefaultAgent agent, TaskCompletionSource<Result> gate) = CreateAgent();
        try
        {
            var runTask = agent.PromptAsync("long run");
            await WaitUntilRunningAsync(agent);

            // Abort ingress, then the timed-out WaitForIdle path calls reset
            // while the run is still alive: the swap must be refused, so the
            // token stays cancelled.
            agent.RequestAbort();
            agent.ResetAbortSource();

            await Assert.That(agent.AbortToken.IsCancellationRequested).IsTrue();

            // The run drains; once idle the deferred reset must go through and
            // the agent must be usable again.
            gate.TrySetResult(Result.Success());
            var result = await runTask.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(result.IsSuccess).IsTrue();

            agent.ResetAbortSource();
            await Assert.That(agent.AbortToken.IsCancellationRequested).IsFalse();

            var next = await agent.PromptAsync("after reset");
            await Assert.That(next.IsSuccess).IsTrue();
        }
        finally
        {
            agent.Dispose();
        }
    }

    [Test]
    public async Task ResetAbortSource_IdleAfterAbort_SwapsImmediately()
    {
        (DefaultAgent agent, TaskCompletionSource<Result> gate) = CreateAgent();
        try
        {
            gate.TrySetResult(Result.Success());
            var first = await agent.PromptAsync("prime");
            await Assert.That(first.IsSuccess).IsTrue();

            agent.RequestAbort();
            agent.ResetAbortSource();

            await Assert.That(agent.AbortToken.IsCancellationRequested).IsFalse();
        }
        finally
        {
            agent.Dispose();
        }
    }
}
