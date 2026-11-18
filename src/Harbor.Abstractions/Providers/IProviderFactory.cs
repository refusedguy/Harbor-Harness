using Harbor.Abstractions.Models.Identifiers;
using Microsoft.Extensions.Logging;

namespace Harbor.Abstractions.Providers;

/// <summary>
///     Creates the LLM client for a provider id.
/// </summary>
/// <remarks>
///     A factory is registered per provider so the registry can bind a
///     <see cref="ProviderId" /> to a client without the caller knowing which
///     concrete client implements it.
/// </remarks>
public interface IProviderFactory
{
    /// <summary>The provider id this factory creates clients for.</summary>
    ProviderId ProviderId { get; }

    /// <summary>
    ///     Creates a client using the supplied logger factory.
    /// </summary>
    /// <param name="loggerFactory">The logger factory the client uses for its diagnostics.</param>
    /// <returns>A client bound to <see cref="ProviderId" />.</returns>
    ILlmClient CreateClient(ILoggerFactory loggerFactory);
}
