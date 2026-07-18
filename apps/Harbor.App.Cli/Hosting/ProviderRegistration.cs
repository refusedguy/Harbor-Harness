using System.Reflection;
using System.Text.Json;
using Harbor.Abstractions.Providers;
using Harbor.Core.Configuration;
#if !HARBOR_MINIMAL
using Harbor.Providers.OpenAiCompatible;
using Harbor.Providers.OpenAiCompatible.Compat;
#endif
using Microsoft.Extensions.Logging;
namespace Harbor.Cli.Hosting;
/// <summary>
///     Provider registration — single responsibility: discover and register LLM providers.
///     Extracted from Program.cs.
/// </summary>
internal static class ProviderRegistration
{
#if !HARBOR_MINIMAL
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

        // Filesystem providers
        string? providersDir = FindProvidersDirectory();
        if (providersDir is not null && Directory.Exists(providersDir))
        {
            foreach (string file in Directory.EnumerateFiles(providersDir, "*.json"))
                RegisterOne(file, builder, httpClientFactory, authStore, modelCatalog, loggerFactory);
        }

        // Embedded providers
        foreach ((string name, string content) in LoadEmbeddedProviders())
        {
            try
            {
                var config = JsonSerializer.Deserialize<ProviderConfig>(content);
                if (config is null || string.IsNullOrEmpty(config.Id)) continue;
                if (config.Id is "anthropic" or "openai" or "ollama") continue;

                var http = httpClientFactory.CreateClient($"provider:{config.Id}");
                http.Timeout = TimeSpan.FromSeconds(config.Timeout);
                // §OOP-002 (RESOLVED): attach provider-specific compat flags
                // (Strategy pattern) so the client can apply them without hardcoding
                // provider ids. ProviderCompatFlags.For returns null when there are
                // no flags for this provider, which the client treats as no-op.
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
        DynamicModelCatalog modelCatalog, ILoggerFactory loggerFactory)
    {
        try
        {
            var config = ProviderConfig.LoadFromFile(file);
            if (config.IsFailure) return;
            if (config.Value.Id is "anthropic" or "openai" or "ollama") return;

            var http = httpClientFactory.CreateClient($"provider:{config.Value.Id}");
            http.Timeout = TimeSpan.FromSeconds(config.Value.Timeout);
            // §OOP-002 (RESOLVED): attach provider-specific compat flags
            // (Strategy pattern) so the client can apply them without hardcoding
            // provider ids. ProviderCompatFlags.For returns null when there are
            // no flags for this provider, which the client treats as no-op.
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

    private static IEnumerable<(string Name, string Content)> LoadEmbeddedProviders()
    {
        var assembly = Assembly.GetExecutingAssembly();
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

    public static string? FindProvidersDirectory()
    {
        string exeDir = AppContext.BaseDirectory;
        string providersInExe = Path.Combine(exeDir, "providers");
        if (Directory.Exists(providersInExe)) return providersInExe;

        string? current = exeDir;
        for (int i = 0; i < 8 && current is not null; i++)
        {
            string candidate = Path.Combine(current, "providers");
            if (Directory.Exists(candidate)) return candidate;
            current = Path.GetDirectoryName(current);
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string userProviders = Path.Combine(home, ".harbor", "providers");
        return Directory.Exists(userProviders) ? userProviders : null;
    }
}
