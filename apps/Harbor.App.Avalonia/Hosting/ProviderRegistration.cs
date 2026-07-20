using Harbor.Abstractions.Providers;
using Harbor.Core.Configuration;
using Harbor.Desktop.Abstractions.Configuration;
using Harbor.Providers.Ollama;
using Harbor.Providers.OpenAiCompatible;
using Harbor.Providers.OpenAiCompatible.Compat;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harbor.App.Avalonia.Hosting;

/// <summary>
///     Provider registration — Ollama (native client, works offline) +
///     all OpenAI-compatible providers discovered from
///     <c>providers/*.json</c> (Kilocode, OpenRouter, DeepSeek, Groq,
///     Mistral, xAI, Together, Fireworks, Cerebras, OpenAI, vLLM). Also
///     registers the <see cref="IAuthResolver"/> + <see cref="IModelCatalog"/>
///     singletons that the providers + view-models depend on.
/// </summary>
/// <remarks>
///     Mirrors the CLI's <c>ProviderRegistration.RegisterJsonProviders</c>
///     but inlined so Avalonia doesn't need to reference
///     <c>Harbor.Cli.Hosting</c>.
/// </remarks>
internal static class ProviderRegistration
{
    /// <summary>
    ///     Build the <see cref="ProviderRegistry"/> eagerly with the Ollama
    ///     native client + every discovered OpenAI-compatible JSON provider.
    ///     The returned registry is registered as a singleton by the caller.
    /// </summary>
    /// <param name="loggerFactory">Bootstrap logger factory (must outlive the host build).</param>
    /// <param name="authResolver">The auth resolver (used by OpenAI-compatible clients).</param>
    /// <param name="modelCatalog">The model catalog (used by OpenAI-compatible clients).</param>
    /// <returns>The constructed + frozen <see cref="ProviderRegistry"/>.</returns>
    public static ProviderRegistry Build(
        ILoggerFactory loggerFactory,
        IAuthResolver authResolver,
        IModelCatalog modelCatalog)
    {
        var providerRegistry = new ProviderRegistry(loggerFactory.CreateLogger<ProviderRegistry>());
        var pb = new ProviderRegistryBuilder(providerRegistry);
        pb.AddProvider("ollama", () => new OllamaLlmClient(
            new HttpClient
            {
                BaseAddress = new Uri(Environment.GetEnvironmentVariable("OLLAMA_HOST") ?? "http://localhost:11434"),
                // 10s timeout — the default 100s makes the UI feel frozen when
                // Ollama isn't running. The ProviderBrowserViewModel adds its
                // own 5s cancellation token on top, so a missing Ollama is
                // surfaced as a quick "no models" rather than a 100s hang.
                Timeout = TimeSpan.FromSeconds(10),
            },
            new OllamaConfig(),
            loggerFactory.CreateLogger<OllamaLlmClient>()));
        RegisterJsonProviders(pb, authResolver, modelCatalog, loggerFactory);
        providerRegistry.Freeze();
        return providerRegistry;
    }

    /// <summary>
    ///     Discover and register OpenAI-compatible providers from every
    ///     <c>providers/*.json</c> candidate directory. Mirrors the CLI's
    ///     <c>ProviderRegistration.RegisterJsonProviders</c> but inlined so
    ///     Avalonia doesn't need to reference <c>Harbor.Cli.Hosting</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Directories searched (first wins on id collision):</b>
    ///     </para>
    ///     <list type="number">
    ///         <item><c>~/.harbor/providers/</c> — user overrides.</item>
    ///         <item><c>&lt;exeDir&gt;/providers/</c> — bundled (single-file publish).</item>
    ///         <item>Walk up from <c>exeDir</c> looking for a sibling <c>providers/</c> — dev-clone layout.</item>
    ///     </list>
    ///     <para>
    ///         Providers with <c>apiType != "openai-compatible"</c> (e.g.
    ///         Anthropic, which needs its native client) and <c>ollama</c>
    ///         (already registered above with the native OllamaLlmClient) are
    ///         skipped. The first matching id wins; duplicates are silently
    ///         dropped so user overrides in <c>~/.harbor/providers/</c> take
    ///         precedence over bundled JSON.
    ///     </para>
    /// </remarks>
    private static void RegisterJsonProviders(
        ProviderRegistryBuilder pb,
        IAuthResolver authResolver,
        IModelCatalog modelCatalog,
        ILoggerFactory loggerFactory)
    {
        var seenIds = new HashSet<string>(StringComparer.Ordinal) { "ollama" };
        var logger = loggerFactory.CreateLogger(typeof(ProviderRegistration).FullName ?? "ProviderRegistration");

        foreach (string dir in FindProvidersDirectories())
        {
            if (!Directory.Exists(dir)) continue;
            foreach (string file in Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    var result = ProviderConfig.LoadFromFile(file);
                    if (result.IsFailure)
                    {
                        logger.LogWarning("Skipping provider config '{File}': {Error}", file, result.Error);
                        continue;
                    }
                    var config = result.Value;
                    if (config.ApiType != "openai-compatible")
                    {
                        logger.LogDebug("Skipping provider '{Id}' (apiType={Type}, not openai-compatible)",
                            config.Id, config.ApiType);
                        continue;
                    }
                    if (!seenIds.Add(config.Id))
                    {
                        logger.LogDebug("Skipping duplicate provider '{Id}' from '{File}'", config.Id, file);
                        continue;
                    }

                    var http = new HttpClient { Timeout = TimeSpan.FromSeconds(config.Timeout) };
                    config.Quirks = ProviderCompatFlags.For(config.GetProviderId());
                    var configRef = config;
                    pb.AddProvider(config.Id, () => new OpenAiCompatibleLlmClient(
                        http, configRef, authResolver, modelCatalog,
                        loggerFactory.CreateLogger<OpenAiCompatibleLlmClient>()));
                    logger.LogInformation("Registered OpenAI-compatible provider '{Id}' from '{File}'", config.Id, file);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to load provider config '{File}'", file);
                }
            }
        }
    }

    /// <summary>
    ///     Enumerate every candidate providers directory in precedence order:
    ///     user config (<c>~/.harbor/providers/</c>) first, then bundled
    ///     (<c>&lt;exeDir&gt;/providers/</c> and any ancestor). User config wins
    ///     on id collisions so E2E tests (and power users) can override a
    ///     bundled provider with their own JSON.
    /// </summary>
    private static IEnumerable<string> FindProvidersDirectories()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(home, ".harbor", "providers");

        string exeDir = AppContext.BaseDirectory;
        yield return Path.Combine(exeDir, "providers");

        string? current = exeDir;
        for (int i = 0; i < 8 && current is not null; i++)
        {
            yield return Path.Combine(current, "providers");
            current = Path.GetDirectoryName(current);
        }
    }
}
