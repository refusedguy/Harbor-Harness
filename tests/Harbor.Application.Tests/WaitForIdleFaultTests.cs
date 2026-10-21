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
        await Assert.That(promptResult.IsFailure).IsTrue()
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
}
