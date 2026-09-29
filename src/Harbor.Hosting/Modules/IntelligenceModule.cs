using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Configuration;
using Harbor.Application.Permissions;
using Harbor.Application.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harbor.Hosting;

internal static class IntelligenceModule
{
    /// <summary>Compaction + permission services over the published registries.</summary>
    internal static IServiceCollection AddHarborIntelligence(
        this IServiceCollection services,
        HarborCompositionContext ctx)
    {
        services.AddSingleton<ICompactionService>(sp => new CompactionService(
            sp.GetRequiredService<ITokenTracker>(),
            sp.GetRequiredService<Harbor.Abstractions.Providers.IProviderRegistry>(),
            sp.GetRequiredService<ILogger<CompactionService>>(),
            ctx.Harbor.SecondaryModel));
        // #178: path-extraction policies (Strategy) — a new file-based tool
        // registers an IPathExtractionPolicy instead of editing the permission
        // core. Order matters: first Handles win, legacy fallback is terminal.
        services.AddSingleton<IPathExtractionPolicy>(PathArgExtractionPolicy.Instance);
        services.AddSingleton<IPathExtractionPolicy>(LegacyArgExtractionPolicy.Instance);
        services.AddSingleton<IPermissionService>(sp => new PermissionService(
            sp.GetRequiredService<Harbor.Abstractions.Agents.IAgentRegistry>(),
            sp.GetRequiredService<ILogger<PermissionService>>(),
            workspaceRoot: Directory.GetCurrentDirectory(),
            configStore: sp.GetService<IConfigStore>(),
            pathPolicies: sp.GetServices<IPathExtractionPolicy>(),
            // #557: the guards are the ones the TOOL REGISTRY derived from what each
            // tool declares on ITool.SafetyProfile — not a static name list. Read
            // through a delegate so a tool registered later (a plugin, a hot reload)
            // is guarded from its very first permission check.
            safetyPolicies: () => sp.GetRequiredService<IToolRegistry>().SafetyPolicies));
        // #49 PR1: runtime-owned approval/cancellation coordinator — the single
        // ingress for run cancellation and the linearization point for gate
        // decisions vs cancel. Stateless w.r.t. the agent (takes IAgentRunner
        // per RequestCancel), so no construction-time ordering constraints.
        services.AddSingleton<IApprovalCoordinator>(sp => new ApprovalCoordinator(
            sp.GetRequiredService<ILogger<ApprovalCoordinator>>()));
        return services;
    }
}
