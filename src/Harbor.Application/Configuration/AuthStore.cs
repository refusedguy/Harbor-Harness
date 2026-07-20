using System.Collections;
using Microsoft.Extensions.Logging;

namespace Harbor.Core.Configuration;

/// <summary>
///     Auth store — manages API keys per provider.
///     Stored in ~/.harbor/config.json (in ApiKeys dictionary).
///     Future: use OS keychain.
/// </summary>
/// <remarks>
///     Resolution order: config file → preset env var (e.g. <c>KILO_API_KEY</c>) →
///     conventional env var (<c>&lt;PROVIDER&gt;_API_KEY</c>).
/// </remarks>
public sealed class AuthStore
{
    private readonly IConfigStore _configStore;
    private readonly ILogger<AuthStore>? _logger;

    /// <summary>Construct an auth store backed by the supplied config store.</summary>
    /// <param name="configStore">The config store to read/write API keys.</param>
    /// <param name="logger">Optional logger.</param>
    public AuthStore(IConfigStore configStore, ILogger<AuthStore>? logger = null)
    {
        _configStore = configStore;
        _logger = logger;
    }

    /// <summary>
    ///     Look up the API key for the named provider. Resolution order: config file,
    ///     preset env var (e.g. <c>KILO_API_KEY</c>), conventional env var
    ///     (<c>&lt;PROVIDER&gt;_API_KEY</c>).
    /// </summary>
    /// <param name="providerId">The provider id (e.g. <c>anthropic</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success with the key, or failure listing all the env var names that were tried.</returns>
    public async Task<Result<string>> GetApiKeyAsync(string providerId, CancellationToken ct = default)
    {
        // 1. Check config file (single key lookup, no full-config materialization).
        var keyResult = await _configStore.GetApiKeyAsync(providerId, ct).ConfigureAwait(false);
        if (keyResult.IsSuccess)
            return keyResult;

        // 2. Check preset env var name (e.g. KILO_API_KEY for kilocode)
        var preset = ProviderPresets.Find(providerId);
        if (preset?.EnvVarName is not null)
        {
            string? presetEnv = Environment.GetEnvironmentVariable(preset.EnvVarName);
            if (!string.IsNullOrEmpty(presetEnv))
            {
                _logger?.LogDebug("Using API key from preset env var {Name}", preset.EnvVarName);
                return Result.Success(presetEnv);
            }
        }

        // 3. Fall back to conventional env var: PROVIDERID_API_KEY
        string envName = providerId.ToUpperInvariant().Replace('-', '_') + "_API_KEY";
        string? envValue = Environment.GetEnvironmentVariable(envName);
        if (!string.IsNullOrEmpty(envValue))
        {
            _logger?.LogDebug("Using API key from env var {Name}", envName);
            return Result.Success(envValue);
        }

        // 4. Build helpful error mentioning all possible env var names
        var envVars = new List<string> { envName };
        if (preset?.EnvVarName is not null && preset.EnvVarName != envName)
            envVars.Add(preset.EnvVarName);

        return Result.Failure<string>(
            $"No API key for '{providerId}'. " +
            $"Run `harbor auth set {providerId} <key>` or set one of: " +
            envVars.Select(v => $"${v}").JoinToString(", ") + " env var.");
    }

    /// <summary>Persist the API key for a provider into the config file.</summary>
    /// <param name="providerId">The provider id.</param>
    /// <param name="apiKey">The API key to persist.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success, or failure with an error message.</returns>
    public async Task<Result> SetApiKeyAsync(string providerId, string apiKey, CancellationToken ct = default)
    {
        return await _configStore.UpdateAsync(c =>
        {
            c.ApiKeys[providerId] = apiKey;
            return c;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Remove the stored API key for a provider from the config file.</summary>
    /// <param name="providerId">The provider id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success, or failure with an error message.</returns>
    public async Task<Result> RemoveApiKeyAsync(string providerId, CancellationToken ct = default)
    {
        return await _configStore.UpdateAsync(c =>
        {
            c.ApiKeys.Remove(providerId);
            return c;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     List all known API keys with their availability status (configured in
    ///     config file OR available via env var). The boolean is <see langword="true" />
    ///     when the key is set.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A map of provider id → present?</returns>
    public async Task<Result<IReadOnlyDictionary<string, bool>>> ListApiKeysAsync(CancellationToken ct = default)
    {
        var configResult = await _configStore.LoadAsync(ct).ConfigureAwait(false);
        if (configResult.IsFailure) return Result.Failure<IReadOnlyDictionary<string, bool>>(configResult.Error);

        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in configResult.Value.ApiKeys)
        {
            result[kv.Key] = !string.IsNullOrEmpty(kv.Value);
        }
        // Also check env vars
        foreach (var env in Environment.GetEnvironmentVariables().Cast<DictionaryEntry>())
        {
            string? key = env.Key?.ToString();
            if (key is null) continue;
            if (key.EndsWith("_API_KEY", StringComparison.OrdinalIgnoreCase))
            {
                string pid = key[..^"_API_KEY".Length].ToLowerInvariant().Replace('_', '-');
                if (!result.ContainsKey(pid)) result[pid] = env.Value is string s && !string.IsNullOrEmpty(s);
            }
        }
        return Result.Success<IReadOnlyDictionary<string, bool>>(result);
    }
}
