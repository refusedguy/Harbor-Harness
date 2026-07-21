using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
namespace Harbor.Core.Configuration;
/// <summary>
///     Configuration store — reads/writes ~/.harbor/config.json.
///     Implements Repository pattern for config.
/// </summary>
/// <remarks>
///     <para>
///         The configuration store is the single source of truth for user
///         preferences and API keys. The default <see cref="JsonConfigStore" />
///         reads and writes a JSON file at <see cref="JsonConfigStore.GetDefaultPath" />;
///         alternative implementations can back the store with a database, OS keychain, etc.
///     </para>
///     <para>
///         Implementations MUST be thread-safe. <see cref="JsonConfigStore" /> uses a
///         process-wide lock to serialize reads/writes.
///     </para>
/// </remarks>
public interface IConfigStore
{
    /// <summary>Load the current configuration.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The loaded <see cref="HarborConfig" />, or failure with an error message.</returns>
    public Task<Result<HarborConfig>> LoadAsync(CancellationToken ct = default);

    /// <summary>Save the supplied configuration atomically.</summary>
    /// <param name="config">The configuration to save.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success, or failure with an error message.</returns>
    public Task<Result> SaveAsync(HarborConfig config, CancellationToken ct = default);

    /// <summary>
    ///     Load → mutate → save in one atomic operation. The supplied
    ///     <paramref name="updater" /> function receives the current config and
    ///     returns the modified copy.
    /// </summary>
    /// <param name="updater">Pure function that maps the current config to the new one.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success, or failure with an error message.</returns>
    public Task<Result> UpdateAsync(Func<HarborConfig, HarborConfig> updater, CancellationToken ct = default);

    /// <summary>
    ///     Look up a single API key without loading the whole config into a
    ///     <see cref="HarborConfig" /> snapshot.
    /// </summary>
    /// <param name="providerId">The provider id (e.g. <c>anthropic</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success with the key, or failure if not present.</returns>
    public Task<Result<string>> GetApiKeyAsync(string providerId, CancellationToken ct = default);
}

/// <summary>
///     JSON-backed <see cref="IConfigStore" /> implementation. Reads/writes
///     ~/.harbor/config.json.
/// </summary>
public sealed class JsonConfigStore : IConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _configPath;
    private readonly object _lock = new();
    private readonly ILogger<JsonConfigStore>? _logger;

    /// <summary>Construct a JSON-backed config store.</summary>
    /// <param name="configPath">Optional path override; defaults to <see cref="GetDefaultPath" />.</param>
    /// <param name="logger">Optional logger.</param>
    public JsonConfigStore(string? configPath = null, ILogger<JsonConfigStore>? logger = null)
    {
        _configPath = configPath ?? GetDefaultPath();
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<Result<HarborConfig>> LoadAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_configPath))
                {
                    _logger?.LogInformation("Config file not found, using defaults");
                    return Task.FromResult(Result.Success(HarborConfig.Default));
                }

                string json = File.ReadAllText(_configPath);
                var raw = JsonSerializer.Deserialize<RawConfigDto>(json, JsonOptions);
                if (raw is null)
                    return Task.FromResult(Result.Failure<HarborConfig>("config.json is empty"));

                // Normalize legacy aliases (defaultProvider/defaultModel/…) once.
                var normalized = ConfigNormalizer.Normalize(raw);
                if (normalized.IsFailure)
                    return Task.FromResult(Result.Failure<HarborConfig>(normalized.Error));

                // Validate every section — surface errors instead of silently
                // discarding a corrupt config and pretending defaults were chosen.
                return Task.FromResult(normalized.Value.Validate());
            }
            catch (JsonException ex)
            {
                _logger?.LogError(ex, "Failed to parse config");
                return Task.FromResult(Result.Failure<HarborConfig>($"config.json is corrupt: {ex.Message}"));
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to load config");
                return Task.FromResult(Result.Failure<HarborConfig>(ex.Message));
            }
        }
    }

    /// <inheritdoc />
    public Task<Result> SaveAsync(HarborConfig config, CancellationToken ct = default)
    {
        lock (_lock)
        {
            try
            {
                string? dir = Path.GetDirectoryName(_configPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                // Serialize the canonical flat shape (not the composed sections)
                // so a reload reads the same fields back via RawConfigDto.
                string json = JsonSerializer.Serialize(config.ToRaw(), JsonOptions);
                File.WriteAllText(_configPath, json);
                _logger?.LogDebug("Config saved to {Path}", _configPath);
                return Task.FromResult(Result.Success());
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to save config");
                return Task.FromResult(Result.Failure(ex.Message));
            }
        }
    }

    /// <inheritdoc />
    public async Task<Result> UpdateAsync(Func<HarborConfig, HarborConfig> updater, CancellationToken ct = default)
    {
        var loadResult = await LoadAsync(ct).ConfigureAwait(false);
        if (loadResult.IsFailure) return loadResult;

        var updated = updater(loadResult.Value);
        return await SaveAsync(updated, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<string>> GetApiKeyAsync(string providerId, CancellationToken ct = default)
    {
        var loadResult = await LoadAsync(ct).ConfigureAwait(false);
        if (loadResult.IsFailure) return Result.Failure<string>(loadResult.Error);
        if (loadResult.Value.ApiKeys.TryGetValue(providerId, out string? key) && !string.IsNullOrEmpty(key))
            return Result.Success(key);
        return Result.Failure<string>($"No API key for '{providerId}' in config.json");
    }

    /// <summary>Returns the default config file path (<c>~/.harbor/config.json</c>).</summary>
    /// <returns>The absolute default path.</returns>
    public static string GetDefaultPath()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string harborDir = Path.Combine(home, ".harbor");
        return Path.Combine(harborDir, "config.json");
    }
}
