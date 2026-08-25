using CSharpFunctionalExtensions;
using Harbor.Application.Configuration;
#if HARBOR_WITH_ALL_PROVIDERS
using Harbor.Providers.Anthropic;
using Harbor.Providers.OpenAI;
using Harbor.Providers.OpenAiCompatible;

namespace Harbor.Hosting;

/// <summary>Adapter that resolves API key via AuthStore.</summary>
/// <remarks>
///     Excluded when <c>HARBOR_WITH_ALL_PROVIDERS</c> is undefined — the
///     interface it implements (IAuthResolver, the single auth abstraction
///     since ROP-A ПР.6) lives in Harbor.Providers.OpenAiCompatible,
///     which is removed from the project reference graph when
///     HarborWithAllProviders=false. Ollama (always included) has no auth
///     resolver because OllamaLlmClient doesn't take an auth resolver — it
///     talks to a local daemon that doesn't require an API key.
/// </remarks>
internal sealed class ConfigAuthResolver : IAuthResolver
{
    private readonly AuthStore _authStore;
    private readonly string _providerId;

    public ConfigAuthResolver(AuthStore authStore, string providerId)
    {
        _authStore = authStore;
        _providerId = providerId;
    }

    public Task<Result<string>> ResolveApiKeyAsync(string providerId, CancellationToken ct = default)
        => _authStore.GetApiKeyAsync(string.IsNullOrEmpty(providerId) ? _providerId : providerId, ct);
}
#endif
