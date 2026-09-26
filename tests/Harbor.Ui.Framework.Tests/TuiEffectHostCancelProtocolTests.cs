using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Ui.Framework.State;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     #91 (cancel/reset protocol) regression tests for <see cref="TuiEffectHost" />:
///     a single terminal <c>AgentEnded</c> per run (the failure path used to
///     publish <c>AgentEnded("error")</c> AND fall into the finally's
///     <c>AgentEnded()</c>), and per-run store capture (a
///     <see cref="TuiEffectHost.RebindStore" /> landing mid-flight must not
///     redirect the in-flight run's terminal dispatches into the new store).
/// </summary>
public class TuiEffectHostCancelProtocolTests
{
    /// <summary>Runner whose PromptAsync returns a pre-set (possibly pending) task.</summary>
    private sealed class GatedRunner(Task<Result> outcome) : IAgentRunner
    {
        private readonly CancellationTokenSource _abortSource = new();
        public CancellationToken AbortToken => _abortSource.Token;
        public void RequestAbort() => _abortSource.Cancel();

        public Task<Result> PromptAsync(string text, CancellationToken ct = default) => outcome;

        public Task WaitForIdleAsync(CancellationToken ct = default) => Task.CompletedTask;

        public void ResetAbortSource()
        {
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }

            await Task.Delay(10);
        }
    }

    [Test]
    public async Task PromptAsync_Failure_PublishesExactlyOneTerminalEnd()
    {
        var store = new UiStore();
        var host = new TuiEffectHost(
            new GatedRunner(Task.FromResult(Result.Failure("boom"))),
            store);

        var dispatches = 0;
        store.Changed += (_, _) => Interlocked.Increment(ref dispatches);

        host.Run(new TuiEffect.PromptAgent("explode"));

        await WaitUntilAsync(
            () => !store.State.IsAgentRunning && store.State.Status == "error",
            "failed run to settle");

        // Let any stray (duplicate terminal) dispatch land before counting.
        await Task.Delay(250);

        // Exactly AgentStarted + one AgentEnded — the pre-fix shape dispatched
        // AgentEnded("error", ...) AND the finally's AgentEnded() (3 total).
        await Assert.That(dispatches).IsEqualTo(2);
        await Assert.That(store.State.Status).IsEqualTo("error");
        await Assert.That(store.State.Lines.Count(l => l.Role == Harbor.Abstractions.Models.ChatRole.Error)).IsEqualTo(1);
    }

    [Test]
    public async Task PromptAsync_RebindMidRun_TerminalLandsInCapturedStore()
    {
        var gate = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        var storeA = new UiStore();
        var storeB = new UiStore();
        var host = new TuiEffectHost(new GatedRunner(gate.Task), storeA);

        host.Run(new TuiEffect.PromptAgent("long run"));
        await WaitUntilAsync(() => storeA.State.IsAgentRunning, "run to start in store A");

        // Session switch mid-flight: the run must still terminate in A.
        host.RebindStore(storeB);
        gate.TrySetResult(Result.Success());

        await WaitUntilAsync(
            () => !storeA.State.IsAgentRunning,
            "run to terminate in the captured store");

        // Let any misdirected dispatch land before asserting.
        await Task.Delay(250);

        await Assert.That(storeA.State.Status).IsEqualTo("idle");
        await Assert.That(storeB.State.IsAgentRunning).IsFalse();
        await Assert.That(storeB.State.Lines.Length).IsEqualTo(0);
    }
}
