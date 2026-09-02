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

        services.AddHarborTelemetry()
                .AddHarborCore(ctx)
                .AddHarborHttpClients(ctx)
                .AddHarborRegistries(ctx)
                .AddHarborIntelligence(ctx)
                .AddHarborStorage(ctx)
                .AddHarborTui(ctx)
                .AddHarborIpc(ctx);

        return ctx;
    }
}
