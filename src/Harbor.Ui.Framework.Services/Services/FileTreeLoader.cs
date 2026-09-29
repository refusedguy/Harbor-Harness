using System.Collections.Immutable;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Filesystem;
using Harbor.Abstractions.Tools;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.Logging;

namespace Harbor.Ui.Framework.Services;

/// <summary>
///     Owns the file-tree listing load: what is in flight, what cancels it, and
///     where the result lands (#667).
/// </summary>
/// <remarks>
/// <para>
///     <b>Why this type exists at all.</b> The listing is state, the walk is I/O,
///     and something has to sit between a view that wants to see a directory and
///     a filesystem that has to be asked. Before #667 that something was a private
///     field in the panel, and because a render-thread cache can only be filled by
///     the render thread, the walk had to run inside a painted frame. This is the
///     part that was actually missing: not a call to move, but the owner of its
///     lifetime.
/// </para>
/// <para>
///     <b>Why the view ASKS, rather than subscribing.</b> The obvious alternative
///     is a service that subscribes to <c>UiStore.Changed</c> and reconciles
///     "every panel with a directory in it" on every dispatch. It was rejected on
///     one concrete ground: a panel that is hidden is never built, so the user may
///     not open the file tree for the whole session, and a subscription would walk
///     the CWD anyway. A demand signal fires when there is a frame to draw and
///     stops when there is not. It is also the only one of the two that stays
///     correct across renderer swaps and per-session stores, where a subscription's
///     lifetime has to be re-established by hand.
/// </para>
/// <para>
///     <b>Why demand is a method rather than a message.</b> A message would need
///     an effect arm, and an effect arm needs a runner that knows about file
///     trees — every host would grow a dependency it otherwise does not have, in
///     exchange for a hop through a union that carries no extra information. The
///     store still owns the RESULT; only the ASK is a direct call, and it is a
///     call that does nothing but start a cancellable task.
/// </para>
/// <para>
///     <b>Single-flight per panel.</b> One in-flight walk per panel id, and a
///     second request for the same directory is a no-op. Holding a per-panel
///     <see cref="CancellationTokenSource" /> rather than one global source is
///     deliberate: navigating the file tree must not cancel an unrelated panel's
///     load, and closing the panel must not either.
/// </para>
/// <para>
///     <b>Thread safety.</b> <see cref="Request" /> is called from the render
///     thread; the walk completes on a thread-pool thread and dispatches from
///     there. The in-flight map is guarded by a lock and every entry is compared
///     by reference before it is acted on, so a superseded walk that finishes
///     late cannot overwrite the walk that replaced it.
/// </para>
/// </remarks>
public sealed class FileTreeLoader : IFileTreeLoader, IDisposable
{
    /// <summary>Hard ceiling on one walk, mirroring the port's own limit.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly IDirectoryLister _lister;
    private readonly ILogger<FileTreeLoader> _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, InFlight> _inFlight = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>One walk in flight, identified so a late finisher can tell it lost the race.</summary>
    private sealed class InFlight(string directory, CancellationTokenSource cts)
    {
        public string Directory { get; } = directory;

        public CancellationTokenSource Cts { get; } = cts;
    }

    /// <summary>Construct a loader over the injected directory-listing seam.</summary>
    /// <param name="lister">The Domain port that performs the walk off the render thread.</param>
    /// <param name="logger">Logger for the diagnostic trail of a failed load.</param>
    public FileTreeLoader(IDirectoryLister lister, ILogger<FileTreeLoader> logger)
    {
        ArgumentNullException.ThrowIfNull(lister);
        _lister = lister;
        _logger = logger;
    }

    /// <summary>How many walks are in flight right now. Diagnostics and tests only.</summary>
    public int InFlightCount
    {
        get
        {
            lock (_gate)
            {
                return _inFlight.Count;
            }
        }
    }

