namespace Harbor.Abstractions.Providers;

/// <summary>
///     Resolves the API key for a provider at request time (ROP-A ПР.6).
///     The single auth abstraction for ALL ILlmClient implementations —
///     implementations may read config stores, OS keychains, CLI overrides
///     or conventional environment variables.
/// </summary>
public interface IAuthResolver
{
    /// <summary>Resolve the API key for <paramref name="providerId" />.</summary>
    public Task<Result<string>> ResolveApiKeyAsync(string providerId, CancellationToken ct = default);
}
