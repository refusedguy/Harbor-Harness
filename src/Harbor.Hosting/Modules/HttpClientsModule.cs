using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harbor.Hosting;

internal static class HttpClientsModule
{
    internal static IServiceCollection AddHarborHttpClients(
        this IServiceCollection services,
        HarborCompositionContext ctx)
    {
        HttpClientsModuleLog.RegisteringHttpClients(ctx.Logger);
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
            HttpClientsModuleLog.OllamaOnlyHttpClients(ctx.Logger);
        }
        return services;
    }
}

/// <summary>
///     SG4: BCL <c>[LoggerMessage]</c> delegates for <see cref="HttpClientsModule" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class HttpClientsModuleLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Registering HTTP clients")]
    public static partial void RegisteringHttpClients(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "HARBOR_WITH_ALL_PROVIDERS=false — registered only the ollama HTTP client")]
    public static partial void OllamaOnlyHttpClients(ILogger logger);
}
