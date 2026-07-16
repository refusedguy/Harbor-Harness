using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;

namespace Harbor.Abstractions.Providers;

/// <summary>
/// Registry of LLM providers. Implements Registry pattern.
/// Provides lazy-loading and lookup by provider ID.
/// </summary>
/// <remarks>
/// <para>
/// The provider registry is the canonical lookup table for <see cref="ILlmClient"/>
/// instances. Builtin providers are registered at startup with a factory function
/// (lazy instantiation — the client is only created the first time it's needed).
/// </para>
/// <para>
/// Implementations MUST be thread-safe. The default <c>ProviderRegistry</c> in
/// <c>Harbor.Core</c> uses a <c>NonBlocking.ConcurrentDictionary</c> for lock-free scaling
/// and an optional <see cref="FrozenDictionary{TKey, TValue}"/> snapshot for O(1) reads
/// after <see cref="ProviderRegistry.Freeze"/> is called.
/// </para>
/// </remarks>
public interface IProviderRegistry
{
    /// <summary>
    /// Get all registered provider IDs.
    /// </summary>
    /// <returns>A snapshot list of registered <see cref="ProviderId"/>s.</returns>
    IReadOnlyList<ProviderId> GetRegisteredProviderIds();

    /// <summary>
    /// Get or load an LLM client by provider ID.
    /// Lazy-loads the client on first access.
    /// </summary>
    /// <param name="providerId">The provider id to look up.</param>
    /// <returns>Success with the client, or failure if the provider is not registered or its factory throws.</returns>
    Result<ILlmClient> GetClient(ProviderId providerId);

    /// <summary>
    /// Get all available models from all providers (lazy-loaded).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success with the aggregated list of models, or failure if no provider could be reached.</returns>
    Task<Result<IReadOnlyList<ModelInfo>>> GetAllModelsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Register a provider with a factory function (lazy instantiation).
    /// </summary>
    /// <param name="providerId">The provider id to register.</param>
    /// <param name="factory">Factory invoked the first time the provider is needed.</param>
    void Register(ProviderId providerId, Func<ILlmClient> factory);

    /// <summary>
    /// Try to unregister a provider.
    /// </summary>
    /// <param name="providerId">The provider id to remove.</param>
    /// <returns>Success, or failure if the provider is not registered.</returns>
    Result Unregister(ProviderId providerId);
}

/// <summary>
/// Builder for provider registration (used by plugins).
/// </summary>
/// <remarks>
/// Plugins receive an <see cref="IProviderRegistryBuilder"/> during initialization and call
/// one of the <see cref="AddProvider"/> overloads for each provider they wish to contribute.
/// Registration failures surface as <see cref="InvalidOperationException"/>.
/// </remarks>
public interface IProviderRegistryBuilder
{
    /// <summary>
    /// Register a provider. The provider's <see cref="ILlmClient.ProviderId"/> is used as the
    /// registration key. The factory is invoked immediately to read the id.
    /// </summary>
    /// <param name="factory">Factory producing the client.</param>
    void AddProvider(Func<ILlmClient> factory);

    /// <summary>
    /// Register a provider with an explicit id (overrides the client's own id).
    /// </summary>
    /// <param name="providerId">The provider id to register under.</param>
    /// <param name="factory">Factory producing the client (invoked lazily).</param>
    void AddProvider(ProviderId providerId, Func<ILlmClient> factory);

    /// <summary>
    /// Register a provider by id string. Throws on invalid format.
    /// </summary>
    /// <param name="providerId">The provider id string.</param>
    /// <param name="factory">Factory producing the client (invoked lazily).</param>
    void AddProvider(string providerId, Func<ILlmClient> factory);
}