    /// <summary>
    ///     Ask for the listing of <paramref name="directory" /> for
    ///     <paramref name="panelId" />, starting a walk if one is not already on
    ///     its way. Returns immediately either way — this is called from the render
    ///     thread and must never block it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A request for a directory the store has already settled is a no-op,
    ///         which is what makes calling this once per frame affordable. A request
    ///         for a DIFFERENT directory supersedes whatever is in flight for that
    ///         panel: the old source is cancelled, and the old walk's result is
    ///         discarded rather than painted.
    ///     </para>
    ///     <para>
    ///         <paramref name="store" /> is passed in rather than injected because
    ///         the same loader serves per-session stores; a singleton that captured
    ///         one store would be wrong the moment a session is rebound.
    ///     </para>
    /// </remarks>
    /// <param name="panelId">The panel id that wants the listing.</param>
    /// <param name="directory">The resolved directory to list. Must not be the empty "means CWD" marker.</param>
    /// <param name="store">The store to publish the result into. Null disables the request (degraded hosts).</param>
    public void Request(string panelId, string directory, UiStore? store) =>
        StartRequest(panelId, directory, store);

    /// <inheritdoc />
    public void CancelPanelLoad(string panelId) => StopPanelLoad(panelId);

    /// <summary>
    ///     The body of <see cref="Request" />. Split out so the public member
    ///     carries the contract and this carries the reasoning, which is long
    ///     enough that burying it in the interface implementation would make the
    ///     part that must be right — the lock boundaries — hard to see.
    /// </summary>
    private void StartRequest(string panelId, string directory, UiStore? store)
    {
        if (string.IsNullOrEmpty(panelId) || string.IsNullOrEmpty(directory) || store is null)
        {
            return;
        }

        CancellationTokenSource? superseded = null;
        InFlight? started = null;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // Already settled for this exact directory: nothing to do, ever again
            // until something invalidates it.
            FileTreeSnapshot current = store.State.Ui.FileTreeFor(panelId, directory);
            if (current.Status is AsyncStatus.Success or AsyncStatus.Error)
            {
                return;
            }

            if (_inFlight.TryGetValue(panelId, out InFlight? existing))
            {
                if (string.Equals(existing.Directory, directory, StringComparison.Ordinal))
                {
                    return; // same walk, still running
                }

                superseded = existing.Cts;
                started = new InFlight(directory, new CancellationTokenSource(Timeout));
                _inFlight[panelId] = started;
            }
            else
            {
                started = new InFlight(directory, new CancellationTokenSource(Timeout));
                _inFlight[panelId] = started;
            }
        }

        // Outside the lock: cancel the loser, publish the intent, then start.
        // A dispatch inside the lock would run arbitrary store subscribers under
        // it, which is how a lock becomes a deadlock.
        Cancel(superseded);

        // Publish "loading" BEFORE the walk so the next frame has something honest
        // to draw, and so a frame that arrives before the walk finishes does not
        // issue a second request for the same directory.
        store.Dispatch(new AppMsg.SetFileTreePending(panelId, directory));

