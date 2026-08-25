using Harbor.Providers.OpenAiCompatible.Compat;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Providers.OpenAiCompatible;
/// <summary>
///     Provider configuration loaded from JSON.
/// </summary>
public sealed class ProviderConfig
{

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        TypeInfoResolver = OpenAiCompatibleJsonContext.Default
    };
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string ApiType { get; set; } = "openai-compatible";
    public string? ApiVersion { get; set; }
    public string AuthType { get; set; } = "bearer";
    public string? AuthHeader { get; set; }
    public string? AuthEnvVar { get; set; }
    public string? ModelsUrl { get; set; }
    public int ModelsRefreshHours { get; set; } = 24;
    public string? ModelsPath { get; set; }
    public ModelMapping? ModelMapping { get; set; }
    public List<ModelInfo>? Models { get; set; }
    public Dictionary<string, string>? Headers { get; set; }
    public Dictionary<string, string>? Capabilities { get; set; }
    public int Timeout { get; set; } = 60;

    /// <summary>
    ///     §OOP-002 (RESOLVED): provider-specific request quirks (Strategy pattern).
    ///     Populated by the registration code (see <see cref="ProviderCompatFlags.For" />);
    ///     not deserialized from JSON. May be <see langword="null" /> when the provider
    ///     has no quirks — the client treats null and empty identically.
    /// </summary>
    public IReadOnlyList<IProviderCompatFlag>? Quirks { get; set; }

    public ProviderId GetProviderId() => ProviderId.Create(Id);

    public static Result<ProviderConfig> LoadFromFile(string path) =>
        Result.Try(() => JsonSerializer.Deserialize<ProviderConfig>(File.ReadAllText(path), JsonOptions))
            .MapError(ex => $"Failed to load provider config '{path}': {ex}")
            .Ensure(c => c is not null, $"Provider config '{path}' deserialized to null.")
            .Ensure(c => !string.IsNullOrEmpty(c!.Id), $"Provider config '{path}' is missing 'id'.")
            .Ensure(c => !string.IsNullOrEmpty(c!.BaseUrl), $"Provider config '{path}' is missing 'baseUrl'.")
            .Map(c => c!);
}

public sealed class ModelMapping
{
    public string? Id { get; set; }
    public string? DisplayName { get; set; }
    public string? ContextWindow { get; set; }
    public string? MaxOutputTokens { get; set; }
    public string? SupportsVision { get; set; }
    public string? SupportsToolUse { get; set; }
    public string? SupportsReasoning { get; set; }
    public Dictionary<string, string>? Pricing { get; set; }
}

/// <summary>
///     Default auth resolver — CLI override → conventional env var
///     (<c>PROVIDER_ID</c> upper-cased with dashes replaced) → failure hint.
///     The env twin of the single <see cref = "Harbor.Abstractions.Providers.IAuthResolver" />
///     abstraction (ROP-A ПР.6): the former IOpenAIAuthResolver /
///     IAnthropicAuthResolver interfaces and their per-provider resolvers
///     collapsed into this pair.
/// </summary>
/// <summary>
///     Default auth resolver — CLI override → conventional env var
///     (<c>PROVIDER_ID</c> upper-cased with dashes replaced) → failure hint.
/// </summary>
public sealed class EnvVarAuthResolver : IAuthResolver
{
    private readonly ILogger<EnvVarAuthResolver> _logger;
    private readonly Dictionary<string, string> _overrides;

    public EnvVarAuthResolver(Dictionary<string, string>? overrides = null, ILogger<EnvVarAuthResolver>? logger = null)
    {
        _overrides = overrides ?? new Dictionary<string, string>();
        _logger = logger ?? NullLogger<EnvVarAuthResolver>.Instance;
    }

    public Task<Result<string>> ResolveApiKeyAsync(string providerId, CancellationToken ct = default)
    {
        // 1. Override (from CLI flag)
        if (_overrides.TryGetValue(providerId, out string? key) && !string.IsNullOrEmpty(key))
            return Task.FromResult(Result.Success(key));

        // 2. Conventional env var
        string envName = providerId.ToUpperInvariant().Replace("-", "_") + "_API_KEY";
        string? envValue = Environment.GetEnvironmentVariable(envName);

        return Task.FromResult(
            Result.SuccessIf(!string.IsNullOrEmpty(envValue),
                    $"API key not found. Set ${envName} or pass --{providerId}-api-key.")
                .Map(() => envValue!));
    }
}

/// <summary>
///     Model catalog — fetches and caches models list.
/// </summary>
public interface IModelCatalog
{
    public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(ProviderConfig config, CancellationToken ct = default);
}

public sealed class DynamicModelCatalog : IModelCatalog
{
    private readonly string _cacheDir;
    private readonly HttpClient _http;
    private readonly ILogger<DynamicModelCatalog> _logger;

    public DynamicModelCatalog(HttpClient http, string cacheDir, ILogger<DynamicModelCatalog> logger)
    {
        _http = http;
        _cacheDir = cacheDir;
        _logger = logger;
        if (!Directory.Exists(_cacheDir))
            Directory.CreateDirectory(_cacheDir);
    }

