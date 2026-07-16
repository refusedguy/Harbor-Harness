using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Microsoft.Extensions.Logging;

namespace Harbor.Providers.OpenAiCompatible;

/// <summary>
/// Provider configuration loaded from JSON.
/// </summary>
public sealed class ProviderConfig
{
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
    public int Retries { get; set; } = 3;

    public ProviderId GetProviderId() => ProviderId.Create(Id);

    public static Result<ProviderConfig> LoadFromFile(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize<ProviderConfig>(json, JsonOptions) ?? throw new InvalidOperationException("Deserialization returned null");
            if (string.IsNullOrEmpty(config.Id))
                return Result.Failure<ProviderConfig>($"Provider config '{path}' is missing 'id'.");
            if (string.IsNullOrEmpty(config.BaseUrl))
                return Result.Failure<ProviderConfig>($"Provider config '{path}' is missing 'baseUrl'.");
            return Result.Success(config);
        }
        catch (Exception ex)
        {
            return Result.Failure<ProviderConfig>($"Failed to load provider config '{path}': {ex.Message}");
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
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
/// Auth resolver — fetches API key from env, file, or OS keychain.
/// </summary>
public interface IAuthResolver
{
    Task<Result<string>> ResolveApiKeyAsync(string providerId, CancellationToken ct = default);
}

/// <summary>
/// Default auth resolver — env var → config file.
/// </summary>
public sealed class EnvVarAuthResolver : IAuthResolver
{
    private readonly Dictionary<string, string> _overrides;
    private readonly ILogger<EnvVarAuthResolver> _logger;

    public EnvVarAuthResolver(Dictionary<string, string>? overrides = null, ILogger<EnvVarAuthResolver>? logger = null)
    {
        _overrides = overrides ?? new();
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<EnvVarAuthResolver>.Instance;
    }

    public Task<Result<string>> ResolveApiKeyAsync(string providerId, CancellationToken ct = default)
    {
        // 1. Override (from CLI flag)
        if (_overrides.TryGetValue(providerId, out var key) && !string.IsNullOrEmpty(key))
            return Task.FromResult(Result.Success(key));

        // 2. Env var (conventional name)
        var envName = providerId.ToUpperInvariant().Replace("-", "_") + "_API_KEY";
        var envValue = Environment.GetEnvironmentVariable(envName);
        if (!string.IsNullOrEmpty(envValue))
            return Task.FromResult(Result.Success(envValue));

        return Task.FromResult(Result.Failure<string>($"API key not found. Set ${envName} or pass --{providerId}-api-key."));
    }
}

/// <summary>
/// Model catalog — fetches and caches models list.
/// </summary>
public interface IModelCatalog
{
    Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(ProviderConfig config, CancellationToken ct = default);
}

public sealed class DynamicModelCatalog : IModelCatalog
{
    private readonly HttpClient _http;
    private readonly string _cacheDir;
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

        var cachePath = Path.Combine(_cacheDir, $"{config.Id}.json");
        var cacheAge = GetCacheAge(cachePath);

        if (cacheAge < TimeSpan.FromHours(config.ModelsRefreshHours))
        {
            var cached = await TryReadCacheAsync(cachePath, config, ct).ConfigureAwait(false);
            if (cached.IsSuccess)
                return cached;
        }

        try
        {
            var response = await _http.GetStringAsync(config.ModelsUrl, ct).ConfigureAwait(false);
            Directory.CreateDirectory(_cacheDir);
            await File.WriteAllTextAsync(cachePath, response, ct).ConfigureAwait(false);
            return ParseModelsResponse(response, config);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch models for {Provider}, trying stale cache", config.Id);
            var stale = await TryReadCacheAsync(cachePath, config, ct).ConfigureAwait(false);
            return stale.IsSuccess ? stale : Result.Failure<IReadOnlyList<ModelInfo>>(ex.Message);
        }
    }

    private static TimeSpan GetCacheAge(string path)
    {
        if (!File.Exists(path)) return TimeSpan.MaxValue;
        return DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(path);
    }

    private async Task<Result<IReadOnlyList<ModelInfo>>> TryReadCacheAsync(string path, ProviderConfig config, CancellationToken ct)
    {
        if (!File.Exists(path))
            return Result.Failure<IReadOnlyList<ModelInfo>>("No cache.");

        try
        {
            var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
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
                foreach (var part in config.ModelsPath.Split('.'))
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
        var id = GetStringField(item, mapping.Id ?? "id");
        if (string.IsNullOrEmpty(id)) return null;

        var displayName = GetStringField(item, mapping.DisplayName ?? "name") ?? id;
        var contextWindow = GetIntField(item, mapping.ContextWindow ?? "context_length") ?? 4096;
        var maxOutput = GetIntField(item, mapping.MaxOutputTokens ?? "max_output_tokens") ?? 4096;

        return new ModelInfo(
            Id: id,
            ProviderId: config.Id,
            DisplayName: displayName,
            ContextWindow: contextWindow,
            MaxOutputTokens: maxOutput,
            SupportsReasoning: false,
            SupportsVision: false,
            SupportsToolUse: true,
            Pricing: Pricing.Unknown,
            PromptTemplate: "openai");
    }

    private static string? GetStringField(JsonElement item, string path)
    {
        var current = item;
        foreach (var part in path.Split('.'))
        {
            if (!current.TryGetProperty(part, out var next)) return null;
            current = next;
        }
        return current.ValueKind == JsonValueKind.String ? current.GetString() : current.GetRawText();
    }

    private static int? GetIntField(JsonElement item, string path)
    {
        var current = item;
        foreach (var part in path.Split('.'))
        {
            if (!current.TryGetProperty(part, out var next)) return null;
            current = next;
        }
        return current.ValueKind switch
        {
            JsonValueKind.Number => (int)current.GetInt64(),
            JsonValueKind.String when int.TryParse(current.GetString(), out var i) => i,
            _ => null,
        };
    }
}
