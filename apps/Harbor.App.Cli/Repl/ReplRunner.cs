using System.Runtime.InteropServices;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Application.Configuration;
using Harbor.Application.Onboarding;
using Harbor.App.Cli.Hosting;
using Harbor.Terminal.Abstractions;
using Harbor.Tui.CellForge.Input;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Streaming;
using Harbor.Tui.CellForge.Widgets;
using Microsoft.Extensions.Logging;
namespace Harbor.App.Cli.Repl;
/// <summary>
///     REPL and interactive session runner — single responsibility: run the user interaction loop.
///     Extracted from Program.cs. All singletons arrive via ctor (#63); the
///     CellForge screen graph resolves lazily through <see cref="CellForgeScreens" />
///     so the legacy path never touches stdin/screens.
/// </summary>
internal sealed class ReplRunner
{
    private readonly ILogger<ReplRunner> _logger;
    private readonly IConfigStore _configStore;
    private readonly AuthStore _authStore;
    private readonly OnboardingWizard _wizard;
    private readonly ITuiRenderer _renderer;
    private readonly IEventBus _eventBus;
    private readonly IAgent _agent;
    private readonly ISessionStore _sessionStore;
    private readonly IAgentRegistry _agentRegistry;
    private readonly IProviderRegistry _providers;
    private readonly ILogger<CellForgeReplRunner> _cellForgeLogger;
    private readonly SlashCommandDispatcher _slashes;
    private readonly Harbor.Hosting.Rendering.IRendererPipeline? _rendererPipeline;
    private readonly ITokenTracker? _tokens;
    private readonly Func<CellForgeScreens> _cellForgeScreens;

    /// <summary>
    ///     Forwarded untouched to <see cref="IInteractiveTuiRenderer.RunInteractiveAsync" />
    ///     (the public renderer contract demands a host provider; renderers use
    ///     it for optional lookups only). #63: ReplRunner itself never resolves
    ///     from it — all of its own deps are the ctor params above.
    /// </summary>
    private readonly IServiceProvider _rendererHost;

    public ReplRunner(
        ILogger<ReplRunner> logger,
        IConfigStore configStore,
        AuthStore authStore,
        OnboardingWizard wizard,
        ITuiRenderer renderer,
        IEventBus eventBus,
        IAgent agent,
        ISessionStore sessionStore,
        IAgentRegistry agentRegistry,
        IProviderRegistry providers,
        IToolRegistry tools,
        IPermissionService permissions,
        ILogger<SlashCommandDispatcher> slashLogger,
        ILogger<CellForgeReplRunner> cellForgeLogger,
        Harbor.Hosting.PluginReloadService? pluginReload,
        Harbor.Hosting.Rendering.IRendererPipeline? rendererPipeline,
        ITokenTracker? tokens,
        Func<CellForgeScreens> cellForgeScreens,
        IServiceProvider rendererHost)
    {
        _logger = logger;
        _configStore = configStore;
        _authStore = authStore;
        _wizard = wizard;
        _renderer = renderer;
        _eventBus = eventBus;
        _agent = agent;
        _sessionStore = sessionStore;
        _agentRegistry = agentRegistry;
        _providers = providers;
        _cellForgeLogger = cellForgeLogger;
        _slashes = new SlashCommandDispatcher(
            slashLogger, tools, sessionStore, wizard, permissions, pluginReload, rendererPipeline);
        _rendererPipeline = rendererPipeline;
        _tokens = tokens;
        _cellForgeScreens = cellForgeScreens;
        _rendererHost = rendererHost;
    }

