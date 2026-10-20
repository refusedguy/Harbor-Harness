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
        // The claim this test makes is "the fault was OBSERVED": with a null
        // callback the exception is read and dropped, so it never surfaces as
        // an unobserved task exception. `Assert.That(true).IsTrue()` — the
        // assertion this test used to end with — asserted nothing at all
        // (TUnit's TUnitAssertions0005 says so out loud), so the claim was
        // never checked. It is checked here: the process-wide unobserved
        // exception event is captured, and the assertion is that THIS fault
        // never reached it.
        var unobserved = new List<Exception>();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs args)
        {
            lock (unobserved)
            {
                unobserved.AddRange(args.Exception.InnerExceptions);
            }
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            TaskFireAndForget.Forget(Task.FromException(new InvalidOperationException("dropped")));

            await Task.Delay(100).ConfigureAwait(false);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            // The event is raised from the finalizer thread, so give it a
            // moment to be delivered before the absence is asserted.
            await Task.Delay(100).ConfigureAwait(false);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }

        // Snapshot under the lock: the handler runs on a finalizer thread, and
        // unsubscribing does not prove it is not mid-add.
        Exception[] surfaced;
        lock (unobserved)
        {
            surfaced = [.. unobserved];
        }

        await Assert.That(surfaced.Any(ex => ex.Message == "dropped")).IsFalse()
            .Because("TaskFireAndForget reads the fault even with a null callback, so it must "
                   + "never reach TaskScheduler.UnobservedTaskException");
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
