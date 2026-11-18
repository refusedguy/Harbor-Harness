using Harbor.Abstractions.Dsl;

namespace Harbor.Ui.Framework.Projection;

/// <summary>
///     Ordered chain of panel rules: local rules first, global rules second,
///     the fallback last.
/// </summary>
/// <remarks>
///     The order is the contract. A rule registered as local can override a
///     global rule for the same state, and a global rule can override the
///     fallback. The fallback is fixed at construction and is never moved by
///     later registrations, so no state is ever left without a panel.
///     <para>
///         This is the panel-side equivalent of Avalonia's template lookup:
///         match against a list of descriptors instead of switching on the
///         runtime type of the state. Matching uses <see cref="IPanelDescriptor.Match" />
///         only; the registry never resolves types by name or loads code, so
///         the chain works under NativeAOT.
///     </para>
/// </remarks>
public sealed class PanelDescriptorRegistry
{
    private readonly List<IPanelDescriptor> _local = [];
    private readonly List<IPanelDescriptor> _global = [];
    private readonly IPanelDescriptor _fallback;

    /// <summary>Creates a chain with an optional fallback rule.</summary>
    /// <param name="fallback">
    ///     Rule tried last. When omitted, <see cref="FallbackPanelDescriptor"/>
    ///     renders a one-line marker for an unmapped state.
    /// </param>
    public PanelDescriptorRegistry(IPanelDescriptor? fallback = null)
    {
        _fallback = fallback ?? new FallbackPanelDescriptor();
    }

    /// <summary>Local rules, in lookup order, before the global rules.</summary>
    public IReadOnlyList<IPanelDescriptor> Local => _local;

    /// <summary>Global rules, in lookup order, after the local rules.</summary>
    public IReadOnlyList<IPanelDescriptor> Global => _global;

    /// <summary>The rule tried after every local and global rule.</summary>
    public IPanelDescriptor Fallback => _fallback;

    /// <summary>Adds a local rule. Local rules are tried before global ones.</summary>
    /// <param name="descriptor">Rule to add.</param>
    public void AddLocal(IPanelDescriptor descriptor)
    {
        if (descriptor is null)
            throw new ArgumentNullException(nameof(descriptor));
        _local.Add(descriptor);
    }

    /// <summary>Adds a global rule. Global rules are tried after local ones.</summary>
    /// <param name="descriptor">Rule to add.</param>
    public void AddGlobal(IPanelDescriptor descriptor)
    {
        if (descriptor is null)
            throw new ArgumentNullException(nameof(descriptor));
        _global.Add(descriptor);
    }

    /// <summary>Finds the first rule that matches <paramref name="state"/>.</summary>
    /// <param name="state">State to match.</param>
    /// <returns>
    ///     The matching local rule, else the matching global rule, else the
    ///     fallback. Returns <see langword="null"/> only if the fallback itself
    ///     declines the state.
    /// </returns>
    public IPanelDescriptor? Find(object state)
    {
        for (int i = 0; i < _local.Count; i++)
        {
            if (_local[i].Match(state))
                return _local[i];
        }

        for (int i = 0; i < _global.Count; i++)
        {
            if (_global[i].Match(state))
                return _global[i];
        }

        return _fallback.Match(state) ? _fallback : null;
    }
}

/// <summary>The last rule in a chain: it matches any non-null state.</summary>
/// <remarks>
///     Its content is a one-line marker that names the state type. A host sees
///     an unmapped state instead of a blank panel while the descriptor for it
///     is being written.
/// </remarks>
public sealed class FallbackPanelDescriptor : IPanelDescriptor
{
    /// <inheritdoc />
    public Type StateType => typeof(object);

    /// <inheritdoc />
    public bool Match(object state) => state is not null;

    /// <inheritdoc />
    public object? Build(object state) =>
        $"[no panel for {state.GetType().Name}]";
}

/// <summary>Projects a state into panel content through a descriptor chain.</summary>
/// <remarks>
///     The projector has no type switch. A new state type needs a new
///     descriptor registered as local or global, not a new arm here.
/// </remarks>
public sealed class PanelDescriptorProjector
{
    private readonly PanelDescriptorRegistry _registry;

    /// <summary>Creates a projector over <paramref name="registry"/>.</summary>
    /// <param name="registry">Rule chain to resolve against.</param>
    public PanelDescriptorProjector(PanelDescriptorRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <summary>Resolves and builds the panel for <paramref name="state"/>.</summary>
    /// <param name="state">State to project.</param>
    /// <returns>Content from the first matching descriptor, or the fallback.</returns>
    public object? Project(object state) => _registry.Find(state)?.Build(state);
}