        TaskFireAndForget.Forget(
            LoadAsync(panelId, directory, started, store),
            ex => _logger.LogError(ex, "File-tree load failed for {Dir}", directory));
    }

    /// <summary>
    ///     Cancel whatever walk is in flight for <paramref name="panelId" />, if
    ///     any. Used when a panel closes; the store entry is left alone, because
    ///     the listing is still a true statement about that directory.
    /// </summary>
    private void StopPanelLoad(string panelId)
    {
        CancellationTokenSource? cancelled = null;
        lock (_gate)
        {
            if (string.IsNullOrEmpty(panelId) || !_inFlight.Remove(panelId, out InFlight? existing))
            {
                return;
            }

            cancelled = existing.Cts;
        }

        Cancel(cancelled);
    }

    /// <summary>
    ///     Cancel every in-flight walk and release their sources. Safe to call more
    ///     than once.
    /// </summary>
    public void Dispose()
    {
        CancellationTokenSource[] cancelled;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            cancelled = _inFlight.Values.Select(static f => f.Cts).ToArray();
            _inFlight.Clear();
        }

        foreach (CancellationTokenSource cts in cancelled)
        {
            Cancel(cts);
        }
    }

    /// <summary>The walk itself. Never touches the render thread's stack.</summary>
    private async Task LoadAsync(string panelId, string directory, InFlight flight, UiStore store)
    {
        try
        {
            Result<DirectoryListing> result = await _lister
                .ListAsync(directory, flight.Cts.Token)
                .ConfigureAwait(false);

            if (!Publish(panelId, flight))
            {
                return; // superseded or disposed while the walk was running
            }

            if (result.IsSuccess)
            {
                store.Dispatch(new AppMsg.SetFileTreeLoaded(
                    panelId,
                    directory,
                    ToEntries(result.Value),
                    result.Value.Truncated,
                    result.Value.TotalCount));
                return;
            }

            store.Dispatch(new AppMsg.SetFileTreeFailed(panelId, directory, result.Error));
        }
        catch (OperationCanceledException)
        {
            // Superseded, timed out, or the host is shutting down. NOT an error:
            // the newer walk owns the state, and painting a failure for a result
            // nobody is waiting for is a lie the user would have to read.
            _logger.LogDebug("File-tree load for {Dir} cancelled", directory);
        }
        catch (Exception ex)
        {
            // The contract says an expected failure is a Result, so anything here
            // is a bug or a genuinely exotic IO error. It is still not allowed to
            // take the renderer down with it, and the panel still needs to be told
            // something.
            _logger.LogError(ex, "File-tree load threw for {Dir}", directory);
            if (Publish(panelId, flight))
            {
                store.Dispatch(new AppMsg.SetFileTreeFailed(panelId, directory, ex.Message));
            }
        }
        finally
        {
            Release(panelId, flight);
        }
    }

    /// <summary>
    ///     Whether <paramref name="flight" /> is still the walk this panel is
    ///     waiting for. Reference identity, deliberately: two walks of the SAME
    ///     directory can overlap (a refresh that raced a re-request), and the
    ///     older one must lose even though the directory matches.
    /// </summary>
    private bool Publish(string panelId, InFlight flight)
    {
        lock (_gate)
        {
            return !_disposed
                && _inFlight.TryGetValue(panelId, out InFlight? current)
                && ReferenceEquals(current, flight);
        }
    }

    private void Release(string panelId, InFlight flight)
    {
        CancellationTokenSource? dispose = null;
        lock (_gate)
        {
            if (_inFlight.TryGetValue(panelId, out InFlight? current) && ReferenceEquals(current, flight))
            {
                _inFlight.Remove(panelId);
                dispose = flight.Cts;
            }
        }

        // The source is disposed only when it is still ours. A superseded walk's
        // source was cancelled by Request and is disposed there, once the walk
        // that replaced it has taken the map slot.
        dispose?.Dispose();
    }

    private static void Cancel(CancellationTokenSource? cts)
    {
        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The walk already finished and released its own source. Losing a
            // cancel race against your own completion is normal, not an error.
        }
    }

    /// <summary>
    ///     Project the port's vocabulary into the state's. Both sides define their
    ///     own entry type on purpose — see <see cref="FileTreeEntry" /> — so the
    ///     seam can be reshaped without a state migration, and the state assembly
    ///     never has to reference a port contract it does not implement.
    /// </summary>
    private static ImmutableArray<FileTreeEntry> ToEntries(DirectoryListing listing)
    {
        var builder = ImmutableArray.CreateBuilder<FileTreeEntry>(listing.Entries.Count);
        foreach (DirectoryEntry entry in listing.Entries)
        {
            builder.Add(FileTreeEntry.From(entry));
        }

        return builder.ToImmutable();
    }
}
