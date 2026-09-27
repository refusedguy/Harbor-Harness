// SessionLockStrip.cs — per-session async mutual exclusion for the session
// stores (Harbor.Storage.Jsonl, Harbor.Storage.Sqlite).
//
// Linked source (no .csproj): compiled into each store assembly via
// <Compile Include="..\Harbor.Storage.Shared\*.cs" /> — the same mechanism as
// Harbor.Providers.Shared. No <ProjectReference> is added, so the
// Storage_ReferencesOnlyAbstractions architecture rule stays green.
//
// This replaces the coarse <c>lock (_lock)</c> global mutex the SQLite store
// used to hold across every mutation (#184): operations on different sessions
// now proceed concurrently, while operations on the SAME session stay
// serialized exactly as before. The JSONL store already striped this way with
// an inline <c>ConcurrentDictionary&lt;string, SemaphoreSlim&gt;</c>; both
// stores now share one acquire/evict implementation so the semantics cannot
// drift (e.g. forgetting the CT guard or the delete-time eviction).
//
// Ownership note: the strip dictionary itself stays a private field of each
// store (<c>_sessionLocks</c> — pinned by name via reflection in
// JsonlSessionStoreConcurrencyTests). These helpers only operate on it.

using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Harbor.Storage.Shared;

/// <summary>
///     Per-session <see cref="SemaphoreSlim" /> management shared by the
///     session stores. One semaphore per session id; unrelated sessions never
///     block each other, same-session writers serialize.
/// </summary>
internal static class SessionLockStrip
{
    /// <summary>
    ///     Get (creating on first use) the mutex for a session. Observes
    ///     <paramref name="ct" /> before touching the strip (§3.4: an Esc
    ///     must never surface as a store failure).
    /// </summary>
    public static ValueTask<SemaphoreSlim> AcquireAsync(
        ConcurrentDictionary<string, SemaphoreSlim> strip,
        string sessionId,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return new ValueTask<SemaphoreSlim>(
            strip.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1)));
    }

    /// <summary>
    ///     Drop a session's mutex (delete path). A stale entry would pin a
    ///     <see cref="SemaphoreSlim" /> per deleted session forever; eviction
    ///     keeps the strip proportional to live sessions. A racing acquirer
    ///     simply recreates the entry — both observe the same file state.
    /// </summary>
    public static void Evict(
        ConcurrentDictionary<string, SemaphoreSlim> strip,
        string sessionId) =>
        strip.TryRemove(sessionId, out _);
}
