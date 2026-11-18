using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Harbor.Tui.CellForge.CodeGen.DevReload;

/// <summary>
///     DEV-ONLY file watcher for <c>.harbor-ui</c> markup (issue #2437). It
///     polls the file, debounces a burst of edits, then asks the shared dev
///     interpreter for one atomic reload. Every observation and decision goes
///     to the <see cref="DevLogChannel"/>; the watcher never writes to the
///     console, an event bus or a renderer, so it cannot paint into the cell
///     grid or disturb the last valid frame.
/// </summary>
/// <remarks>
///     This is the logging-slice watcher: it owns no host state and performs no
///     template diff of its own. The poll interval and debounce match the
///     #2434 pipeline (250 ms polling, 200 ms debounce). Passing
///     <see cref="TimeSpan.Zero"/> as the poll interval disables the timer so
///     a test can drive <see cref="Poll"/> deterministically.
/// </remarks>
public sealed class DslDevFileWatcher : IDisposable
{
    /// <summary>Polling interval, from the #2434 pipeline.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Debounce window, from the #2434 pipeline.</summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(200);

    private readonly string _path;
    private readonly DevReloadInterpreter _interpreter;
    private readonly DevLogChannel _log;
    private readonly TimeSpan _debounce;
    private readonly Timer? _timer;
    private readonly object _gate = new();

    private DateTime _lastStamp;
    private DateTime? _pendingStamp;
    private DateTime _pendingSince;

    /// <summary>Creates a watcher and starts its poll timer.</summary>
    /// <param name="path">The .harbor-ui file to poll.</param>
    /// <param name="interpreter">The dev interpreter owning the live tree.</param>
    /// <param name="log">The channel that receives every observation.</param>
    /// <param name="pollInterval">Timer period; zero disables the timer.</param>
    /// <param name="debounce">How long a change must stay stable before reload.</param>
    public DslDevFileWatcher(
        string path,
        DevReloadInterpreter interpreter,
        DevLogChannel log,
        TimeSpan? pollInterval = null,
        TimeSpan? debounce = null)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _interpreter = interpreter ?? throw new ArgumentNullException(nameof(interpreter));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _debounce = debounce ?? Debounce;
        _lastStamp = Stamp();

        var interval = pollInterval ?? PollInterval;
        _timer = interval > TimeSpan.Zero
            ? new Timer(_ => Poll(), null, interval, interval)
            : null;
    }

    /// <summary>The last file stamp the watcher accepted, for deterministic tests.</summary>
    public DateTime LastSeenStamp
    {
        get { lock (_gate) return _lastStamp; }
    }

    private DateTime Stamp() => File.Exists(_path) ? File.GetLastWriteTimeUtc(_path) : DateTime.MinValue;

    /// <summary>One poll cycle. Public so tests need no wall-clock timer.</summary>
    public void Poll()
    {
        DateTime stamp = Stamp();
        lock (_gate)
        {
            if (_pendingStamp is { } pending)
            {
                if (stamp != pending)
                {
                    // A second write restarts the debounce window.
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
            var result = _interpreter.Reload(File.ReadAllText(_path));
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
}
