using Harbor.App.Cli.Commands;
using Harbor.App.Cli.Hosting;
using Harbor.App.Cli.Logging;
using Harbor.App.Cli.Repl;
using Harbor.Ui.Framework.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace Harbor.App.Cli;
/// <summary>
///     Entry point — thin dispatcher. All logic delegated to the
///     <c>Commands/</c> verb runners, HostBuilder, ReplRunner and
///     SlashCommandDispatcher.
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

        _logger.LogInformation("Starting Harbor CLI with {ArgCount} args: {Args}", args.Length, string.Join(' ', args));
        _logger.LogInformation("Console log level: {ConsoleLevel}; file log: {FilePath}; interactive-tui: {InteractiveTui}",
            consoleLevel, fileProvider.FilePath, interactiveTui);
        try
        {
            if (args.Length == 0)
            {
                _logger.LogInformation("No args provided — entering interactive mode");
                return await InteractiveVerb.RunAsync(_logger, args, scriptPath);
            }

        string command = args[0].ToLowerInvariant();
        if (command == "--demo")
            command = "demo"; // `harbor --demo` is the documented alias of `harbor demo`
        _logger.LogInformation("Command: {Command}", command);

        var cliCommands = new ICommand[]
        {
            new LogsCommand(Console.Out, Console.Error),
            new DaemonCommand(Console.Out, Console.Error),
            new StatusCommand(Console.Out, Console.Error),
            new PluginsCommand(Console.Out, Console.Error),
            new SkillsCommand(Console.Out, Console.Error),
            new DemoCommand(Console.Out, Console.Error),
        };
        if (await SlashCommandDispatcherStatic.TryHandleAsync(command, args.Skip(1).ToArray(), cliCommands).ConfigureAwait(false) is int exitCode)
            return exitCode;

        return command switch
        {
            "ask" => await AskVerb.RunAsync(_logger, args.Skip(1).ToArray(), scriptPath),
            "ide" => await IdeVerb.RunAsync(_logger, args.Skip(1).ToArray()),
            "--headless" or "headless" => await HeadlessVerb.RunAsync(_logger, args.Skip(1).ToArray()),
            "run" => await RunTaskVerb.RunAsync(args.Skip(1).ToArray()),
            "providers" or "--providers" => await ProviderVerbs.RunListProvidersAsync(_logger),
            "models" => await ProviderVerbs.RunListModelsAsync(_logger, args.Skip(1).FirstOrDefault()),
            "sessions" => await SessionsVerb.RunAsync(_logger, args.Skip(1).ToArray()),
            "mcp" => await McpLoginRunner.RunAsync(Console.Out, Console.Error, args.Skip(1).ToArray()),
            "serve" => await ServeVerb.RunAsync(_logger, args.Skip(1).ToArray()),
            "tui" => await TuiAttachVerb.RunAsync(args.Skip(1).ToArray()),
            "events" => await EventsVerb.RunAsync(args.Skip(1).ToArray()),
            "storage" => HelpVerbs.PrintStorageOptions(),
            "setup" => await SetupVerb.RunAsync(_logger),
            "auth" => await AuthVerb.RunAsync(_logger, args.Skip(1).ToArray()),
            "config" => await ConfigVerb.RunAsync(_logger, args.Skip(1).ToArray()),
            "help" or "--help" or "-h" => HelpVerbs.PrintHelp(),
            "version" or "--version" or "-v" => HelpVerbs.PrintVersion(),
            _ => await InteractiveVerb.RunAsync(_logger, Array.Empty<string>(), scriptPath)
        };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in CLI entry point");
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
