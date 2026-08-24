using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Core.Configuration;
using Harbor.Providers.Ollama;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
#if HARBOR_WITH_ALL_PROVIDERS
using Harbor.Providers.Anthropic;
using Harbor.Providers.OpenAI;
#endif

namespace Harbor.Hosting;

// Provider factories shared by every app. Auth resolution goes through
// ConfigAuthResolver (AuthStore-backed) — Avalonia swaps the resolver via its
// own auth path before these factories are used.
internal static class ProviderFactories
{
    internal static ProviderRegistry CreateProviderRegistry(HarborCompositionContext ctx, IServiceCollection services)
    {
        var registry = new ProviderRegistry(ctx.LoggerFactory.CreateLogger<ProviderRegistry>());
        var pb = new ProviderRegistryBuilder(registry, ctx.LoggerFactory);

        if (ctx.Options.Providers == HarborProviderFlavor.Desktop)
        {
            // Ollama talks to a local daemon: direct HttpClient from OLLAMA_HOST.
            pb.AddProvider(new DesktopOllamaProviderFactory(new HttpClient
            {
                BaseAddress = new Uri(Environment.GetEnvironmentVariable("OLLAMA_HOST") ?? "http://localhost:11434"),
                Timeout = TimeSpan.FromSeconds(10)
            }));
            if (ctx.Options.DesktopAuthResolver is not null && ctx.Options.DesktopModelCatalog is not null)
            {
                JsonProviderDiscovery.RegisterDesktopProviders(
                    pb, ctx.Options.DesktopAuthResolver, ctx.Options.DesktopModelCatalog, ctx.LoggerFactory);
            }
        }
        else
        {
            // CLI flavor needs IHttpClientFactory named clients + AuthStore.
            using var tempSp = services.BuildServiceProvider();
            var httpFactory = tempSp.GetRequiredService<IHttpClientFactory>();
            var authStore = tempSp.GetRequiredService<AuthStore>();
            string cacheDir = Path.Combine(ctx.Options.HarborDir, "cache", "providers");

            pb.AddProvider(new OllamaProviderFactory(httpFactory));

#if HARBOR_WITH_ALL_PROVIDERS
            pb.AddProvider(new AnthropicProviderFactory(httpFactory, authStore));
            pb.AddProvider(new OpenAiProviderFactory(httpFactory, authStore));
            JsonProviderDiscovery.RegisterJsonProviders(pb, httpFactory, loggerFactory: ctx.LoggerFactory, cacheDir, authStore);
#endif
        }

        registry.Freeze();
        ctx.Logger.LogInformation("Registered providers: {Count}", registry.GetRegisteredProviderIds().Count);
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

/// <summary>Ollama over a direct HttpClient (OLLAMA_HOST), no IHttpClientFactory.</summary>
internal sealed class DesktopOllamaProviderFactory : IProviderFactory
{
    private readonly HttpClient _httpClient;

    public DesktopOllamaProviderFactory(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public ProviderId ProviderId => ProviderId.Create("ollama");

    public ILlmClient CreateClient(ILoggerFactory loggerFactory) => new OllamaLlmClient(
        _httpClient,
        new OllamaConfig(),
        loggerFactory.CreateLogger<OllamaLlmClient>());
}
