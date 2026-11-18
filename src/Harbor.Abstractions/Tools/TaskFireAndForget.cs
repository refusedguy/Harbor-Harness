namespace Harbor.Abstractions.Tools;

/// <summary>
///     Single fire-and-forget helper for tool-adjacent paths (#201).
///     Attaches an <c>OnlyOnFaulted</c> continuation that observes the exception
///     (§FP-006: no unobserved-task deaths) and routes it to
///     <c>onError</c>. An <see cref="OperationCanceledException" />
///     racing a cancelled <c>cancellationToken</c> is an expected
///     abort (CT-002: abort ≠ error), not a failure — it is swallowed.
/// </summary>
/// <remarks>
///     The <c>Run</c> contract of effect hosts stays synchronous, so awaiting is
///     not an option — but every fault is now observable instead of dying in
///     <c>TaskScheduler.UnobservedTaskException</c>. Even with a
///     <see langword="null" /> callback the exception is observed (read), just
///     not logged.
///     The continuation is the last observer in this chain. If <c>onError</c>
///     throws, rethrowing would fault the continuation with nobody left to observe
///     it, reproducing this defect one level deeper (FP-003/FP-006). Such a
///     secondary fault is reported to <see cref="System.Diagnostics.Trace" />
///     and swallowed instead.
/// </remarks>
public static class TaskFireAndForget
{
    /// <summary>
    ///     Launch <paramref name="task" /> without awaiting it; observe its fault.
    /// </summary>
    /// <param name="task">The task to detach (must not be null).</param>
    /// <param name="onError">Fault sink (typically a log call). Null observes-and-drops.</param>
    /// <param name="cancellationToken">
    ///     Owner's token, used only to classify <see cref="OperationCanceledException" />:
    ///     cancelled-owner aborts are swallowed, anything else reaches <paramref name="onError" />.
    /// </param>
    public static void Forget(Task task, Action<Exception>? onError = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        task.ContinueWith(
            static (t, state) =>
            {
                var (callback, token) = ((Action<Exception>?, CancellationToken))state!;
                Exception fault = t.Exception?.InnerException ?? t.Exception!;
                if (fault is OperationCanceledException && token.IsCancellationRequested)
                    return;
                try
                {
                    callback?.Invoke(fault);
                }
                catch (Exception sinkFault)
                {
                    // FP-003/FP-006: the original fault was already handed to this
                    // sink, so a second fault has no recipient. Letting it escape
                    // would fault THIS continuation with nobody observing it, the
                    // exact defect Forget exists to prevent one level deeper.
                    // Report the broken sink on the diagnostic trace and return
                    // normally; never rethrow from the last observer.
                    try
                    {
                        System.Diagnostics.Trace.TraceError(
                            "TaskFireAndForget fault sink threw while reporting {0}: {1}",
                            fault,
                            sinkFault);
                    }
                    catch
                    {
                        // Even a throwing trace listener must not resurrect the
                        // unobserved-fault bug. There is no further sink.
                    }
                }
            },
            (onError, cancellationToken),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }
}