    public async Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(ProviderConfig config, CancellationToken ct = default)
    {
        // Hardcoded models in config
        if (config.Models is { Count: > 0 })
            return Result.Success<IReadOnlyList<ModelInfo>>(config.Models);

        if (string.IsNullOrEmpty(config.ModelsUrl))
            return Result.Failure<IReadOnlyList<ModelInfo>>($"Provider '{config.Id}' has no modelsUrl and no hardcoded models.");

        // ROP-A ПР.8 — canonical Compensate chain: fresh cache → network →
        // stale cache. Each fallback fires only when the previous source
        // failed; the headline error stays the FETCH failure, not a cache miss.
        string cachePath = Path.Combine(_cacheDir, $"{config.Id}.json");
        TimeSpan maxAge = TimeSpan.FromHours(config.ModelsRefreshHours);

        return await ReadCacheAsync(cachePath, config, ct, freshOnly: true, maxAge: maxAge)
            .Compensate(_ => FetchAndCacheAsync(config, config.ModelsUrl!, cachePath, ct))
            .Compensate(fetchError => ReadCacheAsync(cachePath, config, ct, freshOnly: false, maxAge: maxAge)
                .MapError(_ => fetchError))
            .ConfigureAwait(false);
    }

    /// <summary>Fetch the live model list and seed the cache.</summary>
    private async Task<Result<IReadOnlyList<ModelInfo>>> FetchAndCacheAsync(
        ProviderConfig config, string modelsUrl, string cachePath, CancellationToken ct)
    {
        try
        {
            string response = await _http.GetStringAsync(modelsUrl, ct).ConfigureAwait(false);
            Directory.CreateDirectory(_cacheDir);
            await File.WriteAllTextAsync(cachePath, response, ct).ConfigureAwait(false);
            return ParseModelsResponse(response, config);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch models for {Provider}, trying stale cache", config.Id);
            return Result.Failure<IReadOnlyList<ModelInfo>>(ex.Message);
        }
    }

    /// <summary>
    ///     Read the cached model list. <paramref name="freshOnly" /> enforces
    ///     the <paramref name="maxAge" /> window (a missing or aged-out cache is
    ///     a MISS, reported distinctly from a corrupt cache).
    /// </summary>
    private async Task<Result<IReadOnlyList<ModelInfo>>> ReadCacheAsync(
        string path, ProviderConfig config, CancellationToken ct, bool freshOnly, TimeSpan maxAge)
    {
        if (freshOnly)
        {
            if (!File.Exists(path))
                return Result.Failure<IReadOnlyList<ModelInfo>>("no cached model catalog");

            if (DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(path) >= maxAge)
                return Result.Failure<IReadOnlyList<ModelInfo>>("cached model catalog is stale");
        }

        try
        {
            string json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            return ParseModelsResponse(json, config);
        }
        catch (Exception ex)
        {
            return Result.Failure<IReadOnlyList<ModelInfo>>($"Cache read failed: {ex.Message}");
        }
    }

    private Result<IReadOnlyList<ModelInfo>> ParseModelsResponse(string json, ProviderConfig config)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var modelsArray = root;
            if (!string.IsNullOrEmpty(config.ModelsPath))
            {
                foreach (string part in config.ModelsPath.Split('.'))
                {
                    if (!modelsArray.TryGetProperty(part, out var next))
                        return Result.Failure<IReadOnlyList<ModelInfo>>($"Path '{config.ModelsPath}' not found in response.");
                    modelsArray = next;
                }
            }
            else if (root.TryGetProperty("data", out var data))
            {
                modelsArray = data;
            }
            else if (root.TryGetProperty("models", out var models))
            {
                modelsArray = models;
            }

            if (modelsArray.ValueKind != JsonValueKind.Array)
                return Result.Failure<IReadOnlyList<ModelInfo>>("Models response is not an array.");

            var mapping = config.ModelMapping ?? new ModelMapping();
            var result = new List<ModelInfo>();

            foreach (var item in modelsArray.EnumerateArray())
            {
                try
                {
                    var model = ParseModel(item, config, mapping);
                    if (model is not null)
                        result.Add(model);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse model from response");
                }
            }

            return Result.Success<IReadOnlyList<ModelInfo>>(result);
        }
        catch (Exception ex)
        {
            return Result.Failure<IReadOnlyList<ModelInfo>>($"Failed to parse models response: {ex.Message}");
        }
    }

    private static ModelInfo? ParseModel(JsonElement item, ProviderConfig config, ModelMapping mapping)
    {
        string? id = GetStringField(item, mapping.Id ?? "id");
        if (string.IsNullOrEmpty(id)) return null;

        string displayName = GetStringField(item, mapping.DisplayName ?? "name") ?? id;
        int contextWindow = GetIntField(item, mapping.ContextWindow ?? "context_length") ?? 4096;
        int maxOutput = GetIntField(item, mapping.MaxOutputTokens ?? "max_output_tokens") ?? 4096;

        return new ModelInfo(
            id,
            config.Id,
            displayName,
            contextWindow,
            maxOutput,
            false,
            false,
            true,
            Pricing.Unknown,
            "openai");
    }

    private static string? GetStringField(JsonElement item, string path)
    {
        var current = item;
        foreach (string part in path.Split('.'))
        {
            if (!current.TryGetProperty(part, out var next)) return null;
            current = next;
        }
        return current.ValueKind == JsonValueKind.String ? current.GetString() : current.GetRawText();
    }

    private static int? GetIntField(JsonElement item, string path)
    {
        var current = item;
        foreach (string part in path.Split('.'))
        {
            if (!current.TryGetProperty(part, out var next)) return null;
            current = next;
        }
        return current.ValueKind switch
        {
            JsonValueKind.Number => (int)current.GetInt64(),
            JsonValueKind.String when int.TryParse(current.GetString(), out int i) => i,
            _ => null
        };
    }
}
