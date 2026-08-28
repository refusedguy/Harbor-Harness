using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.App.Cli.Commands;
using Harbor.App.Cli.Hosting;
using Harbor.App.Cli.Logging;
using Harbor.App.Cli.Repl;
using Harbor.Application.Configuration;
using Harbor.Application.Onboarding;
using Harbor.Ipc;
using Harbor.Ipc.Protocol;
using Harbor.Tui.AnsiPlain;
using Harbor.Terminal.Abstractions;
using Harbor.Ui.Framework.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using System.Runtime.InteropServices;
#if HARBOR_WITH_PLUGINS
#endif
namespace Harbor.App.Cli;
/// <summary>
///     Entry point — thin dispatcher. All logic delegated to HostBuilder, ReplRunner, SlashCommandDispatcher.
/// </summary>
public static class Program
{
    private static ILogger _logger = null!;

    public static async Task<int> Main(string[] args)
    {
        // Extract --script <path> (or --script=<path>) from args before dispatch.
        // The script is run after the host is built but before the REPL/ask loop
        // starts — so script-registered tools are available to the agent.
        string? scriptPath = ExtractScriptArg(args, out string[] remainingArgs);
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
                return await RunInteractiveAsync(args, scriptPath);
            }

        string command = args[0].ToLowerInvariant();
        _logger.LogInformation("Command: {Command}", command);

        var cliCommands = new ICommand[]
        {
            new LogsCommand(Console.Out, Console.Error),
            new DaemonCommand(Console.Out, Console.Error),
            new StatusCommand(Console.Out, Console.Error),
            new PluginsCommand(Console.Out, Console.Error),
        };
        if (await SlashCommandDispatcher.TryHandleAsync(command, args.Skip(1).ToArray(), cliCommands).ConfigureAwait(false) is int exitCode)
            return exitCode;

        return command switch
        {
            "ask" => await RunAskAsync(args.Skip(1).ToArray(), scriptPath),
            "--headless" or "headless" => await RunHeadlessAsync(args.Skip(1).ToArray()),
            "providers" => await RunListProvidersAsync(),
            "models" => await RunListModelsAsync(args.Skip(1).FirstOrDefault()),
            "sessions" => await RunSessionsAsync(args.Skip(1).ToArray()),
            "tui" => PrintTuiOptions(),
            "storage" => PrintStorageOptions(),
            "setup" => await RunSetupAsync(),
            "auth" => await RunAuthAsync(args.Skip(1).ToArray()),
            "config" => await RunConfigAsync(args.Skip(1).ToArray()),
            "help" or "--help" or "-h" => PrintHelp(),
            "version" or "--version" or "-v" => PrintVersion(),
            _ => await RunInteractiveAsync(Array.Empty<string>(), scriptPath)
        };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in CLI entry point");
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static async Task<int> RunInteractiveAsync(string[] args, string? scriptPath = null)
    {
        _logger.LogInformation("Starting interactive mode");
        using var host = HostBuilder.Build(args);
        await StartIpcAsync(host.Services).ConfigureAwait(false);
        var scriptResult = await RunStartupScriptAsync(host.Services, scriptPath).ConfigureAwait(false);
        if (scriptResult.IsFailure)
        {
            _logger.LogWarning("Startup script failed: {Error}", scriptResult.Error);
        }
        var runner = new ReplRunner(host.Services.GetRequiredService<ILogger<ReplRunner>>());
        int exitCode = await runner.RunInteractiveAsync(host.Services).ConfigureAwait(false);
        _logger.LogInformation("Interactive mode ended with exit code {ExitCode}", exitCode);
        await StopIpcAsync(host.Services).ConfigureAwait(false);
        return exitCode;
    }

