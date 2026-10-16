// SessionSnapshotRetryPolicy.cs — Decorator: bounded retry over one snapshot-read attempt (#1586).
//
// SessionFileReader used to carry its own retry loop inline in the read method
// (counter, re-measure, drop-previous-records, last-attempt-passthrough).
// Attempt counting, the inter-attempt delay and the last-attempt passthrough
// are now this decorator's job; the reader owns a single pass over the file.
//
// Why the Application-layer RetryPolicy is NOT reused literally: this assembly
// is Infrastructure and may reference only Harbor.Abstractions
// (docs/ARCHITECTURE_LAYERS.md §2, enforced by FullLayerMatrixTests) — an
// Infrastructure → Application edge is a dependency-inversion violation, the
// same reason the remote-MCP transports consume TransientFailurePolicy rather
// than RetryPolicy (TransportRetryOwnershipRules, #572). The shared budget
// (DefaultMaxAttempts) and backoff (BackoffDelay) ARE that policy's verdict,
// reused here instead of a reader-private constant; this type only applies
// them to a result-shaped ("stable?") completion test rather than RetryPolicy's
// exception-shaped one — a torn snapshot is not an exception, it is an
// explicitly unstable value the caller must not cache (#459).

using Harbor.Abstractions.Resilience;

namespace Harbor.Storage.Jsonl;

/// <summary>
///     Decorator over a single snapshot-read attempt: re-runs the attempt while
///     it reports itself incomplete, sleeps the shared backoff between attempts
///     so a concurrent writer can finish, and returns the last attempt as-is
///     when the budget is exhausted.
/// </summary>
internal static class SessionSnapshotRetryPolicy
{
    /// <summary>
    ///     Run <paramref name="attempt" /> until <paramref name="isComplete" />
    ///     accepts the outcome or the shared attempt budget is exhausted; the
    ///     last outcome is returned as-is either way.
    /// </summary>
    /// <param name="attempt">One pass over the file.</param>
    /// <param name="isComplete">
    ///     True when the attempt's outcome needs no retry — a stable snapshot,
    ///     or a failure (e.g. an unbounded file) retrying cannot fix.
    /// </param>
    /// <param name="ct">Cancellation token observed before every attempt and sleep.</param>
    internal static async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> attempt,
        Func<T, bool> isComplete,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(isComplete);

        int attemptNumber = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            attemptNumber++;

            T current = await attempt(ct).ConfigureAwait(false);
            if (isComplete(current) || attemptNumber >= TransientFailurePolicy.DefaultMaxAttempts)
                return current;

            await Task.Delay(TransientFailurePolicy.BackoffDelay(attemptNumber), ct).ConfigureAwait(false);
        }
    }
}
