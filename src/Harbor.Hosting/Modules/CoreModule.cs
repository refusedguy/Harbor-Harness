using Harbor.Application.Agents;
using Harbor.Application.Onboarding;
using Harbor.Application.Resilience;
using Harbor.Application.Sessions;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Diagnostics;
using Harbor.Telemetry;
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
        // PROD-UI-0 З.2: cheap "test connection" probe shared by the CLI
        // wizard, the desktop onboarding VM and future model pickers.
        services.AddSingleton<Harbor.Abstractions.Providers.IProviderHealthCheck>(sp =>
            new Harbor.Application.Providers.ProviderHealthCheck(
                sp.GetRequiredService<Harbor.Abstractions.Providers.IProviderRegistry>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()
                    .CreateLogger<Harbor.Application.Providers.ProviderHealthCheck>()));
        services.AddSingleton<ITokenTracker, TokenTracker>();
        services.AddSingleton<ISystemPromptBuilder>(sp => new SystemPromptBuilder(sp.GetRequiredService<ILogger<SystemPromptBuilder>>()));
        services.AddSingleton<ISkillProvider, SkillProvider>();
        services.AddSingleton<MessageConverter>();
        services.AddSingleton<IRetryPolicy, RetryPolicy>();
        // ROP-C П.5: the loop depends on the IToolDispatcher seam; the concrete
        // dispatcher logs under its own category instead of borrowing the
        // AgentLoop's (ROP-C П.8).
        services.AddSingleton<IToolDispatcher>(sp => new ToolDispatcher(
            sp.GetRequiredService<IToolRegistry>(),
            sp.GetRequiredService<IPermissionService>(),
            sp.GetRequiredService<IEventBus>(),
            sp.GetRequiredService<ILogger<ToolDispatcher>>()));
        services.AddSingleton<IAgentLoop, AgentLoop>();
        services.AddSingleton<DefaultAgent>();
        // sprint3-C C1: IAgent consumers get the tracing proxy (agent.turn span,
        // turn metrics, ambient correlation scope); DefaultAgent stays resolvable
        // for code that must bypass telemetry.
        services.AddSingleton<IAgent>(sp => new TracingAgentProxy(
            sp.GetRequiredService<DefaultAgent>(),
            sp.GetRequiredService<IMetrics>(),
            sp.GetRequiredService<ITracer>()));
        // Forward IAgentRunner → IAgent (canonical MS DI interface-forwarding pattern).
        services.AddSingleton<IAgentRunner>(sp => sp.GetRequiredService<IAgent>());

        // One event bus for the whole process: the same instance is visible to
        // the eager registries/plugins AND to the final container (the old CLI
        // built a second bus inside its temp provider — unified here).
        services.AddSingleton(ctx.EventBus);

        return services;
    }
}
