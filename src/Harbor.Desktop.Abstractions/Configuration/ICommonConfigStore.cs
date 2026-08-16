// ICommonConfigStore.cs — repository contract for the shared CommonConfig.
//
// Mirrors IAppConfigStore<T> but is NOT generic — there is exactly one
// CommonConfig type, shared by every Harbor app. The store reads/writes
// ~/.harbor/config.json atomically and is thread-safe.

using CSharpFunctionalExtensions;
namespace Harbor.Desktop.Abstractions.Configuration;
/// <summary>
///     Repository contract for the shared <see cref="CommonConfig" />. Every
///     Harbor app (CLI, Avalonia, WPF, MAUI, Blazor) registers a single
///     <see cref="ICommonConfigStore" /> in its DI container — they all share
///     the same on-disk file (<c>~/.harbor/config.json</c>).
/// </summary>
/// <remarks>
///     <para>
///         <b>Thread safety:</b> implementations MUST be thread-safe. The
///         default <see cref="JsonCommonConfigStore" /> uses a
///         <see cref="SemaphoreSlim" /> to serialise Load/Save/Update against
///         concurrent callers.
///     </para>
///     <para>
///         <b>Result returns:</b> Load/Save/Update return
///         <see cref="Result{T}" /> (from <c>CSharpFunctionalExtensions</c>) —
///         failures (missing file, deserialisation error, IO error) are
///         surfaced as <see cref="Result.IsFailure" /> with an error message,
///         never as thrown exceptions. Unexpected bugs (null args) may still
///         throw.
///     </para>
/// </remarks>
public interface ICommonConfigStore
{
    /// <summary>
    ///     Load the current shared configuration. When the config file is
    ///     absent or unreadable, returns the default <see cref="CommonConfig" />
    ///     instance — never throws for expected IO failures.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    ///     The loaded config on success; the default config on missing-file;
    ///     an error on IO/deserialise failure.
    /// </returns>
    public Task<Result<CommonConfig>> LoadAsync(CancellationToken ct = default);

    /// <summary>
    ///     Persist the supplied config atomically (temp file + rename).
    /// </summary>
    /// <param name="config">The config snapshot to persist.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success, or failure with an error message.</returns>
    public Task<Result> SaveAsync(CommonConfig config, CancellationToken ct = default);

    /// <summary>
    ///     Load → mutate → save in one atomic operation. The supplied
    ///     <paramref name="updater" /> receives the current config and returns
    ///     the modified copy (typically via a <c>with</c> expression).
    /// </summary>
    /// <param name="updater">Pure function mapping the current config to the new one.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success, or failure with an error message.</returns>
    public Task<Result> UpdateAsync(Func<CommonConfig, CommonConfig> updater, CancellationToken ct = default);
}
