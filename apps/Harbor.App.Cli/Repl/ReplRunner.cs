using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Core.Configuration;
using Harbor.Core.Onboarding;
using Harbor.Terminal.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
namespace Harbor.Cli.Repl;
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
        _logger.LogDebug("Renderer initialized, subscribing to event bus");
        eventBus.Subscribe(async (evt, c) => await renderer.RenderAsync(evt, c).ConfigureAwait(false));

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
            interactive.SetSlashHandler(raw => dispatcher.HandleAsync(
                raw, sp, renderer, agent, agentRegistry, configStore, authStore, providers, sessionResult.Value));
            int exitCode = await interactive.RunInteractiveAsync(agent, sp).ConfigureAwait(false);
            _logger.LogInformation("Interactive loop ended with exit code {ExitCode}", exitCode);
            return exitCode;
        }

        _logger.LogInformation("Non-interactive renderer — entering line REPL");
        int lineExitCode = await RunLineReplAsync(renderer, agent, sp, configStore, authStore, providers, agentRegistry, sessionResult.Value).ConfigureAwait(false);
        _logger.LogInformation("Line REPL ended with exit code {ExitCode}", lineExitCode);
        return lineExitCode;
    }

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
                await dispatcher.HandleAsync(trimmed, sp, renderer, agent, agentRegistry, configStore, authStore, providers, session).ConfigureAwait(false);
                continue;
            }

            _logger.LogDebug("User prompt ({Length} chars)", trimmed.Length);
            var promptResult = await agent.PromptAsync(trimmed).ConfigureAwait(false);
            if (promptResult.IsFailure)
            {
                _logger.LogWarning("Prompt failed: {Error}", promptResult.Error);
                await renderer.WriteLineAsync($"Error: {promptResult.Error}").ConfigureAwait(false);
            }
        }

        _logger.LogInformation("Line REPL ended");
        return 0;
    }
}
