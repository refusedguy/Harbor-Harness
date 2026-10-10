#if HARBOR_WITH_PLUGINS
using Harbor.Abstractions.Tools;
using Harbor.Plugins.Abstractions;
using Harbor.Plugins.Hosting;

namespace Harbor.Hosting;

/// <summary>
///     Handle for the background startup plugin load (#1055, slice 2).
///     <see cref="RegistriesModule" /> composes the plugin pipeline (cheap —
///     the Roslyn stack hides behind <c>LazyPluginCompiler</c>), publishes the
///     live backend maps, and returns immediately; compilation + registration
///     run here on a worker thread. A failing/slow plugin never blocks startup:
///     per-plugin failures stay isolated (ContinueOnError, pinned by the
///     composer) and surface as warnings, never exceptions.
/// </summary>
internal sealed class StartupPluginLoad
{
    private readonly object _sync = new();
    private Task<IReadOnlyList<LoadedPlugin>> _completion = Task.FromResult<IReadOnlyList<LoadedPlugin>>([]);
    private Action<IReadOnlyList<LoadedPlugin>>? _late;
    private IReadOnlyList<LoadedPlugin>? _loaded;

    private StartupPluginLoad()
    {
    }

    /// <summary>
    ///     Completes with the startup-bound plugins once the background load
    ///     finished — empty when there were no plugins or the run failed
    ///     (failures are logged, never thrown). Acceptance hook for tests.
    /// </summary>
    public Task<IReadOnlyList<LoadedPlugin>> Completion => _completion;

    /// <summary>
    ///     Absence handle (#1055, slice 1): no plugin scripts anywhere, so there
    ///     is nothing to load. <see cref="Completion" /> is an already-completed
    ///     empty list and <see cref="Attach" /> fires immediately with it.
    /// </summary>
    public static StartupPluginLoad Empty()
    {
        var sink = new StartupPluginLoad();
        sink.Complete([]);
        return sink;
    }

    /// <summary>
    ///     Start the background load over an already-composed pipeline.
    /// </summary>
    /// <param name="runtime">Composed plugin runtime (lazy compiler inside).</param>
    /// <param name="host">Live load host the plugins register into.</param>
    /// <param name="log">Startup logger for the summary lines.</param>
    /// <returns>Handle tracking the background load.</returns>
    public static StartupPluginLoad Start(PluginHost runtime, PluginLoadHost host, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(log);

        var sink = new StartupPluginLoad();
        Task<IReadOnlyList<LoadedPlugin>> load = Task.Run(() => LoadInBackgroundAsync(runtime, host, log, sink));
        sink._completion = load;
        // §FP-003/006: the handle above already observes the task, and the
        // body catches everything it can — this only covers what it cannot
        // (a fault outside the guarded body), per the house idiom (#569).
        TaskFireAndForget.Forget(load, ex => log.LogError(ex, "Startup plugin task faulted"));
        return sink;
    }

    /// <summary>
    ///     Report startup-bound plugins to a late consumer (the
    ///     <c>/plugins</c> panel seed). Fires immediately when the background
    ///     load already finished, otherwise when it completes. Thread-safe.
    /// </summary>
    /// <param name="onLoaded">Consumer of the loaded plugin list.</param>
    public void Attach(Action<IReadOnlyList<LoadedPlugin>> onLoaded)
    {
        ArgumentNullException.ThrowIfNull(onLoaded);
        IReadOnlyList<LoadedPlugin>? now;
        lock (_sync)
        {
            now = _loaded;
            if (now is null)
                _late += onLoaded;
        }

        if (now is not null)
            onLoaded(now);
    }

    private void Complete(IReadOnlyList<LoadedPlugin> loaded)
    {
        Action<IReadOnlyList<LoadedPlugin>>? late;
        lock (_sync)
        {
            _loaded = loaded;
            late = _late;
            _late = null;
        }

        late?.Invoke(loaded);
    }

    private static async Task<IReadOnlyList<LoadedPlugin>> LoadInBackgroundAsync(
        PluginHost runtime,
        PluginLoadHost host,
        ILogger log,
        StartupPluginLoad sink)
    {
        IReadOnlyList<LoadedPlugin> loaded = [];
        try
        {
            var result = await runtime.LoadAllAsync(host).ConfigureAwait(false);
            if (result.IsSuccess) // §4.6-ok: ветка логирования успеха/провала, не конверсия.
            {
                loaded = result.Value;
                log.LogInformation("Loaded {Count} CS plugin(s) in background", loaded.Count);
                foreach (var p in loaded)
                {
                    log.LogInformation("  - {DisplayName} (from cache: {FromCache})", p.DisplayName, p.LoadedFromCache);
                }

                if (!host.SessionStores.IsEmpty)
                {
                    log.LogInformation(
                        "Plugin session-store backends: {Ids}", string.Join(", ", host.SessionStores.Keys));
                }

                if (!host.TuiBackends.IsEmpty)
                {
                    log.LogInformation(
                        "Plugin TUI backends: {Ids}", string.Join(", ", host.TuiBackends.Keys));
                }
            }
            else
            {
                // Run-level failure with ContinueOnError still must not take
                // startup down — warn once and report zero plugins.
                log.LogWarning("Background CS plugin loading failed: {Error}", result.Error);
            }
        }
        catch (Exception ex)
        {
            // Absence/brokenness surface (missing dirs, corrupt trust store, …)
            // is slice 1's matrix to define; the mechanism here is: never throw
            // out of startup, warn once, report zero plugins.
            log.LogWarning(ex, "Background CS plugin loading crashed");
        }

        sink.Complete(loaded);
        return loaded;
    }
}
#endif
