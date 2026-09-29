using Harbor.Abstractions.Models;
using Harbor.Application.Diagnostics;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.Logging;

namespace Harbor.App.Cli.Repl;

/// <summary>
///     Wires the headless core's <see cref="DiagnosticsAggregator" /> to the REPL's
///     UI store: every snapshot the core composes is dispatched as
///     <see cref="ChatAppMsg.SyncDiagnostics" />, and the reducer stores it.
/// </summary>
/// <remarks>
///     <para>
///         This type is the whole of the host's job in the #674 arrangement, and
///         its smallness is the point. The core decides what an issue is and how
///         bad it is; the store holds the answer; a renderer draws it. Nothing
///         between those three steps inspects text or counts anything.
///     </para>
///     <para>
///         <b>Why it lives in the app and not in a renderer.</b> The aggregator
///         is core, and a TUI assembly may not reference core. The app holds both
///         halves, so the app is where the seam belongs.
///     </para>
///     <para>
///         <b>Threading.</b> <see cref="DiagnosticsAggregator" /> raises
///         <c>Changed</c> on whichever thread ran the tool, or on a language
///         server's reader loop. <see cref="UiStore.Dispatch" /> is a lock-free
///         CAS over an immutable snapshot, so dispatching from here needs no
///         marshalling onto the frame loop and cannot tear a half-updated
///         snapshot into the state.
///     </para>
///     <para>
///         <b>Seeding.</b> Anything classified before the REPL came up is
///         already in the aggregator, so the current snapshot is pushed once at
///         construction. Subscribing alone would open on an empty panel and wait
///         for the next tool call to notice the truth.
///     </para>
/// </remarks>
internal sealed class DiagnosticsSync : IDisposable
{
    private readonly UiStore _store;
    private readonly DiagnosticsAggregator? _aggregator;
    private readonly ILogger _logger;
    private bool _disposed;

    /// <summary>Subscribe to <paramref name="aggregator" /> and seed the store.</summary>
    /// <param name="store">The REPL store to push snapshots into.</param>
    /// <param name="aggregator">
    ///     The core's aggregator. Null in a host that did not register one — the
    ///     sync then does nothing rather than failing, and the panel reads an
    ///     empty snapshot, which is honest: nothing was reported.
    /// </param>
    /// <param name="logger">Sink for dispatch failures.</param>
    public DiagnosticsSync(UiStore store, DiagnosticsAggregator? aggregator, ILogger logger)
    {
        _store = store;
        _logger = logger;

        if (aggregator is null)
        {
            return;
        }

        _aggregator = aggregator;
        aggregator.Changed += OnChanged;
        Push(aggregator.Snapshot);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_aggregator is not null)
        {
            _aggregator.Changed -= OnChanged;
        }
    }

    private void OnChanged(object? sender, DiagnosticsSnapshotEventArgs args) => Push(args.Snapshot);

    private void Push(IReadOnlyList<DiagnosticIssue> snapshot)
    {
        // Dispatch runs a reducer; a reducer that throws is a bug, but this runs
        // on the tool's thread and a swallowed exception there is how a snapshot
        // silently stops arriving. Log it and let the next change try again.
        try
        {
            _ = _store.Dispatch(new ChatAppMsg.SyncDiagnostics([.. snapshot]));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Diagnostics: dispatching the snapshot failed");
        }
    }
}
