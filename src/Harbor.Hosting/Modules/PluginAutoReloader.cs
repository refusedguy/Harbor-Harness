#if HARBOR_WITH_PLUGINS
using Harbor.Plugins.Hosting;
using Microsoft.Extensions.Logging;
namespace Harbor.Hosting;

/// <summary>
///     Glue between <see cref="DebouncedPluginWatcher" /> (filesystem signal) and
///     <see cref="PluginReloadService" /> (live re-registration). One reload at a time:
///     while a pass runs, further change events are folded into the NEXT pass — the
///     event that starts each run reflects the latest burst, and the reload pipeline
///     re-enumerates the current disk state anyway.
/// </summary>
/// <remarks>
///     Lifecycle: constructed by DI on first resolution in the interactive REPL;
///     disposal happens with the host container (<c>using var host</c> entry points),
///     which stops all watchers. Gated by <c>tooling.autoReloadPlugins</c>
///     (default true) — set it to false to require explicit <c>/plugins reload</c>.

/// <summary>See class summary above.</summary>
public sealed class PluginAutoReloader : IDisposable
{
    private const int DebounceMs = 500;

    private readonly PluginReloadService _reload;
    private readonly ILogger<PluginAutoReloader> _logger;
    private readonly DebouncedPluginWatcher? _watcher;
    private int _inFlight;

    /// <summary>
    ///     Start watching both plugin scopes. Pass <paramref name="autoReloadEnabled" />
    ///     = false to stay inert (watchers not created).
    /// </summary>
    public PluginAutoReloader(
        PluginReloadService reload,
        string harborDir,
        bool autoReloadEnabled,
        ILoggerFactory loggerFactory)
    {
        _reload = reload ?? throw new ArgumentNullException(nameof(reload));
        _logger = loggerFactory.CreateLogger<PluginAutoReloader>();

        if (!autoReloadEnabled)
        {
            PluginAutoReloaderLog.AutoReloadDisabled(_logger);
            return;
        }

        string globalPluginsDir = Path.Combine(harborDir, "plugins");
        string projectPluginsDir = Path.Combine(Directory.GetCurrentDirectory(), ".harbor", "plugins");
        _watcher = new DebouncedPluginWatcher(
            [globalPluginsDir, projectPluginsDir],
            TimeSpan.FromMilliseconds(DebounceMs),
            loggerFactory.CreateLogger<DebouncedPluginWatcher>());
        _watcher.ChangesReady += OnChange;

        var watched = string.Join(", ", _watcher.WatchedDirectories);
        PluginAutoReloaderLog.AutoReloadWatching(_logger, watched.Length == 0 ? "(none)" : watched);
    }

    private void OnChange(object? sender, PluginSourceChangeEventArgs e)
    {
        // Fold concurrent bursts: skip while a previous pass is still running. The
        // skipped events are harmless — every pass re-reads disk state wholesale.
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
        {
            PluginAutoReloaderLog.ChangeDuringReload(_logger, e.Path);
            return;
        }

        // RunAsync covers its own failures, so this async start cannot drop errors.
        Task.Run(() => RunAsync(e.Path));
    }

    private async Task RunAsync(string changedPath)
    {
        try
        {
            var summary = await _reload.ReloadAsync().ConfigureAwait(false);
            PluginAutoReloaderLog.AutoReloaded(_logger, changedPath, summary.Loaded);
        }
        catch (OperationCanceledException ocex)
        {
            PluginAutoReloaderLog.AutoReloadCancelled(_logger, ocex, changedPath);
        }
        catch (Exception ex)
        {
            PluginAutoReloaderLog.AutoReloadFailed(_logger, ex, changedPath);
        }
        finally
        {
            Interlocked.Exchange(ref _inFlight, 0);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _watcher?.Dispose();
}

/// <summary>
///     SG4: BCL <c>[LoggerMessage]</c> delegates for <see cref="PluginAutoReloader" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class PluginAutoReloaderLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Plugin auto-reload disabled via tooling.autoReloadPlugins — use /plugins reload")]
    public static partial void AutoReloadDisabled(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Plugin auto-reload watching: {Dirs}")]
    public static partial void AutoReloadWatching(ILogger logger, string dirs);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "Change to {Path} arrived during a running reload pass — will be covered next time")]
    public static partial void ChangeDuringReload(ILogger logger, string path);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Auto-reloaded plugins after {Path}: {Loaded} loaded")]
    public static partial void AutoReloaded(ILogger logger, string path, int loaded);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "Auto-reload cancelled after {Path}")]
    public static partial void AutoReloadCancelled(ILogger logger, Exception ex, string path);

    [LoggerMessage(EventId = 6, Level = LogLevel.Error, Message = "Auto-reload failed after {Path} — keep using /plugins reload manually")]
    public static partial void AutoReloadFailed(ILogger logger, Exception ex, string path);
}
#endif
