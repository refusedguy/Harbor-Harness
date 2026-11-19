using System;
using System.Threading;
using System.Threading.Tasks;

namespace Harbor.Tui.CellForge.CodeGen.DevReload;

/// <summary>
///     Issue #2434: dev-only polling host for one <c>.harbor-ui</c> file. It
///     scans the file stamp on the 250 ms cadence and delegates debounce,
///     compilation and the atomic swap to <see cref="DevReloadPipeline"/>.
///     The watcher owns no tree: disposing it stops polling but leaves the
///     last published snapshot intact on screen.
/// </summary>
/// <remarks>
///     Dev-only by placement, like the pipeline and the interpreter: this
///     assembly is referenced by the CLI only with
///     <c>HarborWithDslDev=true</c>, which an AOT publish switches off.
/// </remarks>
public sealed class DevFileWatcher : IDisposable
{
    private readonly DevReloadPipeline _pipeline;
    private readonly Func<FileStamp> _readStamp;
    private readonly TimeProvider _timeProvider;
    private readonly PeriodicTimer _timer;
    private readonly CancellationTokenSource _stop;
    private readonly Task _loop;
    private readonly Action<ReloadCommitResult>? _onCommit;
    private int _disposed;

    /// <summary>Creates a watcher, publishes the initial tree and starts polling.</summary>
    /// <param name="filePath">The watched <c>.harbor-ui</c> or <c>.cs</c> file.</param>
    /// <param name="pipeline">Pipeline whose source reader targets the same file.</param>
    /// <param name="timeProvider">Clock for ticks and stamps; defaults to the system clock.</param>
    /// <param name="pollInterval">Scan cadence; defaults to 250 ms.</param>
    /// <param name="onCommit">Optional observer for each initialization or reload decision.</param>
    public DevFileWatcher(
        string filePath,
        DevReloadPipeline pipeline,
        TimeProvider? timeProvider = null,
        TimeSpan? pollInterval = null,
        Action<ReloadCommitResult>? onCommit = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(pipeline);
        if (pollInterval is { } interval && interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(pollInterval));

        FilePath = filePath;
        _pipeline = pipeline;
        _readStamp = () => FileStamp.FromFile(filePath);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _timer = new PeriodicTimer(pollInterval ?? DevReloadTiming.PollInterval, _timeProvider);
        _stop = new CancellationTokenSource();
        _onCommit = onCommit;
        InitialResult = Observe(pipeline.Initialize(_readStamp()));
        _loop = Task.Run(LoopAsync, CancellationToken.None);
    }

    /// <summary>The watched source file.</summary>
    public string FilePath { get; }

    /// <summary>The pipeline driven by this watcher.</summary>
    public DevReloadPipeline Pipeline => _pipeline;

    /// <summary>Result of the initial build performed by the constructor.</summary>
    public ReloadCommitResult InitialResult { get; }

    /// <summary>The polling loop task, exposed for diagnostics and shutdown.</summary>
    public Task Loop => _loop;

    private async Task LoopAsync()
    {
        try
        {
            while (await _timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
                Observe(_pipeline.Poll(_readStamp(), _timeProvider.GetUtcNow()));
        }
        catch (OperationCanceledException)
        {
            // Disposal cancels the wait.
        }
        catch (ObjectDisposedException)
        {
            // Disposing the timer can race the cancellation above.
        }
    }

    private ReloadCommitResult Observe(ReloadCommitResult result)
    {
        _onCommit?.Invoke(result);
        return result;
    }

    /// <summary>Stops polling. The published tree is left untouched.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _stop.Cancel();
        _timer.Dispose();
        _stop.Dispose();
    }
}
