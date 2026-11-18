// DisposeDuringLiveRunTests.cs — issue #2585.
//
// DefaultAgent.Dispose used to set the disposed flag and then unconditionally
// dispose the abort CTS, the run gate and the steering channel. Called while a
// PromptAsync was in flight (host shutdown, DI scope teardown, a listener that
// disposes its own agent), the run's finally then released a disposed gate and
// threw ObjectDisposedException, while WaitForIdleAsync waiters could be left
// parked. These tests pin the two ways out: abort and drain when the run is
// cooperative, defer teardown when it is not.

using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Agents;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;

namespace Harbor.Application.Tests;

public sealed class DisposeDuringLiveRunTests
{
    private static Session NewSession() =>
        Session.Create("/tmp/harbor-dispose-live-run-tests", "code", "test", "test-model");

    private static DefaultAgent NewAgent(Session session, IAgentLoop loop) =>
        new(new FakeSessionStore(session), loop, new FakeEventBus(), NullLogger<DefaultAgent>.Instance);

    /// <summary>Loop that parks until the abort token is cancelled, then reports the cancel.</summary>
    private sealed class AbortAwareLoop : IAgentLoop
    {
        public async Task<Result> RunAsync(ISessionContext session, AgentDefinition agent, CancellationToken ct = default)
        {
            var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using CancellationTokenRegistration registration = ct.Register(() => released.TrySetResult());
            await released.Task.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return Result.Success();
        }
    }

    /// <summary>Loop that ignores the abort token entirely and only leaves on its own gate.</summary>
    private sealed class StubbornLoop(TaskCompletionSource<Result> gate) : IAgentLoop
    {
        public Task<Result> RunAsync(ISessionContext session, AgentDefinition agent, CancellationToken ct = default)
            => gate.Task;
    }

    /// <summary>Loop that disposes the agent from inside its own run flow.</summary>
    private sealed class SelfDisposingLoop : IAgentLoop
    {
        public DefaultAgent? Agent { get; set; }

        public Exception? DisposeError { get; private set; }

        public Task<Result> RunAsync(ISessionContext session, AgentDefinition agent, CancellationToken ct = default)
        {
            try
            {
                Agent!.Dispose();
            }
            catch (Exception ex)
            {
                DisposeError = ex;
            }

            return Task.FromResult(Result.Success());
        }
    }

    [Test]
    public async Task Dispose_WhileCooperativeRunIsActive_AbortsAndUnblocksIdleWaiters()
    {
        Session session = NewSession();
        DefaultAgent agent = NewAgent(session, new AbortAwareLoop());
        agent.Initialize(session, TestAgents.AllowAll());

        Task<Result> prompt = agent.PromptAsync("work");
        await Assert.That(await AsyncWaits.WaitUntilAsync(() => agent.IsRunning())).IsTrue()
            .Because("Dispose must be exercised while the run holds the gate, not before it starts.");

        Task idle = agent.WaitForIdleAsync();

        // The bounded wait lives on another thread so the test itself is not
        // serialized behind the drain timeout.
        await Task.Run(agent.Dispose).WaitAsync(TimeSpan.FromSeconds(10));

        Result result = await prompt.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(result.IsFailure).IsTrue()
            .Because("Dispose aborts the run at its next safe boundary.");
        await Assert.That(result.Error).DoesNotContain("ObjectDisposedException")
            .Because("No synchronization primitive may be torn down under the live run (#2585).");

        await idle.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(agent.IsRunning()).IsFalse();
    }

    [Test]
    public async Task Dispose_WhileUncooperativeRunIsActive_DefersGateTeardown()
    {
        Session session = NewSession();
        var gate = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        using DefaultAgent agent = NewAgent(session, new StubbornLoop(gate));
        agent.Initialize(session, TestAgents.AllowAll());

        Task<Result> prompt = agent.PromptAsync("work");
        await Assert.That(await AsyncWaits.WaitUntilAsync(() => agent.IsRunning())).IsTrue();

        // This run never observes the abort. Dispose must time out its drain,
        // leave the gate intact and arrange cleanup for when the run leaves.
        agent.Dispose();

        gate.TrySetResult(Result.Success());
        Result result = await prompt.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(result.IsSuccessful).IsTrue()
            .Because("Releasing a still-live gate after Dispose must not throw ObjectDisposedException (#2585).");
        await Assert.That(agent.IsRunning()).IsFalse();
    }

    [Test]
    public async Task Dispose_CalledFromInsideTheRun_DefersWithoutWaitingOnItself()
    {
        Session session = NewSession();
        var loop = new SelfDisposingLoop();
        using DefaultAgent agent = NewAgent(session, loop);
        loop.Agent = agent;
        agent.Initialize(session, TestAgents.AllowAll());

        Result result = await agent.PromptAsync("work").WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(loop.DisposeError).IsNull()
            .Because("A listener or loop disposing its own agent must not take an ObjectDisposedException (#2585).");
        await Assert.That(result.IsSuccessful).IsTrue()
            .Because("The run must be able to finish after a reentrant Dispose instead of deadlocking on its exit signal.");
    }
}
