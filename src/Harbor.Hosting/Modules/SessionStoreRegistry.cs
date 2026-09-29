using System.Collections.Frozen;
using System.Collections.Immutable;
using CSharpFunctionalExtensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harbor.Hosting;

// Issue #175 (OCP — string switch instead of Strategy): session-store
// construction moved out of StorageModule into one strategy per backend.
// The silent `_ => jsonl` fallback is gone: unknown ids fail fast with an
// ArgumentException naming the id and the known backends.
//
// Issue #581 — the seam was sealed. `ISessionStoreFactory` used to be
// `internal`: less accessible than the `ISessionStore` it produces, so the
// extension interface was reachable from exactly one assembly. The registry
// also carried a second hand-written copy of its own key set
// (`internal const string KnownIds`) whose doc claimed "kept next to the
// factory set so error text cannot drift" — adjacency is not enforcement, and
// nothing in `tests/` ever compared the two. Both are gone: the interface and
// the registry are public, and the id list the error message prints is derived
// from `Build()` at the call site. There is no second list left to drift; the
// registry below is the only declaration.

/// <summary>
///     Construction strategy for one session-store backend id.
/// </summary>
public interface ISessionStoreFactory
{
    /// <summary>Canonical backend id (lowercase), as spelled in <c>HARBOR_STORAGE</c>.</summary>
    string BackendId { get; }

    /// <summary>
    ///     Construct the store. Runs inside the <c>ISessionStore</c>
    ///     singleton factory lambda — resolving here is idiomatic MS DI
    ///     (#63), not service location.
    /// </summary>
    ISessionStore Create(IServiceProvider sp, string sessionsDir, string sqlitePath);
}

/// <summary>In-memory store (<c>HARBOR_STORAGE=memory</c>).</summary>
internal sealed class MemorySessionStoreFactory : ISessionStoreFactory
{
    public string BackendId => "memory";

    public ISessionStore Create(IServiceProvider sp, string sessionsDir, string sqlitePath) =>
        new MemorySessionStore();
}

/// <summary>JSONL file store (<c>HARBOR_STORAGE=jsonl</c>).</summary>
internal sealed class JsonlSessionStoreFactory : ISessionStoreFactory
{
    public string BackendId => "jsonl";

    public ISessionStore Create(IServiceProvider sp, string sessionsDir, string sqlitePath) =>
        new JsonlSessionStore(sessionsDir, sp.GetRequiredService<ILogger<JsonlSessionStore>>());
}

#if HARBOR_WITH_ALL_PROVIDERS
/// <summary>SQLite store (<c>HARBOR_STORAGE=sqlite</c>).</summary>
internal sealed class SqliteSessionStoreFactory : ISessionStoreFactory
{
    public string BackendId => "sqlite";

    public ISessionStore Create(IServiceProvider sp, string sessionsDir, string sqlitePath) =>
        new Harbor.Storage.Sqlite.SqliteSessionStore(sqlitePath, sp.GetRequiredService<ILogger<Harbor.Storage.Sqlite.SqliteSessionStore>>());
}
#endif

/// <summary>
///     Wraps a plugin-contributed <c>Func&lt;ISessionStore&gt;</c> (the shape
///     <c>IPluginLoadHost.RegisterSessionStore</c> accepts) as an
///     <see cref="ISessionStoreFactory" />, so a plugin backend and a compiled-in
///     backend are the same kind of thing in the same registry — which is what makes
///     the extension interface reachable rather than decorative (#581).
/// </summary>
public sealed class PluginSessionStoreFactory : ISessionStoreFactory
{
    private readonly Func<ISessionStore> _factory;

    /// <summary>Wrap a plugin store factory under <paramref name="backendId" />.</summary>
    /// <param name="backendId">Backend id the plugin registered it under.</param>
    /// <param name="factory">Constructs the store; invoked lazily by the DI factory lambda.</param>
    public PluginSessionStoreFactory(string backendId, Func<ISessionStore> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backendId);
        BackendId = backendId.Trim().ToLowerInvariant();
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    /// <inheritdoc />
    public string BackendId { get; }

    /// <inheritdoc />
    public ISessionStore Create(IServiceProvider sp, string sessionsDir, string sqlitePath) =>
        _factory();
}

/// <summary>
///     Lookup registry over the <see cref="ISessionStoreFactory"/>
///     strategies. Unknown ids are an explicit failure (no silent
///     default): a <c>HARBOR_STORAGE</c> typo used to boot on jsonl and
///     silently split the session history.
/// </summary>
public static class SessionStoreRegistry
{
    /// <summary>Build the id → factory index for the compiled-in backends.</summary>
    public static FrozenDictionary<string, ISessionStoreFactory> Build() =>
        Build(ImmutableDictionary<string, Func<ISessionStore>>.Empty);

    /// <summary>
    ///     Build the id → factory index, folding in plugin-contributed backends.
    /// </summary>
    /// <param name="pluginStores">
    ///     Backends a plugin registered through
    ///     <c>IPluginLoadHost.RegisterSessionStore</c> (id → store factory). A plugin id
    ///     that collides with a compiled-in id wins, so a plugin can replace a built-in
    ///     backend — that is what the door means, and a collision is loud rather than
    ///     silent. The input is read here and never retained, so the dictionary is
    ///     still built fresh (and lazily wrt store construction: the
    ///     <c>Func&lt;ISessionStore&gt;</c> runs only when the store is resolved).
    /// </param>
    public static FrozenDictionary<string, ISessionStoreFactory> Build(
        IReadOnlyDictionary<string, Func<ISessionStore>> pluginStores)
    {
        List<ISessionStoreFactory> factories =
        [
            new MemorySessionStoreFactory(),
            new JsonlSessionStoreFactory(),
#if HARBOR_WITH_ALL_PROVIDERS
            new SqliteSessionStoreFactory(),
#endif
        ];
        foreach (KeyValuePair<string, Func<ISessionStore>> entry in pluginStores)
        {
            factories.Add(new PluginSessionStoreFactory(entry.Key, entry.Value));
        }

        // Indexer assignment rather than ToFrozenDictionary: a plugin id that collides
        // with a compiled-in one must REPLACE it (that is what the door means), and
        // ToFrozenDictionary would throw on the duplicate key instead.
        var index = new Dictionary<string, ISessionStoreFactory>(StringComparer.OrdinalIgnoreCase);
        foreach (ISessionStoreFactory factory in factories)
        {
            index[factory.BackendId] = factory;
        }

        return index.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Resolve a raw <c>HARBOR_STORAGE</c>/default value to its factory
    ///     (trim + case-insensitive). Unknown ids come back as
    ///     <see cref="Maybe{T}.None" /> — the caller fails fast instead of
    ///     silently defaulting. Absence is a value, so it travels in the
    ///     signature instead of in a nullable out-parameter.
    /// </summary>
    public static Maybe<ISessionStoreFactory> Resolve(
        FrozenDictionary<string, ISessionStoreFactory> registry,
        string rawId)
    {
        return registry.TryFind(rawId.Trim());
    }
}
