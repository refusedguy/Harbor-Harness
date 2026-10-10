#if HARBOR_WITH_PLUGINS
using Harbor.Plugins.Abstractions;

namespace Harbor.Hosting;

/// <summary>
///     Handle for the startup plugin load (#1055, slice 3).
///     The CLI never compiles CS plugins in-process — the default route is the
///     out-of-process host over MCP — so the startup load is always empty:
///     <see cref="Completion" /> is an already-completed empty list and
///     <see cref="Attach" /> fires immediately with it. The handle stays so
///     late consumers (the <c>/plugins</c> panel seed) keep one observation
///     point, and so the graceful-absence matrix keeps its shape.
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
    ///     Completes with the startup-bound plugins — always empty
    ///     out-of-process. Acceptance hook for tests.
    /// </summary>
    public Task<IReadOnlyList<LoadedPlugin>> Completion => _completion;

    /// <summary>
    ///     Absence handle (#1055, slice 1): no plugin scripts anywhere, so there
    ///     is nothing to load. <see cref="Completion" /> is an already-completed
    ///     empty list and <see cref="Attach" /> fires immediately with it.
    ///     Slice 3 returns this same handle on every path: in-process loading
    ///     is removed, so "nothing to load in-process" is unconditional.
    /// </summary>
    public static StartupPluginLoad Empty()
    {
        var sink = new StartupPluginLoad();
        sink.Complete([]);
        return sink;
    }

    /// <summary>
    ///     Report startup-bound plugins to a late consumer (the
    ///     <c>/plugins</c> panel seed). Fires immediately when the load already
    ///     finished, otherwise when it completes. Thread-safe.
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
}
#endif
