// CliConfig.cs — CLI-specific configuration record.
//
// The CLI app's own config. Stored at ~/.harbor/cli.json (NON-overlapping
// with the desktop apps' avalonia.json / wpf.json / maui.json / blazor.json
// files, AND non-overlapping with the shared ~/.harbor/config.json which
// holds CommonConfig).
//
// As of task C1, common fields (Theme, LogLevel, LastUsed*, RecentSessions,
// default storage) live in CommonConfig (~/.harbor/config.json). CliConfig
// owns ONLY CLI-specific UX preferences. The CLI's HostBuilder reads
// CommonConfig.StorageBackend for the storage selection — CliConfig no longer
// carries a duplicate DefaultStorage field.

using System.Collections.Immutable;
using Harbor.Desktop.Abstractions.Configuration;
namespace Harbor.Cli.Configuration;
/// <summary>
///     Per-app configuration for the Harbor CLI. Stored at
///     <c>~/.harbor/cli.json</c>. Non-overlapping with desktop app configs
///     and with the shared <c>~/.harbor/config.json</c>.
/// </summary>
/// <remarks>
///     <para>
///         <b>Shared vs app-specific:</b> fields that the user expects to be
///         the same across every Harbor app (API keys, default provider /
///         model / agent, storage backend, log level, permission mode, plugin
///         enablement, HTTP proxy, compaction tuning) live in
///         <see cref="CommonConfig" />. CliConfig owns ONLY the CLI's UX
///         preferences (TUI renderer, onboarding flag, slash-command enable,
///         input history).
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
    ///     startup. Defaults to <c>"auto"</c> (which resolves to
    ///     <c>"spectre-tui"</c> in the CLI composition root).
    /// </summary>
    public string DefaultTuiRenderer { get; init; } = "auto";

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

    /// <summary>
    ///     Path to the readline-style history file. Empty (the default)
    ///     means <c>~/.harbor/history</c>. Set to an absolute path to keep
    ///     per-project or per-machine history separate.
    /// </summary>
    public string HistoryFile { get; init; } = "";

    /// <summary>
    ///     Maximum number of input-history entries to retain. Older entries
    ///     are evicted FIFO when this cap is hit. Defaults to <c>1000</c>.
    /// </summary>
    public int MaxHistoryEntries { get; init; } = 1000;
}
