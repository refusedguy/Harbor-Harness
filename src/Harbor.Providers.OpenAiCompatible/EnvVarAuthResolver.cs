using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Providers.OpenAiCompatible;

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
