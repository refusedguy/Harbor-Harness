using System.Collections;
using Microsoft.Extensions.Logging;
namespace Harbor.Application.Configuration;
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
    /// <remarks>
    ///     ROP-B П.20: the three sources form a <see cref="CSharpFunctionalExtensions.ResultExtensions.Compensate" />
    ///     chain — each fallback runs ONLY when the previous source missed, so the
    ///     priority reads top-to-bottom and the final helpful error is built once
    ///     at the boundary instead of being assembled with flags mid-flight.
    /// </remarks>
    /// <param name="providerId">The provider id (e.g. <c>anthropic</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success with the key, or failure listing all the env var names that were tried.</returns>
    public async Task<Result<string>> GetApiKeyAsync(string providerId, CancellationToken ct = default) =>
        await FromConfigStoreAsync(providerId, ct)
            .Compensate(_ => FromPresetEnv(providerId))
            .Compensate(_ => FromConventionalEnv(providerId))
            .MapError(_ => MissingKeyHelp(providerId))
            .ConfigureAwait(false);

    private async Task<Result<string>> FromConfigStoreAsync(string providerId, CancellationToken ct) =>
        await _configStore.GetApiKeyAsync(providerId, ct).ConfigureAwait(false);

    private Result<string> FromPresetEnv(string providerId)
    {
        string? name = ProviderPresets.Find(providerId)?.EnvVarName;
        if (name is null)
            return Result.Failure<string>($"no preset env var for '{providerId}'");

        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(value))
            return Result.Failure<string>($"{name} not set");

        if (_logger is not null) AuthStoreLog.ApiKeyFromPresetEnv(_logger, name);
        return Result.Success(value);
    }

    /// <summary>Build the conventional <c>&lt;PROVIDER&gt;_API_KEY</c> name for an id.</summary>
    private static string ConventionalEnvName(string providerId) =>
        providerId.ToUpperInvariant().Replace('-', '_') + "_API_KEY";

    private static bool KeyIsSet(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 };

    private Result<string> FromConventionalEnv(string providerId)
    {
        string name = ConventionalEnvName(providerId);
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(value))
            return Result.Failure<string>($"{name} not set");

        if (_logger is not null) AuthStoreLog.ApiKeyFromEnv(_logger, name);
        return Result.Success(value);
    }

    /// <summary>Build the aggregated help message naming every env var that would work.</summary>
    private string MissingKeyHelp(string providerId)
    {
        string conventional = ConventionalEnvName(providerId);
        var envVars = new List<string> { conventional };
        string? presetName = ProviderPresets.Find(providerId)?.EnvVarName;
        if (presetName is not null && presetName != conventional)
            envVars.Add(presetName);

        return $"No API key for '{providerId}'. " +
               $"Run `harbor auth set {providerId} <key>` or set one of: " +
               envVars.Select(static v => $"${v}").JoinToString(", ") + " env var.";
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
    /// <remarks>
    ///     <b>ROP-C Z1:</b> the load-failure passthrough ladder rides a
    ///     <c>Bind</c> chain — the env-var scan only runs on a successfully
    ///     loaded config, and the failure propagates without a hand-rolled
    ///     if-return.
    /// </remarks>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A map of provider id → present?</returns>
    public Task<Result<IReadOnlyDictionary<string, bool>>> ListApiKeysAsync(CancellationToken ct = default)
    {
        return _configStore.LoadAsync(ct).Bind(config =>
        {
            Dictionary<string, bool> result = config.ApiKeys.ToDictionary(
                static kv => kv.Key,
                static kv => !string.IsNullOrEmpty(kv.Value),
                StringComparer.OrdinalIgnoreCase);

            // Presets own their env var names. Derive the provider id from the preset
            // instead of reverse-parsing the variable name: KILO_API_KEY belongs to
            // "kilocode", not to a phantom provider called "kilo" (#2560).
            var presetOwnedEnvVars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ProviderPresets.Preset preset in ProviderPresets.All)
            {
                string conventional = ConventionalEnvName(preset.Id);
                presetOwnedEnvVars.Add(conventional);

                if (preset.EnvVarName is { } presetName)
                {
                    presetOwnedEnvVars.Add(presetName);
                    if (KeyIsSet(presetName)) result[preset.Id] = true;
                }

                if (KeyIsSet(conventional)) result[preset.Id] = true;
            }

            // Providers without a preset are still discovered from a conventional
            // env var, but names already claimed above are skipped so their ids
            // cannot be invented a second time from the raw variable name.
            foreach (var env in Environment.GetEnvironmentVariables().Cast<DictionaryEntry>())
            {
                string? key = env.Key?.ToString();
                if (key is null) continue;
                if (!key.EndsWith("_API_KEY", StringComparison.OrdinalIgnoreCase)) continue;
                if (presetOwnedEnvVars.Contains(key)) continue;

                string pid = key[..^"_API_KEY".Length].ToLowerInvariant().Replace('_', '-');
                if (!result.ContainsKey(pid)) result[pid] = env.Value is string s && !string.IsNullOrEmpty(s);
            }

            return Result.Success<IReadOnlyDictionary<string, bool>>(result);
        });
    }
}

/// <summary>
///     SG2: BCL <c>[LoggerMessage]</c> delegates for <see cref="AuthStore" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class AuthStoreLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Using API key from preset env var {Name}")]
    public static partial void ApiKeyFromPresetEnv(ILogger logger, string name);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "Using API key from env var {Name}")]
    public static partial void ApiKeyFromEnv(ILogger logger, string name);

}
