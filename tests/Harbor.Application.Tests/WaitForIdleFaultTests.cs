// WaitForIdleFaultTests.cs — issue #1105, one row of the SubAgentRunner cluster.
//
// Row: DefaultAgent fault path completed the run-completion source with
// TrySetException while success/cancel use TrySetResult, so WaitForIdleAsync
// waiters got the exception thrown at them instead of a quiet completion.
// The waiter holds a non-generic Task and cannot observe a Result value —
// the failure itself travels via PromptAsync's own return — so all three
// terminal paths must complete the source the same way.

using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Agents;
using Harbor.Application.Tests.Fakes;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using FakeEventBus = Harbor.TestKit.FakeEventBus;
using FakeSessionStore = Harbor.TestKit.FakeSessionStore;

namespace Harbor.Application.Tests;

/// <summary>
///     #1105: a faulted run must not fault the <see cref="DefaultAgent.WaitForIdleAsync" />
///     waiters — they observe idle, not the run's outcome.
/// </summary>
public sealed class WaitForIdleFaultTests
{
    /// <summary>
    ///     Loop that parks on the gate, then throws: lets the test attach a
    ///     <c>WaitForIdleAsync</c> waiter to the in-flight run before the fault.
    /// </summary>
    private sealed class FaultAfterReleaseAgentLoop(Task gate, Exception fault) : IAgentLoop
    {
        public async Task<Result> RunAsync(ISessionContext session, AgentDefinition agent, CancellationToken ct = default)
        {
            await gate.ConfigureAwait(false);
            throw fault;
        }
    }

    [Test]
    public async Task WaitForIdleAsync_FaultedRun_CompletesWithoutThrowing()
    {
        var session = Session.Create("/tmp/harbor-waitforidle-fault-tests", "code", "test", "test-model");
        var store = new FakeSessionStore(session);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loop = new FaultAfterReleaseAgentLoop(
            release.Task, new InvalidOperationException("loop blew up"));
        using var subject = new DefaultAgent(store, loop, new FakeEventBus(), NullLogger<DefaultAgent>.Instance);
        subject.Initialize(session, TestAgents.AllowAll());

        Task<Result> promptTask = subject.PromptAsync("do work");
        await Assert.That(await AsyncWaits.WaitUntilAsync(() => subject.IsRunning())).IsTrue()
            .Because("WaitForIdleAsync only observes the in-flight run, so the waiter must attach while it is running.");

        Task waitTask = subject.WaitForIdleAsync();
        release.TrySetResult();

        Result promptResult = await promptTask;
        await Assert.That(promptResult).IsFailed()
            .Because("The run itself failed — PromptAsync still reports the failure to its own caller.");
        await Assert.That(subject.IsRunning()).IsFalse()
            .Because("The fault path must settle the running flag exactly like success and cancel.");

        Exception? waiterError = null;
        try
        {
            await waitTask;
        }
        catch (Exception ex)
        {
            waiterError = ex;
        }

        await Assert.That(waiterError).IsNull()
            .Because(
                "#1105: success and cancel complete the run-completion source with TrySetResult, "
                + "so the fault path must too — a waiter holds a non-generic Task and cannot observe "
                + "a Result value, it must simply see idle.");
    }

    // #2585: Dispose must not tear down the gate or the abort CTS while a run
    // still holds them. The three cases below cover a cooperative run, a run
    // that ignores the abort, and a Dispose issued from inside the run itself.

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

    private static Session NewDisposeSession() =>
        Session.Create("/tmp/harbor-dispose-live-run-tests", "code", "test", "test-model");

    private static DefaultAgent NewDisposeAgent(Session session, IAgentLoop loop) =>
        new(new FakeSessionStore(session), loop, new FakeEventBus(), NullLogger<DefaultAgent>.Instance);

    [Test]
    public async Task Dispose_WhileCooperativeRunIsActive_AbortsAndUnblocksIdleWaiters()
    {
        Session session = NewDisposeSession();
        DefaultAgent agent = NewDisposeAgent(session, new AbortAwareLoop());
        agent.Initialize(session, TestAgents.AllowAll());

        Task<Result> prompt = agent.PromptAsync("work");
        await Assert.That(await AsyncWaits.WaitUntilAsync(() => agent.IsRunning())).IsTrue()
            .Because("Dispose must be exercised while the run holds the gate, not before it starts.");

        Task idle = agent.WaitForIdleAsync();

        // The bounded wait lives on another thread so the test is not
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
        Session session = NewDisposeSession();
        var gate = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        using DefaultAgent agent = NewDisposeAgent(session, new StubbornLoop(gate));
        agent.Initialize(session, TestAgents.AllowAll());

        Task<Result> prompt = agent.PromptAsync("work");
        await Assert.That(await AsyncWaits.WaitUntilAsync(() => agent.IsRunning())).IsTrue();

        // This run never observes the abort. Dispose must time out its drain,
        // leave the gate intact and arrange cleanup for when the run leaves.
        agent.Dispose();

        gate.TrySetResult(Result.Success());
        Result result = await prompt.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(result).IsSuccessful()
            .Because("Releasing a still-live gate after Dispose must not throw ObjectDisposedException (#2585).");
        await Assert.That(agent.IsRunning()).IsFalse();
    }

    [Test]
    public async Task Dispose_CalledFromInsideTheRun_DefersWithoutWaitingOnItself()
    {
        Session session = NewDisposeSession();
        var loop = new SelfDisposingLoop();
        using DefaultAgent agent = NewDisposeAgent(session, loop);
        loop.Agent = agent;
        agent.Initialize(session, TestAgents.AllowAll());

        Result result = await agent.PromptAsync("work").WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(loop.DisposeError).IsNull()
            .Because("A listener or loop disposing its own agent must not take an ObjectDisposedException (#2585).");
        await Assert.That(result).IsSuccessful()
            .Because("The run must be able to finish after a reentrant Dispose instead of deadlocking on its exit signal.");
    }
}
