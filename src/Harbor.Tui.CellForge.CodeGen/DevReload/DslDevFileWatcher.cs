using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Harbor.Tui.CellForge.CodeGen.DevReload;

/// <summary>File access the dev watcher needs, supplied by the dev CLI host.</summary>
/// <param name="StampUtc">Last write time of the markup file, in UTC.</param>
/// <param name="ReadText">Current text of the markup file.</param>
public sealed record DevMarkupFileSource(Func<DateTime> StampUtc, Func<string> ReadText);

/// <summary>
///     DEV-ONLY file watcher for <c>.harbor-ui</c> markup (issue #2437, graph
///     support in #3083). It polls every file in the import graph, debounces a
///     burst as one, then asks the shared dev interpreter for one atomic
///     reload of the expanded tree. Every observation and decision goes to
///     the <see cref="DevLogChannel"/>; the watcher never writes to the
///     console, an event bus or a renderer, so it cannot paint into the cell
///     grid or disturb the last valid frame.
/// </summary>
/// <remarks>
///     The original single-file constructor is kept: it wraps one source in a
///     one-entry graph and keeps the same 250 ms polling / 200 ms debounce
///     behaviour. Passing <see cref="TimeSpan.Zero"/> as the poll interval
///     disables the timer so a test can drive <see cref="Poll"/>
///     deterministically.
/// </remarks>
public sealed class DslDevFileWatcher : IDisposable
{
    /// <summary>Polling interval, from the #2434 pipeline.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Debounce window, from the #2434 pipeline.</summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(200);

    private readonly string _rootPath;
    private readonly IReadOnlyDictionary<string, DevMarkupFileSource> _sources;
    private readonly DevReloadInterpreter _interpreter;
    private readonly DevLogChannel _log;
    private readonly TimeSpan _debounce;
    private readonly Timer? _timer;
    private readonly object _gate = new();

    private DateTime _lastStamp;
    private DateTime? _pendingStamp;
    private DateTime _pendingSince;

    /// <summary>Creates a watcher for one file, preserving the single-file path.</summary>
    /// <param name="file">Stamp and read access to the markup file.</param>
    /// <param name="interpreter">The dev interpreter owning the live tree.</param>
    /// <param name="log">The channel that receives every observation.</param>
    /// <param name="pollInterval">Timer period; zero disables the timer.</param>
    /// <param name="debounce">How long a change must stay stable before reload.</param>
    public DslDevFileWatcher(
        DevMarkupFileSource file,
        DevReloadInterpreter interpreter,
        DevLogChannel log,
        TimeSpan? pollInterval = null,
        TimeSpan? debounce = null)
        : this(
            string.Empty,
            new Dictionary<string, DevMarkupFileSource>(StringComparer.Ordinal) { [string.Empty] = file },
            interpreter,
            log,
            pollInterval,
            debounce)
    {
    }

    /// <summary>Creates a watcher for a whole module graph.</summary>
    /// <param name="rootPath">Path of the screen file that owns the live tree.</param>
    /// <param name="sources">Stamp and read access for every file in the graph.</param>
    /// <param name="interpreter">The dev interpreter owning the live tree.</param>
    /// <param name="log">The channel that receives every observation.</param>
    /// <param name="pollInterval">Timer period; zero disables the timer.</param>
    /// <param name="debounce">How long a change must stay stable before reload.</param>
    public DslDevFileWatcher(
        string rootPath,
        IReadOnlyDictionary<string, DevMarkupFileSource> sources,
        DevReloadInterpreter interpreter,
        DevLogChannel log,
        TimeSpan? pollInterval = null,
        TimeSpan? debounce = null)
    {
        _rootPath = rootPath;
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        _interpreter = interpreter ?? throw new ArgumentNullException(nameof(interpreter));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _debounce = debounce ?? Debounce;
        _lastStamp = AggregateStamp();

        var interval = pollInterval ?? PollInterval;
        _timer = interval > TimeSpan.Zero
            ? new Timer(_ => Poll(), null, interval, interval)
            : null;
    }

    /// <summary>The last aggregate file stamp the watcher accepted, for deterministic tests.</summary>
    public DateTime LastSeenStamp
    {
        get { lock (_gate) return _lastStamp; }
    }

    /// <summary>One poll cycle. Public so tests need no wall-clock timer.</summary>
    public void Poll()
    {
        DateTime stamp = AggregateStamp();
        lock (_gate)
        {
            if (_pendingStamp is { } pending)
            {
                if (stamp != pending)
                {
                    // A second write anywhere in the graph restarts the debounce.
                    _pendingStamp = stamp;
                    _pendingSince = DateTime.UtcNow;
                    return;
                }

                if (DateTime.UtcNow - _pendingSince < _debounce)
                    return;

                _pendingStamp = null;
                _lastStamp = stamp;
            }
            else
            {
                if (stamp == DateTime.MinValue || stamp == _lastStamp)
                    return;

                _pendingStamp = stamp;
                _pendingSince = DateTime.UtcNow;
                _log.Write("watcher", "change detected; debouncing");
                return;
            }
        }

        // Read and interpret outside the lock. The interpreter performs the
        // single atomic tree swap; a rejected step leaves the live tree as it
        // was, so the screen keeps its last valid frame.
        try
        {
            var result = Reload();
            _log.Write("watcher", $"applied {result.Outcome} rev={result.Revision}: {result.Reason}");
        }
        catch (Exception ex)
        {
            _log.Write("watcher", "reload failed: " + ex.Message);
        }
    }

    /// <summary>Runs until the token is cancelled; the timer does the polling.</summary>
    /// <param name="cancellationToken">Cancellation from Ctrl-C or the caller.</param>
    public async Task RunUntilCancelledAsync(CancellationToken cancellationToken)
    {
        var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => stopped.TrySetResult(true));
        await stopped.Task.ConfigureAwait(false);
    }

    /// <summary>Stops the poll timer.</summary>
    public void Dispose() => _timer?.Dispose();

    private DateTime AggregateStamp()
    {
        var stamp = DateTime.MinValue;
        foreach (var source in _sources.Values)
        {
            var candidate = source.StampUtc();
            if (candidate > stamp)
                stamp = candidate;
        }

        return stamp;
    }

    private ReloadResult Reload()
    {
        var texts = new Dictionary<string, string>(_sources.Count, StringComparer.Ordinal);
        foreach (var (path, source) in _sources)
            texts[MarkupPaths.Normalize(path)] = source.ReadText();

        return _interpreter.ReloadFromGraph(texts, _rootPath);
    }
}
