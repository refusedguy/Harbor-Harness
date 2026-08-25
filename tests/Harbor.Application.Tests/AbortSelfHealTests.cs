using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Tests.Fakes;
using Harbor.Application.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;

namespace Harbor.Application.Tests;

/// <summary>
///     G1 (deep2-core): aborting an agent cancels its CancellationTokenSource
///     permanently; callers who only call <c>AbortSource.Cancel()</c> (IPC server,
///     InProcess client) never reset it, so every subsequent prompt died with
///     "Agent was cancelled." until process restart. The agent must self-heal at
///     gate acquisition instead of relying on external temporal coupling.
/// </summary>
public class AbortSelfHealTests
{
    /// <summary>Mirrors AgentLoop's contract: refuses to run on an already-cancelled token.</summary>
    private sealed class TokenObservingLoop : IAgentLoop
    {
        private int _runs;

        public int Runs => Volatile.Read(ref _runs);

        public Task<Result> RunAsync(ISessionContext session, AgentDefinition agent, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _runs);
            return Task.FromResult(ct.IsCancellationRequested
                ? Result.Failure("loop observed pre-cancelled token")
                : Result.Success());
        }
    }

    private static (DefaultAgent Agent, TokenObservingLoop Loop) CreateAgent()
    {
        var session = Session.Create("/tmp/harbor-abort-selfheal-tests", "code", "test", "test-model");
        var loop = new TokenObservingLoop();
        var agent = new DefaultAgent(
            new FakeSessionStore(session),
            loop,
            new FakeEventBus(),
            NullLogger<DefaultAgent>.Instance);
        agent.Initialize(session, new AgentDefinition(
            AgentName.Create("code"),
            "Code",
            "abort self-heal harness",
            "test-model",
            "test",
            new PermissionRuleset(new PermissionRule[] { new("*", "*", PermissionAction.Allow) })));
        return (agent, loop);
    }

    [Test]
    public async Task PromptAsync_AfterExternalCancelWithoutReset_RunSucceeds()
    {
        (DefaultAgent agent, TokenObservingLoop loop) = CreateAgent();
        try
        {
            var first = await agent.PromptAsync("first");
            await Assert.That(first.IsSuccess).IsTrue();

            // Exactly what RequestDispatcher.HandleAbortAgent / InProcessHarborClient
            // .AbortAgentAsync do — no ResetAbortSource anywhere nearby.
            agent.AbortSource.Cancel();

            var second = await agent.PromptAsync("second");

            await Assert.That(second.IsSuccess).IsTrue();
            await Assert.That(loop.Runs).IsEqualTo(2);
            await Assert.That(agent.AbortSource.IsCancellationRequested).IsFalse();
        }
        finally
        {
            agent.Dispose();
        }
    }

    [Test]
    public async Task ResetAbortSource_ConcurrentResetsAfterAbort_SwapStaysConsistent()
    {
        (DefaultAgent agent, _) = CreateAgent();
        try
        {
            var run = await agent.PromptAsync("prime");
            await Assert.That(run.IsSuccess).IsTrue();
            agent.AbortSource.Cancel();

            // F2: N racing resets must not double-dispose or strand sources.
            await Task.WhenAll(Enumerable.Range(0, 8)
                .Select(_ => Task.Run(() => agent.ResetAbortSource())));

            var next = await agent.PromptAsync("after-races");
            await Assert.That(next.IsSuccess).IsTrue();
            await Assert.That(agent.AbortSource.IsCancellationRequested).IsFalse();
        }
        finally
        {
            agent.Dispose();
        }
    }
}
