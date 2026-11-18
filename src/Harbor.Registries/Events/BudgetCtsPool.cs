namespace Harbor.Registries.Events;

/// <summary>
///     Single-slot cache of spare budget <see cref="CancellationTokenSource" />
///     instances (#249), extracted from <see cref="InMemoryEventBus" /> (issue
///     #2561, foundations slice). Rent, <c>TryReset</c>-gated return and
///     contention disposal are one contract and move together; the bus keeps
///     its dispatch semantics and delegates here.
/// </summary>
/// <remarks>
///     The 1/N-subscriber publish path used to allocate a fresh CTS per publish
///     even when every handler completed synchronously. Publishes now rent this
///     instance (allocating one on first use or contention) and return it
///     through <c>TryReset</c>-gated caching. The cached instance is always
///     unlinked — linkage to the publisher's token is emulated per publish with
///     a registration — and never carries a pending budget timer: the timer is
///     armed only for handlers that outlive their synchronous part, which is
///     what makes the instance genuinely reusable. Exchanged with
///     <c>Interlocked</c> only: contention losers dispose their spare instead
///     of blocking.
/// </remarks>
internal sealed class BudgetCtsPool
{
    private CancellationTokenSource? _pooled;

    /// <summary>
    ///     Rent a spare source (#249). Returns the cached instance when a
    ///     previous publish left one behind, otherwise allocates. The rented
    ///     instance is always unlinked, reset and free of a pending budget
    ///     timer — ready for <c>CancelAfter</c> if a handler needs the budget,
    ///     and untouched if not, which is what lets the next publish rent it
    ///     again.
    /// </summary>
    public CancellationTokenSource Rent() =>
        Interlocked.Exchange(ref _pooled, null) ?? new CancellationTokenSource();

    /// <summary>
    ///     Return a rented source to the single-slot cache (#249).
    ///     <c>TryReset</c> refuses a source that was cancelled (a non-cooperative
    ///     orphan still holding the token, or the emulated link firing) — those
    ///     are disposed instead of poisoning the pool. Everything else resets:
    ///     leftover callback registrations are unregistered by the runtime, and
    ///     a still-pending budget timer is disarmed (it only refuses once that
    ///     timer reached <c>TimerQueue</c>'s fire loop, which a 250 ms budget
    ///     returned on time never does — #513). On contention the spare is
    ///     likewise disposed rather than retained.
    /// </summary>
    public void Return(CancellationTokenSource cts)
    {
        if (cts.TryReset())
        {
            Interlocked.Exchange(ref _pooled, cts)?.Dispose();
        }
        else
        {
            cts.Dispose();
        }
    }
}
