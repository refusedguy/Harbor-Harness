// IAppConfigStore.cs — repository contract for per-app configuration.
//
// Mirrors the IConfigStore shape used by the legacy HarborConfig (see
// Harbor.Application/Configuration/HarborConfig.cs) but is generic over the
// per-app config record type. The generic parameter lets each app declare a
// dedicated config record (CliConfig, AvaloniaConfig, …) while sharing the
// same load/save/update API surface.

using CSharpFunctionalExtensions;

namespace Harbor.Desktop.Abstractions.Configuration;

/// <summary>
///     Repository contract for per-app configuration. Each app registers a
///     single <see cref="IAppConfigStore{T}"/> in its DI container where
///     <c>T</c> is the app's own <see cref="AppConfigBase"/>-derived record.
/// </summary>
/// <typeparam name="T">
///     The app-specific config record type (e.g. <c>CliConfig</c>,
///     <c>AvaloniaConfig</c>). Must be a <c>sealed record</c> deriving from
///     <see cref="AppConfigBase"/>.
/// </typeparam>
/// <remarks>
///     <para>
///         Implementations MUST be thread-safe. The default
///         <see cref="JsonAppConfigStore{T}"/> uses a <see cref="SemaphoreSlim"/>
///         to serialize Load/Save/Update against concurrent callers.
///     </para>
///     <para>
///         Load/Save/Update return <see cref="Result{T}"/> (from
///         <c>CSharpFunctionalExtensions</c>) — failures (missing file,
///         deserialization error, IO error) are surfaced as
///         <see cref="Result.IsFailure"/> with an error message, never as
///         thrown exceptions. Unexpected bugs (null args) may still throw.
///     </para>
/// </remarks>
public interface IAppConfigStore<T> where T : AppConfigBase
{
    /// <summary>
    ///     Load the current configuration. When the config file is absent or
    ///     unreadable, returns the default instance supplied at construction
    ///     time — never throws for expected IO failures.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The loaded config on success; the default config on missing-file; an error on IO/deserialize failure.</returns>
    Task<Result<T>> LoadAsync(CancellationToken ct = default);

    /// <summary>
    ///     Persist the supplied config atomically (temp file + rename).
    /// </summary>
    /// <param name="config">The config snapshot to persist.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success, or failure with an error message.</returns>
    Task<Result> SaveAsync(T config, CancellationToken ct = default);

    /// <summary>
    ///     Load → mutate → save in one atomic operation. The supplied
    ///     <paramref name="updater"/> receives the current config and returns
    ///     the modified copy (typically via a <c>with</c> expression).
    /// </summary>
    /// <param name="updater">Pure function mapping the current config to the new one.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success, or failure with an error message.</returns>
    Task<Result> UpdateAsync(Func<T, T> updater, CancellationToken ct = default);
}
