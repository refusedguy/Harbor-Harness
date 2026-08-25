// CommonConfigAuthResolver.cs — IAuthResolver backed by the shared CommonConfig.
//
// The CLI side uses AuthStore (Harbor.Core) which reads HarborConfig from
// ~/.harbor/config.json. The Avalonia app uses CommonConfig (Harbor.Desktop.
// Abstractions) which reads the SAME file but via a different type. Both
// share the `apiKeys` JSON field, so a key saved by the wizard (CommonConfig)
// is visible to the CLI (HarborConfig) and vice versa.
//
// This resolver reads from ICommonConfigStore so the Avalonia app doesn't
// need a second JsonConfigStore just for auth lookup. Resolution order:
//   1. CommonConfig.ApiKeys[providerId]     (persisted by the wizard / Settings)
//   2. ProviderPresets env var              (e.g. KILO_API_KEY for kilocode)
//   3. Conventional env var                 ({PROVIDERID}_API_KEY)

using CSharpFunctionalExtensions;
using Harbor.Core.Configuration;
using Harbor.Desktop.Abstractions.Configuration;
using Harbor.Abstractions.Providers;
using Harbor.Providers.OpenAiCompatible;
using Microsoft.Extensions.Logging;
namespace Harbor.App.Avalonia.Services;
/// <summary>
///     <see cref="IAuthResolver" /> backed by the shared
///     <see cref="CommonConfig.ApiKeys" /> dictionary. Used by the Avalonia
///     desktop app so providers registered via <c>providers/*.json</c> can
///     resolve API keys the user entered in the onboarding wizard or Settings
///     without pulling in the CLI's <c>AuthStore</c> (which reads a different
///     config type).
/// </summary>
/// <remarks>
///     <para>
///         <b>Resolution order:</b>
///     </para>
///     <list type="number">
///         <item><c>CommonConfig.ApiKeys[providerId]</c> — persisted by the wizard / Settings.</item>
///         <item><c>ProviderPresets.Find(providerId).EnvVarName</c> — e.g. <c>KILO_API_KEY</c> for kilocode.</item>
///         <item><c>{PROVIDERID}_API_KEY</c> — conventional fallback.</item>
///     </list>
///     <para>
///         The config is reloaded on every call so keys the user saves via
///         the wizard are immediately visible to in-flight requests (the
///         <see cref="CommonConfig" /> DI singleton is a startup snapshot and
///         does not see post-startup writes).
///     </para>
/// </remarks>
public sealed class CommonConfigAuthResolver : IAuthResolver
{
    private readonly ICommonConfigStore _configStore;
    private readonly ILogger<CommonConfigAuthResolver> _logger;

    /// <summary>
    ///     Construct a <see cref="CommonConfigAuthResolver" />.
    /// </summary>
    /// <param name="configStore">The shared config store (reads ~/.harbor/config.json).</param>
    /// <param name="logger">Logger for diagnostics.</param>
    public CommonConfigAuthResolver(
        ICommonConfigStore configStore,
        ILogger<CommonConfigAuthResolver> logger)
    {
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<Result<string>> ResolveApiKeyAsync(string providerId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return Result.Failure<string>("Provider id is required to resolve an API key.");
        }

        // 1. CommonConfig.ApiKeys (persisted by wizard / Settings).
        var loadResult = await _configStore.LoadAsync(ct).ConfigureAwait(false);
        if (loadResult.IsSuccess)
        {
            if (loadResult.Value.ApiKeys.TryGetValue(providerId, out string? key) && !string.IsNullOrEmpty(key))
            {
                _logger.LogDebug("Resolved API key for {Provider} from CommonConfig.ApiKeys", providerId);
                return Result.Success(key);
            }
        }
        else
        {
            _logger.LogWarning("Failed to load CommonConfig for auth lookup: {Error}", loadResult.Error);
        }

        // 2. Preset env var (e.g. KILO_API_KEY for kilocode — NOT KILOCODE_API_KEY).
        var preset = ProviderPresets.Find(providerId);
        if (preset?.EnvVarName is not null)
        {
            string? presetEnv = Environment.GetEnvironmentVariable(preset.EnvVarName);
            if (!string.IsNullOrEmpty(presetEnv))
            {
                _logger.LogDebug("Resolved API key for {Provider} from preset env var {Name}", providerId, preset.EnvVarName);
                return Result.Success(presetEnv);
            }
        }

        // 3. Conventional fallback: {PROVIDERID}_API_KEY.
        string conventional = providerId.ToUpperInvariant().Replace('-', '_') + "_API_KEY";
        string? envValue = Environment.GetEnvironmentVariable(conventional);
        if (!string.IsNullOrEmpty(envValue))
        {
            _logger.LogDebug("Resolved API key for {Provider} from conventional env var {Name}", providerId, conventional);
            return Result.Success(envValue);
        }

        // Build a helpful error listing every env var name that was tried.
        var tried = new List<string> { conventional };
        if (preset?.EnvVarName is not null && preset.EnvVarName != conventional)
        {
            tried.Add(preset.EnvVarName);
        }
        string envVars = string.Join(", ", tried.Select(v => "$" + v));
        return Result.Failure<string>(
            $"No API key for '{providerId}'. Run the onboarding wizard, use Settings, " +
            $"or set one of: {envVars}.");
    }
}
