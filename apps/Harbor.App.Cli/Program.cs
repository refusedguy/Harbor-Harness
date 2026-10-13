using Harbor.App.Cli.Commands;
using Harbor.App.Cli.Hosting;
using Harbor.App.Cli.Logging;
using Harbor.Ui.Framework.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace Harbor.App.Cli;
/// <summary>
///     Entry point — thin composition root. Global <c>--script</c>/
///     <c>--loglevel</c> stripping, logging and the interactive-TUI routing
///     live here; every CLI verb rides the single ConsoleAppFramework builder
///     in <c>CafPilotVerbs</c> (see <c>Commands/</c>), while bare/unknown
///     invocations fall through to the interactive REPL.
/// </summary>
public static class Program
{
    private static ILogger _logger = null!;

    public static async Task<int> Main(string[] args)
    {
        // Extract --script <path> (or --script=<path>) from args before dispatch.
        // The script is run after the host is built but before the REPL/ask loop
        // starts — so script-registered tools are available to the agent.
        string? scriptPath = CliArgs.ExtractScriptArg(args, out string[] remainingArgs);
        args = remainingArgs;

        // Console level: Debug under debugger, Information by default. User can
        // override via --loglevel/-ll/HARBOR_LOGLEVEL. The previous default was
        // Warning, which is why the user "only saw a minimal log".
        var consoleLevel = HarborLogManager.ResolveConsoleLevel(args);
        // Shared file logger — also used by HostBuilder. The file ALWAYS captures
        // down to Debug so post-mortem has the full picture. Per-run timestamped
        // file (harbor-cli-{timestamp}.log), FileMode.Append — never overwrites
        // a previous run. Rolling cleanup keeps the last 50 files.
        var fileProvider = HarborLogManager.Initialize("cli", LogLevel.Debug);

        // Interactive TUI detection: when the user is about to enter an
        // interactive TUI session (SpectreTUI / Termina / Terminal.Gui /
        // RazorConsole / Fullscreen / Spectre), the TUI owns the alt-screen
        // buffer and any stray Console.Write from the logger would corrupt the
        // rendered frame. In that mode we:
        //   * skip the simple-console logger (no Console.Out writes),
        //   * initialize the shared IDiagnosticsPanel singleton and route
        //     ILogger entries into it via DiagnosticsPanelLoggerProvider,
        //   * the in-TUI panel (F12) shows the live log stream.
        // One-shot commands (`harbor ask`, `harbor providers`, …) and
        // non-interactive TUIs (plain, ansi) keep the console logger so the
        // user sees output inline.
        bool interactiveTui = TuiMode.WillEnterInteractiveTui(args);
        var diagnosticsPanel = interactiveTui ? DiagnosticsSink.Initialize() : null;

        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddProvider(fileProvider);
            if (diagnosticsPanel is not null)
            {
                // Interactive TUI mode: route logs to the in-TUI panel instead
                // of the console. File logging stays on (fileProvider above).
                builder.AddProvider(new DiagnosticsPanelLoggerProvider(diagnosticsPanel));
            }
            else
            {
                // Non-interactive: keep the console logger so the user sees
                // output inline (one-shot commands, plain/ansi TUI).
                builder.AddSimpleConsole(o =>
                {
                    o.SingleLine = true;
                    o.TimestampFormat = "HH:mm:ss.fff ";
                    o.IncludeScopes = false;
                });
                builder.AddFilter<ConsoleLoggerProvider>((category, level) =>
                {
                    if (category is not null && consoleLevel > LogLevel.Debug &&
                        (category.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) ||
                         category.StartsWith("Microsoft.Extensions.Hosting", StringComparison.Ordinal) ||
                         category.StartsWith("Microsoft.Hosting", StringComparison.Ordinal)))
                    {
                        return level >= LogLevel.Warning;
                    }
                    return level >= consoleLevel;
                });
            }
            // File provider filters itself by its own _fileLevel; set the
            // pipeline floor to Debug so the file actually receives Debug events.
            // The diagnostics panel does its own ring-buffer eviction so we
            // don't need a filter for it.
            builder.SetMinimumLevel(LogLevel.Debug);
        });
        _logger = loggerFactory.CreateLogger(typeof(Program).FullName ?? "Program");

        ProgramLog.StartingCli(_logger, args.Length, string.Join(' ', args));
        ProgramLog.ConsoleLogLevel(_logger, consoleLevel, fileProvider.FilePath, interactiveTui);
        try
        {
            if (args.Length == 0)
            {
                ProgramLog.NoArgsInteractive(_logger);
                return await InteractiveVerb.RunAsync(_logger, args, scriptPath);
            }

        string command = args[0].ToLowerInvariant();
        if (command == "--demo")
            command = "demo"; // `harbor --demo` is the documented alias of `harbor demo`
        if (command == "--headless")
            command = "headless"; // `harbor --headless` is the documented alias of `harbor headless`
        ProgramLog.DispatchingCommand(_logger, command);

        // Slice A (#1144): StatusCommand names Client-assembly transports, so
        // the file leaves the compile when the daemon is off (see the csproj
        // Compile Remove); the CAF-D (#1289) handler + registration follow the
        // same gate. DaemonCommand is always compiled and rides CAF ungated.
#if !HARBOR_WITH_DAEMON
        // Slice A (#1144): without the daemon there is no IPC Client assembly,
        // so `status` (daemon probing) and `ide` (daemon attach) cannot run.
        // `status` without flags is local text, but its command type lives in
        // the excluded file — point at the rebuild instead of the REPL.
        if (command is "status" or "ide")
        {
            Console.Error.WriteLine($"{command}: not available in a build without daemon support (rebuild with --with-daemon).");
            return 1;
        }
#endif
        // CAF-E slice 2 (#1290): every verb rides the single
        // ConsoleAppFramework builder in CafPilotVerbs (one RunAsync per
        // assembly, CS8785 rule) — the hand-rolled ICommand array + per-verb
        // switch are gone. Unknown commands fall through to the interactive
        // REPL, as before.
        string[] tail = args.Skip(1).ToArray();
        return command switch
        {
            "ask" => await CafPilotVerbs.RunAsync("ask", scriptPath is null ? tail : ["--script", scriptPath, ..tail]),
#if HARBOR_WITH_DAEMON
            "ide" => await CafPilotVerbs.RunAsync("ide", tail),
#endif
            "headless" => await CafPilotVerbs.RunAsync("headless", tail),
            "run" => await CafPilotVerbs.RunAsync("run", tail),
            "providers" or "--providers" => await CafPilotVerbs.RunAsync("providers", tail),
            "models" => await CafPilotVerbs.RunAsync("models", tail),
            "sessions" => await CafPilotVerbs.RunAsync("sessions", tail),
            "daemon" => await CafPilotVerbs.RunAsync("daemon", tail),
#if HARBOR_WITH_DAEMON
            "status" => await CafPilotVerbs.RunAsync("status", tail),
#endif
            "mcp" => await CafPilotVerbs.RunAsync("mcp", tail),
            "serve" => await CafPilotVerbs.RunAsync("serve", tail),
            "tui" => await CafPilotVerbs.RunAsync("tui", tail),
            "events" => await CafPilotVerbs.RunAsync("events", tail),
            "storage" => await CafPilotVerbs.RunAsync("storage", tail),
            "setup" => await CafPilotVerbs.RunAsync("setup", tail),
            "demo" => await CafPilotVerbs.RunAsync("demo", tail),
            "logs" => await CafPilotVerbs.RunAsync("logs", tail),
            "plugin" => await CafPilotVerbs.RunAsync("plugin", tail),
            "skill" => await CafPilotVerbs.RunAsync("skill", tail),
            "auth" => await CafPilotVerbs.RunAsync("auth", tail),
            "config" => await CafPilotVerbs.RunAsync("config", tail),
            "help" or "--help" or "-h" => await CafPilotVerbs.RunAsync("help", tail),
            "version" or "--version" or "-v" => await CafPilotVerbs.RunAsync("version", tail),
            _ => await InteractiveVerb.RunAsync(_logger, Array.Empty<string>(), scriptPath)
        };
        }
        catch (Exception ex)
        {
            ProgramLog.UnhandledException(_logger, ex);
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    // ── Backward-compat forwarders ──
    // The implementations live in Commands/CliArgs.cs (#176). These one-liners
    // stay so existing callers (HostBuilder.Logging, tests via
    // InternalsVisibleTo) keep compiling without changes.

    /// <summary>
    ///     Помечает процесс как approver-less (#52): one-shot verbs
    ///     (<c>ask</c>, <c>run task</c>) никогда не крутят ChatScreen frame
    ///     loop — см. <see cref="Commands.CliArgs" />.
    /// </summary>
    internal static void MarkApproverless() => CliArgs.MarkApproverless();

    // Delegates to HarborLogManager.ResolveConsoleLevel — see Commands/CliArgs.
    internal static LogLevel ResolveLogLevel(string[] args) => CliArgs.ResolveLogLevel(args);

    internal static string[] StripLogArgs(string[] args) => CliArgs.StripLogArgs(args);

    internal static string? ExtractScriptArg(string[] args, out string[] remaining) =>
        CliArgs.ExtractScriptArg(args, out remaining);
}

/// <summary>
///     SG6: BCL <c>[LoggerMessage]</c> delegates for <see cref="Program" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class ProgramLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Starting Harbor CLI with {ArgCount} args: {Args}")]
    public static partial void StartingCli(ILogger logger, int argCount, string args);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Console log level: {ConsoleLevel}; file log: {FilePath}; interactive-tui: {InteractiveTui}")]
    public static partial void ConsoleLogLevel(ILogger logger, LogLevel consoleLevel, string filePath, bool interactiveTui);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "No args provided — entering interactive mode")]
    public static partial void NoArgsInteractive(ILogger logger);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Command: {Command}")]
    public static partial void DispatchingCommand(ILogger logger, string command);

    [LoggerMessage(EventId = 5, Level = LogLevel.Error, Message = "Unhandled exception in CLI entry point")]
    public static partial void UnhandledException(ILogger logger, Exception ex);
}
