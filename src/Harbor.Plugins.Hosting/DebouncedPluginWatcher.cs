using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
namespace Harbor.Plugins.Hosting;

/// <summary>The kind of filesystem change detected for a plugin source file.</summary>
public enum PluginSourceChangeKind
{
    /// <summary>A new <c>.cs</c> file appeared.</summary>
    Added,

    /// <summary>An existing file was modified in place or replaced.</summary>
    Modified,

    /// <summary>The file was removed.</summary>
    Removed,
}

/// <summary>
///     One detected change to a CS-source plugin file.
/// </summary>
/// <remarks>Named with the <c>EventArgs</c> suffix per convention — this is raised
///     through <see cref="DebouncedPluginWatcher.ChangesReady" />.</remarks>
public sealed class PluginSourceChangeEventArgs : EventArgs
{
    /// <summary>Construct one change report.</summary>
    public PluginSourceChangeEventArgs(string path, PluginSourceChangeKind kind)
    {
        Path = path;
        Kind = kind;
    }

    /// <summary>Absolute path of the affected <c>.cs</c> file.</summary>
    public string Path { get; }

    /// <summary>The most severe outcome of the burst.</summary>
    public PluginSourceChangeKind Kind { get; }
}

/// <summary>
///     Debounced <see cref="FileSystemWatcher" /> over one or more CS-source plugin
///     directories. Editors rarely write a file once (save → atomic replace → metadata
///     touch): raw watcher events for the same path within the debounce window are
///     collapsed into at most one <see cref="ChangesReady" /> callback per burst, where
///     the most severe outcome wins — Modified &gt; Added &gt; Removed — so quick-save
///     bursts stay visible while a removal keeps outranking earlier re-add noise.
/// </summary>
/// <remarks>
///     Debounce correctness relies on generations instead of a shared timer map: every
///     raw event bumps the pending generation and arms a private timer; on wake a timer
///     fires only if it still matches the latest generation, so stale timers degrade to
///     no-ops without any cancellation races. The component knows nothing about
///     compilation or registries; callbacks fire on thread-pool threads.
/// </remarks>
public sealed class DebouncedPluginWatcher : IDisposable
{
    private const int WatcherBufferSizeBytes = 64 * 1024;

    private readonly ConcurrentDictionary<string, PendingChange> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, FileSystemWatcher> _watched = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _debounce;
    private readonly ILogger<DebouncedPluginWatcher>? _logger;
    private int _disposed;

    /// <summary>Raised on a thread-pool thread once per debounced per-file burst.</summary>
    public event EventHandler<PluginSourceChangeEventArgs>? ChangesReady;

    /// <summary>
    ///     Tracks one path mid-burst. Rank always holds the MOST RECENT raw event's
    ///     kind; Generation is bumped per raw event and arms the matching timer.
    /// </summary>
    private readonly record struct PendingChange(byte Rank, long Generation);

    /// <summary>
    ///     Construct and start watching immediately.
    /// </summary>
    /// <param name="directories">Directories to watch (missing ones are skipped with a debug log).</param>
    /// <param name="debounce">Quiet period before raising <see cref="ChangesReady" />.</param>
    /// <param name="logger">Optional diagnostics logger.</param>
    public DebouncedPluginWatcher(
        IEnumerable<string> directories,
        TimeSpan debounce,
        ILogger<DebouncedPluginWatcher>? logger = null)
    {
        _debounce = debounce <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(300) : debounce;
        _logger = logger;

        foreach (string dir in (directories ?? throw new ArgumentNullException(nameof(directories)))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(dir))
            {
                _logger?.LogDebug("Plugin directory {Dir} does not exist — not watched", dir);
                continue;
            }

            var fsw = new FileSystemWatcher(dir, "*.cs")
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = WatcherBufferSizeBytes,
            };
            fsw.Created += (_, e) => OnRawEvent(e.FullPath, PluginSourceChangeKind.Added);
            fsw.Changed += (_, e) => OnRawEvent(e.FullPath, PluginSourceChangeKind.Modified);
            fsw.Renamed += (_, e) =>
            {
                // Moving out removes the old entry from reload scope.
                if (!e.OldFullPath.Equals(e.FullPath, StringComparison.OrdinalIgnoreCase))
                    OnRawEvent(e.OldFullPath, PluginSourceChangeKind.Removed);
                OnRawEvent(e.FullPath, PluginSourceChangeKind.Added);
            };
            fsw.Deleted += (_, e) => OnRawEvent(e.FullPath, PluginSourceChangeKind.Removed);

