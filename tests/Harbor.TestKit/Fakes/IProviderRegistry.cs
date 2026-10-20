using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;

namespace Harbor.TestKit;

/// <summary>In-memory provider registry over the given client.</summary>
public sealed class FakeProviderRegistry(ILlmClient client) : IProviderRegistry
{
    public IReadOnlyList<ProviderId> GetRegisteredProviderIds() => [client.ProviderId];

    public Result<ILlmClient> GetClient(ProviderId providerId) => Result.Success(client);

    public Task<Result<IReadOnlyList<ModelInfo>>> GetAllModelsAsync(CancellationToken cancellationToken = default)
        => client.GetModelsAsync(cancellationToken);

    public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsCachedAsync(ProviderId providerId, CancellationToken cancellationToken = default)
        => client.GetModelsAsync(cancellationToken);

    public void Register(ProviderId providerId, Func<ILlmClient> factory)
    {
    }

    public Result Unregister(ProviderId providerId) => Result.Failure("FakeProviderRegistry does not support unregister.");
}
