// CommonConfig.cs — settings SHARED across every Harbor app.
//
// The five Harbor apps (CLI, Avalonia, WPF, MAUI, Blazor) previously each
// duplicated the same set of "common" fields (Theme, LogLevel, LastUsed*,
// RecentSessions) on AppConfigBase. That meant if the user picked a provider
// in the CLI, the desktop apps didn't see it — they had their own copy. This
// record replaces that duplication with a single shared file at
// ~/.harbor/config.json (the legacy HarborConfig path — see §Migrating in
// docs/CONFIGURATION.md).
//
// CommonConfig owns:
//   • API keys (redacted in UI, persisted here)
//   • Default provider / model / agent
//   • Storage backend + path
//   • Logging verbosity + file-logging flags
//   • Permission mode + always-allow / always-deny tool lists
//   • Plugins / scripting enablement
//   • HTTP proxy + timeout + user-agent
//   • Compaction tuning
//
// App-specific settings (window size, fonts, TUI renderer, listen port, …)
// stay on each app's own AppConfigBase-derived record so the user can have a
// different TUI renderer in the CLI vs. a different window size in Avalonia.

namespace Harbor.Desktop.Abstractions.Configuration;
/// <summary>
///     Shared configuration that is common to every Harbor app (CLI, Avalonia,
///     WPF, MAUI, Blazor). Persisted as a single JSON file at
///     <c>~/.harbor/config.json</c> and read by all five apps.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why a shared file:</b> settings like API keys, default provider,
///         and storage backend are app-agnostic — the user expects the same
///         API key to work whether they launch the CLI, the Avalonia GUI, or
///         the Blazor web UI. Centralising them here avoids the duplication
///         problem of the previous per-app layout (where each app's
///         <c>AppConfigBase</c> re-declared the same fields).
///     </para>
///     <para>
///         <b>Immutability:</b> <c>sealed record</c> with <c>init</c>-only
///         setters. Mutation is performed by <c>with</c> expressions inside
///         <see cref="ICommonConfigStore.UpdateAsync" /> — the store always
///         persists a fresh snapshot.
///     </para>
///     <para>
///         <b>Persistence:</b> handled by <see cref="ICommonConfigStore" /> /
///         <see cref="JsonCommonConfigStore" />. Same atomic-write + thread-safe
///         pattern as <see cref="JsonAppConfigStore{T}" /> (temp file + rename,
///         guarded by a <see cref="SemaphoreSlim" />).
///     </para>
/// </remarks>
public sealed record CommonConfig
{
    /// <summary>
    ///     Process-wide snapshot of the Harbor home directory (<c>~/.harbor</c>).
    ///     Resolved lazily at first access; test hosts may pin it via
    ///     <see cref="OverrideHarborHomeForTests" /> BEFORE building the app
    ///     host (A11: per-class isolation — the previous process-wide
    ///     one-shot snapshot froze the FIRST test class's home for every
    ///     later class, cross-wiring their config files).
    /// </summary>
    public static string HarborHome => _harborHomeOverride ?? ComputeDefaultHarborHome();

    private static string? _harborHomeOverride;

    /// <summary>Pin the harbor home for the current test scope. Not for production use.</summary>
    public static void OverrideHarborHomeForTests(string harborDirectory)
        => _harborHomeOverride = harborDirectory;

