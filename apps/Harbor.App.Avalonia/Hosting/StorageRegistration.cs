using Harbor.Abstractions.Sessions;
using Harbor.Desktop.Abstractions.Configuration;
using Harbor.Storage.Jsonl;
using Harbor.Storage.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
namespace Harbor.App.Avalonia.Hosting;
/// <summary>
///     Storage backend registration — opt-in via the <c>HARBOR_STORAGE</c>
///     env var or <see cref="CommonConfig.StorageBackend" />. Defaults to
///     <c>memory</c> (ephemeral) for the Avalonia desktop shell when no
///     explicit choice is made.
/// </summary>
internal static class StorageRegistration
{
    /// <summary>
    ///     Register <see cref="ISessionStore" /> based on the configured
    ///     backend (<c>jsonl</c> for file-based persistence,
    ///     <c>memory</c> for ephemeral).
    /// </summary>
    /// <param name="services">The DI container.</param>
    /// <param name="sessionsDir">The sessions directory (used by jsonl backend).</param>
    /// <param name="commonConfig">The loaded <see cref="CommonConfig" /> (read for StorageBackend).</param>
    public static void Register(IServiceCollection services, string sessionsDir, CommonConfig commonConfig)
    {
        // Storage — opt-in via HARBOR_STORAGE env var. The default comes from
        // CommonConfig.StorageBackend (shared across every Harbor app) and
        // falls back to "memory" (ephemeral) for the Avalonia desktop shell.
        string storage = Environment.GetEnvironmentVariable("HARBOR_STORAGE")
                         ?? (string.IsNullOrEmpty(commonConfig.StorageBackend) ? "memory" : commonConfig.StorageBackend);
        services.AddSingleton<ISessionStore>(sp => storage.ToLowerInvariant() switch
        {
            "jsonl" => new JsonlSessionStore(sessionsDir, sp.GetRequiredService<ILogger<JsonlSessionStore>>()),
            _ => new MemorySessionStore()
        });
    }
}
