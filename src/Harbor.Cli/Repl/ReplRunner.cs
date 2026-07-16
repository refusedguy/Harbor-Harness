using Microsoft.Extensions.DependencyInjection;
using Harbor.Tui.Abstractions;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Abstractions.Tui;
using Harbor.Core.Agents;
using Harbor.Core.Configuration;
using Harbor.Core.Onboarding;
using Harbor.Cli.Commands;
using Harbor.Cli.Hosting;

namespace Harbor.Cli.Repl;

/// <summary>
/// REPL and interactive session runner — single responsibility: run the user interaction loop.
/// Extracted from Program.cs.
/// </summary>
internal static class ReplRunner
{
    public static async Task<int> RunInteractiveAsync(IServiceProvider sp, CancellationToken ct = default)
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

        await renderer.InitializeAsync().ConfigureAwait(false);
        eventBus.Subscribe(async (evt, c) => await renderer.RenderAsync(evt, c).ConfigureAwait(false));

        var configResult = await configStore.LoadAsync().ConfigureAwait(false);
        var config = configResult.IsSuccess ? configResult.Value : HarborConfig.Default;

        if (!config.Onboarded)
        {
            var writer = (Action<string>)(msg => renderer.WriteLineAsync(msg).GetAwaiter().GetResult());
            var reader = (Func<string, Task<string>>)(async prompt =>
            {
                var r = await renderer.ReadLineAsync(prompt).ConfigureAwait(false);
                return r.IsSuccess ? r.Value : string.Empty;
            });
            var wizardResult = await wizard.RunAsync(reader, writer).ConfigureAwait(false);
            if (wizardResult.IsFailure)
            {
                await renderer.WriteLineAsync($"Setup failed: {wizardResult.Error}").ConfigureAwait(false);
                return 1;
            }
            config = (await configStore.LoadAsync().ConfigureAwait(false)).Value;
        }

        if (renderer is not IInteractiveTuiRenderer)
        {
            await renderer.WriteLineAsync("Harbor — modular AI coding agent").ConfigureAwait(false);
            await renderer.WriteLineAsync($"Provider: {config.Provider} | Model: {config.Model} | Agent: {config.Agent}").ConfigureAwait(false);
            await renderer.WriteLineAsync("Type '/help' for commands, '/exit' to quit.").ConfigureAwait(false);
            await renderer.WriteLineAsync(string.Empty).ConfigureAwait(false);
        }

        var defaultAgent = agentRegistry.GetAllAgents().FirstOrDefault(a => a.Name.Value == config.Agent)
                           ?? agentRegistry.GetAllAgents()[0];
        string[] parts = config.Model.Split('/', 2);
        var sessionResult = await sessionStore.CreateAsync(
            Environment.CurrentDirectory, defaultAgent.Name.Value, parts[0],
            parts.Length > 1 ? parts[1] : config.Model).ConfigureAwait(false);
        if (sessionResult.IsFailure)
        {
            await renderer.WriteLineAsync($"Failed: {sessionResult.Error}").ConfigureAwait(false);
            return 1;
        }
        agent.Initialize(sessionResult.Value, defaultAgent);

        if (renderer is IInteractiveTuiRenderer interactive)
        {
            interactive.SetSlashHandler(raw => SlashCommandDispatcher.HandleAsync(
                raw, sp, renderer, agent, agentRegistry, configStore, authStore, providers, sessionResult.Value));
            return await interactive.RunInteractiveAsync(agent, sp, ct: default).ConfigureAwait(false);
        }

        return await RunLineReplAsync(renderer, agent, sp, configStore, authStore, providers, agentRegistry, sessionResult.Value).ConfigureAwait(false);
    }

    public static async Task<int> RunAskAsync(IServiceProvider sp, string prompt)
    {
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
        string[] parts = config.Model.Split('/', 2);
        var sessionResult = await sessionStore.CreateAsync(
            Environment.CurrentDirectory, defaultAgent.Name.Value, parts[0],
            parts.Length > 1 ? parts[1] : config.Model).ConfigureAwait(false);
        if (sessionResult.IsFailure)
        {
            Console.Error.WriteLine($"Failed: {sessionResult.Error}");
            return 1;
        }
        agent.Initialize(sessionResult.Value, defaultAgent);
        var result = await agent.PromptAsync(prompt).ConfigureAwait(false);
        return result.IsSuccess ? 0 : 1;
    }

    private static async Task<int> RunLineReplAsync(
        ITuiRenderer renderer, IAgent agent, IServiceProvider sp,
        IConfigStore configStore, AuthStore authStore, IProviderRegistry providers,
        IAgentRegistry agentRegistry, Session session)
    {
        while (true)
        {
            var inputResult = await renderer.ReadLineAsync("> ").ConfigureAwait(false);
            if (inputResult.IsFailure) break;
            string? input = inputResult.Value;
            if (string.IsNullOrWhiteSpace(input)) continue;
            string trimmed = input.Trim();
            if (trimmed is "exit" or "quit" or ":q") break;
            if (trimmed.StartsWith('/'))
            {
                await SlashCommandDispatcher.HandleAsync(trimmed, sp, renderer, agent, agentRegistry, configStore, authStore, providers, session).ConfigureAwait(false);
                continue;
            }
            var promptResult = await agent.PromptAsync(trimmed).ConfigureAwait(false);
            if (promptResult.IsFailure)
                await renderer.WriteLineAsync($"Error: {promptResult.Error}").ConfigureAwait(false);
        }
        return 0;
    }
}
