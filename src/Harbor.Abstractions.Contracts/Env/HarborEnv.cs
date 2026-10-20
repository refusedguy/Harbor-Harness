// Harbor.Abstractions.Contracts — root namespace file: Harbor.Abstractions.Env.
// Single source of truth for HARBOR_* process-environment variable names (#1562).
// The literal spelling lives here and only here; every read site references a
// constant (or the Get/TryGet helpers), so a typo fails to compile instead of
// silently misconfiguring a run.
namespace Harbor.Abstractions.Env;

/// <summary>
///     Well-known <c>HARBOR_*</c> process-environment variable names, plus
///     trivial <see cref="Get" />/<see cref="TryGet" /> wrappers around
///     <see cref="Environment.GetEnvironmentVariable(string)" />.
/// </summary>
/// <remarks>
///     The class owns <em>names</em> only. Default values, parsers and
///     fallback semantics stay at the read sites, exactly where they were
///     before this class existed — replacing a spelling with a constant must
///     not change behavior.
/// </remarks>
public static class HarborEnv
{
    // --- Home / paths -----------------------------------------------------

    /// <summary>Overrides <c>~/.harbor</c> as the Harbor home directory.</summary>
    public const string Home = "HARBOR_HOME";

    /// <summary>Directory the theme picker scans for extra <c>*.json</c> palettes.</summary>
    public const string ThemesDir = "HARBOR_THEMES_DIR";

    /// <summary>Theme JSON file that takes precedence over the stored theme path.</summary>
    public const string ThemeFile = "HARBOR_THEME_FILE";

    /// <summary>
    ///     Project directory for the out-of-process CS-plugin host
    ///     (overrides the default <c>plugins-cs</c> location).
    /// </summary>
    public const string ProjectPlugins = "HARBOR_PROJECT_PLUGINS";

    // --- Core wiring: storage / mode / model / log level ------------------

    /// <summary>Session store id: <c>memory</c>, <c>jsonl</c>, <c>sqlite</c>, …</summary>
    public const string Storage = "HARBOR_STORAGE";

    /// <summary>
    ///     The tier a session log is written in: <c>local</c> (default) or
    ///     <c>keyless</c> — the tier that scrubs provider credentials out of
    ///     the log so it can be committed and replayed without them (#1740).
    /// </summary>
    public const string SessionTier = "HARBOR_SESSION_TIER";

    /// <summary>Host composition mode: <c>inprocess</c>, <c>ipc-server</c>, <c>ipc-client</c>.</summary>
    public const string Mode = "HARBOR_MODE";

    /// <summary>Override of the default provider/model id (<c>provider/model</c>).</summary>
    public const string Model = "HARBOR_MODEL";

    /// <summary>Startup renderer id (<c>cellforge</c>, <c>plain</c>, …); legacy alias <c>consoleex</c>.</summary>
    public const string Tui = "HARBOR_TUI";

    /// <summary>Minimum console log level parsed by <c>Enum.TryParse</c> (e.g. <c>Warning</c>).</summary>
    public const string LogLevel = "HARBOR_LOGLEVEL";

    /// <summary>Skips the first-run onboarding wizard when set (any non-empty value).</summary>
    public const string SkipOnboarding = "HARBOR_SKIP_ONBOARDING";

    // --- Telemetry ----------------------------------------------------------

    /// <summary>
    ///     Turns the visible telemetry sink on: <c>none</c> (default, no
    ///     output), <c>console</c> or <c>file</c>.
    /// </summary>
    public const string TelemetryExport = "HARBOR_TELEMETRY_EXPORT";

    /// <summary>File the <c>file</c> telemetry sink appends to.</summary>
    public const string TelemetryExportPath = "HARBOR_TELEMETRY_EXPORT_PATH";

    /// <summary>Cap on one exported telemetry record, in bytes.</summary>
    public const string TelemetryExportMaxRecordBytes = "HARBOR_TELEMETRY_MAX_RECORD_BYTES";

    /// <summary>Cap on the bytes the telemetry sink holds before it starts dropping.</summary>
    public const string TelemetryExportMaxBufferBytes = "HARBOR_TELEMETRY_MAX_BUFFER_BYTES";

    // --- IPC / daemon ------------------------------------------------------

    /// <summary>Named pipe / UDS socket name for the daemon connection (default <c>harbor-ipc</c>).</summary>
    public const string IpcPipe = "HARBOR_IPC_PIPE";

    /// <summary>Loopback scope for the optional TCP listener (<c>loopback</c> | <c>tailscale0</c> | <c>all</c>).</summary>
    public const string Listen = "HARBOR_LISTEN";

    /// <summary>TCP port for the optional networked listener (default 48710).</summary>
    public const string Port = "HARBOR_PORT";

    // --- MCP ---------------------------------------------------------------

