#if HARBOR_WITH_SCRIPTING
using Harbor.Scripting.Abstractions;
#endif
#if HARBOR_WITH_PLUGINS
using Harbor.Plugins.Abstractions;
#endif
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Cli.Commands;
using Harbor.Cli.Hosting;
using Harbor.Cli.Logging;
using Harbor.Cli.Repl;
using Harbor.Core.Configuration;
using Harbor.Core.Onboarding;
#if HARBOR_WITH_SCRIPTING
using Harbor.Scripting.Bridge;
using Harbor.Scripting.Compilation;
using Harbor.Scripting.Engines;
using Harbor.Scripting.Hosting;
using Harbor.Scripting.Storage;
#endif
using Harbor.Terminal.Abstractions;
using Harbor.Ui.Framework.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
namespace Harbor.Cli;
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
        string? scriptPath = ExtractScriptArg(args, out var remainingArgs);
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
        bool interactiveTui = Hosting.TuiMode.WillEnterInteractiveTui(args);
        var diagnosticsPanel = interactiveTui ? Hosting.DiagnosticsSink.Initialize() : null;

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
                builder.AddFilter<Microsoft.Extensions.Logging.Console.ConsoleLoggerProvider>(
                    (category, level) =>
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
            return command switch
            {
                "ask" => await RunAskAsync(args.Skip(1).ToArray(), scriptPath),
                "providers" => await RunListProvidersAsync(),
                "models" => await RunListModelsAsync(args.Skip(1).FirstOrDefault()),
                "sessions" => await RunListSessionsAsync(),
                "tui" => PrintTuiOptions(),
                "storage" => PrintStorageOptions(),
                "setup" => await RunSetupAsync(),
                "auth" => await RunAuthAsync(args.Skip(1).ToArray()),
                "config" => await RunConfigAsync(args.Skip(1).ToArray()),
                "logs" => RunLogsCommand(args.Skip(1).ToArray()),
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
            var server = services.GetService<Harbor.Ipc.IHarborServer>();
            if (server is not null)
            {
                _logger.LogInformation("Starting IPC server at {Endpoint}", server.Endpoint);
                await server.StartAsync().ConfigureAwait(false);
            }
        }
        else if (string.Equals(mode, "ipc-client", StringComparison.OrdinalIgnoreCase))
        {
            var client = services.GetService<Harbor.Ipc.IHarborClient>();
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
            var server = services.GetService<Harbor.Ipc.IHarborServer>();
            if (server is not null)
            {
                _logger.LogInformation("Stopping IPC server");
                await server.StopAsync().ConfigureAwait(false);
            }
        }
        else if (string.Equals(mode, "ipc-client", StringComparison.OrdinalIgnoreCase))
        {
            var client = services.GetService<Harbor.Ipc.IHarborClient>();
            if (client is not null)
            {
                _logger.LogInformation("Disconnecting IPC client");
                await client.DisconnectAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    ///     Run a script file at startup via <see cref="ScriptHost" />. The script's
    ///     <c>Harbor.registerTool</c> calls register tools in the live
    ///     <see cref="IToolRegistry" />, making them available to the agent.
    /// </summary>
    /// <returns>Success, or failure with an error message. Never throws for expected script failures.</returns>
    private static async Task<CSharpFunctionalExtensions.Result> RunStartupScriptAsync(IServiceProvider services, string? scriptPath)
    {
        if (string.IsNullOrEmpty(scriptPath))
        {
            return CSharpFunctionalExtensions.Result.Success();
        }
#if !HARBOR_WITH_SCRIPTING
        // No-scripting build excludes the entire Harbor.Scripting.* stack —
        // --script is reported as unsupported rather than silently ignored.
        _ = services;
        _logger.LogWarning("--script flag ignored: HARBOR_WITH_SCRIPTING build flag is off");
        return CSharpFunctionalExtensions.Result.Failure(
            "Scripting is disabled in this build. Use the full build (./build.sh Publish) with --with-scripting to enable --script.");
#else
        var tools = services.GetRequiredService<IToolRegistry>();
        var providers = services.GetRequiredService<IProviderRegistry>();
        var agents = services.GetRequiredService<IAgentRegistry>();
        var loggerFactory = services.GetRequiredService<ILoggerFactory>();

        // Compose the script host: SharpTS engine (default) + tsc compiler
        // fallback + in-memory store seeded with the one-shot script path.
        // SharpTS handles TypeScript natively (no tsc needed); if `sharpts`
        // is not on PATH, the host falls back to the Jint engine.
        var sharpTsLogger = loggerFactory.CreateLogger<SharpTsScriptEngine>();
        var jintLogger = loggerFactory.CreateLogger("Harbor.Scripting.Jint");
        var tscLogger = loggerFactory.CreateLogger<TscCompiler>();
        var hostLogger = loggerFactory.CreateLogger<ScriptHost>();

        var sharpTs = new SharpTsScriptEngine(sharpTsLogger);
        IScriptEngine engine = sharpTs.IsAvailable
            ? sharpTs
            : new JintScriptEngine(jintLogger);
        IScriptCompiler compiler = engine is SharpTsScriptEngine
            ? new PassThroughCompiler()
            : new TscCompiler(tscLogger);

        var globals = new ScriptGlobals
        {
            Tools = tools,
            Providers = providers,
            Agents = agents,
            Logger = loggerFactory.CreateLogger("Harbor.Script")
        };

        var host = new ScriptHost(engine, new InMemoryScriptStore(), compiler, hostLogger);
        string fullPath = Path.GetFullPath(scriptPath);
        string source;
        try
        {
            source = File.ReadAllText(fullPath);
        }
        catch (Exception ex)
        {
            return CSharpFunctionalExtensions.Result.Failure($"Failed to read script '{fullPath}': {ex.Message}");
        }

        var result = await host.EvaluateAsync(fullPath, source, globals).ConfigureAwait(false);
        return result.IsSuccess
            ? CSharpFunctionalExtensions.Result.Success()
            : CSharpFunctionalExtensions.Result.Failure(result.Error ?? "Script evaluation failed.");
#endif
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
        await cmd.ExecuteAsync(args, ctx);
        return 0;
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
        await cmd.ExecuteAsync(args, ctx);
        return 0;
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
                          corresponding Harbor.Tui.* project reference to Harbor.Cli.csproj
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
    private static int RunLogsCommand(string[] args)
    {
        var cmd = new LogsCommand(Console.Out, Console.Error);
        return cmd.Execute(args);
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