    private static string ComputeDefaultHarborHome() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } profile
                ? profile
                : Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } homeEnv
                    ? homeEnv
                    : AppContext.BaseDirectory,
            ".harbor");

    /// <summary>
    ///     Schema version of the common config file. Bumped whenever the JSON
    ///     shape changes in a backward-incompatible way. The loader refuses to
    ///     read a file with a newer <c>ConfigVersion</c> and surfaces a
    ///     descriptive error so the user knows to upgrade the app.
    /// </summary>
    public string ConfigVersion { get; init; } = "1";

    /// <summary>
    ///     Whether the first-launch onboarding wizard has been completed.
    ///     Defaults to <c>false</c> — every Harbor app shows the wizard once
    ///     before opening the main window, then flips this to <c>true</c> and
    ///     persists it to <c>~/.harbor/config.json</c>. The user can re-run
    ///     the wizard at any time from Settings (which sets this back to
    ///     <c>false</c> and restarts the app).
    /// </summary>
    public bool OnboardingCompleted { get; init; } = false;

    /// <summary>
    ///     Cross-app UI theme preference: <c>"dark"</c>, <c>"light"</c>, or
    ///     <c>"system"</c> (default). Each app's <c>ThemeService</c> reads this
    ///     on startup AND when Settings changes it — so picking "light" in the
    ///     CLI makes the Avalonia app open in light mode too. Individual apps
    ///     may keep their own per-app override (e.g.
    ///     <c>AvaloniaConfig.Theme</c>), but the shared value is the source of
    ///     truth for "what did the user pick globally".
    /// </summary>
    public string Theme { get; init; } = "system";

    // ── API keys ──────────────────────────────────────────────────────────

    /// <summary>
    ///     API keys per provider ID. Keys are one of:
    ///     <c>anthropic</c>, <c>openai</c>, <c>openrouter</c>, <c>deepseek</c>,
    ///     <c>groq</c>, <c>mistral</c>, <c>xai</c>, <c>together</c>,
    ///     <c>fireworks</c>, <c>cerebras</c>, <c>kilocode</c>.
    ///     UI surfaces should redact the values (show only the last 4 chars).
    /// </summary>
    public ImmutableDictionary<string, string> ApiKeys { get; init; }
        = ImmutableDictionary<string, string>.Empty;

    // ── Default selections ────────────────────────────────────────────────

    /// <summary>
    ///     Default provider ID used on first launch / when the user hasn't
    ///     explicitly picked one in any app. Defaults to <c>"anthropic"</c>.
    /// </summary>
    public string DefaultProvider { get; init; } = "anthropic";

    /// <summary>
    ///     Default full model ID (e.g. <c>"claude-sonnet-4"</c>) used on first
    ///     launch. Defaults to <c>"claude-sonnet-4"</c>.
    /// </summary>
    public string DefaultModel { get; init; } = "claude-sonnet-4";

    /// <summary>
    ///     Default agent mode: <c>"code"</c>, <c>"plan"</c>, or
    ///     <c>"explore"</c>. Defaults to <c>"code"</c>.
    /// </summary>
    public string DefaultAgent { get; init; } = "code";

    // ── Storage ───────────────────────────────────────────────────────────

    /// <summary>
    ///     Session storage backend: <c>"jsonl"</c>, <c>"sqlite"</c>, or
    ///     <c>"memory"</c>. Empty (the default) means "not chosen" — each
    ///     app's composition preset then supplies its own fallback (CLI:
    ///     <c>jsonl</c>, desktop: <c>memory</c>), overridable via env (e.g.
    ///     <c>HARBOR_STORAGE</c>) that wins over both.
    /// </summary>
    public string StorageBackend { get; init; } = "";

    /// <summary>
    ///     Optional override for the session-storage directory. Empty (the
    ///     default) means <c>~/.harbor/sessions/</c>. Set to an absolute path
    ///     to keep session data on a different volume.
    /// </summary>
    public string StoragePath { get; init; } = "";

    // ── Logging ───────────────────────────────────────────────────────────

    /// <summary>
    ///     Log verbosity for the Microsoft.Extensions.Logging pipeline. One of
    ///     <c>"trace"</c>, <c>"debug"</c>, <c>"info"</c>, <c>"warning"</c>,
    ///     <c>"error"</c>. Defaults to <c>"info"</c>. Apps map this to a
    ///     <see cref="Microsoft.Extensions.Logging.LogLevel" /> at startup.
    /// </summary>
    public string LogLevel { get; init; } = "info";

    /// <summary>
    ///     Whether to additionally write logs to a rotating file under
    ///     <c>~/.harbor/logs/</c>. Defaults to <c>true</c>.
    /// </summary>
    public bool EnableFileLogging { get; init; } = true;

    /// <summary>
    ///     Maximum number of rotating log files to retain. Defaults to
    ///     <c>50</c>.
    /// </summary>
    public int MaxLogFiles { get; init; } = 50;

    // ── Permissions ───────────────────────────────────────────────────────

    /// <summary>
    ///     Permission mode for tool execution: <c>"default"</c> (ask for
    ///     sensitive tools), <c>"permissive"</c> (auto-allow all but a
    ///     deny-list), <c>"strict"</c> (ask for every tool). Defaults to
    ///     <c>"default"</c>.
    /// </summary>
    public string PermissionMode { get; init; } = "default";

    /// <summary>
    ///     Tool IDs the user has marked "always allow" — skip the permission
    ///     prompt for these. Typically populated by the user clicking
    ///     "Always allow" in the per-tool permission dialog.
    /// </summary>
    public ImmutableList<string> AlwaysAllowTools { get; init; } = ImmutableList<string>.Empty;

    /// <summary>
    ///     Tool IDs the user has marked "always deny" — never run these even
    ///     if a plugin/script requests them.
    /// </summary>
    public ImmutableList<string> AlwaysDenyTools { get; init; } = ImmutableList<string>.Empty;

    // ── Plugins / scripting ───────────────────────────────────────────────

    /// <summary>
    ///     Whether the CS plugin runtime is enabled at startup. Defaults to
    ///     <c>true</c>. Set to <c>false</c> for sandboxed environments that
    ///     disallow compiling user code.
    /// </summary>
    public bool EnablePlugins { get; init; } = true;

    /// <summary>
    ///     Comma-separated list of extra plugin directories to scan in
    ///     addition to <c>~/.harbor/plugins/</c>. Empty (the default) means
    ///     only the standard plugin root is scanned.
    /// </summary>
    public string PluginDirectories { get; init; } = "";

    /// <summary>
    ///     Whether the Jint/SharpTS scripting subsystem is enabled. Defaults
    ///     to <c>true</c>. Disabling does NOT disable plugins — it only
    ///     disables the in-process <c>script</c> tool that lets the agent
    ///     evaluate user JS/TS.
    /// </summary>
    public bool EnableScripting { get; init; } = true;

    // ── Network ───────────────────────────────────────────────────────────

    /// <summary>
    ///     HTTP proxy URL (e.g. <c>http://proxy.corp:3128</c>) applied to
    ///     every outbound LLM / WebFetch request. Empty (the default) means
    ///     direct connection.
    /// </summary>
    public string HttpProxy { get; init; } = "";

    /// <summary>
    ///     Per-request HTTP timeout in seconds. Defaults to <c>30</c>.
    /// </summary>
    public int HttpTimeoutSeconds { get; init; } = 30;

    /// <summary>
    ///     <c>User-Agent</c> header sent on every outbound HTTP request.
    ///     Defaults to <c>"Harbor/0.7"</c>.
    /// </summary>
    public string UserAgent { get; init; } = "Harbor/0.7";

    // ── Compaction ────────────────────────────────────────────────────────

    /// <summary>
    ///     Number of tokens to reserve below the model's context window
    ///     before triggering compaction. Defaults to <c>16384</c>.
    /// </summary>
    public int CompactionReserveTokens { get; init; } = 16384;

    /// <summary>
    ///     Target token count for the kept tail when compacting. Defaults to
    ///     <c>20000</c>.
    /// </summary>
    public int CompactionKeepRecentTokens { get; init; } = 20000;

    /// <summary>
    ///     Minimum number of recent turns to keep verbatim after compaction.
    ///     Defaults to <c>2</c>.
    /// </summary>
    public int CompactionTailTurns { get; init; } = 2;

    /// <summary>
    ///     Absolute directory path that holds the common config file.
    ///     Defaults to <c>~/.harbor</c> (i.e. <c>%USERPROFILE%\.harbor</c>
    ///     on Windows, <c>$HOME/.harbor</c> on Linux/macOS). Init-only so
    ///     tests can override via <c>new CommonConfig { ConfigDirectory = ... }</c>.
    /// </summary>
    /// <remarks>
    ///     Resolved through <see cref="HarborHome"/> so the HOME lookup happens
    ///     exactly once per process. Calling Environment.GetFolderPath(UserProfile)
    ///     lazily is a trap: on Linux it re-resolves $HOME on every call, and after
    ///     a mid-process HOME change (E2E driver, su, systemd unit) it can return
    ///     the EMPTY string — silently turning '~/.harbor' into a CWD-relative
    ///     '.harbor'. Snapshotted at type-init instead.
    /// </remarks>
    public string ConfigDirectory { get; init; } = HarborHome;

    /// <summary>
    ///     Filename (no directory) of the common JSON config file:
    ///     <c>config.json</c>.
    /// </summary>
    public string ConfigFileName => "config.json";

    /// <summary>
    ///     Absolute path to the common JSON config file:
    ///     <c>~/.harbor/config.json</c>.
    /// </summary>
    public string ConfigFilePath => Path.Combine(ConfigDirectory, ConfigFileName);
}
