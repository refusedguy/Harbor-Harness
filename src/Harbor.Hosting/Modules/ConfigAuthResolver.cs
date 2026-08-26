using CSharpFunctionalExtensions;
using Harbor.Application.Configuration;
using Harbor.Providers.OpenAiCompatible;

namespace Harbor.Hosting;

/// <summary>Adapter that resolves API key via AuthStore.</summary>
/// <remarks>
///     Compiles in every flavor: <c>IAuthResolver</c> lives in
///     Harbor.Providers.OpenAiCompatible, which is a baseline (unconditional)
///     ProjectReference of Harbor.Hosting. JSON-provider discovery
///     (RegisterJsonProviders) needs it for every discovered provider,
///     including minimal builds; native Anthropic/OpenAI factories stay
///     behind HARBOR_WITH_ALL_PROVIDERS. Ollama has no auth resolver because
///     OllamaLlmClient talks to a local daemon that doesn't require a key.
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
