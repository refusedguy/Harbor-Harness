using System.Text.Json;
using Harbor.Ui.Framework.Diagnostics;
namespace Harbor.App.Cli.Hosting;
/// <summary>
///     Resolves the active TUI renderer id from <c>HARBOR_TUI</c> /
///     <c>CliConfig.DefaultTuiRenderer</c>, and classifies it as interactive
///     (takes over the alt-screen buffer — console logging must be suppressed)
///     vs non-interactive (writes to the regular console — logging stays on).
/// </summary>
/// <remarks>
///     <para>
///         <b>Interactive renderers</b> (alt-screen, full-screen): SpectreTUI,
///         Fullscreen, Spectre, Termina, Terminal.Gui, RazorConsole. Any one of
///         these will be corrupted by stray <c>Console.WriteLine</c> from the
///         logger, so the console provider is removed and a
///         <see cref="DiagnosticsPanelLoggerProvider" /> is added instead — log
///         entries surface inside the TUI via F12.
///     </para>
///     <para>
///         <b>Non-interactive renderers</b>: <c>plain</c>, <c>ansi</c>. These
///         write to the regular console, so logging stays where the user can see
///         it inline.
///     </para>
///     <para>
///         <b>One-shot commands</b> (<c>harbor ask &lt;prompt&gt;</c>,
///         <c>harbor providers</c>, …) are always non-interactive regardless of
///         <c>HARBOR_TUI</c> — they print results and exit.
///     </para>
/// </remarks>
internal static class TuiMode
{
    /// <summary>
    ///     TUI renderer ids that take over the alternate screen buffer. Console
    ///     logging must be suppressed while one of these is active.
    /// </summary>
    private static readonly HashSet<string> InteractiveTuis = new(StringComparer.OrdinalIgnoreCase)
    {
        "spectre-tui", "spectre", "fullscreen", "termina", "terminal-gui", "razor",
        "consoleex",
    };

    /// <summary>The ConsoleEx renderer id (<c>HARBOR_TUI=consoleex</c>, <c>tui: "consoleex"</c>).</summary>
    public const string ConsoleExId = "consoleex";

    /// <summary>
    ///     Returns <see langword="true" /> when the CLI is going to enter an
    ///     interactive TUI session that owns the screen (and therefore console
    ///     logging must be suppressed to avoid corrupting the rendered frame).
    /// </summary>
    /// <remarks>
    ///     The decision mirrors <see cref="Program.Main" />: when no subcommand
    ///     is supplied (or an unknown one falls back to interactive mode), AND
    ///     the resolved TUI is interactive, this returns <see langword="true" />.
    ///     One-shot commands (<c>ask</c>, <c>providers</c>, <c>sessions</c>, …)
    ///     always return <see langword="false" /> regardless of <c>HARBOR_TUI</c>.
    /// </remarks>
    public static bool WillEnterInteractiveTui(string[] args)
    {
        if (!IsInteractiveCommand(args))
            return false;
        return IsInteractiveTuiId(ResolveTuiId());
    }

    /// <summary>True when the resolved TUI id owns the alternate screen buffer.</summary>
    public static bool IsInteractiveTuiId(string tuiId) =>
        !string.IsNullOrEmpty(tuiId) && InteractiveTuis.Contains(tuiId);

    /// <summary>
    ///     Resolve the TUI id from <c>HARBOR_TUI</c> or the persisted
    ///     <c>CliConfig.DefaultTuiRenderer</c>. Falls back to <c>"spectre-tui"</c>
    ///     when neither is set or when the value is <c>"auto"</c>. Returns
    ///     <c>"plain"</c> when Spectre is excluded at build time
    ///     (<c>HARBOR_WITH_SPECTRE_TUI=false</c>).
    /// </summary>
    public static string ResolveTuiId()
    {
#if HARBOR_WITH_SPECTRE_TUI
        string envTui = Environment.GetEnvironmentVariable("HARBOR_TUI") ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(envTui))
            return envTui.Trim();

        // Best-effort read of the persisted CliConfig — a missing or unreadable
        // file falls back to the default "spectre-tui". We deliberately do NOT
        // throw here: the host-build path also reads CliConfig and tolerates the
        // same failure modes, so logging setup must be at least as permissive.
        string? configTui = TryReadDefaultTuiFromConfig();
        if (!string.IsNullOrWhiteSpace(configTui) && !string.Equals(configTui, "auto", StringComparison.OrdinalIgnoreCase))
            return configTui!;

        // CE-4: HarborConfig (~/.harbor/config.json) may opt into ConsoleEx via
        // `tui: "consoleex"`. Only the consoleex value is honored here — legacy
        // values keep their historical "cli.json wins / config.tui ignored"
        // semantics so nobody's existing setup changes behavior.
        if (string.Equals(TryReadHarborConfigTui(), ConsoleExId, StringComparison.OrdinalIgnoreCase))
            return ConsoleExId;

