using System.Runtime.InteropServices;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Configuration;
using Harbor.Application.Onboarding;
using Harbor.App.Cli.Hosting;
using Harbor.Terminal.Abstractions;
using Harbor.Tui.CellForge.Input;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Streaming;
using Harbor.Tui.CellForge.Widgets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
namespace Harbor.App.Cli.Repl;
/// <summary>
///     REPL and interactive session runner — single responsibility: run the user interaction loop.
///     Extracted from Program.cs.
/// </summary>
internal sealed class ReplRunner
{
    private readonly ILogger<ReplRunner> _logger;

    public ReplRunner(ILogger<ReplRunner> logger)
    {
        _logger = logger;
    }

    public async Task<int> RunInteractiveAsync(IServiceProvider sp, CancellationToken ct = default)
    {
        // Plugin hot-reload: resolve (and thereby start) the FS watcher glue for the
        // interactive session; disposal rides on the host container teardown.
        _ = sp.GetService<Harbor.Hosting.PluginAutoReloader>();

        // ── CE-4: CellForge gate (второй путь рендера) ────────────────────
        // Режим включается значением consoleex у переменной окружения HARBOR_TUI
        // или поля tui в config.json. Kill-switch — секция ui.consoleEx.enabled.
        // При отказе raw-режима — прозрачный откат на legacy-путь ниже.
        var earlyConfigResult = await sp.GetRequiredService<IConfigStore>().LoadAsync().ConfigureAwait(false);
        var earlyConfig = earlyConfigResult.IsSuccess ? earlyConfigResult.Value : HarborConfig.Default;
        if (TuiMode.IsCellForgeSelected())
        {
            if (!earlyConfig.Ui.CellForge.Enabled)
            {
                _logger.LogWarning("CellForge выбран (tui/env), но ui.consoleEx.enabled=false — используется legacy-рендер");
            }
            else if (!earlyConfig.Onboarded)
            {
                _logger.LogInformation("CellForge отложен: onboarding не завершён — мастер требует legacy-рендер");
            }
            else
            {
                var consoleResult = await RunCellForgeAsync(sp, ct).ConfigureAwait(false);
                if (consoleResult.IsSuccess)
                {
                    return consoleResult.Value;
                }

                _logger.LogWarning("CellForge недоступен ({Reason}) — откат на legacy-рендер", consoleResult.Error);
            }
        }

        var configStore = sp.GetRequiredService<IConfigStore>();
        var authStore = sp.GetRequiredService<AuthStore>();
        var wizard = sp.GetRequiredService<OnboardingWizard>();
        var renderer = sp.GetRequiredService<ITuiRenderer>();
        var eventBus = sp.GetRequiredService<IEventBus>();
        var agent = sp.GetRequiredService<IAgent>();
        var sessionStore = sp.GetRequiredService<ISessionStore>();
        var agentRegistry = sp.GetRequiredService<IAgentRegistry>();
        var providers = sp.GetRequiredService<IProviderRegistry>();

        _logger.LogInformation("Interactive REPL starting — renderer={RendererType}", renderer.GetType().Name);
        await renderer.InitializeAsync().ConfigureAwait(false);

        var configResult = await configStore.LoadAsync().ConfigureAwait(false);
        var config = configResult.IsSuccess ? configResult.Value : HarborConfig.Default;
        _logger.LogInformation("Config loaded: provider={Provider}, model={Model}, agent={Agent}, onboarded={Onboarded}",
            config.EffectiveProvider, config.EffectiveModel, config.Agent, config.Onboarded);

        if (!config.Onboarded)
        {
            _logger.LogInformation("Onboarding not complete — launching wizard");
            var writer = (Action<string>)(msg => _ = renderer.WriteLineAsync(msg));
            var reader = (Func<string, Task<string>>)(async prompt =>
            {
                var r = await renderer.ReadLineAsync(prompt).ConfigureAwait(false);
                return r.IsSuccess ? r.Value : string.Empty;
            });
            var wizardResult = await wizard.RunAsync(reader, writer).ConfigureAwait(false);
            if (wizardResult.IsFailure)
            {
                _logger.LogError("Onboarding wizard failed: {Error}", wizardResult.Error);
                await renderer.WriteLineAsync($"Setup failed: {wizardResult.Error}").ConfigureAwait(false);
                return 1;
            }
            config = (await configStore.LoadAsync().ConfigureAwait(false)).Value;
            _logger.LogInformation("Onboarding completed, config reloaded");
        }

        if (renderer is not IInteractiveTuiRenderer)
        {
            _logger.LogDebug("Non-interactive renderer — showing header text");
            await renderer.WriteLineAsync("Harbor — modular AI coding agent").ConfigureAwait(false);
            await renderer.WriteLineAsync($"Provider: {config.EffectiveProvider} | Model: {config.EffectiveModel} | Agent: {config.Agent}").ConfigureAwait(false);
            await renderer.WriteLineAsync("Type '/help' for commands, '/exit' to quit.").ConfigureAwait(false);
            await renderer.WriteLineAsync(string.Empty).ConfigureAwait(false);
        }

        var defaultAgent = agentRegistry.GetAllAgents().FirstOrDefault(a => a.Name.Value == config.Agent)
                           ?? agentRegistry.GetAllAgents()[0];
        string[] parts = config.EffectiveModel.Split('/', 2);
        _logger.LogInformation("Creating session: agent={Agent}, provider={Provider}, model={Model}",
            defaultAgent.Name.Value, parts[0], parts.Length > 1 ? parts[1] : config.EffectiveModel);
        var sessionResult = await sessionStore.CreateAsync(
            Environment.CurrentDirectory, defaultAgent.Name.Value, parts[0],
            parts.Length > 1 ? parts[1] : config.EffectiveModel).ConfigureAwait(false);
        if (sessionResult.IsFailure)
        {
            _logger.LogError("Session creation failed: {Error}", sessionResult.Error);
            await renderer.WriteLineAsync($"Failed: {sessionResult.Error}").ConfigureAwait(false);
            return 1;
        }
        agent.Initialize(sessionResult.Value, defaultAgent);
        _logger.LogInformation("Agent initialized: session={SessionId}, agent={Agent}", sessionResult.Value.Id, defaultAgent.Name.Value);

        if (renderer is IInteractiveTuiRenderer interactive)
        {
            _logger.LogInformation("Interactive renderer detected — entering interactive loop");
            var dispatcher = new SlashCommandDispatcher(sp.GetRequiredService<ILogger<SlashCommandDispatcher>>());
            int? slashExitCode = null;
            interactive.SetSlashHandler(async raw =>
            {
                SlashCommandOutcome outcome = await dispatcher.HandleAsync(
                    raw, sp, renderer, agent, agentRegistry, configStore, authStore, providers, sessionResult.Value).ConfigureAwait(false);
                if (outcome.ShouldQuit)
                {
                    slashExitCode = outcome.ExitCode;
                }
            });
            int exitCode = await interactive.RunInteractiveAsync(agent, sp).ConfigureAwait(false);
            if (slashExitCode is int quitCode)
            {
                _logger.LogInformation("Interactive loop ended via /exit with code {ExitCode}", quitCode);
                return quitCode;
            }
            _logger.LogInformation("Interactive loop ended with exit code {ExitCode}", exitCode);
            return exitCode;
        }

        _logger.LogDebug("Renderer initialized, subscribing to event bus");
        eventBus.Subscribe(async (evt, c) => await renderer.RenderAsync(evt, c).ConfigureAwait(false));

        _logger.LogInformation("Non-interactive renderer — entering line REPL");
        int lineExitCode = await RunLineReplAsync(renderer, agent, sp, configStore, authStore, providers, agentRegistry, sessionResult.Value).ConfigureAwait(false);
        _logger.LogInformation("Line REPL ended with exit code {ExitCode}", lineExitCode);
        return lineExitCode;
    }

