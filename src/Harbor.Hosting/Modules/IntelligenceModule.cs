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
        services.AddSingleton<IPermissionService>(sp => new PermissionService(
            sp.GetRequiredService<Harbor.Abstractions.Agents.IAgentRegistry>(),
            sp.GetRequiredService<ILogger<PermissionService>>(),
            workspaceRoot: Directory.GetCurrentDirectory()));
        return services;
    }
}
