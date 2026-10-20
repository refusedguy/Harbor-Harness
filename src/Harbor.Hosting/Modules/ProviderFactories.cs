using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Application.Configuration;
using Harbor.Providers.Ollama;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Harbor.Registries.Providers;
#if HARBOR_WITH_ALL_PROVIDERS
using Harbor.Providers.Anthropic;
using Harbor.Providers.OpenAI;
#endif

namespace Harbor.Hosting;

// Provider factories shared by every app. Auth resolution goes through
// ConfigAuthResolver (AuthStore-backed) — Avalonia swaps the resolver via its
// own auth path before these factories are used.

// DI014 (Excubo): the CLI flavor builds a bootstrap service provider on
// purpose — registries are eager artifacts constructed before the app-level
// provider exists (same justification as RegistriesModule). The scope is
// NOT disposed: DefaultHttpClientFactory resolves its meter factory and
// handler dependencies lazily on first request from this very provider.
#pragma warning disable DI014

internal static class ProviderFactories
{
    internal static ProviderRegistry CreateProviderRegistry(HarborCompositionContext ctx, IServiceCollection services)
    {
        var registry = new ProviderRegistry(ctx.LoggerFactory.CreateLogger<ProviderRegistry>());
        var pb = new ProviderRegistryBuilder(registry, ctx.LoggerFactory);

        // The CLI flavor takes its provider clients from the factory; the desktop
        // flavor takes the clients of the providers it discovers in
        // providers/*.json from the same factory (#1854 — the desktop flavor used
        // to build one client per discovered file, each with a private connection
        // pool and no owner). Ollama is the one exception in the desktop flavor:
        // it owns a single process-wide client (see below). The bootstrap scope
        // is deliberately kept alive for the process — see the DI014 note above
        // (CE-5 PTY-suite finding: disposing it killed every later provider HTTP
        // call with ObjectDisposedException).
        var bootstrapSp = services.BuildServiceProvider();
        var httpFactory = bootstrapSp.GetRequiredService<IHttpClientFactory>();

        if (ctx.Options.Providers == HarborProviderFlavor.Desktop)
        {
            // Ollama talks to a local daemon: ONE client for the whole process,
            // built HERE so the source handed to the factory only ever RETURNS
            // it. A client created inside that source would be minted per call —
            // a private connection pool per call, with its own DNS lifetime and
            // nobody to dispose it (#1854; the rule that forbids the shape is
            // HttpClientReuseRules in the architecture tests).
            var ollamaHttp = new HttpClient
            {
                BaseAddress = new Uri(Environment.GetEnvironmentVariable("OLLAMA_HOST") ?? "http://localhost:11434"),
                Timeout = TimeSpan.FromSeconds(10)
            };
            pb.AddProvider(new OllamaProviderFactory(() => ollamaHttp));
            if (ctx.Options.DesktopAuthResolver is not null && ctx.Options.DesktopModelCatalog is not null)
            {
                JsonProviderDiscovery.RegisterDesktopProviders(
                    pb, ctx.Options.DesktopAuthResolver, ctx.Options.DesktopModelCatalog, httpFactory, ctx.LoggerFactory);
            }
        }
        else
        {
            // CLI flavor needs IHttpClientFactory named clients + AuthStore.
            var authStore = bootstrapSp.GetRequiredService<AuthStore>();
            string cacheDir = Path.Combine(ctx.Options.HarborDir, "cache", "providers");

            // The ollama client comes from the factory's named "ollama" client:
            // the handler is pooled and rotated by the factory, and a client is
            // minted per call — which is what the factory is for.
            pb.AddProvider(new OllamaProviderFactory(() => httpFactory.CreateClient("ollama")));

            // JSON discovery runs in every CLI flavor (see JsonProviderDiscovery
            // NOTE) — providers/*.json is the documented "no code" provider path.
            JsonProviderDiscovery.RegisterJsonProviders(pb, httpFactory, loggerFactory: ctx.LoggerFactory, cacheDir, authStore);

#if HARBOR_WITH_ALL_PROVIDERS
            pb.AddProvider(new AnthropicProviderFactory(httpFactory, authStore));
            pb.AddProvider(new OpenAiProviderFactory(httpFactory, authStore));
#endif
        }

        registry.Freeze();
        ProviderFactoriesLog.ProvidersRegistered(ctx.Logger, registry.GetRegisteredProviderIds().Count);
        return registry;
    }
}

/// <summary>
///     Ollama — one factory for both flavors (#1501).
/// </summary>
/// <remarks>
///     <para>
///         The two flavors differ in ONE thing: where the HTTP client comes
///         from. The CLI flavor takes the named "ollama" client from
///         <see cref="IHttpClientFactory" /> (the handler is pooled and rotated
///         by the factory); the desktop flavor owns one process-wide client
///         built from OLLAMA_HOST. Everything else — the provider id, the
///         config, the retry seam — is the same, so the client SOURCE is the
///         parameter and the flavor choice stops being spread over two classes
///         that both claimed the same provider id.
///     </para>
///     <para>
///         The source is asked for a client once per
///         <see cref="CreateClient" /> call, and it must RETURN a client, never
///         build one: a delegate that creates a client is invoked per call, so
///         it would mint a client — and a private connection pool — per call
///         (#1854; the rule that forbids the shape is HttpClientReuseRules in
///         the architecture tests).
///     </para>
/// </remarks>
internal sealed class OllamaProviderFactory : IProviderFactory
{
    private readonly Func<HttpClient> _httpClientSource;

    public OllamaProviderFactory(Func<HttpClient> httpClientSource)
    {
        _httpClientSource = httpClientSource;
    }

    public ProviderId ProviderId => ProviderId.Create("ollama");

    // #1502: transient-retry at the provider seam (preflight = first event).
    public ILlmClient CreateClient(ILoggerFactory loggerFactory) => new OllamaLlmClient(
        _httpClientSource(),
        new OllamaConfig(),
        loggerFactory.CreateLogger<OllamaLlmClient>()).WithTransientRetry(loggerFactory);
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
        // #1502: transient-retry at the provider seam (preflight = first event).
        loggerFactory.CreateLogger<AnthropicLlmClient>()).WithTransientRetry(loggerFactory);
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
        // #1502: transient-retry at the provider seam (preflight = first event).
        loggerFactory.CreateLogger<OpenAILlmClient>()).WithTransientRetry(loggerFactory);
}
#endif

/// <summary>
///     SG4: BCL <c>[LoggerMessage]</c> delegates for <see cref="ProviderFactories" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class ProviderFactoriesLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Registered providers: {Count}")]
    public static partial void ProvidersRegistered(ILogger logger, int count);
}
