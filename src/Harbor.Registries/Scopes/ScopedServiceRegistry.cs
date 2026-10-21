namespace Harbor.Registries.Scopes;

/// <summary>
///     A registry of service implementations, one per scope and key, with the
///     default scope as the fallback every other scope inherits from.
/// </summary>
/// <typeparam name="TKey">
///     The key the service is looked up by inside a scope — a provider id for
///     the LLM client, a backend id for a session store.
/// </typeparam>
/// <typeparam name="TService">The service whose implementation is scoped.</typeparam>
/// <remarks>
///     <para>
///         Issue #1736. This is the mechanism behind "one implementation per
///         scope": a binding is DECLARED for a (scope, key) pair, and a lookup
///         in a scope finds the binding declared for that scope, or the one
///         declared for <see cref="ServiceScope.Default" />, or nothing.
///     </para>
///     <para>
///         The default scope is what keeps the feature additive. A registry
///         with no bindings at all resolves nothing, and the caller falls back
///         to the process-wide implementation — which is exactly what the
///         process did before scopes existed.
///     </para>
///     <para>
///         Thread-safety: the registry is written at composition time and read
///         once per run, so a single lock over the map is enough. It is NOT a
///         hot path: the agent loop resolves a client once per run, not once
///         per token.
///     </para>
/// </remarks>
public sealed class ScopedServiceRegistry<TKey, TService>
    where TKey : notnull
    where TService : class
{
    private readonly object _gate = new();
    private readonly Dictionary<(ServiceScope Scope, TKey Key), TService> _bindings = new();

    /// <summary>
    ///     Declare the implementation a scope sees for a key. A later binding for
    ///     the same pair replaces the earlier one.
    /// </summary>
    /// <param name="scope">The scope the implementation belongs to.</param>
    /// <param name="key">The key the service is looked up by inside the scope.</param>
    /// <param name="implementation">The implementation the scope sees.</param>
    public void Bind(ServiceScope scope, TKey key, TService implementation)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(implementation);

        lock (_gate)
        {
            _bindings[(scope, key)] = implementation;
        }
    }

    /// <summary>
    ///     Resolve the implementation a scope sees for a key: the binding declared
    ///     for that exact scope, else the one declared for
    ///     <see cref="ServiceScope.Default" />, else nothing.
    /// </summary>
    /// <param name="scope">The scope the lookup happens in.</param>
    /// <param name="key">The key the service is looked up by inside the scope.</param>
    /// <returns>Success with the implementation, or failure when the scope inherits nothing.</returns>
    public Result<TService> Resolve(ServiceScope scope, TKey key)
    {
        ArgumentNullException.ThrowIfNull(scope);

        lock (_gate)
        {
            if (_bindings.TryGetValue((scope, key), out var scoped))
            {
                return Result.Success(scoped);
            }

            if (scope != ServiceScope.Default
                && _bindings.TryGetValue((ServiceScope.Default, key), out var inherited))
            {
                return Result.Success(inherited);
            }
        }

        return Result.Failure<TService>(
            $"No implementation of '{key}' is bound in scope '{scope}'.");
    }

    /// <summary>
    ///     Whether a scope declares its own implementation for a key — as opposed
    ///     to inheriting the default scope's one.
    /// </summary>
    /// <param name="scope">The scope the lookup happens in.</param>
    /// <param name="key">The key the service is looked up by inside the scope.</param>
    /// <returns><see langword="true" /> when the scope declares its own implementation.</returns>
    public bool IsBound(ServiceScope scope, TKey key)
    {
        ArgumentNullException.ThrowIfNull(scope);

        lock (_gate)
        {
            return _bindings.ContainsKey((scope, key));
        }
    }
}
