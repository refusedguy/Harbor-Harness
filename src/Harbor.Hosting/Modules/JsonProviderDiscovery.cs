using System.Reflection;
using Harbor.Abstractions.Providers;
using Harbor.Application.Configuration;
using Harbor.Providers.OpenAiCompatible;
using Harbor.Providers.OpenAiCompatible.Compat;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
    ///     Enumerate every candidate providers directory in precedence order.
    ///     Delegates to <see cref="ProviderPresetCatalog" /> so provider
    ///     <i>registration</i> (here) and the onboarding preset catalog
    ///     (<see cref="ProviderPresets.All" />, #580) can never walk different sets
    ///     of directories — one walk, one precedence order.
    /// </summary>
    public static IEnumerable<string> FindProvidersDirectories() =>
        ProviderPresetCatalog.FindProvidersDirectories();

    /// <summary>
    ///     Desktop flavor: OpenAI-compatible providers only (apiType filter),
    ///     resolver + catalog injected by the app (async-loaded config).
    ///     Mirrors the former Avalonia ProviderRegistration.RegisterJsonProviders.
    /// </summary>
    /// <param name="pb">Registry builder the discovered providers are added to.</param>
    /// <param name="authResolver">Auth resolver (desktop flavor).</param>
    /// <param name="modelCatalog">Model catalog (desktop flavor).</param>
    /// <param name="httpClientFactory">
    ///     The client factory the discovered providers speak over (#1854). The
    ///     desktop flavor used to build one <see cref="HttpClient" /> per
    ///     discovered <c>providers/*.json</c> — a private connection pool each,
    ///     never disposed and never re-resolving DNS — while the CLI flavor
    ///     already took its clients from this factory. Same shape now, both
    ///     flavors.
    /// </param>
    /// <param name="loggerFactory">Logger factory.</param>
    public static void RegisterDesktopProviders(
        IProviderRegistryBuilder pb,
        Harbor.Abstractions.Providers.IAuthResolver authResolver,
        Harbor.Providers.OpenAiCompatible.IModelCatalog modelCatalog,
        IHttpClientFactory httpClientFactory,
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
                    if (result.IsFailure) // §4.6-ok: discovery-резильенс — skip-and-log, битый файл не валид discovery.
                    {
                        JsonProviderDiscoveryLog.SkippingProviderConfig(logger, file, result.Error);
                        continue;
                    }
                    #pragma warning disable CFE0001
                    // CFE0001 false positive. Baseline: docs/ROP-API-INVENTORY.md 5.
                    // Guard upstream is an loop-continue guard.
                    var config = result.Value;
                    #pragma warning restore CFE0001
                    if (config.ApiType != "openai-compatible")
                    {
                        JsonProviderDiscoveryLog.SkippingNonCompatProvider(logger, config.Id, config.ApiType);
                        continue;
                    }
                    if (!seenIds.Add(config.Id))
                    {
                        JsonProviderDiscoveryLog.SkippingDuplicateProvider(logger, config.Id, file);
                        continue;
                    }

                    // #1854: the client comes from the factory, like every other
                    // provider client in the product (the CLI flavor below does
                    // the same). One handler per provider, pooled and rotated by
                    // the factory — instead of one private connection pool per
                    // providers/*.json file, each with its own DNS lifetime and
                    // no owner to dispose it.
                    var http = httpClientFactory.CreateClient($"provider:{config.Id}");
                    http.Timeout = TimeSpan.FromSeconds(config.Timeout);
                    // #195: configs are immutable — attach quirks via a sealed copy.
                    var configRef = config.WithQuirks(Harbor.Providers.OpenAiCompatible.Compat.ProviderCompatFlags.For(config.GetProviderId()));
                    pb.AddProvider(config.Id, () => new Harbor.Providers.OpenAiCompatible.OpenAiCompatibleLlmClient(
                        http, configRef, authResolver, modelCatalog,
                        loggerFactory.CreateLogger<Harbor.Providers.OpenAiCompatible.OpenAiCompatibleLlmClient>())
                        // #1502: transient-retry at the provider seam (preflight = first event).
                        .WithTransientRetry(loggerFactory));
                    JsonProviderDiscoveryLog.ProviderRegistered(logger, config.Id, file);
                }
                catch (Exception ex)
                {
                    JsonProviderDiscoveryLog.LoadProviderConfigFailed(logger, ex, file);
                }
            }
        }
    }