    /// <summary>
    ///     CE-4: сборка и запуск CellForge-REPL. Сессия создаётся только после
    ///     успешного входа в raw-режим, чтобы откат на legacy не оставлял
    ///     осиротевших сессий.
    /// </summary>
    private async Task<Result<int>> RunCellForgeAsync(IServiceProvider sp, CancellationToken ct)
    {
        var modeController = CreateModeController();
        var agentRegistry = sp.GetRequiredService<IAgentRegistry>();
        var sessionStore = sp.GetRequiredService<ISessionStore>();
        var configStore = sp.GetRequiredService<IConfigStore>();
        var configResult = await configStore.LoadAsync().ConfigureAwait(false);
        var config = configResult.IsSuccess ? configResult.Value : HarborConfig.Default;

        try
        {
            modeController.Enter();
            modeController.Restore(); // the runner re-enters inside its own lifetime
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException)
        {
            return Result.Failure<int>($"raw mode unavailable: {ex.Message}");
        }

        var defaultAgent = agentRegistry.GetAllAgents().FirstOrDefault(a => a.Name.Value == config.Agent)
                           ?? agentRegistry.GetAllAgents()[0];
        string[] parts = config.EffectiveModel.Split('/', 2);
        _logger.LogInformation("CellForge: creating session agent={Agent}, provider={Provider}, model={Model}",
            defaultAgent.Name.Value, parts[0], parts.Length > 1 ? parts[1] : config.EffectiveModel);
        var sessionResult = await sessionStore.CreateAsync(
            Environment.CurrentDirectory, defaultAgent.Name.Value, parts[0],
            parts.Length > 1 ? parts[1] : config.EffectiveModel).ConfigureAwait(false);
        if (sessionResult.IsFailure)
        {
            return Result.Failure<int>(sessionResult.Error);
        }

        var agent = sp.GetRequiredService<IAgent>();
        agent.Initialize(sessionResult.Value, defaultAgent);

        var runner = new CellForgeReplRunner(
            sp,
            agent,
            sessionResult.Value,
            sp.GetRequiredService<ScreenSession>(),
            sp.GetRequiredService<ChatScreen>(),
            sp.GetRequiredService<ChatScreenBridge>(),
            sp.GetRequiredService<TerminalInputSource>(),
            modeController,
            sp.GetRequiredService<ITerminalBackend>(),
            sp.GetRequiredService<ILogger<CellForgeReplRunner>>());
        int exitCode = await runner.RunAsync(ct).ConfigureAwait(false);
        return Result.Success(exitCode);
    }