        return "spectre-tui";
#else
        return "plain";
#endif
    }

    /// <summary>
    ///     Returns <see langword="true" /> for CLI invocations that end up in
    ///     <see cref="Repl.ReplRunner.RunInteractiveAsync" /> — i.e. no
    ///     subcommand, or an unknown subcommand that falls back to interactive
    ///     mode.
    /// </summary>
    private static bool IsInteractiveCommand(string[] args)
    {
        if (args is null || args.Length == 0)
            return true;

        // Strip loglevel args before checking the command name so flags don't
        // accidentally classify as a subcommand.
        string first = args[0];
        if (first.StartsWith("-", StringComparison.Ordinal))
            return true;

        // Known one-shot commands never enter interactive mode.
        return first.ToLowerInvariant() switch
        {
            "ask" or "providers" or "models" or "sessions" or "tui" or "storage"
                or "setup" or "auth" or "config" or "logs" or "help" or "--help"
                or "-h" or "version" or "--version" or "-v"
                or "--headless" or "headless" => false,
            _ => true // unknown command falls back to interactive
        };
    }

    /// <summary>
    ///     Best-effort read of <c>tui</c> directly from
    ///     <c>~/.harbor/config.json</c>. Returns <see langword="null" /> if the
    ///     file is missing or unreadable. Mirrors
    ///     <see cref="TryReadDefaultTuiFromConfig" />: pre-DI, never throws.
    /// </summary>
    private static string? TryReadHarborConfigTui()
    {
        try
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string path = Path.Combine(home, ".harbor", "config.json");
            if (!File.Exists(path))
                return null;
            string json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("tui", out var el)
                   && el.ValueKind == JsonValueKind.String
                ? el.GetString()
                : null;
        }
        catch
        {
            // Best-effort — never block logging setup on a config-read failure.
            return null;
        }
    }

    /// <summary>
    ///     Returns <see langword="true" /> when the ConsoleEx renderer should
    ///     serve the interactive REPL. Selection sources (first match wins):
    ///     <c>HARBOR_TUI=consoleex</c>, then <c>tui: "consoleex"</c> in
    ///     <c>~/.harbor/config.json</c>. The caller additionally gates on
    ///     <c>consoleEx.enabled</c>.
    /// </summary>
    public static bool IsConsoleExSelected()
    {
#if HARBOR_WITH_SPECTRE_TUI
        string envTui = Environment.GetEnvironmentVariable("HARBOR_TUI") ?? string.Empty;
        if (envTui.Equals(ConsoleExId, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrWhiteSpace(envTui))
            return false; // an explicit non-consoleex renderer always wins

        string configTui = ResolveTuiId();
        return configTui.Equals(ConsoleExId, StringComparison.OrdinalIgnoreCase);
#else
        return false;
#endif
    }

    /// <summary>
    ///     Best-effort read of <c>CliConfig.DefaultTuiRenderer</c> directly from
    ///     <c>~/.harbor/cli.json</c>. Returns <see langword="null" /> if the file
    ///     is missing or unreadable. We deliberately avoid the full
    ///     <c>JsonAppConfigStore&lt;CliConfig&gt;</c> pipeline here because the
    ///     logging builder runs before DI exists — and we want to know whether to
    ///     attach a console logger before the host is built.
    /// </summary>
    private static string? TryReadDefaultTuiFromConfig()
    {
        try
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string path = Path.Combine(home, ".harbor", "cli.json");
            if (!File.Exists(path))
                return null;
            string json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("defaultTuiRenderer", out var el)
                && el.ValueKind == JsonValueKind.String)
            {
                return el.GetString();
            }
            return null;
        }
        catch
        {
            // Best-effort — never block logging setup on a config-read failure.
            return null;
        }
    }
}

/// <summary>
///     Process-wide owner of the shared <see cref="InMemoryDiagnosticsPanel" />.
///     Both <c>Program.Main</c> (which builds a tiny logger factory for
///     one-shot commands) and <c>HostBuilder.Build</c> (which builds the full
///     host) call <see cref="Initialize" /> to obtain the same singleton, so
///     every log line — from the very first line of <c>Main</c> to the last line
///     emitted during host disposal — can flow into the in-TUI diagnostics panel
///     when an interactive TUI is active.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why a singleton:</b> the file logger is shared via
///         <see cref="Logging.HarborLogManager" /> for the same reason — having
///         one IDiagnosticsPanel means the F12 panel shows entries from every
///         logger (Program.cs's factory + the host's factory + any plugin
///         factory), in arrival order.
///     </para>
///     <para>
///         <b>Null when not interactive:</b> when the CLI runs a one-shot
///         command (or a non-interactive TUI like <c>plain</c>/<c>ansi</c>),
///         <see cref="Current" /> stays <see langword="null" /> and
///         <see cref="Initialize" /> is a no-op. In that mode the console logger
///         stays attached and no diagnostics panel is rendered.
///     </para>
/// </remarks>
internal static class DiagnosticsSink
{
    private static readonly object InitLock = new();

    /// <summary>
    ///     The shared panel, or <see langword="null" /> when no interactive TUI
    ///     is active (one-shot command, plain/ansi TUI).
    /// </summary>
    public static InMemoryDiagnosticsPanel? Current
    {
        get;
        private set;
    }

    /// <summary>
    ///     Create (or reuse) the shared <see cref="InMemoryDiagnosticsPanel" />.
    ///     Subsequent calls return the already-initialized instance. Safe to
    ///     call from any thread.
    /// </summary>
    public static InMemoryDiagnosticsPanel Initialize()
    {
        lock (InitLock)
        {
            Current ??= new InMemoryDiagnosticsPanel();
            return Current;
        }
    }

    /// <summary>
    ///     Reset the singleton. Intended for tests; production code never calls
    ///     this — the panel is process-singleton and self-evicting via its ring
    ///     buffer.
    /// </summary>
    public static void Reset()
    {
        lock (InitLock)
        {
            Current = null;
        }
    }
}