            try
            {
                fsw.EnableRaisingEvents = true;
                _watched[dir] = fsw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger?.LogDebug(ex, "Cannot watch plugin directory {Dir}", dir);
                fsw.Dispose();
            }
        }
    }

    /// <summary>Directories that are actually being watched.</summary>
    public IReadOnlyCollection<string> WatchedDirectories => [.. _watched.Keys];

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        foreach (var fsw in _watched.Values)
        {
            fsw.EnableRaisingEvents = false;
            fsw.Dispose();
        }

        _watched.Clear();
    }

    private void OnRawEvent(string fullPath, PluginSourceChangeKind kind)
    {
        if (_disposed != 0 || !".cs".Equals(Path.GetExtension(fullPath), StringComparison.OrdinalIgnoreCase))
            return;

        byte incomingRank = MapRank(kind);
        long armedGeneration = -1;
        while (true)
        {
            PendingChange current = _pending.GetOrAdd(fullPath, new PendingChange(incomingRank, 0));
            PendingChange next = new(incomingRank, current.Generation + 1); // last event wins, chronologically
            if (_pending.TryUpdate(fullPath, next, current))
            {
                armedGeneration = next.Generation;
                break;
            }
        }

        // Cold path: one small closure per raw event is the price of race-free timers.
        Timer timer = null!;
        string path = fullPath;
        long generation = armedGeneration;
        DebouncedPluginWatcher owner = this;
        timer = new Timer(
            _ =>
            {
                timer.Dispose();
                owner.FireIfLatestCore(path, generation);
            },
            null,
            _debounce,
            Timeout.InfiniteTimeSpan);
    }

    private void FireIfLatestCore(string fullPath, long generation)
    {
        if (_disposed != 0)
            return;

        // Take ownership atomically only if this generation is STILL the latest — a
        // plain TryRemove could otherwise swallow a burst that arrived between the
        // get-check and the remove, dropping its change forever.
        PendingChange state = default;
        while (true)
        {
            if (_disposed != 0 || !_pending.TryGetValue(fullPath, out state!))
                return;
            if (state.Generation != generation)
                return; // superseded by a newer burst — its timer owns the fire
            if (_pending.TryRemove(new KeyValuePair<string, PendingChange>(fullPath, state)))
                break;
        }

        var handler = ChangesReady;
        if (handler is null)
            return;

        // Filesystem truth outranks raw-event order: inotify (Linux) may deliver a
        // stale Changed event AFTER Deleted for the same path, and "last event wins"
        // would then report Modified for a file that no longer exists. Reload scope
        // keys on existence, so a vanished file is always Removed.
        var kind = UnmapRank(state.Rank);
        if (kind != PluginSourceChangeKind.Added && !File.Exists(fullPath))
            kind = PluginSourceChangeKind.Removed;

        try
        {
            handler(this, new PluginSourceChangeEventArgs(fullPath, kind));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "ChangesReady callback threw for {Path}", fullPath);
        }
    }

    private static byte MapRank(PluginSourceChangeKind kind) => kind switch
    {
        PluginSourceChangeKind.Removed => 0,
        PluginSourceChangeKind.Added => 1,
        _ => 2,
    };

    private static PluginSourceChangeKind UnmapRank(byte rank) => rank switch
    {
        0 => PluginSourceChangeKind.Removed,
        1 => PluginSourceChangeKind.Added,
        _ => PluginSourceChangeKind.Modified,
    };
}
