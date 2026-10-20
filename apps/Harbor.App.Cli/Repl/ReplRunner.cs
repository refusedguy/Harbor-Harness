using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Configuration;
using Harbor.App.Cli.Hosting;
using Harbor.Terminal.Abstractions;
using Harbor.Tui.CellForge.Input;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Streaming;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.Projection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
namespace Harbor.App.Cli.Repl;
/// <summary>
///     REPL and interactive session runner — single responsibility: run the user interaction loop.
///     Extracted from Program.cs. All singletons arrive via ctor (#63); the
///     CellForge screen graph resolves lazily through <see cref="CellForgeScreens" />
///     so the legacy path never touches stdin/screens.
/// </summary>
/// <remarks>
///     <para>
///         <b>#486 — the constructor is a field-assignment list and nothing else.</b> It declared
///         21 parameters, four of which (<c>tools</c>, <c>permissions</c>, <c>skillRefresh</c>,
///         <c>skillUpdate</c>) were read exactly once each, as arguments to a
///         <c>new SlashCommandDispatcher(…)</c> inside the constructor. They were never stored in
///         a field and never passed on: not dependencies of this class, but values it resolved for
///         a collaborator and then carried in its own signature. The slash layer is built once at
///         the composition root now — which is the method that has the container in scope to
///         resolve its nine collaborators from — and arrives here as a parameter. Seventeen of the
///         twenty-one were real dependencies and are unchanged; the eighteen that remain are those
///         seventeen plus the dispatcher.
///     </para>
///     <para>
///         <c>ILoggerFactory</c> is the one composition left here, and it cannot be removed by
///         injection: this class builds <see cref="CellForgeReplRunner" /> and needs
///         <c>ILogger&lt;CellForgeReplRunner&gt;</c>, which S6672 forbids a class from holding
///         for a type it does not own. The repo works around S6672 the same way twice already
///         (<c>ToolDispatcher</c>, <c>AgentLoop</c>). The guard encodes that as a single named
///         exception rather than leaving it to judgement.
///     </para>
///     <para>
///         The <c>ReplContext</c> the issue proposed instead would have bundled all twenty-one
///         behind one name, which MOVES the four masked defaults rather than deleting them: one
///         name instead of twenty-one, the same twenty-one values, four of which should never have
///         been supplied. Guarded by
///         <c>tests/Harbor.Architecture.Tests/ReplConstructorCompositionTests.cs</c>.
///     </para>
///     <para>
///         <b>And the wiring the dispatcher's move left behind is now gone too.</b>
///         <c>RunCellForgeAsync</c> used to <c>new LegacySlashRunner(_slashes, _agentRegistry,
///         _configStore, _authStore, _providers)</c> — five registered services re-wired by hand
///         inside the consumer that serves them, which is finding 2's shape one level below the
///         place finding 2 was fixed. It is built at the composition root beside the dispatcher and
///         arrives as <c>legacySlash</c>, so <c>legacySlash</c> is a nineteenth parameter and the
///         count of wirings inside a consumer is zero. The count is the symptom, not the finding:
///         one fewer <c>new</c> of a container-owned type is worth one more name in a signature that
///         already says what this class depends on.
///     </para>
/// </remarks>
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
    private readonly LegacySlashRunner _legacySlash;
    private readonly Harbor.Hosting.Rendering.IRendererPipeline? _rendererPipeline;
    private readonly Harbor.Hosting.PluginReloadService? _pluginReload;
    private readonly IProviderHealthCheck? _healthCheck;
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
        SlashCommandDispatcher slashes,
        LegacySlashRunner legacySlash,
        ILoggerFactory loggerFactory,
        Harbor.Hosting.PluginReloadService? pluginReload,
        Harbor.Hosting.Rendering.IRendererPipeline? rendererPipeline,
        ITokenTracker? tokens,
        Func<CellForgeScreens> cellForgeScreens,
        IServiceProvider rendererHost,
        IProviderHealthCheck? healthCheck = null)
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
        _slashes = slashes;
        _legacySlash = legacySlash;
        _cellForgeLogger = loggerFactory.CreateLogger<CellForgeReplRunner>();
        _rendererPipeline = rendererPipeline;
        _pluginReload = pluginReload;
        _healthCheck = healthCheck;
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
        //
        // ONE read, ONE decision (#602). `IConfigStore.LoadAsync` really does
        // fail: `JsonConfigStore.LoadCore` returns
        // `Failure("config.json is corrupt: …")` on a JsonException, and a
        // hand-edited config.json with a trailing comma is the ordinary trigger.
        // This method used to spell that rule three different ways — two sites
        // read `(await LoadAsync()).Value`, which THROWS ResultFailureException,
        // and the third read `IsSuccess ? Value : Default`, which throws nothing
        // and is worse: it quietly starts a session on the default provider and
        // model, which is not the one the user configured. So the load error is
        // reported once, with the parser's own composed message, and the run
        // stops. The renderer is initialized first because that is the only
        // channel available before the REPL starts.
        var configResult = await _configStore.LoadAsync().ConfigureAwait(false);
        if (configResult.IsFailure)
        {
            ReplRunnerLog.ConfigLoadFailed(_logger, configResult.Error);
            await _renderer.InitializeAsync().ConfigureAwait(false);
            await _renderer.WriteLineAsync($"Failed: {configResult.Error}").ConfigureAwait(false);
            return 1;
        }

        // Read through the IsSuccess ternary rather than a bare `.Value`. CFE0001
        // models this shape and does NOT model the early-return guard above —
        // docs/ROP-API-INVENTORY.md §5.4 lists that shape among the false
        // positives that need a pragma — and a guard this file would then depend
        // on is not a guard. The default arm is unreachable in practice: the
        // failure already returned above. It exists to satisfy the analyzer, NOT
        // to paper over a failed load.
        HarborConfig config = configResult.IsSuccess ? configResult.Value : HarborConfig.Default;
        // PX2 slice 2 (issue #1248): first-run (!Onboarded) enters CellForge
        // with the setup overlay up. The legacy diversion survives only behind
        // HARBOR_LEGACY_SETUP=1 — see TuiMode.ShouldEnterCellForge.
        if (TuiMode.ShouldEnterCellForge(config.Ui.CellForge.Enabled, config.Onboarded))
        {
            if (!config.Onboarded)
            {
                ReplRunnerLog.CellForgeFirstRunSetupOverlay(_logger);
            }

            var consoleResult = await RunCellForgeAsync(config, ct).ConfigureAwait(false);
            if (consoleResult.IsSuccess)
            {
                return consoleResult.Value;
            }

            ReplRunnerLog.CellForgeUnavailableFallback(_logger, consoleResult.Error);
        }
        else if (TuiMode.IsCellForgeSelected() && !config.Ui.CellForge.Enabled)
        {
            ReplRunnerLog.CellForgeSelectedButDisabled(_logger);
        }
        else if (TuiMode.IsCellForgeSelected() && !config.Onboarded)
        {
            ReplRunnerLog.CellForgeDeferredLegacySetup(_logger);
        }

        ReplRunnerLog.InteractiveReplStarting(_logger, _renderer.GetType().Name);
        await _renderer.InitializeAsync().ConfigureAwait(false);

        ReplRunnerLog.ConfigLoaded(_logger,
            config.EffectiveProvider, config.EffectiveModel, config.Agent, config.Onboarded);

        if (!config.Onboarded)
        {
            ReplRunnerLog.OnboardingLaunchingWizard(_logger);
            var writer = (Action<string>)(msg => _ = _renderer.WriteLineAsync(msg));
            var reader = (Func<string, Task<string>>)(async prompt =>
            {
                var r = await _renderer.ReadLineAsync(prompt).ConfigureAwait(false);
                return r.GetValueOrDefault(string.Empty);
            });
            var wizardResult = await _wizard.RunAsync(reader, writer).ConfigureAwait(false);
            if (wizardResult.IsFailure)
            {
                ReplRunnerLog.OnboardingWizardFailed(_logger, wizardResult.Error);
                await _renderer.WriteLineAsync($"Setup failed: {wizardResult.Error}").ConfigureAwait(false);
                return 1;
            }
            // The wizard claims to have written the file; re-read it so the
            // session runs on what was actually persisted. Guarded, unlike the
            // bare `.Value` this replaces — that threw ResultFailureException
            // out of the REPL when the post-wizard write did not land (#602).
            // A reload that fails is NOT survivable: continuing would start the
            // session on `HarborConfig.Default` and throw away the answer the
            // user just gave the wizard.
            var reloadResult = await _configStore.LoadAsync().ConfigureAwait(false);
            if (reloadResult.IsFailure)
            {
                ReplRunnerLog.ConfigReloadAfterOnboardingFailed(_logger, reloadResult.Error);
                await _renderer.WriteLineAsync($"Setup failed: {reloadResult.Error}").ConfigureAwait(false);
                return 1;
            }

            config = reloadResult.IsSuccess ? reloadResult.Value : HarborConfig.Default;
            ReplRunnerLog.OnboardingCompleted(_logger);
        }

        if (_renderer is not IInteractiveTuiRenderer)
        {
            ReplRunnerLog.NonInteractiveShowingHeader(_logger);
            await _renderer.WriteLineAsync("Harbor — modular AI coding agent").ConfigureAwait(false);
            await _renderer.WriteLineAsync($"Provider: {config.EffectiveProvider} | Model: {config.EffectiveModel} | Agent: {config.Agent}").ConfigureAwait(false);
            await _renderer.WriteLineAsync("Type '/help' for commands, '/exit' to quit.").ConfigureAwait(false);
            await _renderer.WriteLineAsync(string.Empty).ConfigureAwait(false);
        }

        // Absent ⇒ the registry's first agent; an EMPTY registry is a reported
        // failure, not an index that throws (#602). `GetAllAgents()[0]` — which
        // this replaces — threw ArgumentOutOfRangeException on an empty list,
        // and an empty registry is exactly the state of a fresh install or of a
        // failed plugin-load pass. It also enumerated the registry twice.
        Maybe<AgentDefinition> resolvedAgent = FindRunAgent(config);
        if (resolvedAgent.HasNoValue)
        {
            ReplRunnerLog.NoAgentsRegistered(_logger);
            await _renderer.WriteLineAsync("No agents are registered — nothing to run.").ConfigureAwait(false);
            return 1;
        }

        AgentDefinition defaultAgent = resolvedAgent.Value;
        string[] parts = config.EffectiveModel.Split('/', 2);
        ReplRunnerLog.CreatingSession(_logger,
            defaultAgent.Name.Value, parts[0], parts.Length > 1 ? parts[1] : config.EffectiveModel);
        var sessionResult = await _sessionStore.CreateAsync(
            Environment.CurrentDirectory, defaultAgent.Name.Value, parts[0],
            parts.Length > 1 ? parts[1] : config.EffectiveModel).ConfigureAwait(false);
        if (sessionResult.IsFailure)
        {
            ReplRunnerLog.SessionCreationFailed(_logger, sessionResult.Error);
            await _renderer.WriteLineAsync($"Failed: {sessionResult.Error}").ConfigureAwait(false);
            return 1;
        }
        _agent.Initialize(sessionResult.Value, defaultAgent);
        ReplRunnerLog.AgentInitialized(_logger, sessionResult.Value.Id, defaultAgent.Name.Value);
        (_renderer as BaseTuiRenderer)?.SetActiveAgent(defaultAgent.Name.Value);

        if (_renderer is IInteractiveTuiRenderer interactive)
        {
            ReplRunnerLog.InteractiveRendererEnteringLoop(_logger);
            int? slashExitCode = null;
            interactive.SetSlashHandler(async raw =>
            {
                SlashCommandOutcome outcome = await _slashes.HandleAsync(
                    #pragma warning disable CFE0001
                    // CFE0001 baseline: docs/ROP-API-INVENTORY.md 5.
                    // Guard upstream is an early-return guard..
                    // The .Value is read inside a slash-handler lambda, further from the guard than the
                    // analyzer's body walk models.
                    raw, _renderer, _agent, _agentRegistry, _configStore, _authStore, _providers, sessionResult.Value).ConfigureAwait(false);
                    #pragma warning restore CFE0001
                if (outcome.ShouldQuit)
                {
                    slashExitCode = outcome.ExitCode;
                }
            });
            int exitCode = await interactive.RunInteractiveAsync(_agent, _rendererHost).ConfigureAwait(false);
            if (slashExitCode is int quitCode)
            {
                ReplRunnerLog.InteractiveLoopEndedViaExit(_logger, quitCode);
                return quitCode;
            }
            ReplRunnerLog.InteractiveLoopEnded(_logger, exitCode);
            return exitCode;
        }

        ReplRunnerLog.RendererInitializedSubscribing(_logger);
        _eventBus.Subscribe(async (evt, c) => await _renderer.RenderAsync(evt, c).ConfigureAwait(false));

        ReplRunnerLog.EnteringLineRepl(_logger);
        int lineExitCode = await RunLineReplAsync(sessionResult.Value).ConfigureAwait(false);
        ReplRunnerLog.LineReplEndedWithExitCode(_logger, lineExitCode);
        return lineExitCode;
    }

    /// <summary>
    ///     CE-4: сборка и запуск CellForge-REPL. Сессия создаётся только после
    ///     успешного входа в raw-режим, чтобы откат на legacy не оставлял
    ///     осиротевших сессий.
    /// </summary>
    private async Task<Result<int>> RunCellForgeAsync(HarborConfig config, CancellationToken ct)
    {
        // #1249 bring-up slice 2: Windows is a first-class CellForge platform,
        // so the only platform gate left is the opt-out below. The legacy
        // fallback that survives is the capability one — a Windows run whose
        // raw-mode probe genuinely fails (redirected stdin, a console that
        // refuses SetConsoleMode) is reported by the probe block below and
        // turned into the logged fallback, never a crash.
        if (TuiMode.IsCellForgeBlockedOnThisOs())
        {
            return Result.Failure<int>(
                "CellForge on Windows is opted out (HARBOR_CELLFORGE_WINDOWS=0) — "
                + "unset it to use the Windows VT raw-mode controller.");
        }

        var modeController = CreateModeController();
        // Deferred: screens/stdin resolve only on the CellForge path so the
        // legacy path never pays for (or disturbs) stdin ownership.
        var screens = _cellForgeScreens();

        try
        {
            modeController.Enter();
            modeController.Restore(); // the runner re-enters inside its own lifetime
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException)
        {
            return Result.Failure<int>($"raw mode unavailable: {ex.Message}");
        }

        // Absent ⇒ the registry's first agent; an empty registry is a reported
        // failure, not `GetAllAgents()[0]` (#602).
        Maybe<AgentDefinition> resolvedAgent = FindRunAgent(config);
        if (resolvedAgent.HasNoValue)
        {
            ReplRunnerLog.NoAgentsCellForgeNotCreated(_logger);
            return Result.Failure<int>("no agents are registered");
        }

        AgentDefinition defaultAgent = resolvedAgent.Value;
        string[] parts = config.EffectiveModel.Split('/', 2);
        ReplRunnerLog.CellForgeCreatingSession(_logger,
            defaultAgent.Name.Value, parts[0], parts.Length > 1 ? parts[1] : config.EffectiveModel);
        var sessionResult = await _sessionStore.CreateAsync(
            Environment.CurrentDirectory, defaultAgent.Name.Value, parts[0],
            parts.Length > 1 ? parts[1] : config.EffectiveModel).ConfigureAwait(false);
        if (sessionResult.IsFailure)
        {
            return sessionResult.ConvertFailure<int>();
        }

        _agent.Initialize(sessionResult.Value, defaultAgent);

        var runner = new CellForgeReplRunner(
            new CellForgeCoreServices(
                _configStore,
                _providers,
                _agentRegistry,
                _authStore,
                _eventBus,
                _agent,
                // #486: the adapter the root built beside the dispatcher. It used to be
                // `new LegacySlashRunner(_slashes, _agentRegistry, _configStore,
                // _authStore, _providers)` written out right here — composition in a
                // consumer, of a type whose five collaborators this class already
                // holds. Built once, where the container is.
                _legacySlash,
                _cellForgeLogger),
            new CellForgeOptionalServices(
                _sessionStore,
                _rendererPipeline,
                _tokens,
                _pluginReload,
                _healthCheck,
                _rendererHost.GetService<Harbor.Ui.Framework.Panels.IPanelRegistry>(),
                // #674: the headless core owns diagnostic classification; the REPL is
                // only the pipe that carries its snapshot to the store. Null in a host
                // that registers no aggregator, which leaves the panel honestly empty.
                _rendererHost.GetService<Harbor.Application.Diagnostics.DiagnosticsAggregator>(),
                // #857: the /jump palette's worktree column. TuiModule registers
                // IGitQuery for the CLI (same reason it registers the file-tree
                // seam — the consumer is a TUI surface, and the CLI is the default
                // CellForge backend), so this resolves on the normal path.
                _rendererHost.GetService<Harbor.Abstractions.Git.IGitQuery>()),
            screens,
            sessionResult.Value,
            modeController);
        int exitCode = await runner.RunAsync(ct).ConfigureAwait(false);
        return Result.Success(exitCode);
    }

    /// <summary>
    ///     The platform's raw-mode controller. The selection itself lives in
    ///     <c>TerminalModeControllers</c> (#1249 slice 2) so every OS — the
    ///     Linux CI runners included — can assert which controller it is
    ///     handed; the composition root stays the only place that constructs
    ///     one.
    /// </summary>
    private static ITerminalModeController CreateModeController() =>
        TerminalModeControllers.CreateForCurrentPlatform();

    /// <summary>
    ///     The agent this run should use: the one <paramref name="config" /> names,
    ///     else the registry's first. Absence is <see cref="Maybe{T}.None" />, not an
    ///     index.
    /// </summary>
    /// <remarks>
    ///     The registry is read ONCE. Every caller previously wrote
    ///     <c>GetAllAgents()[0]</c> as the fallback arm, which throws
    ///     <see cref="ArgumentOutOfRangeException" /> when the registry is empty —
    ///     the state of a fresh install, and of a failed plugin-load pass — and
    ///     enumerated the registry a second time to do it (#602).
    ///     <para />
    ///     The resolved agent carries the step budget the configuration asks
    ///     for (#1118). The registry's builtin agents already carry it — the
    ///     composition root projects it onto them — but this is the seam where
    ///     the run is BOUND to the agent it is about to execute, so the budget
    ///     is applied from the config this run just read: it covers an agent a
    ///     plugin contributed (the plugin path registers straight into the
    ///     registry, past the composition root) and it honours a config
    ///     edited between composition and this run. Applying it twice is a
    ///     no-op — the same value lands on the same axis.
    /// </remarks>
    private Maybe<AgentDefinition> FindRunAgent(HarborConfig config)
    {
        IReadOnlyList<AgentDefinition> agents = _agentRegistry.GetAllAgents();
        Maybe<AgentDefinition> resolved = agents
            .TryFirst(a => a.Name.Value == config.Agent)
            .Or(() => agents.TryFirst());
        if (resolved.HasNoValue)
        {
            return resolved;
        }

        return Maybe.From(AgentStepBudget.Apply(config, resolved.Value));
    }

    public async Task<int> RunAskAsync(string prompt)
    {
        ReplRunnerLog.AskModePrompt(_logger, prompt.Length > 80 ? prompt[..80] + "..." : prompt);

        await _renderer.InitializeAsync().ConfigureAwait(false);
        _eventBus.Subscribe(async (evt, c) => await _renderer.RenderAsync(evt, c).ConfigureAwait(false));

        // `harbor ask` has no wizard and no interactive recovery, so a config it
        // cannot read is a hard stop with the parser's message on stderr. This
        // is the #602 crash site: the previous `(await LoadAsync()).Value` threw
        // ResultFailureException out of the single most-run command in the
        // harness, and the diagnostic JsonConfigStore had already composed
        // ("config.json is corrupt: …") was thrown away with it.
        var configResult = await _configStore.LoadAsync().ConfigureAwait(false);
        if (configResult.IsFailure)
        {
            ReplRunnerLog.AskConfigLoadFailed(_logger, configResult.Error);
            Console.Error.WriteLine($"Failed: {configResult.Error}");
            return 1;
        }

        // IsSuccess ternary, not a bare `.Value` — see the note in
        // RunInteractiveAsync. The default arm is unreachable: the failure above
        // already returned. It is NOT a silent fallback to the default provider.
        HarborConfig config = configResult.IsSuccess ? configResult.Value : HarborConfig.Default;

        // Absent ⇒ the registry's first agent; an empty registry is a reported
        // failure rather than `GetAllAgents()[0]`, which threw on it (#602).
        Maybe<AgentDefinition> resolvedAgent = FindRunAgent(config);
        if (resolvedAgent.HasNoValue)
        {
            ReplRunnerLog.AskNoAgentsRegistered(_logger);
            Console.Error.WriteLine("No agents are registered — nothing to run.");
            return 1;
        }

        AgentDefinition defaultAgent = resolvedAgent.Value;
        string[] parts = config.EffectiveModel.Split('/', 2);
        var sessionResult = await _sessionStore.CreateAsync(
            Environment.CurrentDirectory, defaultAgent.Name.Value, parts[0],
            parts.Length > 1 ? parts[1] : config.EffectiveModel).ConfigureAwait(false);
        if (sessionResult.IsFailure)
        {
            ReplRunnerLog.AskSessionCreationFailed(_logger, sessionResult.Error);
            Console.Error.WriteLine($"Failed: {sessionResult.Error}");
            return 1;
        }
        _agent.Initialize(sessionResult.Value, defaultAgent);
        (_renderer as BaseTuiRenderer)?.SetActiveAgent(defaultAgent.Name.Value);
        ReplRunnerLog.AskAgentInitializedSendingPrompt(_logger);
        var result = await _agent.PromptAsync(prompt).ConfigureAwait(false);
        ReplRunnerLog.AskCompleted(_logger, result.IsSuccess);
        return result.IsSuccess ? 0 : 1;
    }

    /// <summary>
    ///     Line-buffered REPL loop for renderers that are not
    ///     <see cref="IInteractiveTuiRenderer" />. Internal so the end-of-input
    ///     contract is testable (#589) — this loop is what spun at 100% CPU on
    ///     Ctrl-D and on any exhausted pipe, and a hang is only observable by
    ///     driving the loop itself. The contract itself is pinned separately in
    ///     <c>ReadLineAbsenceContractTests</c> and <c>TuiReadLineContractRules</c>.
    /// </summary>
    internal async Task<int> RunLineReplAsync(Session session)
    {
        ReplRunnerLog.LineReplStarting(_logger);
        while (true)
        {
            var inputResult = await _renderer.ReadLineAsync("> ").ConfigureAwait(false);

            // Maybe.None is end of input — EOF, Ctrl-D, exhausted/closed stdin, or a
            // renderer that cannot read. Leaving the loop is the whole point: this used
            // to be Result.Success(""), which is indistinguishable from a blank
            // submission, so the blank-line `continue` below spun at 100% CPU forever
            // on a closed stdin (#589).
            if (inputResult.HasNoValue)
            {
                ReplRunnerLog.LineReplEndOfInput(_logger);
                break;
            }

            string input = inputResult.Value;
            if (string.IsNullOrWhiteSpace(input))
            {
                continue;
            }
            string trimmed = input.Trim();

            // The exit words come from ChatCommands — the one list the reducer and the
            // desktop app read (#684). This used to be a literal pattern
            // (`trimmed is "exit" or "quit" or ":q"`), which compares ordinally: `QUIT`
            // quit the TUI and did not quit here, falling through to PromptAsync and
            // billing the word to the model as a prompt. Case-insensitive is the correct
            // reading, not the pattern's: SlashCommandCatalog resolves `/EXIT` with
            // OrdinalIgnoreCase and autocomplete offers `/exit` for the prefix `EXI`, so
            // rejecting the word the completion just offered was the inconsistency.
            if (Harbor.Ui.Framework.State.ChatCommands.ExitWords.Contains(trimmed))
            {
                ReplRunnerLog.LineReplUserRequestedExit(_logger);
                break;
            }

            if (trimmed.StartsWith('/'))
            {
                ReplRunnerLog.LineReplSlashCommand(_logger, trimmed);
                SlashCommandOutcome outcome = await _slashes.HandleAsync(
                    trimmed, _renderer, _agent, _agentRegistry, _configStore, _authStore, _providers, session).ConfigureAwait(false);
                if (outcome.ShouldQuit)
                {
                    // Managed shutdown: returning the code lets Program run its
                    // normal cleanup (IPC stop, host dispose) before exiting.
                    ReplRunnerLog.LineReplQuitViaSlash(_logger, outcome.ExitCode);
                    return outcome.ExitCode;
                }
                continue;
            }

            ReplRunnerLog.LineReplUserPrompt(_logger, trimmed.Length);
            await _agent.PromptAsync(trimmed).ConfigureAwait(false);
        }

        ReplRunnerLog.LineReplEnded(_logger);
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

/// <summary>
///     SG9: BCL <c>[LoggerMessage]</c> delegates for <see cref="ReplRunner" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class ReplRunnerLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Config load failed: {Error}")]
    public static partial void ConfigLoadFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "CellForge first-run: onboarding не завершён — вход с setup-оверлеем")]
    public static partial void CellForgeFirstRunSetupOverlay(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "CellForge недоступен ({Reason}) — откат на legacy-рендер")]
    public static partial void CellForgeUnavailableFallback(ILogger logger, string reason);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "CellForge выбран (tui/env), но ui.consoleEx.enabled=false — используется legacy-рендер")]
    public static partial void CellForgeSelectedButDisabled(ILogger logger);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "CellForge отложен: onboarding не завершён и HARBOR_LEGACY_SETUP=1 — мастер требует legacy-рендер")]
    public static partial void CellForgeDeferredLegacySetup(ILogger logger);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information, Message = "Interactive REPL starting — renderer={RendererType}")]
    public static partial void InteractiveReplStarting(ILogger logger, string rendererType);

    [LoggerMessage(EventId = 7, Level = LogLevel.Information, Message = "Config loaded: provider={Provider}, model={Model}, agent={Agent}, onboarded={Onboarded}")]
    public static partial void ConfigLoaded(ILogger logger, string provider, string model, string agent, bool onboarded);

    [LoggerMessage(EventId = 8, Level = LogLevel.Information, Message = "Onboarding not complete — launching wizard")]
    public static partial void OnboardingLaunchingWizard(ILogger logger);

    [LoggerMessage(EventId = 9, Level = LogLevel.Error, Message = "Onboarding wizard failed: {Error}")]
    public static partial void OnboardingWizardFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 10, Level = LogLevel.Error, Message = "Config reload after onboarding failed: {Error}")]
    public static partial void ConfigReloadAfterOnboardingFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 11, Level = LogLevel.Information, Message = "Onboarding completed, config reloaded")]
    public static partial void OnboardingCompleted(ILogger logger);

    [LoggerMessage(EventId = 12, Level = LogLevel.Debug, Message = "Non-interactive renderer — showing header text")]
    public static partial void NonInteractiveShowingHeader(ILogger logger);

    [LoggerMessage(EventId = 13, Level = LogLevel.Error, Message = "No agents are registered — nothing to run.")]
    public static partial void NoAgentsRegistered(ILogger logger);

    [LoggerMessage(EventId = 14, Level = LogLevel.Information, Message = "Creating session: agent={Agent}, provider={Provider}, model={Model}")]
    public static partial void CreatingSession(ILogger logger, string agent, string provider, string model);

    [LoggerMessage(EventId = 15, Level = LogLevel.Error, Message = "Session creation failed: {Error}")]
    public static partial void SessionCreationFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 16, Level = LogLevel.Information, Message = "Agent initialized: session={SessionId}, agent={Agent}")]
    public static partial void AgentInitialized(ILogger logger, string sessionId, string agent);

    [LoggerMessage(EventId = 17, Level = LogLevel.Information, Message = "Interactive renderer detected — entering interactive loop")]
    public static partial void InteractiveRendererEnteringLoop(ILogger logger);

    [LoggerMessage(EventId = 18, Level = LogLevel.Information, Message = "Interactive loop ended via /exit with code {ExitCode}")]
    public static partial void InteractiveLoopEndedViaExit(ILogger logger, int exitCode);

    [LoggerMessage(EventId = 19, Level = LogLevel.Information, Message = "Interactive loop ended with exit code {ExitCode}")]
    public static partial void InteractiveLoopEnded(ILogger logger, int exitCode);

    [LoggerMessage(EventId = 20, Level = LogLevel.Debug, Message = "Renderer initialized, subscribing to event bus")]
    public static partial void RendererInitializedSubscribing(ILogger logger);

    [LoggerMessage(EventId = 21, Level = LogLevel.Information, Message = "Non-interactive renderer — entering line REPL")]
    public static partial void EnteringLineRepl(ILogger logger);

    [LoggerMessage(EventId = 22, Level = LogLevel.Information, Message = "Line REPL ended with exit code {ExitCode}")]
    public static partial void LineReplEndedWithExitCode(ILogger logger, int exitCode);

    [LoggerMessage(EventId = 23, Level = LogLevel.Error, Message = "No agents are registered — CellForge session not created.")]
    public static partial void NoAgentsCellForgeNotCreated(ILogger logger);

    [LoggerMessage(EventId = 24, Level = LogLevel.Information, Message = "CellForge: creating session agent={Agent}, provider={Provider}, model={Model}")]
    public static partial void CellForgeCreatingSession(ILogger logger, string agent, string provider, string model);

    [LoggerMessage(EventId = 25, Level = LogLevel.Information, Message = "Ask mode: prompt={Prompt}")]
    public static partial void AskModePrompt(ILogger logger, string prompt);

    [LoggerMessage(EventId = 26, Level = LogLevel.Error, Message = "Config load failed: {Error}")]
    public static partial void AskConfigLoadFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 27, Level = LogLevel.Error, Message = "No agents are registered — nothing to run.")]
    public static partial void AskNoAgentsRegistered(ILogger logger);

    [LoggerMessage(EventId = 28, Level = LogLevel.Error, Message = "Session creation failed: {Error}")]
    public static partial void AskSessionCreationFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 29, Level = LogLevel.Information, Message = "Agent initialized, sending prompt")]
    public static partial void AskAgentInitializedSendingPrompt(ILogger logger);

    [LoggerMessage(EventId = 30, Level = LogLevel.Information, Message = "Ask completed: success={Success}")]
    public static partial void AskCompleted(ILogger logger, bool success);

    [LoggerMessage(EventId = 31, Level = LogLevel.Information, Message = "Line REPL starting")]
    public static partial void LineReplStarting(ILogger logger);

    [LoggerMessage(EventId = 32, Level = LogLevel.Information, Message = "End of input — line REPL exiting")]
    public static partial void LineReplEndOfInput(ILogger logger);

    [LoggerMessage(EventId = 33, Level = LogLevel.Information, Message = "User requested exit")]
    public static partial void LineReplUserRequestedExit(ILogger logger);

    [LoggerMessage(EventId = 34, Level = LogLevel.Debug, Message = "Slash command: {Command}")]
    public static partial void LineReplSlashCommand(ILogger logger, string command);

    [LoggerMessage(EventId = 35, Level = LogLevel.Information, Message = "Quit requested via slash command — REPL exiting with code {ExitCode}")]
    public static partial void LineReplQuitViaSlash(ILogger logger, int exitCode);

    [LoggerMessage(EventId = 36, Level = LogLevel.Debug, Message = "User prompt ({Length} chars)")]
    public static partial void LineReplUserPrompt(ILogger logger, int length);

    [LoggerMessage(EventId = 37, Level = LogLevel.Information, Message = "Line REPL ended")]
    public static partial void LineReplEnded(ILogger logger);
}
