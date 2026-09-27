namespace Harbor.Abstractions.Tools;

/// <summary>
///     Single fire-and-forget helper for tool-adjacent paths (#201).
///     Attaches an <c>OnlyOnFaulted</c> continuation that observes the exception
///     (§FP-006: no unobserved-task deaths) and routes it to
///     <paramref name="onError" />. An <see cref="OperationCanceledException" />
///     racing a cancelled <paramref name="cancellationToken" /> is an expected
///     abort (CT-002: abort ≠ error), not a failure — it is swallowed.
/// </summary>
/// <remarks>
///     The <c>Run</c> contract of effect hosts stays synchronous, so awaiting is
///     not an option — but every fault is now observable instead of dying in
///     <c>TaskScheduler.UnobservedTaskException</c>. Even with a
///     <see langword="null" /> callback the exception is observed (read), just
///     not logged.
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
                callback?.Invoke(fault);
            },
            (onError, cancellationToken),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }
}
