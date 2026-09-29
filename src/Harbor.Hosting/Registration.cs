using Microsoft.Extensions.DependencyInjection;
namespace Harbor.Hosting;

/// <summary>
///     ЕДИНСТВЕННАЯ точка сборки DI-графа Harbor. Приложения не содержат
///     регистраций: они вызывают AddHarbor и передают специфику через
///     HarborComposeOptions. Порядок вызовов фиксирован (di-design §3.5):
///     конфигурация → ядро → реестры (+плагины, Freeze до публикации) →
///     intelligence → http-clients → storage → TUI → IPC.
/// </summary>
public static class Registration
{
    public static HarborCompositionContext AddHarbor(
        this IServiceCollection services,
        HarborComposeOptions options)
    {
        var ctx = services.AddHarborConfiguration(options);

        ctx.Logger.LogInformation("Feature flags: plugins={Plugins}, spectre-tui={SpectreTui}, all-providers={AllProviders}",
            ctx.Options.Features.Plugins, ctx.Options.Features.SpectreTui, ctx.Options.Features.AllProviders);

        services.AddHarborTelemetry(ctx)
                .AddHarborCore(ctx)
                .AddHarborHttpClients(ctx)
                .AddHarborRegistries(ctx)
                .AddHarborIntelligence(ctx)
                .AddHarborStorage(ctx)
                .AddHarborTui(ctx)
                .AddHarborIpc(ctx);

        // #562: the two state members a module owns (EventBus, Registries) refuse
        // to be read before they are assigned, which catches a reader that moved
        // ahead of its owner. This catches the other half of a bad edit here —
        // an owner that was dropped from the chain, or moved after every reader,
        // in which case nothing read the hole during composition and the process
        // would only discover it on first use.
        ctx.AssertFullyInitialized();

        return ctx;
    }
}
