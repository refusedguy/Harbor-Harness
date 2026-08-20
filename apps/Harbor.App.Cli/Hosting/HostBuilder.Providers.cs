using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Core.Configuration;
using Harbor.Providers.Ollama;
#if HARBOR_WITH_ALL_PROVIDERS
using Harbor.Providers.Anthropic;
using Harbor.Providers.OpenAI;
#endif
namespace Harbor.Cli.Hosting;

internal static partial class HostBuilder
{
    private static void RegisterHttpClients(HostApplicationBuilder builder)
    {
        _logger.LogInformation("Registering HTTP clients");
        builder.Services.AddHttpClient("ollama");
#if HARBOR_WITH_ALL_PROVIDERS
        builder.Services.AddHttpClient("anthropic");
        builder.Services.AddHttpClient("openai");
        builder.Services.AddHttpClient("providers");
        builder.Services.AddHttpClient("default");
#else
        _logger.LogInformation("HARBOR_WITH_ALL_PROVIDERS=false — registered only the ollama HTTP client");
#endif
    }

    private static ProviderRegistry CreateProviderRegistry(IServiceProvider sp, string harborDir, HarborConfig config)
    {
        var registry = new ProviderRegistry(sp.GetRequiredService<ILogger<ProviderRegistry>>());
        var pb = new ProviderRegistryBuilder(registry, sp.GetRequiredService<ILoggerFactory>());
        var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var authStore = sp.GetRequiredService<AuthStore>();
        string cacheDir = Path.Combine(harborDir, "cache", "providers");

        pb.AddProvider(new OllamaProviderFactory(httpFactory));

#if HARBOR_WITH_ALL_PROVIDERS
        pb.AddProvider(new AnthropicProviderFactory(httpFactory, authStore));
        pb.AddProvider(new OpenAiProviderFactory(httpFactory, authStore));

        ProviderRegistration.RegisterJsonProviders(pb, httpFactory, loggerFactory, cacheDir, authStore);
#endif

        registry.Freeze();
        _logger.LogInformation("Registered providers: {Count}", registry.GetRegisteredProviderIds().Count);
        return registry;
    }
}

internal sealed class OllamaProviderFactory : IProviderFactory
{
    private readonly IHttpClientFactory _httpFactory;

    public OllamaProviderFactory(IHttpClientFactory httpFactory)
    {
        _httpFactory = httpFactory;
    }

    public ProviderId ProviderId => ProviderId.Create("ollama");

    public ILlmClient CreateClient(ILoggerFactory loggerFactory) => new OllamaLlmClient(
        _httpFactory.CreateClient("ollama"),
        new OllamaConfig(),
        loggerFactory.CreateLogger<OllamaLlmClient>());
}

#if HARBOR_WITH_ALL_PROVIDERS
internal sealed class AnthropicProviderFactory : IProviderFactory
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly AuthStore _authStore;

    public AnthropicProviderFactory(IHttpClientFactory httpFactory, AuthStore authStore)
    {
        _httpFactory = httpFactory;
        _authStore = authStore;
    }

    public ProviderId ProviderId => ProviderId.Create("anthropic");

    public ILlmClient CreateClient(ILoggerFactory loggerFactory) => new AnthropicLlmClient(
        _httpFactory.CreateClient("anthropic"),
        new AnthropicConfig(),
        new ConfigAuthResolver(_authStore, "anthropic"),
        loggerFactory.CreateLogger<AnthropicLlmClient>());
}

internal sealed class OpenAiProviderFactory : IProviderFactory
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly AuthStore _authStore;

    public OpenAiProviderFactory(IHttpClientFactory httpFactory, AuthStore authStore)
    {
        _httpFactory = httpFactory;
        _authStore = authStore;
    }

    public ProviderId ProviderId => ProviderId.Create("openai");

    public ILlmClient CreateClient(ILoggerFactory loggerFactory) => new OpenAILlmClient(
        _httpFactory.CreateClient("openai"),
        new OpenAIConfig(),
        new ConfigAuthResolver(_authStore, "openai"),
        loggerFactory.CreateLogger<OpenAILlmClient>());
}
#endif
