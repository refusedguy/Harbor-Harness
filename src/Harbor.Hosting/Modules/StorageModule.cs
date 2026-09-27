using System.Collections.Frozen;
using Harbor.Abstractions.Sessions;
using Harbor.Storage.Jsonl;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harbor.Hosting;

internal static class StorageModule
{
    /// <summary>
    ///     Session-store selection (issue #175: strategy registry —
    ///     <see cref="SessionStoreRegistry"/> — instead of a string switch
    ///     with a silent jsonl fallback). Default backend comes from the preset
    ///     (jsonl for CLI, memory for desktop), overridden by CommonConfig
    ///     (StorageBackend) and the HARBOR_STORAGE env var. Unknown ids fail
    ///     fast at composition time so a typo can no longer silently split
    ///     the session history across two backends.
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
        string envStorage = Environment.GetEnvironmentVariable("HARBOR_STORAGE") ?? string.Empty;
        string requested = string.IsNullOrWhiteSpace(envStorage) ? defaultStorage : envStorage.Trim();

        FrozenDictionary<string, ISessionStoreFactory> registry = SessionStoreRegistry.Build();
        if (!SessionStoreRegistry.TryResolve(registry, requested, out ISessionStoreFactory? factory) || factory is null)
        {
            throw new ArgumentException(
                $"Unknown HARBOR_STORAGE: '{requested}'. Expected one of: {SessionStoreRegistry.KnownIds}.");
        }

        ctx.Logger.LogInformation("Storage backend: {Storage}", factory.BackendId);

        services.AddSingleton<ISessionStore>(sp => factory.Create(sp, sessionsDir, sqlitePath));
        // Session import/export works over ANY registered backend: the porter reads via
        // ISessionStore and encodes through the shared JSONL message codec (V4-slice).
        services.AddSingleton<ISessionPorter, JsonlSessionPorter>();
        return services;
    }
}
