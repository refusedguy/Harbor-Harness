// AppConfigBase.cs — shared base for per-app configuration records.
//
// Each Harbor app (CLI, Avalonia, WPF, MAUI, Blazor) owns a dedicated config
// record derived from AppConfigBase. The record is persisted as JSON under
// ~/.harbor/<AppId>.json — never overlapping with another app's file. Common
// fields (Theme, LogLevel, LastUsed*) live on the base; app-specific fields
// live on the derived record. See docs/CONFIGURATION.md for the full guide.

using System.Collections.Immutable;

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
///         file. Shared cross-app state (theme tokens, session JSONL) lives in
///         separate files (<c>theme.json</c>, <c>sessions/*.jsonl</c>).
///     </para>
///     <para>
///         <b>Immutability:</b> all configs are <c>sealed record</c> types with
///         <c>init</c>-only setters. Mutation is performed by <c>with</c>
///         expressions inside <see cref="IAppConfigStore{T}.UpdateAsync"/> — the
///         store always persists a fresh snapshot.
///     </para>
///     <para>
///         <b>Common fields:</b> <see cref="Theme"/>, <see cref="LogLevel"/>,
///         <see cref="LastUsedProvider"/>, <see cref="LastUsedModel"/>,
///         <see cref="LastUsedAgent"/>, <see cref="RecentSessions"/> — these are
///         shared across every app because the user expects their theme + last
///         provider to follow them regardless of which Harbor GUI they launched.
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
    ///     Absolute directory path that holds every Harbor config file. Defaults
    ///     to <c>~/.harbor</c> (i.e. <c>%USERPROFILE%\.harbor</c> on Windows,
    ///     <c>$HOME/.harbor</c> on Linux/macOS). Override only for tests.
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

    // ── Common fields shared by every Harbor app ──────────────────────────

    /// <summary>
    ///     UI theme preference: <c>"dark"</c>, <c>"light"</c>, or
    ///     <c>"system"</c> (follow OS theme). Apps without a theme subsystem
    ///     (CLI) ignore this field. Defaults to <c>"dark"</c>.
    /// </summary>
    public string Theme { get; init; } = "dark";

    /// <summary>
    ///     Log verbosity for the Microsoft.Extensions.Logging pipeline. One of
    ///     <c>"trace"</c>, <c>"debug"</c>, <c>"info"</c>, <c>"warning"</c>,
    ///     <c>"error"</c>. Apps map this to <see cref="Microsoft.Extensions.Logging.LogLevel"/>
    ///     at startup. Defaults to <c>"info"</c>.
    /// </summary>
    public string LogLevel { get; init; } = "info";

    /// <summary>
    ///     Provider ID last selected by the user in any app (e.g.
    ///     <c>"anthropic"</c>, <c>"openai"</c>, <c>"kilocode"</c>). Empty when
    ///     no provider has ever been selected. Apps use this to pre-select the
    ///     provider dropdown on next launch.
    /// </summary>
    public string LastUsedProvider { get; init; } = "";

    /// <summary>
    ///     Full model ID last selected (e.g. <c>"anthropic/claude-sonnet-4-20250514"</c>).
    ///     Empty when no model has ever been selected.
    /// </summary>
    public string LastUsedModel { get; init; } = "";

    /// <summary>
    ///     Last selected agent mode: <c>"code"</c>, <c>"plan"</c>, or
    ///     <c>"explore"</c>. Defaults to <c>"code"</c>.
    /// </summary>
    public string LastUsedAgent { get; init; } = "code";

    /// <summary>
    ///     Recent session IDs (most-recent-first), capped at a small number
    ///     (typically 10) by the app. Used to populate the "Recent sessions"
    ///     list in the desktop GUIs and the CLI <c>/recent</c> slash command.
    /// </summary>
    public ImmutableList<string> RecentSessions { get; init; } = ImmutableList<string>.Empty;
}
