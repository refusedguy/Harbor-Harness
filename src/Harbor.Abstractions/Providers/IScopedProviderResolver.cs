using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;

namespace Harbor.Abstractions.Providers;

/// <summary>
///     Resolves the LLM client an agent sees, honouring the isolation scope the
///     agent runs in.
/// </summary>
/// <remarks>
///     <para>
///         Issue #1736. <see cref="IProviderRegistry" /> is process-wide: one
///         provider id maps to one client for the whole process. A scope adds the
///         missing axis — a realm in which a provider id may map to a DIFFERENT
///         client, while every other realm keeps the process-wide one.
///     </para>
///     <para>
///         The resolution order is fixed and is the whole contract:
///         <list type="number">
///             <item>the binding declared for the exact scope and provider id;</item>
///             <item>the binding declared for <see cref="ServiceScope.Default" /> and the same provider id;</item>
///             <item>the process-wide client the global registry holds.</item>
///         </list>
///         An empty table of scoped bindings therefore changes nothing: every
///         lookup lands on step three, which is what the process did before
///         scopes existed.
///     </para>
///     <para>
///         Implementations MUST be thread-safe: the agent loop reads the
///         resolver once per run, and a host may bind a scope while another
///         run is already in flight.
///     </para>
/// </remarks>
public interface IScopedProviderResolver
{
    /// <summary>
    ///     The isolation scope an agent runs in. An agent no scope is declared
    ///     for runs in <see cref="ServiceScope.Default" />.
    /// </summary>
    /// <param name="agent">The agent whose scope is asked for.</param>
    /// <returns>The scope bound to the agent, or <see cref="ServiceScope.Default" />.</returns>
    public ServiceScope ScopeFor(AgentName agent);

    /// <summary>
    ///     Get the LLM client a scope sees for a provider id.
    /// </summary>
    /// <param name="scope">The scope the lookup happens in.</param>
    /// <param name="providerId">The provider id to look up.</param>
    /// <returns>
    ///     Success with the client the scope sees — the scoped binding when the
    ///     scope declares one, the process-wide client otherwise — or failure
    ///     when neither the scope nor the global registry holds the provider.
    /// </returns>
    public Result<ILlmClient> GetClient(ServiceScope scope, ProviderId providerId);

    /// <summary>
    ///     Get the model catalog a scope sees for a provider id.
    /// </summary>
    /// <remarks>
    ///     The catalog comes from the SAME place the client does: a scope that
    ///     declares its own client is served that client's own model list, and a
    ///     scope that inherits is served the process-wide cached catalog. A run
    ///     that read one provider's catalog and called another provider's client
    ///     would fail on a model that is perfectly available.
    /// </remarks>
    /// <param name="scope">The scope the lookup happens in.</param>
    /// <param name="providerId">The provider id to look up.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success with the model catalog, or failure when the provider is unreachable.</returns>
    public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(
        ServiceScope scope,
        ProviderId providerId,
        CancellationToken cancellationToken = default);
}
