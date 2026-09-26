using Harbor.Abstractions.Models.Identifiers;

namespace Harbor.Abstractions.Providers;

/// <summary>
///     Shared resolve preamble for the <c>TryCreate → GetClient</c> chain
///     (ROP boundary batch #101). Every <see cref="IProviderRegistry" />
///     consumer resolves a client id string the same way so parse and lookup
///     failures surface as one <c>Result</c>, never a throw.
/// </summary>
public static class ProviderRegistryResolve
{
    /// <summary>
    ///     Parse <paramref name="providerId" /> and resolve the registered
    ///     client in a single Bind railway.
    /// </summary>
    /// <param name="registry">The provider registry to look up.</param>
    /// <param name="providerId">The raw provider id string (may be null/blank).</param>
    /// <returns>Success with the client, or failure with the parse/lookup reason.</returns>
    public static Result<ILlmClient> ResolveClient(this IProviderRegistry registry, string? providerId) =>
        ProviderId.TryCreate(providerId).Bind(registry.GetClient);
}
