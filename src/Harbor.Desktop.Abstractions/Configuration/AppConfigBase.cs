// AppConfigBase.cs — abstract base for per-app configuration records.
//
// Each Harbor app (CLI, Avalonia, WPF, MAUI, Blazor) owns a dedicated config
// record derived from AppConfigBase. The record is persisted as JSON under
// ~/.harbor/<AppId>.json — never overlapping with another app's file.
//
// IMPORTANT (B2 → C1 refactor): this base used to declare the "common" fields
// (Theme, LogLevel, LastUsedProvider, LastUsedModel, LastUsedAgent,
// RecentSessions) directly. As of task C1 those have been MOVED to
// CommonConfig (~/.harbor/config.json) so they are shared across every app.
// AppConfigBase now contains ONLY path plumbing — every persistent field
// lives either on CommonConfig (shared) or on the derived app record
// (app-specific). See docs/CONFIGURATION.md for the full guide.

namespace Harbor.Desktop.Abstractions.Configuration;

/// <summary>
///     Abstract base for per-app configuration records. Each Harbor app
///     (CLI, Avalonia, WPF, MAUI, Blazor) derives its own <c>sealed record</c>
///     from this base and adds app-specific fields. Persistence is handled by
///     <see cref="IAppConfigStore{T}"/> / <see cref="JsonAppConfigStore{T}"/>.
/// </summary>
/// <remarks>
///     <para>
///         <b>Non-overlapping storage:</b> every derived config declares a
///         unique <see cref="ConfigFileName"/> (e.g. <c>cli.json</c>,
///         <c>avalonia.json</c>) so the five apps never read or write the same
///         file. Shared cross-app state (API keys, default provider/model,
///         storage backend, logging, permissions, plugins, network,
///         compaction) lives in <see cref="CommonConfig"/> at
///         <c>~/.harbor/config.json</c>. Shared theme tokens + session
///         transcripts live in <c>theme.json</c> + <c>sessions/*.jsonl</c>.
///     </para>
///     <para>
///         <b>Immutability:</b> all configs are <c>sealed record</c> types
///         with <c>init</c>-only setters. Mutation is performed by
///         <c>with</c> expressions inside
///         <see cref="IAppConfigStore{T}.UpdateAsync"/> — the store always
///         persists a fresh snapshot.
///     </para>
///     <para>
///         <b>App-specific only:</b> as of task C1, this base no longer
///         declares common fields. Each derived record owns ONLY its own
///         UX preferences (window size, fonts, TUI renderer, listen port, …).
///         If you need a shared field, add it to <see cref="CommonConfig"/>.
///     </para>
/// </remarks>
public abstract record AppConfigBase
{
    /// <summary>
    ///     Stable lowercase identifier for the owning app (<c>"cli"</c>,
    ///     <c>"avalonia"</c>, <c>"wpf"</c>, <c>"maui"</c>, <c>"blazor"</c>).
    ///     Used for diagnostics, telemetry, and as the default config-file
    ///     prefix. Derived records MUST override this with a constant.
    /// </summary>
    public abstract string AppId { get; }

    /// <summary>
    ///     Filename (no directory) of the per-app JSON config file, e.g.
    ///     <c>cli.json</c>. Derived records MUST override this with a constant
    ///     that does NOT collide with any other app's filename.
    /// </summary>
    public abstract string ConfigFileName { get; }

    /// <summary>
    ///     Absolute directory path that holds every Harbor config file.
    ///     Defaults to <c>~/.harbor</c> (i.e. <c>%USERPROFILE%\.harbor</c>
    ///     on Windows, <c>$HOME/.harbor</c> on Linux/macOS). Override only
    ///     for tests.
    /// </summary>
    public virtual string ConfigDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".harbor");

    /// <summary>
    ///     Absolute path to this app's JSON config file:
    ///     <c>~/.harbor/&lt;ConfigFileName&gt;</c>.
    /// </summary>
    public string ConfigFilePath => Path.Combine(ConfigDirectory, ConfigFileName);
}