// NOTE: JSON-provider discovery is NOT gated behind HARBOR_WITH_ALL_PROVIDERS.
// providers/*.json ("no code" provider path, docs/EXAMPLES §9) and the embedded
// bundled configs must work in every CLI flavor, including HARBOR_MINIMAL —
// OpenAiCompatible is a baseline ProjectReference of Harbor.Hosting. Only the
// native Anthropic/OpenAI factories stay behind the flag. (CE-5 PTY-suite
// finding: plain slnx builds produced an app that registered ollama only,
// so every scenarios-2..8 test failed with "Provider 'mock' is not registered".)
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
                var loaded = ProviderConfig.LoadFromJson(content);
                if (loaded.IsFailure)
                {
                    Console.Error.WriteLine($"Failed to load embedded provider '{name}': {loaded.Error}");
                    continue;
                }

                #pragma warning disable CFE0001
                // CFE0001 false positive. Baseline: docs/ROP-API-INVENTORY.md 5.
                // Guard upstream is an loop-continue guard.
                var config = loaded.Value;
                #pragma warning restore CFE0001
                if (config is null || string.IsNullOrEmpty(config.Id)) continue;
                if (config.Id is "anthropic" or "openai" or "ollama") continue;
                if (!seenIds.Add(config.Id)) continue;

                var http = httpClientFactory.CreateClient($"provider:{config.Id}");
                http.Timeout = TimeSpan.FromSeconds(config.Timeout);
                // §OOP-002 (RESOLVED): attach provider-specific compat flags
                // (Strategy pattern) so the client can apply them without hardcoding
                // provider ids. #195: via a sealed copy — configs are immutable.
                config = config.WithQuirks(ProviderCompatFlags.For(config.GetProviderId()));
                builder.AddProvider(config.Id, () => new OpenAiCompatibleLlmClient(
                    http, config,
                    new ConfigAuthResolver(authStore, config.Id),
                    modelCatalog,
                    loggerFactory.CreateLogger<OpenAiCompatibleLlmClient>())
                    // #1502: transient-retry at the provider seam (preflight = first event).
                    .WithTransientRetry(loggerFactory));
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
            if (config.IsFailure) return; // §4.6-ok: skip-and-log discovery (ранний выход перечисления).
            var loaded = config.Value;
            if (loaded.Id is "anthropic" or "openai" or "ollama") return;
            if (!seenIds.Add(loaded.Id)) return;

            var http = httpClientFactory.CreateClient($"provider:{loaded.Id}");
            http.Timeout = TimeSpan.FromSeconds(loaded.Timeout);
            // #195: configs are immutable — attach quirks via a sealed copy.
            var sealedConfig = loaded.WithQuirks(ProviderCompatFlags.For(loaded.GetProviderId()));
            builder.AddProvider(sealedConfig.Id, () => new OpenAiCompatibleLlmClient(
                http, sealedConfig,
                new ConfigAuthResolver(authStore, sealedConfig.Id),
                modelCatalog,
                loggerFactory.CreateLogger<OpenAiCompatibleLlmClient>())
                // #1502: transient-retry at the provider seam (preflight = first event).
                .WithTransientRetry(loggerFactory));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to register provider: {ex.Message}");
        }
    }
}

/// <summary>
///     SG4: BCL <c>[LoggerMessage]</c> delegates for <see cref="JsonProviderDiscovery" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class JsonProviderDiscoveryLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Skipping provider config '{File}': {Error}")]
    public static partial void SkippingProviderConfig(ILogger logger, string file, string error);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "Skipping provider '{Id}' (apiType={Type}, not openai-compatible)")]
    public static partial void SkippingNonCompatProvider(ILogger logger, string id, string type);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "Skipping duplicate provider '{Id}' from '{File}'")]
    public static partial void SkippingDuplicateProvider(ILogger logger, string id, string file);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Registered OpenAI-compatible provider '{Id}' from '{File}'")]
    public static partial void ProviderRegistered(ILogger logger, string id, string file);

    [LoggerMessage(EventId = 5, Level = LogLevel.Error, Message = "Failed to load provider config '{File}'")]
    public static partial void LoadProviderConfigFailed(ILogger logger, Exception ex, string file);
}