    /// <summary>
    ///     Headless daemon mode (<c>harbor --headless</c>, also used by
    ///     <c>harbor daemon start</c>): run the full agent host (EventBus,
    ///     tools, providers, storage) plus the IPC server — no UI, no REPL,
    ///     no console interaction — and block until SIGINT/SIGTERM. Remote
    ///     clients connect over IPC; the spawning parent typically redirects
    ///     stdin/stdout.
    /// </summary>
    private static async Task<int> RunHeadlessAsync(string[] args)
    {
        _logger.LogInformation("Starting headless daemon mode");

        // A headless host without a transport serves nobody: default to
        // ipc-server unless the operator pinned another mode explicitly.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HARBOR_MODE")))
        {
            Environment.SetEnvironmentVariable("HARBOR_MODE", "ipc-server");
        }

        using var host = HostBuilder.Build(args);
        var server = host.Services.GetService<IHarborServer>();
        if (server is null)
        {
            _logger.LogError(
                "Headless mode requires an IPC server, but no IHarborServer is registered (HARBOR_MODE={Mode})",
                Environment.GetEnvironmentVariable("HARBOR_MODE"));
            Console.Error.WriteLine("daemon: IPC server unavailable — cannot run headless.");
            return 1;
        }

        await StartIpcAsync(host.Services).ConfigureAwait(false);
        Console.WriteLine($"harbor daemon listening on '{server.Endpoint}'");
        PrintPairingBlock(host.Services);
        _logger.LogInformation("Daemon ready on {Endpoint} — waiting for clients or shutdown signal", server.Endpoint);

        using var shutdownCts = new CancellationTokenSource();

        // Ctrl+C (SIGINT): cancel the wait but stay alive long enough for the
        // graceful IPC stop + host dispose below.
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            shutdownCts.Cancel();
        };

        // SIGTERM (`kill`, `harbor daemon stop`). Registration is best-effort:
        // without it the process still exits on SIGTERM, just ungracefully.
        try
        {
            using PosixSignalRegistration sigterm = PosixSignalRegistration.Create(
                PosixSignal.SIGTERM, _ => shutdownCts.Cancel());
            await WaitForShutdownAsync(shutdownCts.Token).ConfigureAwait(false);
        }
        catch (PlatformNotSupportedException ex)
        {
            _logger.LogWarning(ex, "POSIX signal handling unavailable — falling back to SIGINT only");
            await WaitForShutdownAsync(shutdownCts.Token).ConfigureAwait(false);
        }

