using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     Regression tests for concurrent agent runs sharing the TEA pipeline
///     (multi-agent sprint): every fold must ride <c>Dispatch(UiMsg)</c> through the
///     CAS reducer — the removed <c>UiStore.Transition</c> escape hatch allowed
///     callers to bypass the pure reducer, so two parallel prompts could clobber
///     each other's status (e.g. paint a failed session "error" into a healthy one).
/// </summary>
public class TuiEffectHostConcurrencyTests
{
    /// <summary>Runner whose PromptAsync returns a pre-set (possibly pending) task.</summary>
    private sealed class GatedRunner(Task<Result> outcome) : IAgentRunner
    {
        public CancellationTokenSource AbortSource { get; } = new();

        public Task<Result> PromptAsync(string text, CancellationToken ct = default) => outcome;

        public Task WaitForIdleAsync(CancellationToken ct = default) => Task.CompletedTask;

        public void ResetAbortSource()
        {
        }
    }

    private static TaskCompletionSource<UiState> SettledWhenIdle(UiStore store)
    {
        var settled = new TaskCompletionSource<UiState>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Changed += (_, e) =>
        {
            if (!e.State.IsAgentRunning && e.State.Status is "idle" or "error")
                settled.TrySetResult(e.State);
        };
        return settled;
    }

    private static bool HasErrorLine(UiState state, string text) =>
        state.Lines.Any(l => l.Role == ChatRole.Error && l.Text == text);

    [Test]
    public async Task TwoParallelPromptHosts_FailureDoesNotLeakIntoHealthySession()
    {
        var failing = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        var succeeding = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        var storeA = new UiStore();
        var storeB = new UiStore();
        var hostA = new TuiEffectHost(new GatedRunner(failing.Task), storeA);
        var hostB = new TuiEffectHost(new GatedRunner(succeeding.Task), storeB);

        var settledA = SettledWhenIdle(storeA);
        var settledB = SettledWhenIdle(storeB);

        // Two concurrent prompts — one per session store (R25/R26 per-session stores).
        hostA.Run(new TuiEffect.PromptAgent("explode"));
        hostB.Run(new TuiEffect.PromptAgent("be healthy"));

        await Assert.That(storeA.State.IsAgentRunning).IsTrue();
        await Assert.That(storeB.State.IsAgentRunning).IsTrue();

        // Session B finishes CLEANLY while session A is still in flight.
        succeeding.TrySetResult(Result.Success());
        UiState stateB = await settledB.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(stateB.Status).IsEqualTo("idle");
        await Assert.That(HasErrorLine(storeB.State, "session A failed")).IsFalse();

        // Session A then fails — the error must land ONLY in A's store.
        failing.TrySetResult(Result.Failure("session A failed"));
        UiState stateA = await settledA.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(stateA.Status).IsEqualTo("error");
        await Assert.That(HasErrorLine(storeA.State, "session A failed")).IsTrue();

        // No cross-contamination in either direction.
        await Assert.That(HasErrorLine(storeB.State, "session A failed")).IsFalse();
        await Assert.That(storeB.State.Status).IsEqualTo("idle");
        await Assert.That(storeA.State.IsAgentRunning).IsFalse();
        await Assert.That(storeB.State.IsAgentRunning).IsFalse();
    }

    [Test]
    public async Task ConcurrentDispatches_SameStore_CasKeepsEveryFold()
    {
        var store = new UiStore();
        const int perThread = 200;
        const int threads = 4;

        var tasks = Enumerable.Range(0, threads)
            .Select(_ => Task.Run(async () =>
            {
                for (int i = 0; i < perThread; i++)
                {
                    store.Dispatch(new UiMsg.AppendLine(ChatRole.System, "fold"));
                    await Task.Yield();
                }
            }))
            .ToArray();

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));

        // CAS retry must not lose a single fold — no locks, no lost updates.
        await Assert.That(store.State.Lines.Length).IsEqualTo(threads * perThread);
    }
}
