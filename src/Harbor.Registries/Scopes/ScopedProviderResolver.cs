namespace Harbor.Registries.Scopes;

/// <summary>
///     The scope-aware provider resolver: it knows which scope an agent runs in
///     and which LLM client that scope sees.
/// </summary>
/// <remarks>
///     <para>
///         Issue #1736. The resolver holds TWO declared tables and nothing else:
///         the agent → scope table (which realm a run belongs to) and the
///         (scope, provider id) → client table (which implementation a realm
///         sees). A lookup consults the second table first and falls back to
///         the process-wide <see cref="IProviderRegistry" />, so a resolver
///         with empty tables behaves exactly like the global registry does.
///     </para>
///     <para>
///         The tables are declared, not discovered: a host, a plugin or a test
///         says which agent runs in which scope and which client a scope sees.
///         A test that wants its own client therefore binds one and hands the
///         resolver to the agent loop — it never edits the product's wiring.
///     </para>
///     <para>
///         Thread-safety: the agent loop reads the resolver once per run, and a
///         host may bind a scope while another run is in flight, so both
///         tables are guarded.
///     </para>
/// </remarks>
public sealed class ScopedProviderResolver : IScopedProviderResolver
{
    private readonly IProviderRegistry _global;
    private readonly ScopedServiceRegistry<ProviderId, ILlmClient> _scoped;
    private readonly object _scopeGate = new();
    private readonly Dictionary<AgentName, ServiceScope> _agentScopes = new();

    /// <summary>
    ///     Build a resolver over the process-wide provider registry.
    /// </summary>
    /// <param name="global">
    ///     The registry holding the process-wide clients. It is the fallback for
    ///     every lookup no scope answers.
    /// </param>
    /// <param name="scoped">
    ///     The declared (scope, provider id) → client table. A null value means
    ///     an empty table, which is the product's default.
    /// </param>
    public ScopedProviderResolver(
        IProviderRegistry global,
        ScopedServiceRegistry<ProviderId, ILlmClient>? scoped = null)
    {
        _global = global ?? throw new ArgumentNullException(nameof(global));
        _scoped = scoped ?? new ScopedServiceRegistry<ProviderId, ILlmClient>();
    }

    /// <inheritdoc />
    public ServiceScope ScopeFor(AgentName agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        lock (_scopeGate)
        {
            if (_agentScopes.TryGetValue(agent, out var scope))
            {
                return scope;
            }
        }

        return ServiceScope.Default;
    }

    /// <inheritdoc />
    public Result<ILlmClient> GetClient(ServiceScope scope, ProviderId providerId)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(providerId);

        Result<ILlmClient> scoped = _scoped.Resolve(scope, providerId);
        if (scoped.IsSuccess)
        {
            return scoped;
        }

        // Nothing declared for this scope: the process-wide client answers, and
        // the failure it returns is the one the caller has always seen.
        return _global.GetClient(providerId);
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(
        ServiceScope scope,
        ProviderId providerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(providerId);

        Result<ILlmClient> scoped = _scoped.Resolve(scope, providerId);
        if (scoped.IsFailure)
        {
            // Nothing declared for this scope: the process-wide cached catalog answers.
            return await _global.GetModelsCachedAsync(providerId, cancellationToken).ConfigureAwait(false);
        }

        // A scope that declares its own client is served that client's own
        // model list: the catalog and the client must come from the same
        // place, or a run can ask for a model its client does not serve.
        return await scoped.Value.GetModelsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Declare the client a scope sees for a provider id. A scope that
    ///     declares nothing inherits the process-wide client.
    /// </summary>
    /// <param name="scope">The scope the client belongs to.</param>
    /// <param name="providerId">The provider id the client answers for.</param>
    /// <param name="client">The client the scope sees.</param>
    public void BindProvider(ServiceScope scope, ProviderId providerId, ILlmClient client) =>
        _scoped.Bind(scope, providerId, client);

    /// <summary>
    ///     Declare the scope an agent runs in. An agent no scope is declared for
    ///     runs in <see cref="ServiceScope.Default" />.
    /// </summary>
    /// <param name="agent">The agent the scope belongs to.</param>
    /// <param name="scope">The scope the agent runs in.</param>
    public void BindScope(AgentName agent, ServiceScope scope)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(scope);

        lock (_scopeGate)
        {
            _agentScopes[agent] = scope;
        }
    }
}