    public async Task<int> RunInteractiveAsync(CancellationToken ct = default)
    {
        // ── CE-4: CellForge gate (второй путь рендера) ────────────────────
        // Режим включается значением consoleex у переменной окружения HARBOR_TUI
        // или поля tui в config.json. Kill-switch — секция ui.consoleEx.enabled.
        // При отказе raw-режима — прозрачный откат на legacy-путь ниже.
        // (Plugin hot-reload watcher is resolved — thereby started — by the
        // composition root in Program.cs; disposal rides on host teardown.)
        var earlyConfigResult = await _configStore.LoadAsync().ConfigureAwait(false);
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
                var consoleResult = await RunCellForgeAsync(ct).ConfigureAwait(false);
                if (consoleResult.IsSuccess)
                {
                    return consoleResult.Value;
                }

                _logger.LogWarning("CellForge недоступен ({Reason}) — откат на legacy-рендер", consoleResult.Error);
            }
        }

        _logger.LogInformation("Interactive REPL starting — renderer={RendererType}", _renderer.GetType().Name);
        await _renderer.InitializeAsync().ConfigureAwait(false);

        var configResult = await _configStore.LoadAsync().ConfigureAwait(false);
        var config = configResult.IsSuccess ? configResult.Value : HarborConfig.Default;
        _logger.LogInformation("Config loaded: provider={Provider}, model={Model}, agent={Agent}, onboarded={Onboarded}",
            config.EffectiveProvider, config.EffectiveModel, config.Agent, config.Onboarded);

        if (!config.Onboarded)
        {
            _logger.LogInformation("Onboarding not complete — launching wizard");
            var writer = (Action<string>)(msg => _ = _renderer.WriteLineAsync(msg));
            var reader = (Func<string, Task<string>>)(async prompt =>
            {
                var r = await _renderer.ReadLineAsync(prompt).ConfigureAwait(false);
                return r.IsSuccess ? r.Value : string.Empty;
            });
            var wizardResult = await _wizard.RunAsync(reader, writer).ConfigureAwait(false);
            if (wizardResult.IsFailure)
            {
                _logger.LogError("Onboarding wizard failed: {Error}", wizardResult.Error);
                await _renderer.WriteLineAsync($"Setup failed: {wizardResult.Error}").ConfigureAwait(false);
                return 1;
            }
            config = (await _configStore.LoadAsync().ConfigureAwait(false)).Value;
            _logger.LogInformation("Onboarding completed, config reloaded");
        }

        if (_renderer is not IInteractiveTuiRenderer)
        {
            _logger.LogDebug("Non-interactive renderer — showing header text");
            await _renderer.WriteLineAsync("Harbor — modular AI coding agent").ConfigureAwait(false);
            await _renderer.WriteLineAsync($"Provider: {config.EffectiveProvider} | Model: {config.EffectiveModel} | Agent: {config.Agent}").ConfigureAwait(false);
            await _renderer.WriteLineAsync("Type '/help' for commands, '/exit' to quit.").ConfigureAwait(false);
            await _renderer.WriteLineAsync(string.Empty).ConfigureAwait(false);
        }

        var defaultAgent = _agentRegistry.GetAllAgents().FirstOrDefault(a => a.Name.Value == config.Agent)
                           ?? _agentRegistry.GetAllAgents()[0];
        string[] parts = config.EffectiveModel.Split('/', 2);
        _logger.LogInformation("Creating session: agent={Agent}, provider={Provider}, model={Model}",
            defaultAgent.Name.Value, parts[0], parts.Length > 1 ? parts[1] : config.EffectiveModel);
        var sessionResult = await _sessionStore.CreateAsync(
            Environment.CurrentDirectory, defaultAgent.Name.Value, parts[0],
            parts.Length > 1 ? parts[1] : config.EffectiveModel).ConfigureAwait(false);
        if (sessionResult.IsFailure)
        {
            _logger.LogError("Session creation failed: {Error}", sessionResult.Error);
            await _renderer.WriteLineAsync($"Failed: {sessionResult.Error}").ConfigureAwait(false);
            return 1;
        }
        _agent.Initialize(sessionResult.Value, defaultAgent);
        _logger.LogInformation("Agent initialized: session={SessionId}, agent={Agent}", sessionResult.Value.Id, defaultAgent.Name.Value);

        if (_renderer is IInteractiveTuiRenderer interactive)
        {
            _logger.LogInformation("Interactive renderer detected — entering interactive loop");
            int? slashExitCode = null;
            interactive.SetSlashHandler(async raw =>
            {
                SlashCommandOutcome outcome = await _slashes.HandleAsync(
                    raw, _renderer, _agent, _agentRegistry, _configStore, _authStore, _providers, sessionResult.Value).ConfigureAwait(false);
                if (outcome.ShouldQuit)
                {
                    slashExitCode = outcome.ExitCode;
                }
            });
            int exitCode = await interactive.RunInteractiveAsync(_agent, _rendererHost).ConfigureAwait(false);
            if (slashExitCode is int quitCode)
            {
                _logger.LogInformation("Interactive loop ended via /exit with code {ExitCode}", quitCode);
                return quitCode;
            }
            _logger.LogInformation("Interactive loop ended with exit code {ExitCode}", exitCode);
            return exitCode;
        }

        _logger.LogDebug("Renderer initialized, subscribing to event bus");
        _eventBus.Subscribe(async (evt, c) => await _renderer.RenderAsync(evt, c).ConfigureAwait(false));

        _logger.LogInformation("Non-interactive renderer — entering line REPL");
        int lineExitCode = await RunLineReplAsync(sessionResult.Value).ConfigureAwait(false);
        _logger.LogInformation("Line REPL ended with exit code {ExitCode}", lineExitCode);
        return lineExitCode;
    }

    /// <summary>
    ///     CE-4: сборка и запуск CellForge-REPL. Сессия создаётся только после
    ///     успешного входа в raw-режим, чтобы откат на legacy не оставлял
    ///     осиротевших сессий.
    /// </summary>
    private async Task<Result<int>> RunCellForgeAsync(CancellationToken ct)
    {
        var modeController = CreateModeController();
        // Deferred: screens/stdin resolve only on the CellForge path so the
        // legacy path never pays for (or disturbs) stdin ownership.
        var screens = _cellForgeScreens();
        var configResult = await _configStore.LoadAsync().ConfigureAwait(false);
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

        var defaultAgent = _agentRegistry.GetAllAgents().FirstOrDefault(a => a.Name.Value == config.Agent)
                           ?? _agentRegistry.GetAllAgents()[0];
        string[] parts = config.EffectiveModel.Split('/', 2);
        _logger.LogInformation("CellForge: creating session agent={Agent}, provider={Provider}, model={Model}",
            defaultAgent.Name.Value, parts[0], parts.Length > 1 ? parts[1] : config.EffectiveModel);
        var sessionResult = await _sessionStore.CreateAsync(
            Environment.CurrentDirectory, defaultAgent.Name.Value, parts[0],
            parts.Length > 1 ? parts[1] : config.EffectiveModel).ConfigureAwait(false);
        if (sessionResult.IsFailure)
        {
            return Result.Failure<int>(sessionResult.Error);
        }

        _agent.Initialize(sessionResult.Value, defaultAgent);

        var runner = new CellForgeReplRunner(
            _configStore,
            _providers,
            _agentRegistry,
            _authStore,
            _sessionStore,
            _rendererPipeline,
            _eventBus,
            _tokens,
            new LegacySlashRunner(
                _slashes,
                _agentRegistry,
                _configStore,
                _authStore,
                _providers),
            _agent,
            sessionResult.Value,
            screens.Session,
            screens.Screen,
            screens.Bridge,
            screens.Input,
            modeController,
            screens.Backend,
            _cellForgeLogger,
            screens.Coordinator);
        int exitCode = await runner.RunAsync(ct).ConfigureAwait(false);
        return Result.Success(exitCode);
    }

    private static ITerminalModeController CreateModeController() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new WindowsVtModeController()
            : new UnixTermiosModeController();

    public async Task<int> RunAskAsync(string prompt)
    {
        _logger.LogInformation("Ask mode: prompt={Prompt}", prompt.Length > 80 ? prompt[..80] + "..." : prompt);

        await _renderer.InitializeAsync().ConfigureAwait(false);
        _eventBus.Subscribe(async (evt, c) => await _renderer.RenderAsync(evt, c).ConfigureAwait(false));

        var config = (await _configStore.LoadAsync().ConfigureAwait(false)).Value;
        var defaultAgent = _agentRegistry.GetAllAgents().FirstOrDefault(a => a.Name.Value == config.Agent)
                           ?? _agentRegistry.GetAllAgents()[0];
        string[] parts = config.EffectiveModel.Split('/', 2);
        var sessionResult = await _sessionStore.CreateAsync(
            Environment.CurrentDirectory, defaultAgent.Name.Value, parts[0],
            parts.Length > 1 ? parts[1] : config.EffectiveModel).ConfigureAwait(false);
        if (sessionResult.IsFailure)
        {
            _logger.LogError("Session creation failed: {Error}", sessionResult.Error);
            Console.Error.WriteLine($"Failed: {sessionResult.Error}");
            return 1;
        }
        _agent.Initialize(sessionResult.Value, defaultAgent);
        _logger.LogInformation("Agent initialized, sending prompt");
        var result = await _agent.PromptAsync(prompt).ConfigureAwait(false);
        _logger.LogInformation("Ask completed: success={Success}", result.IsSuccess);
        return result.IsSuccess ? 0 : 1;
    }

    private async Task<int> RunLineReplAsync(Session session)
    {
        _logger.LogInformation("Line REPL starting");
        while (true)
        {
            var inputResult = await _renderer.ReadLineAsync("> ").ConfigureAwait(false);
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
                SlashCommandOutcome outcome = await _slashes.HandleAsync(
                    trimmed, _renderer, _agent, _agentRegistry, _configStore, _authStore, _providers, session).ConfigureAwait(false);
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
            await _agent.PromptAsync(trimmed).ConfigureAwait(false);
        }

        _logger.LogInformation("Line REPL ended");
        return 0;
    }
}

/// <summary>
///     Deferred CellForge screen graph (#63): the composition root supplies
///     this as a <c>Func</c> so stdin/screens resolve only when CellForge mode
///     is actually entered — the legacy/ask paths never touch them.
/// </summary>
internal sealed record CellForgeScreens(
    ScreenSession Session,
    ChatScreen Screen,
    ChatScreenBridge Bridge,
    TerminalInputSource Input,
    ITerminalBackend Backend,
    IApprovalCoordinator Coordinator);