    /// <summary>Explicit <c>mcp.json</c> path the login/config flows read instead of the default one.</summary>
    public const string McpConfig = "HARBOR_MCP_CONFIG";

    /// <summary>Bearer token handed to remote MCP servers, bypassing the OAuth cache.</summary>
    public const string McpOAuthToken = "HARBOR_MCP_OAUTH_TOKEN";

    // --- TUI / desktop shell ----------------------------------------------

    /// <summary>Desktop shell id for the Avalonia app: <c>classic</c> | <c>orca</c>.</summary>
    public const string Shell = "HARBOR_SHELL";

    /// <summary>Startup theme mode for the Avalonia app: <c>dark</c> | <c>light</c> | <c>system</c>.</summary>
    public const string Theme = "HARBOR_THEME";

    /// <summary>Locale override used by <c>UiStrings</c>.</summary>
    public const string Locale = "HARBOR_LOCALE";

    /// <summary>Renderer swap override at runtime (renderer form applies it mid-session).</summary>
    public const string TuiRuntimeSwap = "HARBOR_TUI_RUNTIME_SWAP";

    /// <summary>Opt-out of built-in panels for the (contrib) SpectreTUI renderer.</summary>
    public const string TuiNoBuiltinPanels = "HARBOR_TUI_NO_BUILTIN_PANELS";

    /// <summary>Windows-only opt-out of the CellForge renderer during renderer selection.</summary>
    public const string CellForgeWindows = "HARBOR_CELLFORGE_WINDOWS";

    /// <summary>Opts the first-run wizard back to the legacy non-CellForge flow.</summary>
    public const string LegacySetup = "HARBOR_LEGACY_SETUP";

    /// <summary>Suppresses the interactive CellForge approval prompt (<c>--no-approver</c> marker).</summary>
    public const string NoApprover = "HARBOR_NO_APPROVER";

    /// <summary>Suppresses automatic AI session titling in the REPL.</summary>
    public const string NoAutotitle = "HARBOR_NO_AUTOTITLE";

    // --- Demo / experiment flags -------------------------------------------

    /// <summary>Marks the process as a scripted <c>harbor demo</c> run (read by the REPL and E2E).</summary>
    public const string Demo = "HARBOR_DEMO";

    /// <summary>Renderer id the <c>harbor demo</c> command was asked to use.</summary>
    public const string DemoTui = "HARBOR_DEMO_TUI";

    /// <summary>Delay between scripted demo steps, parsed as milliseconds.</summary>
    public const string DemoStepMs = "HARBOR_DEMO_STEP_MS";

    /// <summary>Enables the DiffPlex-based diff provider in the UI framework.</summary>
    public const string Diffplex = "HARBOR_DIFFPLEX";

    /// <summary>Mascot rendering level (<c>off</c>, …) used by the CellForge chat widgets.</summary>
    public const string Mascot = "HARBOR_MASCOT";

    /// <summary>Mascot expression mode (blinks, ambient state) for CellForge chat widgets.</summary>
    public const string MascotMode = "HARBOR_MASCOT_MODE";

    /// <summary>Overrides inline-image protocol detection in the CellForge engine capabilities probe.</summary>
    public const string InlineImage = "HARBOR_INLINE_IMAGE";

    /// <summary>Overrides the OSC 777 desktop-notification protocol probe.</summary>
    public const string Osc777 = "HARBOR_OSC777";

    /// <summary>Opts in to the experimental Skill freshness panel.</summary>
    public const string SkillFreshness = "HARBOR_SKILL_FRESHNESS";

    /// <summary>Registers only the Ollama HTTP client when not <c>true</c>/<c>1</c> (provider parity flag).</summary>
    public const string WithAllProviders = "HARBOR_WITH_ALL_PROVIDERS";

    /// <summary>
    ///     Reads a HARBOR_* environment variable by its canonical name from
    ///     <see cref="HarborEnv" />. Same contract as
    ///     <see cref="Environment.GetEnvironmentVariable(string)" />: the
    ///     process environment only, <see langword="null" /> when unset.
    /// </summary>
    /// <param name="variable">Variable name — pass a <see cref="HarborEnv" /> constant.</param>
    /// <returns>Current value, or <see langword="null" /> when the variable is unset.</returns>
    public static string? Get(string variable) => Environment.GetEnvironmentVariable(variable);

    /// <summary>
    ///     Reads a HARBOR_* environment variable and reports whether it was
    ///     set, so call sites can distinguish an unset variable from an
    ///     empty-string value.
    /// </summary>
    /// <param name="variable">Variable name — pass a <see cref="HarborEnv" /> constant.</param>
    /// <param name="value">Receives the current value; <see langword="null" /> when unset.</param>
    /// <returns><see langword="true" /> when the variable is set in the process environment.</returns>
    public static bool TryGet(string variable, out string? value)
    {
        value = Environment.GetEnvironmentVariable(variable);
        return value is not null;
    }
}
