using System.Reflection;
using System.Text.Json;
using Harbor.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
#if HARBOR_WITH_ALL_PROVIDERS
using Harbor.Abstractions.Providers;
using Harbor.Providers.OpenAiCompatible;
using Harbor.Providers.OpenAiCompatible.Compat;
#endif

namespace Harbor.Hosting;

/// <summary>
///     Json-provider discovery (providers/*.json): user config first, then
///     bundled directories, then embedded resources. Shared by CLI and desktop.
/// </summary>
internal static class JsonProviderDiscovery
{
    private static IEnumerable<(string Name, string Content)> LoadEmbeddedProviders()
    {
        var assembly = typeof(JsonProviderDiscovery).Assembly;
        foreach (string name in assembly.GetManifestResourceNames()
                     .Where(n => n.Contains("providers.") && n.EndsWith(".json")))
        {
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null) continue;
            using var reader = new StreamReader(stream);
            string content = reader.ReadToEnd();
            string shortName = name.Substring(name.LastIndexOf("providers.", StringComparison.Ordinal) + "providers.".Length);
            yield return (shortName, content);
        }
    }

    /// <summary>
    ///     Enumerate every candidate providers directory in precedence order:
    ///     user config (<c>~/.harbor/providers/</c>) first, then bundled
    ///     (<c>&lt;exeDir&gt;/providers/</c> and any ancestor). User config wins
    ///     on id collisions so E2E tests (and power users) can override a
    ///     bundled provider with their own JSON.
    /// </summary>
    public static IEnumerable<string> FindProvidersDirectories()
    {
        // 1. User config — always checked first so user overrides win.
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string userProviders = Path.Combine(home, ".harbor", "providers");
        yield return userProviders;

        // 2. providers/ next to the running executable (single-file publish, etc.).
        string exeDir = AppContext.BaseDirectory;
        string providersInExe = Path.Combine(exeDir, "providers");
        yield return providersInExe;

        // 3. Walk up from exeDir looking for a sibling providers/ directory
        //    (the typical dev-clone layout: repo root has providers/ next to apps/).
        string? current = exeDir;
        for (int i = 0; i < 8 && current is not null; i++)
        {
            string candidate = Path.Combine(current, "providers");
            yield return candidate;
            current = Path.GetDirectoryName(current);
        }
    }

    /// <summary>
    ///     Desktop flavor: OpenAI-compatible providers only (apiType filter),
    ///     resolver + catalog injected by the app (async-loaded config).
    ///     Mirrors the former Avalonia ProviderRegistration.RegisterJsonProviders.
    /// </summary>
    public static void RegisterDesktopProviders(
        IProviderRegistryBuilder pb,
        Harbor.Providers.OpenAiCompatible.IAuthResolver authResolver,
        Harbor.Providers.OpenAiCompatible.IModelCatalog modelCatalog,
        ILoggerFactory loggerFactory)
    {
        var seenIds = new HashSet<string>(StringComparer.Ordinal) { "ollama" };
        var logger = loggerFactory.CreateLogger(typeof(JsonProviderDiscovery).FullName ?? "JsonProviderDiscovery");

        foreach (string dir in FindProvidersDirectories())
        {
            if (!Directory.Exists(dir)) continue;
            foreach (string file in Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    var result = Harbor.Providers.OpenAiCompatible.ProviderConfig.LoadFromFile(file);
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
                    config.Quirks = Harbor.Providers.OpenAiCompatible.Compat.ProviderCompatFlags.For(config.GetProviderId());
                    var configRef = config;
                    pb.AddProvider(config.Id, () => new Harbor.Providers.OpenAiCompatible.OpenAiCompatibleLlmClient(
                        http, configRef, authResolver, modelCatalog,
                        loggerFactory.CreateLogger<Harbor.Providers.OpenAiCompatible.OpenAiCompatibleLlmClient>()));
                    logger.LogInformation("Registered OpenAI-compatible provider '{Id}' from '{File}'", config.Id, file);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to load provider config '{File}'", file);
                }
            }
        }
    }

#if HARBOR_WITH_ALL_PROVIDERS
    public static void RegisterJsonProviders(
        IProviderRegistryBuilder builder,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        string cacheDir,
        AuthStore authStore)
    {
        var modelCatalog = new DynamicModelCatalog(
            httpClientFactory.CreateClient("providers"),
            cacheDir,
            loggerFactory.CreateLogger<DynamicModelCatalog>());

        // Discover providers from EVERY candidate directory (user config first,
        // then bundled). Tracked-registered ids guard against duplicates.
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (string dir in FindProvidersDirectories())
        {
            if (!Directory.Exists(dir)) continue;
            foreach (string file in Directory.EnumerateFiles(dir, "*.json"))
                RegisterOne(file, builder, httpClientFactory, authStore, modelCatalog, loggerFactory, seenIds);
        }

        // Embedded providers
        foreach ((string name, string content) in LoadEmbeddedProviders())
        {
            try
            {
                var config = JsonSerializer.Deserialize<ProviderConfig>(content, OpenAiCompatibleJsonContext.Default.Options);
                if (config is null || string.IsNullOrEmpty(config.Id)) continue;
                if (config.Id is "anthropic" or "openai" or "ollama") continue;
                if (!seenIds.Add(config.Id)) continue;

                var http = httpClientFactory.CreateClient($"provider:{config.Id}");
                http.Timeout = TimeSpan.FromSeconds(config.Timeout);
                // §OOP-002 (RESOLVED): attach provider-specific compat flags
                // (Strategy pattern) so the client can apply them without hardcoding
                // provider ids.
                config.Quirks = ProviderCompatFlags.For(config.GetProviderId());
                builder.AddProvider(config.Id, () => new OpenAiCompatibleLlmClient(
                    http, config,
                    new ConfigAuthResolver(authStore, config.Id),
                    modelCatalog,
                    loggerFactory.CreateLogger<OpenAiCompatibleLlmClient>()));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to load embedded provider '{name}': {ex.Message}");
            }
        }
    }

    private static void RegisterOne(
        string file, IProviderRegistryBuilder builder,
        IHttpClientFactory httpClientFactory, AuthStore authStore,
        DynamicModelCatalog modelCatalog, ILoggerFactory loggerFactory,
        HashSet<string> seenIds)
    {
        try
        {
            var config = ProviderConfig.LoadFromFile(file);
            if (config.IsFailure) return;
            if (config.Value.Id is "anthropic" or "openai" or "ollama") return;
            if (!seenIds.Add(config.Value.Id)) return;

            var http = httpClientFactory.CreateClient($"provider:{config.Value.Id}");
            http.Timeout = TimeSpan.FromSeconds(config.Value.Timeout);
            config.Value.Quirks = ProviderCompatFlags.For(config.Value.GetProviderId());
            builder.AddProvider(config.Value.Id, () => new OpenAiCompatibleLlmClient(
                http, config.Value,
                new ConfigAuthResolver(authStore, config.Value.Id),
                modelCatalog,
                loggerFactory.CreateLogger<OpenAiCompatibleLlmClient>()));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to register provider: {ex.Message}");
        }
    }
#endif
}
