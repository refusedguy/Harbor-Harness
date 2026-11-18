using System.Runtime.ExceptionServices;

namespace Harbor.TestKit;

/// <summary>
///     A test-only worker whose idle state is exact: <see cref="DrainAsync" />
///     waits for BOTH the queued items and the item that is currently running
///     (issue #2673, item A17). Tests that poll a queue or sleep for a few
///     milliseconds to let a background callback finish are asserting against
///     the scheduler; a drain makes "queue empty" mean "no work in flight".
/// </summary>
/// <remarks>
///     Work runs sequentially in enqueue order. A faulting item does not stop
///     the items behind it: the first fault is recorded and rethrown by the
///     next <see cref="DrainAsync" /> once the worker is idle. That keeps the
///     helper usable as a shared test instrument instead of a one-shot
///     assertion whose first exception poisons every later enqueue.
/// </remarks>
public sealed class DrainableWorker
{
    private readonly object _gate = new();
    private Task _tail = Task.CompletedTask;
    private int _enqueued;
    private int _completed;
    private ExceptionDispatchInfo? _fault;

    /// <summary>
    ///     Number of items that are running or waiting. Zero means the worker
    ///     has no work in flight, but a caller that needs to WAIT for that
    ///     state should use <see cref="DrainAsync" />.
    /// </summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _enqueued - _completed;
            }
        }
    }

    /// <summary>Queue one unit of work. Later items start after this one finishes.</summary>
    public void Enqueue(Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        lock (_gate)
        {
            Task previous = _tail;
            _enqueued++;
            _tail = RunAsync(previous, work);
        }
    }

    /// <summary>
    ///     Wait until every item queued so far has finished, then surface the
    ///     first fault, if any. The cancellation token cancels the WAIT, not
    ///     the work itself: work owns its own lifetime.
    /// </summary>
    public async Task DrainAsync(CancellationToken ct = default)
    {
        while (true)
        {
            Task tail;
            lock (_gate)
            {
                if (_enqueued == _completed)
                {
                    _fault?.Throw();
                    return;
                }

                tail = _tail;
            }

            await tail.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task RunAsync(Task previous, Func<Task> work)
    {
        // A faulted predecessor must not block the queue: its exception is
        // reported by DrainAsync, not by the next item's await.
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch
        {
            // Intentionally swallowed; see _fault and DrainAsync.
        }

        try
        {
            await work().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _fault ??= ExceptionDispatchInfo.Capture(ex);
            }
        }
        finally
        {
            lock (_gate)
            {
                _completed++;
            }
        }
    }
}
