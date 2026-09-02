using Harbor.Abstractions.Sessions;
using Harbor.Storage.Jsonl;
using Harbor.Storage.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harbor.Hosting;

internal static class StorageModule
{
    /// <summary>
    ///     Session-store switch. Default backend comes from the preset
    ///     (jsonl for CLI, memory for desktop), overridden by CommonConfig
    ///     (StorageBackend) and the HARBOR_STORAGE env var.
    /// </summary>
    internal static IServiceCollection AddHarborStorage(
        this IServiceCollection services,
        HarborCompositionContext ctx)
    {
        string sessionsDir = Path.Combine(ctx.Options.HarborDir, "sessions");
        string sqlitePath = Path.Combine(ctx.Options.HarborDir, "sessions.db");

        string defaultStorage = string.IsNullOrEmpty(ctx.Common.StorageBackend)
            ? ctx.Options.DefaultStorageBackend
            : ctx.Common.StorageBackend;
        string storage = Environment.GetEnvironmentVariable("HARBOR_STORAGE") ?? defaultStorage;
        ctx.Logger.LogInformation("Storage backend: {Storage}", storage);

        services.AddSingleton<ISessionStore>(sp => storage.ToLowerInvariant() switch
        {
            "memory" => new MemorySessionStore(),
#if HARBOR_WITH_ALL_PROVIDERS
            "sqlite" => new Harbor.Storage.Sqlite.SqliteSessionStore(sqlitePath, sp.GetRequiredService<ILogger<Harbor.Storage.Sqlite.SqliteSessionStore>>()),
#endif
            _ => new JsonlSessionStore(sessionsDir, sp.GetRequiredService<ILogger<JsonlSessionStore>>())
        });
        // Session import/export works over ANY registered backend: the porter reads via
        // ISessionStore and encodes through the shared JSONL message codec (V4-slice).
        services.AddSingleton<ISessionPorter, JsonlSessionPorter>();
        return services;
    }
}
