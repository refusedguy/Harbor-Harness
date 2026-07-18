// CliConfig.cs — CLI-specific configuration record.
//
// The CLI app's own config. Stored at ~/.harbor/cli.json (NON-overlapping with
// the desktop apps' avalonia.json / wpf.json / maui.json / blazor.json files).
// Common fields (Theme, LogLevel, LastUsed*, RecentSessions) are inherited from
// AppConfigBase; CLI-specific fields are declared here.

using System.Collections.Immutable;
using Harbor.Desktop.Abstractions.Configuration;

namespace Harbor.Cli.Configuration;

/// <summary>
///     Per-app configuration for the Harbor CLI. Stored at
///     <c>~/.harbor/cli.json</c>. Non-overlapping with desktop app configs.
/// </summary>
/// <remarks>
///     <para>
///         <b>Migrating from HarborConfig:</b> the legacy
///         <c>Harbor.Application/Configuration/HarborConfig.cs</c> stored CLI
///         state in <c>~/.harbor/config.json</c> and merged it with auth,
///         provider presets, and compaction settings. CliConfig is narrower:
///         it owns only the per-CLI-app UX preferences (TUI renderer, storage
///         backend, onboarding flags). Auth + provider presets stay in
///         HarborConfig (config.json) for now — see docs/CONFIGURATION.md §Migrating.
///     </para>
/// </remarks>
public sealed record CliConfig : AppConfigBase
{
    /// <inheritdoc />
    public override string AppId => "cli";

    /// <inheritdoc />
    public override string ConfigFileName => "cli.json";

    /// <summary>
    ///     Default TUI renderer: <c>"auto"</c> (probe), <c>"ansi"</c>,
    ///     <c>"plain"</c>, <c>"spectre"</c>, <c>"spectre-tui"</c>,
    ///     <c>"fullscreen"</c>, <c>"terminal-gui"</c>, <c>"termina"</c>,
    ///     <c>"razor"</c>. The <c>HARBOR_TUI</c> env var overrides this at
    ///     startup. Defaults to <c>"auto"</c>.
    /// </summary>
    public string DefaultTuiRenderer { get; init; } = "auto";

    /// <summary>
    ///     Default session storage backend: <c>"jsonl"</c>, <c>"sqlite"</c>,
    ///     <c>"memory"</c>. The <c>HARBOR_STORAGE</c> env var overrides this at
    ///     startup. Defaults to <c>"jsonl"</c>.
    /// </summary>
    public string DefaultStorage { get; init; } = "jsonl";

    /// <summary>
    ///     Whether to run the first-run onboarding wizard on the next launch.
    ///     Set to <c>false</c> after the user completes onboarding. Defaults
    ///     to <c>true</c>.
    /// </summary>
    public bool EnableOnboardingWizard { get; init; } = true;

    /// <summary>
    ///     Whether to register builtin slash commands (<c>/help</c>,
    ///     <c>/clear</c>, <c>/agents</c>, etc.). Defaults to <c>true</c>.
    ///     Disabled in test harnesses that want a bare REPL.
    /// </summary>
    public bool EnableSlashCommands { get; init; } = true;

    /// <summary>
    ///     Disabled builtin tool IDs (e.g. <c>"bash"</c>, <c>"web-fetch"</c>).
    ///     Tools listed here are NOT registered in the ToolRegistry at startup.
    ///     Empty by default — all builtins are enabled.
    /// </summary>
    public ImmutableList<string> DisabledTools { get; init; } = ImmutableList<string>.Empty;
}
