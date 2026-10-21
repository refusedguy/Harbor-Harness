using System.Collections.Frozen;
using CSharpFunctionalExtensions;
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

        // #581: the id list in the error text is derived from the registry the
        // resolution actually used. It used to be a hand-written string constant
        // next to the factory array that no test compared to it, so registering a
        // backend without editing the string produced a message that lied about
        // the available set. `Build` also folds in the plugin-contributed backends
        // (IPluginLoadHost.RegisterSessionStore), so those are named too.
        FrozenDictionary<string, ISessionStoreFactory> registry =
            SessionStoreRegistry.Build(ctx.Registries.SessionStores);
        Maybe<ISessionStoreFactory> factory = SessionStoreRegistry.Resolve(registry, requested);
        if (factory.HasNoValue)
        {
            throw new ArgumentException(
                $"Unknown HARBOR_STORAGE: '{requested}'. Expected one of: {string.Join(", ", registry.Keys.Order(StringComparer.Ordinal))}.");
        }

        StorageModuleLog.StorageBackend(ctx.Logger, factory.Value.BackendId);

        services.AddSingleton<ISessionStore>(sp => factory.Value.Create(sp, sessionsDir, sqlitePath));
        // Session import/export works over ANY registered backend: the porter reads via
        // ISessionStore and encodes through the shared JSONL message codec (V4-slice).
        services.AddSingleton<ISessionPorter, JsonlSessionPorter>();
        // #1750: the session ARCHIVE — one zip holding the canonical log, the
        // attachments and the manifest. It is given the credential values the
        // process holds for the same reason the keyless tier is: the archive
        // is the artifact that LEAVES the machine, so it must not carry a
        // credential even when the log on disk was written in the local tier.
        services.AddSingleton(sp => new SessionArchiveWriter(
            sp.GetRequiredService<ILogger<SessionArchiveWriter>>(),
            SessionLogEnvironment.ReadSecretValues()));
        return services;
    }
}

/// <summary>
///     SG4: BCL <c>[LoggerMessage]</c> delegates for <see cref="StorageModule" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class StorageModuleLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Storage backend: {Storage}")]
    public static partial void StorageBackend(ILogger logger, string storage);
}
