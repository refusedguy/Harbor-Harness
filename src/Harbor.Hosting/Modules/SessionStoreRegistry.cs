using System.Collections.Frozen;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harbor.Hosting;

// Issue #175 (OCP — string switch instead of Strategy): session-store
// construction moved out of StorageModule into one strategy per backend.
// The silent `_ => jsonl` fallback is gone: unknown ids fail fast with an
// ArgumentException naming the id and the known backends.

/// <summary>
///     Construction strategy for one session-store backend id.
/// </summary>
internal interface ISessionStoreFactory
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
///     Lookup registry over the <see cref="ISessionStoreFactory"/>
///     strategies. Unknown ids are an explicit failure (no silent
///     default): a <c>HARBOR_STORAGE</c> typo used to boot on jsonl and
///     silently split the session history.
/// </summary>
internal static class SessionStoreRegistry
{
#if HARBOR_WITH_ALL_PROVIDERS
    /// <summary>Known backend ids, in registration order — kept next to the factory set so error text cannot drift.</summary>
    internal const string KnownIds = "memory, jsonl, sqlite";
#else
    /// <summary>Known backend ids, in registration order — kept next to the factory set so error text cannot drift.</summary>
    internal const string KnownIds = "memory, jsonl";
#endif

    /// <summary>Build the id → factory index for the compiled-in backends.</summary>
    internal static FrozenDictionary<string, ISessionStoreFactory> Build()
    {
        ISessionStoreFactory[] factories =
        [
            new MemorySessionStoreFactory(),
            new JsonlSessionStoreFactory(),
#if HARBOR_WITH_ALL_PROVIDERS
            new SqliteSessionStoreFactory(),
#endif
        ];
        return factories.ToFrozenDictionary(f => f.BackendId, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Resolve a raw <c>HARBOR_STORAGE</c>/default value to its factory
    ///     (trim + case-insensitive). Returns <c>false</c> for unknown ids —
    ///     the caller fails fast instead of silently defaulting.
    /// </summary>
    internal static bool TryResolve(
        FrozenDictionary<string, ISessionStoreFactory> registry,
        string rawId,
        out ISessionStoreFactory? factory)
    {
        return registry.TryGetValue(rawId.Trim(), out factory);
    }
}