        _logger.LogInformation("Shutdown requested — stopping IPC server");
        await StopIpcAsync(host.Services).ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    ///     Park the caller until the shutdown token fires. An infinite delay
    ///     arms no timer — the await completes only via cancellation.
    /// </summary>
    private static async Task WaitForShutdownAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected shutdown path — the token is cancelled by SIGINT/SIGTERM.
        }
    }

    private static async Task<int> RunAskAsync(string[] args, string? scriptPath = null)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: harbor ask <prompt> [--script <path>]");
            return 1;
        }
        string prompt = string.Join(' ', StripLogArgs(args));
        _logger.LogInformation("Starting ask command with prompt length {Length}", prompt.Length);
        using var host = HostBuilder.Build(args);
        await StartIpcAsync(host.Services).ConfigureAwait(false);
        var scriptResult = await RunStartupScriptAsync(host.Services, scriptPath).ConfigureAwait(false);
        if (scriptResult.IsFailure)
        {
            _logger.LogWarning("Startup script failed: {Error}", scriptResult.Error);
        }
        var runner = new ReplRunner(host.Services.GetRequiredService<ILogger<ReplRunner>>());
        int exitCode = await runner.RunAskAsync(host.Services, prompt).ConfigureAwait(false);
        await StopIpcAsync(host.Services).ConfigureAwait(false);
        return exitCode;
    }

    /// <summary>
    ///     When a networked listener is configured, print the pairing block:
    ///     the canonical harbor:// pairing code plus its QR (address follows
    ///     tailscale &gt; lan &gt; loopback priority, so peers outside the
    ///     LAN get the tailnet address — never eth0). Best-effort: a QR
    ///     failure never blocks daemon startup.
    /// </summary>
    private static void PrintPairingBlock(IServiceProvider services)
    {
        var pairing = services.GetService<DaemonPairingInfo>();
        if (pairing is null) return;

        Console.WriteLine();
        Console.WriteLine("Remote pairing:");
        Console.WriteLine($"  {pairing.Code}");
        Console.WriteLine($"  PSK file: {PskStore.DefaultPath}");
        try
        {
            Console.WriteLine(TerminalQrRenderer.Render(new Uri(pairing.Code)));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "QR rendering failed; the text pairing code above remains authoritative");
        }
    }

    /// <summary>
    ///     Start the IPC layer based on the active HARBOR_MODE:
    ///     <list type="bullet">
    ///         <item><c>inprocess</c> — no-op (InProcessHarborClient has no transport).</item>
    ///         <item><c>ipc-server</c> — bind <c>IHarborServer</c> and start accepting clients.</item>
    ///         <item><c>ipc-client</c> — call <c>IHarborClient.ConnectAsync</c> to open the pipe/socket.</item>
    ///     </list>
    ///     Silently skips when the relevant service is not registered (e.g. tests
    ///     that build a partial host).
    /// </summary>
    private static async Task StartIpcAsync(IServiceProvider services)
    {
        string mode = Environment.GetEnvironmentVariable("HARBOR_MODE") ?? "inprocess";
        if (string.Equals(mode, "ipc-server", StringComparison.OrdinalIgnoreCase))
        {
            // Every registered server starts: the local pipe/UDS listener and
            // — when HARBOR_LISTEN is configured — the networked TCP one.
            foreach (var server in services.GetServices<IHarborServer>())
            {
                _logger.LogInformation("Starting IPC server at {Endpoint}", server.Endpoint);
                await server.StartAsync().ConfigureAwait(false);
            }
        }
        else if (string.Equals(mode, "ipc-client", StringComparison.OrdinalIgnoreCase))
        {
            var client = services.GetService<IHarborClient>();
            if (client is not null)
            {
                _logger.LogInformation("Connecting IPC client");
                await client.ConnectAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    ///     Stop the IPC layer (mirror of <see cref="StartIpcAsync" />).
    /// </summary>
    private static async Task StopIpcAsync(IServiceProvider services)
    {
        string mode = Environment.GetEnvironmentVariable("HARBOR_MODE") ?? "inprocess";
        if (string.Equals(mode, "ipc-server", StringComparison.OrdinalIgnoreCase))
        {
            var server = services.GetService<IHarborServer>();
            if (server is not null)
            {
                _logger.LogInformation("Stopping IPC server");
                await server.StopAsync().ConfigureAwait(false);
            }
        }
        else if (string.Equals(mode, "ipc-client", StringComparison.OrdinalIgnoreCase))
        {
            var client = services.GetService<IHarborClient>();
            if (client is not null)
            {
                _logger.LogInformation("Disconnecting IPC client");
                await client.DisconnectAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    ///     Run a script file at startup. Scripting moved to contrib/scripting
    ///     (sprint 2) — the main CLI reports --script as unsupported.
    /// </summary>
    /// <returns>Success, or failure with an error message. Never throws for expected script failures.</returns>
    private static async Task<Result> RunStartupScriptAsync(IServiceProvider services, string? scriptPath)
    {
        if (string.IsNullOrEmpty(scriptPath))
        {
            return Result.Success();
        }

        // Scripting moved to contrib/scripting (sprint 2) — the main CLI no
        // longer ships Harbor.Scripting.*. --script is reported as unsupported
        // rather than silently ignored.
        _ = services;
        _logger.LogWarning("--script flag ignored: scripting lives in contrib/scripting and is not part of the main CLI build");
        return CSharpFunctionalExtensions.Result.Failure(
            "Scripting is not available in this build. Build contrib/Contrib.slnx for the scripting-enabled projects.");
    }

    /// <summary>
    ///     Extract <c>--script &lt;path&gt;</c> (or <c>--script=&lt;path&gt;</c>) from the
    ///     argument list. Returns the script path (or <see langword="null" /> if not
    ///     present) and the remaining args with the flag stripped.
    /// </summary>
    internal static string? ExtractScriptArg(string[] args, out string[] remaining)
    {
        var remainingList = new List<string>(args.Length);
        string? scriptPath = null;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a.Equals("--script", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length)
                {
                    scriptPath = args[i + 1];
#pragma warning disable S127 // Intentional: consume the value token alongside the flag.
                    i++;
#pragma warning restore S127
                }
                continue;
            }
            if (a.StartsWith("--script=", StringComparison.OrdinalIgnoreCase))
            {
                scriptPath = a["--script=".Length..];
                continue;
            }
            remainingList.Add(a);
        }
        remaining = remainingList.ToArray();
        return scriptPath;
    }

    private static async Task<int> RunSetupAsync()
    {
        _logger.LogInformation("Starting setup wizard");
        using var host = HostBuilder.Build();
        var wizard = host.Services.GetRequiredService<OnboardingWizard>();
        var renderer = host.Services.GetRequiredService<ITuiRenderer>();
        await renderer.InitializeAsync().ConfigureAwait(false);
        var writer = (Action<string>)(msg => _ = renderer.WriteLineAsync(msg));
        var reader = (Func<string, Task<string>>)(async prompt =>
        {
            var r = await renderer.ReadLineAsync(prompt).ConfigureAwait(false);
            return r.IsSuccess ? r.Value : string.Empty;
        });
        var result = await wizard.RunAsync(reader, writer).ConfigureAwait(false);
        _logger.LogInformation("Setup wizard finished with success={Success}", result.IsSuccess);
        return result.IsSuccess ? 0 : 1;
    }

    private static async Task<int> RunAuthAsync(string[] args)
    {
        _logger.LogInformation("Starting auth command");
        using var host = HostBuilder.Build(args);
        var authStore = host.Services.GetRequiredService<AuthStore>();
        var writer = (Action<string>)Console.WriteLine;
        var cmd = new AuthCommand(authStore, writer);
        var ctx = new SimpleCommandContext(null!, null!,
            host.Services.GetRequiredService<IProviderRegistry>(),
            host.Services.GetRequiredService<IToolRegistry>(),
            writer, _ => Task.FromResult(string.Empty));
        // Propagate the command's own result (0 = success, non-zero = failure)
        // instead of unconditionally reporting success.
        var authResult = await cmd.ExecuteAsync(args, ctx).ConfigureAwait(false);
        return authResult.IsSuccess ? 0 : 1;
    }

    private static async Task<int> RunConfigAsync(string[] args)
    {
        _logger.LogInformation("Starting config command");
        using var host = HostBuilder.Build(args);
        var configStore = host.Services.GetRequiredService<IConfigStore>();
        var writer = (Action<string>)Console.WriteLine;
        var cmd = new ConfigCommand(configStore, writer);
        var ctx = new SimpleCommandContext(null!, null!,
            host.Services.GetRequiredService<IProviderRegistry>(),
            host.Services.GetRequiredService<IToolRegistry>(),
            writer, _ => Task.FromResult(string.Empty));
        // Propagate the command's own result (0 = success, non-zero = failure)
        // instead of unconditionally reporting success.
        var configResult = await cmd.ExecuteAsync(args, ctx).ConfigureAwait(false);
        return configResult.IsSuccess ? 0 : 1;
    }

    private static async Task<int> RunListProvidersAsync()
    {
        _logger.LogInformation("Listing providers");
        using var host = HostBuilder.Build();
        var providers = host.Services.GetRequiredService<IProviderRegistry>();
        var ids = providers.GetRegisteredProviderIds();
        _logger.LogInformation("Found {Count} registered providers", ids.Count);
        Console.WriteLine($"Providers ({ids.Count}):");
        foreach (var id in ids)
        {
            var r = providers.GetClient(id);
            Console.WriteLine($"  [{(r.IsSuccess ? "OK" : "FAIL")}] {id}");
        }
        await Task.CompletedTask;
        return 0;
    }

    private static async Task<int> RunListModelsAsync(string? providerId)
    {
        _logger.LogInformation("Listing models for provider {Provider}", providerId ?? "(all)");
        using var host = HostBuilder.Build();
        var providers = host.Services.GetRequiredService<IProviderRegistry>();
        if (!string.IsNullOrEmpty(providerId))
        {
            var pidResult = ProviderId.TryCreate(providerId);
            if (pidResult.IsFailure)
            {
                Console.Error.WriteLine(pidResult.Error);
                return 1;
            }
            var clientResult = providers.GetClient(pidResult.Value);
            if (clientResult.IsFailure)
            {
                Console.Error.WriteLine(clientResult.Error);
                return 1;
            }
            var modelsResult = await clientResult.Value.GetModelsAsync().ConfigureAwait(false);
            if (modelsResult.IsFailure)
            {
                Console.Error.WriteLine(modelsResult.Error);
                return 1;
            }
            _logger.LogInformation("Found {Count} models for {Provider}", modelsResult.Value.Count, providerId);
            Console.WriteLine($"Models for {providerId}:");
            foreach (var m in modelsResult.Value) Console.WriteLine($"  {m.Id} — {m.DisplayName}");
            return 0;
        }
        var allResult = await providers.GetAllModelsAsync().ConfigureAwait(false);
        if (allResult.IsFailure)
        {
            Console.Error.WriteLine(allResult.Error);
            return 1;
        }
        _logger.LogInformation("Found {Count} total models", allResult.Value.Count);
        Console.WriteLine($"All models ({allResult.Value.Count}):");
        foreach (var g in allResult.Value.GroupBy(m => m.ProviderId))
        {
            Console.WriteLine($"\n{g.Key}:");
            foreach (var m in g) Console.WriteLine($"  {m.Id} — {m.DisplayName}");
        }
        return 0;
    }

    private static async Task<int> RunListSessionsAsync()
    {
        _logger.LogInformation("Listing sessions");
        using var host = HostBuilder.Build();
        var store = host.Services.GetRequiredService<ISessionStore>();
        var result = await store.ListAsync().ConfigureAwait(false);
        if (result.IsSuccess)
        {
            _logger.LogInformation("Found {Count} sessions", result.Value.Count);
            foreach (var s in result.Value)
                Console.WriteLine($"  {s.Id} — {s.Title} [{s.ProviderId}/{s.Model}]");
        }
        return 0;
    }

    /// <summary>
    ///     `harbor sessions` family: list (default), rename, export, import.
    ///     Rename persists a new title via ISessionStore.UpdateAsync; export/import
    ///     round-trip one session through the portable line payload built by
    ///     <c>ISessionPorter</c> (see StorageModule for the backend wiring).
    /// </summary>
    private static async Task<int> RunSessionsAsync(string[] args)
    {
        string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
        switch (sub)
        {
            case "list":
                return await RunListSessionsAsync();
            case "rename":
                return await RunRenameSessionAsync(args.Skip(1).ToArray());
            case "export":
                return await RunExportSessionAsync(args.Skip(1).ToArray());
            case "import":
                return await RunImportSessionAsync(args.Skip(1).ToArray());
            case "search":
                return await RunSearchSessionsAsync(args.Skip(1).ToArray());
            case "revert":
                return await RunRevertSessionAsync(args.Skip(1).ToArray());
            case "fork":
                return await RunForkSessionAsync(args.Skip(1).ToArray());
            default:
                Console.Error.WriteLine("""
                                        Usage: harbor sessions [list|rename|export|import|search|revert|fork]
                                          sessions                        list all sessions
                                          sessions rename <id> <title>    rename a session
                                          sessions export <id> [file]     export session to a portable file (default: harbor-session-<id>.jsonl)
                                          sessions import <file>          import an exported file as a NEW session
                                          sessions search <query> [--session <id>]   find messages by substring
                                          sessions revert <id> <message-id>          rewind session to the given message
                                          sessions fork <id> <message-id>            branch a NEW session copying messages up to and including the given one
                                        """);
                return 2;
        }
    }

    private static async Task<int> RunRenameSessionAsync(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: harbor sessions rename <session-id> <new-title>");
            return 2;
        }

        string sessionId = args[0];
        string title = string.Join(' ', args.Skip(1));
        using var host = HostBuilder.Build();
        var store = host.Services.GetRequiredService<ISessionStore>();

        var loaded = await store.GetAsync(sessionId).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            _logger.LogError("Rename failed: {Error}", loaded.Error);
            Console.Error.WriteLine($"Cannot rename '{sessionId}': {loaded.Error}");
            return 1;
        }

        var renamed = loaded.Value with { Title = title, UpdatedAt = DateTimeOffset.UtcNow };
        var saved = await store.UpdateAsync(renamed).ConfigureAwait(false);
        if (saved.IsFailure)
        {
            _logger.LogError("Rename persist failed: {Error}", saved.Error);
            Console.Error.WriteLine($"Cannot persist rename: {saved.Error}");
            return 1;
        }

        Console.WriteLine($"Renamed {sessionId} → \"{title}\"");
        return 0;
    }

    private static async Task<int> RunExportSessionAsync(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: harbor sessions export <session-id> [file]");
            return 2;
        }

        string sessionId = args[0];
        string path = args.Length > 1 ? args[1] : $"harbor-session-{sessionId}.jsonl";
        using var host = HostBuilder.Build();
        var porter = host.Services.GetRequiredService<ISessionPorter>();
        await using var output = new StreamWriter(path, append: false, System.Text.Encoding.UTF8);

        var exported = await porter.ExportAsync(host.Services.GetRequiredService<ISessionStore>(), sessionId, output)
            .ConfigureAwait(false);
        if (exported.IsFailure)
        {
            _logger.LogError("Export failed: {Error}", exported.Error);
            Console.Error.WriteLine($"Export failed: {exported.Error}");
            return 1;
        }

        Console.WriteLine($"Exported session {sessionId} → {path}");
        return 0;
    }

    private static async Task<int> RunImportSessionAsync(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: harbor sessions import <file>");
            return 2;
        }

        string path = args[0];
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"File not found: {path}");
            return 1;
        }

        using var host = HostBuilder.Build();
        var porter = host.Services.GetRequiredService<ISessionPorter>();
        await using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream);

        var imported = await porter.ImportAsync(host.Services.GetRequiredService<ISessionStore>(), reader)
            .ConfigureAwait(false);
        if (imported.IsFailure)
        {
            _logger.LogError("Import failed: {Error}", imported.Error);
            Console.Error.WriteLine($"Import failed: {imported.Error}");
            return 1;
        }

        string newId = imported.Value;
        var created = await host.Services.GetRequiredService<ISessionStore>().GetAsync(newId).ConfigureAwait(false);
        string title = created.IsSuccess ? created.Value.Title : "?";
        Console.WriteLine($"Imported {Path.GetFileName(path)} → new session {newId} \"{title}\"");
        return 0;
    }

    /// <summary>
    ///     <c>harbor sessions search &lt;query&gt; [--session &lt;id&gt;]</c> —
    ///     case-insensitive substring scan over persisted messages; the core
    ///     lives in <see cref="SessionSearchRunner" /> (read-only).
    /// </summary>
    private static async Task<int> RunSearchSessionsAsync(string[] args)
    {
        string? sessionFilter = null;
        List<string> rest = [];
        int i = 0;
        while (i < args.Length)
        {
            if (args[i] is "--session" or "-s" && i + 1 < args.Length)
            {
                sessionFilter = args[i + 1];
                i += 2;
                continue;
            }

            rest.Add(args[i]);
            i++;
        }

        if (rest.Count == 0)
        {
            Console.Error.WriteLine("""
                                    Usage: harbor sessions search <query> [--session <id>]
                                      Search all persisted messages (case-insensitive substring).
                                    """);
            return 2;
        }

        using var host = HostBuilder.Build();
        var store = host.Services.GetRequiredService<ISessionStore>();
        return await SessionSearchRunner.RunAsync(Console.Out, Console.Error, store, string.Join(' ', rest), sessionFilter)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     <c>harbor sessions revert &lt;session-id&gt; &lt;message-id&gt;</c> —
    ///     rewind the session to the given message: it and everything before it
    ///     stays, everything after is deleted (backend-level "rewind to here").
    /// </summary>
    private static async Task<int> RunRevertSessionAsync(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("""
                                    Usage: harbor sessions revert <session-id> <message-id>
                                      Delete every message AFTER the given one (the target itself is kept).
                                    """);
            return 2;
        }

        string sessionId = args[0];
        string messageId = args[1];
        using var host = HostBuilder.Build();
        var store = host.Services.GetRequiredService<ISessionStore>();

        var reverted = await store.DeleteMessagesAfterAsync(sessionId, messageId).ConfigureAwait(false);
        if (reverted.IsFailure)
        {
            _logger.LogError("Revert failed: {Error}", reverted.Error);
            Console.Error.WriteLine($"Cannot revert '{sessionId}' to '{messageId}': {reverted.Error}");
            return 1;
        }

        var messages = await store.GetMessagesAsync(sessionId).ConfigureAwait(false);
        int remaining = messages.IsSuccess ? messages.Value.Count : -1;
        Console.WriteLine($"Reverted session {sessionId}: deleted {reverted.Value} message(s), {remaining} remain.");
        return 0;
    }

    /// <summary>
    ///     <c>harbor sessions fork &lt;session-id&gt; &lt;message-id&gt;</c> —
    ///     branch a NEW session that copies every message up to and including the
    ///     given one (the source session is left untouched). The fork records its
    ///     lineage via <c>ParentSessionId</c> and a "(fork)" title suffix.
    /// </summary>
    private static async Task<int> RunForkSessionAsync(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("""
                                    Usage: harbor sessions fork <session-id> <message-id>
                                      Create a NEW session with all messages up to AND INCLUDING the given one.
                                    """);
            return 2;
        }

        string sessionId = args[0];
        string messageId = args[1];
        using var host = HostBuilder.Build();
        var store = host.Services.GetRequiredService<ISessionStore>();
        var runner = new Harbor.App.Cli.Commands.SessionForkRunner(store);

        var forked = await runner.ForkAsync(sessionId, messageId).ConfigureAwait(false);
        if (forked.IsFailure)
        {
            _logger.LogError("Fork failed: {Error}", forked.Error);
            Console.Error.WriteLine(forked.Error);
            return 1;
        }

        Console.WriteLine($"Forked session {sessionId} → {forked.Value.ForkId}: copied {forked.Value.Copied} message(s) up to '{messageId}'.");
        return 0;
    }

    private static int PrintTuiOptions()
    {
        Console.WriteLine("""
                          TUI renderers (set HARBOR_TUI):
                            Terminal:  ansi (default), plain, spectre, fullscreen, spectre-tui,
                                       terminal-gui, termina, razor, sixel
                            Desktop:   wpf (Windows), avalonia (cross-platform), maui (WinUI/Android/iOS/Mac)
                            Web:       blazor (Blazor Server, http://localhost:5000)
                            Non-interactive: notifications (desktop OS notifications only)

                          See docs/ALTERNATIVE_UIS.md for the full comparison.
                          Note: wpf/avalonia/maui/blazor/sixel/notifications require adding the
                          corresponding Harbor.Tui.* project reference to Harbor.App.Cli.csproj
                          (and the matching workload — e.g. `dotnet workload install maui`).
                          """);
        return 0;
    }
    private static int PrintStorageOptions()
    {
        Console.WriteLine("Storage: jsonl (default), memory, sqlite");
        return 0;
    }

    private static int PrintHelp()
    {
        Console.WriteLine("""
                          Harbor — modular AI coding agent.
                          Usage: harbor [ask <prompt>|setup|auth|config|providers|models|sessions|tui|storage|logs|help|version] [--script <path>]

                          --script <path>   Run a .js or .ts script at startup (registers tools via Harbor.registerTool).
                                            See docs/SCRIPTING.md for the full comparison of CS / Jint / SharpTS / MCP.
                          --loglevel <lvl>  Console log level (Trace/Debug/Information/Warning/Error/Critical).
                                            Defaults to Debug under debugger, Information otherwise.
                                            File log always captures down to Debug.
                          """);
        return 0;
    }

    private static int PrintVersion()
    {
        Console.WriteLine("Harbor v0.4.0-alpha");
        Console.WriteLine($".NET {Environment.Version}");
        return 0;
    }

    /// <summary>
    ///     <c>harbor logs</c> — view/manage the per-run log files under
    ///     <c>~/.harbor/logs/</c>. Subcommands: <c>--list</c> (default),
    ///     <c>--last</c> (print the latest file), <c>--follow</c> (tail -f),
    ///     <c>--clean</c> (delete all log files).
    /// </summary>
    private static async Task<int> RunLogsCommand(string[] args)
    {
        var cmd = new LogsCommand(Console.Out, Console.Error);
        return await cmd.ExecuteAsync(args).ConfigureAwait(false);
    }

    // ── Helpers ──
    // Delegates to HarborLogManager.ResolveConsoleLevel so the default level
    // (Debug under debugger, Information otherwise) and the --log-level /
    // --loglevel / -ll / HARBOR_LOGLEVEL forms stay in one place. Kept for
    // backward compat with any internal caller that still hits Program.ResolveLogLevel.
    internal static LogLevel ResolveLogLevel(string[] args) => HarborLogManager.ResolveConsoleLevel(args);

    internal static string[] StripLogArgs(string[] args)
    {
        var result = new List<string>(args.Length);
        int i = 0;
        while (i < args.Length)
        {
            if (args[i].Equals("--loglevel", StringComparison.OrdinalIgnoreCase) ||
                args[i].Equals("--log-level", StringComparison.OrdinalIgnoreCase) ||
                args[i].Equals("-ll", StringComparison.OrdinalIgnoreCase))
            {
                i += 2;
                continue;
            }
            // Also strip the --loglevel=Info / --log-level=Info inline form.
            if (args[i].StartsWith("--loglevel=", StringComparison.OrdinalIgnoreCase) ||
                args[i].StartsWith("--log-level=", StringComparison.OrdinalIgnoreCase))
            {
                i += 1;
                continue;
            }
            result.Add(args[i]);
            i++;
        }
        return result.ToArray();
    }
}
