using Harbor.Application.Agents;
using Harbor.Application.Agents.Pipeline;
using Harbor.Application.Diagnostics;
using Harbor.Application.Onboarding;
using Harbor.Application.Resilience;
using Harbor.Application.Sessions;
using Harbor.Abstractions.Permissions;
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
        services.AddSingleton<IToolRetryDecider, DefaultToolRetryDecider>();
        // ROP-C П.5: the loop depends on the IToolDispatcher seam; the concrete
        // dispatcher logs under its own category instead of borrowing the
        // AgentLoop's (ROP-C П.8).
        services.AddSingleton<IToolDispatcher>(sp => new ToolDispatcher(
            sp.GetRequiredService<IToolRegistry>(),
            sp.GetRequiredService<IPermissionService>(),
            sp.GetRequiredService<IEventBus>(),
            sp.GetRequiredService<ILogger<ToolDispatcher>>(),
            sp.GetRequiredService<IApprovalCoordinator>(),
            sp.GetRequiredService<IToolRetryDecider>()));
        // #480 A10: the run's cross-cutting behaviours are the CONTAINER's list.
        // AgentLoop used to spell them out as a literal in its own constructor,
        // which made a third run-level concern reachable only by editing the
        // class that runs the agent — an extension point that existed and could
        // not be delivered. Registration order is pipeline order, outermost
        // first: logging wraps permission.
        //
        // Each behaviour takes its OWN typed logger rather than borrowing the
        // loop's, which is the same call made for IToolDispatcher above
        // (ROP-C П.8) and the reason the product log category for these two is
        // `...Pipeline.LoggingBehavior` instead of `...Agents.AgentLoop`.
        services.AddSingleton<IPipelineBehavior>(sp => new LoggingBehavior(
            sp.GetRequiredService<ILogger<LoggingBehavior>>()));
        services.AddSingleton<IPipelineBehavior>(sp => new PermissionCheckBehavior(
            sp.GetRequiredService<ILogger<PermissionCheckBehavior>>()));
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

        // #674: the headless owner of every diagnostic, from both producers —
        // language servers (structured, via ILspService) and tool output
        // (pattern-detected, because no core API reports it). Registered as a
        // singleton so it is constructed ONCE per process: its snapshot is the
        // single answer every renderer draws from, and a second instance would
        // be a second, silent truth. ILspService is resolved optionally — a
        // minimal host without one still gets the tool-output half, which is
        // exactly why the rows carry their producer.
        services.AddSingleton(sp => new DiagnosticsAggregator(
            sp.GetRequiredService<IEventBus>(),
            sp.GetService<Harbor.Abstractions.Lsp.ILspService>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()
                .CreateLogger<DiagnosticsAggregator>()));

        return services;
    }
}
