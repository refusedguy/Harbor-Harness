using System.Buffers;

namespace Harbor.Registries.Events;

/// <summary>
///     Bounded dispatch-duration sample window (#47/S2): a fixed-capacity ring
///     of monotonic stopwatch ticks plus the nearest-rank percentile view the
///     telemetry layer polls.
/// </summary>
/// <remarks>
///     Extracted verbatim from <see cref="InMemoryEventBus" /> (issue #2561,
///     foundations slice): the ring, its lock, the head/count pair and the
///     percentile computation share one contract — the lock guards the slots,
///     the count saturates at capacity, and the reader copies under the same
///     lock — so they move as one unit. The bus keeps its public surface and
///     delegates to this type; no sampling or percentile logic changed.
///     <para>
///         Retained memory is O(1) (256 longs = 2 KB) no matter how many
///         publishes run. One slot store under a short lock per completed
///         publish; the percentile rents a pooled buffer because the telemetry
///         layer polls, not the publish path. The ring is a power of two so the
///         slot index is a mask, not a modulo. Percentile returns raw ticks so
///         the single <c>StopwatchTicksToTimeSpan</c> conversion stays on the
///         bus, where the other queue-age gauges use it too.
///     </para>
/// </remarks>
internal sealed class DispatchWindow
{
    private readonly long[] _ticks;
    private readonly int _mask;
    private readonly object _lock = new();
    private int _head;
    private int _count;

    /// <param name="capacity">Number of retained samples. Must be a positive power of two.</param>
    public DispatchWindow(int capacity)
    {
        if (capacity <= 0 || (capacity & (capacity - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be a positive power of two.");
        }

        _ticks = new long[capacity];
        _mask = capacity - 1;
    }

    public int Capacity => _ticks.Length;

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _count;
            }
        }
    }

    /// <summary>
    ///     Fold one completed publish into the ring. One slot store under a
    ///     short lock — no allocation, no await, safe inside <c>finally</c>.
    /// </summary>
    public void Record(long elapsedTicks)
    {
        lock (_lock)
        {
            _ticks[_head] = elapsedTicks;
            _head = (_head + 1) & _mask;
            if (_count < _ticks.Length)
            {
                _count++;
            }
        }
    }

    /// <summary>
    ///     Nearest-rank percentile over a point-in-time copy of the window, as
    ///     raw stopwatch ticks (zero when empty). Quantiles are clamped to
    ///     <c>[0, 1]</c>; <see cref="double.NaN" /> counts as an empty request.
    /// </summary>
    public long Percentile(double quantile)
    {
        if (double.IsNaN(quantile) || quantile <= 0)
        {
            quantile = 0;
        }
        else if (quantile > 1)
        {
            quantile = 1;
        }

        // Pooled: the telemetry layer polls, the publish path never touches it.
        long[] window = ArrayPool<long>.Shared.Rent(_ticks.Length);
        try
        {
            int count;
            lock (_lock)
            {
                count = _count;
                if (count == 0)
                {
                    return 0;
                }

                // Copy under the lock; the order inside the window is irrelevant
                // to a percentile, only the multiset matters.
                Array.Copy(_ticks, window, count);
            }

            Array.Sort(window, 0, count);

            // Nearest-rank: index = ceil(q * n) - 1, clamped into the window.
            int index = (int)Math.Ceiling(quantile * count) - 1;
            if (index < 0)
            {
                index = 0;
            }
            else if (index >= count)
            {
                index = count - 1;
            }

            return window[index];
        }
        finally
        {
            ArrayPool<long>.Shared.Return(window);
        }
    }
}
