using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harbor.Hosting;

internal static class HttpClientsModule
{
    internal static IServiceCollection AddHarborHttpClients(
        this IServiceCollection services,
        HarborCompositionContext ctx)
    {
        ctx.Logger.LogInformation("Registering HTTP clients");
        services.AddHttpClient("ollama");
        if (ctx.Options.Features.AllProviders)
        {
            services.AddHttpClient("anthropic");
            services.AddHttpClient("openai");
            services.AddHttpClient("providers");
            services.AddHttpClient("default");
        }
        else
        {
            ctx.Logger.LogInformation("HARBOR_WITH_ALL_PROVIDERS=false — registered only the ollama HTTP client");
        }
        return services;
    }
}
