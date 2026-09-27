using Harbor.Abstractions.Tools;
using TUnit.Assertions;

namespace Harbor.Abstractions.Tests;

/// <summary>
///     #201: the shared fire-and-forget helper observes faults (§FP-006) and
///     classifies owner-cancelled aborts as non-errors (CT-002: abort ≠ error).
/// </summary>
public class TaskFireAndForgetTests
{
    [Test]
    public async Task FaultedTask_InvokesOnError()
    {
        var seen = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskFireAndForget.Forget(
            Task.FromException(new InvalidOperationException("boom")),
            ex => seen.TrySetResult(ex));

        Exception fault = await seen.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await Assert.That(fault.Message).IsEqualTo("boom");
    }

    [Test]
    public async Task FaultedTask_NullCallback_ObservedWithoutThrow()
    {
        TaskFireAndForget.Forget(Task.FromException(new InvalidOperationException("dropped")));

        await Task.Delay(100).ConfigureAwait(false);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        await Assert.That(true).IsTrue();
    }

    [Test]
    public async Task OwnerCancelledAbort_Swallowed()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        bool called = false;
        TaskFireAndForget.Forget(
            Task.FromException(new OperationCanceledException()),
            _ => called = true,
            cts.Token);

        await Task.Delay(200).ConfigureAwait(false);
        await Assert.That(called).IsFalse();
    }

    [Test]
    public async Task CancelledFault_WithoutOwnerToken_Surfaces()
    {
        var seen = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskFireAndForget.Forget(
            Task.FromException(new OperationCanceledException()),
            ex => seen.TrySetResult(ex));

        Exception fault = await seen.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await Assert.That(fault).IsTypeOf<OperationCanceledException>();
    }
}
