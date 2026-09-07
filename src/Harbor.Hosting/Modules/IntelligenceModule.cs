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
            sp.GetRequiredService<IProviderRegistry>(),
            sp.GetRequiredService<ILogger<CompactionService>>(),
            ctx.Harbor.SecondaryModel));
        services.AddSingleton<IPermissionService>(sp => new PermissionService(
            sp.GetRequiredService<IAgentRegistry>(),
            sp.GetRequiredService<ILogger<PermissionService>>(),
            workspaceRoot: Directory.GetCurrentDirectory(),
            configStore: sp.GetService<IConfigStore>()));
        return services;
    }
}