    private static ITerminalModeController CreateModeController() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new WindowsVtModeController()
            : new UnixTermiosModeController();

    public async Task<int> RunAskAsync(IServiceProvider sp, string prompt)
    {
        _logger.LogInformation("Ask mode: prompt={Prompt}", prompt.Length > 80 ? prompt[..80] + "..." : prompt);
        var renderer = sp.GetRequiredService<ITuiRenderer>();
        var eventBus = sp.GetRequiredService<IEventBus>();
        var agent = sp.GetRequiredService<IAgent>();
        var sessionStore = sp.GetRequiredService<ISessionStore>();
        var agentRegistry = sp.GetRequiredService<IAgentRegistry>();
        var configStore = sp.GetRequiredService<IConfigStore>();

        await renderer.InitializeAsync().ConfigureAwait(false);
        eventBus.Subscribe(async (evt, c) => await renderer.RenderAsync(evt, c).ConfigureAwait(false));

        var config = (await configStore.LoadAsync().ConfigureAwait(false)).Value;
        var defaultAgent = agentRegistry.GetAllAgents().FirstOrDefault(a => a.Name.Value == config.Agent)
                           ?? agentRegistry.GetAllAgents()[0];
        string[] parts = config.EffectiveModel.Split('/', 2);
        var sessionResult = await sessionStore.CreateAsync(
            Environment.CurrentDirectory, defaultAgent.Name.Value, parts[0],
            parts.Length > 1 ? parts[1] : config.EffectiveModel).ConfigureAwait(false);
        if (sessionResult.IsFailure)
        {
            _logger.LogError("Session creation failed: {Error}", sessionResult.Error);
            Console.Error.WriteLine($"Failed: {sessionResult.Error}");
            return 1;
        }
        agent.Initialize(sessionResult.Value, defaultAgent);
        _logger.LogInformation("Agent initialized, sending prompt");
        var result = await agent.PromptAsync(prompt).ConfigureAwait(false);
        _logger.LogInformation("Ask completed: success={Success}", result.IsSuccess);
        return result.IsSuccess ? 0 : 1;
    }

    private async Task<int> RunLineReplAsync(
        ITuiRenderer renderer, IAgent agent, IServiceProvider sp,
        IConfigStore configStore, AuthStore authStore, IProviderRegistry providers,
        IAgentRegistry agentRegistry, Session session)
    {
        _logger.LogInformation("Line REPL starting");
        while (true)
        {
            var inputResult = await renderer.ReadLineAsync("> ").ConfigureAwait(false);
            if (inputResult.IsFailure) break;
            string? input = inputResult.Value;
            if (string.IsNullOrWhiteSpace(input)) continue;
            string trimmed = input.Trim();
            if (trimmed is "exit" or "quit" or ":q")
            {
                _logger.LogInformation("User requested exit");
                break;
            }

            if (trimmed.StartsWith('/'))
            {
                _logger.LogDebug("Slash command: {Command}", trimmed);
                var dispatcher = new SlashCommandDispatcher(sp.GetRequiredService<ILogger<SlashCommandDispatcher>>());
                SlashCommandOutcome outcome = await dispatcher.HandleAsync(
                    trimmed, sp, renderer, agent, agentRegistry, configStore, authStore, providers, session).ConfigureAwait(false);
                if (outcome.ShouldQuit)
                {
                    // Managed shutdown: returning the code lets Program run its
                    // normal cleanup (IPC stop, host dispose) before exiting.
                    _logger.LogInformation("Quit requested via slash command — REPL exiting with code {ExitCode}", outcome.ExitCode);
                    return outcome.ExitCode;
                }
                continue;
            }

            _logger.LogDebug("User prompt ({Length} chars)", trimmed.Length);
            await agent.PromptAsync(trimmed).ConfigureAwait(false);
        }

        _logger.LogInformation("Line REPL ended");
        return 0;
    }
}
