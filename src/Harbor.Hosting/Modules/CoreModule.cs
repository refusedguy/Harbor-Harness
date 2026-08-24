using Harbor.Core.Onboarding;
using Harbor.Core.Resilience;
using Harbor.Core.Sessions;
using Harbor.Abstractions.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harbor.Hosting;

internal static class CoreModule
{
    /// <summary>
    ///     Core agent-loop services (TokenTracker → DefaultAgent) plus auth and
    ///     onboarding. App-specific app-config stores (CliConfig) stay with the
    ///     applications. The event bus instance comes from the context.
    /// </summary>
    internal static IServiceCollection AddHarborCore(
        this IServiceCollection services,
        HarborCompositionContext ctx)
    {
        ctx.Logger.LogInformation("Registering core services");

        services.AddSingleton<AuthStore>();
        services.AddSingleton<OnboardingWizard>();
        services.AddSingleton<ITokenTracker, TokenTracker>();
        services.AddSingleton<ISystemPromptBuilder>(sp => new SystemPromptBuilder(sp.GetRequiredService<ILogger<SystemPromptBuilder>>()));
        services.AddSingleton<MessageConverter>();
        services.AddSingleton<IRetryPolicy, RetryPolicy>();
        services.AddSingleton<IAgentLoop, AgentLoop>();
        services.AddSingleton<IAgent, DefaultAgent>();
        // Forward IAgentRunner → IAgent (canonical MS DI interface-forwarding pattern).
        services.AddSingleton<IAgentRunner>(sp => sp.GetRequiredService<IAgent>());

        // One event bus for the whole process: the same instance is visible to
        // the eager registries/plugins AND to the final container (the old CLI
        // built a second bus inside its temp provider — unified here).
        services.AddSingleton(ctx.EventBus);

        return services;
    }
}
