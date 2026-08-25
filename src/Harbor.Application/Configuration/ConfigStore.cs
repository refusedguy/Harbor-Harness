using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Harbor.Abstractions.Results;

namespace Harbor.Application.Configuration;
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
            Result<HarborConfig> loaded = LoadCore();
            if (loaded.IsFailure) // §4.6-ok: единая точка лога провала загрузки (rop-final-mile B2).
                _logger?.LogError("Failed to load config: {Error}", loaded.Error);
            return Task.FromResult(loaded);
        }
    }

    // rop-final-mile B2 / §4.6: only File/Deserialize throw; everything
    // downstream is Result-native. Error texts preserved: "config.json is
    // empty", normalize-error passthrough, "config.json is corrupt: …" for
    // JsonException. Honest loss vs the old catch blocks: one consolidated
    // failure log instead of two exception-shaped ones.
    private Result<HarborConfig> LoadCore()
    {
        if (!File.Exists(_configPath))
        {
            _logger?.LogInformation("Config file not found, using defaults");
            return Result.Success(HarborConfig.Default);
        }

        // Result.Try + ResultErrors.Message collapse errors to strings (bible §4.5:
        // the single canon — no ResultGuard), so the JsonException vs generic
        // discrimination happens HERE at the boundary while the exception is
        // still typed; downstream the railway is string-typed.
        return Result.Try(() =>
            {
                try
                {
                    string json = File.ReadAllText(_configPath);
                    return JsonSerializer.Deserialize(json, ConfigJsonContext.Default.RawConfigDto)
                           ?? throw new InvalidDataException("config.json is empty");
                }
                catch (JsonException je)
                {
                    throw new InvalidDataException($"config.json is corrupt: {je.Message}");
                }
            }, ResultErrors.Message)
            .Bind(raw => ConfigNormalizer.Normalize(raw))
            .Bind(config => config.Validate());
    }

    /// <inheritdoc />
    public Task<Result> SaveAsync(HarborConfig config, CancellationToken ct = default)
    {
        lock (_lock)
        {
            // rop-final-mile B3 / §4.6: one guard around the sync IO core.
            // Canonical selector (§4.5): OCE propagates instead of masking
            // cancellation as a save failure — same contract as LoadAsync.
            return Task.FromResult(Result.Try(() => SaveCore(config), ResultErrors.Message)
                .TapError(error => _logger?.LogError("Failed to save config: {Error}", error)));
        }
    }

    private void SaveCore(HarborConfig config)
    {
        string? dir = Path.GetDirectoryName(_configPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        // Serialize the canonical flat shape (not the composed sections)
        // so a reload reads the same fields back via RawConfigDto.
        string json = JsonSerializer.Serialize(config.ToRaw()!, ConfigJsonContext.Default.RawConfigDto);
        File.WriteAllText(_configPath, json);
        _logger?.LogDebug("Config saved to {Path}", _configPath);
    }

    /// <inheritdoc />
    public async Task<Result> UpdateAsync(Func<HarborConfig, HarborConfig> updater, CancellationToken ct = default)
    {
        var loadResult = await LoadAsync(ct).ConfigureAwait(false);
        if (loadResult.IsFailure) return loadResult; // §4.6-ok: одиночный passthrough.


        var updated = updater(loadResult.Value);
        return await SaveAsync(updated, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<string>> GetApiKeyAsync(string providerId, CancellationToken ct = default)
    {
        var loadResult = await LoadAsync(ct).ConfigureAwait(false);
        if (loadResult.IsFailure) return Result.Failure<string>(loadResult.Error); // §4.6-ok: одиночный passthrough (смена типа ошибки).

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
